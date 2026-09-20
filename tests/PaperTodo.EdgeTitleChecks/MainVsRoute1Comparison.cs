using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;
using SharpGen.Runtime;
using Vortice.DirectComposition;

internal static class MainVsRoute1ComparisonEntry
{
    private const string ChildArgument = "--routes12-ab-child";
    private static readonly UIntPtr InputTag = new(0x5054444142554CUL);
    private const int SourceSize = 48;
    private const int Travel = 120;
    private const int DurationMilliseconds = 900;
    private const int StallMilliseconds = 430;
    private const int ProbeDelayMilliseconds = 220;

    [STAThread]
    public static int Main(string[] args)
    {
        if (TryRunChild(args, out var childExit))
            return childExit;

        var mode = ReadMode(args);
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            Run(mode);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    private static string ReadMode(string[] args)
    {
        var index = Array.IndexOf(args, "--mode");
        if (index < 0 || index + 1 >= args.Length)
            throw new ArgumentException("Expected --mode main|route1");
        var mode = args[index + 1].ToLowerInvariant();
        if (mode is not ("main" or "route1"))
            throw new ArgumentException("Expected --mode main|route1");
        return mode;
    }

    private static void Run(string mode)
    {
        Check(GetCursorPos(out var originalCursor), "interactive desktop is available");
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), "monitor work area is available");

        var work = monitor.WorkArea;
        var outputBounds = new DeviceScreenRect(
            work.Left + 220,
            work.Top + 200,
            work.Left + 220 + SourceSize + Travel + 60,
            work.Top + 200 + SourceSize + 32);
        var initial = new DeviceScreenRect(
            outputBounds.Left + 16,
            outputBounds.Top + 12,
            outputBounds.Left + 16 + SourceSize,
            outputBounds.Top + 12 + SourceSize);
        var target = new DeviceScreenRect(
            initial.Left + Travel,
            initial.Top,
            initial.Right + Travel,
            initial.Bottom);
        var sampleY = initial.Top + SourceSize / 2;

        using var lower = new RemoteTarget(outputBounds);
        WaitUntil(() => lower.Handle != IntPtr.Zero, "lower target ready");
        var baselinePoint = new DeviceScreenPoint(
            outputBounds.Left + outputBounds.Width / 2,
            outputBounds.Top + outputBounds.Height / 2);
        MoveMouse(baselinePoint);
        ClickMouse();
        WaitUntil(() => lower.Snapshot.Down == 1 && lower.Snapshot.Up == 1, "lower baseline click");
        Check(SetWindowPos(lower.Handle, new IntPtr(-2), 0, 0, 0, 0, 0x0013), "lower target leaves topmost band");

        var leading = RunCase(mode, "leading", lower, outputBounds, initial, target, sampleY);
        PumpFor(120);
        var stale = RunCase(mode, "stale", lower, outputBounds, initial, target, sampleY);

        var sourceSha = Environment.GetEnvironmentVariable("PAPERTODO_AB_SOURCE_SHA") ?? "unknown";
        Console.WriteLine(
            "AB_SUMMARY mode=" + mode +
            " sourceSha=" + sourceSha +
            " leadingOutcome=" + leading.Outcome +
            " leadingDeliveryMs=" + FormatNullable(leading.DeliveryMilliseconds) +
            " leadingPixelTravel=" + leading.PixelTravel +
            " leadingInputSkewPx=" + leading.InputSkewPixels +
            " staleOutcome=" + stale.Outcome +
            " staleDeliveryMs=" + FormatNullable(stale.DeliveryMilliseconds) +
            " stalePixelTravel=" + stale.PixelTravel +
            " staleInputSkewPx=" + stale.InputSkewPixels);

        MoveMouse(new DeviceScreenPoint(originalCursor.X, originalCursor.Y));
    }

    private static CaseResult RunCase(
        string mode,
        string kind,
        RemoteTarget lower,
        DeviceScreenRect outputBounds,
        DeviceScreenRect initial,
        DeviceScreenRect target,
        int sampleY)
    {
        var logicalInput = initial;
        var proxyPresses = 0;
        long proxyPressTimestamp = 0;
        var source = new Window
        {
            ShowInTaskbar = false,
            ShowActivated = false,
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
            Width = SourceSize,
            Height = SourceSize,
            Content = new Border { Background = Brushes.Red, SnapsToDevicePixels = true }
        };

        EdgeCapsuleQueueProxyWindow? proxy = null;
        IDCompositionDesktopDevice? device = null;
        IDCompositionTarget? compositionTarget = null;
        IDCompositionVisual2? visual = null;
        IDCompositionAnimation? animation = null;
        IDisposable? surface = null;
        var sourceCloaked = false;

        try
        {
            source.Show();
            WindowNative.ApplyNoActivateStyle(source);
            var sourceHandle = new WindowInteropHelper(source).Handle;
            Check(sourceHandle != IntPtr.Zero, "source HWND created");
            Check(WindowNative.TrySetWindowDeviceBounds(source, initial), "source positioned");
            source.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Render, static () => { });
            FlushDesktop();

            proxy = EdgeCapsuleQueueProxyWindow.TryCreate(
                outputBounds,
                true,
                p => Contains(logicalInput, p),
                _ =>
                {
                    Interlocked.Increment(ref proxyPresses);
                    Interlocked.Exchange(ref proxyPressTimestamp, Stopwatch.GetTimestamp());
                },
                static () => { },
                static () => { },
                static () => { });
            Check(proxy != null, "proxy created");

            if (mode == "route1")
                Check(Route1SetRegions(proxy!, initial), "route1 initial HRGN published");
            Check(proxy!.Show(outputBounds, true), "proxy shown");

            var iid = typeof(IDCompositionDesktopDevice).GUID;
            Marshal.ThrowExceptionForHR(DCompositionCreateDevice2(IntPtr.Zero, ref iid, out var pointer));
            device = new IDCompositionDesktopDevice(pointer);
            device.CreateTargetForHwnd(proxy.Handle, true, out compositionTarget).CheckError();
            device.CreateVisual(out visual).CheckError();
            device.CreateSurfaceFromHwnd(sourceHandle, out var live).CheckError();
            surface = live;
            visual.SetContent(live).CheckError();
            visual.SetBitmapInterpolationMode(BitmapInterpolationMode.Linear).CheckError();
            visual.SetBorderMode(BorderMode.Soft).CheckError();
            visual.SetOffsetX(initial.Left - outputBounds.Left).CheckError();
            visual.SetOffsetY(initial.Top - outputBounds.Top).CheckError();
            compositionTarget.SetRoot(visual).CheckError();
            device.Commit().CheckError();
            FlushDesktop();

            sourceCloaked = WindowNative.TrySetWindowCloakedBatchDetailed(
                [new WindowNative.WindowCloakChange(sourceHandle, true, false)]) ==
                WindowNative.WindowCloakBatchResult.Success;
            Check(sourceCloaked, "source cloaked after cover publication");
            FlushDesktop();

            var initialPixels = FindRedSpan(outputBounds, sampleY);
            Check(initialPixels.Count >= SourceSize / 2, "initial DComp pixels visible");

            var startedAt = Stopwatch.GetTimestamp();
            var seconds = DurationMilliseconds / 1000.0;
            animation = device.CreateAnimation();
            var from = (float)(initial.Left - outputBounds.Left);
            animation.SetAbsoluteBeginTime(startedAt).CheckError();
            animation.AddCubic(
                0,
                from,
                (float)(3 * Travel / seconds),
                (float)(-3 * Travel / (seconds * seconds)),
                (float)(Travel / (seconds * seconds * seconds))).CheckError();
            animation.End(seconds, from + Travel).CheckError();
            visual.SetOffsetX(animation).CheckError();
            device.Commit().CheckError();

            if (mode == "route1")
                Check(Route1StartTicket(proxy, startedAt, initial, target), "route1 ticket started after DComp commit");

            var before = lower.Snapshot;
            var regionUpdatesBefore = mode == "route1" ? Route1RegionUpdateCount(proxy) : 0;
            var stallStarted = Stopwatch.GetTimestamp();
            var probeTask = Task.Run(() =>
            {
                Thread.Sleep(ProbeDelayMilliseconds);
                FlushDesktop();
                var pixels = FindRedSpan(outputBounds, sampleY);
                Check(pixels.Count >= SourceSize / 2, "probe sees moving DComp pixels");
                var point = kind == "leading"
                    ? new DeviceScreenPoint(Math.Max(initial.Right + 4, pixels.Right - 8), sampleY)
                    : new DeviceScreenPoint(initial.Left + 6, sampleY);
                var pixelRed = IsRedPixel(point);
                var inputSpan = mode == "route1"
                    ? CaptureRoute1InputSpan(proxy, outputBounds, sampleY)
                    : new PixelSpan(initial.Left, initial.Right, initial.Width);
                var injectAt = Stopwatch.GetTimestamp();
                MoveMouse(point);
                ClickMouse();
                return new ProbeSnapshot(
                    pixels,
                    inputSpan,
                    point,
                    pixelRed,
                    injectAt,
                    ElapsedMilliseconds(stallStarted, injectAt));
            });

            Thread.Sleep(StallMilliseconds);
            var stallEnded = Stopwatch.GetTimestamp();
            PumpFor(280);
            var probe = probeTask.GetAwaiter().GetResult();
            PumpFor(120);

            var after = lower.Snapshot;
            var lowerDeltaDown = after.Down - before.Down;
            var lowerDeltaUp = after.Up - before.Up;
            var pressCount = Volatile.Read(ref proxyPresses);
            var pressTimestamp = Interlocked.Read(ref proxyPressTimestamp);
            var lowerTimestamp = lower.LastInputTimestamp;
            var regionUpdatesAfter = mode == "route1" ? Route1RegionUpdateCount(proxy) : 0;

            string outcome;
            long deliveryTimestamp;
            if (lowerDeltaDown > 0)
            {
                outcome = "lower";
                deliveryTimestamp = lowerTimestamp;
            }
            else if (pressCount > 0)
            {
                outcome = "proxy";
                deliveryTimestamp = pressTimestamp;
            }
            else
            {
                outcome = "absorbed";
                deliveryTimestamp = 0;
            }

            var deliveryMs = deliveryTimestamp > 0
                ? ElapsedMilliseconds(probe.InjectTimestamp, deliveryTimestamp)
                : (double?)null;
            var pixelTravel = probe.Pixels.Left - initial.Left;
            var inputSkew = probe.Input.IsEmpty ? int.MinValue : probe.Input.Left - probe.Pixels.Left;
            var stallActual = ElapsedMilliseconds(stallStarted, stallEnded);

            if (kind == "leading")
            {
                Check(probe.PixelIsRed, "leading probe point is visibly red");
                if (mode == "main")
                    Check(outcome == "lower", "main leading visible pixel leaks through stale UI-owned hit truth");
                else
                    Check(outcome == "absorbed", "route1 leading visible pixel is not leaked to lower app while UI is stalled");
            }
            else
            {
                Check(!probe.PixelIsRed, "stale probe point is visually empty");
                if (mode == "main")
                    Check(outcome == "proxy", "main stale old position still intercepts input");
                else
                    Check(outcome == "lower", "route1 stale old position passes through to lower app");
            }

            Console.WriteLine(
                "AB_CASE mode=" + mode +
                " case=" + kind +
                " stallMs=" + stallActual.ToString("F1", CultureInfo.InvariantCulture) +
                " probeAtMs=" + probe.ProbeElapsedMilliseconds.ToString("F1", CultureInfo.InvariantCulture) +
                " pixels=" + probe.Pixels +
                " input=" + probe.Input +
                " point=" + probe.Point.X.ToString("F0", CultureInfo.InvariantCulture) + "," + probe.Point.Y.ToString("F0", CultureInfo.InvariantCulture) +
                " pixelRed=" + probe.PixelIsRed +
                " pixelTravel=" + pixelTravel +
                " inputSkewPx=" + inputSkew +
                " lowerDownDelta=" + lowerDeltaDown +
                " lowerUpDelta=" + lowerDeltaUp +
                " proxyPresses=" + pressCount +
                " deliveryMs=" + FormatNullable(deliveryMs) +
                " regionUpdates=" + regionUpdatesBefore + "->" + regionUpdatesAfter +
                " outcome=" + outcome);

            logicalInput = target;
            return new CaseResult(outcome, deliveryMs, pixelTravel, inputSkew);
        }
        finally
        {
            try
            {
                if (sourceCloaked)
                {
                    var handle = new WindowInteropHelper(source).Handle;
                    _ = WindowNative.TrySetWindowCloakedBatchDetailed(
                        [new WindowNative.WindowCloakChange(handle, false, true)]);
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
            try { proxy?.Hide(); } catch { }
            try { animation?.Dispose(); } catch { }
            try { visual?.Dispose(); } catch { }
            try { surface?.Dispose(); } catch { }
            try { compositionTarget?.Dispose(); } catch { }
            try { device?.Dispose(); } catch { }
            try { proxy?.Dispose(); } catch { }
            try { source.Hide(); } catch { }
            try { source.Close(); } catch { }
        }
    }

    private static bool Route1SetRegions(EdgeCapsuleQueueProxyWindow proxy, DeviceScreenRect rect)
    {
        var method = proxy.GetType().GetMethod("TrySetInputRegions", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null) return false;
        return method.Invoke(proxy, [new[] { rect }]) is true;
    }

    private static bool Route1StartTicket(
        EdgeCapsuleQueueProxyWindow proxy,
        long startedAt,
        DeviceScreenRect initial,
        DeviceScreenRect target)
    {
        var assembly = typeof(EdgeCapsuleQueueProxyWindow).Assembly;
        var ticketType = assembly.GetType("PaperTodo.EdgeCapsuleQueueInputAnimationTicket");
        if (ticketType == null) return false;
        var ctor = ticketType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(c => c.GetParameters().Length == 3);
        var method = proxy.GetType().GetMethod("TryStartInputAnimation", BindingFlags.Instance | BindingFlags.NonPublic);
        if (ctor == null || method == null) return false;

        var start = Frame(initial);
        var targetFrame = Frame(target);
        var member = new EdgeCapsuleQueueProxyMemberPlan("ab", start, start, targetFrame);
        var ticket = ctor.Invoke([startedAt, DurationMilliseconds, new[] { member }]);
        return method.Invoke(proxy, [ticket]) is true;
    }

    private static int Route1RegionUpdateCount(EdgeCapsuleQueueProxyWindow proxy)
    {
        var property = proxy.GetType().GetProperty("InputRegionUpdateCount", BindingFlags.Instance | BindingFlags.NonPublic);
        return property?.GetValue(proxy) is int value ? value : -1;
    }

    private static IntPtr Route1InputHandle(EdgeCapsuleQueueProxyWindow proxy)
    {
        var property = proxy.GetType().GetProperty("InputHandle", BindingFlags.Instance | BindingFlags.NonPublic);
        return property?.GetValue(proxy) is IntPtr value ? value : IntPtr.Zero;
    }

    private static PixelSpan CaptureRoute1InputSpan(
        EdgeCapsuleQueueProxyWindow proxy,
        DeviceScreenRect bounds,
        int y)
    {
        var handle = Route1InputHandle(proxy);
        if (handle == IntPtr.Zero) return default;
        var region = CreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero) return default;
        try
        {
            if (GetWindowRgn(handle, region) <= 1 || !GetWindowRect(handle, out var windowRect))
                return default;
            var left = int.MaxValue;
            var right = int.MinValue;
            var count = 0;
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                if (!PtInRegion(region, x - windowRect.Left, y - windowRect.Top))
                    continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x + 1);
                count++;
            }
            return count == 0 ? default : new PixelSpan(left, right, count);
        }
        finally
        {
            _ = DeleteObject(region);
        }
    }

    private static EdgeCapsulePresentationFrame Frame(DeviceScreenRect bounds) => new(
        true,
        EdgeCapsuleSurfaceKind.DockedResting,
        bounds,
        bounds,
        bounds,
        EdgeCapsuleEdge.Left,
        bounds.Width,
        bounds.Left,
        1,
        1,
        0,
        1,
        1,
        true,
        true,
        true,
        true);

    private static PixelSpan FindRedSpan(DeviceScreenRect bounds, int y)
    {
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var left = int.MaxValue;
            var right = int.MinValue;
            var count = 0;
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                if (!IsRed(GetPixel(dc, x, y))) continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x + 1);
                count++;
            }
            return count == 0 ? default : new PixelSpan(left, right, count);
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, dc);
        }
    }

    private static bool IsRedPixel(DeviceScreenPoint point)
    {
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return false;
        try { return IsRed(GetPixel(dc, (int)Math.Round(point.X), (int)Math.Round(point.Y))); }
        finally { _ = ReleaseDC(IntPtr.Zero, dc); }
    }

    private static bool IsRed(uint color)
    {
        if (color == 0xFFFFFFFF) return false;
        var r = (byte)(color & 0xFF);
        var g = (byte)((color >> 8) & 0xFF);
        var b = (byte)((color >> 16) & 0xFF);
        return r >= 180 && g <= 90 && b <= 90;
    }

    private static bool Contains(DeviceScreenRect rect, DeviceScreenPoint point) =>
        point.X >= rect.Left && point.X < rect.Right &&
        point.Y >= rect.Top && point.Y < rect.Bottom;

    private static void FlushDesktop() => Marshal.ThrowExceptionForHR(DwmFlush());

    private static double ElapsedMilliseconds(long start, long end) =>
        Math.Max(0, end - start) * 1000.0 / Stopwatch.Frequency;

    private static string FormatNullable(double? value) =>
        value.HasValue ? value.Value.ToString("F1", CultureInfo.InvariantCulture) : "none";

    private static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    private static void WaitUntil(Func<bool> predicate, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate() && watch.ElapsedMilliseconds < 3000)
        {
            PumpFor(10);
        }
        Check(predicate(), message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("AB check failed: " + message);
    }

    private sealed class RemoteTarget : IDisposable
    {
        private readonly ConcurrentQueue<(string Line, long Timestamp)> _messages = new();
        private readonly Process _process;
        private IntPtr _handle;
        private InputSnapshot _snapshot;
        private long _lastInputTimestamp;

        internal RemoteTarget(DeviceScreenRect bounds)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(MainVsRoute1ComparisonEntry).Assembly.Location);
            start.ArgumentList.Add(ChildArgument);
            foreach (var value in new[] { bounds.Left, bounds.Top, bounds.Width, bounds.Height })
                start.ArgumentList.Add(value.ToString(CultureInfo.InvariantCulture));

            _process = new Process { StartInfo = start };
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) _messages.Enqueue((e.Data, Stopwatch.GetTimestamp()));
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) _messages.Enqueue(("ERROR " + e.Data, Stopwatch.GetTimestamp()));
            };
            if (!_process.Start()) throw new InvalidOperationException("lower target failed to start");
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        internal IntPtr Handle { get { ReadMessages(); return _handle; } }
        internal InputSnapshot Snapshot { get { ReadMessages(); return _snapshot; } }
        internal long LastInputTimestamp { get { ReadMessages(); return _lastInputTimestamp; } }

        private void ReadMessages()
        {
            while (_messages.TryDequeue(out var item))
            {
                if (item.Line.StartsWith("ERROR ", StringComparison.Ordinal))
                    throw new InvalidOperationException(item.Line);
                var parts = item.Line.Split(' ');
                if (parts[0] == "READY")
                    _handle = new IntPtr(long.Parse(parts[1], CultureInfo.InvariantCulture));
                else if (parts[0] == "INPUT")
                {
                    _snapshot = new InputSnapshot(
                        int.Parse(parts[1], CultureInfo.InvariantCulture),
                        int.Parse(parts[2], CultureInfo.InvariantCulture),
                        int.Parse(parts[3], CultureInfo.InvariantCulture),
                        int.Parse(parts[4], CultureInfo.InvariantCulture));
                    _lastInputTimestamp = item.Timestamp;
                }
            }
            if (_process.HasExited)
                throw new InvalidOperationException("lower target exited early: " + _process.ExitCode);
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.WriteLine("close");
                _process.StandardInput.Flush();
                if (!_process.WaitForExit(2000))
                {
                    _process.Kill();
                    _process.WaitForExit(2000);
                }
            }
            _process.Dispose();
        }
    }

    private static bool TryRunChild(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || args[0] != ChildArgument)
            return false;
        try
        {
            if (args.Length != 5) throw new ArgumentException("child expects x y width height");
            var values = args.Skip(1)
                .Select(x => int.Parse(x, CultureInfo.InvariantCulture))
                .ToArray();
            var bounds = new DeviceScreenRect(
                values[0],
                values[1],
                values[0] + values[2],
                values[1] + values[3]);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var counts = new ChildCounts();
            var window = CreateChildWindow(bounds, counts);
            _ = Task.Run(() =>
            {
                _ = Console.ReadLine();
                window.Dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)window.Close);
            });
            app.Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            exitCode = 1;
        }
        return true;
    }

    private static Window CreateChildWindow(DeviceScreenRect bounds, ChildCounts counts)
    {
        var window = new Window
        {
            Title = "PaperTodo AB lower target",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Width = bounds.Width,
            Height = bounds.Height,
            Background = Brushes.White
        };
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var source = HwndSource.FromHwnd(handle)!;
        source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            var tagged = GetMessageExtraInfo() == (IntPtr)(long)InputTag.ToUInt64();
            if (message == 0x0201)
            {
                counts.Down++;
                if (tagged) counts.TaggedDown++;
                ReportChild(counts);
            }
            else if (message == 0x0202)
            {
                counts.Up++;
                if (tagged) counts.TaggedUp++;
                ReportChild(counts);
            }
            return IntPtr.Zero;
        });
        window.Closed += (_, _) => window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        window.Show();
        Check(SetWindowPos(handle, new IntPtr(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0010 | 0x0040),
            "child window positioned");
        window.UpdateLayout();
        Console.WriteLine("READY " + handle.ToInt64().ToString(CultureInfo.InvariantCulture));
        Console.Out.Flush();
        return window;
    }

    private static void ReportChild(ChildCounts counts)
    {
        Console.WriteLine(
            "INPUT " + counts.Down + " " + counts.Up + " " + counts.TaggedDown + " " + counts.TaggedUp);
        Console.Out.Flush();
    }

    private static void ClickMouse()
    {
        SendMouse(0, 0, 0x0002);
        SendMouse(0, 0, 0x0004);
    }

    private static void MoveMouse(DeviceScreenPoint point)
    {
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78);
        var height = GetSystemMetrics(79);
        if (width <= 1 || height <= 1 ||
            point.X < left || point.Y < top ||
            point.X >= left + width || point.Y >= top + height)
            throw new InvalidOperationException("mouse target outside virtual desktop");

        SendMouse(
            (int)Math.Round((point.X - left) * 65535d / (width - 1)),
            (int)Math.Round((point.Y - top) * 65535d / (height - 1)),
            0x0001 | 0x8000 | 0x4000 | 0x2000);
    }

    private static void SendMouse(int x, int y, uint flags)
    {
        var input = new InputPacket
        {
            Type = 0,
            Mouse = new MouseInput
            {
                X = x,
                Y = y,
                Flags = flags,
                ExtraInfo = InputTag
            }
        };
        if (SendInput(1, [input], Marshal.SizeOf<InputPacket>()) != 1)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private readonly record struct PixelSpan(int Left, int Right, int Count)
    {
        internal bool IsEmpty => Count == 0;
        public override string ToString() => "[" + Left + "," + Right + ")#" + Count;
    }

    private readonly record struct ProbeSnapshot(
        PixelSpan Pixels,
        PixelSpan Input,
        DeviceScreenPoint Point,
        bool PixelIsRed,
        long InjectTimestamp,
        double ProbeElapsedMilliseconds);

    private readonly record struct CaseResult(
        string Outcome,
        double? DeliveryMilliseconds,
        int PixelTravel,
        int InputSkewPixels);

    private readonly record struct InputSnapshot(int Down, int Up, int TaggedDown, int TaggedUp);

    private sealed class ChildCounts
    {
        internal int Down;
        internal int Up;
        internal int TaggedDown;
        internal int TaggedUp;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputPacket { internal uint Type; internal MouseInput Mouse; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint Data;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendInput(uint count, InputPacket[] inputs, int size);

    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", EntryPoint = "GetMessageExtraInfo")]
    private static extern IntPtr GetMessageExtraInfo();

    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll", EntryPoint = "DwmFlush")]
    private static extern int DwmFlush();

    [DllImport("dcomp.dll", EntryPoint = "DCompositionCreateDevice2", ExactSpelling = true)]
    private static extern int DCompositionCreateDevice2(IntPtr renderingDevice, ref Guid iid, out IntPtr device);

    [DllImport("user32.dll", EntryPoint = "GetDC", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll", EntryPoint = "GetPixel")]
    private static extern uint GetPixel(IntPtr dc, int x, int y);

    [DllImport("gdi32.dll", EntryPoint = "CreateRectRgn", SetLastError = true)]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("user32.dll", EntryPoint = "GetWindowRgn")]
    private static extern int GetWindowRgn(IntPtr handle, IntPtr region);

    [DllImport("gdi32.dll", EntryPoint = "PtInRegion")]
    private static extern bool PtInRegion(IntPtr region, int x, int y);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    private static extern bool DeleteObject(IntPtr item);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
}
