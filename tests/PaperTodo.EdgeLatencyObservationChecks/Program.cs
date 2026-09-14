using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PaperTodo;

#if DEBUG
internal static class Program
{
    private const int BaselineMessage = 0x8051;
    private const int OuterMessage = 0x8052;
    private const int InnerMessage = 0x8053;
    private const int AfterInnerMessage = 0x8054;
    private const int OuterTailMessage = 0x8055;
    private const int AfterScopeMessage = 0x8056;
    private const int AfterRemoveMessage = 0x8057;
    private const int ReinstalledMessage = 0x8058;
    private static int _assertions;

    [STAThread]
    private static int Main(string[] args)
    {
        Environment.SetEnvironmentVariable("PAPERTODO_EDGE_DIAGNOSTICS", "memory");
        Environment.SetEnvironmentVariable("PAPERTODO_EDGE_DEEP_OBSERVATIONS", "1");
        var directory = args.Length == 2 && args[0] == "--output"
            ? Path.GetFullPath(args[1])
            : Path.Combine(AppContext.BaseDirectory, "artifacts");
        EdgeCapsulePerformanceDiagnostics.Initialize(directory);
        Application? application = null;
        var exitCode = 1;
        try
        {
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            CheckNumericReaders();
            CheckNativeForwarding();
            CheckDispatcherObservation(application.Dispatcher);
            CheckMessageObservation(application.Dispatcher);
            var entries = Entries();
            Check(!entries.Any(entry => entry.Name.EndsWith(".error", StringComparison.Ordinal)),
                "Observers did not report an internal error");
            Check(!entries.Any(entry => entry.Name == "native.deep.install-failed"),
                "Owned UI HWND subclass installation succeeded");
            var stats = EdgeCapsulePerformanceDiagnostics.Buffer.Stats;
            Check(stats.DroppedCapacity == 0 && stats.DroppedTextBudget == 0,
                "All behavior-check observations fit in the bounded journal");
            Console.WriteLine($"PASS: {_assertions} assertions; native forwarding and Dispatcher lifecycle preserved.");
            exitCode = 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
        }
        finally
        {
            EdgeDispatcherLatencyObservation.Remove();
            EdgeNativeLatencyObservation.Remove();
            application?.Shutdown();
            var completion = EdgeCapsulePerformanceDiagnostics.Buffer.Complete("behavior-check-exit");
            Console.WriteLine($"Journal: {completion.Path}");
            if (!completion.Completed || completion.Dropped != 0)
            {
                Console.Error.WriteLine($"Journal completion failed: {completion.Error}, dropped={completion.Dropped}");
                exitCode = 1;
            }
        }
        return exitCode;
    }

    private enum NumericState : long { Waiting = -17, Ready = 23 }

    private sealed class NumericFields
    {
        internal int InstanceCount = -31;
        internal long InstanceTicks = 9_007_199_254_740_993L;
        internal bool InstanceFlag = true;
        internal TimeSpan InstanceDuration = TimeSpan.FromTicks(-123_456_789);
        internal NumericState InstanceState = NumericState.Waiting;
        internal string Unsupported = "unchanged";
        internal static int SharedCount = 43;
        internal static long SharedTicks = -9_007_199_254_740_995L;
        internal static bool SharedFlag = false;
        internal static TimeSpan SharedDuration = TimeSpan.FromTicks(987_654_321);
        internal static NumericState SharedState = NumericState.Ready;
    }

    private static void CheckNumericReaders()
    {
        var factory = typeof(EdgeDispatcherLatencyObservation).GetMethod("MakeNumericReader",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Numeric reader factory unavailable");
        Func<object, long>? Reader(string name) =>
            (Func<object, long>?)factory.Invoke(null, [typeof(NumericFields), name]);
        var first = new NumericFields();
        var second = new NumericFields { InstanceCount = 61 };
        (string Name, long Expected, bool Shared)[] samples =
        [
            (nameof(NumericFields.InstanceCount), -31, false),
            (nameof(NumericFields.InstanceTicks), 9_007_199_254_740_993L, false),
            (nameof(NumericFields.InstanceFlag), 1, false),
            (nameof(NumericFields.InstanceDuration), -123_456_789, false),
            (nameof(NumericFields.InstanceState), -17, false),
            (nameof(NumericFields.SharedCount), 43, true),
            (nameof(NumericFields.SharedTicks), -9_007_199_254_740_995L, true),
            (nameof(NumericFields.SharedFlag), 0, true),
            (nameof(NumericFields.SharedDuration), 987_654_321, true),
            (nameof(NumericFields.SharedState), 23, true)
        ];
        foreach (var (name, expected, shared) in samples)
        {
            var reader = Reader(name);
            Check(reader != null, $"Numeric reader supports {(shared ? "static" : "instance")} {name}");
            Check(reader!(first) == expected && reader(first) == expected,
                $"Repeated reads preserve the exact typed value of {name}");
            if (shared)
                Check(reader(new object()) == expected,
                    $"Static reader {name} does not cast or dereference an instance target");
        }
        Check(Reader("MissingField") == null, "Missing numeric field safely disables its reader");
        Check(Reader(nameof(NumericFields.Unsupported)) == null,
            "Unsupported numeric field safely disables its reader");
        Check(first.Unsupported == "unchanged", "Reader creation does not mutate unsupported data");
        var instance = Reader(nameof(NumericFields.InstanceCount))!;
        Check(instance(second) == 61 && instance(first) == -31,
            "Instance reader selects the supplied object without retaining the first object");
        first.InstanceCount = 73;
        Check(instance(first) == 73 && instance(second) == 61, "Cached reader reads current instance state");
        var sharedCount = Reader(nameof(NumericFields.SharedCount))!;
        var sharedFlag = Reader(nameof(NumericFields.SharedFlag))!;
        try
        {
            NumericFields.SharedCount = 79;
            NumericFields.SharedFlag = true;
            Check(sharedCount(first) == 79 && sharedCount(second) == 79,
                "Cached static reader observes current shared state across instances");
            Check(sharedFlag(first) == 1, "Static bool reader converts true to one after mutation");
        }
        finally
        {
            NumericFields.SharedCount = 43;
            NumericFields.SharedFlag = false;
        }
        Check(sharedCount(first) == 43 && sharedFlag(first) == 0,
            "Static readers observe restored state without scheduling or writes");
    }

    private static void CheckNativeForwarding()
    {
        var received = new List<(int Message, IntPtr WParam, IntPtr LParam)>();
        var sent = new List<(int Message, IntPtr Result)>();
        using var source = new HwndSource(new HwndSourceParameters("PaperTodo latency observer checks")
        {
            Width = 16,
            Height = 16,
            // No WS_VISIBLE and no RootVisual: this is a test-owned hidden HWND, not a paper.
            WindowStyle = unchecked((int)0x80000000)
        });
        var handle = source.Handle;

        IntPtr Send(int message)
        {
            var result = SendMessage(handle, message, WParam(message), LParam(message));
            sent.Add((message, result));
            return result;
        }

        IntPtr Receive(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message < BaselineMessage || message > ReinstalledMessage) return IntPtr.Zero;
            received.Add((message, wParam, lParam));
            if (message == OuterMessage)
            {
                using (EdgeNativeLatencyObservation.BeginBatch([handle])) Send(InnerMessage);
                Send(AfterInnerMessage);
            }
            handled = true;
            return ExpectedResult(message);
        }

        source.AddHook(Receive);
        var baselineStart = Entries().Length;
        Send(BaselineMessage);
        Check(!Since(baselineStart).Any(entry => entry.Name.StartsWith("native.", StringComparison.Ordinal)),
            "Unobserved HWND forwards normally without emitting native records");

        var observedStart = Entries().Length;
        using (EdgeNativeLatencyObservation.BeginBatch([handle, handle]))
        {
            Send(OuterMessage);
            Send(OuterTailMessage);
        }
        var observed = Since(observedStart);
        Check(observed.Count(entry => entry.Name == "native.deep.installed") == 1,
            "Repeated HWND registration installs one subclass");
        var batches = observed.Where(entry => entry.Name == "native.deep.begin").ToArray();
        Check(batches.Length == 2, "Outer and nested native batches are both observed");
        var outerBatch = batches.Single(entry => entry.Value2 == 0).CorrelationId;
        var innerBatch = batches.Single(entry => entry.Value2 == outerBatch).CorrelationId;
        Check(innerBatch != outerBatch, "Nested native batch has its own identity");
        var outer = SingleMessage(observed, OuterMessage);
        var inner = SingleMessage(observed, InnerMessage);
        var afterInner = SingleMessage(observed, AfterInnerMessage);
        var tail = SingleMessage(observed, OuterTailMessage);
        Check(outer.Value1 == outerBatch && outer.Value4 == 0, "Outer message belongs to outer batch");
        Check(inner.Value1 == innerBatch && inner.Value4 == outer.CorrelationId,
            "Reentrant message records both nested batch and parent message");
        Check(afterInner.Value1 == outerBatch && afterInner.Value4 == outer.CorrelationId,
            "Nested batch disposal restores the parent batch inside the parent message");
        Check(tail.Value1 == outerBatch && tail.Value4 == 0,
            "Returning from the nested message restores the top-level message parent");
        foreach (var begin in observed.Where(entry => entry.Name == "native.message.begin"))
        {
            var end = observed.Single(entry => entry.Name == "native.message.end" && entry.CorrelationId == begin.CorrelationId);
            Check(end.Value2 == begin.Value1 && end.Value3 == begin.Value2 && end.Value4 == begin.Value3,
                "Native message completion retains batch, HWND and message identity");
        }
        Check(observed.Count(entry => entry.Name == "native.deep.end") == 2, "Both batch scopes complete exactly once");

        var outsideStart = Entries().Length;
        Send(AfterScopeMessage);
        Check(!Since(outsideStart).Any(entry => entry.Name.StartsWith("native.message.", StringComparison.Ordinal)),
            "No message is observed after the outer batch scope exits");

        using (EdgeNativeLatencyObservation.BeginBatch([]))
        {
            EdgeNativeLatencyObservation.Remove();
            EdgeNativeLatencyObservation.Remove();
            var removedStart = Entries().Length;
            Send(AfterRemoveMessage);
            Check(!Since(removedStart).Any(entry => entry.Name.StartsWith("native.message.", StringComparison.Ordinal)),
                "Remove detaches the native observer even while a batch is active");
        }

        var reinstalledStart = Entries().Length;
        using (EdgeNativeLatencyObservation.BeginBatch([handle])) Send(ReinstalledMessage);
        Check(Since(reinstalledStart).Count(entry => entry.Name == "native.deep.installed") == 1,
            "A removed native observer can be installed again");
        SingleMessage(Since(reinstalledStart), ReinstalledMessage);

        // Check downstream behavior independently of observer records, including 64-bit sentinels.
        for (var message = BaselineMessage; message <= ReinstalledMessage; message++)
        {
            var delivered = received.Where(item => item.Message == message).ToArray();
            Check(delivered.Length == 1, $"Message {message:X} reaches the HwndSource hook exactly once");
            Check(delivered[0].WParam == WParam(message) && delivered[0].LParam == LParam(message),
                $"Message {message:X} retains all WPARAM/LPARAM bits");
            Check(sent.Single(item => item.Message == message).Result == ExpectedResult(message),
                $"Message {message:X} returns the downstream hook's exact result");
        }

        var destroyStart = Entries().Length;
        using (EdgeNativeLatencyObservation.BeginBatch([handle])) source.Dispose();
        Check(Since(destroyStart).Count(entry => entry.Name == "native.message.begin" && entry.Value3 == 0x82) == 1,
            "WM_NCDESTROY is forwarded and observed once during an active batch");
        EdgeNativeLatencyObservation.Remove();
        Check(!IsWindow(handle), "The hidden test HWND is destroyed normally");
    }

    private static void CheckDispatcherObservation(Dispatcher dispatcher)
    {
        EdgeDispatcherLatencyObservation.Remove();
        var baseline = RunDispatcherScenario(dispatcher, observed: false);
        var reserved = typeof(Dispatcher).GetProperty("Reserved0", BindingFlags.Instance | BindingFlags.NonPublic);
        var existingContext = reserved?.GetValue(dispatcher);
        var installStart = Entries().Length;
        EdgeDispatcherLatencyObservation.Install();
        EdgeDispatcherLatencyObservation.Install();
        EdgeDispatcherLatencyObservation.Snapshot("behavior-check");
        Check(Since(installStart).Count(entry => entry.Name == "deep.dispatcher.installed") == 1,
            "Repeated Install attaches Dispatcher hooks once");
        if (reserved != null)
            Check(ReferenceEquals(existingContext, reserved.GetValue(dispatcher)),
                "Install and Snapshot retain the existing MediaContext identity");

        var observed = RunDispatcherScenario(dispatcher, observed: true);
        Check(observed.SequenceEqual(baseline), "Dispatcher observation preserves priority and FIFO execution order");
        EdgeDispatcherLatencyObservation.Remove();
        EdgeDispatcherLatencyObservation.Remove();
        var removedStart = Entries().Length;
        EdgeDispatcherLatencyObservation.Snapshot("after-remove");
        var afterRemove = RunDispatcherScenario(dispatcher, observed: false);
        Check(afterRemove.SequenceEqual(baseline), "Removing observation preserves operation execution behavior");
        Check(!Since(removedStart).Any(entry => entry.Name.StartsWith("deep.", StringComparison.Ordinal)),
            "Remove disconnects Dispatcher hooks and explicit MediaContext snapshots");

        EdgeDispatcherLatencyObservation.Install();
        var reinstalled = RunDispatcherScenario(dispatcher, observed: true);
        Check(reinstalled.SequenceEqual(baseline), "Dispatcher observation can be reinstalled without duplicate callbacks");
        EdgeDispatcherLatencyObservation.Remove();
    }

    private static void CheckMessageObservation(Dispatcher dispatcher)
    {
        var timingType = typeof(EdgeMessageLatencyObservation).GetNestedType("DwmTimingInfo", BindingFlags.NonPublic)!;
        Check(Marshal.SizeOf(timingType) == 292 && Marshal.OffsetOf(timingType, "QpcVBlank").ToInt32() == 28 &&
            Marshal.OffsetOf(timingType, "QpcRefreshPeriod").ToInt32() == 12,
            "DWM timing buffer matches the complete packed Windows SDK layout");
        var timingStart = Entries().Length;
        typeof(EdgeMessageLatencyObservation).GetMethod("CaptureDwmTiming", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [91823L]);
        var timing = Since(timingStart).Single(e => e.Name == "deep.message.dwm-call");
        Check(timing.Value3 >= 0 && timing.Value2 >= timing.Value1,
            "Read-only DWM timing call succeeds on this desktop with ordered before/after QPC");
        var clock = Since(timingStart).Single(e => e.Name == "deep.message.dwm-clock");
        var rate = Since(timingStart).Single(e => e.Name == "deep.message.dwm-rate");
        Check(clock.Value1 > 0 && clock.Value2 > 0 && rate.Value1 > 0 && rate.Value2 > 0 && rate.Value3 > 0 && rate.Value4 > 0,
            "Successful DWM read returns usable QPC and exact refresh/compose ratios");
        var messageId = unchecked((int)RegisterWindowMessage("MilChannelNotify"));
        var deliveries = new List<(IntPtr W, IntPtr L)>();
        using var source = new HwndSource(new HwndSourceParameters("PaperTodo message observer checks")
        { Width = 16, Height = 16, WindowStyle = unchecked((int)0x80000000) });
        source.AddHook((IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (message != messageId) return IntPtr.Zero;
            deliveries.Add((w, l));
            handled = true;
            return ExpectedResult(messageId);
        });
        var handle = source.Handle;
        var baseline = SendMessage(handle, messageId, WParam(messageId), LParam(messageId));
        Check(baseline == ExpectedResult(messageId), "Controlled channel-like message has a known downstream result");
        Environment.SetEnvironmentVariable("PAPERTODO_EDGE_MESSAGE_OBSERVATIONS", "1");
        var start = Entries().Length;
        EdgeDispatcherLatencyObservation.Install();
        EdgeMessageLatencyObservation.Install();
        Check(Since(start).Count(e => e.Name == "deep.message.installed") == 1,
            "Repeated observer installation attaches one thread-message filter");
        var filter = typeof(EdgeMessageLatencyObservation).GetMethod("OnThreadMessage", BindingFlags.NonPublic | BindingFlags.Static)!;
        var msg = new MSG { hwnd = handle, message = messageId, wParam = WParam(messageId),
            lParam = LParam(messageId), time = unchecked((int)GetTickCount() - 17) };
        object?[] arguments = [msg, false];
        filter.Invoke(null, arguments);
        var unchanged = (MSG)arguments[0]!;
        Check(!(bool)arguments[1]! && unchanged.hwnd == msg.hwnd && unchanged.message == msg.message &&
            unchanged.wParam == msg.wParam && unchanged.lParam == msg.lParam && unchanged.time == msg.time,
            "Thread filter leaves handled, HWND, message, parameters and timestamp unchanged");
        var queued = Since(start).Single(e => e.Name == "deep.message.queued" && e.Value1 == handle.ToInt64());
        Check(queued.Number1 == unchecked((uint)((int)queued.Value4 - (int)queued.Value3)),
            "Queue age uses recorded uptime values with unsigned wrap semantics");
        var result = SendMessage(handle, messageId, WParam(messageId), LParam(messageId));
        Check(result == baseline && deliveries.Count == 2 && deliveries[1] == deliveries[0],
            "Subclass preserves exact downstream result, arguments and once-only forwarding");
        var channel = Since(start).Where(e => e.Name.StartsWith("deep.message.channel.", StringComparison.Ordinal)).ToArray();
        Check(channel.Length == 2 && channel[0].Name.EndsWith("begin", StringComparison.Ordinal) &&
            channel[1].Name.EndsWith("end", StringComparison.Ordinal) && channel[0].CorrelationId == channel[1].CorrelationId,
            "Channel dispatch has one matched before/after observation");
        Check(channel[1].Value4 >= 0 && channel[1].Value4 <= channel[1].Value1,
            "Downstream QPC duration is nonnegative and enclosed by the inclusive observation");

        var wrapStart = Entries().Length;
        arguments = [new MSG { hwnd = handle, message = messageId, time = int.MaxValue }, true];
        filter.Invoke(null, arguments);
        var wrap = Since(wrapStart).Single(e => e.Name == "deep.message.queued");
        Check((bool)arguments[1]! && wrap.Number2 == 1 && wrap.Value3 == int.MaxValue,
            "Already handled messages retain their flag and timestamp across the sign boundary");

        var postStart = Entries().Length;
        Check(PostMessage(handle, messageId, WParam(messageId), LParam(messageId)), "Test-owned message can be posted");
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Check(deliveries.Count == 3 && deliveries[2] == deliveries[0],
            "Real Dispatcher message pumping forwards the posted message once with exact parameters");
        Check(Since(postStart).Count(e => e.Name == "deep.message.queued" && e.Value1 == handle.ToInt64() && e.Value2 == messageId) == 1,
            "Real ThreadFilterMessage observes one queue timestamp for the controlled posted message");
        Check(Since(postStart).Count(e => e.Name == "deep.message.channel.begin" && e.Value1 == handle.ToInt64()) == 1,
            "Posted dispatch reaches the subclass once");
        EdgeDispatcherLatencyObservation.Remove();
        EdgeMessageLatencyObservation.Remove();
        var removedStart = Entries().Length;
        Check(SendMessage(handle, messageId, WParam(messageId), LParam(messageId)) == baseline,
            "Removal preserves downstream behavior");
        Check(!Since(removedStart).Any(e => e.Name.StartsWith("deep.message.", StringComparison.Ordinal)),
            "Removal detaches native and managed message observation");
        EdgeDispatcherLatencyObservation.Install();
        arguments = [msg, false];
        filter.Invoke(null, arguments);
        source.Dispose();
        Check(!IsWindow(handle), "An observed channel-like HWND is destroyed normally");
        EdgeDispatcherLatencyObservation.Remove();
        Environment.SetEnvironmentVariable("PAPERTODO_EDGE_MESSAGE_OBSERVATIONS", null);
    }

    private static string[] RunDispatcherScenario(Dispatcher dispatcher, bool observed)
    {
        var steps = new List<string>();
        var pending = new List<(string Label, DispatcherOperation Operation, long Id)>();
        var frame = new DispatcherFrame();

        DispatcherOperation Post(string label, DispatcherPriority priority, Action? body = null)
        {
            var start = Entries().Length;
            var operation = dispatcher.BeginInvoke(priority, new Action(() =>
            {
                if (label != "stop") steps.Add(label);
                body?.Invoke();
            }));
            var id = observed
                ? Since(start).Single(entry => entry.Name == "deep.dispatcher.posted").CorrelationId
                : 0;
            pending.Add((label, operation, id));
            return operation;
        }

        Post("background-a", DispatcherPriority.Background);
        Post("normal-a", DispatcherPriority.Normal,
            () => dispatcher.Invoke(() => steps.Add("inline"), DispatcherPriority.Send));
        Post("render", DispatcherPriority.Render);
        Post("input", DispatcherPriority.Input);
        Post("normal-b", DispatcherPriority.Normal);
        var promoted = Post("promoted", DispatcherPriority.Background);
        promoted.Priority = DispatcherPriority.Send;
        var aborted = Post("aborted", DispatcherPriority.Normal);
        Check(aborted.Abort(), "Pending Dispatcher operation can still be aborted");
        Post("background-b", DispatcherPriority.Background);
        Post("stop", DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);

        string[] expected = ["promoted", "normal-a", "inline", "normal-b", "render", "input", "background-a", "background-b"];
        Check(steps.SequenceEqual(expected), "Known operations execute once with priority, FIFO and synchronous Invoke semantics");
        foreach (var (label, operation, id) in pending)
        {
            Check(operation.Status == (label == "aborted" ? DispatcherOperationStatus.Aborted : DispatcherOperationStatus.Completed),
                $"Operation {label} reaches its expected terminal state");
            if (!observed) continue;
            var events = Entries().Where(entry => entry.CorrelationId == id && entry.Name.StartsWith("deep.dispatcher.", StringComparison.Ordinal)).ToArray();
            Check(events.Count(entry => entry.Name == "deep.dispatcher.posted") == 1, $"Operation {label} has one posted observation");
            Check(events.Count(entry => entry.Name == "deep.dispatcher.started") == (label == "aborted" ? 0 : 1),
                $"Operation {label} has the expected start count");
            Check(events.Count(entry => entry.Name == "deep.dispatcher.completed") == (label == "aborted" ? 0 : 1),
                $"Operation {label} has the expected completion count");
            Check(events.Count(entry => entry.Name == "deep.dispatcher.aborted") == (label == "aborted" ? 1 : 0),
                $"Operation {label} has the expected abort count");
            var priority = events.Where(entry => entry.Name == "deep.dispatcher.priority-changing").ToArray();
            Check(priority.Length == (label == "promoted" ? 1 : 0), $"Operation {label} has the expected priority-change count");
            if (label == "promoted")
                Check(priority[0].Value1 == (long)DispatcherPriority.Background,
                    "Priority-changing reports the old priority without altering the requested promotion");
        }
        return steps.ToArray();
    }

    private static EdgeDiagnosticJournal.Entry SingleMessage(EdgeDiagnosticJournal.Entry[] entries, int message)
    {
        var matches = entries.Where(entry => entry.Name == "native.message.begin" && entry.Value3 == message).ToArray();
        Check(matches.Length == 1, $"Native message {message:X} has one observation");
        return matches[0];
    }

    private static EdgeDiagnosticJournal.Entry[] Entries() => EdgeCapsulePerformanceDiagnostics.Buffer.Snapshot();
    private static EdgeDiagnosticJournal.Entry[] Since(int start) => Entries().Skip(start).ToArray();
    private static IntPtr WParam(int message) => new(unchecked((long)0xFEDCBA9876540000UL) | (uint)message);
    private static IntPtr LParam(int message) => new(0x12345678ABCD0000L | (uint)message);
    private static IntPtr ExpectedResult(int message) => new(unchecked((long)0x89ABCDEF01230000UL) | (uint)message);
    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr handle);
    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();
}

// Only the facade is adapted. The observers and bounded journal are linked production code.
internal static class EdgeCapsulePerformanceDiagnostics
{
    internal static EdgeDiagnosticJournal.Buffer Buffer { get; private set; } = null!;
    internal static void Initialize(string directory) => Buffer = new(directory, 16_384, 4 * 1024 * 1024);
    internal static void Event(string name, long correlationId = 0, long value1 = 0, long value2 = 0,
        long value3 = 0, long value4 = 0, double number1 = 0, double number2 = 0, string? detail = null) =>
        Buffer.Event(name, correlationId, value1, value2, value3, value4, number1, number2, detail);
}
#else
internal static class Program
{
    private static int Main()
    {
        Console.Error.WriteLine("This check exercises Debug-only observers. Run with -c Debug.");
        return 2;
    }
}
#endif
