using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

// The retained Aero regression covers native HWND motion and exact reflection alignment,
// not desktop capture or auxiliary-material intensity.
internal static class MaterialDragChecks
{
    internal static void Run(AppController controller)
    {
        CheckReflectionAlignment(controller);
        Console.WriteLine("PASS Aero translation: correct native origin and teardown.");
    }

    private static void CheckReflectionAlignment(AppController controller)
    {
        var saved = (controller.State.PaperSkin, controller.State.EnableAnimations);
        controller.State.PaperSkin = PaperSkins.Aero;
        controller.State.EnableAnimations = true;
        Theme.Invalidate();
        var surface = new SkinBorder { CornerRadius = new CornerRadius(8) };
        var window = new Window
        {
            Left = 220,
            Top = 220,
            Width = 220,
            Height = 100,
            Content = surface,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize
        };
        try
        {
            window.Show();
            Wait(200);
            Program.Assert(surface.HasAeroReflectionSubscription,
                "expanded Aero surface observes native movement");
            Transform ReflectionShift() => (Transform)typeof(SkinBorder)
                .GetField("_reflectionShift", Program.Private)!.GetValue(surface)!;
            var hwnd = new WindowInteropHelper(window).Handle;
            GetWindowRect(hwnd, out var bounds);
            Program.Assert(SetWindowPos(hwnd, IntPtr.Zero, bounds.Left + 12, bounds.Top + 8, 0, 0, 0x0015),
                "Aero translation");
            Wait(100);
            Program.Assert(MaterialSurfaceHost.TryGetScreenOrigin(surface, HwndSource.FromHwnd(hwnd)!, out var origin),
                "Aero world origin");
            var dpi = VisualTreeHelper.GetDpi(surface);
            var reference = new TranslateTransform(
                -origin.X / dpi.DpiScaleX * .10,
                -origin.Y / dpi.DpiScaleY * .06).Value;
            Program.Assert(ReflectionShift().Value == reference,
                "reflection preserves exact X/Y translation after native movement");
            controller.State.EnableAnimations = false;
            surface.RefreshSkin();
            Program.Assert(ReflectionShift().Value.IsIdentity,
                "disabling reflection restores identity without retaining motion");
            Program.Assert(!surface.HasMaterialHostSubscription,
                "disabling animation releases the reflection observer");
            controller.State.EnableAnimations = true;
            surface.RefreshSkin();
            Program.Assert(surface.HasMaterialHostSubscription, "reflection observer resumes");
            window.Hide();
            Program.Assert(!surface.HasMaterialHostSubscription,
                "hiding the surface releases its window observer");
        }
        finally
        {
            window.Close();
            (controller.State.PaperSkin, controller.State.EnableAnimations) = saved;
            Theme.Invalidate();
        }
    }

    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(milliseconds),
            DispatcherPriority.Background,
            (_, _) => frame.Continue = false,
            Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectI
    {
        internal int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RectI rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
