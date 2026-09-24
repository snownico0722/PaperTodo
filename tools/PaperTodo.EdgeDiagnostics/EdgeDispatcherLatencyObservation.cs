#if DEBUG
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// Opt-in, passive observations of the existing UI Dispatcher and WPF MediaContext.
/// Private readers are diagnostic capabilities, not runtime dependencies. Missing members
/// disable that observation only. No operation, timer, render subscription or frame is created.
/// </summary>
internal static class EdgeDispatcherLatencyObservation
{
    private sealed record MethodDescription(long Id, bool IsMediaContext, string Name);
    private sealed record OperationIdentity(long Id, MethodDescription Method);

    // Numeric field indices also define the bit positions in deep.media.available/value1.
    private static readonly string[] NumericFields =
    [
        "_needToCommitChannel", "_commitPendingAfterRender", "_isRendering", "_interlockState",
        "_lastPresentationResults", "_lastPresentationTime", "_estimatedNextPresentationTime",
        "_lastCommitTime", "_animationRenderRate", "_contextRenderID"
    ];
    // Timer indices define deep.media.available/value2 bits 0..2; bit 3 is currentRenderOp.
    private static readonly string[] TimerFields =
    [
        "_estimatedNextVSyncTimer", "_promoteRenderOpToInput", "_promoteRenderOpToRender"
    ];
    private sealed class MediaReaders
    {
        internal readonly Func<object, long>?[] Numbers = new Func<object, long>?[NumericFields.Length];
        internal readonly Func<object, DispatcherTimer?>?[] Timers = new Func<object, DispatcherTimer?>?[TimerFields.Length];
        internal Func<object, DispatcherOperation?>? CurrentOperation;
        internal long NumberMask;
        internal long ObjectMask;
    }

    private const int MaximumMethodMappings = 512;
    private static readonly object MethodGate = new();
    private static readonly Dictionary<MethodInfo, MethodDescription> Methods = new();
    private static readonly ConditionalWeakTable<DispatcherOperation, OperationIdentity> Operations = new();
    private static readonly MethodDescription UnknownMethod = new(0, false, "unavailable");
    private static Dispatcher? _dispatcher;
    private static DispatcherHooks? _hooks;
    private static Func<Dispatcher, object?>? _readReservedContext;
    private static Func<DispatcherOperation, Delegate?>? _readOperationMethod;
    private static object? _media;
    private static MediaReaders? _readers;
    private static MethodInfo? _removeCommittingHandler;
    private static bool _committingInstalled;
    private static bool _installed;
    private static bool _reportedNoContext;
    private static bool _reportedMethodLimit;
    private static long _nextId;
    private static int _faultReported;
    [ThreadStatic] private static bool _insideObservation;

    internal static void Install()
    {
        try
        {
            if (_installed || !EdgeDiagnosticJournal.Enabled ||
                Environment.GetEnvironmentVariable("PAPERTODO_EDGE_DEEP_OBSERVATIONS") != "1") return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || !dispatcher.CheckAccess())
            {
                Emit("deep.dispatcher.unavailable", detail: "Install requires the existing application UI thread");
                return;
            }

            _dispatcher = dispatcher;
            // Reserved0 is storage for an already-created MediaContext. MediaContext.From and
            // CurrentMediaContext would create one when empty, and are deliberately not called.
            var reserved = typeof(Dispatcher).GetProperty("Reserved0", BindingFlags.Instance | BindingFlags.NonPublic);
            _readReservedContext = MakePropertyReader<Dispatcher, object?>(reserved);
            _readOperationMethod = MakeFieldReader<DispatcherOperation, Delegate?>(
                typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic));
            Emit("deep.dispatcher.available", value1: _readReservedContext == null ? 0 : 1,
                value2: _readOperationMethod == null ? 0 : 1,
                detail: typeof(CompositionTarget).Assembly.FullName);

            _hooks = dispatcher.Hooks;
            _installed = true;
            _hooks.OperationPosted += OnPosted;
            _hooks.OperationStarted += OnStarted;
            _hooks.OperationCompleted += OnCompleted;
            _hooks.OperationAborted += OnAborted;
            _hooks.OperationPriorityChanged += OnPriorityChanged;
            EdgeMessageLatencyObservation.Install();
            Emit("deep.dispatcher.installed");
            Snapshot("install");
        }
        catch (Exception error)
        {
            ReportFault("install", error);
            Remove();
        }
    }

    internal static void Remove()
    {
        _installed = false;
        try
        {
            EdgeMessageLatencyObservation.Remove();
            if (_hooks is { } hooks)
            {
                hooks.OperationPosted -= OnPosted;
                hooks.OperationStarted -= OnStarted;
                hooks.OperationCompleted -= OnCompleted;
                hooks.OperationAborted -= OnAborted;
                hooks.OperationPriorityChanged -= OnPriorityChanged;
            }
            DetachCommittingHandler();
        }
        catch (Exception error) { ReportFault("remove", error); }
        finally
        {
            _hooks = null;
            _dispatcher = null;
            _media = null;
            _readers = null;
            _readReservedContext = null;
            _readOperationMethod = null;
            Operations.Clear();
            lock (MethodGate) Methods.Clear();
            _reportedNoContext = false;
            _reportedMethodLimit = false;
        }
    }

    internal static void Snapshot(string reason)
    {
        if (!_installed || _insideObservation || _dispatcher?.CheckAccess() != true) return;
        _insideObservation = true;
        try { WriteSnapshot(reason); }
        catch (Exception error) { ReportFault("snapshot", error); }
        finally { _insideObservation = false; }
    }

    private static void OnPosted(object? sender, DispatcherHookEventArgs args) => ObserveOperation("deep.dispatcher.posted", args.Operation);
    private static void OnStarted(object? sender, DispatcherHookEventArgs args) => ObserveOperation("deep.dispatcher.started", args.Operation);
    private static void OnCompleted(object? sender, DispatcherHookEventArgs args) => ObserveOperation("deep.dispatcher.completed", args.Operation);
    private static void OnAborted(object? sender, DispatcherHookEventArgs args) => ObserveOperation("deep.dispatcher.aborted", args.Operation);
    private static void OnPriorityChanged(object? sender, DispatcherHookEventArgs args) => ObserveOperation("deep.dispatcher.priority-changing", args.Operation);

    private static void ObserveOperation(string kind, DispatcherOperation operation)
    {
        if (!_installed || _insideObservation) return;
        _insideObservation = true;
        try
        {
            var identity = Identity(operation);
            // PriorityChanged is raised inside Dispatcher.SetPriority, before the property setter
            // updates operation.Priority. This is the observed OLD value, not the requested value.
            // Started is likewise before Invoke changes Status from Pending to Executing.
            Emit(kind, identity.Id, (long)operation.Priority, identity.Method.Id,
                (long)operation.Status, _dispatcher?.CheckAccess() == true ? 1 : 0);
            if (identity.Method.IsMediaContext &&
                (kind is "deep.dispatcher.started" or "deep.dispatcher.completed") &&
                _dispatcher?.CheckAccess() == true)
                WriteSnapshot(kind);
        }
        catch (Exception error) { ReportFault("dispatcher-hook", error); }
        finally { _insideObservation = false; }
    }

    private static OperationIdentity Identity(DispatcherOperation operation) => Operations.GetValue(
        operation, static value => new OperationIdentity(Interlocked.Increment(ref _nextId), DescribeMethod(value)));

    private static MethodDescription DescribeMethod(DispatcherOperation operation)
    {
        var method = _readOperationMethod?.Invoke(operation)?.Method;
        if (method == null) return UnknownMethod;
        lock (MethodGate)
        {
            if (Methods.TryGetValue(method, out var known)) return known;
            if (Methods.Count >= MaximumMethodMappings)
            {
                if (!_reportedMethodLimit)
                {
                    _reportedMethodLimit = true;
                    Emit("deep.dispatcher.method-limit", value1: MaximumMethodMappings);
                }
                return UnknownMethod;
            }
            var typeName = method.DeclaringType?.FullName ?? "unknown";
            var description = new MethodDescription(Interlocked.Increment(ref _nextId),
                typeName == "System.Windows.Media.MediaContext", typeName + "." + method.Name);
            Methods.Add(method, description);
            Emit("deep.dispatcher.method", description.Id, description.IsMediaContext ? 1 : 0, detail: description.Name);
            return description;
        }
    }

    private static void WriteSnapshot(string reason)
    {
        if (!BindExistingMediaContext() || _media == null || _readers == null) return;
        var media = _media;
        var readers = _readers;
        var id = Interlocked.Increment(ref _nextId);
        var operation = readers.CurrentOperation?.Invoke(media);
        var estimated = readers.Timers[0]?.Invoke(media);
        var inputPromotion = readers.Timers[1]?.Invoke(media);
        var renderPromotion = readers.Timers[2]?.Invoke(media);
        // flags: needCommit=1, commitPending=2, isRendering=4; enabled timers=8/16/32.
        // Check availability masks before treating an absent flag as a known false value.
        long flags = (ReadNumber(readers, media, 0) == 1 ? 1 : 0) |
            (ReadNumber(readers, media, 1) == 1 ? 2 : 0) |
            (ReadNumber(readers, media, 2) == 1 ? 4 : 0) |
            (estimated?.IsEnabled == true ? 8 : 0) |
            (inputPromotion?.IsEnabled == true ? 16 : 0) |
            (renderPromotion?.IsEnabled == true ? 32 : 0);
        Emit("deep.media.state", id, flags, ReadNumber(readers, media, 3),
            ReadNumber(readers, media, 4), operation == null ? 0 : Identity(operation).Id,
            operation == null ? -1 : (double)operation.Priority,
            operation == null ? -1 : (double)operation.Status, reason);
        // lastPresentation is QPC; estimatedPresentation and lastCommit are WPF 100ns ticks.
        Emit("deep.media.clock", id, ReadNumber(readers, media, 5), ReadNumber(readers, media, 6),
            ReadNumber(readers, media, 7), ReadNumber(readers, media, 9),
            ReadNumber(readers, media, 8));
        Emit("deep.media.timer", id, estimated?.Tag is long ticks ? ticks : -1,
            renderPromotion?.Interval.Ticks ?? -1, 0, 0,
            estimated?.Interval.TotalMilliseconds ?? -1, inputPromotion?.Interval.TotalMilliseconds ?? -1);
    }

    private static long ReadNumber(MediaReaders readers, object media, int index) => readers.Numbers[index]?.Invoke(media) ?? -1;

    private static bool BindExistingMediaContext()
    {
        var dispatcher = _dispatcher;
        if (dispatcher == null || !dispatcher.CheckAccess()) return false;
        var media = _readReservedContext?.Invoke(dispatcher);
        if (media?.GetType().FullName != "System.Windows.Media.MediaContext")
        {
            if (!_reportedNoContext)
            {
                _reportedNoContext = true;
                Emit("deep.media.unavailable", detail: "No readable existing Dispatcher.Reserved0 MediaContext");
            }
            return false;
        }
        if (ReferenceEquals(media, _media)) return _readers != null;
        DetachCommittingHandler();
        _media = media;
        _reportedNoContext = false;
        var readers = new MediaReaders();
        var type = media.GetType();
        for (var index = 0; index < NumericFields.Length; index++)
        {
            readers.Numbers[index] = MakeNumericReader(type, NumericFields[index]);
            if (readers.Numbers[index] != null) readers.NumberMask |= 1L << index;
            Emit("deep.media.field", value1: index, value2: readers.Numbers[index] == null ? 0 : 1, detail: NumericFields[index]);
        }
        for (var index = 0; index < TimerFields.Length; index++)
        {
            readers.Timers[index] = MakeFieldReader<object, DispatcherTimer?>(type.GetField(TimerFields[index], BindingFlags.Instance | BindingFlags.NonPublic));
            if (readers.Timers[index] != null) readers.ObjectMask |= 1L << index;
            Emit("deep.media.field", value1: 16 + index, value2: readers.Timers[index] == null ? 0 : 1, detail: TimerFields[index]);
        }
        readers.CurrentOperation = MakeFieldReader<object, DispatcherOperation?>(type.GetField("_currentRenderOp", BindingFlags.Instance | BindingFlags.NonPublic));
        if (readers.CurrentOperation != null) readers.ObjectMask |= 8;
        Emit("deep.media.field", value1: 19, value2: readers.CurrentOperation == null ? 0 : 1, detail: "_currentRenderOp");
        _readers = readers;
        Emit("deep.media.available", value1: readers.NumberMask, value2: readers.ObjectMask,
            detail: type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? type.Assembly.FullName);
        AttachCommittingHandler(type, media);
        return true;
    }

    private static void AttachCommittingHandler(Type type, object media)
    {
        try
        {
            // Unlike RenderComplete.add, this field-like event does not reset commit state.
            // Its callback precedes Channel.Commit; it does NOT report physical presentation.
            var notification = type.GetEvent("CommittingBatch", BindingFlags.Instance | BindingFlags.NonPublic);
            var add = notification?.GetAddMethod(true);
            _removeCommittingHandler = notification?.GetRemoveMethod(true);
            if (notification?.EventHandlerType == typeof(EventHandler) && add != null && _removeCommittingHandler != null)
            {
                add.Invoke(media, [new EventHandler(OnCommitting)]);
                _committingInstalled = true;
            }
            Emit("deep.media.committing-available", value1: _committingInstalled ? 1 : 0);
        }
        catch (Exception error) { ReportFault("committing-install", error); }
    }

    private static void DetachCommittingHandler()
    {
        try
        {
            if (_committingInstalled && _media != null)
                _removeCommittingHandler?.Invoke(_media, [new EventHandler(OnCommitting)]);
        }
        catch (Exception error) { ReportFault("committing-remove", error); }
        finally { _committingInstalled = false; _removeCommittingHandler = null; }
    }

    private static void OnCommitting(object? sender, EventArgs args) => Snapshot("committing-before-channel-commit");

    private static Func<object, long>? MakeNumericReader(Type type, string name)
    {
        try
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic);
            if (field == null) return null;
            var input = Expression.Parameter(typeof(object));
            // WPF's _contextRenderID is static and shared across MediaContexts. Read it
            // without a target; it counts render walks, not channel commits or presents.
            Expression value = Expression.Field(field.IsStatic ? null : Expression.Convert(input, type), field);
            if (value.Type == typeof(TimeSpan)) value = Expression.Property(value, nameof(TimeSpan.Ticks));
            if (value.Type == typeof(bool)) value = Expression.Condition(value, Expression.Constant(1L), Expression.Constant(0L));
            return Expression.Lambda<Func<object, long>>(Expression.Convert(value, typeof(long)), input).Compile();
        }
        catch { return null; }
    }

    private static Func<TSource, TResult>? MakeFieldReader<TSource, TResult>(FieldInfo? field)
    {
        try
        {
            if (field?.DeclaringType == null) return null;
            var input = Expression.Parameter(typeof(TSource));
            var value = Expression.Field(Expression.Convert(input, field.DeclaringType), field);
            return Expression.Lambda<Func<TSource, TResult>>(Expression.Convert(value, typeof(TResult)), input).Compile();
        }
        catch { return null; }
    }

    private static Func<TSource, TResult>? MakePropertyReader<TSource, TResult>(PropertyInfo? property)
    {
        try
        {
            if (property?.DeclaringType == null || property.GetGetMethod(true) == null) return null;
            var input = Expression.Parameter(typeof(TSource));
            var value = Expression.Property(Expression.Convert(input, property.DeclaringType), property);
            return Expression.Lambda<Func<TSource, TResult>>(Expression.Convert(value, typeof(TResult)), input).Compile();
        }
        catch { return null; }
    }

    private static void ReportFault(string stage, Exception error)
    {
        if (Interlocked.Exchange(ref _faultReported, 1) == 0)
            Emit("deep.dispatcher.error", detail: stage + ":" + error.GetType().FullName);
    }

    private static void Emit(string name, long id = 0, long value1 = 0, long value2 = 0,
        long value3 = 0, long value4 = 0, double number1 = 0, double number2 = 0, string? detail = null)
    {
        try { EdgeCapsulePerformanceDiagnostics.Event(name, id, value1, value2, value3, value4, number1, number2, detail); }
        catch { /* Diagnostics must not interrupt dispatcher or channel processing. */ }
    }
}
#endif
