using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using PaperTodo;

internal static class CaptureExclusionChecks
{
    private const uint Exclude = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    internal static void Run(AppController controller)
    {
        var state = controller.State;
        var saved = (state.HidePapersInCapture, state.HideCapsulesInCapture, state.EnableAnimations);
        var paper = new PaperData { Type = PaperTypes.Todo, IsVisible = true, Width = 360, Height = 280 };
        state.Papers.Add(paper);
        state.HidePapersInCapture = true;
        state.HideCapsulesInCapture = false;
        state.EnableAnimations = true;
        var window = new PaperWindow(paper, controller);
        try
        {
            window.Show();
            Program.Pump();
            var hwnd = new WindowInteropHelper(window).Handle;
            Expect(hwnd, Exclude, "expanded paper at startup");
            window.SetCollapsedState(true, animate: true, saveGeometry: false);
            Expect(hwnd, Exclude, "outgoing paper during collapse");
            window.SetCollapsedState(false, animate: true, saveGeometry: false);
            window.SettleAnimationsForDisabledSetting();
            Program.Pump();
            Expect(hwnd, Exclude, "collapse reversed to expanded endpoint");
            window.SetCollapsedState(true, animate: false, saveGeometry: false);
            Program.Pump();
            Expect(hwnd, 0, "unselected collapsed endpoint");

            state.HidePapersInCapture = false;
            state.HideCapsulesInCapture = true;
            window.UpdateCaptureExclusion();
            Expect(hwnd, Exclude, "capsule after live setting change");
            window.SetCollapsedState(false, animate: true, saveGeometry: false);
            Expect(hwnd, Exclude, "outgoing capsule during expansion");
            window.SettleAnimationsForDisabledSetting();
            Program.Pump();
            Expect(hwnd, 0, "unselected expanded endpoint after interruption");

            state.HidePapersInCapture = true;
            window.UpdateCaptureExclusion();
            typeof(PaperWindow).GetMethod("ShowBuiltInFind", Program.Private)!.Invoke(window, null);
            var popup = (Popup)typeof(PaperWindow).GetField("_findPopup", Program.Private)!.GetValue(window)!;
            var popupHwnd = ((HwndSource)PresentationSource.FromVisual(popup.Child)!).Handle;
            CheckAuxiliary(popupHwnd, () =>
            {
                state.HidePapersInCapture = false;
                window.UpdateCaptureExclusion();
            }, "open find popup");
            popup.IsOpen = false;

            var preview = new ExperimentalAttachmentPreviewWindow(state.HideCapsulesInCapture);
            var field = typeof(PaperWindow).GetField("_experimentalCapsuleMagnetPreview", Program.Private)!;
            field.SetValue(window, preview);
            try
            {
                preview.Show();
                CheckAuxiliary(new WindowInteropHelper(preview).Handle, () =>
                {
                    state.HideCapsulesInCapture = false;
                    window.UpdateCaptureExclusion();
                }, "magnet preview");
            }
            finally { field.SetValue(window, null); preview.CloseForOwner(); }
        }
        finally
        {
            window.CloseForReal();
            state.Papers.Remove(paper);
            (state.HidePapersInCapture, state.HideCapsulesInCapture, state.EnableAnimations) = saved;
        }
    }

    private static void Expect(IntPtr hwnd, uint expected, string phase)
    {
        var read = GetWindowDisplayAffinity(hwnd, out var actual);
        Program.Assert(read && actual == expected,
            $"{phase}: expected 0x{expected:X}, got 0x{actual:X}, read={read}, error={Marshal.GetLastWin32Error()}");
    }

    private static void CheckAuxiliary(IntPtr hwnd, Action clearSetting, string phase)
    {
        // Compare to what Windows actually accepts on this transparent HWND. A missed
        // application on a capable host must fail even if the paper itself was excluded.
        var read = GetWindowDisplayAffinity(hwnd, out var initial);
        if (!SetWindowDisplayAffinity(hwnd, Exclude))
        {
            Console.WriteLine($"UNSUPPORTED {phase}: Win32 error {Marshal.GetLastWin32Error()}");
            clearSetting();
            return;
        }
        Program.Assert(read && initial == Exclude, phase + " excluded at creation");
        clearSetting();
        Expect(hwnd, 0, phase + " setting disabled");
    }
}
