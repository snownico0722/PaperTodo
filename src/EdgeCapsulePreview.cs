using System.Windows;

namespace PaperTodo;

internal readonly record struct EdgeCapsulePreviewSize(
    double WidthDip,
    double HeightDip)
{
    // Protocol 1.8 does not impose a product-level mini-card minimum or maximum. These legacy
    // minimum constants remain only as built-in presentation defaults; Normalize deliberately
    // accepts any positive finite plugin size and clamps only to the current monitor work area.
    public const double MinimumWidthDip = 120;
    public const double MaximumWidthDip = double.MaxValue;
    public const double MinimumHeightDip = 90;
    public const double MaximumHeightDip = double.MaxValue;

    public EdgeCapsulePreviewSize Normalize(double maximumWidthDip, double maximumHeightDip)
    {
        if (!double.IsFinite(WidthDip) ||
            !double.IsFinite(HeightDip) ||
            WidthDip <= 0 ||
            HeightDip <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EdgeCapsulePreviewSize),
                "Edge preview width and height must be positive finite numbers.");
        }
        if (!double.IsFinite(maximumWidthDip) ||
            !double.IsFinite(maximumHeightDip) ||
            maximumWidthDip <= 0 ||
            maximumHeightDip <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumWidthDip),
                "Edge preview monitor limits must be positive finite numbers.");
        }

        return new EdgeCapsulePreviewSize(
            Math.Min(WidthDip, maximumWidthDip),
            Math.Min(HeightDip, maximumHeightDip));
    }
}

/// <summary>
/// Size is the complete visible card rectangle in DIPs, including the host-owned close segment.
/// The host normalizes it before CreateContent is invoked and freezes it for the preview session.
/// </summary>
internal sealed record EdgeCapsulePreviewDescriptor(
    EdgeCapsulePreviewSize Size,
    Func<EdgeCapsulePreviewSize, FrameworkElement> CreateContent,
    Action<bool>? SetVisibility = null,
    Action? PrepareForActivation = null,
    bool DeferContentCreation = false);

internal sealed record EdgeCapsulePreviewRequest(
    EdgeCapsulePreviewSize Size,
    FrameworkElement Content,
    Action<bool>? SetVisibility = null,
    Action? PrepareForActivation = null,
    Func<FrameworkElement>? CreateDeferredContent = null);



internal readonly record struct EdgeCapsulePreviewScreenGeometry(
    DeviceScreenRect Bounds,
    double DpiScaleX,
    double DpiScaleY);

internal sealed class EdgeCapsulePreviewInvalidationSource
{
    public event Action? Invalidated;

    public void Invalidate() => Invalidated?.Invoke();
}

internal sealed record EdgeCapsulePreviewContext(
    PaperData Paper,
    Func<string> ReadTitle,
    bool PaperExpanded,
    Func<string> ReadMarkdownText,
    Func<string, bool, bool> SetTodoDone,
    Func<string, bool> OpenTodoLinkedTarget,
    Func<Style> ReadTodoCheckStyle,
    Func<string> ReadPluginStatus,
    Action<string> OpenExternal,
    EdgeCapsulePreviewInvalidationSource InvalidationSource)
{
    public string Title => ReadTitle();
}

/// <summary>
/// Internal content seam for edge preview cards. Built-in Todo/Markdown and protocol 1.8 plugin
/// adapters replace only the descriptor; queue, host, transition and input code remain shared.
/// </summary>
internal interface IEdgeCapsulePreviewProvider
{
    EdgeCapsulePreviewDescriptor Describe(EdgeCapsulePreviewContext context);
}

internal static class EdgeCapsulePreviewInteraction
{
    public static readonly DependencyProperty ConsumesPointerProperty =
        DependencyProperty.RegisterAttached(
            "ConsumesPointer",
            typeof(bool),
            typeof(EdgeCapsulePreviewInteraction),
            new FrameworkPropertyMetadata(false));

    public static void SetConsumesPointer(DependencyObject element, bool value) =>
        element.SetValue(ConsumesPointerProperty, value);

    public static bool GetConsumesPointer(DependencyObject element) =>
        (bool)element.GetValue(ConsumesPointerProperty);
}

internal sealed class DefaultEdgeCapsulePreviewProvider : IEdgeCapsulePreviewProvider
{
    public static DefaultEdgeCapsulePreviewProvider Instance { get; } = new();

    private DefaultEdgeCapsulePreviewProvider()
    {
    }

    public EdgeCapsulePreviewDescriptor Describe(EdgeCapsulePreviewContext context)
    {
        var title = context.Title;
        var status = context.ReadPluginStatus();
        var width = EdgeCapsulePreviewMeasure.MeasureWidth(
            title,
            status,
            minimum: EdgeCapsulePreviewSize.MinimumWidthDip,
            maximum: 440);
        var height = Math.Clamp(
            150 + EdgeCapsulePreviewMeasure.EstimateWrappedLines(
                status,
                Math.Max(72, width - 40)) * AppTypography.Scale(20),
            160,
            280);

        return new EdgeCapsulePreviewDescriptor(
            new EdgeCapsulePreviewSize(width, height),
            size => new PluginFallbackEdgeCapsulePreviewView(context, size));
    }
}

internal sealed record EdgeCapsulePreviewLayoutSession(
    string QueueKey,
    string OwnerPaperId,
    EdgeCapsulePreviewSize Size,
    IReadOnlyList<string> QueuePaperIds,
    IReadOnlyDictionary<string, double> TopOffsetsDip);

/// <summary>
/// Preview placement policy. The compact queue remains the base plan. During one browsing session
/// only the owner has a non-standard height. Downward transfers keep the newly hovered capsule at
/// its current pointer-side position whenever that preview itself still fits the monitor; only a
/// space-constrained target falls back to filling the released preview region. Full compaction
/// happens on exit.
/// </summary>
internal static class EdgeCapsulePreviewLayoutCoordinator
{
    public static EdgeCapsulePreviewLayoutSession? OpenOrTransfer(
        EdgeCapsuleQueuePlan basePlan,
        EdgeCapsulePreviewLayoutSession? previous,
        string queueKey,
        string ownerPaperId,
        EdgeCapsulePreviewSize size,
        double compactHeightDip,
        double gapDip)
    {
        var queue = basePlan.Queues.FirstOrDefault(item =>
            string.Equals(item.Key, queueKey, StringComparison.Ordinal));
        if (queue == null)
        {
            return null;
        }

        var papers = queue.Papers;
        var newIndex = IndexOf(papers, ownerPaperId);
        if (newIndex < 0)
        {
            return null;
        }

        var compactHeight = Math.Max(1, compactHeightDip);
        var gap = Math.Max(0, gapDip);
        var slotHeight = compactHeight + gap;
        var baseTops = papers
            .Select(paper =>
                basePlan.Placements[paper.Id].VisualIndex * slotHeight)
            .ToArray();
        var currentTops = baseTops.ToArray();

        var paperIds = papers.Select(paper => paper.Id).ToArray();
        var sameQueue = previous != null &&
            string.Equals(previous.QueueKey, queueKey, StringComparison.Ordinal) &&
            previous.QueuePaperIds.SequenceEqual(
                paperIds,
                StringComparer.Ordinal);
        var oldIndex = -1;
        if (sameQueue)
        {
            for (var index = 0; index < papers.Count; index++)
            {
                currentTops[index] += previous!.TopOffsetsDip
                    .GetValueOrDefault(papers[index].Id);
            }
            oldIndex = IndexOf(papers, previous!.OwnerPaperId);
        }

        var newHeight = Math.Max(compactHeight, size.HeightDip);
        var tops = currentTops.ToArray();

        // Preview browsing deliberately keeps queue-relative motion even if followers extend below
        // the monitor work area. Only the target preview itself decides whether its pointer-side top
        // can be retained; overflow followers are allowed by the normal queue policy.
        if (oldIndex < 0)
        {
            tops[newIndex] = baseTops[newIndex];
            PushFollowingMembers(
                tops,
                currentTops,
                newIndex,
                newHeight,
                compactHeight,
                gap);
        }
        else if (newIndex > oldIndex &&
                 CanKeepPointerSideTopOnDownwardTransfer(
                     papers,
                     newIndex,
                     newHeight))
        {
            // Prefer interaction continuity over packing density while the target card itself has
            // room. The newly hovered capsule is already under the pointer, so leave its top exactly
            // where it is and let the preview grow downward. Followers may overflow just as a long
            // compact queue may; the released upper region stays empty until the session ends.
            for (var index = 0; index < newIndex; index++)
            {
                tops[index] = baseTops[index];
            }
            tops[newIndex] = currentTops[newIndex];
            PushFollowingMembers(
                tops,
                currentTops,
                newIndex,
                newHeight,
                compactHeight,
                gap);
        }
        else if (newIndex > oldIndex)
        {
            // Space-constrained downward transfer: compact only the released upper side. Keep the
            // lower anchor of the newly hovered capsule (and everything below it) where it already
            // is, then grow the new preview upward into the space released by the old owner.
            for (var index = 0; index < newIndex; index++)
            {
                tops[index] = baseTops[index];
            }

            var nextTop = newIndex + 1 < papers.Count
                ? currentTops[newIndex + 1]
                : currentTops[newIndex] + compactHeight + gap;
            var proposedTop = nextTop - gap - newHeight;
            var anchoredTop = Math.Min(
                proposedTop,
                currentTops[newIndex]);
            var minimumTop = newIndex > 0
                ? tops[newIndex - 1] + compactHeight + gap
                : baseTops[newIndex];
            tops[newIndex] = Math.Max(anchoredTop, minimumTop);
            PushFollowingMembers(
                tops,
                currentTops,
                newIndex,
                newHeight,
                compactHeight,
                gap);
        }
        else if (newIndex < oldIndex)
        {
            // Moving upward cannot invert any crossed member. Compact from the new owner downward
            // so followers fill space released by a taller old card. The downward branch above
            // intentionally remains anchored because compacting below a skipped target can make a
            // follower appear to pass that target during the shared transition.
            for (var index = 0; index <= newIndex; index++)
            {
                tops[index] = baseTops[index];
            }
            PlaceFollowingMembers(
                tops,
                currentTops,
                newIndex,
                newHeight,
                compactHeight,
                gap,
                retainExistingGaps: false);
        }
        else
        {
            return previous! with { Size = size };
        }

        var offsets = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var index = 0; index < papers.Count; index++)
        {
            offsets[papers[index].Id] = tops[index] - baseTops[index];
        }

        return new EdgeCapsulePreviewLayoutSession(
            queueKey,
            ownerPaperId,
            size,
            paperIds,
            offsets);
    }

    public static EdgeCapsuleQueuePlan Apply(
        EdgeCapsuleQueuePlan basePlan,
        EdgeCapsulePreviewLayoutSession? session)
    {
        if (session == null)
        {
            return basePlan;
        }

        var placements = basePlan.Placements.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        foreach (var (paperId, offset) in session.TopOffsetsDip)
        {
            if (placements.TryGetValue(paperId, out var placement))
            {
                placements[paperId] = placement with { TopOffsetDip = offset };
            }
        }

        return new EdgeCapsuleQueuePlan(basePlan.Queues, placements);
    }

    private static bool CanKeepPointerSideTopOnDownwardTransfer(
        IReadOnlyList<PaperData> papers,
        int ownerIndex,
        double ownerHeight)
    {
        if (ownerIndex < 0 || ownerIndex >= papers.Count)
        {
            return false;
        }

        return EdgeCapsulePreviewViewportPolicy.CanKeepCurrentTop(
            papers[ownerIndex],
            ownerHeight);
    }

    private static void PushFollowingMembers(
        double[] tops,
        double[] currentTops,
        int ownerIndex,
        double ownerHeight,
        double compactHeight,
        double gap) =>
        PlaceFollowingMembers(
            tops,
            currentTops,
            ownerIndex,
            ownerHeight,
            compactHeight,
            gap,
            retainExistingGaps: true);

    private static void PlaceFollowingMembers(
        double[] tops,
        double[] currentTops,
        int ownerIndex,
        double ownerHeight,
        double compactHeight,
        double gap,
        bool retainExistingGaps)
    {
        for (var index = ownerIndex + 1; index < tops.Length; index++)
        {
            var previousHeight = index - 1 == ownerIndex
                ? ownerHeight
                : compactHeight;
            var minimumTop = tops[index - 1] + previousHeight + gap;
            tops[index] = retainExistingGaps
                ? Math.Max(currentTops[index], minimumTop)
                : minimumTop;
        }
    }

    private static int IndexOf(IReadOnlyList<PaperData> papers, string paperId)
    {
        for (var index = 0; index < papers.Count; index++)
        {
            if (string.Equals(
                    papers[index].Id,
                    paperId,
                    StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }
}
