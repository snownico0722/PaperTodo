using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PaperTodo;

// A settled-window test can hide a one-frame crop lag. Move real HWNDs without pumping
// the dispatcher, then inspect the production sampler mapping BEFORE another frame.
// No desktop screenshots, pixel renders, or synthetic FPS claims.
internal static class MaterialMotionChecks
{
    internal static void Run(AppController controller)
    {
        var saved = (controller.State.PaperSkin, controller.State.Theme, controller.State.LiquidGlassRefraction);
        controller.State.PaperSkin = PaperSkins.LiquidGlass;
        controller.State.Theme = "light";
        controller.State.LiquidGlassRefraction = true;
        Theme.Invalidate();
        try
        {
            foreach (var layered in new[] { false, true }) CheckNativeMotion(layered);
        }
        finally
        {
            (controller.State.PaperSkin, controller.State.Theme, controller.State.LiquidGlassRefraction) = saved;
            Theme.Invalidate();
        }
    }
    private static void CheckNativeMotion(bool layered)
    {
        var translation = new TranslateTransform(.375, .625);
        var content = new TextBlock { Text = "Keep this editor unchanged" };
        var surface = new SkinBorder { Width = 240, Height = 160, RenderTransform = translation,
            CornerRadius = new CornerRadius(12), Child = content };
        var host = new Canvas();
        Canvas.SetLeft(surface, 10); Canvas.SetTop(surface, 12); host.Children.Add(surface);
        var window = new Window { Left = 110, Top = 130, Width = 300, Height = 220,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = layered, Background = Brushes.Transparent, ShowInTaskbar = false,
            Topmost = true, Content = host };
        try
        {
            window.Show(); window.UpdateLayout();
            var deadline = Environment.TickCount64 + 5000;
            while (!surface.IsRefractionActive || surface.HasRefractionRenderSubscription)
            {
                Program.Assert(surface.RefractionFailure == null && Environment.TickCount64 < deadline,
                    "motion test obtains a live scene: " + surface.RefractionFailure);
                Program.Pump(); Thread.Sleep(10);
            }
            var hwnd = new WindowInteropHelper(window).Handle;
            var start = new NativePoint(); Program.Assert(ClientToScreen(hwnd, ref start), "client origin available");
            var uploads = surface.RefractionFrameCount;
            for (var i = 1; i <= 80; i++)
            {
                // Oscillate inside the overscan. Real WM_WINDOWPOSCHANGED, not invoking
                // UpdateRefractionCrop directly and not a dispatcher-pumped approximation.
                var dx = (i % 32) - 16; var dy = (i % 19) - 9;
                Program.Assert(SetWindowPos(hwnd, IntPtr.Zero, start.X + dx, start.Y + dy, 0, 0,
                    0x0001 | 0x0004 | 0x0010), "native window movement succeeds");
                AssertCrop(surface, hwnd, translation);
            }
            Program.Assert(surface.RefractionFrameCount == uploads,
                "80 native movements require neither a texture upload nor a new capture callback");
            Program.Assert(!surface.HasRefractionRenderSubscription,
                "pure movement does not leave a second rendering clock queued");
            Program.Assert(ReferenceEquals(surface.Child, content) && surface.Effect == null && window.Opacity == 1,
                "motion fix preserves content, whole-window opacity and effect ownership");
            // Test fractions that PointToScreen rounds away. Translated shells are legal
            // at any DPI; visual position and capture crop must share the same exact origin.
            foreach (var fraction in new[] { .125, .375, .625, .875 })
            {
                translation.X = fraction; translation.Y = 1 - fraction;
                typeof(SkinBorder).GetMethod("UpdateRefractionCrop", Program.Private)!.Invoke(surface, null);
                AssertCrop(surface, hwnd, translation);
            }
            window.Hide(); Program.Pump();
            Program.Assert(!surface.HasRefractionWorker && !surface.HasRefractionRenderSubscription,
                "native movement fast path still tears down on hide");
            Console.WriteLine($"MOTION {(layered ? "layered" : "native")}: 80 immediate native mappings; no pump/upload; 4 fractional origins; hide released.");
        }
        finally { window.Close(); }
    }
    private static void AssertCrop(SkinBorder surface, IntPtr hwnd, TranslateTransform translation)
    {
        var scene = typeof(SkinBorder).GetField("_scene", Program.Private)!.GetValue(surface)!;
        var effect = (LiquidRefractionEffect)scene.GetType().GetProperty("Effect", Program.Private)!.GetValue(scene)!;
        var layout = (LensCaptureLayout.Scene)scene.GetType().GetField("Layout", Program.Private)!.GetValue(scene)!;
        var native = new NativePoint(); Program.Assert(ClientToScreen(hwnd, ref native), "native origin readable");
        var dpi = VisualTreeHelper.GetDpi(surface);
        // Independent reference: known Canvas coordinates plus known local translation.
        var expected = new Point(native.X + (10 + translation.X) * dpi.DpiScaleX,
            native.Y + (12 + translation.Y) * dpi.DpiScaleY);
        var crop = effect.Crop;
        var actual = new Point(layout.Bounds.X + crop.Z * layout.Bounds.Width,
            layout.Bounds.Y + crop.W * layout.Bounds.Height);
        Program.Assert((actual - expected).Length < .00001,
            $"native motion crop matches HWND immediately: actual={actual}; expected={expected}; no dispatcher pump");
        Program.Assert(Math.Abs(crop.X * layout.Bounds.Width - surface.ActualWidth * dpi.DpiScaleX) < .00001 &&
            Math.Abs(crop.Y * layout.Bounds.Height - surface.ActualHeight * dpi.DpiScaleY) < .00001,
            "moving the scene does not scale the optical footprint");
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
}
