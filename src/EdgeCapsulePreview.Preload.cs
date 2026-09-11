using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

// Dispatcher-local, disposable speculative work. Never a new preview/animation authority.
// Only content that already qualifies for the renderer's expensive prepared-paragraph path is
// retained. There is deliberately no paper-count/LRU cap: light notes do not enter this cache,
// while heavy notes keep one current excerpt/body each until invalidated or their window closes.
internal sealed class MarkdownEdgePreviewPreload
{
    private static readonly ConditionalWeakTable<Dispatcher, MarkdownEdgePreviewPreload> Instances = new();
    internal static MarkdownEdgePreviewPreload For(Dispatcher dispatcher) =>
        Instances.GetValue(dispatcher, value => new(value));

    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<EdgeCapsulePreviewInvalidationSource, Excerpt> _excerpts = new();
    private readonly LinkedList<Body> _bodies = new();
    // Kept only for the same-binary diagnostic that proves text-only warming is not generally
    // useful. Product scheduling does not call RequestText.
    private readonly Dictionary<EdgeCapsulePreviewInvalidationSource, Func<EdgeCapsulePreviewContext?>> _pendingText = new();
    private readonly LinkedList<(EdgeCapsulePreviewInvalidationSource Source, Func<Target?> Read)> _pendingLayout = new();
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _work;
    private long _clock;
    private bool _enabled = true;
    internal int ExcerptCount => _excerpts.Count;
    internal int BodyCount => _bodies.Count;
    internal int PendingCount => _pendingText.Count + _pendingLayout.Count;
    internal long BodyHits { get; private set; }
    internal long WarmCompletions { get; private set; }

    internal sealed record Target(EdgeCapsulePreviewContext Context, Panel Anchor,
        EdgeCapsulePreviewSize Size, Func<bool> StillEligible);
    private sealed record Excerpt(MarkdownEdgeCapsulePreviewRenderer.PreviewContent Content, long Touched);
    internal sealed record Binding(MarkdownEdgePreviewPreload Owner,
        EdgeCapsulePreviewInvalidationSource Source, long Version,
        MarkdownEdgeCapsulePreviewRenderer.PreviewContent Content, double Zoom)
    {
        internal bool Current => Source.Version == Version;
    }
    internal sealed record Key(Binding Binding, Size Size, DpiScale Dpi, string Appearance);
    internal sealed record Body(Key Key, StackPanel Panel, bool Truncated);

    private MarkdownEdgePreviewPreload(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        // One-shot editing/interest debounce, stopped as soon as it fires; no idle polling.
        _debounce = new DispatcherTimer(DispatcherPriority.ContextIdle, dispatcher)
        { Interval = TimeSpan.FromMilliseconds(500) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Drain(); };
        dispatcher.ShutdownStarted += (_, _) => Clear();
    }

    // Preload policy is intentionally broader than the renderer's paragraph-path threshold.
    // Complete idle layout is worthwhile when the bounded excerpt is large, has substantial
    // styled coverage, or has several distinct styled/link pieces. Thresholds are strict.
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
                foreach (var piece in content.Inlines.Get(line.Text, content.RenderMode).Pieces)
                {
                    if (piece.Style == MarkdownEdgeCapsulePreviewRenderer.InlineStyle.None && piece.Link == null)
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
        if (!IsClearlyHighLoad(candidate))
        {
            _excerpts.Remove(source);
            ForgetBodies(source);
            return candidate;
        }
        if (_excerpts.TryGetValue(source, out var entry) &&
            entry.Content.RenderMode == candidate.RenderMode && entry.Content.Truncated == candidate.Truncated &&
            entry.Content.Lines.SequenceEqual(candidate.Lines))
        {
            _excerpts[source] = entry with { Touched = ++_clock };
            return entry.Content;
        }
        ForgetBodies(source);
        _excerpts[source] = new(candidate, ++_clock);
        return candidate;
    }

    internal Binding? Bind(EdgeCapsulePreviewContext context,
        MarkdownEdgeCapsulePreviewRenderer.PreviewContent content, double zoom) =>
        _enabled ? new(this, context.InvalidationSource, context.InvalidationSource.Version, content, zoom) : null;

    internal static Key? MakeKey(Binding? binding, FrameworkElement surface, Size size)
    {
        if (binding is not { Current: true } || !binding.Owner._enabled) return null;
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
        if (Theme.SyntaxFadeBrush is not SolidColorBrush syntax || syntax.HasAnimatedProperties) return null;
        stamps.Add(syntax.Color + ":" + syntax.Opacity.ToString("R", CultureInfo.InvariantCulture));
        stamps.Add(string.Join("|", NoteTypography.FontFamily.Source, NoteTypography.CodeFontFamily.Source,
            AppTypography.FontFamilyFor(content: true, bold: true).Source,
            AppTypography.FontWeightFor(true), AppTypography.UsesCustomBoldFace(true),
            NoteTypography.FontWeight, NoteTypography.FontStyle, NoteTypography.FontStretch,
            NoteTypography.Language.IetfLanguageTag, NoteTypography.HeadingFontWeight, AppTypography.TextFormattingMode,
            NoteTypography.FontSize, NoteTypography.CodeFontSize,
            NoteTypography.Heading1FontSize, NoteTypography.Heading2FontSize, NoteTypography.Heading3FontSize,
            AppTypography.Scale(1), surface.Language.IetfLanguageTag, surface.FlowDirection));
        return new(binding, size, dpi, string.Join("|", stamps));
    }

    internal bool TryTake(Key key, out Body? body, bool demand = true)
    {
        _dispatcher.VerifyAccess();
        body = null;
        for (var node = _bodies.First; node != null;)
        {
            var next = node.Next;
            if (!node.Value.Key.Binding.Current) _bodies.Remove(node);
            else if (node.Value.Key == key)
            {
                body = node.Value;
                _bodies.Remove(node);
                if (demand) BodyHits++;
                return true;
            }
            node = next;
        }
        return false;
    }

    internal bool Store(Body body)
    {
        _dispatcher.VerifyAccess();
        if (!_enabled || !body.Key.Binding.Current || body.Panel.Parent != null ||
            !IsClearlyHighLoad(body.Key.Binding.Content) ||
            !_excerpts.TryGetValue(body.Key.Binding.Source, out var current) ||
            !ReferenceEquals(current.Content, body.Key.Binding.Content)) return false;
        // Exactly one completed body per heavy note. Count is determined by heavy-note count, not
        // mouse prediction or a recent-items LRU.
        ForgetBodies(body.Key.Binding.Source);
        _bodies.AddFirst(body);
        return true;
    }

    private void ForgetBodies(EdgeCapsulePreviewInvalidationSource source)
    {
        for (var node = _bodies.First; node != null;)
        {
            var next = node.Next;
            if (ReferenceEquals(node.Value.Key.Binding.Source, source)) _bodies.Remove(node);
            node = next;
        }
    }

    internal void Invalidate(EdgeCapsulePreviewInvalidationSource source)
    {
        _dispatcher.VerifyAccess();
        ForgetBodies(source);
        // Pure text may still be identical after a theme/title update. Capture will compare it.
    }

    internal void Forget(EdgeCapsulePreviewInvalidationSource source)
    {
        _dispatcher.VerifyAccess();
        _excerpts.Remove(source); _pendingText.Remove(source); ForgetBodies(source);
        for (var node = _pendingLayout.First; node != null;)
        {
            var next = node.Next;
            if (ReferenceEquals(node.Value.Source, source)) _pendingLayout.Remove(node);
            node = next;
        }
        if (PendingCount == 0) _debounce.Stop();
    }

    internal void RequestText(EdgeCapsulePreviewInvalidationSource source, Func<EdgeCapsulePreviewContext?> read)
    {
        if (!_enabled || _dispatcher.HasShutdownStarted) return;
        _dispatcher.VerifyAccess();
        _pendingText[source] = read;
        Arm();
    }

    internal void RequestLayout(EdgeCapsulePreviewInvalidationSource source, Func<Target?> read)
    {
        if (!_enabled || _dispatcher.HasShutdownStarted) return;
        _dispatcher.VerifyAccess();
        for (var node = _pendingLayout.First; node != null;)
        {
            var next = node.Next;
            if (ReferenceEquals(node.Value.Source, source)) _pendingLayout.Remove(node);
            node = next;
        }
        _pendingLayout.AddLast((source, read));
        Arm();
    }

    private void Arm() { _debounce.Stop(); _debounce.Start(); }

    internal void BeginDemand()
    {
        _dispatcher.VerifyAccess();
        // Demand wins immediately, but queued high-load notes are not discarded. They resume at
        // ContextIdle afterwards, so pointer movement does not decide which notes deserve preload.
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
                if (work.IsCancellationRequested) break;
                if (_pendingLayout.First is { } node)
                {
                    _pendingLayout.RemoveFirst();
                    var target = node.Value.Read();
                    if (target != null) await WarmLayoutAsync(target, work.Token);
                    continue;
                }
                var pair = _pendingText.First();
                _pendingText.Remove(pair.Key);
                var context = pair.Value();
                if (context == null) { Forget(pair.Key); continue; }
                var content = Capture(context);
                var version = context.InvalidationSource.Version;
                foreach (var step in MarkdownEdgeCapsulePreviewRenderer.WarmInlineSteps(content))
                {
                    if (work.IsCancellationRequested || context.InvalidationSource.Version != version) break;
                    await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Trace.TraceWarning("Markdown preview preload failed: {0}", ex.GetType().Name); }
        finally
        {
            _work = null;
            if (PendingCount > 0 && !_dispatcher.HasShutdownStarted) Arm();
        }
    }

    // An invisible zero-sized holder inherits the real target host's DPI/resources. It never
    // stages host preview content, changes geometry, takes focus or enlarges the input area.
    internal async Task<bool> WarmLayoutAsync(Target target, CancellationToken cancellation = default)
    {
        _dispatcher.VerifyAccess();
        if (!_enabled || cancellation.IsCancellationRequested || !target.StillEligible() ||
            !target.Anchor.IsLoaded || !target.Anchor.IsVisible) return false;
        var version = target.Context.InvalidationSource.Version;
        var descriptor = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(target.Context);
        // Recheck at execution time: a queued heavy note may have become cheap before its turn.
        var content = Capture(target.Context);
        if (!IsClearlyHighLoad(content)) return false;
        var view = (MarkdownEdgeCapsulePreviewView)descriptor.CreateContent(target.Size);
        var viewport = view.PreloadViewport;
        var holder = new Canvas { Width = 0, Height = 0, ClipToBounds = true,
            Opacity = 0, IsHitTestVisible = false, Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        // Hidden speculative content cannot join keyboard navigation, even though opacity zero
        // keeps it eligible for WPF layout and inheriting the target host's resources.
        KeyboardNavigation.SetTabNavigation(holder, KeyboardNavigationMode.None);
        KeyboardNavigation.SetControlTabNavigation(holder, KeyboardNavigationMode.None);
        KeyboardNavigation.SetDirectionalNavigation(holder, KeyboardNavigationMode.None);
        var sized = new Border { Width = Math.Max(1, target.Size.WidthDip - 22), Height = target.Size.HeightDip,
            IsHitTestVisible = false, Focusable = false, Child = view };
        holder.Children.Add(sized);
        var complete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finished(bool success) => complete.TrySetResult(success);
        viewport.PreparationFinished += Finished;
        bool Current() => _enabled && !cancellation.IsCancellationRequested && target.StillEligible() &&
            target.Context.InvalidationSource.Version == version && target.Anchor.IsLoaded && target.Anchor.IsVisible;
        viewport.PreloadStillCurrent = Current;
        void Abandoned(object? sender, EventArgs args)
        {
            if (!Current()) complete.TrySetResult(false);
        }
        void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => Abandoned(sender, EventArgs.Empty);
        void Invalidated() => complete.TrySetResult(false);
        target.Anchor.Unloaded += Abandoned;
        target.Anchor.IsVisibleChanged += VisibilityChanged;
        target.Context.InvalidationSource.Invalidated += Invalidated;
        using var registration = cancellation.Register(() => complete.TrySetCanceled(cancellation));
        try
        {
            target.Anchor.Children.Add(holder);
            view.PrepareForFirstDisplay();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            if (!Current()) return false;
            sized.Measure(new Size(sized.Width, sized.Height));
            sized.Arrange(new Rect(0, 0, sized.Width, sized.Height));
            var success = await complete.Task;
            if (!success || !Current()) return false;
            var retained = viewport.ReturnBodyToPreload();
            if (retained) WarmCompletions++;
            return retained;
        }
        finally
        {
            target.Anchor.Unloaded -= Abandoned;
            target.Anchor.IsVisibleChanged -= VisibilityChanged;
            target.Context.InvalidationSource.Invalidated -= Invalidated;
            viewport.PreparationFinished -= Finished;
            viewport.SetPreviewActive(false);
            viewport.PreloadStillCurrent = () => false;
            target.Anchor.Children.Remove(holder);
            sized.Child = null;
        }
    }

    internal void Clear()
    {
        _dispatcher.VerifyAccess();
        _debounce.Stop(); _work?.Cancel();
        _pendingText.Clear(); _pendingLayout.Clear(); _bodies.Clear(); _excerpts.Clear();
    }

    // Same-binary A/B probe; no settings, environment switch or persistent product option.
    internal void SetEnabledForChecks(bool enabled) { Clear(); _enabled = enabled; }
}
