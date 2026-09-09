using System.Diagnostics;

namespace PaperTodo;

internal enum EdgeCapsuleVisualAuthority
{
    RealDocked = 0,
    QueueTranslation = 1,
    FloatingDrag = 2,
    DockingOverlap = 3,
    AuthoritySwap = 4,
    Lost = 5
}

internal readonly record struct EdgeCapsuleQueueProxyCandidate(
    string PaperId,
    string QueueKey,
    EdgeCapsulePresentationFrame Start,
    EdgeCapsulePresentationFrame Source,
    EdgeCapsulePresentationFrame Target,
    EdgeCapsuleMotion Motion,
    bool HostReady,
    bool Topmost,
    bool RetainedByCurrentProxy,
    EdgeCapsuleVisualAuthority Authority =
        EdgeCapsuleVisualAuthority.RealDocked);

internal readonly record struct EdgeCapsuleQueueProxyMemberPlan(
    string PaperId,
    EdgeCapsulePresentationFrame Start,
    EdgeCapsulePresentationFrame Source,
    EdgeCapsulePresentationFrame Target);

internal sealed record EdgeCapsuleQueueProxyPlan(
    string QueueKey,
    DeviceScreenRect Envelope,
    EdgeCapsuleEdge Edge,
    int WallDeviceX,
    double DpiScaleX,
    double DpiScaleY,
    int DurationMilliseconds,
    bool Topmost,
    IReadOnlyList<EdgeCapsuleQueueProxyMemberPlan> Members);

/// <summary>
/// V3 Lite compositor admission. The compositor may translate a stable live
/// HWND surface, and nothing else. Width, height, layout, clip, opacity and
/// preview identity stay in the bounded WPF host. A direct drag owner may be
/// omitted while eligible peers continue through the queue compositor.
/// </summary>
internal static class EdgeCapsuleQueueProxyPolicy
{
    internal static EdgeCapsuleVisualAuthority ResolveVisualAuthority(
        EdgeCapsuleGestureState gesture,
        bool floatingCoverActive,
        bool queueTranslationActive)
    {
        if (floatingCoverActive)
        {
            return gesture is
                EdgeCapsuleGestureState.DockingHandoff or
                EdgeCapsuleGestureState.DockingReveal
                    ? EdgeCapsuleVisualAuthority.DockingOverlap
                    : EdgeCapsuleVisualAuthority.FloatingDrag;
        }
        if (queueTranslationActive)
        {
            return EdgeCapsuleVisualAuthority.QueueTranslation;
        }
        if (gesture is
            EdgeCapsuleGestureState.DockingHandoff or
            EdgeCapsuleGestureState.DockingReveal)
        {
            return EdgeCapsuleVisualAuthority.AuthoritySwap;
        }
        return EdgeCapsuleVisualAuthority.RealDocked;
    }

    internal static bool AllowsQueueProxyOwnership(
        EdgeCapsuleVisualAuthority authority) =>
        authority is
            EdgeCapsuleVisualAuthority.RealDocked or
            EdgeCapsuleVisualAuthority.QueueTranslation;

    /// <summary>
    /// Translation owns one live HWND surface, not one frozen WPF frame. A retained successor may
    /// observe a different Rest/Hover/Preview morph sample while the native host capacity, wall,
    /// DPI and source identity remain unchanged.
    /// </summary>
    internal static bool HasStableLiveSurfaceIdentity(
        EdgeCapsulePresentationFrame current,
        EdgeCapsulePresentationFrame expected) =>
        current.Visible &&
        expected.Visible &&
        current.IsUsable &&
        expected.IsUsable &&
        current.HostBounds == expected.HostBounds &&
        current.Edge == expected.Edge &&
        current.WallDeviceX == expected.WallDeviceX &&
        Math.Abs(current.DpiScaleX - expected.DpiScaleX) < 0.001 &&
        Math.Abs(current.DpiScaleY - expected.DpiScaleY) < 0.001;

    internal static DeviceScreenRect PresentedHostBounds(
        EdgeCapsulePresentationFrame frame)
    {
        if (!frame.Visible || frame.HostBounds.IsEmpty ||
            frame.Bounds.IsEmpty)
        {
            return default;
        }

        var width = frame.HostBounds.Width;
        var height = frame.HostBounds.Height;
        var left = frame.Edge == EdgeCapsuleEdge.Left
            ? frame.WallDeviceX
            : frame.WallDeviceX - width;
        return new DeviceScreenRect(
            left,
            frame.Bounds.Top,
            left + width,
            frame.Bounds.Top + height);
    }

    internal static bool RequiresTranslation(
        EdgeCapsulePresentationFrame start,
        EdgeCapsulePresentationFrame target) =>
        start.Visible &&
        target.Visible &&
        PresentedHostBounds(start) != target.HostBounds;

    public static EdgeCapsuleQueueProxyPlan? TryCreate(
        string queueKey,
        IReadOnlyList<EdgeCapsuleQueueProxyCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return Reject(queueKey, "no-candidates", candidates);
        }

        var members = new List<EdgeCapsuleQueueProxyMemberPlan>(
            candidates.Count);
        foreach (var candidate in candidates)
        {
            var translated =
                RequiresTranslation(candidate.Start, candidate.Target);
            if (!AllowsQueueProxyOwnership(candidate.Authority))
            {
                // The real/floating owner remains direct. If a predecessor
                // currently owns it, startup reveals only that source while
                // transferring the remaining peers to the successor.
                continue;
            }
            if (!translated && !candidate.RetainedByCurrentProxy)
            {
                // Pure Rest/Hover/Preview morph stays in WPF.
                continue;
            }

            var rejection =
                TranslationCandidateRejection(candidate, queueKey);
            if (rejection != null)
            {
                // A fresh member can safely remain direct. A retained source
                // with an incompatible live surface must let its predecessor
                // finish rather than silently changing pixel identity.
                if (!candidate.RetainedByCurrentProxy)
                {
                    continue;
                }
                return Reject(queueKey, rejection, candidates, candidate);
            }

            members.Add(new EdgeCapsuleQueueProxyMemberPlan(
                candidate.PaperId,
                candidate.Start,
                candidate.Source,
                candidate.Target));
        }

        if (members.Count == 0)
        {
            return Reject(
                queueKey,
                "no-eligible-translation",
                candidates);
        }

        var first = members[0];
        var mismatch = members.FirstOrDefault(member =>
            member.Start.Edge != first.Start.Edge ||
            member.Source.Edge != first.Start.Edge ||
            member.Target.Edge != first.Start.Edge ||
            member.Start.WallDeviceX != first.Start.WallDeviceX ||
            member.Source.WallDeviceX != first.Start.WallDeviceX ||
            member.Target.WallDeviceX != first.Start.WallDeviceX ||
            Math.Abs(member.Start.DpiScaleX -
                     first.Start.DpiScaleX) > 0.001 ||
            Math.Abs(member.Start.DpiScaleY -
                     first.Start.DpiScaleY) > 0.001 ||
            Math.Abs(member.Source.DpiScaleX -
                     first.Start.DpiScaleX) > 0.001 ||
            Math.Abs(member.Source.DpiScaleY -
                     first.Start.DpiScaleY) > 0.001 ||
            Math.Abs(member.Target.DpiScaleX -
                     first.Start.DpiScaleX) > 0.001 ||
            Math.Abs(member.Target.DpiScaleY -
                     first.Start.DpiScaleY) > 0.001);
        if (!string.IsNullOrEmpty(mismatch.PaperId))
        {
            return Reject(
                queueKey,
                "queue-geometry-mismatch",
                candidates);
        }

        var envelope = default(DeviceScreenRect);
        foreach (var member in members)
        {
            envelope = EdgeCapsuleQueueProxyGeometry.Union(
                envelope,
                PresentedHostBounds(member.Start));
            envelope = EdgeCapsuleQueueProxyGeometry.Union(
                envelope,
                member.Target.HostBounds);
        }
        if (envelope.IsEmpty)
        {
            return Reject(
                queueKey,
                "empty-translation-envelope",
                candidates);
        }

        var durationByPaper = candidates.ToDictionary(
            candidate => candidate.PaperId,
            candidate => candidate.Motion.DurationMilliseconds,
            StringComparer.Ordinal);
        var duration = Math.Max(
            1,
            members.Select(member =>
                    durationByPaper.GetValueOrDefault(
                        member.PaperId,
                        EdgeCapsuleLayout.SlotMoveMilliseconds))
                .DefaultIfEmpty(
                    EdgeCapsuleLayout.SlotMoveMilliseconds)
                .Max());

#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.admission mode=translation-only outcome=accepted " +
            $"queue={queueKey} candidates={candidates.Count} " +
            $"members={members.Count} durationMs={duration} " +
            $"papers={string.Join(',', members.Select(member => EdgeCapsulePerformanceDiagnostics.ShortId(member.PaperId)))}");
#endif
        return new EdgeCapsuleQueueProxyPlan(
            queueKey,
            envelope,
            first.Start.Edge,
            first.Start.WallDeviceX,
            first.Start.DpiScaleX,
            first.Start.DpiScaleY,
            duration,
            Topmost: true,
            members);
    }

    private static string? TranslationCandidateRejection(
        EdgeCapsuleQueueProxyCandidate candidate,
        string queueKey)
    {
        if (!candidate.Topmost)
        {
            return "translation-member-not-topmost";
        }
        if (!candidate.HostReady)
        {
            return "translation-member-host-not-ready";
        }
        if (!candidate.Start.IsUsable ||
            !candidate.Source.IsUsable ||
            !candidate.Target.IsUsable)
        {
            return "translation-member-frame-unusable";
        }
        if (!candidate.Start.Visible ||
            !candidate.Source.Visible ||
            !candidate.Target.Visible)
        {
            return "translation-member-hidden";
        }
        if (candidate.Motion.Kind != EdgeCapsuleMotionKind.Animate &&
            !candidate.RetainedByCurrentProxy)
        {
            return $"translation-member-motion-{candidate.Motion.Kind}";
        }
        if (!string.Equals(
                candidate.QueueKey,
                queueKey,
                StringComparison.Ordinal))
        {
            return "translation-member-queue-change";
        }
        if (!CanWrapMovingMemberLive(
                candidate.Source,
                candidate.Target) ||
            candidate.Start.HostBounds.Width !=
                candidate.Source.HostBounds.Width ||
            candidate.Start.HostBounds.Height !=
                candidate.Source.HostBounds.Height)
        {
            return "translation-member-unstable-host-capacity";
        }
        return null;
    }

    internal static bool CanWrapMovingMemberLive(
        EdgeCapsulePresentationFrame source,
        EdgeCapsulePresentationFrame target) =>
        source.Visible &&
        target.Visible &&
        !source.HostBounds.IsEmpty &&
        !target.HostBounds.IsEmpty &&
        source.HostBounds.Width == target.HostBounds.Width &&
        source.HostBounds.Height == target.HostBounds.Height &&
        source.Edge == target.Edge &&
        source.WallDeviceX == target.WallDeviceX &&
        Math.Abs(source.DpiScaleX - target.DpiScaleX) < 0.001 &&
        Math.Abs(source.DpiScaleY - target.DpiScaleY) < 0.001;

    internal static EdgeCapsulePresentationFrame SampleLogicalFrame(
        EdgeCapsuleQueueProxyMemberPlan member,
        long startedAtTimestamp,
        int durationMilliseconds,
        long nowTimestamp)
    {
        var durationTicks = Math.Max(
            1,
            (long)Math.Round(
                Stopwatch.Frequency *
                Math.Max(1, durationMilliseconds) /
                1000.0));
        var target = member.Target;
        var transition = new EdgeCapsuleTransition(
            member.Start,
            new EdgeCapsuleTargetPresentation(
                target.Visible,
                target.Surface,
                target.Bounds,
                target.HostBounds,
                target.InteractiveBounds,
                target.Edge,
                target.BodyWindowWidthDevice,
                target.WallDeviceX,
                target.DpiScaleX,
                target.DpiScaleY,
                target.MaximumCloseWidthDip,
                target.Opacity,
                target.ContentOpacity,
                target.OutlineVisible,
                target.IsHitTestVisible,
                target.CloseSegmentActsAsContent,
                target.TitleVisible),
            startedAtTimestamp,
            durationTicks,
            EdgeCapsuleTransitionReason.Placement);
        return EdgeCapsuleTransitionPolicy
            .Sample(transition, nowTimestamp)
            .Frame;
    }

    internal static DeviceScreenPoint TranslationOffset(
        EdgeCapsuleQueueProxyMemberPlan member,
        long startedAtTimestamp,
        int durationMilliseconds,
        long nowTimestamp)
    {
        var frame = SampleLogicalFrame(
            member,
            startedAtTimestamp,
            durationMilliseconds,
            nowTimestamp);
        var currentHost = PresentedHostBounds(frame);
        var targetHost = member.Target.HostBounds;
        return currentHost.IsEmpty || targetHost.IsEmpty
            ? default
            : new DeviceScreenPoint(
                currentHost.Left - targetHost.Left,
                currentHost.Top - targetHost.Top);
    }

    internal static double SampleProgress(
        long startedAtTimestamp,
        int durationMilliseconds,
        long nowTimestamp)
    {
        if (startedAtTimestamp <= 0)
        {
            return 0;
        }
        var durationTicks = Math.Max(
            1,
            (long)Math.Round(
                Stopwatch.Frequency *
                Math.Max(1, durationMilliseconds) /
                1000.0));
        var raw = Math.Clamp(
            Math.Max(0, nowTimestamp - startedAtTimestamp) /
                (double)durationTicks,
            0,
            1);
        return 1.0 - Math.Pow(1.0 - raw, 3.0);
    }

    private static EdgeCapsuleQueueProxyPlan? Reject(
        string queueKey,
        string reason,
        IReadOnlyList<EdgeCapsuleQueueProxyCandidate> candidates,
        EdgeCapsuleQueueProxyCandidate? offending = null)
    {
#if DEBUG
        var detail = offending is { } candidate
            ? $" paper={EdgeCapsulePerformanceDiagnostics.ShortId(candidate.PaperId)} " +
              $"authority={candidate.Authority} " +
              $"hostReady={candidate.HostReady} " +
              $"retained={candidate.RetainedByCurrentProxy}"
            : string.Empty;
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.admission mode=translation-only outcome=rejected " +
            $"queue={queueKey} reason={reason} " +
            $"candidates={candidates.Count}{detail}");
#endif
        return null;
    }
}
