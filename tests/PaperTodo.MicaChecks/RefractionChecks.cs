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
        OpticalProfileChecks.Run(); CheckCaptureLayout(); CheckExcludedSource();
        var saved = (controller.State.PaperSkin, controller.State.Theme, controller.State.ColorScheme,
            controller.State.EnableAnimations, controller.State.LiquidGlassRefraction, controller.State.UseCapsuleMode);
        PaperWindow? window = null;
        var rear = new Window { Left = 20, Top = 20, Width = 680, Height = 510, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Background = Grid(0) };
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
                var foreground = Pixels(Render((FrameworkElement)editor!));
                var bent = Render(window); Save(bent, "lens-refracted");
                using var bentDesktop = NativeSurfaceChecks.Capture(window, Output, "lens-desktop-refracted");
                surface.SetRefractionStrengthForEvidence(0); Wait(80);
                var flat = Render(window); Save(flat, "lens-flat-control");
                using var flatDesktop = NativeSurfaceChecks.Capture(window, Output, "lens-desktop-flat-control");
                surface.SetRefractionStrengthForEvidence(1); Wait(80);
                var a = Pixels(bent); var b = Pixels(flat); var changed = 0;
                // Sample the actual resized shoulder, including its first painted pixels.
                // The old four-pixel inset discarded most of this gentler, narrower lens.
                // Tint/light and the captured frame stay identical in this comparison.
                var shoulder = (int)Math.Ceiling(GlassMetrics.For(new Size(bent.PixelWidth, bent.PixelHeight), false).Bezel) + 2;
                for (var y = 180; y < 290; y++) for (var x = 2; x < bent.PixelWidth - 2; x++)
                {
                    if (x >= shoulder && x < bent.PixelWidth - shoulder) continue;
                    var i = (y * bent.PixelWidth + x) * 4;
                    if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 30) changed++;
                }
                Console.WriteLine($"REFRACTION changed shoulder pixels={changed}; shader tier={RenderCapability.Tier >> 16}");
                Program.Assert(changed > 250, "actual captured grid bends, not just a changed highlight/tint");
                Program.Assert(foreground.SequenceEqual(Pixels(Render((FrameworkElement)editor!))),
                    "optics change the background only, never the actual foreground editor rendering");
                var finish = (DrawingVisual)typeof(SkinBorder).GetField("_opticalFinish", Program.Private)!.GetValue(surface)!;
                finish.Opacity = 0; Wait(80);
                var bare = Pixels(Render(window));
                finish.Opacity = 1; Wait(80);
                var coated = Pixels(Render(window));
                var coatChanges = 0;
                for (var y = 200; y < 290; y++) for (var x = 60; x < 350; x++)
                {
                    var i = (y * bent.PixelWidth + x) * 4;
                    if (Math.Abs(bare[i]-coated[i]) + Math.Abs(bare[i+1]-coated[i+1]) + Math.Abs(bare[i+2]-coated[i+2]) > 6) coatChanges++;
                }
                Program.Assert(coatChanges > 100, "the clear-coat actually composites above the sampled body, not underneath it");
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
                "the coherent glass body follows actual changing desktop content");
            Wait(800); count = surface.RefractionFrameCount;
            var uploaded = surface.RefractionUploadedPixels;
            Wait(500);
            Program.Assert(surface.RefractionFrameCount == count && surface.RefractionUploadedPixels == uploaded,
                "unchanged desktop produces no WPF uploads or presentation frames");
            Program.Assert(!surface.HasRefractionRenderSubscription, "an unchanged desktop releases the WPF composition subscription");
            var inputDelays = new List<double>();
            var pixelsBefore = surface.RefractionUploadedPixels; var framesBefore = surface.RefractionFrameCount;
            for (var i = 0; i < 48; i++)
            {
                var inputQueued = Stopwatch.GetTimestamp();
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                    new Action(() => inputDelays.Add(Stopwatch.GetElapsedTime(inputQueued).TotalMilliseconds)));
                rear.Background = Grid(i * 2); Wait(100);
                // The body and shoulder now share one captured source and one timestamp.
                Save(Render(window), $"lens-live-{i:D3}");
            }
            Program.Assert(inputDelays.Count == 48, "input callbacks are not starved by background publication");
            inputDelays.Sort();
            Console.WriteLine($"LENS INPUT (CI diagnostic, not hardware FPS): p50={inputDelays[24]:F2}ms p95={inputDelays[45]:F2}ms; uploaded={surface.RefractionUploadedPixels-pixelsBefore} pixels over {surface.RefractionFrameCount-framesBefore} frames; nonblocking retries={surface.RefractionBusyFrames}");
            var projectionStart = surface.RefractionProjectionCount;
            var captureStart = surface.RefractionFrameCount;
            for (var move = 0; move < 20; move++) { window.Left += 2; Wait(16); }
            Program.Assert(surface.RefractionProjectionCount - projectionStart > surface.RefractionFrameCount - captureStart,
                "moving glass reprojects between source samples instead of running at the capture rate");
            window.Left += 300; window.Top += 30; count = surface.RefractionFrameCount;
            Until(() => surface.RefractionFrameCount > count, surface, "movement resamples new physical background");
            Save(Render(window), "lens-moved");
            var resizeProjection = surface.RefractionProjectionCount;
            window.Width += 70; window.Height += 30;
            Until(() => surface.RefractionProjectionCount > resizeProjection, surface,
                "resize reprojects retained geometry without editor recreation");
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
            count = surface.RefractionFrameCount; window.SetCollapsedState(true, false, false);
            Until(() => surface.RefractionFrameCount > count, surface, "capsule uses the same live material path");
            Program.Assert(surface.HasRefractionWorker && DesktopLensCapture.ReadAffinity(hwnd) == 0x11,
                "collapse retains background processing rather than an opaque static substitute");
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
                    var tile = frame; var pixel = (70 * tile.Layout.PixelWidth + 80) * 4;
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
                Program.Assert(latest != null && capture.TakeLatest() == null && latest.Pixels[0] > 230,
                    "one-slot mailbox resumes on real background changes");
            }
            Program.Assert(DesktopLensCapture.ReadAffinity(hwnd) == 0, "capture lease restores prior affinity");
        }
        finally { front.Close(); rear.Close(); }
    }
    internal static void CheckOptics()
    {
        var profile = LensDisplacement.ProfileBitmap;
        Program.Assert(profile.IsFrozen && ReferenceEquals(profile, LensDisplacement.ProfileBitmap),
            "all lens sizes share a frozen one-dimensional optical profile");
        var previous = 1d;
        for (var i = 0; i <= 512; i++)
        {
            var p = LensDisplacement.ProfileAt(i / 512d);
            Program.Assert(double.IsFinite(p.Shift) && p.Shift is >= 0 and <= 1 &&
                p.Slope is >= 0 and <= 1 && p.Fresnel is >= 0 and <= 1 && p.Coverage is >= 0 and <= 1,
                "finite optical lookup coefficients");
            Program.Assert(p.Shift <= previous, "no secondary peak or folded inner rim");
            previous = p.Shift;
        }
        Program.Assert(LensDisplacement.ProfileAt(0).Shift == 1 &&
            LensDisplacement.ProfileAt(.999).Shift < .0001,
            "monotone shoulder joins the flat body with zero displacement and slope");
        var size = new Size(430, 350);
        for (var y = 1; y < 350; y += 13) for (var x = 1; x < 430; x += 13)
        {
            var (a, f) = LensDisplacement.Sample(new Point(x, y), size, 8);
            var (b, _) = LensDisplacement.Sample(new Point(430-x, y), size, 8);
            Program.Assert(double.IsFinite(a.X) && double.IsFinite(a.Y) && f is >= 0 and <= 1 &&
                Math.Abs(a.X) <= GlassMetrics.For(size, false).Displacement && Math.Abs(a.Y) <= GlassMetrics.For(size, false).Displacement && Math.Abs(a.X+b.X) < .001,
                "finite symmetric inward optical shoulder");
        }
        Program.Assert(LensDisplacement.Sample(new Point(2, 175), size, 8).Offset.X is > 6 and < 11,
            "reference inward edge profile displaces actual source pixels");
        foreach (var point in new[] { new Point(215,175), new Point(24,100), new Point(406,100) })
            Program.Assert(LensDisplacement.Sample(point, size, 8) == (new Vector(), 0d),
                "the shoulder profile joins the body at zero offset and coverage; body magnification is a separate shader transform");
        foreach (var dark in new[] { false, true })
        {
            var small = GlassMetrics.For(new Size(240, 160), dark);
            var large = GlassMetrics.For(new Size(1000, 800), dark);
            var narrow = GlassMetrics.For(new Size(160, 1000), dark);
            Program.Assert(large.Bezel > small.Bezel && narrow.Bezel < large.Bezel &&
                small.Bezel > 0 && narrow.Bezel > 0 &&
                large.Displacement > small.Displacement && small.Displacement > 0 &&
                narrow.Displacement > 0,
                "larger reading surfaces deepen the shoulder; narrow surfaces retain a shallower rim");
            foreach (var candidate in new[] { large, narrow })
            {
                Program.Assert(candidate.Blur == small.Blur && candidate.Tint == small.Tint &&
                    candidate.Saturation == small.Saturation,
                    "size changes shoulder geometry, not body blur, tint or saturation");
            }
            Program.Assert(small.Blur > 0 && small.Tint is > 0 and < 1 && small.Saturation >= 1,
                "body filtering is still enabled, not removed to satisfy resize invariance");
        }
        Program.Assert(GlassMetrics.For(new Size(240, 160), true).Tint >
            GlassMetrics.For(new Size(240, 160), false).Tint,
            "dark and light modes retain distinct transmission recipes");
        var last = GlassMetrics.For(new Size(200, 180), false);
        for (var width = 201; width <= 1600; width++)
        {
            var next = GlassMetrics.For(new Size(width, width * .9), false);
            Program.Assert(next.Bezel >= last.Bezel && next.Bezel - last.Bezel < .04 &&
                next.Blur - last.Blur < .012, "resize changes optical dimensions continuously, without breakpoints");
            last = next;
        }
    }
    private static void CheckCaptureLayout()
    {
        foreach (var dpi in new[] { 1.0, 1.25, 1.5, 2.0 })
        foreach (var size in new[] { new Size(430,350), new Size(1024,768), new Size(2400,1200), new Size(12,8) })
        {
            var width = (int)Math.Ceiling(size.Width*dpi); var height = (int)Math.Ceiling(size.Height*dpi);
            var region = new DesktopLensCapture.Region(1,1,width,height,(int)Math.Ceiling(48*dpi));
            var layout = LensCaptureLayout.Create(new Int32Rect(-700,50,width,height), region, new Int32Rect(-8192,-2160,16384,8640));
            Program.Assert(layout != null && (long)layout.PixelWidth * layout.PixelHeight <= LensCaptureLayout.PixelBudget,
                "negative screen origins / DPI / large surfaces retain bounded physical capture budget");
            var tile = layout!;
            Program.Assert(tile.Bounds.X <= -699 && tile.Bounds.Y <= 51 &&
                tile.Bounds.X + tile.Bounds.Width >= -699 + width && tile.Bounds.Y + tile.Bounds.Height >= 51 + height,
                "one coherent scene covers the complete optical surface");
            var step = tile.Bounds.Width / tile.PixelWidth;
            Program.Assert(tile.Bounds.Width == tile.PixelWidth * step && tile.Bounds.Height == tile.PixelHeight * step &&
                tile.Bounds.X % step == 0 && tile.Bounds.Y % step == 0,
                "every sample stays on the same integral screen grid, including negative origins");
            Console.WriteLine($"LENS BUDGET {size.Width}x{size.Height}@{dpi}: {tile.PixelWidth * (long)tile.PixelHeight}; stride={step}");

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
            // Twelve-pixel cells exercise both the optical shoulder and body scattering.
            for (var x = -18 + phase % 12; x < 680; x += 12) dc.DrawLine(pen, new Point(x, 0), new Point(x, 510));
            for (var y = -18 + phase % 12; y < 510; y += 12) dc.DrawLine(pen, new Point(0, y), new Point(680, y));
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
