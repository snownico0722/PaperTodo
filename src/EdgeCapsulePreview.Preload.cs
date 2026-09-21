using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

// Dispatcher-local, discardable prelayout of eligible edge notes. The retained product is one
// immutable whole-preview artifact per source: never a detached WPF body or hidden preview tree.
internal sealed partial class MarkdownEdgePreviewPreload
{
    private static readonly ConditionalWeakTable<Dispatcher, MarkdownEdgePreviewPreload> Instances = new();
    internal static MarkdownEdgePreviewPreload For(Dispatcher dispatcher) =>
        Instances.GetValue(dispatcher, value => new(value));

    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<EdgeCapsulePreviewInvalidationSource, MarkdownEdgeCapsulePreviewRenderer.PreviewContent> _excerpts = new();
    private readonly Dictionary<EdgeCapsulePreviewInvalidationSource, ArtifactEntry> _artifacts = new();
    private readonly Dictionary<EdgeCapsulePreviewInvalidationSource, Func<ReadResult>> _pendingLayout = new();
    private readonly HashSet<EdgeCapsulePreviewInvalidationSource> _deferred = new();
    private readonly DispatcherTimer _debounce;
    private Task _drainTask = Task.CompletedTask;
    private CancellationTokenSource? _work;
    private EdgeCapsulePreviewInvalidationSource? _workingSource;
    private bool _enabled = true;
    internal int ExcerptCount => _excerpts.Count;
    internal int ArtifactCount => _artifacts.Count;
    internal int PendingCount => _pendingLayout.Count + (_selectionDirty ? 1 : 0);
    internal int DeferredCount => _deferred.Count;
    private int RunnableCount => PendingCount - DeferredCount;
    internal long ArtifactHits { get; private set; }
    internal long WarmCompletions { get; private set; }

    internal enum Readiness { Discard, Deferred, Ready }
    internal readonly record struct ReadResult(Readiness State, Target? Target = null)
    {
        internal static ReadResult Ready(Target target) => new(Readiness.Ready, target);
        internal static ReadResult Deferred => new(Readiness.Deferred);
        internal static ReadResult Discard => default;
    }

    internal sealed record Target(EdgeCapsulePreviewContext Context, Panel Anchor,
        EdgeCapsulePreviewSize Size, Func<bool> StillEligible,
        MarkdownEdgeCapsulePreviewRenderer.PreviewContent Content);
    internal sealed record Binding(MarkdownEdgePreviewPreload Owner,
        EdgeCapsulePreviewInvalidationSource Source, long Version,
        MarkdownEdgeCapsulePreviewRenderer.PreviewContent Content, double Zoom)
    {
        internal bool Current => Owner._enabled && Source.Version == Version &&
            Owner._excerpts.TryGetValue(Source, out var current) && ReferenceEquals(current, Content);
    }
    // Height is canonicalized to zero by MakeKey. The artifact is prepared through the current
    // 410-DIP card envelope and is clipped by the real viewport, so only layout width is reusable geometry.
    internal sealed record Key(Binding Binding, Size Size, DpiScale Dpi, string Appearance);
    private sealed record ArtifactEntry(Key Key, MarkdownPreviewArtifact Artifact);

    private MarkdownEdgePreviewPreload(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        // One-shot editing/interest debounce, stopped as soon as it fires; no idle polling.
        _debounce = new DispatcherTimer(DispatcherPriority.ContextIdle, dispatcher)
        { Interval = TimeSpan.FromMilliseconds(500) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _debounce.Interval = TimeSpan.FromMilliseconds(500); _ = DrainPendingAsync(); };
        dispatcher.ShutdownStarted += (_, _) => Clear();
    }

    internal MarkdownEdgeCapsulePreviewRenderer.PreviewContent Capture(EdgeCapsulePreviewContext context)
    {
        _dispatcher.VerifyAccess();
        // Check the bounded excerpt even when a caller forgot to invalidate. Never retain the
        // entire editor string or match only by paper ID/version when the visible text changed.
        var candidate = MarkdownEdgeCapsulePreviewRenderer.CaptureContent(
            context.ReadMarkdownText(), context.ReadMarkdownRenderMode());
        if (!_enabled) return candidate;
        var source = context.InvalidationSource;
        // Compare the bounded source BEFORE classifying: unchanged previews must not reparse.
        if (_excerpts.TryGetValue(source, out var entry) &&
            SameContent(entry, candidate)) return entry;
        _artifacts.Remove(source);
        _excerpts.Remove(source);
        if (_candidates.TryGetValue(source, out var registered))
        {
            if (SameContent(registered.Content, candidate)) candidate = registered.Content!;
            else
            {
                // Demand remains valid independently of admission. Reconcile an unannounced
                // text change later, through the same idle/debounce path as ordinary edits.
                _selectionDirty = true;
                _work?.Cancel();
                Arm();
            }
        }
        if (ShouldPreload(context, candidate)) _excerpts[source] = candidate;
        return candidate;
    }

    internal Binding? Bind(EdgeCapsulePreviewContext context,
        MarkdownEdgeCapsulePreviewRenderer.PreviewContent content, double zoom) =>
        _enabled && _excerpts.TryGetValue(context.InvalidationSource, out var entry) && ReferenceEquals(entry, content)
            ? new(this, context.InvalidationSource, context.InvalidationSource.Version,
                content, zoom) : null;

    internal static Key? MakeKey(Binding? binding, FrameworkElement surface, Size size)
    {
        if (binding is not { Current: true } || !binding.Owner._enabled ||
            !double.IsFinite(size.Width) || size.Width <= 0) return null;
        var dpi = VisualTreeHelper.GetDpi(surface);
        // Frozen drawings keep concrete resources. Compare values as well as notifications, so
        // replacing/mutating a brush between preload and demand cannot reuse yesterday's colors.
        var stamps = new List<string>();
        foreach (var name in new[] { "TextBrushKey", "WeakTextBrushKey", "LinkBrushKey", "HoverBrushKey", "PaperBorderBrushKey" })
        {
            var resource = surface.TryFindResource(name);
            if (resource == null) { stamps.Add("missing:" + name); continue; }
            if (resource is not SolidColorBrush brush || brush.HasAnimatedProperties ||
                !brush.Transform.Value.IsIdentity || !brush.RelativeTransform.Value.IsIdentity)
            {
                binding.Owner.TracePreload("uncacheable-resource", binding.Source, $"resource={name}");
                return null;
            }
            stamps.Add(brush.Color + ":" + brush.Opacity.ToString("R", CultureInfo.InvariantCulture));
        }
        if (Theme.SyntaxFadeBrush is not SolidColorBrush syntax || syntax.HasAnimatedProperties ||
            !syntax.Transform.Value.IsIdentity || !syntax.RelativeTransform.Value.IsIdentity) return null;
        stamps.Add(syntax.Color + ":" + syntax.Opacity.ToString("R", CultureInfo.InvariantCulture));
        stamps.Add(MarkdownEdgeCapsulePreviewRenderer.TypographyStamp(surface));
        // Whole artifacts are height-independent inside the current card envelope. Demand clips
        // the same immutable drawing to its actual row height and owns the overflow indicator.
        return new(binding, new Size(size.Width, 0), dpi, string.Join("|", stamps));
    }

    internal bool TryGetArtifact(Key key, out MarkdownPreviewArtifact? artifact, bool demand = true)
    {
        _dispatcher.VerifyAccess();
        artifact = null;
        if (!key.Binding.Current || !_artifacts.TryGetValue(key.Binding.Source, out var candidate))
        {
            if (demand) TracePreload("miss", key.Binding.Source, $"reason={(key.Binding.Current ? "absent" : "binding-stale")} width={key.Size.Width:R}");
            return false;
        }
        if (!candidate.Key.Binding.Current) { _artifacts.Remove(key.Binding.Source); return false; }
        if (candidate.Key != key)
        {
            if (demand) TracePreload("miss", key.Binding.Source,
                $"reason=key cachedWidth={candidate.Key.Size.Width:R} width={key.Size.Width:R} " +
                $"bindingEqual={candidate.Key.Binding == key.Binding} dpiEqual={candidate.Key.Dpi.Equals(key.Dpi)} " +
                $"appearanceEqual={candidate.Key.Appearance == key.Appearance}");
            return false;
        }
        artifact = candidate.Artifact;
        if (demand) { ArtifactHits++; TracePreload("hit", key.Binding.Source, $"width={key.Size.Width:R}"); }
        return true;
    }

    private bool StoreArtifact(Key key, MarkdownPreviewArtifact artifact)
    {
        if (!key.Binding.Current) return false;
        _artifacts[key.Binding.Source] = new(key, artifact);
        return true;
    }

    internal void Invalidate(EdgeCapsulePreviewInvalidationSource source)
    {
        _dispatcher.VerifyAccess();
        _artifacts.Remove(source);
        TracePreload("invalidate", source);
    }

    internal void Forget(EdgeCapsulePreviewInvalidationSource source)
    {
        _dispatcher.VerifyAccess();
        _excerpts.Remove(source); _artifacts.Remove(source); _pendingLayout.Remove(source); _deferred.Remove(source);
        if (_candidates.Remove(source)) _selectionDirty = _candidates.Count > 0;
        if (ReferenceEquals(_workingSource, source)) _work?.Cancel();
        if (RunnableCount == 0) _debounce.Stop();
        else if (_work == null) Arm();
        TracePreload("forget", source);
    }

    internal void RequestLayout(EdgeCapsulePreviewInvalidationSource source, Func<ReadResult> read)
    {
        _dispatcher.VerifyAccess();
        if (!_enabled || _dispatcher.HasShutdownStarted) return;
        // Keep only the newest request. Capturing/classifying text happens after the debounce,
        // never on a keystroke or pointer callback. A running drain cannot bypass a new 500ms wait.
        _pendingLayout[source] = read;
        _deferred.Remove(source);
        _work?.Cancel();
        Arm();
        TracePreload("request", source);
    }

    internal void Resume(EdgeCapsulePreviewInvalidationSource source)
    {
        _dispatcher.VerifyAccess();
        if (!_enabled || _dispatcher.HasShutdownStarted) return;
        var sameSourceRunning = ReferenceEquals(_workingSource, source);
        var resumed = _deferred.Remove(source);
        if (!resumed && !sameSourceRunning) return;
        // A host can become ready inside its reader, before Deferred is recorded. Restart only
        // that source; waking A must not cancel useful work already running for B.
        if (sameSourceRunning) { _work?.Cancel(); Arm(); }
        else if (_work == null) Arm();
    }

    private void Arm()
    {
        _debounce.Stop();
        _debounce.Interval = TimeSpan.FromMilliseconds(500);
        if (RunnableCount > 0) _debounce.Start();
    }

    internal Task StartStartupWork()
    {
        _dispatcher.VerifyAccess();
        if (!_enabled || _dispatcher.HasShutdownStarted) return Task.CompletedTask;
        if (RunnableCount == 0) return _drainTask;
        // The first stable batch uses the normal renderer/drain, without the editing debounce.
        // Await only this pass: a user edit can cancel it and retain its ordinary 500ms delay.
        _debounce.Stop();
        return DrainPendingAsync();
    }

    private Task DrainPendingAsync()
    {
        if (!_drainTask.IsCompleted) return _drainTask;
        return _drainTask = DrainAsync();
    }

    internal void BeginDemand()
    {
        _dispatcher.VerifyAccess();
        _work?.Cancel();
        _debounce.Stop();
        if (RunnableCount > 0) Arm();
    }

    private async Task DrainAsync()
    {
        if (!_enabled || _work != null || _dispatcher.HasShutdownStarted) return;
        using var work = new CancellationTokenSource();
        _work = work;
        try
        {
            while (!work.IsCancellationRequested && RunnableCount > 0)
            {
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                if (work.IsCancellationRequested || RunnableCount == 0) break;
                if (_selectionDirty) await RefreshSelectionAsync(work.Token);
                if (work.IsCancellationRequested) break;
                if (_pendingLayout.Count == _deferred.Count) continue;
                var pair = _pendingLayout.First(item => !_deferred.Contains(item.Key));
                var defer = false;
                _workingSource = pair.Key;
                try
                {
                    var read = pair.Value();
                    TracePreload("read", pair.Key, $"state={read.State}");
                    defer = read.State == Readiness.Deferred;
                    if (read.State == Readiness.Ready && read.Target is { } target)
                    {
                        var prepared = await WarmLayoutAsync(target, work.Token);
                        defer = !prepared && (!target.StillEligible() ||
                            !target.Anchor.IsLoaded || !target.Anchor.IsVisible);
                    }
                }
                catch (OperationCanceledException) when (work.IsCancellationRequested) { }
                catch (Exception ex) { Trace.TraceWarning("Markdown preview preload failed: {0}", ex.GetType().Name); }
                finally
                {
                    _workingSource = null;
                    // Keep temporarily unavailable sources dormant without polling. A replacement
                    // reader or retired source cannot be overwritten by this older completion.
                    if (!work.IsCancellationRequested &&
                        _pendingLayout.TryGetValue(pair.Key, out var current) && ReferenceEquals(current, pair.Value))
                    {
                        if (defer) _deferred.Add(pair.Key);
                        else _pendingLayout.Remove(pair.Key);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_dispatcher.HasShutdownStarted) { }
        finally
        {
            _work = null;
            if (RunnableCount > 0 && !_dispatcher.HasShutdownStarted && !_debounce.IsEnabled) Arm();
        }
    }

    internal async Task<bool> WarmLayoutAsync(Target target, CancellationToken cancellation = default)
    {
        _dispatcher.VerifyAccess();
        if (!_enabled || cancellation.IsCancellationRequested || !target.StillEligible() ||
            !target.Anchor.IsLoaded || !target.Anchor.IsVisible) return false;

        var version = target.Context.InvalidationSource.Version;
        TracePreload("prepare", target.Context.InvalidationSource,
            $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(target.Context.Paper.Id)} card={target.Size.WidthDip:R}x{target.Size.HeightDip:R}");
        var content = target.Content;
        if (!ShouldPreload(target.Context, content)) return false;
        var binding = Bind(target.Context, content, target.Context.Paper.TextZoom);
        var width = MarkdownEdgeCapsulePreviewRenderer.ArtifactBodyWidth(target.Size, target.Anchor);
        var key = MakeKey(binding, target.Anchor, new Size(width, 0));
        if (key == null) return false;
        if (_artifacts.TryGetValue(key.Binding.Source, out var current) && current.Key == key) return true;

        // Capture resources, inline values and DPI once on the owning Dispatcher. After this point
        // the shared STA sees only immutable values/frozen Freezables; no hidden WPF host is built.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var listening = true;
        void CancelLifetime() { if (listening) lifetime.Cancel(); }
        Action invalidated = CancelLifetime;
        RoutedEventHandler unloaded = (_, _) => CancelLifetime();
        DependencyPropertyChangedEventHandler visibilityChanged = (_, args) =>
        {
            if (args.NewValue is false) CancelLifetime();
        };
        target.Context.InvalidationSource.Invalidated += invalidated;
        target.Anchor.Unloaded += unloaded;
        target.Anchor.IsVisibleChanged += visibilityChanged;

        async Task DetachListenersAsync()
        {
            void Detach()
            {
                listening = false;
                target.Context.InvalidationSource.Invalidated -= invalidated;
                target.Anchor.Unloaded -= unloaded;
                target.Anchor.IsVisibleChanged -= visibilityChanged;
            }
            if (_dispatcher.CheckAccess()) Detach();
            else await _dispatcher.InvokeAsync(Detach, DispatcherPriority.Send);
        }

        try
        {
            MarkdownPreviewArtifact? artifact;
            try
            {
                artifact = await MarkdownEdgeCapsulePreviewRenderer.PrepareArtifactAsync(
                    target.Anchor, content, new Size(width, MarkdownEdgeCapsulePreviewRenderer.ArtifactMaximumBodyHeight),
                    key.Binding.Zoom, speculative: true, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                return false;
            }
            cancellation.ThrowIfCancellationRequested();
            if (artifact == null) return false;

            // Do not rely on a DispatcherSynchronizationContext being installed. Tests and some
            // host paths can await the worker without one, so every WPF read and final composition
            // explicitly returns to the owning Dispatcher.
            var operation = _dispatcher.InvokeAsync(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (lifetime.IsCancellationRequested || !_enabled || !target.StillEligible() || target.Context.InvalidationSource.Version != version ||
                    !target.Anchor.IsLoaded || !target.Anchor.IsVisible || !key.Binding.Current ||
                    MakeKey(key.Binding, target.Anchor, new Size(width, 0)) != key) return false;

                if (!StoreArtifact(key, artifact)) return false;
                WarmCompletions++;
                TracePreload("ready", key.Binding.Source, $"width={width:R} dpi={key.Dpi.DpiScaleX:R}x{key.Dpi.DpiScaleY:R}");
                return true;
            }, DispatcherPriority.ContextIdle);
            return await operation.Task.ConfigureAwait(false);
        }
        finally
        {
            await DetachListenersAsync().ConfigureAwait(false);
        }
    }

    internal void Clear()
    {
        _dispatcher.VerifyAccess();
        _debounce.Stop(); _work?.Cancel();
        _pendingLayout.Clear(); _deferred.Clear(); _artifacts.Clear(); _excerpts.Clear();
        _candidates.Clear(); _selectionDirty = false; _nextCandidateOrder = 0;
    }

    [Conditional("DEBUG")]
    private void TracePreload(string phase, EdgeCapsulePreviewInvalidationSource? source = null, string? detail = null) =>
        EdgeCapsulePerformanceDiagnostics.Trace($"markdown.preload phase={phase} " +
            $"source={(source == null ? 0 : RuntimeHelpers.GetHashCode(source))} version={source?.Version ?? -1} " +
            $"pending={PendingCount} deferred={DeferredCount} artifacts={ArtifactCount} {detail}");

    // Same-binary A/B probe; no settings, environment switch or persistent product option.
    internal void SetEnabledForChecks(bool enabled) { Clear(); _enabled = enabled; }
}
