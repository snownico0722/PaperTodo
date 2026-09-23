using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static class MaterialPipelineChecks
{
    internal static void Run(AppController controller)
    {
        CheckStaticLayout();
        Program.Assert(DesktopBackgroundCapture.CaptureRasterOperation == 0x00CC0020,
            "background reads use SRCCOPY without cursor-disrupting CAPTUREBLT");
        CheckPreparedMenu(controller);
        CheckCaptureExclusion();
        CheckStaticCapsuleProjection(controller);
    }

    private static void CheckStaticCapsuleProjection(AppController controller)
    {
        var saved = (controller.State.PaperSkin, controller.State.Theme,
            controller.State.MatchAuxiliaryMaterialStrength);
        controller.State.PaperSkin = PaperSkins.Acrylic;
        controller.State.Theme = "light";
        controller.State.MatchAuxiliaryMaterialStrength = true;
        Theme.Invalidate();

        var content = new TextBlock { Text = "Unchanged foreground" };
        var surface = new SkinBorder
        {
            IsCapsule = true,
            Child = content,
            CornerRadius = new CornerRadius(12)
        };
        var window = new Window
        {
            Left = 100,
            Top = 100,
            Width = 280,
            Height = 160,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Content = surface
        };
        try
        {
            window.Show();
            Until(() => surface.IsBackgroundActive && !surface.HasBackgroundCapture,
                "capsule receives one completed static snapshot");

            var session = surface.BackgroundSessionState!;
            var bitmap = session.Bitmap;
            var frames = surface.BackgroundFrameCount;
            var projections = surface.BackgroundProjectionCount;
            var scene = session.SceneVisual!;
            var before = scene.Offset;

            window.Left += 12;
            window.Top += 8;
            window.UpdateLayout();
            Program.Pump();
            session.TranslationChanged();

            var after = scene.Offset;
            var dpi = VisualTreeHelper.GetDpi(surface);
            Program.Assert(ReferenceEquals(bitmap, session.Bitmap) &&
                surface.BackgroundFrameCount == frames,
                "translation reuses the same immutable snapshot");
            Program.Assert(surface.BackgroundProjectionCount > projections &&
                Math.Abs(after.X - before.X + 12 / dpi.DpiScaleX) < .1 &&
                Math.Abs(after.Y - before.Y + 8 / dpi.DpiScaleY) < .1,
                "translation changes only the screen-space crop");

            var oldFrames = surface.BackgroundFrameCount;
            window.Width += 24;
            window.UpdateLayout();
            session.GeometryChanged();
            Until(() => surface.BackgroundFrameCount > oldFrames && !surface.HasBackgroundCapture,
                "settled geometry takes one replacement snapshot");
            Program.Assert(ReferenceEquals(content, surface.Child) && window.Opacity == 1,
                "static snapshot refresh never replaces or fades foreground content");
        }
        finally
        {
            window.Close();
            (controller.State.PaperSkin, controller.State.Theme,
                controller.State.MatchAuxiliaryMaterialStrength) = saved;
            Theme.Invalidate();
        }

        Program.Assert(!surface.HasBackgroundCapture && !surface.HasMaterialHostSubscription,
            "closed static surface releases capture and host observation");
        Console.WriteLine("PIPELINE: one-shot capture, retained projection and geometry refresh passed.");
    }

    private static void CheckPreparedMenu(AppController controller)
    {
        controller.State.PaperSkin = PaperSkins.Acrylic;
        Theme.Invalidate();
        var surface = new SkinBorder { IsMenu = true, Width = 240, Height = 160 };
        surface.Measure(new Size(240, 160));
        surface.Arrange(new Rect(0, 0, 240, 160));
        var layout = new BackgroundCaptureLayout.Scene(new Int32Rect(0, 0, 240, 160), 240, 160);
        var pixels = Enumerable.Repeat((byte)83, 240 * 160 * 4).ToArray();
        using var frame = new DesktopBackgroundCapture.Frame(layout, pixels);
        surface.PrepareMenuBackground(frame, false);
        var scene = surface.BackgroundSessionState!;
        var bitmap = (BitmapSource)scene.Bitmap!;
        Program.Assert(PresentationSource.FromVisual(surface) == null &&
            !surface.HasBackgroundCapture && bitmap.IsFrozen && scene.HasPreparedFrame,
            "detached menu owns one immutable prepared frame and no capture task");
        var copy = new byte[240 * 160 * 4];
        bitmap.CopyPixels(copy, 240 * 4, 0);
        Program.Assert(copy.All(b => b == 83),
            "prepared menu scene retains the supplied pixels");
        surface.PrepareMenuBackground(null, true);
        Program.Assert(surface.BackgroundSessionState!.Visual == null,
            "fallback preparation releases the staged scene");
    }

    private static void CheckCaptureExclusion()
    {
        var blue = Color.FromRgb(20, 90, 180);
        var rear = new Window
        {
            Left = 90,
            Top = 90,
            Width = 240,
            Height = 180,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = new SolidColorBrush(blue),
            ShowInTaskbar = false,
            Topmost = true
        };
        var sample = new Window
        {
            Left = 130,
            Top = 130,
            Width = 100,
            Height = 80,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Red,
            ShowInTaskbar = false,
            Topmost = true
        };
        try
        {
            rear.Show();
            sample.Show();
            Program.Pump();
            Exception? failure = null;
            using (var capture = new DesktopBackgroundCapture(
                new WindowInteropHelper(sample).Handle,
                new DesktopBackgroundCapture.Region(0, 0, 100, 80, 0),
                sample.Dispatcher,
                ex => failure = ex))
            {
                var until = Environment.TickCount64 + 3000;
                DesktopBackgroundCapture.Frame? captured = null;
                while (captured == null && Environment.TickCount64 < until)
                {
                    Program.Pump();
                    Thread.Sleep(10);
                    if (failure != null) throw new InvalidOperationException("background capture failed", failure);
                    captured = capture.TakeLatest();
                }
                Program.Assert(captured != null, "one-shot background capture completed");
                var frame = captured!;
                var pixels = new byte[frame.Layout.PixelWidth * frame.Layout.PixelHeight * 4];
                frame.Bitmap.CopyPixels(pixels, frame.Layout.PixelWidth * 4, 0);
                var offset = ((frame.Layout.PixelHeight / 2) * frame.Layout.PixelWidth +
                    frame.Layout.PixelWidth / 2) * 4;
                Program.Assert(
                    Math.Abs(pixels[offset] - blue.B) < 12 &&
                    Math.Abs(pixels[offset + 1] - blue.G) < 12 &&
                    Math.Abs(pixels[offset + 2] - blue.R) < 12,
                    "one-shot capture preserves rear content and excludes its own surface");
            }
            Program.Assert(DesktopBackgroundCapture.ReadAffinity(new WindowInteropHelper(sample).Handle) == 0,
                "capture exclusion is restored immediately after the one-shot owner is disposed");
        }
        finally
        {
            sample.Close();
            rear.Close();
        }
    }

    private static void CheckStaticLayout()
    {
        var desktop = new Int32Rect(-8192, -2160, 16384, 8640);
        foreach (var dpiValue in new[] { 1d, 1.25, 1.5, 2d })
        foreach (var size in new[] { new Size(360, 240), new Size(1600, 900), new Size(4096, 2048) })
        {
            var dpi = new DpiScale(dpiValue, dpiValue);
            var padding = BackgroundCaptureLayout.Padding(dpi);
            Program.Assert(padding / 2 >= Math.Ceiling(38 * dpiValue),
                "static snapshot guard fully covers the strongest material diffusion");

            var region = new DesktopBackgroundCapture.Region(
                0, 0, (int)(size.Width * dpiValue), (int)(size.Height * dpiValue), padding);
            var window = new Int32Rect(-2400, 200, region.Width, region.Height);
            var layout = BackgroundCaptureLayout.Create(window, region, desktop)!;
            Program.Assert(
                layout.Bounds.X >= desktop.X &&
                layout.Bounds.Y >= desktop.Y &&
                layout.Bounds.X + layout.Bounds.Width <= desktop.X + desktop.Width &&
                layout.Bounds.Y + layout.Bounds.Height <= desktop.Y + desktop.Height,
                "one-shot layout stays inside the virtual desktop");
            Program.Assert(
                (long)layout.PixelWidth * layout.PixelHeight <= BackgroundCaptureLayout.PixelBudget &&
                layout.PixelWidth <= 2048 &&
                layout.PixelHeight <= 2048,
                "one-shot layout remains inside the bounded pixel budget");
        }
    }

    private static void Until(Func<bool> predicate, string context)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!predicate() && Environment.TickCount64 < deadline)
        {
            Program.Pump();
            Thread.Sleep(10);
        }
        Program.Assert(predicate(), context);
    }
}
