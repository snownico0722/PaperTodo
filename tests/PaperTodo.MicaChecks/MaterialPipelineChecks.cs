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
                Program.Assert(ReferenceEquals(bitmap, SceneField<WriteableBitmap>(scene, "Bitmap")), "same-sized samples reuse the bitmap");

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
