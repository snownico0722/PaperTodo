using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

// Material behavior, not a mock screenshot of a border without its real controls.
internal static class MaterialStudyChecks
{
    internal static void Run(AppController controller)
    {
        CheckRelief();
        var saved = (controller.State.PaperSkin, controller.State.Theme, controller.State.ColorScheme,
            controller.State.EnableAnimations, controller.State.LiquidGlassRefraction);
        var output = Environment.GetEnvironmentVariable("PAPER_SKIN_CAPTURE");
        if (string.IsNullOrWhiteSpace(output)) return;
        var rear = new Window { Left = 20, Top = 20, Width = 720, Height = 550,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
            Background = RearSurface() };
        PaperWindow? window = null;
        try
        {
            rear.Show(); Wait(150);
            using (NativeSurfaceChecks.Capture(rear, output, "study-rear-control")) { }
            foreach (var mode in new[] { "light", "dark" })
            foreach (var skin in new[] { PaperSkins.LiquidGlass, PaperSkins.Aero, PaperSkins.Ceramic, PaperSkins.Paper })
            {
                controller.State.PaperSkin = skin; controller.State.Theme = mode;
                controller.State.ColorScheme = ColorSchemes.Neutral;
                controller.State.EnableAnimations = true; controller.State.LiquidGlassRefraction = true;
                Theme.Invalidate();
                window = new PaperWindow(new PaperData { Type = PaperTypes.Note, Title = "材质 · 日常笔记",
                    Content = "# 留一点桌面空间\n\n正文与按钮保持清晰。\n\n- 整理今天的想法\n- 拖动纸片，观察光线\n- 背景只在边缘发生折射\n\n**材质不应妨碍阅读。**",
                    X = 100, Y = 90, Width = 432, Height = 370, AlwaysOnTop = true }, controller);
                window.Show(); window.Activate(); Wait(300);
                var surface = (SkinBorder)typeof(PaperWindow).GetField("_paperChrome", Program.Private)!.GetValue(window)!;
                if (skin == PaperSkins.LiquidGlass)
                {
                    var timer = Stopwatch.StartNew();
                    while (!surface.IsRefractionActive && timer.ElapsedMilliseconds < 4000 && surface.RefractionFailure == null) Wait(30);
                    Program.Assert(surface.IsRefractionActive, "material study uses actual live refracted desktop: " + surface.RefractionFailure);
                }
                using (NativeSurfaceChecks.Capture(window, output, $"study-{skin}-{mode}")) { }
                if (skin == PaperSkins.Aero)
                {
                    // A successful private API return does not prove visible blur: test
                    // actual background response before accepting an Aero screenshot.
                    var background = rear.Background;
                    rear.Background = Brushes.White; Wait(150);
                    using var white = NativeSurfaceChecks.Capture(window, output, $"study-aero-{mode}-white-control");
                    rear.Background = Brushes.RoyalBlue; Wait(150);
                    using var blue = NativeSurfaceChecks.Capture(window, output, $"study-aero-{mode}-blue-control");
                    var wp = white.GetPixel(340, 260); var bp = blue.GetPixel(340, 260);
                    Console.WriteLine($"AERO REAR {mode}: white={wp}; blue={bp}");
                    Program.Assert(mode != "light" || Math.Min(wp.R, Math.Min(wp.G, wp.B)) > 130,
                        "light Aero must not turn a genuine white rear window into a black underlay");
                    var response = Math.Abs(wp.R-bp.R) + Math.Abs(wp.G-bp.G) + Math.Abs(wp.B-bp.B);
                    if (response <= 50)
                    {
                        // Hosted Server can suppress all blur transparency. Only skip if
                        // the independent, documented system Acrylic control also does.
                        controller.State.PaperSkin = PaperSkins.Acrylic; Theme.Invalidate(); window.UpdateTheme();
                        rear.Background = Brushes.White; Wait(150);
                        using var systemWhite = NativeSurfaceChecks.Capture(window, output, $"study-system-{mode}-white-control");
                        rear.Background = Brushes.RoyalBlue; Wait(150);
                        using var systemBlue = NativeSurfaceChecks.Capture(window, output, $"study-system-{mode}-blue-control");
                        var sw = systemWhite.GetPixel(340, 260); var sb = systemBlue.GetPixel(340, 260);
                        Program.Assert(Math.Abs(sw.R-sb.R) + Math.Abs(sw.G-sb.G) + Math.Abs(sw.B-sb.B) <= 10,
                            "Aero transparency failed while the documented system blur control responds normally");
                        Console.WriteLine($"SKIP Aero rear-color comparison: system control also suppresses transparency ({sw}/{sb}); native Windows 11 appearance still requires manual acceptance.");
                        controller.State.PaperSkin = PaperSkins.Aero; Theme.Invalidate(); window.UpdateTheme();
                    }
                    rear.Background = background; Wait(100);
                    var brush = (LinearGradientBrush)typeof(SkinBorder).GetField("_aeroReflection", Program.Private)!.GetValue(surface)!;
                    var shift = (TranslateTransform)brush.Transform;
                    var oldX = shift.X; var width = brush.EndPoint.X - brush.StartPoint.X;
                    Program.Assert(surface.HasLensLightSubscription, "Aero has only a visible-window movement subscription");
                    window.Left += 90; Wait(80);
                    Program.Assert(Math.Abs(shift.X - oldX + 9) < .01 && brush.MappingMode == BrushMappingMode.Absolute,
                        "Aero reflection shifts continuously in world-space rather than stretching the diagonal");
                    window.Width += 80; Wait(80);
                    Program.Assert(Math.Abs(brush.EndPoint.X - brush.StartPoint.X - width) < .001,
                        "Aero reflection width is independent of window resize");
                    using (NativeSurfaceChecks.Capture(window, output, $"study-aero-{mode}-moved")) { }
                    controller.State.EnableAnimations = false; window.RefreshSkin(); Wait(30);
                    Program.Assert(!surface.HasLensLightSubscription, "disabling animation detaches Aero parallax");
                }
                if (skin is PaperSkins.LiquidGlass or PaperSkins.Aero)
                {
                    window.Left = 60; window.Top = 60;
                    foreach (var size in new[] { new Size(280,240), new Size(640,450), new Size(640,210), new Size(280,450) })
                    {
                        window.Width = size.Width; window.Height = size.Height;
                        var count = surface.RefractionFrameCount;
                        Wait(200);
                        if (skin == PaperSkins.LiquidGlass)
                        {
                            var timer = Stopwatch.StartNew();
                            while (surface.RefractionFrameCount <= count && timer.ElapsedMilliseconds < 4000) Wait(30);
                            Program.Assert(surface.RefractionFrameCount > count, "resized study uses its own geometry and fresh background");
                        }
                        using (NativeSurfaceChecks.Capture(window, output, $"study-{skin}-{mode}-{size.Width}x{size.Height}")) { }
                    }
                }
                window.Hide(); Wait(30);
                Program.Assert(!surface.HasLensLightSubscription && !surface.HasRefractionWorker,
                    "hidden study surface has no optical subscription/capture worker");
                window.CloseForReal(); window = null;
            }
        }
        finally
        {
            window?.CloseForReal(); rear.Close();
            (controller.State.PaperSkin, controller.State.Theme, controller.State.ColorScheme,
                controller.State.EnableAnimations, controller.State.LiquidGlassRefraction) = saved;
            Theme.Invalidate();
        }
    }
    private static void CheckRelief()
    {
        foreach (var skin in new[] { PaperSkins.Ceramic, PaperSkins.Aero })
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            var clock = Stopwatch.StartNew();
            var drawing = MaterialRelief.Create(new Size(420, 340), new CornerRadius(16),
                new Thickness(0, 1, 1, 1), new DpiScale(scale, scale), skin, false);
            var images = drawing.Children.OfType<ImageDrawing>().Select(i => (BitmapSource)i.ImageSource).ToArray();
            Program.Assert(drawing.IsFrozen && images.Length == 4 && images.All(i => i.IsFrozen) &&
                images.Sum(i => i.PixelWidth * i.PixelHeight) < 270000, "frozen perimeter-only lighting has a bounded raster budget");
            var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawDrawing(drawing);
            var rendered = new RenderTargetBitmap(420, 340, 96, 96, PixelFormats.Pbgra32); rendered.Render(visual);
            var bytes = new byte[420 * 340 * 4]; rendered.CopyPixels(bytes, 420 * 4, 0);
            byte Alpha(int x, int y) => bytes[(y * 420 + x) * 4 + 3];
            Program.Assert(Alpha(210, 170) == 0 && Alpha(1, 170) == 0 && Alpha(0, 0) == 0,
                "clear-coat relief cannot fill the content, paint a docked open edge or change corner shape");
            Program.Assert(Enumerable.Range(1, 7).Any(x => Alpha(419 - x, 170) > 0), "curved edge lighting is actually visible");
            Console.WriteLine($"RELIEF {skin}@{scale}: {images.Sum(i => i.PixelWidth * i.PixelHeight)} cached edge pixels, construction {clock.Elapsed.TotalMilliseconds:F2}ms (CI diagnostic)");
        }
    }
    private static Brush RearSurface()
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(105, 178, 207), Color.FromRgb(19, 67, 129), 30), null, new Rect(0, 0, 720, 550));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(187, 213, 190)), null, new Point(60, 115), 165, 310);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(75, 140, 190)), null, new Point(440, 440), 270, 160);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(175, 212, 173, 205)), null, new Rect(295, 35, 115, 255), 20, 20);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(115, 250, 253, 255)), 2);
            for (var x = 0; x < 720; x += 40) dc.DrawLine(pen, new Point(x, 0), new Point(x, 550));
        }
        // Oversized ellipses must not expand the relative brush viewbox into unpainted
        // space. Otherwise the test backdrop itself has black gaps behind clear glass.
        var bounds = new Rect(0, 0, 720, 550);
        drawing.ClipGeometry = new RectangleGeometry(bounds);
        drawing.Freeze();
        var brush = new DrawingBrush(drawing)
        { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds };
        brush.Freeze();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(brush, null, bounds);
        var image = new RenderTargetBitmap(720, 550, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        var bytes = new byte[720 * 550 * 4]; image.CopyPixels(bytes, 720 * 4, 0);
        Program.Assert(Enumerable.Range(0, 720 * 550).All(i => bytes[i * 4 + 3] == 255),
            "the calibration backdrop fills every pixel; transparent fixture gaps are not material evidence");
        return brush;
    }
    private static void Wait(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
