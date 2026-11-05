using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static class EdgeExpandForegroundChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Prepare(AppState state)
    {
        state.PaperSkin = PaperSkins.Mica;
        state.EnableAnimations = true;
        state.FastAnimations = false;
        state.ExperimentalEdgeCapsuleHoverPreview = false;
    }

    internal static async Task Run(AppController controller, IReadOnlyDictionary<string, PaperWindow> windows)
    {
        var window = windows["fixture-0"];
        var paper = controller.State.Papers.Single(item => item.Id == window.PaperId);
        var presenter = (EdgeCapsulePresenter)Part(window, "_edgeCapsule");
        var host = (EdgeCapsuleHost)Part(window, "_edgeCapsuleHost");
        var cursorSaved = GetCursorPos(out var cursor);
        using var other = new ForegroundWindow();
        HwndSource? source = null;
        var rejectActivation = false;
        var unexpectedActivations = 0;

        IntPtr ObserveActivation(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Observe the real HWND activation messages as well as the final foreground;
            // a stale callback must not briefly steal focus and then return it.
            if (rejectActivation && message == 0x0006 && (wParam.ToInt64() & 0xffff) != 0)
                unexpectedActivations++;
            return IntPtr.Zero;
        }

        bool CapsuleReady() => paper.IsCollapsed && !Transitioning(window) &&
            !presenter.HasActiveTransition && !presenter.NativeBatchRetryPending &&
            presenter.AppliedPresentation.Visible && !presenter.AppliedPresentation.InteractiveBounds.IsEmpty;

        async Task Collapse()
        {
            rejectActivation = false;
            window.SetCollapsedState(true, animate: false);
            await Until(CapsuleReady, "collapsed edge capsule did not settle");
            await DrainActivationCallbacks();
            other.Activate();
        }

        void ForbidFurtherActivation()
        {
            other.Activate();
            unexpectedActivations = 0;
            rejectActivation = true;
        }

        void RequireOtherForeground(string context)
        {
            Require(GetForegroundWindow() == other.Handle && unexpectedActivations == 0,
                context + $": foreground=0x{GetForegroundWindow():X}, expected=0x{other.Handle:X}, " +
                $"unexpected paper activations={unexpectedActivations}");
        }

        string InputState()
        {
            GetCursorPos(out var position);
            return $"collapsed={paper.IsCollapsed}, paperVisible={paper.IsVisible}, " +
                $"windowVisible={window.IsVisible}, transitioning={Transitioning(window)}, " +
                $"progress={window.TransitionProgress:F4}, foreground=0x{GetForegroundWindow():X}, " +
                $"paperHWND=0x{new WindowInteropHelper(window).Handle:X}, hostHWND=0x{host.Handle:X}, " +
                $"cursor={position.X},{position.Y}, WindowFromPoint=0x{WindowFromPoint(position):X}, " +
                $"mouseDirectlyOver={Mouse.DirectlyOver?.GetType().Name ?? "null"}";
        }

        async Task<(double Progress, IntPtr Foreground)> FirstFrame(ProgressObservation progress, string context)
        {
            try { return await progress.FirstFrame.WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (TimeoutException error)
            {
                throw new TimeoutException(context + ": no rendered expansion frame; " + InputState(), error);
            }
        }

        try
        {
            Require(cursorSaved && SetCursorPos(10, 10), "could not park the cursor outside the edge queue");
            await Until(() => windows.Values.All(item => item.IsShellBuilt) && CapsuleReady(),
                "initial edge capsule and startup shells did not settle");
            await DrainActivationCallbacks();
            // Reproduce an ordinary restored paper's history through the real show entry.
            // A never-shown capsule still has WPF's default ShowActivated=true, which would
            // let Show() itself mask a late explicit activation on the first expansion.
            window.SetCollapsedState(false, animate: false);
            controller.ShowPaper(paper, activate: false);
            await Until(() => window.IsVisible && window.Opacity >= 0.999 && !Transitioning(window),
                "background restoration did not finish showing the paper");
            Require(!window.ShowActivated, "background restoration did not establish nonactivating show history");
            await Collapse();

            using (var progress = new ProgressObservation(window))
            {
                var hit = presenter.AppliedPresentation.InteractiveBounds;
                var point = new Point(hit.Left + hit.Width / 2, hit.Top + hit.Height / 2);
                var hostWindow = (Window)Part(host, "Window");
                // Inject movement and wait for both the native target and WPF input state.
                // Back-to-back SetCursorPos/button injection can precede WPF's mouse target.
                LifecycleInput.MoveCursor(point);
                try
                {
                    await Until(() => WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }) == host.Handle &&
                            hostWindow.IsMouseOver && !presenter.HasActiveTransition,
                        "native movement did not reach the edge capsule");
                    // Hover can reveal the close segment. Click the actual content area after
                    // that layout settles so the test cannot accidentally press close.
                    var content = (FrameworkElement)Part(host, "ContentArea");
                    point = content.PointToScreen(new Point(content.ActualWidth / 2, content.ActualHeight / 2));
                    Require(EdgeCapsuleGeometry.Contains(presenter.AppliedPresentation.InteractiveBounds,
                        DeviceScreenPoint.FromPoint(point)), "capsule content is outside the applied hit bounds");
                    LifecycleInput.MoveCursor(point);
                    await Until(() => WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }) == host.Handle &&
                            content.IsMouseOver,
                        "native movement did not reach the edge capsule content");
                }
                catch (TimeoutException error)
                {
                    throw new TimeoutException("edge capsule click target was not ready; " + InputState(), error);
                }
                Console.WriteLine("EDGE_EXPAND_CLICK " + InputState());
                // Exercise the actual NOACTIVATE host and its mouse-up focus cleanup.
                MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
                MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                var first = await FirstFrame(progress, "native edge click");
                var handle = new WindowInteropHelper(window).Handle;
                Require(handle != IntPtr.Zero && first.Foreground == handle,
                    $"EDGE_EXPAND_FOREGROUND: first rendered expansion progress={first.Progress:F4}, " +
                    $"foreground=0x{first.Foreground:X}, paper=0x{handle:X}; " +
                    "explicit edge expansion rendered before the paper owned OS foreground");
                Require(Transitioning(window), "first-frame check ran only after the expansion completed");
                Console.WriteLine($"PASS edge first-frame foreground: progress={first.Progress:F4}, " +
                    $"HWND=0x{handle:X}, native-material-session={controller.UsesNativeMicaWindows}");

                source = HwndSource.FromHwnd(handle);
                Require(source != null, "expanded paper has no real HwndSource");
                source!.AddHook(ObserveActivation);
                // The user switches away while the animation is still running. The deferred
                // mouse-up focus cleanup must never activate this older interaction again.
                ForbidFurtherActivation();
                await Until(() => !Transitioning(window), "user expansion did not finish");
                await DrainActivationCallbacks();
                Require(progress.HasProgressed, "explicit expansion never produced an intermediate animation tick");
                RequireOtherForeground("switching away during expansion was overwritten");
                Console.WriteLine("PASS edge expansion preserves a later foreground choice");
            }

            Require(SetCursorPos(10, 10), "could not move the cursor away from the capsule");
            await Collapse();
            ForbidFurtherActivation();
            using (var progress = new ProgressObservation(window))
            {
                // This production entry passes activateOnExpand=false to the same transition.
                window.ExpandForProgrammaticOpen();
                var first = await FirstFrame(progress, "background programmatic expansion");
                Require(first.Foreground == other.Handle,
                    "background expansion took foreground on its first animation frame");
                await Until(() => !Transitioning(window), "background expansion did not finish");
                await DrainActivationCallbacks();
                Require(progress.HasProgressed, "background expansion never produced an intermediate animation tick");
                RequireOtherForeground("activateOnExpand=false stole foreground");
                Console.WriteLine("PASS background expansion does not activate at any point");
            }

            await Collapse();
            window.ActivateFromEdgeShortcut();
            window.SetCollapsedState(true, animate: false);
            ForbidFurtherActivation();
            await Until(CapsuleReady, "reversed expansion did not return to its edge capsule");
            await DrainActivationCallbacks();
            RequireOtherForeground("cancelled expansion's old callback stole foreground");
            Console.WriteLine("PASS cancelled expansion cannot reactivate its paper");

            rejectActivation = false;
            controller.State.EnableAnimations = false;
            try
            {
                window.ActivateFromEdgeShortcut();
                Require(!paper.IsCollapsed && !Transitioning(window) &&
                    GetForegroundWindow() == new WindowInteropHelper(window).Handle,
                    "animation-disabled explicit expansion did not immediately own foreground");
                ForbidFurtherActivation();
                await DrainActivationCallbacks();
                RequireOtherForeground("animation-disabled expansion overwrote a later foreground choice");
                Console.WriteLine("PASS animation-disabled expansion activates synchronously");
            }
            finally { controller.State.EnableAnimations = true; }
            await Collapse();

            rejectActivation = false;
            window.ActivateFromEdgeShortcut();
            controller.HidePaper(paper);
            ForbidFurtherActivation();
            await Until(() => !window.HasVisibleSurface, "hidden expansion retained a visible surface");
            await DrainActivationCallbacks();
            RequireOtherForeground("hidden expansion's old callback stole foreground");
            Console.WriteLine("PASS hidden expansion cannot reactivate its paper");

            window.SetCollapsedState(true, animate: false);
            controller.ShowPaper(paper, activate: false);
            await Until(CapsuleReady, "reentrant-hide fixture did not restore its edge capsule");
            rejectActivation = false;
            var hiddenDuringActivation = false;
            var staleAnimation = false;
            void HideWhenActivated(object? sender, EventArgs args)
            {
                hiddenDuringActivation = true;
                controller.HidePaper(paper);
            }
            void ObserveClock(object? sender, EventArgs args) => staleAnimation |=
                DependencyPropertyHelper.GetValueSource(window, PaperWindow.TransitionProgressProperty).IsAnimated;
            window.Activated += HideWhenActivated;
            CompositionTarget.Rendering += ObserveClock;
            try
            {
                window.ActivateFromEdgeShortcut();
                ObserveClock(null, EventArgs.Empty);
                Require(hiddenDuringActivation && !paper.IsVisible && !Transitioning(window),
                    "synchronous Activated hide did not cancel the active expansion");
                ForbidFurtherActivation();
                await Until(() => !window.HasVisibleSurface, "Activated hide did not finish hiding");
                await DrainActivationCallbacks();
                Require(!staleAnimation, "the superseded expansion started an animation after Activated hid the paper");
                RequireOtherForeground("Activated hide's superseded expansion stole foreground");
                Console.WriteLine("PASS synchronous Activated hide prevents stale animation and activation");
            }
            finally
            {
                window.Activated -= HideWhenActivated;
                CompositionTarget.Rendering -= ObserveClock;
            }
        }
        finally
        {
            rejectActivation = false;
            source?.RemoveHook(ObserveActivation);
            if (cursorSaved) SetCursorPos(cursor.X, cursor.Y);
        }
    }

    private sealed class ProgressObservation : IDisposable
    {
        private readonly PaperWindow _window;
        private readonly DependencyPropertyDescriptor _descriptor;
        private readonly TaskCompletionSource<(double Progress, IntPtr Foreground)> _first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HasProgressed { get; private set; }

        internal ProgressObservation(PaperWindow window)
        {
            _window = window;
            _descriptor = DependencyPropertyDescriptor.FromProperty(
                PaperWindow.TransitionProgressProperty, typeof(PaperWindow));
            _descriptor.AddValueChanged(window, OnProgressChanged);
            CompositionTarget.Rendering += OnRendering;
        }

        internal Task<(double Progress, IntPtr Foreground)> FirstFrame => _first.Task;

        private void OnProgressChanged(object? sender, EventArgs args)
        {
            var progress = _window.TransitionProgress;
            if (progress > 0 && progress < 1 && Transitioning(_window))
                HasProgressed = true;
        }

        private void OnRendering(object? sender, EventArgs args)
        {
            var paper = (PaperData)Part(_window, "_paper");
            if (!paper.IsCollapsed && Transitioning(_window))
            {
                // Include the zero-progress frame: ContextIdle may run between it and the
                // first positive tick, which would hide the late-foreground defect.
                _first.TrySetResult((_window.TransitionProgress, GetForegroundWindow()));
            }
        }

        public void Dispose()
        {
            CompositionTarget.Rendering -= OnRendering;
            _descriptor.RemoveValueChanged(_window, OnProgressChanged);
        }
    }

    private sealed class ForegroundWindow : IDisposable
    {
        internal IntPtr Handle { get; }

        internal ForegroundWindow()
        {
            Handle = CreateWindowEx(0, "STATIC", "PaperTodo edge foreground check",
                0x00CF0000, 80, 80, 420, 260, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
        }

        internal void Activate() => Require(SetForegroundWindow(Handle) && GetForegroundWindow() == Handle,
            "the interactive test session did not grant foreground to the independent fixture HWND");

        public void Dispose() => DestroyWindow(Handle);
    }

    private static bool Transitioning(PaperWindow window) => (bool)Part(window, "IsPaperFormTransitioning");

    private static object Part(object target, string name) =>
        target.GetType().GetField(name, Private)?.GetValue(target) ??
        target.GetType().GetProperty(name, Private)?.GetValue(target) ??
        throw new MissingMemberException(target.GetType().Name, name);

    private static async Task DrainActivationCallbacks()
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task Until(Func<bool> ready, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException(message);
            await Task.Delay(10);
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style,
        int x, int y, int width, int height, IntPtr owner, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
}
