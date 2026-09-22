using System.Buffers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

// Exercise the production bounded-background upload/retained-visual path without
// recording screenshots or treating CI drawing speed as hardware FPS.
internal static class MaterialPipelineChecks
{
    internal static void Run(AppController controller)
    {
        CheckLayoutReuse();
        Program.Assert(DesktopBackgroundCapture.CaptureRasterOperation == 0x00CC0020,
            "all background reads use SRCCOPY without the cursor-disrupting CAPTUREBLT flag");

        var saved = (controller.State.PaperSkin, controller.State.Theme, controller.State.LiveBackgroundProcessing);
        controller.State.LiveBackgroundProcessing = false;
        try
        {
            CheckPreparedMenu(controller);
            CheckCaptureExclusion();

            controller.State.PaperSkin = PaperSkins.Acrylic;
            controller.State.Theme = "light";
            Theme.Invalidate();
            var content = new TextBlock { Text = "Unchanged foreground" };
            var surface = new SkinBorder { IsCapsule = true, Child = content, CornerRadius = new CornerRadius(12) };
            var window = new Window { Left = 100, Top = 100, Width = 280, Height = 160,
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ShowInTaskbar = false, Content = surface };
            using var frozen = surface.FreezeBackgroundForEvidence();
            try
            {
                window.Show();
                window.UpdateLayout();
                Program.Pump();
                var origin = surface.PointToScreen(new Point());
                var bounds = new Int32Rect((int)origin.X - 256, (int)origin.Y - 256, 800, 700);
                var layout = new BackgroundCaptureLayout.Scene(bounds, 800, 700);

                WriteFrame(surface, layout, 23);
                var scene = surface.BackgroundSessionState!;
                var seed = scene.Bitmap!;
                Program.Assert(seed.IsFrozen, "mapped scene pixels are complete before first binding");
                var visual = scene.SceneVisual!;

                WriteFrame(surface, layout, 47);
                var bitmap = scene.Bitmap!;
                Program.Assert(!bitmap.IsFrozen && !ReferenceEquals(seed, bitmap),
                    "same-region content adopts a mutable working texture");
                WriteFrame(surface, layout, 47);
                Program.Assert(ReferenceEquals(bitmap, scene.Bitmap!),
                    "samples of the same world-space region reuse the bitmap");

                foreach (var skin in new[] { PaperSkins.Acrylic, PaperSkins.TracingPaper, PaperSkins.ClearAcrylic })
                {
                    controller.State.PaperSkin = skin;
                    Theme.Invalidate();
                    surface.RefreshSkin();
                    Project(surface);
                    var draws = surface.BackgroundSceneDrawCount;
                    var projections = surface.BackgroundProjectionCount;
                    var uploads = surface.BackgroundFrameCount;
                    for (var i = 0; i < 40; i++)
                    {
                        window.Left += .5;
                        Project(surface);
                    }
                    Program.Assert(surface.BackgroundProjectionCount == projections + 40 &&
                        surface.BackgroundSceneDrawCount == draws,
                        skin + ": motion changes only scene offset/crop, never re-records the sampled image");
                    Program.Assert(surface.BackgroundFrameCount == uploads &&
                        ReferenceEquals(bitmap, scene.Bitmap!),
                        skin + ": motion needs no new sample or texture allocation");
                    Program.Assert(visual.Offset.X != 0,
                        skin + ": diffusion keeps a world-space visual offset");
                }

                var recentered = layout with { Bounds = new Int32Rect(bounds.X + 128, bounds.Y, bounds.Width, bounds.Height) };
                WriteFrame(surface, recentered, 63);
                var movedBitmap = scene.Bitmap!;
                Program.Assert(!ReferenceEquals(bitmap, movedBitmap) && movedBitmap.IsFrozen,
                    "same-size recenter publishes a complete immutable mapped texture");
                var retainedPixels = new byte[layout.PixelWidth * layout.PixelHeight * 4];
                bitmap.CopyPixels(retainedPixels, layout.PixelWidth * 4, 0);
                Program.Assert(retainedPixels.All(b => b == 47),
                    "recenter does not mutate pixels referenced by previous drawing commands");

                WriteFrame(surface, recentered, 71);
                var workingBitmap = scene.Bitmap!;
                Program.Assert(!workingBitmap.IsFrozen, "same-region content resumes the mutable upload path");
                WriteFrame(surface, recentered, 71);
                Program.Assert(ReferenceEquals(workingBitmap, scene.Bitmap!),
                    "after recenter, stationary updates resume bitmap reuse");

                var larger = layout with { Bounds = new Int32Rect(bounds.X, bounds.Y, 810, 700), PixelWidth = 810 };
                WriteFrame(surface, larger, 79);
                var replacement = scene.Bitmap!;
                Program.Assert(!ReferenceEquals(bitmap, replacement) && replacement.PixelWidth == 810,
                    "resized scene replaces its texture only after successful upload");
                Program.Assert(ReferenceEquals(surface.Child, content) && surface.Effect == null && window.Opacity == 1,
                    "background optimization leaves foreground ownership and opacity unchanged");
                Console.WriteLine("PIPELINE: retained diffusion scene projection, texture reuse and recipe round-trip passed.");
            }
            finally
            {
                window.Close();
            }
            Program.Assert(!surface.HasBackgroundWorker && !surface.HasBackgroundRenderSubscription,
                "closed scene owns no capture or rendering callback");
        }
        finally
        {
            (controller.State.PaperSkin, controller.State.Theme, controller.State.LiveBackgroundProcessing) = saved;
            Theme.Invalidate();
        }
    }

    private static void CheckPreparedMenu(AppController controller)
    {
        controller.State.PaperSkin = PaperSkins.Acrylic;
        Theme.Invalidate();
        var surface = new SkinBorder { IsMenu = true, Width = 240, Height = 160 };
        surface.Measure(new Size(240, 160));
        surface.Arrange(new Rect(0, 0, 240, 160));
        var layout = new BackgroundCaptureLayout.Scene(new Int32Rect(0, 0, 240, 160), 240, 160);
        var pixels = ArrayPool<byte>.Shared.Rent(240 * 160 * 4);
        pixels.AsSpan(0, 240 * 160 * 4).Fill(83);
        surface.PrepareMenuBackground(new DesktopBackgroundCapture.Frame(layout, pixels), false);
        var scene = surface.BackgroundSessionState!;
        var bitmap = scene.Bitmap!;
        Program.Assert(PresentationSource.FromVisual(surface) == null && !surface.HasBackgroundWorker && bitmap.IsFrozen,
            "detached menu has immutable initial pixels without a hidden HWND or capture worker");
        Program.Assert(!surface.BackgroundSessionState!.HasPreparedFrame,
            "first render has no remaining pooled-frame upload");
        var copy = new byte[240 * 160 * 4];
        bitmap.CopyPixels(copy, 240 * 4, 0);
        Program.Assert(copy.All(b => b == 83), "frozen first scene retains pixels after pooled input is released");
        surface.PrepareMenuBackground(null, true);
        Program.Assert(surface.BackgroundSessionState!.Visual == null,
            "cancelled/fallback preparation releases the staged scene");
    }

    private static void CheckCaptureExclusion()
    {
        var blue = Color.FromRgb(20, 90, 180);
        var rear = new Window { Left = 90, Top = 90, Width = 240, Height = 180,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = new SolidColorBrush(blue),
            ShowInTaskbar = false, Topmost = true };
        var sample = new Window { Left = 130, Top = 130, Width = 100, Height = 80,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Red,
            ShowInTaskbar = false, Topmost = true };
        try
        {
            rear.Show();
            sample.Show();
            Program.Pump();
            Exception? failure = null;
            using var capture = new DesktopBackgroundCapture(new WindowInteropHelper(sample).Handle,
                new DesktopBackgroundCapture.Region(0, 0, 100, 80, 0), sample.Dispatcher, ex => failure = ex);
            var until = Environment.TickCount64 + 3000;
            var matched = false;
            while (!matched && Environment.TickCount64 < until)
            {
                Program.Pump();
                Thread.Sleep(15);
                if (failure != null) throw new InvalidOperationException("background capture failed", failure);
                using var frame = capture.TakeLatest();
                if (frame == null) continue;
                var offset = ((frame.Layout.PixelHeight / 2) * frame.Layout.PixelWidth + frame.Layout.PixelWidth / 2) * 4;
                var b = frame.Pixels[offset];
                var g = frame.Pixels[offset + 1];
                var r = frame.Pixels[offset + 2];
                matched = Math.Abs(b - blue.B) < 12 && Math.Abs(g - blue.G) < 12 && Math.Abs(r - blue.R) < 12;
            }
            Program.Assert(matched, "bounded background capture preserves rear content and excludes its own surface");
        }
        finally
        {
            sample.Close();
            rear.Close();
        }
    }

    private static void CheckLayoutReuse()
    {
        var desktop = new Int32Rect(-8192, -2160, 16384, 8640);
        foreach (var dpi in new[] { 1d, 1.25, 1.5, 2d })
        foreach (var size in new[] { new Size(360, 240), new Size(1600, 900) })
        {
            var region = new DesktopBackgroundCapture.Region(0, 0, (int)(size.Width * dpi), (int)(size.Height * dpi), (int)(256 * dpi));
            var original = new Int32Rect(-2400, 200, region.Width, region.Height);
            var cached = BackgroundCaptureLayout.Create(original, region, desktop)!;
            for (var dx = -64; dx <= 64; dx++)
            {
                var moved = original;
                moved.X += dx;
                Program.Assert(ReferenceEquals(cached, BackgroundCaptureLayout.Create(moved, region, desktop, cached)),
                    "small moves preserve scene bounds, filter phase and bitmap dimensions");
            }
            var far = original;
            far.X += 3 * region.Padding;
            var next = BackgroundCaptureLayout.Create(far, region, desktop, cached)!;
            Program.Assert(!ReferenceEquals(cached, next) && next.Bounds.X <= far.X &&
                next.Bounds.X + next.Bounds.Width >= far.X + region.Width,
                "crossing the guard recenters the scene to cover the actual surface");
            Program.Assert((long)next.PixelWidth * next.PixelHeight <= BackgroundCaptureLayout.PixelBudget,
                "retained scenes keep the same pixel budget");
        }
    }

    private static void Project(SkinBorder surface) =>
        surface.BackgroundSessionState!.Project();

    private static void WriteFrame(SkinBorder surface, BackgroundCaptureLayout.Scene layout, byte value)
    {
        var length = checked(layout.PixelWidth * layout.PixelHeight * 4);
        var bytes = ArrayPool<byte>.Shared.Rent(length);
        bytes.AsSpan(0, length).Fill(value);
        using var frame = new DesktopBackgroundCapture.Frame(layout, bytes);
        var session = surface.BackgroundSessionState!;
        var deadline = Environment.TickCount64 + 2000;
        while (!session.PresentFrame(frame))
        {
            Program.Assert(Environment.TickCount64 < deadline, "nonblocking upload eventually acquires a bitmap");
            Program.Pump();
            Thread.Sleep(1);
        }
        var scene = surface.BackgroundSessionState!;
        var copy = new byte[length];
        scene.Bitmap!.CopyPixels(copy, layout.PixelWidth * 4, 0);
        Program.Assert(copy.AsSpan().SequenceEqual(bytes.AsSpan(0, length)),
            "single contiguous upload preserves every source byte");
    }
}
