using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

// One lazy STA for all built-in edge-preview text. It owns formatting, not UI state.
// Dispatcher work is finite and event-driven; no polling timer or per-paper thread is used.
internal sealed class MarkdownLayoutWorker : IDisposable
{
    private const int MaxLinesPerTurn = 4;
    private const double MaxTurnMilliseconds = 1.5;
    private static readonly Lazy<MarkdownLayoutWorker> Instance = new(() => new());
    internal static MarkdownLayoutWorker Shared => Instance.Value;
    internal static int OutstandingRequests => Instance.IsValueCreated ? Instance.Value.OutstandingCount : 0;
    private readonly object _startLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<MarkdownParagraphRequest, Job> _jobs = new(); // worker-owned
    private readonly Dispatcher? _owner;
    private Thread? _thread;
    private bool _stopping;
    private int _outstanding;
    internal int OutstandingCount => Volatile.Read(ref _outstanding);
    internal Task Completion => _stopped.Task;

    internal MarkdownLayoutWorker()
    {
        _owner = Application.Current?.Dispatcher;
        if (_owner != null) _owner.ShutdownStarted += OnOwnerShutdown;
    }

    internal async Task<MarkdownParagraphResult> PrepareAsync(
        MarkdownParagraphRequest request, bool speculative, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _outstanding);
        try
        {
            lock (_startLock)
            {
                if (_stopping) throw new OperationCanceledException(token);
                if (_thread == null)
                {
                    _thread = new Thread(Run) { IsBackground = true, Name = "PaperTodo Markdown layout" };
                    _thread.SetApartmentState(ApartmentState.STA);
                    _thread.Start();
                }
            }
            var dispatcher = await _ready.Task.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var ticket = new Ticket(token);
            // Demand joins/promotes a matching in-flight layout rather than duplicating it.
            // Priority also keeps new demand ahead of older unrelated speculative paragraphs.
            _ = dispatcher.InvokeAsync(() => Enqueue(request, speculative, ticket),
                speculative ? DispatcherPriority.Background : DispatcherPriority.Normal);
            return await ticket.Completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally { Interlocked.Decrement(ref _outstanding); }
    }

    private void Run()
    {
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ready.TrySetResult(dispatcher);
            Dispatcher.Run();
        }
        catch (Exception ex) { _ready.TrySetException(ex); }
        finally
        {
            _lifetime.Cancel();
            foreach (var job in _jobs.Values.ToArray()) Retire(job, null, null);
            _stopped.TrySetResult();
        }
    }

    private sealed class Ticket(CancellationToken token)
    {
        internal CancellationToken Token { get; } = token;
        internal TaskCompletionSource<MarkdownParagraphResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Job(MarkdownParagraphRequest request, bool speculative)
    {
        internal MarkdownParagraphRequest Request { get; } = request;
        internal bool Speculative = speculative;
        internal readonly List<Ticket> Tickets = new();
        internal IEnumerator<MarkdownParagraphResult?>? Steps;
        internal DispatcherOperation? Operation;
    }

    private void Enqueue(MarkdownParagraphRequest request, bool speculative, Ticket ticket)
    {
        if (ticket.Token.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            ticket.Completion.TrySetCanceled(); return;
        }
        if (!_jobs.TryGetValue(request, out var job))
        {
            job = new(request, speculative);
            _jobs.Add(request, job);
        }
        job.Tickets.Add(ticket);
        if (!speculative) job.Speculative = false;
        if (job.Operation is { Status: DispatcherOperationStatus.Pending } pending)
        {
            if (!job.Speculative) pending.Priority = DispatcherPriority.Normal;
        }
        else Schedule(job);
    }

    private void Schedule(Job job) => job.Operation = Dispatcher.CurrentDispatcher.InvokeAsync(
        () => Advance(job), job.Speculative ? DispatcherPriority.Background : DispatcherPriority.Normal);

    private void Advance(Job job)
    {
        job.Operation = null;
        if (_lifetime.IsCancellationRequested || job.Tickets.All(ticket => ticket.Token.IsCancellationRequested))
        {
            Retire(job, null, null); return;
        }
        try
        {
            job.Steps ??= MarkdownParagraphLayout.Prepare(job.Request).GetEnumerator();
            MarkdownParagraphResult? result = null;
            var cancelled = false;
            var turnStarted = Stopwatch.GetTimestamp();
            // This is a dedicated worker Dispatcher. Batch a few visible lines per turn instead of
            // paying one DispatcherOperation per line, while keeping a short priority/cancel boundary.
            using (Dispatcher.CurrentDispatcher.DisableProcessing())
            {
                for (var line = 0; line < MaxLinesPerTurn; line++)
                {
                    if (_lifetime.IsCancellationRequested || job.Tickets.All(ticket => ticket.Token.IsCancellationRequested))
                    {
                        cancelled = true;
                        break;
                    }
                    if (!job.Steps.MoveNext()) throw new InvalidOperationException("Paragraph ended without a result.");
                    if (job.Steps.Current is { } completed)
                    {
                        result = completed;
                        break;
                    }
                    if (Stopwatch.GetElapsedTime(turnStarted).TotalMilliseconds >= MaxTurnMilliseconds) break;
                }
            }
            if (cancelled) Retire(job, null, null);
            else if (result != null) Retire(job, result, null);
            else Schedule(job);
        }
        catch (Exception ex) { Retire(job, null, ex); }
    }

    private void Retire(Job job, MarkdownParagraphResult? result, Exception? error)
    {
        _jobs.Remove(job.Request);
        job.Operation?.Abort();
        try { job.Steps?.Dispose(); }
        catch (Exception ex) { error ??= ex; }
        foreach (var ticket in job.Tickets)
        {
            if (ticket.Token.IsCancellationRequested || _lifetime.IsCancellationRequested)
                ticket.Completion.TrySetCanceled();
            else if (error != null) ticket.Completion.TrySetException(error);
            else if (result != null) ticket.Completion.TrySetResult(result);
            else ticket.Completion.TrySetCanceled();
        }
    }

    private void OnOwnerShutdown(object? sender, EventArgs e) => Dispose();
    public void Dispose()
    {
        lock (_startLock)
        {
            if (_stopping) return;
            _stopping = true;
            _lifetime.Cancel();
            if (_owner != null) _owner.ShutdownStarted -= OnOwnerShutdown;
            if (_thread == null) { _stopped.TrySetResult(); return; }
        }
        StopAsync(); // no Join/Wait on the UI; the worker disposes its own formatting objects
    }
    private async void StopAsync()
    {
        try
        {
            var dispatcher = await _ready.Task.ConfigureAwait(false);
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }
        catch { /* startup failure is already delivered through _ready */ }
    }
}
