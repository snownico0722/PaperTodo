using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;
using SharpGen.Runtime;
using Vortice.DirectComposition;

internal static partial class Program
{
    internal static int RunPr260H8DedicatedInputOwnerEntry(string[] args)
    {
        if (TryRunProxyNativeInputChild(args, out var childExit))
            return childExit;

        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            ProxyH8DedicatedInputOwnerChecks();
            Console.WriteLine($"PR260 H8 dedicated-owner checks: {assertions} assertions passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    private static void ProxyH8DedicatedInputOwnerChecks()
    {
        Check(NativeInputGetCursorPos(out var originalCursor),
            "H8 dedicated-owner checks require an interactive desktop");
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor),
            "H8 dedicated-owner checks require a monitor work area");

        var work = monitor.WorkArea;
        var outputBounds = new DeviceScreenRect(
            work.Left + 220,
            work.Top + 220,
            work.Left + 220 + Pr260H8SourceSize + Pr260H8Travel + 48,
            work.Top + 220 + Pr260H8SourceSize + 28);
        var initialBounds = new DeviceScreenRect(
            outputBounds.Left + 14,
            outputBounds.Top + 14,
            outputBounds.Left + 14 + Pr260H8SourceSize,
            outputBounds.Top + 14 + Pr260H8SourceSize);
        var sampleY = initialBounds.Top + Pr260H8SourceSize / 2;

        using var target = new NativeInputRemoteTarget(outputBounds, separateProcess: true);
        NativeInputUntil(() => target.Handle != IntPtr.Zero,
            "H8 dedicated-owner lower process publishes its HWND");
        NativeInputGetWindowThreadProcessId(target.Handle, out var targetProcessId);
        Check(targetProcessId != Environment.ProcessId,
            "H8 dedicated-owner lower input target runs in another process");
        Check(NativeInputGetWindowThreadProcessId(target.Handle, out _) != NativeInputGetCurrentThreadId(),
            "H8 dedicated-owner lower input target runs on another native thread");

        var baselinePoint = new DeviceScreenPoint(
            outputBounds.Left + outputBounds.Width / 2,
            outputBounds.Top + outputBounds.Height / 2);
        NativeInputMove(baselinePoint);
        NativeInputClick();
        NativeInputUntil(() => target.Snapshot.Click == 1 && target.Snapshot.Up == 1,
            "H8 dedicated-owner lower process receives baseline tagged gesture");
        Check(target.Snapshot == new NativeInputSnapshot(1, 1, 1, 1, 1),
            "H8 dedicated-owner baseline lower-process gesture is observed exactly once");
        Check(NativeInputSetWindowPos(target.Handle, new IntPtr(-2), 0, 0, 0, 0, 0x0013),
            "H8 dedicated-owner lower target moves to the ordinary non-topmost band");

        var root = new Border
        {
            Width = Pr260H8SourceSize,
            Height = Pr260H8SourceSize,
            Background = Brushes.Red,
            SnapsToDevicePixels = true
        };
        var sourceWindow = new Window
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Topmost = true,
            Left = -32000,
            Top = -32000,
            Width = Pr260H8SourceSize,
            Height = Pr260H8SourceSize,
            Content = root
        };

        EdgeCapsuleQueueProxyWindow? output = null;
        Pr260H8DedicatedInputOwner? inputOwner = null;
        IDCompositionDesktopDevice? device = null;
        IDCompositionTarget? compositionTarget = null;
        IDCompositionVisual2? compositionRoot = null;
        IDCompositionVisual2? visual = null;
        IDCompositionAnimation? animation = null;
        IDisposable? surface = null;
        CancellationTokenSource? observerStop = null;
        Task? observerTask = null;
        var observation = new Pr260H8OwnerObservation();
        var sourceCloaked = false;
        try
        {
            sourceWindow.Show();
            WindowNative.ApplyNoActivateStyle(sourceWindow);
            var sourceHandle = new WindowInteropHelper(sourceWindow).Handle;
            Check(sourceHandle != IntPtr.Zero, "H8 dedicated-owner WPF source HWND was created");
            Check(WindowNative.TrySetWindowDeviceBounds(sourceWindow, initialBounds),
                "H8 dedicated-owner WPF source can be positioned in device pixels");
            sourceWindow.UpdateLayout();
            root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Render, static () => { });
            Pr260H8FlushDesktop("dedicated-owner initial WPF source render");

            output = EdgeCapsuleQueueProxyWindow.TryCreate(
                outputBounds,
                topmost: true,
                _ => false,
                static _ => { },
                static () => { },
                static () => { },
                static () => { });
            Check(output != null, "H8 dedicated-owner production output HWND pair can be created");
            Check(output!.TrySetInputRegions(Array.Empty<DeviceScreenRect>()),
                "H8 dedicated-owner leaves the production UI-owned input HWND empty");
            Check(output.Show(outputBounds, topmost: true),
                "H8 dedicated-owner production output pair is visible");
            Check(NativeInputRegionEmpty(output.InputHandle),
                "H8 dedicated-owner production UI-owned input HWND remains empty");

            inputOwner = new Pr260H8DedicatedInputOwner(outputBounds, initialBounds);
            Check(inputOwner.Handle != IntPtr.Zero,
                "H8 dedicated-owner input-only HWND is created on its native owner thread");
            Check(inputOwner.OwnerThreadId != NativeInputGetCurrentThreadId(),
                "H8 dedicated-owner input HWND belongs to a thread distinct from the WPF/Dispatcher thread");
            Check(NativeInputRegionContains(inputOwner.Handle,
                    new DeviceScreenPoint(initialBounds.Left + 10, sampleY)),
                "H8 dedicated-owner initial region owns visible-card input");

            var iid = typeof(IDCompositionDesktopDevice).GUID;
            Marshal.ThrowExceptionForHR(NativeInputDCompositionCreateDevice2(IntPtr.Zero, ref iid, out var pointer));
            device = new IDCompositionDesktopDevice(pointer);
            device.CreateTargetForHwnd(output.Handle, topmost: true, out compositionTarget).CheckError();
            device.CreateVisual(out compositionRoot).CheckError();
            device.CreateSurfaceFromHwnd(sourceHandle, out var liveSurface).CheckError();
            surface = liveSurface;
            device.CreateVisual(out visual).CheckError();
            visual.SetContent(liveSurface).CheckError();
            visual.SetBitmapInterpolationMode(BitmapInterpolationMode.Linear).CheckError();
            visual.SetBorderMode(BorderMode.Soft).CheckError();
            visual.SetOffsetX(initialBounds.Left - outputBounds.Left).CheckError();
            visual.SetOffsetY(initialBounds.Top - outputBounds.Top).CheckError();
            compositionRoot.AddVisual(visual, insertAbove: true, referenceVisual: null!).CheckError();
            compositionTarget.SetRoot(compositionRoot).CheckError();
            device.Commit().CheckError();

            sourceCloaked = WindowNative.TrySetWindowCloakedBatchDetailed(
                new[]
                {
                    new WindowNative.WindowCloakChange(
                        sourceHandle,
                        Cloaked: true,
                        RollbackCloaked: false)
                }) == WindowNative.WindowCloakBatchResult.Success;
            Check(sourceCloaked, "H8 dedicated-owner real WPF source can be cloaked behind proxy output");
            Pr260H8FlushDesktop("dedicated-owner source cloak");

            var initialPixels = Pr260H8FindRedSpan(outputBounds, sampleY);
            Check(initialPixels.Count >= Pr260H8SourceSize / 2,
                $"H8 dedicated-owner observes initial redirected red source (count={initialPixels.Count})");
            Check(initialPixels.Right <= initialBounds.Right + 3,
                $"H8 dedicated-owner initial redirected source is within initial input region: {initialPixels}");

            animation = device.CreateAnimation();
            var from = (float)(initialBounds.Left - outputBounds.Left);
            var to = from + Pr260H8Travel;
            var startTimestamp = Stopwatch.GetTimestamp();
            animation.SetAbsoluteBeginTime(startTimestamp).CheckError();
            animation.AddCubic(
                0,
                from,
                (float)(3 * Pr260H8Travel / Pr260H8DurationSeconds),
                (float)(-3 * Pr260H8Travel / (Pr260H8DurationSeconds * Pr260H8DurationSeconds)),
                (float)(Pr260H8Travel / (Pr260H8DurationSeconds * Pr260H8DurationSeconds * Pr260H8DurationSeconds))).CheckError();
            animation.End(Pr260H8DurationSeconds, to).CheckError();
            visual.SetOffsetX(animation).CheckError();
            device.Commit().CheckError();
            inputOwner.StartAnimation(startTimestamp, Pr260H8Travel, Pr260H8DurationSeconds);

            observerStop = new CancellationTokenSource();
            var observerToken = observerStop.Token;
            observerTask = Task.Run(() =>
                Pr260H8ObserveDedicatedOwnerDuringStall(
                    inputOwner,
                    outputBounds,
                    initialBounds,
                    sampleY,
                    observation,
                    observerToken));

            // Deliberately block the WPF/Dispatcher thread exactly as the R3 reproduction does.
            // The input owner must continue pumping its own HWND and updating finite HRGNs.
            Thread.Sleep(Pr260H8StallMilliseconds);
            observerStop.Cancel();
            Check(observerTask.Wait(TimeSpan.FromSeconds(3)),
                "H8 dedicated-owner observer exits after the UI-thread stall");
            Pr260H8FlushDesktop("dedicated-owner stalled compositor sample");

            var stalledPixels = Pr260H8FindRedSpan(outputBounds, sampleY);
            Check(stalledPixels.Count >= Pr260H8SourceSize / 2,
                $"H8 dedicated-owner redirected source remains visible during UI stall (count={stalledPixels.Count})");
            Check(stalledPixels.Right > initialBounds.Right + 12,
                $"H8 dedicated-owner compositor advances while WPF UI is stalled: initialRight={initialBounds.Right} stalled={stalledPixels}");
            Check(inputOwner.RegionUpdateFailures == 0,
                "H8 dedicated-owner region mutations complete without native failures on their owner thread");
            Check(inputOwner.RegionUpdates >= 5,
                $"H8 dedicated-owner continues publishing regions during UI stall (updates={inputOwner.RegionUpdates})");
            Check(observation.Samples >= 5,
                $"H8 dedicated-owner observer captures composed desktop samples during UI stall (samples={observation.Samples})");
            Check(observation.VisibleOwnershipMisses == 0,
                $"H8 dedicated-owner has no >12px visible-leading ownership misses during sampled UI stall (misses={observation.VisibleOwnershipMisses})");
            Check(observation.StaleEmptyBlocks == 0,
                $"H8 dedicated-owner has no >12px stale trailing blocks during sampled UI stall (blocks={observation.StaleEmptyBlocks})");

            var livePoint = new DeviceScreenPoint(stalledPixels.Left + 12, sampleY);
            var vacatedPoint = new DeviceScreenPoint(stalledPixels.Left - 12, sampleY);
            Check(Pr260H8IsRedPixel(livePoint),
                "H8 dedicated-owner wake sample chooses a point visibly inside the moved card");
            Check(NativeInputRegionContains(inputOwner.Handle, livePoint),
                "H8 dedicated-owner wake sample owns the moved visible card despite WPF stall");
            if (vacatedPoint.X >= initialBounds.Left)
            {
                Check(!Pr260H8IsRedPixel(vacatedPoint),
                    "H8 dedicated-owner wake sample chooses a visually vacated trailing point");
                Check(!NativeInputRegionContains(inputOwner.Handle, vacatedPoint),
                    "H8 dedicated-owner wake sample releases the visually vacated trailing point");
            }

            // Freeze both authorities to the same already-observed composed position before
            // issuing OS gestures. This keeps the routing assertion deterministic while the
            // observer above is what tests independent progress during the UI stall.
            var frozenBounds = new DeviceScreenRect(
                stalledPixels.Left,
                initialBounds.Top,
                stalledPixels.Left + Pr260H8SourceSize,
                initialBounds.Bottom);
            visual.SetOffsetX(frozenBounds.Left - outputBounds.Left).CheckError();
            device.Commit().CheckError();
            inputOwner.SetStaticRegion(frozenBounds);
            NativeInputUntil(() => Math.Abs(inputOwner.LastAppliedLeft - frozenBounds.Left) <= 1,
                "H8 dedicated-owner freezes native input to the sampled composed position");
            Pr260H8FlushDesktop("dedicated-owner freeze matched visual/input authorities");
            var frozenPixels = Pr260H8FindRedSpan(outputBounds, sampleY);

            var ownerBefore = inputOwner.Snapshot;
            var targetBefore = target.Snapshot;
            var ownedPoint = new DeviceScreenPoint(frozenPixels.Left + 12, sampleY);
            Check(Pr260H8IsRedPixel(ownedPoint) && NativeInputRegionContains(inputOwner.Handle, ownedPoint),
                "H8 dedicated-owner visible gesture point is both visible and owned");
            NativeInputMove(ownedPoint);
            NativeInputClick();
            NativeInputUntil(() => inputOwner.Snapshot.Down == ownerBefore.Down + 1 &&
                                 inputOwner.Snapshot.Up == ownerBefore.Up + 1,
                "H8 dedicated-owner visible-card gesture reaches the independent input owner");
            NativeInputPumpFor(60);
            Check(target.Snapshot == targetBefore,
                "H8 dedicated-owner visible-card gesture does not leak to the lower process");

            var emptyPoint = new DeviceScreenPoint(initialBounds.Left + 6, sampleY);
            Check(!Pr260H8IsRedPixel(emptyPoint) && !NativeInputRegionContains(inputOwner.Handle, emptyPoint),
                "H8 dedicated-owner old location is visually empty and no longer owned");
            NativeInputMove(emptyPoint);
            NativeInputClick();
            NativeInputUntil(() => target.Snapshot.Click == targetBefore.Click + 1 &&
                                 target.Snapshot.Up == targetBefore.Up + 1,
                "H8 dedicated-owner vacated-region gesture reaches the independent lower process");
            NativeInputPumpFor(60);
            Check(inputOwner.Snapshot == new Pr260H8OwnerInputSnapshot(ownerBefore.Down + 1, ownerBefore.Up + 1),
                "H8 dedicated-owner vacated-region gesture is not intercepted by the input owner");

            Console.WriteLine(
                $"PASS pr260-h8-dedicated-input-owner-ui-stall samples={observation.Samples} " +
                $"visibleMisses={observation.VisibleOwnershipMisses} staleBlocks={observation.StaleEmptyBlocks} " +
                $"regionUpdates={inputOwner.RegionUpdates} failures={inputOwner.RegionUpdateFailures} " +
                $"initial={initialPixels} stalled={stalledPixels} frozen={frozenPixels} " +
                $"lastRegionLeft={inputOwner.LastAppliedLeft}");
        }
        finally
        {
            try { observerStop?.Cancel(); } catch { }
            try { observerTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
            try
            {
                if (sourceCloaked)
                {
                    var handle = new WindowInteropHelper(sourceWindow).Handle;
                    _ = WindowNative.TrySetWindowCloakedBatchDetailed(
                        new[]
                        {
                            new WindowNative.WindowCloakChange(
                                handle,
                                Cloaked: false,
                                RollbackCloaked: true)
                        });
                }
            }
            catch { }
            try
            {
                if (compositionTarget != null && device != null)
                {
                    compositionTarget.SetRoot(null!).CheckError();
                    device.Commit().CheckError();
                }
            }
            catch { }
            try { animation?.Dispose(); } catch { }
            try { visual?.Dispose(); } catch { }
            try { surface?.Dispose(); } catch { }
            try { compositionRoot?.Dispose(); } catch { }
            try { compositionTarget?.Dispose(); } catch { }
            try { device?.Dispose(); } catch { }
            try { inputOwner?.Dispose(); } catch { }
            try { output?.Hide(); } catch { }
            try { output?.Dispose(); } catch { }
            try { sourceWindow.Hide(); } catch { }
            try { sourceWindow.Close(); } catch { }
            try { NativeInputMove(new DeviceScreenPoint(originalCursor.X, originalCursor.Y)); } catch { }
        }
    }

    private static void Pr260H8ObserveDedicatedOwnerDuringStall(
        Pr260H8DedicatedInputOwner inputOwner,
        DeviceScreenRect outputBounds,
        DeviceScreenRect initialBounds,
        int sampleY,
        Pr260H8OwnerObservation observation,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var span = Pr260H8FindRedSpanWithoutAssertions(outputBounds, sampleY);
            if (span.Count >= Pr260H8SourceSize / 2 && span.Left > initialBounds.Left + 2)
            {
                var visiblePoint = new DeviceScreenPoint(span.Left + 12, sampleY);
                var trailingPoint = new DeviceScreenPoint(span.Left - 12, sampleY);
                var visibleOwned = NativeInputRegionContains(inputOwner.Handle, visiblePoint);
                var staleBlocked = trailingPoint.X >= initialBounds.Left &&
                                   NativeInputRegionContains(inputOwner.Handle, trailingPoint);
                observation.Record(visibleOwned, staleBlocked);
            }
            Thread.Sleep(4);
        }
    }

    private static Pr260H8RedSpan Pr260H8FindRedSpanWithoutAssertions(DeviceScreenRect bounds, int y)
    {
        var dc = Pr260H8GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
            return new Pr260H8RedSpan(0, 0, 0);
        try
        {
            var left = int.MaxValue;
            var right = int.MinValue;
            var count = 0;
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                var color = Pr260H8GetPixel(dc, x, y);
                if (!Pr260H8IsRed(color))
                    continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x + 1);
                count++;
            }
            return count == 0
                ? new Pr260H8RedSpan(0, 0, 0)
                : new Pr260H8RedSpan(left, right, count);
        }
        finally
        {
            _ = Pr260H8ReleaseDC(IntPtr.Zero, dc);
        }
    }

    private sealed class Pr260H8OwnerObservation
    {
        private int _samples;
        private int _visibleOwnershipMisses;
        private int _staleEmptyBlocks;

        internal int Samples => Volatile.Read(ref _samples);
        internal int VisibleOwnershipMisses => Volatile.Read(ref _visibleOwnershipMisses);
        internal int StaleEmptyBlocks => Volatile.Read(ref _staleEmptyBlocks);

        internal void Record(bool visibleOwned, bool staleBlocked)
        {
            Interlocked.Increment(ref _samples);
            if (!visibleOwned) Interlocked.Increment(ref _visibleOwnershipMisses);
            if (staleBlocked) Interlocked.Increment(ref _staleEmptyBlocks);
        }
    }

    private readonly record struct Pr260H8OwnerInputSnapshot(int Down, int Up);

    private sealed class Pr260H8DedicatedInputOwner : IDisposable
    {
        private const int GwlWndProc = -4;
        private const int WsPopup = unchecked((int)0x80000000);
        private const int WsExTopmost = 0x00000008;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoRedirectionBitmap = 0x00200000;
        private const int WsExNoActivate = 0x08000000;
        private const int WmPaint = 0x000F;
        private const int WmEraseBackground = 0x0014;
        private const int WmMouseActivate = 0x0021;
        private const int WmLButtonDown = 0x0201;
        private const int WmLButtonUp = 0x0202;
        private const int MaNoActivate = 3;
        private const uint PmRemove = 0x0001;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private static readonly IntPtr HwndTopmost = new(-1);

        private readonly DeviceScreenRect _windowBounds;
        private readonly int _width;
        private readonly int _height;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly object _gate = new();
        private readonly OwnerWndProc _windowProcedure;
        private DeviceScreenRect _staticRegion;
        private long _animationStart;
        private int _animationTravel;
        private double _animationDurationSeconds;
        private bool _animationActive;
        private volatile bool _stop;
        private IntPtr _handle;
        private IntPtr _previousWindowProcedure;
        private int _ownerThreadId;
        private int _lastAppliedLeft;
        private int _regionUpdates;
        private int _regionUpdateFailures;
        private int _down;
        private int _up;

        internal Pr260H8DedicatedInputOwner(DeviceScreenRect windowBounds, DeviceScreenRect initialRegion)
        {
            _windowBounds = windowBounds;
            _staticRegion = initialRegion;
            _width = initialRegion.Width;
            _height = initialRegion.Height;
            _lastAppliedLeft = initialRegion.Left;
            _windowProcedure = WindowMessage;
            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "PR260 H8 dedicated input owner"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(5)) || _handle == IntPtr.Zero)
                throw new InvalidOperationException("Dedicated input owner did not create its HWND");
        }

        internal IntPtr Handle => _handle;
        internal int OwnerThreadId => Volatile.Read(ref _ownerThreadId);
        internal int LastAppliedLeft => Volatile.Read(ref _lastAppliedLeft);
        internal int RegionUpdates => Volatile.Read(ref _regionUpdates);
        internal int RegionUpdateFailures => Volatile.Read(ref _regionUpdateFailures);
        internal Pr260H8OwnerInputSnapshot Snapshot =>
            new(Volatile.Read(ref _down), Volatile.Read(ref _up));

        internal void StartAnimation(long startTimestamp, int travel, double durationSeconds)
        {
            lock (_gate)
            {
                _animationStart = startTimestamp;
                _animationTravel = travel;
                _animationDurationSeconds = durationSeconds;
                _animationActive = true;
            }
        }

        internal void SetStaticRegion(DeviceScreenRect region)
        {
            lock (_gate)
            {
                _staticRegion = region;
                _animationActive = false;
            }
        }

        private void ThreadMain()
        {
            _ownerThreadId = unchecked((int)NativeInputGetCurrentThreadId());
            var exStyle = WsExToolWindow | WsExNoActivate | WsExNoRedirectionBitmap | WsExTopmost;
            var handle = OwnerCreateWindowEx(
                exStyle,
                "Static",
                string.Empty,
                WsPopup,
                _windowBounds.Left,
                _windowBounds.Top,
                _windowBounds.Width,
                _windowBounds.Height,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
            if (handle == IntPtr.Zero)
            {
                _ready.Set();
                return;
            }

            _handle = handle;
            _previousWindowProcedure = OwnerSetWindowLongPtr(
                handle,
                GwlWndProc,
                Marshal.GetFunctionPointerForDelegate(_windowProcedure));
            if (_previousWindowProcedure == IntPtr.Zero || !ApplyRegion(_staticRegion) ||
                !OwnerSetWindowPos(handle, HwndTopmost,
                    _windowBounds.Left, _windowBounds.Top, _windowBounds.Width, _windowBounds.Height,
                    SwpNoActivate | SwpShowWindow | SwpNoOwnerZOrder))
            {
                _regionUpdateFailures++;
                _ready.Set();
                _ = OwnerDestroyWindow(handle);
                _handle = IntPtr.Zero;
                return;
            }
            _ready.Set();

            try
            {
                while (!_stop)
                {
                    while (OwnerPeekMessage(out var message, IntPtr.Zero, 0, 0, PmRemove))
                    {
                        _ = OwnerTranslateMessage(ref message);
                        _ = OwnerDispatchMessage(ref message);
                    }

                    DeviceScreenRect region;
                    lock (_gate)
                    {
                        region = _staticRegion;
                        if (_animationActive)
                        {
                            var elapsed = Math.Max(0,
                                (Stopwatch.GetTimestamp() - _animationStart) / (double)Stopwatch.Frequency);
                            var u = Math.Clamp(elapsed / _animationDurationSeconds, 0, 1);
                            var eased = 1 - Math.Pow(1 - u, 3);
                            var left = _staticRegion.Left + (int)Math.Round(_animationTravel * eased);
                            region = new DeviceScreenRect(
                                left,
                                _staticRegion.Top,
                                left + _width,
                                _staticRegion.Top + _height);
                            if (u >= 1)
                            {
                                _staticRegion = region;
                                _animationActive = false;
                            }
                        }
                    }

                    if (region.Left != Volatile.Read(ref _lastAppliedLeft) && !ApplyRegion(region))
                        Interlocked.Increment(ref _regionUpdateFailures);
                    Thread.Sleep(1);
                }
            }
            finally
            {
                if (_previousWindowProcedure != IntPtr.Zero)
                    _ = OwnerSetWindowLongPtr(handle, GwlWndProc, _previousWindowProcedure);
                _ = OwnerDestroyWindow(handle);
                _handle = IntPtr.Zero;
            }
        }

        private bool ApplyRegion(DeviceScreenRect screenRegion)
        {
            var left = Math.Max(screenRegion.Left, _windowBounds.Left) - _windowBounds.Left;
            var top = Math.Max(screenRegion.Top, _windowBounds.Top) - _windowBounds.Top;
            var right = Math.Min(screenRegion.Right, _windowBounds.Right) - _windowBounds.Left;
            var bottom = Math.Min(screenRegion.Bottom, _windowBounds.Bottom) - _windowBounds.Top;
            var region = OwnerCreateRectRgn(left, top, Math.Max(left, right), Math.Max(top, bottom));
            if (region == IntPtr.Zero)
                return false;
            if (OwnerSetWindowRgn(_handle, region, redraw: false) == 0)
            {
                _ = OwnerDeleteObject(region);
                return false;
            }
            // On success SetWindowRgn owns the handle.
            Volatile.Write(ref _lastAppliedLeft, screenRegion.Left);
            Interlocked.Increment(ref _regionUpdates);
            return true;
        }

        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
        {
            switch (message)
            {
                case WmPaint:
                    var dc = OwnerBeginPaint(hwnd, out var paint);
                    if (dc != IntPtr.Zero) _ = OwnerEndPaint(hwnd, ref paint);
                    return IntPtr.Zero;
                case WmEraseBackground:
                    return new IntPtr(1);
                case WmMouseActivate:
                    return new IntPtr(MaNoActivate);
                case WmLButtonDown:
                    Interlocked.Increment(ref _down);
                    return IntPtr.Zero;
                case WmLButtonUp:
                    Interlocked.Increment(ref _up);
                    return IntPtr.Zero;
                default:
                    return OwnerCallWindowProc(_previousWindowProcedure, hwnd, message, wParam, lParam);
            }
        }

        public void Dispose()
        {
            _stop = true;
            if (_thread.IsAlive)
                _thread.Join(TimeSpan.FromSeconds(3));
            _ready.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct OwnerMessage
        {
            public IntPtr Hwnd;
            public uint Message;
            public UIntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int PointX;
            public int PointY;
            public uint Private;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct OwnerPaintState
        {
            public IntPtr DeviceContext;
            public int Erase;
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
            public int Restore;
            public int IncrementalUpdate;
            private uint _reserved0, _reserved1, _reserved2, _reserved3;
            private uint _reserved4, _reserved5, _reserved6, _reserved7;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr OwnerWndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", SetLastError = true)]
        private static extern IntPtr OwnerCreateWindowEx(
            int exStyle,
            string className,
            string windowName,
            int style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr parameter);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr OwnerSetWindowLongPtr(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static extern IntPtr OwnerCallWindowProc(
            IntPtr previous,
            IntPtr window,
            int message,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OwnerSetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll", EntryPoint = "SetWindowRgn", SetLastError = true)]
        private static extern int OwnerSetWindowRgn(IntPtr window, IntPtr region, bool redraw);

        [DllImport("gdi32.dll", EntryPoint = "CreateRectRgn")]
        private static extern IntPtr OwnerCreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OwnerDeleteObject(IntPtr value);

        [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OwnerPeekMessage(
            out OwnerMessage message,
            IntPtr window,
            uint min,
            uint max,
            uint remove);

        [DllImport("user32.dll", EntryPoint = "TranslateMessage")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OwnerTranslateMessage(ref OwnerMessage message);

        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
        private static extern IntPtr OwnerDispatchMessage(ref OwnerMessage message);

        [DllImport("user32.dll", EntryPoint = "DestroyWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OwnerDestroyWindow(IntPtr window);

        [DllImport("user32.dll", EntryPoint = "BeginPaint")]
        private static extern IntPtr OwnerBeginPaint(IntPtr window, out OwnerPaintState paint);

        [DllImport("user32.dll", EntryPoint = "EndPaint")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OwnerEndPaint(IntPtr window, ref OwnerPaintState paint);
    }
}

internal static class Pr260H8DedicatedInputOwnerEntry
{
    [STAThread]
    private static int Main(string[] args) => Program.RunPr260H8DedicatedInputOwnerEntry(args);
}
