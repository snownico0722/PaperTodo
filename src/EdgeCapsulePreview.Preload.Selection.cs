using System.Diagnostics;
using System.Windows.Threading;
using PreviewContent = PaperTodo.MarkdownEdgeCapsulePreviewRenderer.PreviewContent;

namespace PaperTodo;

internal sealed partial class MarkdownEdgePreviewPreload
{
    private const int MinimumPreloadCount = 10;
    internal enum PreloadTier { Heavy, FirstRelaxation, SecondRelaxation, AnyNonempty, Empty }

    // Keep weak readers and bounded content for selection, never a context holding its window.
    // Unselected candidates remain available to fill a vacancy without polling or a second queue.
    private sealed class Candidate(EdgeCapsulePreviewInvalidationSource source, long order,
        Func<EdgeCapsulePreviewContext?> readContext, Func<PreviewContent, ReadResult> readTarget)
    {
        internal readonly EdgeCapsulePreviewInvalidationSource Source = source;
        internal readonly long Order = order;
        internal readonly Func<EdgeCapsulePreviewContext?> ReadContext = readContext;
        internal PreviewContent? Content;
        internal PreloadTier Tier = PreloadTier.Empty;
        internal bool Selected;
        internal ReadResult Read() => Content is { } content ? readTarget(content) : ReadResult.Discard;
    }

    private readonly Dictionary<EdgeCapsulePreviewInvalidationSource, Candidate> _candidates = new();
    private bool _selectionDirty;
    private long _nextCandidateOrder;

    internal static bool IsClearlyHighLoad(PreviewContent content) => Classify(content) == PreloadTier.Heavy;

    internal static PreloadTier Classify(PreviewContent content)
    {
        if (content.IsEmpty) return PreloadTier.Empty;
        var totalCharacters = content.Lines.Sum(line => line.Text.Length);
        if (totalCharacters > 400) return PreloadTier.Heavy;

        var styledCharacters = 0;
        var styledPieces = 0;
        if (content.RenderMode != MarkdownRenderModes.Off)
        {
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
                        // Count semantic styles and link labels, not faded syntax/URL markers.
                        if ((piece.Style & ~MarkdownEdgeCapsulePreviewRenderer.InlineStyle.Syntax) == 0 && piece.Link == null)
                            continue;
                        styledCharacters += piece.Text.Length;
                        styledPieces++;
                    }
                }
            }
        }

        // Preserve the original strict heavy thresholds. Relaxed style rules deliberately
        // have no total-length gate, so a short note with several links/styles qualifies.
        if (totalCharacters > 200 && (styledCharacters > 100 || styledPieces >= 4)) return PreloadTier.Heavy;
        if (totalCharacters > 200 || styledCharacters >= 30 || styledPieces >= 3) return PreloadTier.FirstRelaxation;
        if (totalCharacters > 100 || styledCharacters >= 10 || styledPieces >= 2) return PreloadTier.SecondRelaxation;
        return PreloadTier.AnyNonempty;
    }

    private static bool SameContent(PreviewContent? first, PreviewContent second) =>
        first != null && first.RenderMode == second.RenderMode && first.Truncated == second.Truncated &&
        first.Lines.SequenceEqual(second.Lines);

    internal bool ShouldPreload(EdgeCapsulePreviewContext context, PreviewContent content) =>
        _enabled && !content.IsEmpty && (_candidates.TryGetValue(context.InvalidationSource, out var candidate)
            ? candidate.Selected && ReferenceEquals(candidate.Content, content)
            : IsClearlyHighLoad(content));

    internal void RequestLayout(EdgeCapsulePreviewInvalidationSource source,
        Func<EdgeCapsulePreviewContext?> readContext, Func<PreviewContent, ReadResult> readTarget)
    {
        _dispatcher.VerifyAccess();
        if (!_enabled || _dispatcher.HasShutdownStarted) return;
        _candidates.TryGetValue(source, out var previous);
        var candidate = new Candidate(source, previous?.Order ?? _nextCandidateOrder++, readContext, readTarget)
        {
            Content = previous?.Content,
            Tier = previous?.Tier ?? PreloadTier.Empty,
            Selected = previous?.Selected ?? false
        };
        _candidates[source] = candidate;
        _selectionDirty = true;
        RequestLayout(source, candidate.Read);
    }

    private async Task RefreshSelectionAsync(CancellationToken cancellation)
    {
        // All candidates are classified before any is warmed. Yield between bounded excerpts;
        // edits and real demand cancel this pass and retain the normal coalescing delay.
        foreach (var candidate in _candidates.Values.ToArray())
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            if (cancellation.IsCancellationRequested) return;
            if (!_candidates.TryGetValue(candidate.Source, out var current) || !ReferenceEquals(current, candidate))
                continue;
            try
            {
                var context = candidate.ReadContext();
                if (context == null) { Forget(candidate.Source); continue; }
                var content = MarkdownEdgeCapsulePreviewRenderer.CaptureContent(
                    context.ReadMarkdownText(), context.ReadMarkdownRenderMode());
                if (!SameContent(candidate.Content, content))
                {
                    candidate.Content = content;
                    candidate.Tier = Classify(content);
                }
            }
            catch (Exception ex)
            {
                Forget(candidate.Source);
                Trace.TraceWarning("Markdown preview selection failed: {0}", ex.GetType().Name);
            }
        }
        if (cancellation.IsCancellationRequested) return;

        var selected = new HashSet<EdgeCapsulePreviewInvalidationSource>();
        foreach (var candidate in _candidates.Values.Where(item => item.Tier != PreloadTier.Empty)
            .OrderBy(item => item.Tier).ThenByDescending(item => item.Selected).ThenBy(item => item.Order))
        {
            // Ten is a fill target, never a cap on the original heavy set. At a crossing
            // tier, take only its remaining slots instead of rejecting the entire tier.
            if (candidate.Tier != PreloadTier.Heavy && selected.Count >= MinimumPreloadCount) break;
            selected.Add(candidate.Source);
        }

        foreach (var candidate in _candidates.Values)
        {
            var source = candidate.Source;
            var wasSelected = candidate.Selected;
            candidate.Selected = selected.Contains(source);
            if (!candidate.Selected)
            {
                _excerpts.Remove(source); _artifacts.Remove(source);
                _pendingLayout.Remove(source); _deferred.Remove(source);
                continue;
            }

            var content = candidate.Content!;
            var changed = !_excerpts.TryGetValue(source, out var old) || !ReferenceEquals(old, content);
            if (changed) { _artifacts.Remove(source); _excerpts[source] = content; }
            if (!wasSelected || changed)
            {
                _pendingLayout[source] = candidate.Read;
                _deferred.Remove(source);
            }
        }
        _selectionDirty = false;
    }
}
