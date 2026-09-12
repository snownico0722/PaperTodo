using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

// Dispatcher-local, discardable prelayout of eligible edge notes. This cache never owns
// presentation, input or persistence. One current body per source, with no count-based eviction.
internal sealed class MarkdownEdgePreviewPreload
{
    private static readonly ConditionalWeakTable<Dispatcher, MarkdownEdgePreviewPreload> Instances = new();
    internal static MarkdownEdgePreviewPreload For(Dispatcher dispatcher) =>
        Instances.GetValue(dispatcher, value => new(value));

    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<EdgeCapsulePreviewInvalidationSource, MarkdownEdgeCapsulePreviewRenderer.PreviewContent> _excerpts = new();
    private readonly Dictionary<EdgeCapsulePreviewInvalidationSource, Body> _bodies = new();
    private readonly Dictionary<EdgeCapsulePreviewInvalidationSource, Func<ReadResult>> _pendingLayout = new();
    private readonly HashSet<EdgeCapsulePreviewInvalidationSource> _deferred = new();
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _work;
    private EdgeCapsulePreviewInvalidationSource? _workingSource;
    private bool _enabled = true;
    internal int ExcerptCount => _excerpts.Count;
    internal int BodyCount => _bodies.Count;
    internal int PendingCount => _pendingLayout.Count;
    internal int DeferredCount => _deferred.Count;
    private int RunnableCount => PendingCount - DeferredCount;
    internal long BodyHits { get; private set; }
    internal long WarmCompletions { get; private set; }

    internal enum Readiness { Discard, Deferred, Ready }
    internal readonly record struct ReadResult(Readiness State, Target? Target = null)
    {
        internal static ReadResult Ready(Target target) => new(Readiness.Ready, target);
        internal static ReadResult Deferred => new(Readiness.Deferred);
        internal static ReadResult Discard => default;
    }

    internal sealed record Target(EdgeCapsulePreviewContext Context, Panel Anchor,
        EdgeCapsulePreviewSize Size, Func<bool> StillEligible);
    internal sealed record Binding(MarkdownEdgePreviewPreload Owner,
        EdgeCapsulePreviewInvalidationSource Source, long Version,
        MarkdownEdgeCapsulePreviewRenderer.PreviewContent Content, double Zoom)
    {
        internal bool Current => Owner._enabled && Source.Version == Version &&
            Owner._excerpts.TryGetValue(Source, out var current) && ReferenceEquals(current, Content);
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
        _bodies.Remove(source);
        _excerpts.Remove(source);
        if (IsClearlyHighLoad(candidate)) _excerpts[source] = candidate;
        return candidate;
    }

    internal Binding? Bind(EdgeCapsulePreviewContext context,
        MarkdownEdgeCapsulePreviewRenderer.PreviewContent content, double zoom) =>
        _enabled && _excerpts.TryGetValue(context.InvalidationSource, out var entry) && ReferenceEquals(entry, content)
            ? new(this, context.InvalidationSource, context.InvalidationSource.Version, content, zoom) : null;

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
        if (Theme.SyntaxFadeBrush is not SolidColorBrush syntax || syntax.HasAnimatedProperties ||
            !syntax.Transform.Value.IsIdentity || !syntax.RelativeTransform.Value.IsIdentity) return null;
        stamps.Add(syntax.Color + ":" + syntax.Opacity.ToString("R", CultureInfo.InvariantCulture));
        stamps.Add(string.Join("|", NoteTypography.FontFamily.Source, NoteTypography.CodeFontFamily.Source,
            AppTypography.FontFamilyFor(content: true, bold: true).Source,
            AppTypography.FontWeightFor(true), AppTypography.UsesCustomBoldFace(true),
            NoteTypography.FontWeight, NoteTypography.FontStyle, NoteTypography.FontStretch,
            NoteTypography.Language.IetfLanguageTag, NoteTypography.HeadingFontWeight, AppTypography.TextFormattingMode,
            NoteTypography.FontSize, NoteTypography.CodeFontSize,
            NoteTypography.Heading1FontSize, NoteTypography.Heading2FontSize, NoteTypography.Heading3FontSize,
            AppTypography.Scale(1), surface.Language.IetfLanguageTag, surface.FlowDirection,
            TextOptions.GetTextRenderingMode(surface), TextOptions.GetTextHintingMode(surface)));
        return new(binding, size, dpi, string.Join("|", stamps));
    }

    internal bool TryTake(Key key, out Body? body, bool demand = true)
    {
        _dispatcher.VerifyAccess();
        body = null;
        if (!key.Binding.Current || !_bodies.TryGetValue(key.Binding.Source, out var candidate)) return false;
        if (!candidate.Key.Binding.Current) { _bodies.Remove(key.Binding.Source); return false; }
        if (candidate.Key != key) return false;
        _bodies.Remove(key.Binding.Source);
        body = candidate;
        if (demand) BodyHits++;
        return true;
    }

    internal bool Store(Body body)
    {
        _dispatcher.VerifyAccess();
        if (!body.Key.Binding.Current || body.Panel.Parent != null) return false;
        _bodies[body.Key.Binding.Source] = body;
        return true;
    }

    internal void Invalidate(EdgeCapsulePreviewInvalidationSource source)
    {
        _dispatcher.VerifyAccess();
        _bodies.Remove(source);
    }

    internal void Forget(EdgeCapsulePreviewInvalidationSource source)
    {
        _dispatcher.VerifyAccess();
        _excerpts.Remove(source); _bodies.Remove(source); _pendingLayout.Remove(source); _deferred.Remove(source);
        if (ReferenceEquals(_workingSource, source)) _work?.Cancel();
        if (RunnableCount == 0) _debounce.Stop();
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
    }

    // Resume only an existing suspended intent. Normal input does not create speculative work,
    // and a permanently ineligible/deleted source cannot be resurrected by a late event.
    internal void Resume(EdgeCapsulePreviewInvalidationSource source)
    {
        _dispatcher.VerifyAccess();
        if (!_enabled || _dispatcher.HasShutdownStarted) return;
        var resumed = _deferred.Remove(source);
        if (!resumed && !ReferenceEquals(_workingSource, source)) return;
        // A real ready transition can precede the old drain's Deferred registration. Cancel
        // that drain so its finally cannot discard the request after this one-shot wake-up.
        _work?.Cancel();
        Arm();
    }

    private void Arm() { _debounce.Stop(); if (RunnableCount > 0) _debounce.Start(); }

    internal void BeginDemand()
    {
        _dispatcher.VerifyAccess();
        _work?.Cancel();
        _debounce.Stop();
        if (RunnableCount > 0) Arm();
    }

    private async void Drain()
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
                var pair = _pendingLayout.First(item => !_deferred.Contains(item.Key));
                var defer = false;
                _workingSource = pair.Key;
                try
                {
                    var read = pair.Value();
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
                    // Temporary loss of eligibility keeps the weak reader dormant, without a
                    // timer. New content supersedes it; explicit lifecycle events resume it.
                    if (!work.IsCancellationRequested &&
                        _pendingLayout.TryGetValue(pair.Key, out var current) && ReferenceEquals(current, pair.Value))
                    {
                        if (defer) _deferred.Add(pair.Key);
                        else _pendingLayout.Remove(pair.Key);
                    }
                }
            }
        }
        finally
        {
            _work = null;
            if (RunnableCount > 0 && !_dispatcher.HasShutdownStarted && !_debounce.IsEnabled) Arm();
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
        var content = Capture(target.Context);
        if (!IsClearlyHighLoad(content)) return false;
        var view = new MarkdownEdgeCapsulePreviewView(target.Context, target.Size, content, version);
        var viewport = view.PreloadViewport;
        var holder = new Canvas { Width = 0, Height = 0, ClipToBounds = true,
            Opacity = 0, IsHitTestVisible = false, Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        // Hidden speculative content cannot join keyboard navigation, even though opacity zero
        // keeps it eligible for WPF layout and inheriting the target host's resources.
        KeyboardNavigation.SetTabNavigation(holder, KeyboardNavigationMode.None);
        KeyboardNavigation.SetControlTabNavigation(holder, KeyboardNavigationMode.None);
        KeyboardNavigation.SetDirectionalNavigation(holder, KeyboardNavigationMode.None);
        var contentSize = target.Size.ContentSize;
        var sized = new Border { Width = contentSize.Width, Height = contentSize.Height,
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
            if (!viewport.IsLoaded || viewport.RenderSize.Width <= 0 || viewport.RenderSize.Height <= 0) return false;
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
        _pendingLayout.Clear(); _deferred.Clear(); _bodies.Clear(); _excerpts.Clear();
    }

    // Same-binary A/B probe; no settings, environment switch or persistent product option.
    internal void SetEnabledForChecks(bool enabled) { Clear(); _enabled = enabled; }
}
