using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PaperTodo;
using SharpGen.Runtime;
using Vortice.DirectComposition;

// Experiment-only entry. The ordinary application and ordinary check entry are unchanged.
internal static class Routes12Entry
{
    [STAThread]
    private static int Main(string[] args) => Program.RunRoutes12(args);
}

internal static partial class Program
{
    // Device-pixel, immutable translation ticket. No WPF object or user callback crosses threads.
    private sealed class Routes12Ticket
    {
        internal readonly long Epoch, Start, Duration;
        private readonly DeviceScreenRect[] starts;
        internal readonly int Travel;
        internal Routes12Ticket(long epoch, long start, long duration,
            IEnumerable<DeviceScreenRect> rectangles, int travel)
        {
            if (epoch <= 0 || duration <= 0) throw new ArgumentOutOfRangeException(nameof(epoch));
            Epoch = epoch; Start = start; Duration = duration; Travel = travel;
            starts = rectangles.ToArray();
            if (starts.Length == 0 || starts.Any(r => r.IsEmpty))
                throw new ArgumentException("A ticket needs nonempty finite member regions.");
        }
        internal DeviceScreenRect[] Sample(long now)
        {
            var u = Math.Clamp((now - (double)Start) / Duration, 0, 1);
            var dx = (int)Math.Round(Travel * (1 - Math.Pow(1 - u, 3)));
            return starts.Select(r => new DeviceScreenRect(
                checked(r.Left + dx), r.Top, checked(r.Right + dx), r.Bottom)).ToArray();
        }
    }

    // Reuses the production native HWND pair, but the complete pair belongs to this native thread.
    // It is NOT a worker mutating a WPF/UI-owned HWND. No production integration is claimed.
    private sealed class Routes12NativeOwner : IDisposable
    {
        private const uint WorkMessage = 0x8000 + 712;
        private readonly Thread thread;
        private readonly ConcurrentQueue<Action> work = new();
        private readonly TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private EdgeCapsuleQueueProxyWindow? window;
        private Routes12Ticket? ticket;
        private DeviceScreenRect[] applied = [];
        private Exception? failure;
        private uint nativeThread;
        private long epoch;
        private int updates, presses;
        private bool closed;
        internal IntPtr OutputHandle { get; private set; }
        internal IntPtr InputHandle { get; private set; }
        internal int Updates => Volatile.Read(ref updates);
        internal int Presses => Volatile.Read(ref presses);

        internal Routes12NativeOwner(DeviceScreenRect bounds, DeviceScreenRect initial)
        {
            thread = new Thread(() => Run(bounds, initial)) { IsBackground = true, Name = "PaperTodo route1 input experiment" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!started.Task.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Native owner startup did not complete.");
            started.Task.GetAwaiter().GetResult();
        }
        private void Run(DeviceScreenRect bounds, DeviceScreenRect initial)
        {
            try
            {
                nativeThread = NativeInputGetCurrentThreadId();
                applied = [initial];
                window = EdgeCapsuleQueueProxyWindow.TryCreate(bounds, true,
                    p => applied.Any(r => EdgeCapsuleGeometry.Contains(r, p)),
                    _ => Interlocked.Increment(ref presses),
                    () => ticket = null, () => ticket = null, () => ticket = null)
                    ?? throw new InvalidOperationException("Native owner HWND creation failed.");
                if (!window.TrySetInputRegions(applied) || !window.Show(bounds, true))
                    throw new InvalidOperationException("Native owner initial publication failed.");
                OutputHandle = window.Handle;
                InputHandle = window.InputHandle;
                started.SetResult(true);
                while (true)
                {
                    var result = Routes12GetMessage(out var message, IntPtr.Zero, 0, 0);
                    if (result == -1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    if (result == 0) break;
                    if (message.message == WorkMessage)
                    {
                        while (work.TryDequeue(out var action)) action();
                    }
                    else if (message.message == 0x0113 && message.hwnd == window.Handle && message.wParam == new IntPtr(1))
                    {
                        var current = ticket;
                        if (current != null)
                        {
                            var now = Stopwatch.GetTimestamp();
                            var next = current.Sample(now);
                            if (!window.TrySetInputRegions(next))
                                throw new InvalidOperationException("Native region publication failed.");
                            applied = next;
                            Interlocked.Increment(ref updates);
                            if (now - current.Start >= current.Duration)
                            {
                                ticket = null;
                                Routes12KillTimer(window.Handle, 1);
                            }
                        }
                    }
                    else
                    {
                        Routes12TranslateMessage(ref message);
                        Routes12DispatchMessage(ref message);
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                started.TrySetException(ex);
            }
            finally
            {
                ticket = null;
                if (window != null) { Routes12KillTimer(window.Handle, 1); window.Dispose(); }
            }
        }
        private void Invoke(Action action)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (failure != null) throw new InvalidOperationException("Native owner failed.", failure);
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            work.Enqueue(() =>
            {
                try { action(); done.SetResult(true); }
                catch (Exception ex) { done.SetException(ex); }
            });
            if (!Routes12PostThreadMessage(nativeThread, WorkMessage, IntPtr.Zero, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (!done.Task.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Native owner acknowledgement timed out.");
            done.Task.GetAwaiter().GetResult();
        }
        internal bool Publish(Routes12Ticket next)
        {
            var accepted = false;
            Invoke(() =>
            {
                if (next.Epoch <= epoch) return;
                if (Routes12SetTimer(window!.Handle, 1, 8, IntPtr.Zero) == 0)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                epoch = next.Epoch;
                ticket = next;
                accepted = true;
            });
            return accepted;
        }
        internal DeviceScreenRect[] Freeze(long nextEpoch)
        {
            DeviceScreenRect[] snapshot = [];
            Invoke(() =>
            {
                if (nextEpoch <= epoch) throw new InvalidOperationException("Freeze must supersede the current ticket.");
                epoch = nextEpoch;
                ticket = null;
                Routes12KillTimer(window!.Handle, 1);
                snapshot = (DeviceScreenRect[])applied.Clone();
            });
            return snapshot;
        }
        public void Dispose()
        {
            if (closed) return;
            closed = true;
            // Never destroy a native window on the WPF caller thread or hold a UI lock while joining.
            if (thread.IsAlive)
            {
                if (!Routes12PostThreadMessage(nativeThread, 0x0012, IntPtr.Zero, IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if (!thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Native owner did not shut down.");
            }
            if (failure != null) throw new InvalidOperationException("Native owner failed.", failure);
        }
    }

    internal static int RunRoutes12(string[] args)
    {
        if (TryRunProxyNativeInputChild(args, out var childExit)) return childExit;
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            Routes12PolicyChecks();
            // The known-bad H8 control must still reproduce both directions of input mismatch.
            ProxyH8InputTimingChecks();
            Routes12Case(dedicated: true);
            Routes12Case(dedicated: false);
            Console.WriteLine($"ROUTES12 R0: {assertions} assertions passed. Feasibility only; NOT dynamic same-frame acceptance.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Application.Current.Shutdown(); }
    }

    private static void Routes12PolicyChecks()
    {
        var input = new[] { new DeviceScreenRect(-200, -10, -150, 40), new DeviceScreenRect(-100, 60, -50, 110) };
        var ticket = new Routes12Ticket(1, 100, 1000, input, 120);
        input[0] = new DeviceScreenRect(0, 0, 1, 1);
        Check(ticket.Sample(0)[0].Left == -200, "Ticket owns its member snapshot and clamps before start");
        Check(ticket.Sample(1100)[0].Left == -80, "Absolute timeline reaches the exact endpoint");
        Check(ticket.Sample(600)[0].Left == -95, "Both routes share cubic sampling");
        Check(ticket.Sample(1100)[1].Top == 60, "Translation preserves member geometry and holes");
        var copy = ticket.Sample(600); copy[0] = default;
        Check(ticket.Sample(600)[0].Left == -95, "Published samples cannot mutate the ticket");
    }

    private static void Routes12Case(bool dedicated)
    {
        var route = dedicated ? "route1-native-owner" : "route2-app-step";
        Check(NativeInputGetCursorPos(out var cursor), "Routes require an interactive desktop");
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), "Routes need a monitor");
        var bounds = new DeviceScreenRect(monitor.WorkArea.Left + 180, monitor.WorkArea.Top + 180,
            monitor.WorkArea.Left + 400, monitor.WorkArea.Top + 260);
        var initial = new DeviceScreenRect(bounds.Left + 12, bounds.Top + 12, bounds.Left + 60, bounds.Top + 60);
        using var lower = new NativeInputRemoteTarget(bounds, separateProcess: true);
        NativeInputUntil(() => lower.Handle != IntPtr.Zero, "Independent lower process ready");
        NativeInputMove(new DeviceScreenPoint(bounds.Left + 100, bounds.Top + 30));
        NativeInputClick();
        NativeInputUntil(() => lower.Snapshot.Click == 1 && lower.Snapshot.Up == 1, "Lower process baseline gesture");
        Check(NativeInputSetWindowPos(lower.Handle, new IntPtr(-2), 0, 0, 0, 0, 0x0013), "Lower target leaves topmost band");
        var source = new Window
        {
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
            Left = -32000, Top = -32000, Width = 48, Height = 48, Topmost = true,
            Content = new Border { Background = Brushes.Red }
        };
        Routes12NativeOwner? owner = null;
        EdgeCapsuleQueueProxyWindow? uiPair = null;
        IDCompositionDesktopDevice? device = null;
        IDCompositionTarget? target = null;
        IDCompositionVisual2? visual = null;
        IDCompositionAnimation? animation = null;
        IDisposable? surface = null;
        var applied = new[] { initial };
        var uiPresses = 0;
        var sourceCloaked = false;
        try
        {
            source.Show();
            WindowNative.ApplyNoActivateStyle(source);
            Check(WindowNative.TrySetWindowDeviceBounds(source, initial), "Source positioned in device pixels");
            source.UpdateLayout();
            NativeInputPumpFor(80);
            Pr260H8FlushDesktop("routes source preparation");
            var sourceHandle = new WindowInteropHelper(source).Handle;
            IntPtr outputHandle, inputHandle;
            if (dedicated)
            {
                owner = new Routes12NativeOwner(bounds, initial);
                outputHandle = owner.OutputHandle; inputHandle = owner.InputHandle;
                Check(NativeInputGetWindowThreadProcessId(inputHandle, out _) != NativeInputGetCurrentThreadId(),
                    "Route1 input HWND is owned by an independent native message loop");
            }
            else
            {
                uiPair = EdgeCapsuleQueueProxyWindow.TryCreate(bounds, true,
                    p => applied.Any(r => EdgeCapsuleGeometry.Contains(r, p)), _ => uiPresses++,
                    static () => { }, static () => { }, static () => { });
                Check(uiPair != null && uiPair.TrySetInputRegions(applied) && uiPair.Show(bounds, true), "Route2 native pair ready");
                outputHandle = uiPair!.Handle; inputHandle = uiPair.InputHandle;
            }
            var iid = typeof(IDCompositionDesktopDevice).GUID;
            Marshal.ThrowExceptionForHR(NativeInputDCompositionCreateDevice2(IntPtr.Zero, ref iid, out var ptr));
            device = new IDCompositionDesktopDevice(ptr);
            device.CreateTargetForHwnd(outputHandle, true, out target).CheckError();
            device.CreateVisual(out visual).CheckError();
            device.CreateSurfaceFromHwnd(sourceHandle, out var live).CheckError(); surface = live;
            visual.SetContent(live).CheckError();
            visual.SetOffsetX(initial.Left - bounds.Left).CheckError();
            visual.SetOffsetY(initial.Top - bounds.Top).CheckError();
            target.SetRoot(visual).CheckError(); device.Commit().CheckError();
            Pr260H8FlushDesktop("routes cover-before-cloak");
            sourceCloaked = WindowNative.TrySetWindowCloakedBatchDetailed(
                [new WindowNative.WindowCloakChange(sourceHandle, true, false)]) == WindowNative.WindowCloakBatchResult.Success;
            Check(sourceCloaked, "Source protected by published cover before cloak");
            Pr260H8FlushDesktop("routes source cloaked");
            var ticket = new Routes12Ticket(1, Stopwatch.GetTimestamp(), (long)(Stopwatch.Frequency * 0.8), [initial], 120);
            if (dedicated)
            {
                animation = device.CreateAnimation();
                animation.SetAbsoluteBeginTime(ticket.Start).CheckError();
                animation.AddCubic(0, 12, 450, -562.5f, 234.375f).CheckError();
                animation.End(0.8, 132).CheckError();
                visual.SetOffsetX(animation).CheckError(); device.Commit().CheckError();
                Check(owner!.Publish(ticket), "Route1 accepts a new animation epoch");
                Check(!owner.Publish(ticket), "Route1 rejects repeated and stale epochs");
                var before = owner.Updates;
                Thread.Sleep(360);
                Check(owner.Updates > before, "Route1 publishes finite HRGN while WPF UI cannot run");
                applied = owner.Freeze(2);
                Check(applied[0].Left > initial.Left + 12, "Route1 input advanced during WPF stall");
                var frozenUpdates = owner.Updates;
                Thread.Sleep(60);
                Check(owner.Updates == frozenUpdates && !owner.Publish(ticket), "Superseded timer/ticket cannot publish after freeze acknowledgement");
                // Deliberately settle both publications for endpoint routing tests. This is NOT a dynamic pixel/HRGN proof.
                visual.SetOffsetX(applied[0].Left - bounds.Left).CheckError(); device.Commit().CheckError();
            }
            else
            {
                Thread.Sleep(360);
                Check(NativeInputRegionContains(inputHandle, new DeviceScreenPoint(initial.Left + 6, initial.Top + 24)),
                    "Route2 input remains at last published state while UI is blocked");
                var stalePixels = Pr260H8FindRedSpan(bounds, initial.Top + 24);
                Check(Math.Abs(stalePixels.Left - initial.Left) <= 3, "Route2 visual does not autonomously advance during UI stall");
                // One app sample feeds both publications. Win32 region and DComp commit are STILL not atomic.
                applied = ticket.Sample(Stopwatch.GetTimestamp());
                visual.SetOffsetX(applied[0].Left - bounds.Left).CheckError();
                Check(uiPair!.TrySetInputRegions(applied), "Route2 publishes the exact shared sample as finite HRGN");
                device.Commit().CheckError();
            }
            Pr260H8FlushDesktop("routes settled endpoint only");
            var point = new DeviceScreenPoint(applied[0].Left + 12, initial.Top + 24);
            Check(Pr260H8IsRedPixel(point) && NativeInputRegionContains(inputHandle, point), "Settled visible point has native input ownership");
            NativeInputMove(point); NativeInputClick();
            NativeInputUntil(() => (dedicated ? owner!.Presses : uiPresses) == 1, "Settled visible click reaches route owner");
            NativeInputPumpFor(80);
            Check(lower.Snapshot.Click == 1 && lower.Snapshot.Up == 1, "Visible endpoint gesture does not leak to independent lower process");
            var empty = new DeviceScreenPoint(initial.Left + 4, initial.Top + 24);
            Check(!Pr260H8IsRedPixel(empty) && !NativeInputRegionContains(inputHandle, empty), "Vacated point is visually empty and outside input region");
            NativeInputMove(empty); NativeInputClick();
            NativeInputUntil(() => lower.Snapshot.Click == 2 && lower.Snapshot.Up == 2, "Vacated point passes full gesture to lower process");
            Console.WriteLine($"PASS {route} stallMs=360 publicationsDuringStall={(dedicated ? owner!.Updates : 0)} endpointX={applied[0].Left} dynamicSameFrame=UNTESTED captureHandoff=UNTESTED");
        }
        finally
        {
            try
            {
                if (sourceCloaked)
                    _ = WindowNative.TrySetWindowCloakedBatchDetailed([new WindowNative.WindowCloakChange(new WindowInteropHelper(source).Handle, false, true)]);
                if (target != null && device != null) { target.SetRoot(null!).CheckError(); device.Commit().CheckError(); }
            }
            finally
            {
                try { owner?.Dispose(); }
                finally
                {
                    uiPair?.Dispose(); animation?.Dispose(); visual?.Dispose(); surface?.Dispose(); target?.Dispose(); device?.Dispose();
                    source.Close(); NativeInputMove(new DeviceScreenPoint(cursor.X, cursor.Y));
                }
            }
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static extern int Routes12GetMessage(out MSG message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr Routes12DispatchMessage(ref MSG message);
    [DllImport("user32.dll", EntryPoint = "TranslateMessage", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Routes12TranslateMessage(ref MSG message);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Routes12PostThreadMessage(uint thread, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll", EntryPoint = "SetTimer", ExactSpelling = true, SetLastError = true)]
    private static extern nuint Routes12SetTimer(IntPtr hwnd, nuint id, uint interval, IntPtr callback);
    [DllImport("user32.dll", EntryPoint = "KillTimer", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Routes12KillTimer(IntPtr hwnd, nuint id);
}
