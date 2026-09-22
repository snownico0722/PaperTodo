using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static class MaterialDragChecks
{
    internal static void Run(AppController controller)
    {
        Program.Assert(MaterialSurfaceHost.ClassifyWindowPosition(0x0015) == MaterialHostChange.Translation,
            "position-only notification is translation, not size/visibility");
        Program.Assert(MaterialSurfaceHost.ClassifyWindowPosition(0x0017) == MaterialHostChange.None,
            "Z-order-only notification does not schedule material work");
        Program.Assert(MaterialSurfaceHost.ClassifyWindowPosition(0x0016) == MaterialHostChange.Geometry,
            "resize retains geometry refresh");
        Program.Assert((MaterialSurfaceHost.ClassifyWindowPosition(0x0037) & MaterialHostChange.Geometry) != 0,
            "frame change retains local coordinate refresh");
        foreach (var visibility in new uint[] { 0x40, 0x80 })
            Program.Assert((MaterialSurfaceHost.ClassifyWindowPosition(0x0015 | visibility) & MaterialHostChange.Visibility) != 0,
                "show/hide mixed with a move cannot take the translation-only shortcut");
        var saved = (controller.State.PaperSkin, controller.State.LiveBackgroundProcessing, controller.State.EnableAnimations);
        var content = new TextBlock { Text = "Retained foreground", Margin = new Thickness(8) };
        controller.State.PaperSkin = PaperSkins.Acrylic;
        controller.State.LiveBackgroundProcessing = true;
        controller.State.EnableAnimations = true;
        Theme.Invalidate();
        var surface = new SkinBorder { IsCapsule = true, CornerRadius = new CornerRadius(8), Child = content };
        var window = new Window { Left = 220, Top = 220, Width = 220, Height = 100, Content = surface,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize };
        try
        {
            window.Show(); Until(() => surface.IsBackgroundActive, "initial sampled surface"); Wait(200);
            var hwnd = new WindowInteropHelper(window).Handle;
            var capture = surface.BackgroundSessionState!.Capture;
            var scene = surface.BackgroundSessionState.SceneVisual;
            var before = scene!.Offset;
            var projected = surface.BackgroundProjectionCount;
            GetWindowRect(hwnd, out var bounds);
            Program.Assert(SetWindowPos(hwnd, IntPtr.Zero, bounds.Left + 12, bounds.Top + 8, 0, 0, 0x0015), "native translation");
            Until(() => surface.BackgroundProjectionCount > projected, "world-space background follows translation");
            var dpi = VisualTreeHelper.GetDpi(surface);
            var after = scene.Offset;
            Program.Assert(Math.Abs(after.X - before.X + 12 / dpi.DpiScaleX) < .01 &&
                Math.Abs(after.Y - before.Y + 8 / dpi.DpiScaleY) < .01, "translation reprojects the same retained world-space scene");
            Program.Assert(ReferenceEquals(capture, surface.BackgroundSessionState.Capture) && ReferenceEquals(content, surface.Child),
                "translation preserves capture worker and foreground");
            surface.Opacity = .6; Wait(60);
            Program.Assert(!surface.HasBackgroundWorker, "opacity still stops capture");
            surface.Opacity = 1; Until(() => surface.IsBackgroundActive, "opacity restore");
            window.Hide(); Wait(60);
            Program.Assert(!surface.HasBackgroundWorker && !surface.HasBackgroundRenderSubscription, "hide releases capture and rendering");
            window.Show(); Until(() => surface.IsBackgroundActive, "show restore");
            window.Width += 18; window.UpdateLayout(); Wait(120);
            Program.Assert(surface.HasBackgroundWorker && surface.BackgroundFailure == null && ReferenceEquals(content, surface.Child),
                "resize still refreshes region without replacing foreground");
        }
        finally
        {
            window.Close();
            (controller.State.PaperSkin, controller.State.LiveBackgroundProcessing, controller.State.EnableAnimations) = saved;
            Theme.Invalidate();
        }
        Program.Assert(!surface.HasBackgroundWorker && !surface.HasMaterialHostSubscription, "closed drag surface releases subscriptions");
        CheckAtomicReflection(controller);
        Console.WriteLine("PASS material translation: classification, retained projection, opacity, hide/show, resize, atomic Aero reflection and teardown.");
    }
    private static void CheckAtomicReflection(AppController controller)
    {
        var saved = (controller.State.PaperSkin, controller.State.EnableAnimations);
        controller.State.PaperSkin = PaperSkins.Aero; controller.State.EnableAnimations = true; Theme.Invalidate();
        var surface = new SkinBorder { IsCapsule = true, CornerRadius = new CornerRadius(8) };
        var window = new Window { Left = 220, Top = 220, Width = 220, Height = 100, Content = surface,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize };
        try
        {
            window.Show(); Wait(200);
            var shift = (Transform)typeof(SkinBorder).GetField("_reflectionShift", Program.Private)!.GetValue(surface)!;
            var changes = 0; shift.Changed += (_, _) => changes++;
            var hwnd = new WindowInteropHelper(window).Handle;
            GetWindowRect(hwnd, out var bounds);
            Program.Assert(SetWindowPos(hwnd, IntPtr.Zero, bounds.Left + 12, bounds.Top + 8, 0, 0, 0x0015), "Aero translation");
            Wait(100);
            Program.Assert(changes == 1, "one window movement publishes one combined reflection transform");
            Program.Assert(MaterialSurfaceHost.TryGetScreenOrigin(surface, HwndSource.FromHwnd(hwnd)!, out var origin), "Aero world origin");
            var dpi = VisualTreeHelper.GetDpi(surface);
            var reference = new TranslateTransform(-origin.X / dpi.DpiScaleX * .10, -origin.Y / dpi.DpiScaleY * .06).Value;
            Program.Assert(shift.Value == reference, "combined transform preserves exact former X/Y translation");
            controller.State.EnableAnimations = false; surface.RefreshSkin();
            Program.Assert(shift.Value.IsIdentity, "disabling reflection restores identity without retaining motion");
        }
        finally
        {
            window.Close();
            (controller.State.PaperSkin, controller.State.EnableAnimations) = saved; Theme.Invalidate();
        }
    }

    private static void Until(Func<bool> predicate, string context)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate() && started.Elapsed.TotalSeconds < 4) Wait(10);
        Program.Assert(predicate(), context);
    }
    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
}
