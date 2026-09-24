using System.ComponentModel;
using System.Runtime.InteropServices;
using PaperTodo;

internal static class Program
{
    private static int _passed;

    [STAThread]
    private static int Main()
    {
        try
        {
            PolicyChecks();
            NativeChecks();
            Console.WriteLine($"PASS {_passed} window-close activation checks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Check(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine("PASS " + name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void PolicyChecks()
    {
        Check("first eligible window wins in live Z-order", () =>
        {
            IntPtr activated = 0;
            var result = WindowCloseActivationPolicy.TryHandoff(
                1, () => 1, w => w + 1, w => w >= 3,
                w => { activated = w; return true; });
            Require(result && activated == 3, "Skipped the next eligible window.");
        });
        Check("background close never inspects or activates the stack", () =>
        {
            Require(!WindowCloseActivationPolicy.TryHandoff(
                1, () => 2, _ => throw new Exception("Unexpected scan"),
                _ => throw new Exception("Unexpected inspection"),
                _ => throw new Exception("Unexpected activation")), "Background close stole focus.");
        });
        Check("uninitialized HWND is a no-op", () =>
        {
            Require(!WindowCloseActivationPolicy.TryHandoff(
                0, () => throw new Exception("Unexpected foreground read"),
                _ => 0, _ => true, _ => true), "Zero HWND was accepted.");
        });
        Check("no foreground HWND is a no-op", () =>
        {
            Require(!WindowCloseActivationPolicy.TryHandoff(
                1, () => 0, _ => throw new Exception("Unexpected scan"),
                _ => true, _ => true), "Null foreground was overwritten.");
        });
        Check("focus changes during inspection are respected", () =>
        {
            IntPtr foreground = 1;
            Require(!WindowCloseActivationPolicy.TryHandoff(
                1, () => foreground, _ => 2,
                _ => { foreground = 9; return true; },
                _ => throw new Exception("Stole the new foreground")), "Overwrote newer focus.");
        });
        Check("activation denial does not skip to another window", () =>
        {
            var attempts = 0;
            Require(!WindowCloseActivationPolicy.TryHandoff(
                1, () => 1, w => w + 1, _ => true,
                _ => { attempts++; return false; }) && attempts == 1,
                "Retried a refused activation against an unrelated window.");
        });
        Check("no target leaves normal close activation alone", () =>
        {
            Require(!WindowCloseActivationPolicy.TryHandoff(
                1, () => 1, w => w == 1 ? 2 : 0, _ => false,
                _ => throw new Exception("Unexpected activation")), "Invented a target.");
        });
        Check("closing window cannot become its own target", () =>
        {
            Require(!WindowCloseActivationPolicy.TryHandoff(
                1, () => 1, _ => 1, _ => true,
                _ => throw new Exception("Activated closing HWND")), "Accepted self target.");
        });
        Check("a changing or cyclic native list cannot hang close", () =>
        {
            var inspections = 0;
            Require(!WindowCloseActivationPolicy.TryHandoff(
                1, () => 1, w => w == 2 ? 3 : 2,
                _ => { inspections++; return false; }, _ => true) && inspections == 4096,
                "Traversal was not bounded.");
        });
    }

    private static void NativeChecks()
    {
        // These are isolated HWNDs only: no AppController, installed plugins or user data files.
        using var closingOwner = new NativeWindow();
        using var targetOwner = new NativeWindow();
        using var closing = new NativeWindow(owner: closingOwner.Handle);
        using var target = new NativeWindow(owner: targetOwner.Handle);
        target.Show();
        closing.Show();

        Check("a peer with its own hidden owner remains eligible", () =>
            Require(WindowNative.IsCloseActivationTarget(target.Handle, closing.Handle),
                "Filtered out a same-process paper hidden from Alt+Tab."));
        Check("hidden owner is not a focus target", () =>
            Require(!WindowNative.IsCloseActivationTarget(targetOwner.Handle, closing.Handle),
                "Accepted an invisible owner."));
        Check("closing HWND is not a focus target", () =>
            Require(!WindowNative.IsCloseActivationTarget(closing.Handle, closing.Handle),
                "Accepted the closing paper."));
        Check("disabled HWND is not a focus target", () =>
        {
            using var window = new NativeWindow();
            window.Show();
            _ = EnableWindow(window.Handle, false);
            Require(!WindowNative.IsCloseActivationTarget(window.Handle, closing.Handle),
                "Accepted a disabled window.");
        });
        Check("tool HWND is not a focus target", () =>
        {
            using var window = new NativeWindow(exStyle: 0x00000080);
            window.Show();
            Require(!WindowNative.IsCloseActivationTarget(window.Handle, closing.Handle),
                "Accepted a tool window.");
        });
        Check("NOACTIVATE HWND is not a focus target", () =>
        {
            using var window = new NativeWindow(exStyle: 0x08000000);
            window.Show();
            Require(!WindowNative.IsCloseActivationTarget(window.Handle, closing.Handle),
                "Accepted a capsule or other passive surface.");
        });
        Check("owned popup being destroyed with the paper is excluded", () =>
        {
            using var popup = new NativeWindow(owner: closing.Handle);
            popup.Show();
            Require(!WindowNative.IsCloseActivationTarget(popup.Handle, closing.Handle),
                "Accepted the closing paper's popup.");
        });
        Check("destroyed HWND is not a focus target", () =>
        {
            var window = new NativeWindow();
            var handle = window.Handle;
            window.Dispose();
            Require(!WindowNative.IsCloseActivationTarget(handle, closing.Handle),
                "Accepted a destroyed window.");
        });
        Check("minimized HWND is not restored by close", () =>
        {
            using var window = new NativeWindow();
            _ = ShowWindow(window.Handle, 7); // SW_SHOWMINNOACTIVE
            Require(!WindowNative.IsCloseActivationTarget(window.Handle, closing.Handle),
                "Accepted a minimized window.");
        });
        Check("DWM-cloaked HWND is not a focus target", () =>
        {
            using var window = new NativeWindow();
            window.Show();
            var cloak = 1;
            Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(window.Handle, 13, ref cloak, sizeof(int)));
            _ = DwmFlush();
            Require(!WindowNative.IsCloseActivationTarget(window.Handle, closing.Handle),
                "Accepted a cloaked window from another desktop or composition surface.");
        });
        Check("real foreground transfers before hidden-owner teardown", () =>
        {
            using var passive = new NativeWindow(exStyle: 0x08000000);
            passive.Show();
            BringToTop(target.Handle);
            BringToTop(passive.Handle);
            BringToTop(closing.Handle);
            Require(SetForegroundWindow(closing.Handle) && GetForegroundWindow() == closing.Handle,
                "The test session did not grant foreground to the fixture; native handoff was not tested.");
            Require(WindowNative.TryHandoffForegroundBeforeClose(closing.Handle) &&
                GetForegroundWindow() == target.Handle, "Native handoff selected the wrong window.");
            // Match PaperWindow.OnClosing: detach and release its owner before destroying the paper.
            _ = SetWindowLongPtr(closing.Handle, -8, IntPtr.Zero);
            closingOwner.Dispose();
            closing.Dispose();
            Require(GetForegroundWindow() == target.Handle, "Owner/paper teardown changed foreground again.");
        });
        Check("deleting a real background HWND preserves the foreground", () =>
        {
            using var background = new NativeWindow();
            background.Show();
            Require(GetForegroundWindow() == target.Handle, "Fixture unexpectedly activated itself.");
            Require(!WindowNative.TryHandoffForegroundBeforeClose(background.Handle),
                "Background close attempted a handoff.");
            background.Dispose();
            Require(GetForegroundWindow() == target.Handle, "Background deletion changed foreground.");
        });
    }

    private static void BringToTop(IntPtr window) =>
        Require(SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 0x0013), "Could not order fixture HWNDs.");

    private sealed class NativeWindow : IDisposable
    {
        internal IntPtr Handle { get; private set; }
        internal NativeWindow(int exStyle = 0, IntPtr owner = default)
        {
            Handle = CreateWindowEx(exStyle, "STATIC", "PaperTodo close activation check",
                0x00CF0000, 40, 40, 180, 100, owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        internal void Show() => _ = ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
        public void Dispose()
        {
            if (Handle != IntPtr.Zero) { _ = DestroyWindow(Handle); Handle = IntPtr.Zero; }
        }
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style,
        int x, int y, int width, int height, IntPtr owner, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr window, bool enabled);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after,
        int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(
        IntPtr window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
