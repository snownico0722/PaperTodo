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
        CheckOptics(); CheckCaptureLayout(); CheckExcludedSource();
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
            Until(() => surface.RefractionFrameCount >= 1, surface, "first genuine background frames");
            var editor = typeof(PaperWindow).GetProperty("_noteBox", Program.Private)!.GetValue(window);
            Program.Assert(editor != null && DesktopLensCapture.ReadAffinity(hwnd) == 0x11, "one editor, excluded foreground only while capturing");
            using (surface.FreezeRefractionForEvidence())
            {
                Wait(80);
                var bent = Render(window); Save(bent, "lens-refracted");
                using var bentDesktop = NativeSurfaceChecks.Capture(window, Output, "lens-desktop-refracted");
                surface.SetRefractionStrengthForEvidence(0); Wait(80);
                var flat = Render(window); Save(flat, "lens-flat-control");
                using var flatDesktop = NativeSurfaceChecks.Capture(window, Output, "lens-desktop-flat-control");
                surface.SetRefractionStrengthForEvidence(1); Wait(80);
                var a = Pixels(bent); var b = Pixels(flat); var changed = 0;
                // Test both empty shoulders: the inward reference profile can sample a
                // uniform grid cell on one side at this phase. Tint/light stay identical.
                for (var y = 180; y < 290; y++) for (var x = 4; x < bent.PixelWidth - 4; x++)
                {
                    if (x >= 28 && x < bent.PixelWidth - 28) continue;
                    var i = (y * bent.PixelWidth + x) * 4;
                    if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 30) changed++;
                }
                Console.WriteLine($"REFRACTION changed shoulder pixels={changed}; shader tier={RenderCapability.Tier >> 16}");
                Program.Assert(changed > 250, "actual captured grid bends, not just a changed highlight/tint");
                for (var y = 180; y < 290; y++)
                    Program.Assert(a.AsSpan((y * bent.PixelWidth + 32) * 4, (bent.PixelWidth - 64) * 4)
                        .SequenceEqual(b.AsSpan((y * flat.PixelWidth + 32) * 4, (flat.PixelWidth - 64) * 4)),
                        "refraction strength never warps or repaints the clear center");
                Program.Assert(DesktopLensCapture.ReadAffinity(hwnd) == 0, "evidence uses last genuine frame with screenshot visibility restored");
            }
            var count = surface.RefractionFrameCount;
            rear.Background = Brushes.DarkCyan;
            Until(() => surface.RefractionFrameCount > count, surface, "live background change");
            Wait(180);
            using var cyan = NativeSurfaceChecks.Capture(window, Output, "lens-live-cyan");
            count = surface.RefractionFrameCount; rear.Background = Brushes.Crimson;
            Until(() => surface.RefractionFrameCount > count, surface, "second live background change");
            Wait(180);
            using var red = NativeSurfaceChecks.Capture(window, Output, "lens-live-red");
            var c = cyan.GetPixel(210, 240); var r = red.GetPixel(210, 240);
            Program.Assert(r.R > c.R + 30 && c.G > r.G + 20,
                "unmodified center follows desktop composition, not a cached capture or wallpaper");
            Wait(800); count = surface.RefractionFrameCount;
            var uploaded = surface.RefractionUploadedPixels;
            Wait(500);
            Program.Assert(surface.RefractionFrameCount == count && surface.RefractionUploadedPixels == uploaded,
                "unchanged desktop produces no WPF uploads or presentation frames");
            var inputDelays = new List<double>();
            var pixelsBefore = surface.RefractionUploadedPixels; var framesBefore = surface.RefractionFrameCount;
            for (var i = 0; i < 48; i++)
            {
                var inputQueued = Stopwatch.GetTimestamp();
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                    new Action(() => inputDelays.Add(Stopwatch.GetElapsedTime(inputQueued).TotalMilliseconds)));
                rear.Background = Grid(i * 2); Wait(100);
                // Raster frames exercise the optics. Desktop composites are saved separately:
                // the center deliberately does not exist in a WPF capture bitmap any more.
                Save(Render(window), $"lens-live-{i:D3}");
            }
            Program.Assert(inputDelays.Count == 48, "input callbacks are not starved by background publication");
            inputDelays.Sort();
            Console.WriteLine($"LENS INPUT (CI diagnostic, not hardware FPS): p50={inputDelays[24]:F2}ms p95={inputDelays[45]:F2}ms; uploaded={surface.RefractionUploadedPixels-pixelsBefore} pixels over {surface.RefractionFrameCount-framesBefore} frames; nonblocking retries={surface.RefractionBusyFrames}");
            window.Left += 100; window.Top += 30; count = surface.RefractionFrameCount;
            Until(() => surface.RefractionFrameCount > count, surface, "movement resamples new physical background");
            Save(Render(window), "lens-moved");
            window.Width += 70; window.Height += 30; count = surface.RefractionFrameCount;
            Until(() => surface.RefractionFrameCount > count, surface, "resize replaces geometry without editor recreation");
            Program.Assert(ReferenceEquals(editor, typeof(PaperWindow).GetProperty("_noteBox", Program.Private)!.GetValue(window)) &&
                new WindowInteropHelper(window).Handle == hwnd, "live refraction preserves content/undo owner and HWND");
            window.Hide(); Wait(80);
            Program.Assert(!surface.HasRefractionWorker && DesktopLensCapture.ReadAffinity(hwnd) == 0, "hidden restores capture affinity and releases worker");
            count = surface.RefractionFrameCount; window.Show();
            Until(() => surface.RefractionFrameCount > count, surface, "show resumes");
            window.WindowState = WindowState.Minimized; Wait(80);
            Program.Assert(!surface.HasRefractionWorker && DesktopLensCapture.ReadAffinity(hwnd) == 0, "minimize releases worker and affinity");
            count = surface.RefractionFrameCount; window.WindowState = WindowState.Normal;
            Until(() => surface.RefractionFrameCount > count, surface, "restore resumes");
            window.SetCollapsedState(true, false, false); Wait(80);
            Program.Assert(!surface.HasRefractionWorker && DesktopLensCapture.ReadAffinity(hwnd) == 0, "capsule never acquires capture ownership");
            count = surface.RefractionFrameCount; window.SetCollapsedState(false, false, false);
            Until(() => surface.RefractionFrameCount > count, surface, "expand resumes");
            controller.State.LiquidGlassRefraction = false; window.RefreshSkin(); Wait(80);
            Program.Assert(!surface.HasRefractionWorker && DesktopLensCapture.ReadAffinity(hwnd) == 0, "independent switch restores screenshot/screen-sharing visibility");
            controller.State.LiquidGlassRefraction = true; count = surface.RefractionFrameCount; window.RefreshSkin();
            Until(() => surface.RefractionFrameCount > count, surface, "opt-in resumes");
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
            using (var capture = new DesktopLensCapture(hwnd, new DesktopLensCapture.Region(0, 0, 180, 140, 0), front.Dispatcher, ex => failure = ex))
            {
                for (var i = 0; i < 100 && frames == 0 && failure == null; i++)
                {
                    Wait(30);
                    using var frame = capture.TakeLatest();
                    if (frame == null) continue;
                    var tile = frame.Tiles[0]; var pixel = (70 * tile.Layout.PixelWidth + 80) * 4;
                    sample = Color.FromRgb(tile.Pixels[pixel + 2], tile.Pixels[pixel + 1], tile.Pixels[pixel]); frames++;
                }
                Program.Assert(failure == null && frames == 1 && sample.G > 230 && sample.R < 10 && sample.B < 10,
                    $"production capture sees genuine rear green, never foreground magenta or a black substitute: {sample}/{failure}");
                Wait(600); var published = capture.PublishedCount; var sampled = capture.CaptureCount;
                using (capture.TakeLatest()) { }
                Wait(500);
                Program.Assert(capture.PublishedCount == published && capture.TakeLatest() == null && capture.CaptureCount > sampled,
                    "stationary change detection runs without allocating or publishing duplicate frames");
                rear.Background = Brushes.Blue; Wait(400);
                using var latest = capture.TakeLatest();
                Program.Assert(latest != null && capture.TakeLatest() == null && latest.Tiles[0].Pixels[0] > 230,
                    "one-slot mailbox resumes on real background changes");
            }
            Program.Assert(DesktopLensCapture.ReadAffinity(hwnd) == 0, "capture lease restores prior affinity");
        }
        finally { front.Close(); rear.Close(); }
    }
    private static void CheckOptics()
    {
        var size = new Size(430, 350);
        for (var y = 1; y < 350; y += 13) for (var x = 1; x < 430; x += 13)
        {
            var (a, f) = LensDisplacement.Sample(new Point(x, y), size, 8);
            var (b, _) = LensDisplacement.Sample(new Point(430-x, y), size, 8);
            Program.Assert(double.IsFinite(a.X) && double.IsFinite(a.Y) && f is >= 0 and <= 1 &&
                Math.Abs(a.X) <= 18 && Math.Abs(a.Y) <= 18 && Math.Abs(a.X+b.X) < .001,
                "finite symmetric inward optical shoulder");
        }
        Program.Assert(LensDisplacement.Sample(new Point(2, 175), size, 8).Offset.X > 10,
            "reference inward edge profile displaces actual source pixels");
        foreach (var point in new[] { new Point(215,175), new Point(24,100), new Point(406,100) })
            Program.Assert(LensDisplacement.Sample(point, size, 8) == (new Vector(), 0d),
                "clear center has exactly zero displacement and zero sampled coverage");
    }
    private static void CheckCaptureLayout()
    {
        foreach (var dpi in new[] { 1.0, 1.25, 1.5, 2.0 })
        foreach (var size in new[] { new Size(430,350), new Size(1024,768), new Size(2400,1200), new Size(12,8) })
        {
            var width = (int)Math.Ceiling(size.Width*dpi); var height = (int)Math.Ceiling(size.Height*dpi);
            var region = new DesktopLensCapture.Region(1,1,width,height,(int)Math.Ceiling(24*dpi),(int)Math.Ceiling(19*dpi));
            var layout = LensCaptureLayout.Create(new Int32Rect(-700,50,width,height), region, new Int32Rect(-8192,-2160,16384,8640));
            Program.Assert(layout.Length is >= 1 and <= 4 && layout.Sum(t => (long)t.PixelWidth*t.PixelHeight) <= LensCaptureLayout.PixelBudget,
                "negative screen origins / DPI / large surfaces retain bounded physical capture budget");
            for (var i = 0; i < layout.Length; i++) for (var j = i+1; j < layout.Length; j++)
            {
                var a = layout[i].Target; var b = layout[j].Target;
                Program.Assert(Math.Min(a.X+a.Width,b.X+b.Width) <= Math.Max(a.X,b.X) ||
                    Math.Min(a.Y+a.Height,b.Y+b.Height) <= Math.Max(a.Y,b.Y), "optical strips never overlap or double-tint seams");
            }
            if (size.Width >= 1024)
            {
                var pixels = layout.Sum(t => (long)t.PixelWidth*t.PixelHeight);
                var old = (width+(long)Math.Ceiling(128*dpi))*(height+(long)Math.Ceiling(128*dpi));
                Program.Assert(pixels < old*.35, "large paper samples less than 35% of previous full padded capture");
                Console.WriteLine($"LENS BUDGET {size.Width}x{size.Height}@{dpi}: {pixels} vs old {old} pixels ({100d*pixels/old:F1}%)");
            }
        }
    }
    private static string? Output => Environment.GetEnvironmentVariable("PAPER_SKIN_CAPTURE");
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
