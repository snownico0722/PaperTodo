using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PaperTodo;
using SharpGen.Runtime;
using Vortice.DirectComposition;

internal static class Routes12R1Entry
{
    [STAThread]
    private static int Main(string[] args) => Program.RunRoutes12R1(args);
}

internal static partial class Program
{
    private readonly record struct Routes12Span(int Left, int Right, int Count)
    {
        internal bool IsEmpty => Count <= 0;
        public override string ToString() => $"[{Left},{Right}) count={Count}";
    }

    private readonly record struct Routes12Observation(
        long Timestamp,
        Routes12Span Pixels,
        Routes12Span Input,
        bool PixelFallback);

    private readonly record struct Routes12DynamicSummary(
        int Samples,
        int Fallbacks,
        int PixelTravel,
        int InputTravel,
        double MedianAbsSkew,
        double P95AbsSkew,
        int MaxAbsSkew,
        int VisualAheadSamples,
        int InputAheadSamples,
        double MedianIntervalMs,
        double P95IntervalMs)
    {
        public override string ToString() =>
            $"samples={Samples} fallbacks={Fallbacks} pixelTravel={PixelTravel}px inputTravel={InputTravel}px " +
            $"absSkewPx[p50={MedianAbsSkew:F1},p95={P95AbsSkew:F1},max={MaxAbsSkew}] " +
            $"visualAhead={VisualAheadSamples} inputAhead={InputAheadSamples} " +
            $"sampleIntervalMs[p50={MedianIntervalMs:F2},p95={P95IntervalMs:F2}]";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Routes12BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ClrUsed;
        internal uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Routes12BitmapInfo
    {
        internal Routes12BitmapInfoHeader Header;
        internal uint Colors;
    }

    internal static int RunRoutes12R1(string[] args)
    {
        if (TryRunProxyNativeInputChild(args, out var childExit)) return childExit;
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            Routes12PolicyChecks();
            ProxyH8InputTimingChecks();
            Routes12Case(dedicated: true);
            Routes12Case(dedicated: false);
            Routes12DynamicCase(dedicated: true);
            Routes12DynamicCase(dedicated: false);
            Console.WriteLine($"ROUTES12 R1: {assertions} assertions passed. Dynamic physical-pixel/HRGN skew measured; production integration and physical high-refresh acceptance remain open.");
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

    private static void Routes12DynamicCase(bool dedicated)
    {
        var route = dedicated ? "route1-native-owner-dynamic" : "route2-app-driven-dynamic";
        Check(NativeInputGetCursorPos(out var cursor), route + ": interactive desktop available");
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), route + ": monitor available");
        var bounds = new DeviceScreenRect(
            monitor.WorkArea.Left + 180,
            monitor.WorkArea.Top + 180,
            monitor.WorkArea.Left + 420,
            monitor.WorkArea.Top + 270);
        var initial = new DeviceScreenRect(bounds.Left + 12, bounds.Top + 12, bounds.Left + 60, bounds.Top + 60);
        var sampleY = initial.Top + 24;
        var source = new Window
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            Left = -32000,
            Top = -32000,
            Width = 48,
            Height = 48,
            Topmost = true,
            Content = new Border { Background = Brushes.Red }
        };
        Routes12NativeOwner? owner = null;
        EdgeCapsuleQueueProxyWindow? uiPair = null;
        IDCompositionDesktopDevice? device = null;
        IDCompositionTarget? target = null;
        IDCompositionVisual2? visual = null;
        IDCompositionAnimation? animation = null;
        IDisposable? surface = null;
        var sourceCloaked = false;
        var applied = new[] { initial };
        var uiPresses = 0;
        try
        {
            source.Show();
            WindowNative.ApplyNoActivateStyle(source);
            Check(WindowNative.TrySetWindowDeviceBounds(source, initial), route + ": source positioned in device pixels");
            source.UpdateLayout();
            NativeInputPumpFor(80);
            Pr260H8FlushDesktop(route + " source preparation");
            var sourceHandle = new WindowInteropHelper(source).Handle;
            IntPtr outputHandle;
            IntPtr inputHandle;
            if (dedicated)
            {
                owner = new Routes12NativeOwner(bounds, initial);
                outputHandle = owner.OutputHandle;
                inputHandle = owner.InputHandle;
                Check(NativeInputGetWindowThreadProcessId(inputHandle, out _) != NativeInputGetCurrentThreadId(),
                    route + ": input HWND stays owned by the independent native loop");
            }
            else
            {
                uiPair = EdgeCapsuleQueueProxyWindow.TryCreate(
                    bounds,
                    true,
                    point => applied.Any(rect => EdgeCapsuleGeometry.Contains(rect, point)),
                    _ => uiPresses++,
                    static () => { },
                    static () => { },
                    static () => { });
                Check(uiPair != null && uiPair.TrySetInputRegions(applied) && uiPair.Show(bounds, true),
                    route + ": UI-owned native pair ready");
                outputHandle = uiPair!.Handle;
                inputHandle = uiPair.InputHandle;
            }

            var iid = typeof(IDCompositionDesktopDevice).GUID;
            Marshal.ThrowExceptionForHR(NativeInputDCompositionCreateDevice2(IntPtr.Zero, ref iid, out var pointer));
            device = new IDCompositionDesktopDevice(pointer);
            device.CreateTargetForHwnd(outputHandle, true, out target).CheckError();
            device.CreateVisual(out visual).CheckError();
            device.CreateSurfaceFromHwnd(sourceHandle, out var live).CheckError();
            surface = live;
            visual.SetContent(live).CheckError();
            visual.SetOffsetX(initial.Left - bounds.Left).CheckError();
            visual.SetOffsetY(initial.Top - bounds.Top).CheckError();
            target.SetRoot(visual).CheckError();
            device.Commit().CheckError();
            Pr260H8FlushDesktop(route + " cover-before-cloak");
            sourceCloaked = WindowNative.TrySetWindowCloakedBatchDetailed(
                [new WindowNative.WindowCloakChange(sourceHandle, true, false)]) == WindowNative.WindowCloakBatchResult.Success;
            Check(sourceCloaked, route + ": source cloaked only after proxy publication");
            Pr260H8FlushDesktop(route + " source cloaked");

            var initialCapture = Routes12CaptureRedSpan(bounds, sampleY, out _);
            Check(initialCapture.Count >= 40 && Math.Abs(initialCapture.Left - initial.Left) <= 3,
                $"{route}: fast desktop capture sees the initial live red source ({initialCapture})");

            var durationTicks = (long)Stopwatch.Frequency;
            var ticket = new Routes12Ticket(10, Stopwatch.GetTimestamp(), durationTicks, [initial], 120);
            List<Routes12Observation> observations;
            var stepCount = 0;
            var maxCommitMicroseconds = 0.0;
            var maxRegionMicroseconds = 0.0;
            if (dedicated)
            {
                animation = device.CreateAnimation();
                var from = initial.Left - bounds.Left;
                const double durationSeconds = 1.0;
                const int travel = 120;
                animation.SetAbsoluteBeginTime(ticket.Start).CheckError();
                animation.AddCubic(
                    0,
                    from,
                    (float)(3 * travel / durationSeconds),
                    (float)(-3 * travel / (durationSeconds * durationSeconds)),
                    (float)(travel / (durationSeconds * durationSeconds * durationSeconds))).CheckError();
                animation.End(durationSeconds, from + travel).CheckError();
                visual.SetOffsetX(animation).CheckError();
                device.Commit().CheckError();
                Check(owner!.Publish(ticket), route + ": dedicated owner accepts dynamic ticket");
                var before = owner.Updates;
                var observer = Task.Run(() => Routes12ObserveDynamic(inputHandle, bounds, sampleY, 430));
                Thread.Sleep(430);
                observations = observer.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                Check(owner.Updates > before + 4, route + ": HRGN continues publishing while WPF UI is blocked");
                applied = owner.Freeze(11);
                Check(applied[0].Left > initial.Left + 30, route + ": native owner progressed materially during the blocked interval");
            }
            else
            {
                var observer = Task.Run(() => Routes12ObserveDynamic(inputHandle, bounds, sampleY, 930));
                var cadenceTicks = Math.Max(1L, Stopwatch.Frequency / 120);
                var nextTick = ticket.Start;
                while (true)
                {
                    var now = Stopwatch.GetTimestamp();
                    if (now - ticket.Start >= ticket.Duration) break;
                    applied = ticket.Sample(now);
                    visual.SetOffsetX(applied[0].Left - bounds.Left).CheckError();
                    var commitStart = Stopwatch.GetTimestamp();
                    device.Commit().CheckError();
                    maxCommitMicroseconds = Math.Max(maxCommitMicroseconds,
                        (Stopwatch.GetTimestamp() - commitStart) * 1_000_000.0 / Stopwatch.Frequency);
                    var regionStart = Stopwatch.GetTimestamp();
                    Check(uiPair!.TrySetInputRegions(applied), route + ": shared sample publishes finite HRGN");
                    maxRegionMicroseconds = Math.Max(maxRegionMicroseconds,
                        (Stopwatch.GetTimestamp() - regionStart) * 1_000_000.0 / Stopwatch.Frequency);
                    stepCount++;
                    nextTick += cadenceTicks;
                    Routes12WaitUntil(nextTick);
                }
                applied = ticket.Sample(ticket.Start + ticket.Duration);
                visual.SetOffsetX(applied[0].Left - bounds.Left).CheckError();
                device.Commit().CheckError();
                Check(uiPair!.TrySetInputRegions(applied), route + ": exact terminal shared sample published");
                observations = observer.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                Check(stepCount >= 70, route + ": app-driven loop sustains enough scheduled updates for timing evidence");
            }

            var summary = Routes12Summarize(observations);
            Check(summary.Samples >= 5, route + ": observer captured at least five simultaneous physical-pixel/HRGN samples");
            Check(summary.PixelTravel >= 24, route + ": composed pixels moved materially during observation");
            Check(summary.InputTravel >= 24, route + ": native input region moved materially during observation");
            Console.WriteLine($"PASS {route} {summary} updates={(dedicated ? owner!.Updates : stepCount)} " +
                $"maxCommitUs={maxCommitMicroseconds:F1} maxRegionUs={maxRegionMicroseconds:F1} " +
                $"physicalRefreshAcceptance=UNTESTED productionIntegration=UNTESTED captureHandoff=UNTESTED");

            if (dedicated)
            {
                visual.SetOffsetX(applied[0].Left - bounds.Left).CheckError();
                device.Commit().CheckError();
            }
            Pr260H8FlushDesktop(route + " terminal settle");
            var endpoint = Routes12CaptureRedSpan(bounds, sampleY, out _);
            Check(endpoint.Count >= 40 && Math.Abs(endpoint.Left - applied[0].Left) <= 4,
                $"{route}: terminal composed pixels match the final published position ({endpoint})");
        }
        finally
        {
            try
            {
                if (sourceCloaked)
                    _ = WindowNative.TrySetWindowCloakedBatchDetailed(
                        [new WindowNative.WindowCloakChange(new WindowInteropHelper(source).Handle, false, true)]);
                if (target != null && device != null)
                {
                    target.SetRoot(null!).CheckError();
                    device.Commit().CheckError();
                }
            }
            finally
            {
                try { owner?.Dispose(); }
                finally
                {
                    uiPair?.Dispose();
                    animation?.Dispose();
                    visual?.Dispose();
                    surface?.Dispose();
                    target?.Dispose();
                    device?.Dispose();
                    source.Close();
                    NativeInputMove(new DeviceScreenPoint(cursor.X, cursor.Y));
                }
            }
        }
    }

    private static List<Routes12Observation> Routes12ObserveDynamic(
        IntPtr inputHandle,
        DeviceScreenRect bounds,
        int y,
        int milliseconds)
    {
        var result = new List<Routes12Observation>();
        var end = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * milliseconds / 1000.0);
        while (Stopwatch.GetTimestamp() < end)
        {
            var timestamp = Stopwatch.GetTimestamp();
            var pixels = Routes12CaptureRedSpan(bounds, y, out var fallback);
            var input = Routes12CaptureInputSpan(inputHandle, bounds, y);
            if (!pixels.IsEmpty && !input.IsEmpty)
                result.Add(new Routes12Observation(timestamp, pixels, input, fallback));
            Thread.Sleep(6);
        }
        return result;
    }

    private static Routes12DynamicSummary Routes12Summarize(IReadOnlyList<Routes12Observation> observations)
    {
        if (observations.Count == 0)
            return default;
        var deltas = observations.Select(item => item.Input.Left - item.Pixels.Left).ToArray();
        var abs = deltas.Select(Math.Abs).OrderBy(value => value).ToArray();
        var intervals = observations.Zip(observations.Skip(1), (a, b) =>
                (b.Timestamp - a.Timestamp) * 1000.0 / Stopwatch.Frequency)
            .OrderBy(value => value)
            .ToArray();
        return new Routes12DynamicSummary(
            observations.Count,
            observations.Count(item => item.PixelFallback),
            observations.Max(item => item.Pixels.Left) - observations.Min(item => item.Pixels.Left),
            observations.Max(item => item.Input.Left) - observations.Min(item => item.Input.Left),
            Routes12Percentile(abs.Select(value => (double)value).ToArray(), 0.50),
            Routes12Percentile(abs.Select(value => (double)value).ToArray(), 0.95),
            abs[^1],
            deltas.Count(value => value < -1),
            deltas.Count(value => value > 1),
            Routes12Percentile(intervals, 0.50),
            Routes12Percentile(intervals, 0.95));
    }

    private static double Routes12Percentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0) return 0;
        var index = (int)Math.Ceiling(percentile * sortedValues.Length) - 1;
        return sortedValues[Math.Clamp(index, 0, sortedValues.Length - 1)];
    }

    private static void Routes12WaitUntil(long target)
    {
        while (true)
        {
            var remaining = target - Stopwatch.GetTimestamp();
            if (remaining <= 0) return;
            var milliseconds = remaining * 1000.0 / Stopwatch.Frequency;
            if (milliseconds > 2.0)
                Thread.Sleep(Math.Max(1, (int)milliseconds - 1));
            else
                Thread.SpinWait(64);
        }
    }

    private static Routes12Span Routes12CaptureInputSpan(IntPtr inputHandle, DeviceScreenRect bounds, int y)
    {
        var region = NativeInputCreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (NativeInputGetWindowRgn(inputHandle, region) <= 0)
                return default;
            var left = int.MaxValue;
            var right = int.MinValue;
            var count = 0;
            var localY = y - bounds.Top;
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                if (!NativeInputPtInRegion(region, x - bounds.Left, localY)) continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x + 1);
                count++;
            }
            return count == 0 ? default : new Routes12Span(left, right, count);
        }
        finally
        {
            NativeInputDeleteObject(region);
        }
    }

    private static Routes12Span Routes12CaptureRedSpan(DeviceScreenRect bounds, int y, out bool fallback)
    {
        fallback = false;
        var dc = Routes12GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var memory = Routes12CreateCompatibleDC(dc);
        if (memory == IntPtr.Zero)
        {
            Routes12ReleaseDC(IntPtr.Zero, dc);
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        try
        {
            var info = new Routes12BitmapInfo
            {
                Header = new Routes12BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<Routes12BitmapInfoHeader>(),
                    Width = bounds.Width,
                    Height = -1,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0
                }
            };
            bitmap = Routes12CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            previous = Routes12SelectObject(memory, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (!Routes12BitBlt(memory, 0, 0, bounds.Width, 1, dc, bounds.Left, y, 0x40CC0020))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var pixels = new int[bounds.Width];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            var left = int.MaxValue;
            var right = int.MinValue;
            var count = 0;
            for (var index = 0; index < pixels.Length; index++)
            {
                var color = unchecked((uint)pixels[index]);
                var blue = (byte)(color & 0xFF);
                var green = (byte)((color >> 8) & 0xFF);
                var red = (byte)((color >> 16) & 0xFF);
                if (red < 180 || green > 90 || blue > 90) continue;
                var x = bounds.Left + index;
                left = Math.Min(left, x);
                right = Math.Max(right, x + 1);
                count++;
            }
            if (count > 0)
                return new Routes12Span(left, right, count);
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1))
                Routes12SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) NativeInputDeleteObject(bitmap);
            Routes12DeleteDC(memory);
            Routes12ReleaseDC(IntPtr.Zero, dc);
        }

        fallback = true;
        var slow = Pr260H8FindRedSpan(bounds, y);
        return new Routes12Span(slow.Left, slow.Right, slow.Count);
    }

    [DllImport("user32.dll", EntryPoint = "GetDC", SetLastError = true)]
    private static extern IntPtr Routes12GetDC(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static extern int Routes12ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll", EntryPoint = "CreateCompatibleDC", SetLastError = true)]
    private static extern IntPtr Routes12CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", EntryPoint = "DeleteDC")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Routes12DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection", SetLastError = true)]
    private static extern IntPtr Routes12CreateDIBSection(
        IntPtr dc,
        ref Routes12BitmapInfo bitmapInfo,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);

    [DllImport("gdi32.dll", EntryPoint = "SelectObject", SetLastError = true)]
    private static extern IntPtr Routes12SelectObject(IntPtr dc, IntPtr item);

    [DllImport("gdi32.dll", EntryPoint = "BitBlt", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Routes12BitBlt(
        IntPtr destination,
        int x,
        int y,
        int width,
        int height,
        IntPtr source,
        int sourceX,
        int sourceY,
        uint operation);
}
