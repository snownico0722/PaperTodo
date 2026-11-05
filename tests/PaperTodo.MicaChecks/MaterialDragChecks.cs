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
        CheckAtomicReflection(controller);
        Console.WriteLine("PASS Aero translation: one combined transform, correct native origin and teardown.");
    }

    private static void CheckAtomicReflection(AppController controller)
    {
        var saved = (controller.State.PaperSkin, controller.State.EnableAnimations);
        controller.State.PaperSkin = PaperSkins.Aero;
        controller.State.EnableAnimations = true;
        Theme.Invalidate();
        var surface = new SkinBorder { IsCapsule = true, CornerRadius = new CornerRadius(8) };
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
            var shift = (Transform)typeof(SkinBorder).GetField("_reflectionShift", Program.Private)!.GetValue(surface)!;
            var changes = 0;
            shift.Changed += (_, _) => changes++;
            var hwnd = new WindowInteropHelper(window).Handle;
            GetWindowRect(hwnd, out var bounds);
            Program.Assert(SetWindowPos(hwnd, IntPtr.Zero, bounds.Left + 12, bounds.Top + 8, 0, 0, 0x0015),
                "Aero translation");
            Wait(100);
            Program.Assert(changes == 1, "one window movement publishes one combined reflection transform");
            Program.Assert(MaterialSurfaceHost.TryGetScreenOrigin(surface, HwndSource.FromHwnd(hwnd)!, out var origin),
                "Aero world origin");
            var dpi = VisualTreeHelper.GetDpi(surface);
            var reference = new TranslateTransform(
                -origin.X / dpi.DpiScaleX * .10,
                -origin.Y / dpi.DpiScaleY * .06).Value;
            Program.Assert(shift.Value == reference,
                "combined transform preserves exact X/Y reflection translation");
            controller.State.EnableAnimations = false;
            surface.RefreshSkin();
            Program.Assert(shift.Value.IsIdentity,
                "disabling reflection restores identity without retaining motion");
        }
        finally
        {
            window.Close();
            (controller.State.PaperSkin, controller.State.EnableAnimations) = saved;
            Theme.Invalidate();
        }
    }

    private static void Until(Func<bool> predicate, string context)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate() && started.Elapsed.TotalSeconds < 6) Wait(10);
        Program.Assert(predicate(), context);
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
