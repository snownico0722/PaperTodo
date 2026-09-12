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
internal sealed class MarkdownEdgePreviewPreload
{
    private static readonly ConditionalWeakTable<Dispatcher, MarkdownEdgePreviewPreload> Instances = new();
    internal static MarkdownEdgePreviewPreload For(Dispatcher dispatcher) =>
        Instances.GetValue(dispatcher, value => new(value));

    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<EdgeCapsulePreviewInvalidationSource, MarkdownEdgeCapsulePreviewRenderer.PreviewContent> _excerpts = new();
    private readonly Dictionary<EdgeCapsulePreviewInvalidationSource, ArtifactEntry> _artifacts = new();
    private readonly Dictionary<EdgeCapsulePreviewInvalidationSource, Func<Target?>> _pendingLayout = new();
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _work;
    private EdgeCapsulePreviewInvalidationSource? _workingSource;
    private bool _enabled = true;
    internal int ExcerptCount => _excerpts.Count;
    internal int ArtifactCount => _artifacts.Count;
    internal int PendingCount => _pendingLayout.Count;
    internal long ArtifactHits { get; private set; }
    internal long WarmCompletions { get; private set; }

    internal sealed record Target(EdgeCapsulePreviewContext Context, Panel Anchor,
        EdgeCapsulePreviewSize Size, Func<bool> StillEligible);
    internal sealed record Binding(MarkdownEdgePreviewPreload Owner,
        EdgeCapsulePreviewInvalidationSource Source, long Version,
        MarkdownEdgeCapsulePreviewRenderer.PreviewContent Content, double Zoom,
        Action<string> OpenExternal)
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
        _debounce.Tick += (_, _) => { _debounce.Stop(); Drain(); };
        dispatcher.ShutdownStarted += (_, _) => Clear();
    }

    // Preload policy is intentionally broader than the renderer's former paragraph-path threshold.
    // Whole-preview work is worthwhile when the bounded excerpt is large, has substantial styled
    // coverage, or has several distinct styled/link pieces. Thresholds are strict.
    internal static bool IsClearlyHighLoad(MarkdownEdgeCapsulePreviewRenderer.PreviewContent content)
    {
        if (content.IsEmpty) return false;
        var totalCharacters = content.Lines.Sum(line => line.Text.Length);
        if (totalCharacters > 400) return true;
        if (totalCharacters <= 200 || content.RenderMode == MarkdownRenderModes.Off) return false;

        var styledCharacters = 0;
        var styledPieces = 0;
        foreach (var line in content.Lines)
        {
            if (line.FenceKind is MarkdownFenceLineKind.Opening or MarkdownFenceLineKind.Closing)
                continue;
            if (line.WasInsideFence)
            {
                if (line.Text.Length > 0)
                {
                    styledCharacters += line.Text.Length;
                    styledPieces++;
                }
            }
            else
            {
                foreach (var piece in content.Inlines.Get(line.Text, MarkdownRenderModes.Full).Pieces)
                {
                    // Count semantic content, never Enhanced-mode delimiter/URL styling.
                    if ((piece.Style & ~MarkdownEdgeCapsulePreviewRenderer.InlineStyle.Syntax) == 0 && piece.Link == null)
                        continue;
                    styledCharacters += piece.Text.Length;
                    styledPieces++;
                }
            }
            if (styledCharacters > 100 || styledPieces > 3) return true;
        }
        return false;
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
            entry.RenderMode == candidate.RenderMode && entry.Truncated == candidate.Truncated &&
            entry.Lines.SequenceEqual(candidate.Lines)) return entry;
        _artifacts.Remove(source);
        _excerpts.Remove(source);
        if (IsClearlyHighLoad(candidate)) _excerpts[source] = candidate;
        return candidate;
    }

    internal Binding? Bind(EdgeCapsulePreviewContext context,
        MarkdownEdgeCapsulePreviewRenderer.PreviewContent content, double zoom) =>
        _enabled && _excerpts.TryGetValue(context.InvalidationSource, out var entry) && ReferenceEquals(entry, content)
            ? new(this, context.InvalidationSource, context.InvalidationSource.Version,
                content, zoom, context.OpenExternal) : null;

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
                !brush.Transform.Value.IsIdentity || !brush.RelativeTransform.Value.IsIdentity) return null;
            stamps.Add(brush.Color + ":" + brush.Opacity.ToString("R", CultureInfo.InvariantCulture));
        }
        if (Theme.SyntaxFadeBrush is not SolidColorBrush syntax || syntax.HasAnimatedProperties ||
            !syntax.Transform.Value.IsIdentity || !syntax.RelativeTransform.Value.IsIdentity) return null;
        stamps.Add(syntax.Color + ":" + syntax.Opacity.ToString("R", CultureInfo.InvariantCulture));
        stamps.Add(string.Join("|", NoteTypography.FontFamily.Source, NoteTypography.FontFamily.BaseUri,
            NoteTypography.CodeFontFamily.Source, NoteTypography.CodeFontFamily.BaseUri,
            AppTypography.FontFamilyFor(content: true, bold: true).Source,
            AppTypography.FontFamilyFor(content: true, bold: true).BaseUri,
            AppTypography.FontWeightFor(true), AppTypography.UsesCustomBoldFace(true),
            NoteTypography.FontWeight, NoteTypography.FontStyle, NoteTypography.FontStretch,
            NoteTypography.Language.IetfLanguageTag, NoteTypography.HeadingFontWeight, AppTypography.TextFormattingMode,
            NoteTypography.FontSize, NoteTypography.CodeFontSize,
            NoteTypography.Heading1FontSize, NoteTypography.Heading2FontSize, NoteTypography.Heading3FontSize,
            AppTypography.Scale(1), surface.Language.IetfLanguageTag, surface.FlowDirection,
            TextOptions.GetTextRenderingMode(surface), TextOptions.GetTextHintingMode(surface)));
        // Whole artifacts are height-independent inside the current card envelope. Demand clips
        // the same immutable drawing to its actual row height and owns the overflow indicator.
        return new(binding, new Size(size.Width, 0), dpi, string.Join("|", stamps));
    }

    internal bool TryCreateSurface(Key key, out MarkdownPreviewArtifactSurface? surface, bool demand = true)
    {
        _dispatcher.VerifyAccess();
        surface = null;
        if (!key.Binding.Current || !_artifacts.TryGetValue(key.Binding.Source, out var candidate)) return false;
        if (!candidate.Key.Binding.Current)
        {
            _artifacts.Remove(key.Binding.Source);
            return false;
        }
        if (candidate.Key != key) return false;

        // The artifact stays immutable and cached. Native input elements belong only to this mount.
        surface = new MarkdownPreviewArtifactSurface(candidate.Artifact, key.Binding.OpenExternal)
        {
            IsHitTestVisible = false
        };
        if (demand) ArtifactHits++;
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
    }

    internal void Forget(EdgeCapsulePreviewInvalidationSource source)
    {
        _dispatcher.VerifyAccess();
        _excerpts.Remove(source); _artifacts.Remove(source); _pendingLayout.Remove(source);
        if (ReferenceEquals(_workingSource, source)) _work?.Cancel();
        if (PendingCount == 0) _debounce.Stop();
    }

    internal void RequestLayout(EdgeCapsulePreviewInvalidationSource source, Func<Target?> read)
    {
        _dispatcher.VerifyAccess();
        if (!_enabled || _dispatcher.HasShutdownStarted) return;
        // Keep only the newest request. Capturing/classifying text happens after the debounce,
        // never on a keystroke or pointer callback. A running drain cannot bypass a new 500ms wait.
        _pendingLayout[source] = read;
        _work?.Cancel();
        Arm();
    }

    private void Arm() { _debounce.Stop(); _debounce.Start(); }

    internal void BeginDemand()
    {
        _dispatcher.VerifyAccess();
        _work?.Cancel();
        _debounce.Stop();
        if (PendingCount > 0) Arm();
    }

    private async void Drain()
    {
        if (!_enabled || _work != null || _dispatcher.HasShutdownStarted) return;
        using var work = new CancellationTokenSource();
        _work = work;
        try
        {
            while (!work.IsCancellationRequested && PendingCount > 0)
            {
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                if (work.IsCancellationRequested || PendingCount == 0) break;
                var pair = _pendingLayout.First();
                _workingSource = pair.Key;
                try
                {
                    var target = pair.Value();
                    if (target != null) await WarmLayoutAsync(target, work.Token);
                }
                catch (OperationCanceledException) when (work.IsCancellationRequested) { }
                catch (Exception ex) { Trace.TraceWarning("Markdown preview preload failed: {0}", ex.GetType().Name); }
                finally
                {
                    _workingSource = null;
                    // Retain interrupted work. A newer edit replaces the delegate and must not
                    // be removed by the completion of the older request. Failed/ineligible work
                    // is retired, not polled forever; host/content lifecycle supplies a new event.
                    if (!work.IsCancellationRequested &&
                        _pendingLayout.TryGetValue(pair.Key, out var current) && ReferenceEquals(current, pair.Value))
                        _pendingLayout.Remove(pair.Key);
                }
            }
        }
        finally
        {
            _work = null;
            if (PendingCount > 0 && !_dispatcher.HasShutdownStarted && !_debounce.IsEnabled) Arm();
        }
    }

    internal async Task<bool> WarmLayoutAsync(Target target, CancellationToken cancellation = default)
    {
        _dispatcher.VerifyAccess();
        if (!_enabled || cancellation.IsCancellationRequested || !target.StillEligible() ||
            !target.Anchor.IsLoaded || !target.Anchor.IsVisible) return false;

        var version = target.Context.InvalidationSource.Version;
        var content = Capture(target.Context);
        if (!IsClearlyHighLoad(content)) return false;
        var binding = Bind(target.Context, content, target.Context.Paper.TextZoom);
        var width = MarkdownEdgeCapsulePreviewRenderer.ArtifactBodyWidth(target.Size);
        var key = MakeKey(binding, target.Anchor, new Size(width, 0));
        if (key == null) return false;
        if (_artifacts.TryGetValue(key.Binding.Source, out var current) && current.Key == key) return true;

        // Capture resources, inline values and DPI once on the owning Dispatcher. After this point
        // the shared STA sees only immutable values/frozen Freezables; no hidden WPF host is built.
        var plan = MarkdownEdgeCapsulePreviewRenderer.CaptureArtifactPlan(
            target.Anchor, content, width, key.Binding.Zoom);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        void CancelLifetime() => lifetime.Cancel();
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
                target.Context.InvalidationSource.Invalidated -= invalidated;
                target.Anchor.Unloaded -= unloaded;
                target.Anchor.IsVisibleChanged -= visibilityChanged;
            }
            if (_dispatcher.CheckAccess()) Detach();
            else await _dispatcher.InvokeAsync(Detach, DispatcherPriority.Send);
        }

        try
        {
            MarkdownEdgeCapsulePreviewRenderer.MarkdownPreviewArtifactDraft draft;
            try
            {
                draft = await MarkdownEdgeCapsulePreviewRenderer.BuildArtifactDraftAsync(
                    plan, speculative: true, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                return false;
            }
            cancellation.ThrowIfCancellationRequested();

            // Do not rely on a DispatcherSynchronizationContext being installed. Tests and some
            // host paths can await the worker without one, so every WPF read and final composition
            // explicitly returns to the owning Dispatcher.
            var operation = _dispatcher.InvokeAsync(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (lifetime.IsCancellationRequested || !_enabled || !target.StillEligible() || target.Context.InvalidationSource.Version != version ||
                    !target.Anchor.IsLoaded || !target.Anchor.IsVisible || !key.Binding.Current ||
                    MakeKey(key.Binding, target.Anchor, new Size(width, 0)) != key) return false;

                // Aggregating frozen child drawings is the only final UI-thread operation; no layout,
                // Measure/Arrange or visual-tree publication occurs during speculative preparation.
                var artifact = MarkdownEdgeCapsulePreviewRenderer.ComposeArtifact(draft);
                if (!target.StillEligible() || target.Context.InvalidationSource.Version != version ||
                    !key.Binding.Current || MakeKey(key.Binding, target.Anchor, new Size(width, 0)) != key) return false;
                if (!StoreArtifact(key, artifact)) return false;
                WarmCompletions++;
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
        _pendingLayout.Clear(); _artifacts.Clear(); _excerpts.Clear();
    }

    // Same-binary A/B probe; no settings, environment switch or persistent product option.
    internal void SetEnabledForChecks(bool enabled) { Clear(); _enabled = enabled; }
}
