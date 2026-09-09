using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static class RefractionChecks
{
    internal static void Run(AppController controller)
    {
        CheckOptics(); CheckExcludedSource();
        var saved = (controller.State.PaperSkin, controller.State.Theme, controller.State.ColorScheme,
            controller.State.EnableAnimations, controller.State.LiquidGlassRefraction, controller.State.UseCapsuleMode);
        PaperWindow? window = null;
        var rear = new Window { Left = 20, Top = 20, Width = 680, Height = 510, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = Grid(0) };
        try
        {
            controller.State.PaperSkin = PaperSkins.LiquidGlass; controller.State.Theme = "light";
            controller.State.ColorScheme = ColorSchemes.Neutral; controller.State.EnableAnimations = false;
            controller.State.LiquidGlassRefraction = true; controller.State.UseCapsuleMode = true; Theme.Invalidate();
            rear.Show();
            var data = new PaperData { Type = PaperTypes.Note, Title = "真实背景折射", Content = "# 液态玻璃\n\n正文保持清晰，背景沿边缘弯曲。", X = 90, Y = 80,
                Width = 430, Height = 350, AlwaysOnTop = true };
            window = new PaperWindow(data, controller); window.Show(); window.Activate();
            var surface = Surface(window); var hwnd = new WindowInteropHelper(window).Handle;
            Until(() => surface.RefractionFrameCount >= 3, surface, "first genuine background frames");
            var editor = typeof(PaperWindow).GetProperty("_noteBox", Program.Private)!.GetValue(window);
            Program.Assert(editor != null && DesktopLensCapture.ReadAffinity(hwnd) == 0x11, "one editor, excluded foreground only while capturing");
            using (surface.FreezeRefractionForEvidence())
            {
                Wait(80);
                var bent = Render(window); Save(bent, "lens-refracted");
                var effect = (LiquidRefractionEffect)typeof(SkinBorder).GetField("_refractionEffect", Program.Private)!.GetValue(surface)!;
                var shift = effect.Shift; effect.Shift = new Point(); Wait(80);
                var flat = Render(window); Save(flat, "lens-flat-control");
                effect.Shift = shift; Wait(80);
                var a = Pixels(bent); var b = Pixels(flat); var changed = 0;
                // Empty text-free shoulder: only the background sampling offset differs.
                for (var y = 180; y < 290; y++) for (var x = 4; x < 28; x++)
                {
                    var i = (y * bent.PixelWidth + x) * 4;
                    if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 30) changed++;
                }
                Console.WriteLine($"REFRACTION changed shoulder pixels={changed}; shader tier={RenderCapability.Tier >> 16}");
                Program.Assert(changed > 250, "actual captured grid bends, not just a changed highlight/tint");
                Program.Assert(DesktopLensCapture.ReadAffinity(hwnd) == 0, "evidence uses last genuine frame with screenshot visibility restored");
            }
            var count = surface.RefractionFrameCount;
            rear.Background = Brushes.DarkCyan;
            Until(() => surface.RefractionFrameCount > count + 3, surface, "live background change");
            var cyan = Pixels(Render(window));
            count = surface.RefractionFrameCount; rear.Background = Brushes.Crimson;
            Until(() => surface.RefractionFrameCount > count + 3, surface, "second live background change");
            var red = Pixels(Render(window)); var center = (240 * (int)window.ActualWidth + 210) * 4;
            Program.Assert(red[center + 2] > cyan[center + 2] + 30 && cyan[center + 1] > red[center + 1] + 20,
                "stationary lens follows the real rear window, not wallpaper or a cached screenshot");
            for (var i = 0; i < 48; i++)
            {
                rear.Background = Grid(i * 2); Wait(100);
                Save(Render(window), $"lens-live-{i:D3}");
            }
            window.Left += 100; window.Top += 30; count = surface.RefractionFrameCount;
            Until(() => surface.RefractionFrameCount > count + 2, surface, "movement resamples new physical background");
            Save(Render(window), "lens-moved");
            window.Width += 70; window.Height += 30; count = surface.RefractionFrameCount;
            Until(() => surface.RefractionFrameCount > count + 2, surface, "resize replaces geometry without editor recreation");
            Program.Assert(ReferenceEquals(editor, typeof(PaperWindow).GetProperty("_noteBox", Program.Private)!.GetValue(window)) &&
                new WindowInteropHelper(window).Handle == hwnd, "live refraction preserves content/undo owner and HWND");
            window.Hide(); Wait(80);
            Program.Assert(!surface.HasRefractionWorker && DesktopLensCapture.ReadAffinity(hwnd) == 0, "hidden restores capture affinity and releases worker");
            count = surface.RefractionFrameCount; window.Show();
            Until(() => surface.RefractionFrameCount > count + 1, surface, "show resumes");
            window.WindowState = WindowState.Minimized; Wait(80);
            Program.Assert(!surface.HasRefractionWorker && DesktopLensCapture.ReadAffinity(hwnd) == 0, "minimize releases worker and affinity");
            count = surface.RefractionFrameCount; window.WindowState = WindowState.Normal;
            Until(() => surface.RefractionFrameCount > count + 1, surface, "restore resumes");
            window.SetCollapsedState(true, false, false); Wait(80);
            Program.Assert(!surface.HasRefractionWorker && DesktopLensCapture.ReadAffinity(hwnd) == 0, "capsule never acquires capture ownership");
            count = surface.RefractionFrameCount; window.SetCollapsedState(false, false, false);
            Until(() => surface.RefractionFrameCount > count + 1, surface, "expand resumes");
            controller.State.LiquidGlassRefraction = false; window.RefreshSkin(); Wait(80);
            Program.Assert(!surface.HasRefractionWorker && DesktopLensCapture.ReadAffinity(hwnd) == 0, "independent switch restores screenshot/screen-sharing visibility");
            controller.State.LiquidGlassRefraction = true; count = surface.RefractionFrameCount; window.RefreshSkin();
            Until(() => surface.RefractionFrameCount > count + 1, surface, "opt-in resumes");
            controller.State.PaperSkin = PaperSkins.Aero; Theme.Invalidate(); window.UpdateTheme(); Wait(80);
            Program.Assert(!surface.HasRefractionWorker && DesktopLensCapture.ReadAffinity(hwnd) == 0, "other skins retain ordinary screen capture semantics");
        }
        finally
        {
            window?.CloseForReal(); rear.Close();
            (controller.State.PaperSkin, controller.State.Theme, controller.State.ColorScheme,
                controller.State.EnableAnimations, controller.State.LiquidGlassRefraction, controller.State.UseCapsuleMode) = saved;
            Theme.Invalidate();
        }
    }
    private static void CheckExcludedSource()
    {
        var rear = new Window { Left = 20, Top = 20, Width = 340, Height = 250, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = Brushes.Lime };
        var front = new Window { Left = 60, Top = 60, Width = 180, Height = 140, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Background = Brushes.Magenta };
        try
        {
            rear.Show(); front.Show(); Wait(100); var hwnd = new WindowInteropHelper(front).Handle;
            var sample = Color.FromRgb(0, 0, 0); var frames = 0; Exception? failure = null;
            using (var capture = new DesktopLensCapture(hwnd, new DesktopLensCapture.Region(0, 0, 180, 140, 0), front.Dispatcher,
                frame => { var i = (70 * frame.Bounds.Width + 80) * 4; sample = Color.FromRgb(frame.Pixels[i + 2], frame.Pixels[i + 1], frame.Pixels[i]); frames++; }, ex => failure = ex))
            {
                for (var i = 0; i < 100 && frames < 2 && failure == null; i++) Wait(30);
                Program.Assert(failure == null && frames >= 2 && sample.G > 230 && sample.R < 10 && sample.B < 10,
                    $"production capture sees genuine rear green, never foreground magenta or a black substitute: {sample}/{failure}");
            }
            Program.Assert(DesktopLensCapture.ReadAffinity(hwnd) == 0, "capture lease restores prior affinity");
        }
        finally { front.Close(); rear.Close(); }
    }
    private static void CheckOptics()
    {
        foreach (var dpi in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var size = new Size(430, 350); var map = LensDisplacement.Create(size, new CornerRadius(8), new DpiScale(dpi, dpi));
            Program.Assert(map.IsFrozen && map.PixelWidth <= 768 && map.PixelHeight <= 768, "bounded DPI displacement texture");
            for (var y = 1; y < 350; y += 13) for (var x = 1; x < 430; x += 13)
            {
                var (a, f) = LensDisplacement.Sample(new Point(x, y), size, 8);
                var (b, _) = LensDisplacement.Sample(new Point(430 - x, y), size, 8);
                Program.Assert(double.IsFinite(a.X) && double.IsFinite(a.Y) && f is >= 0 and <= 1 &&
                    Math.Abs(a.X) <= 24 && Math.Abs(a.Y) <= 24 && Math.Abs(a.X + b.X) < .001, "finite symmetric Snell displacement");
            }
        }
        Program.Assert(LensDisplacement.Sample(new Point(2, 175), new Size(430, 350), 8).Offset.X > 3,
            "curved shoulder actually samples displaced scene pixels");
    }
    private static SkinBorder Surface(PaperWindow window) =>
        (SkinBorder)typeof(PaperWindow).GetField("_paperChrome", Program.Private)!.GetValue(window)!;
    private static Brush Grid(double phase)
    {
        var drawing = new DrawingGroup(); using (var dc = drawing.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(235, 245, 251)), null, new Rect(0, 0, 680, 510));
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(24, 97, 150)), 2);
            for (var x = -30 + phase % 24; x < 680; x += 24) dc.DrawLine(pen, new Point(x, 0), new Point(x, 510));
            for (var y = -30 + phase % 24; y < 510; y += 24) dc.DrawLine(pen, new Point(0, y), new Point(680, y));
        }
        drawing.Freeze(); var brush = new DrawingBrush(drawing) { Stretch = Stretch.Fill }; brush.Freeze(); return brush;
    }
    private static RenderTargetBitmap Render(FrameworkElement element)
    {
        element.UpdateLayout(); var result = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),
            (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32); result.Render(element); return result;
    }
    private static byte[] Pixels(BitmapSource image)
    { var bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes; }
    private static void Save(BitmapSource image, string name)
    {
        var output = Environment.GetEnvironmentVariable("PAPER_SKIN_CAPTURE"); if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
    }
    private static void Until(Func<bool> condition, SkinBorder surface, string reason)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < 6000 && surface.RefractionFailure == null) Wait(30);
        Program.Assert(condition(), $"{reason}: {surface.RefractionFailure ?? "timeout"}");
    }
    private static void Wait(int ms)
    {
        var f = new System.Windows.Threading.DispatcherFrame();
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); f.Continue = false; }; t.Start(); System.Windows.Threading.Dispatcher.PushFrame(f);
    }
}
