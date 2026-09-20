using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using PaperTodo;

internal static class EdgeBrowsingComparisonEntry
{
    private static readonly UIntPtr InputTag = new(0x5054444252575345UL);
    private const int OwnerTimeoutMilliseconds = 260;
    private const int StableTimeoutMilliseconds = 1200;

    [STAThread]
    public static int Main(string[] args)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PAPERTODO_BROWSE_AB"),
                "1",
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Refusing to run edge-browse fixture without PAPERTODO_BROWSE_AB=1.");
            return 2;
        }

        var mode = ReadMode(args);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 0;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await RunAsync(mode);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
            finally
            {
                app.Shutdown();
            }
        }, DispatcherPriority.Send);
        app.Run();
        return exitCode;
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

    private static async Task RunAsync(string mode)
    {
        Require(GetCursorPos(out var originalCursor), "interactive desktop unavailable");

        var state = new AppState
        {
            TelemetryEnabled = false,
            EnableAnimations = true,
            UseCapsuleMode = true,
            UseDeepCapsuleMode = true,
            ExperimentalEdgeCapsuleHoverPreview = true,
            UsePersistentPowerShellProcess = false,
            McpEnabled = false,
            HidePapersFromTaskbar = true
        };
        for (var i = 0; i < 3; i++)
        {
            state.Papers.Add(new PaperData
            {
                Id = "browse-" + i,
                Type = PaperTypes.Note,
                Content = "# Browse " + i + "\n\nContinuous edge-preview fixture " + i,
                IsVisible = true,
                IsCollapsed = true,
                Width = 320,
                Height = 240,
                CapsuleSide = DeepCapsuleSides.Right
            });
        }

        var store = new StateStore();
        store.SaveJsonSync(store.SerializeState(state), 1);

        using var controller = new AppController();
        try
        {
            await controller.StartAsync(createDefaultPaper: false);
            var stateIds = controller.State.Papers
                .Select(paper => paper.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            Console.WriteLine("BROWSE_STATE mode=" + mode + " ids=" + string.Join(",", stateIds));
            Require(
                stateIds.SequenceEqual(
                    new[] { "browse-0", "browse-1", "browse-2" },
                    StringComparer.Ordinal),
                "fixture state contains unexpected papers: " + string.Join(",", stateIds));

            var windows = (Dictionary<string, PaperWindow>)Field(controller, "_windows");
            await UntilAsync(
                () => windows.Count == 3 &&
                      windows.Values.All(window =>
                          window.CanEnterEdgeCapsulePreview &&
                          window.TryGetEdgeCapsuleInteractiveGeometry(out var geometry) &&
                          !geometry.Bounds.IsEmpty),
                10_000,
                "three real edge capsules did not become browse-ready");

            var ordered = new[]
            {
                windows["browse-0"],
                windows["browse-1"],
                windows["browse-2"]
            };

            await Task.Delay(120);
            Require(TryMoveTo(ordered[0], out var initialPoint, out var initialBounds),
                "initial capsule has no interactive geometry");
            var firstStarted = Stopwatch.GetTimestamp();
            var firstOwnerMs = await WaitForOwnerAsync(controller, ordered[0], OwnerTimeoutMilliseconds);
            Require(firstOwnerMs.HasValue, "first preview did not open from a real OS pointer hit");
            var firstStableMs = await WaitForStableQueueAsync(controller, ordered, firstStarted, StableTimeoutMilliseconds);
            Console.WriteLine(
                $"BROWSE_CASE mode={mode} phase=first target=browse-0 point={Point(initialPoint)} " +
                $"bounds={Rect(initialBounds)} ownerMs={F(firstOwnerMs)} stableMs={F(firstStableMs)}");

            var steadyTargets = new[] { 1, 2, 1, 0 };
            var steadyOwner = new List<double>();
            var steadyStable = new List<double>();
            var steadyMisses = 0;
            foreach (var targetIndex in steadyTargets)
            {
                var target = ordered[targetIndex];
                var from = CurrentOwnerId(controller) ?? "none";
                if (!TryMoveTo(target, out var point, out var bounds))
                {
                    steadyMisses++;
                    Console.WriteLine(
                        $"BROWSE_TRANSFER mode={mode} phase=steady from={from} to={target.EdgeCapsulePreviewPaperId} " +
                        "result=geometry-miss");
                    continue;
                }

                var started = Stopwatch.GetTimestamp();
                var ownerMs = await WaitForOwnerAsync(controller, target, OwnerTimeoutMilliseconds);
                if (!ownerMs.HasValue)
                {
                    steadyMisses++;
                    Console.WriteLine(
                        $"BROWSE_TRANSFER mode={mode} phase=steady from={from} to={target.EdgeCapsulePreviewPaperId} " +
                        $"point={Point(point)} bounds={Rect(bounds)} result=owner-timeout actual={CurrentOwnerId(controller) ?? "none"}");
                    continue;
                }

                steadyOwner.Add(ownerMs.Value);
                var stableMs = await WaitForStableQueueAsync(controller, ordered, started, StableTimeoutMilliseconds);
                if (stableMs.HasValue)
                    steadyStable.Add(stableMs.Value);
                Console.WriteLine(
                    $"BROWSE_TRANSFER mode={mode} phase=steady from={from} to={target.EdgeCapsulePreviewPaperId} " +
                    $"point={Point(point)} bounds={Rect(bounds)} ownerMs={F(ownerMs)} stableMs={F(stableMs)} result=ok");
            }

            // Continuous browsing: begin each next move shortly after the 32 ms owner transfer,
            // deliberately before the normal 200 ms preview/queue transition can finish.
            var fastTargets = new[] { 1, 2, 0, 2, 1, 0, 2, 1, 0, 2 };
            var fastOwner = new List<double>();
            var fastMisses = 0;
            var geometryMisses = 0;
            foreach (var targetIndex in fastTargets)
            {
                var target = ordered[targetIndex];
                var from = CurrentOwnerId(controller) ?? "none";
                if (!TryMoveTo(target, out var point, out var bounds))
                {
                    geometryMisses++;
                    fastMisses++;
                    Console.WriteLine(
                        $"BROWSE_TRANSFER mode={mode} phase=fast from={from} to={target.EdgeCapsulePreviewPaperId} " +
                        "result=geometry-miss");
                    await Task.Delay(60);
                    continue;
                }

                var ownerMs = await WaitForOwnerAsync(controller, target, OwnerTimeoutMilliseconds);
                if (ownerMs.HasValue)
                {
                    fastOwner.Add(ownerMs.Value);
                    Console.WriteLine(
                        $"BROWSE_TRANSFER mode={mode} phase=fast from={from} to={target.EdgeCapsulePreviewPaperId} " +
                        $"point={Point(point)} bounds={Rect(bounds)} ownerMs={F(ownerMs)} result=ok");
                    await Task.Delay(45);
                }
                else
                {
                    fastMisses++;
                    Console.WriteLine(
                        $"BROWSE_TRANSFER mode={mode} phase=fast from={from} to={target.EdgeCapsulePreviewPaperId} " +
                        $"point={Point(point)} bounds={Rect(bounds)} result=owner-timeout actual={CurrentOwnerId(controller) ?? "none"}");
                    await Task.Delay(60);
                }
            }

            var finalExpected = ordered[fastTargets[^1]];
            var finalStableStarted = Stopwatch.GetTimestamp();
            var finalStableMs = await WaitForStableQueueAsync(
                controller,
                ordered,
                finalStableStarted,
                StableTimeoutMilliseconds);
            var finalOwner = CurrentOwnerId(controller) ?? "none";
            var fastSuccess = fastTargets.Length - fastMisses;

            Console.WriteLine(
                $"BROWSE_SUMMARY mode={mode} sourceSha={Environment.GetEnvironmentVariable("PAPERTODO_BROWSE_SOURCE_SHA") ?? "unknown"} " +
                $"firstOwnerMs={F(firstOwnerMs)} firstStableMs={F(firstStableMs)} " +
                $"steadySuccess={steadyTargets.Length - steadyMisses}/{steadyTargets.Length} " +
                $"steadyOwnerP50={F(Percentile(steadyOwner, 0.50))} steadyOwnerP95={F(Percentile(steadyOwner, 0.95))} " +
                $"steadyStableP50={F(Percentile(steadyStable, 0.50))} steadyStableP95={F(Percentile(steadyStable, 0.95))} " +
                $"fastSuccess={fastSuccess}/{fastTargets.Length} geometryMisses={geometryMisses} " +
                $"fastOwnerP50={F(Percentile(fastOwner, 0.50))} fastOwnerP95={F(Percentile(fastOwner, 0.95))} " +
                $"finalOwner={finalOwner} finalExpected={finalExpected.EdgeCapsulePreviewPaperId} finalStableMs={F(finalStableMs)}");
        }
        finally
        {
            try { MoveMouse(new DeviceScreenPoint(originalCursor.X, originalCursor.Y)); } catch { }
            try { controller.Exit(); } catch { }
        }
    }

    private static async Task<double?> WaitForOwnerAsync(
        AppController controller,
        PaperWindow target,
        int timeoutMilliseconds)
    {
        var started = Stopwatch.GetTimestamp();
        while (ElapsedMilliseconds(started) < timeoutMilliseconds)
        {
            if (controller.IsEdgeCapsulePreviewOwner(target) &&
                target.IsEdgeCapsulePreviewOpen)
            {
                return ElapsedMilliseconds(started);
            }
            await Task.Delay(4);
        }
        return null;
    }

    private static async Task<double?> WaitForStableQueueAsync(
        AppController controller,
        IReadOnlyList<PaperWindow> windows,
        long operationStarted,
        int timeoutMilliseconds)
    {
        string? previous = null;
        var sameCount = 0;
        var waitStarted = Stopwatch.GetTimestamp();
        while (ElapsedMilliseconds(waitStarted) < timeoutMilliseconds)
        {
            var signature = QueueSignature(controller, windows);
            if (string.Equals(signature, previous, StringComparison.Ordinal))
                sameCount++;
            else
            {
                previous = signature;
                sameCount = 0;
            }

            if (sameCount >= 3)
                return ElapsedMilliseconds(operationStarted);
            await Task.Delay(16);
        }
        return null;
    }

    private static string QueueSignature(
        AppController controller,
        IReadOnlyList<PaperWindow> windows)
    {
        var owner = CurrentOwnerId(controller) ?? "none";
        var parts = new List<string>(windows.Count + 1) { owner };
        foreach (var window in windows)
        {
            if (window.TryGetEdgeCapsuleAppliedGeometry(out var geometry))
                parts.Add(window.EdgeCapsulePreviewPaperId + ":" + Rect(geometry.Bounds));
            else
                parts.Add(window.EdgeCapsulePreviewPaperId + ":none");
        }
        return string.Join("|", parts);
    }

    private static bool TryMoveTo(
        PaperWindow window,
        out DeviceScreenPoint point,
        out DeviceScreenRect bounds)
    {
        point = default;
        bounds = default;
        if (!window.TryGetEdgeCapsuleInteractiveGeometry(out var geometry) ||
            geometry.Bounds.IsEmpty)
            return false;
        bounds = geometry.Bounds;
        point = new DeviceScreenPoint(
            (bounds.Left + bounds.Right) / 2.0,
            (bounds.Top + bounds.Bottom) / 2.0);
        MoveMouse(point);
        return true;
    }

    private static string? CurrentOwnerId(AppController controller)
    {
        var session = (EdgeCapsulePreviewLayoutSession?)Field(controller, "_edgeCapsulePreviewSession");
        return session?.OwnerPaperId;
    }

    private static object Field(object instance, string name) =>
        instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance)!;

    private static async Task UntilAsync(
        Func<bool> predicate,
        int timeoutMilliseconds,
        string message)
    {
        var started = Stopwatch.GetTimestamp();
        while (!predicate() && ElapsedMilliseconds(started) < timeoutMilliseconds)
            await Task.Delay(16);
        Require(predicate(), message);
    }

    private static double? Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return null;
        var ordered = values.OrderBy(value => value).ToArray();
        var position = Math.Clamp((int)Math.Ceiling(p * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[position];
    }

    private static double ElapsedMilliseconds(long started) =>
        (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

    private static string F(double? value) =>
        value.HasValue
            ? value.Value.ToString("F1", CultureInfo.InvariantCulture)
            : "none";

    private static string Rect(DeviceScreenRect rect) =>
        $"[{rect.Left},{rect.Top},{rect.Right},{rect.Bottom})";

    private static string Point(DeviceScreenPoint point) =>
        $"{point.X:F0},{point.Y:F0}";

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("BROWSE check failed: " + message);
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
            throw new InvalidOperationException("browse target outside virtual desktop");

        var input = new InputPacket
        {
            Type = 0,
            Mouse = new MouseInput
            {
                X = (int)Math.Round((point.X - left) * 65535d / (width - 1)),
                Y = (int)Math.Round((point.Y - top) * 65535d / (height - 1)),
                Flags = 0x0001 | 0x8000 | 0x4000 | 0x2000,
                ExtraInfo = InputTag
            }
        };
        if (SendInput(1, [input], Marshal.SizeOf<InputPacket>()) != 1)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }

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
}
