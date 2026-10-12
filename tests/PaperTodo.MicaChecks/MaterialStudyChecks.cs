using System.Diagnostics;
using System.Windows;
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
        CheckAeroParallaxLifecycle(controller);
    }

    private static void CheckAeroParallaxLifecycle(AppController controller)
    {
        var saved = (controller.State.PaperSkin, controller.State.Theme,
            controller.State.ColorScheme, controller.State.EnableAnimations);
        PaperWindow? window = null;
        try
        {
            controller.State.PaperSkin = PaperSkins.Aero;
            controller.State.Theme = "light";
            controller.State.ColorScheme = ColorSchemes.Neutral;
            controller.State.EnableAnimations = true;
            Theme.Invalidate();

            window = new PaperWindow(new PaperData
            {
                Type = PaperTypes.Note,
                Title = "Aero 反光",
                Content = "# 反光检查",
                X = 100, Y = 90, Width = 432, Height = 370
            }, controller);
            window.Show();
            Wait(70);

            var surface = (SkinBorder)typeof(PaperWindow)
                .GetField("_paperChrome", Program.Private)!.GetValue(window)!;
            var brush = (LinearGradientBrush)typeof(SkinBorder)
                .GetField("_aeroReflection", Program.Private)!.GetValue(surface)!;
            var shift = brush.Transform;
            var originalX = shift.Value.OffsetX;
            var originalWidth = brush.EndPoint.X - brush.StartPoint.X;

            Program.Assert(surface.HasAeroReflectionSubscription,
                "Aero reflection owns a live-window movement subscription");
            window.Left += 90;
            Wait(60);
            Program.Assert(Math.Abs(shift.Value.OffsetX - originalX + 9) < 0.01,
                "Aero reflection follows window movement in world space");
            window.Width += 80;
            Wait(60);
            Program.Assert(Math.Abs(brush.EndPoint.X - brush.StartPoint.X - originalWidth) < 0.001,
                "Aero reflection geometry is independent of the window width");

            controller.State.EnableAnimations = false;
            window.RefreshSkin();
            Wait(30);
            Program.Assert(!surface.HasAeroReflectionSubscription && shift.Value.IsIdentity,
                "Disabling animations releases the parallax and restores neutral translation");
            window.Hide();
            Program.Assert(!surface.HasBackgroundCapture,
                "Hiding Aero does not keep a background-capture worker alive");
        }
        finally
        {
            window?.CloseForReal();
            (controller.State.PaperSkin, controller.State.Theme,
                controller.State.ColorScheme, controller.State.EnableAnimations) = saved;
            Theme.Invalidate();
        }
    }

    private static void CheckRelief()
    {
        foreach (var skin in new[] { PaperSkins.Aero })
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            var clock = Stopwatch.StartNew();
            var drawing = MaterialRelief.Create(new Size(420, 340), new CornerRadius(16),
                new Thickness(0, 1, 1, 1), new DpiScale(scale, scale), false);
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
    private static void Wait(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
