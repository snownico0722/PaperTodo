using System.Buffers;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

// Check the stationary-window resize separately from HWND movement. Read production
// scene bounds and uniforms in the same layout pass; no screenshots or dispatcher wait.
internal static class MaterialResizeChecks
{
    internal static void Run(AppController controller)
    {
        var saved = (controller.State.PaperSkin, controller.State.Theme, controller.State.LiquidGlassRefraction);
        controller.State.PaperSkin = PaperSkins.LiquidGlass;
        controller.State.Theme = "light";
        controller.State.LiquidGlassRefraction = false;
        Theme.Invalidate();
        var failures = new List<Exception>();
        void Check(Action check)
        {
            try { check(); }
            catch (Exception ex) { failures.Add(ex); Console.WriteLine("RESIZE FAILURE: " + ex.Message); }
        }
        try
        {
            Check(CheckDensity);
            Check(CheckBodyFilter);
            foreach (var layered in new[] { false, true }) Check(() => CheckLayout(layered));
            if (failures.Count != 0) throw new AggregateException("resize invariants failed", failures);
        }
        finally
        {
            (controller.State.PaperSkin, controller.State.Theme, controller.State.LiquidGlassRefraction) = saved;
            Theme.Invalidate();
        }
    }
    private static void CheckDensity()
    {
        var desktop = new Int32Rect(-8192, -2160, 16384, 8640);
        var window = new Int32Rect(400, 300, 512, 512);
        var region = new DesktopLensCapture.Region(0, 0, 512, 512, 256);
        var scene = LensCaptureLayout.Create(window, region, desktop)!;
        var retained = scene;
        // One-pixel oscillation crosses the nominal full-padding budget. Existing
        // coverage is still valid, so it must not rephase or downsample the middle.
        for (var i = 0; i < 40; i++)
        {
            var next = region with { Width = 512 + i % 2, Height = 512 + i % 2 };
            retained = LensCaptureLayout.Create(window, next, desktop, retained)!;
            Program.Assert(ReferenceEquals(scene, retained), "resize retains covered scene across the nominal density boundary");
        }
        // Force a genuine coverage change, then shrink around the old threshold.
        retained = LensCaptureLayout.Create(window, region with { Width = 850, Height = 850 }, desktop, retained)!;
        Program.Assert(retained.Bounds.Width / retained.PixelWidth == 2, "larger scene reduces density within the budget");
        for (var i = 0; i < 40; i++)
        {
            retained = LensCaptureLayout.Create(window, region with { Width = 512 + i % 2, Height = 512 + i % 2 }, desktop, retained)!;
            Program.Assert(retained.Bounds.Width / retained.PixelWidth == 2, "density hysteresis prevents repeated sharp/soft switches");
            Program.Assert((long)retained.PixelWidth * retained.PixelHeight <= LensCaptureLayout.PixelBudget, "resize never exceeds pixel budget");
        }
        retained = LensCaptureLayout.Create(window, region with { Width = 256, Height = 256 }, desktop, retained)!;
        Program.Assert(retained.PixelWidth == retained.Bounds.Width, "substantial shrink recovers physical-pixel detail");
        Console.WriteLine("RESIZE DENSITY: 80 threshold oscillations stable; genuine growth bounded; shrink recovers detail.");
    }
    private static void CheckBodyFilter()
    {
        foreach (var dark in new[] { false, true })
        {
            var original = GlassMetrics.For(new Size(320, 260), dark);
            for (var i = 0; i < 100; i++)
            {
                var next = GlassMetrics.For(new Size(320 + i * 7.125, 260 + i * 4.375), dark);
                Program.Assert(next.Blur == original.Blur && next.Tint == original.Tint && next.Saturation == original.Saturation,
                    "resize does not change the interior filter or transmission");
                Program.Assert(next.Displacement > 0 && next.Bezel > 0, "curved shoulder remains enabled");
            }
        }
        Console.WriteLine("RESIZE FILTER: 200 body recipes invariant; shoulder retained.");
    }
    private static void CheckLayout(bool layered)
    {
        var content = new TextBlock { Text = "The centre must not swim" };
        var surface = new SkinBorder { Width = 430, Height = 350, CornerRadius = new CornerRadius(12), Child = content };
        var host = new Canvas(); Canvas.SetLeft(surface, 10); Canvas.SetTop(surface, 12); host.Children.Add(surface);
        var window = new Window { Left = 110, Top = 130, Width = 900, Height = 800,
            WindowStyle = WindowStyle.None, AllowsTransparency = layered, Background = Brushes.Transparent,
            ShowInTaskbar = false, Content = host };
        using var frozen = surface.FreezeRefractionForEvidence();
        try
        {
            window.Show(); window.UpdateLayout(); Program.Pump();
            typeof(SkinBorder).GetField("_captureHwnd", Program.Private)!.SetValue(surface, new WindowInteropHelper(window).Handle);
            var layout = new LensCaptureLayout.Scene(new Int32Rect(-256, -256, 2048, 2048), 1024, 1024);
            var data = ArrayPool<byte>.Shared.Rent(1024 * 1024 * 4); data.AsSpan(0, 1024 * 1024 * 4).Fill(71);
            using (var frame = new DesktopLensCapture.Frame(layout, data))
                Program.Assert((bool)typeof(SkinBorder).GetMethod("PresentRefraction", Program.Private)!.Invoke(surface, [frame])!, "initial private upload succeeds");
            var scene = typeof(SkinBorder).GetField("_scene", Program.Private)!.GetValue(surface)!;
            var effect = (LiquidRefractionEffect)scene.GetType().GetProperty("Effect", Program.Private)!.GetValue(scene)!;
            var bitmap = (WriteableBitmap)scene.GetType().GetField("Bitmap", Program.Private)!.GetValue(scene)!;
            var uploads = surface.RefractionFrameCount;
            var dpi = VisualTreeHelper.GetDpi(surface);
            var client = new NativePoint();
            Program.Assert(ClientToScreen(new WindowInteropHelper(window).Handle, ref client), "client origin readable");
            var origin = new Point(client.X + 10 * dpi.DpiScaleX, client.Y + 12 * dpi.DpiScaleY);
            // Check the old moving-centre magnification independently of stale layout.
            AssertBody(effect, layout, new Point(160.5, 140.5), origin, dpi);
            for (var i = 0; i < 120; i++)
            {
                var t = i < 60 ? i : 119 - i;
                surface.Width = 430 + t * 3.125; surface.Height = 350 + t * 1.375;
                surface.Measure(new Size(surface.Width, surface.Height));
                surface.Arrange(new Rect(10, 12, surface.Width, surface.Height));
                // No UpdateRefractionCrop reflection call, dispatcher pump, desktop read,
                // or render delay here: resizing must publish coherent geometry itself.
                var size = surface.RenderSize;
                var recorded = (Size?)scene.GetType().GetField("LiquidSize", Program.Private)!.GetValue(scene);
                Program.Assert(recorded == size && effect.Extent.X == size.Width && effect.Extent.Y == size.Height,
                    "resize paint commits matching scene extent before another frame");
                Program.Assert(Math.Abs(effect.Crop.X * layout.Bounds.Width - size.Width * dpi.DpiScaleX) < 1e-7 &&
                    Math.Abs(effect.Crop.Y * layout.Bounds.Height - size.Height * dpi.DpiScaleY) < 1e-7,
                    "resize crop has current physical dimensions, not the previous frame");
                AssertBody(effect, layout, new Point(80.5, 90.5), origin, dpi);
                AssertBody(effect, layout, new Point(160.5, 140.5), origin, dpi);
            }
            Program.Assert(surface.RefractionFrameCount == uploads && ReferenceEquals(bitmap, scene.GetType().GetField("Bitmap", Program.Private)!.GetValue(scene)),
                "120 resizes only reproject the retained texture; no new upload");
            Program.Assert(ReferenceEquals(content, surface.Child) && surface.Effect == null && window.Opacity == 1,
                "resize preserves foreground, opacity and effect ownership");
            Console.WriteLine($"RESIZE {(layered ? "layered" : "native")}: 120 same-pass extents; 240 stationary body mappings; no new uploads.");
        }
        finally { window.Close(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    private static void AssertBody(LiquidRefractionEffect effect, LensCaptureLayout.Scene layout, Point point, Point origin, DpiScale dpi)
    {
        // Interior optical-profile value is exactly zero. Extent.W is the legacy body
        // scale slot and must be identity; edge shift/RGB dispersion remain independent.
        var c = effect.Crop; var e = effect.Extent; var b = layout.Bounds;
        var actual = new Point(b.X + (((point.X / e.X - .5) * e.W + .5) * c.X + c.Z) * b.Width,
            b.Y + (((point.Y / e.Y - .5) * e.W + .5) * c.Y + c.W) * b.Height);
        var expected = new Point(origin.X + point.X * dpi.DpiScaleX, origin.Y + point.Y * dpi.DpiScaleY);
        Program.Assert((actual - expected).Length < 1e-7, $"stationary body mapping must not follow resize centre: actual={actual}; expected={expected}");
    }
}
