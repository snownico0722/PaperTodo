using System.Buffers;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using PaperTodo;

// Exercise the production layout/upload/retained-visual path without rasterizing a
// window, recording screenshots, or treating CI drawing speed as hardware FPS.
internal static class MaterialPipelineChecks
{
    internal static void Run(AppController controller)
    {
        CheckLayoutReuse();
        Program.Assert(DesktopLensCapture.CaptureRasterOperation == 0x00CC0020,
            "all background reads use SRCCOPY without the cursor-disrupting CAPTUREBLT flag");
        var first = new LiquidRefractionEffect(); var second = new LiquidRefractionEffect();
        var shaderProperty = typeof(ShaderEffect).GetProperty("PixelShader", Program.Private)!;
        var shader = (PixelShader)shaderProperty.GetValue(first)!;
        Program.Assert(shader.IsFrozen && !ReferenceEquals(shader, shaderProperty.GetValue(second)),
            "bytecode is cached, but each effect owns its shader to avoid reverse-event retention");
        first.Shift = new Point(.01, .02);
        first.Scene = new ImageBrush();
        Program.Assert(second.Shift != first.Shift && !ReferenceEquals(first.Scene, second.Scene),
            "per-window textures and uniforms remain isolated");

        var saved = (controller.State.PaperSkin, controller.State.Theme, controller.State.LiquidGlassRefraction);
        controller.State.LiquidGlassRefraction = false;
        try
        {
            CheckPreparedMenu(controller);
            CheckCaptureExclusion();
            // A recipe round trip on the SAME visual detects stale drawing-kind caches.
            controller.State.PaperSkin = PaperSkins.Acrylic; controller.State.Theme = "light"; Theme.Invalidate();
            var content = new TextBlock { Text = "Unchanged foreground" };
            var surface = new SkinBorder { IsCapsule = true, Child = content, CornerRadius = new CornerRadius(12) };
            var window = new Window { Left = 100, Top = 100, Width = 280, Height = 160,
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ShowInTaskbar = false, Content = surface };
            using var frozen = surface.FreezeRefractionForEvidence();
            try
            {
                window.Show(); window.UpdateLayout(); Program.Pump();
                typeof(SkinBorder).GetField("_captureHwnd", Program.Private)!.SetValue(surface, new WindowInteropHelper(window).Handle);
                var origin = surface.PointToScreen(new Point());
                var bounds = new Int32Rect((int)origin.X - 256, (int)origin.Y - 256, 800, 700);
                var layout = new LensCaptureLayout.Scene(bounds, 800, 700);
                WriteFrame(surface, layout, 23);
                var scene = typeof(SkinBorder).GetField("_scene", Program.Private)!.GetValue(surface)!;
                var bitmap = SceneField<WriteableBitmap>(scene, "Bitmap");
                var visual = SceneField<DrawingVisual>(scene, "Visual");
                WriteFrame(surface, layout, 47);
                Program.Assert(ReferenceEquals(bitmap, SceneField<WriteableBitmap>(scene, "Bitmap")), "samples of the same world-space region reuse the bitmap");

                foreach (var skin in new[] { PaperSkins.Acrylic, PaperSkins.LiquidGlass, PaperSkins.Acrylic })
                {
                    controller.State.PaperSkin = skin; Theme.Invalidate(); surface.RefreshSkin();
                    Project(surface);
                    var draws = surface.RefractionSceneDrawCount;
                    var projections = surface.RefractionProjectionCount;
                    var uploads = surface.RefractionFrameCount;
                    for (var i = 0; i < 40; i++) { window.Left += .5; Project(surface); }
                    Program.Assert(surface.RefractionProjectionCount == projections + 40 && surface.RefractionSceneDrawCount == draws,
                        skin + ": motion changes only crop/offset, never re-records scene drawing");
                    Program.Assert(surface.RefractionFrameCount == uploads && ReferenceEquals(bitmap, SceneField<WriteableBitmap>(scene, "Bitmap")),
                        skin + ": motion needs no new sample or texture allocation");
                    Program.Assert(skin == PaperSkins.LiquidGlass ? visual.Offset == new Vector() : visual.Offset.X != 0,
                        skin + ": liquid uses uniforms; diffusion uses a world-space visual offset");
                }
                // A new origin is a different pixel version even if its dimensions match.
                // Never overwrite pixels still referenced by the old crop/visual commands.
                var recentered = layout with { Bounds = new Int32Rect(bounds.X + 128, bounds.Y, bounds.Width, bounds.Height) };
                WriteFrame(surface, recentered, 63);
                var movedBitmap = SceneField<WriteableBitmap>(scene, "Bitmap");
                Program.Assert(!ReferenceEquals(bitmap, movedBitmap), "same-size recenter publishes a new mapped texture");
                var retainedPixels = new byte[layout.PixelWidth * layout.PixelHeight * 4];
                bitmap.CopyPixels(retainedPixels, layout.PixelWidth * 4, 0);
                Program.Assert(retainedPixels.All(b => b == 47), "recenter does not mutate pixels referenced by previous drawing commands");
                WriteFrame(surface, recentered, 71);
                Program.Assert(ReferenceEquals(movedBitmap, SceneField<WriteableBitmap>(scene, "Bitmap")),
                    "after recenter, stationary updates resume bitmap reuse");
                var larger = layout with { Bounds = new Int32Rect(bounds.X, bounds.Y, 810, 700), PixelWidth = 810 };
                WriteFrame(surface, larger, 79);
                var replacement = SceneField<WriteableBitmap>(scene, "Bitmap");
                Program.Assert(!ReferenceEquals(bitmap, replacement) && replacement.PixelWidth == 810,
                    "resized scene replaces its texture only after successful upload");
                Program.Assert(ReferenceEquals(SceneField<ImageBrush>(scene, "SceneBrush").ImageSource, replacement),
                    "recipe changes sample the committed texture, not an old bitmap");
                Program.Assert(ReferenceEquals(surface.Child, content) && surface.Effect == null && window.Opacity == 1,
                    "background optimization leaves foreground ownership and opacity unchanged");
                Console.WriteLine("PIPELINE: 120 projections, 0 motion-driven scene recordings/uploads; texture reuse and recipe round-trip passed.");
            }
            finally { window.Close(); }
            Program.Assert(!surface.HasRefractionWorker && !surface.HasRefractionRenderSubscription, "closed scene owns no capture or rendering callback");
        }
        finally
        {
            (controller.State.PaperSkin, controller.State.Theme, controller.State.LiquidGlassRefraction) = saved;
            Theme.Invalidate();
        }
    }
    private static void CheckPreparedMenu(AppController controller)
    {
        controller.State.PaperSkin = PaperSkins.LiquidGlass; Theme.Invalidate();
        var surface = new SkinBorder { IsMenu = true, Width = 240, Height = 160 };
        surface.Measure(new Size(240, 160)); surface.Arrange(new Rect(0, 0, 240, 160));
        var layout = new LensCaptureLayout.Scene(new Int32Rect(0, 0, 240, 160), 240, 160);
        var pixels = ArrayPool<byte>.Shared.Rent(240 * 160 * 4);
        pixels.AsSpan(0, 240 * 160 * 4).Fill(83);
        // Ownership transfers to the surface. It must upload and release the pooled
        // frame before a popup/window exists, not only record a successful OnRender.
        surface.PrepareMenuBackground(new DesktopLensCapture.Frame(layout, pixels), false);
        var scene = typeof(SkinBorder).GetField("_scene", Program.Private)!.GetValue(surface)!;
        var bitmap = SceneField<WriteableBitmap>(scene, "Bitmap");
        Program.Assert(PresentationSource.FromVisual(surface) == null && !surface.HasRefractionWorker && bitmap.IsFrozen,
            "detached menu has immutable initial pixels without a hidden HWND or capture worker");
        Program.Assert(typeof(SkinBorder).GetField("_preparedFrame", Program.Private)!.GetValue(surface) == null,
            "first render has no remaining pooled-frame upload");
        var copy = new byte[240 * 160 * 4]; bitmap.CopyPixels(copy, 240 * 4, 0);
        Program.Assert(copy.All(b => b == 83), "frozen first scene retains its pixels after pooled input is released");
        surface.PrepareMenuBackground(null, true);
        Program.Assert(typeof(SkinBorder).GetField("_scene", Program.Private)!.GetValue(surface) == null,
            "cancelled/fallback preparation releases the staged scene");
        Console.WriteLine("MENU PREP: immutable pixels ready before HWND; pooled input released; fallback cleans scene.");
    }
    private static void CheckCaptureExclusion()
    {
        // Check only raw capture bytes and exclusion, not screenshot/rasterized aesthetics.
        // Removing CAPTUREBLT must not turn the lens into recursively captured content.
        var blue = Color.FromRgb(20, 90, 180);
        var rear = new Window { Left = 90, Top = 90, Width = 240, Height = 180,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = new SolidColorBrush(blue),
            ShowInTaskbar = false, Topmost = true };
        var lens = new Window { Left = 130, Top = 130, Width = 100, Height = 80,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Red,
            ShowInTaskbar = false, Topmost = true };
        try
        {
            rear.Show(); lens.Show(); Program.Pump();
            Exception? failure = null;
            using var capture = new DesktopLensCapture(new WindowInteropHelper(lens).Handle,
                new DesktopLensCapture.Region(0, 0, 100, 80, 0), lens.Dispatcher, ex => failure = ex);
            var until = Environment.TickCount64 + 3000;
            var matched = false;
            while (!matched && Environment.TickCount64 < until)
            {
                Program.Pump(); Thread.Sleep(15);
                if (failure != null) throw new InvalidOperationException("cursor-safe capture failed", failure);
                using var frame = capture.TakeLatest();
                if (frame == null) continue;
                var offset = ((frame.Layout.PixelHeight / 2) * frame.Layout.PixelWidth + frame.Layout.PixelWidth / 2) * 4;
                var b = frame.Pixels[offset]; var g = frame.Pixels[offset + 1]; var r = frame.Pixels[offset + 2];
                matched = Math.Abs(b - blue.B) < 12 && Math.Abs(g - blue.G) < 12 && Math.Abs(r - blue.R) < 12;
                Console.WriteLine($"CAPTURE SRCCOPY: rear BGR={b}/{g}/{r}; excluded red lens; match={matched}");
            }
            Program.Assert(matched, "cursor-safe capture preserves DWM layered background and excludes the capturing lens");
        }
        finally { lens.Close(); rear.Close(); }
    }
    private static void CheckLayoutReuse()
    {
        var desktop = new Int32Rect(-8192, -2160, 16384, 8640);
        foreach (var dpi in new[] { 1d, 1.25, 1.5, 2d })
        foreach (var size in new[] { new Size(360, 240), new Size(1600, 900) })
        {
            var region = new DesktopLensCapture.Region(0, 0, (int)(size.Width * dpi), (int)(size.Height * dpi), (int)(256 * dpi));
            var original = new Int32Rect(-2400, 200, region.Width, region.Height);
            var cached = LensCaptureLayout.Create(original, region, desktop)!;
            for (var dx = -64; dx <= 64; dx++)
            {
                var moved = original; moved.X += dx;
                Program.Assert(ReferenceEquals(cached, LensCaptureLayout.Create(moved, region, desktop, cached)),
                    "small moves preserve scene bounds, filter phase and bitmap dimensions");
            }
            var far = original; far.X += 3 * region.Padding;
            var next = LensCaptureLayout.Create(far, region, desktop, cached)!;
            Program.Assert(!ReferenceEquals(cached, next) && next.Bounds.X <= far.X && next.Bounds.X + next.Bounds.Width >= far.X + region.Width,
                "crossing the guard recenters the scene to cover the actual surface");
            Program.Assert((long)next.PixelWidth * next.PixelHeight <= LensCaptureLayout.PixelBudget, "retained scenes keep the same pixel budget");
        }
        var testRegion = new DesktopLensCapture.Region(0, 0, 430, 350, 256);
        LensCaptureLayout.Scene? previous = null; var retainedChanges = 0; var unretainedChanges = 0;
        LensCaptureLayout.Scene? uncached = null;
        for (var x = 0; x <= 360; x += 2)
        {
            var window = new Int32Rect(x, 400, 430, 350);
            var fresh = LensCaptureLayout.Create(window, testRegion, desktop);
            var retained = LensCaptureLayout.Create(window, testRegion, desktop, previous);
            if (fresh != uncached) unretainedChanges++;
            if (retained != previous) retainedChanges++;
            uncached = fresh; previous = retained;
        }
        Program.Assert(retainedChanges < unretainedChanges / 10, "overscan is reused rather than recaptured at each new origin");
        Console.WriteLine($"LAYOUT: 181 synthetic positions; scene changes without retention={unretainedChanges}, retained={retainedChanges} (not an FPS benchmark).");
    }
    private static T SceneField<T>(object scene, string name) => (T)scene.GetType().GetField(name, Program.Private)!.GetValue(scene)!;
    private static void Project(SkinBorder surface) => typeof(SkinBorder).GetMethod("UpdateRefractionCrop", Program.Private)!.Invoke(surface, null);
    private static void WriteFrame(SkinBorder surface, LensCaptureLayout.Scene layout, byte value)
    {
        var length = checked(layout.PixelWidth * layout.PixelHeight * 4);
        var bytes = ArrayPool<byte>.Shared.Rent(length);
        bytes.AsSpan(0, length).Fill(value);
        using var frame = new DesktopLensCapture.Frame(layout, bytes);
        var present = typeof(SkinBorder).GetMethod("PresentRefraction", Program.Private)!;
        var deadline = Environment.TickCount64 + 2000;
        while (!(bool)present.Invoke(surface, [frame])!)
        {
            Program.Assert(Environment.TickCount64 < deadline, "nonblocking upload eventually acquires a bitmap");
            Program.Pump(); Thread.Sleep(1);
        }
        var scene = typeof(SkinBorder).GetField("_scene", Program.Private)!.GetValue(surface)!;
        var copy = new byte[length];
        SceneField<WriteableBitmap>(scene, "Bitmap").CopyPixels(copy, layout.PixelWidth * 4, 0);
        // Raw texture byte verification, not desktop/image rendering or screenshot inspection.
        Program.Assert(copy.AsSpan().SequenceEqual(bytes.AsSpan(0, length)), "single contiguous upload preserves every source byte");
    }
}
