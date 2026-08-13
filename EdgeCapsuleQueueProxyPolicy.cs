using System.Diagnostics;

namespace PaperTodo;

internal enum EdgeCapsuleQueueProxyMemberRole
{
    MovingSource = 0,
    Moving = MovingSource,
    RevealTarget = 1,
    RevealTargetWithSnapshot = 2,
    OpeningPreview = RevealTargetWithSnapshot,
    ConcealSource = 3,
    ClosingPreview = ConcealSource
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
    bool LegacyRegressionGeometry = false)
{
    // Preserve the historical source=start constructor for the broad regression harness. The
    // legacy bit is carried only into policy validation; production callers use the explicit live
    // Source frame and remain subject to native clip containment.
    public EdgeCapsuleQueueProxyCandidate(
        string PaperId,
        string QueueKey,
        EdgeCapsulePresentationFrame Start,
        EdgeCapsulePresentationFrame Target,
        EdgeCapsuleMotion Motion,
        bool HostReady,
        bool Topmost)
        : this(
            PaperId,
            QueueKey,
            Start,
            Start,
            Target,
            Motion,
            HostReady,
            Topmost,
            RetainedByCurrentProxy: false,
            LegacyRegressionGeometry: true)
    {
    }
}

internal readonly record struct EdgeCapsuleQueueProxyMemberPlan(
    string PaperId,
    EdgeCapsulePresentationFrame Start,
    EdgeCapsulePresentationFrame Source,
    EdgeCapsulePresentationFrame Target,
    EdgeCapsuleQueueProxyMemberRole Role,
    bool LegacyRegressionGeometry = false)
{
    public bool DefersRealEndpoint =>
        Role == EdgeCapsuleQueueProxyMemberRole.ConcealSource;

    public bool RequiresStartSnapshot =>
        Role == EdgeCapsuleQueueProxyMemberRole.RevealTargetWithSnapshot;

    public bool UsesTargetSurface =>
        Role is EdgeCapsuleQueueProxyMemberRole.RevealTarget or
            EdgeCapsuleQueueProxyMemberRole.RevealTargetWithSnapshot;
}

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
/// Admission and immutable geometry policy for the queue compositor. The real HWND presentation is
/// the authoritative endpoint; DirectComposition only presents the transition pixels between the
/// sampled start and that endpoint.
/// </summary>
internal static class EdgeCapsuleQueueProxyPolicy
{
    public static bool IsEnabled => true;

    internal static bool AllowsQueueProxyOwnership(
        EdgeCapsuleGestureState gesture,
        bool floatingCoverActive) =>
        !floatingCoverActive &&
        gesture is
            EdgeCapsuleGestureState.Idle or
            EdgeCapsuleGestureState.PendingClick;

    public static EdgeCapsuleQueueProxyPlan? TryCreate(
        string queueKey,
        IReadOnlyList<EdgeCapsuleQueueProxyCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return Reject(queueKey, "no-candidates", candidates);
        }

        var changedCandidates = candidates
            .Where(candidate => !FramesVisuallyMatch(candidate.Start, candidate.Target))
            .ToArray();
        if (changedCandidates.Length == 0)
        {
            return Reject(queueKey, "no-visual-change", candidates);
        }

        // Motion.Reason is transaction bookkeeping. Preview may be staged first and then merged
        // with Placement; immutable surface/geometry identity is the authoritative admission proof.
        var ownsPreviewPixels = changedCandidates.Any(candidate =>
            candidate.Start.Surface == EdgeCapsuleSurfaceKind.DockedPreview ||
            candidate.Source.Surface == EdgeCapsuleSurfaceKind.DockedPreview ||
            candidate.Target.Surface == EdgeCapsuleSurfaceKind.DockedPreview);
        var ownsPointerMorph = changedCandidates.Any(candidate =>
            candidate.Motion.Reason == EdgeCapsuleTransitionReason.Pointer);
        if (!ownsPreviewPixels && !ownsPointerMorph)
        {
            return Reject(
                queueKey,
                "no-preview-or-pointer-transition",
                candidates);
        }

        foreach (var candidate in changedCandidates)
        {
            var rejection = ChangedCandidateRejection(candidate, queueKey);
            if (rejection != null)
            {
                return Reject(queueKey, rejection, candidates, candidate);
            }
        }

        // A successor replaces the complete predecessor root. Every predecessor-owned source must
        // therefore appear in the new root even when its business frame is currently unchanged.
        var ownedCandidates = candidates
            .Where(candidate =>
                !FramesVisuallyMatch(candidate.Start, candidate.Target) ||
                candidate.RetainedByCurrentProxy)
            .ToArray();
        var members = ownedCandidates
            .Select(candidate => new EdgeCapsuleQueueProxyMemberPlan(
                candidate.PaperId,
                candidate.Start,
                candidate.Source,
                candidate.Target,
                RoleFor(candidate),
                candidate.LegacyRegressionGeometry))
            .ToArray();

        foreach (var member in members)
        {
            var rejection = MemberGeometryRejection(member);
            if (rejection != null)
            {
                return Reject(
                    queueKey,
                    rejection,
                    candidates,
                    candidates.First(candidate =>
                        string.Equals(
                            candidate.PaperId,
                            member.PaperId,
                            StringComparison.Ordinal)));
            }
        }

        var first = members[0];
        var mismatchedGeometry = ownedCandidates.FirstOrDefault(candidate =>
            candidate.Start.Edge != first.Start.Edge ||
            candidate.Source.Edge != first.Start.Edge ||
            candidate.Target.Edge != first.Start.Edge ||
            candidate.Start.WallDeviceX != first.Start.WallDeviceX ||
            candidate.Source.WallDeviceX != first.Start.WallDeviceX ||
            candidate.Target.WallDeviceX != first.Start.WallDeviceX ||
            Math.Abs(candidate.Start.DpiScaleX - first.Start.DpiScaleX) > 0.001 ||
            Math.Abs(candidate.Start.DpiScaleY - first.Start.DpiScaleY) > 0.001 ||
            Math.Abs(candidate.Source.DpiScaleX - first.Start.DpiScaleX) > 0.001 ||
            Math.Abs(candidate.Source.DpiScaleY - first.Start.DpiScaleY) > 0.001 ||
            Math.Abs(candidate.Target.DpiScaleX - first.Start.DpiScaleX) > 0.001 ||
            Math.Abs(candidate.Target.DpiScaleY - first.Start.DpiScaleY) > 0.001);
        if (!string.IsNullOrEmpty(mismatchedGeometry.PaperId))
        {
            return Reject(
                queueKey,
                "queue-geometry-mismatch",
                candidates,
                mismatchedGeometry);
        }

        var envelope = default(DeviceScreenRect);
        foreach (var member in members)
        {
            envelope = EdgeCapsuleQueueProxyGeometry.Union(
                envelope,
                member.Start.Bounds);
            envelope = EdgeCapsuleQueueProxyGeometry.Union(
                envelope,
                member.Source.Bounds);
            envelope = EdgeCapsuleQueueProxyGeometry.Union(
                envelope,
                member.Target.Bounds);
        }
        if (envelope.IsEmpty)
        {
            return Reject(queueKey, "empty-envelope", candidates);
        }

#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.admission outcome=accepted queue={queueKey} " +
            $"candidates={candidates.Count} changed={changedCandidates.Length} " +
            $"owned={members.Length} previewPixels={ownsPreviewPixels} " +
            $"pointerMorph={ownsPointerMorph} " +
            $"roles={string.Join(',', members.Select(member => $"{EdgeCapsulePerformanceDiagnostics.ShortId(member.PaperId)}:{member.Role}"))} " +
            $"motions={string.Join(',', changedCandidates.Select(candidate => $"{EdgeCapsulePerformanceDiagnostics.ShortId(candidate.PaperId)}:{candidate.Motion.Kind}/{candidate.Motion.Reason}"))} " +
            $"durationMs={changedCandidates.Max(candidate => candidate.Motion.DurationMilliseconds)}");
#endif
        return new EdgeCapsuleQueueProxyPlan(
            queueKey,
            envelope,
            first.Start.Edge,
            first.Start.WallDeviceX,
            first.Start.DpiScaleX,
            first.Start.DpiScaleY,
            Math.Max(
                1,
                changedCandidates.Max(candidate =>
                    candidate.Motion.DurationMilliseconds)),
            Topmost: true,
            members);
    }

    private static string? ChangedCandidateRejection(
        EdgeCapsuleQueueProxyCandidate candidate,
        string queueKey)
    {
        if (!candidate.Topmost)
        {
            return "changed-member-not-topmost";
        }
        if (!candidate.HostReady)
        {
            return "changed-member-host-not-ready";
        }
        if (!candidate.Start.IsUsable)
        {
            return "changed-member-start-unusable";
        }
        if (!candidate.Source.IsUsable)
        {
            return "changed-member-source-unusable";
        }
        if (!candidate.Target.IsUsable)
        {
            return "changed-member-target-unusable";
        }
        if (!candidate.Start.Visible ||
            !candidate.Source.Visible ||
            !candidate.Target.Visible)
        {
            return "changed-member-hidden";
        }
        if (candidate.Start.Bounds.IsEmpty ||
            candidate.Source.Bounds.IsEmpty ||
            candidate.Target.Bounds.IsEmpty)
        {
            return "changed-member-empty-bounds";
        }
        if (candidate.Motion.Kind != EdgeCapsuleMotionKind.Animate)
        {
            return $"changed-member-motion-{candidate.Motion.Kind}";
        }
        if (!string.Equals(
                candidate.QueueKey,
                queueKey,
                StringComparison.Ordinal))
        {
            return "changed-member-queue-change";
        }
        return null;
    }

    private static string? MemberGeometryRejection(
        EdgeCapsuleQueueProxyMemberPlan member)
    {
        if (member.LegacyRegressionGeometry)
        {
            return member.Role == EdgeCapsuleQueueProxyMemberRole.MovingSource &&
                   !CanWrapMovingMemberLive(
                       member.Source,
                       member.Target)
                ? "moving-member-not-translation-only"
                : null;
        }

        switch (member.Role)
        {
            case EdgeCapsuleQueueProxyMemberRole.MovingSource:
                return CanWrapMovingMemberLive(
                    member.Source,
                    member.Target)
                    ? null
                    : "moving-member-not-translation-only";

            case EdgeCapsuleQueueProxyMemberRole.RevealTarget:
            case EdgeCapsuleQueueProxyMemberRole.RevealTargetWithSnapshot:
                return EdgeCapsuleQueueProxyGeometry.Contains(
                    member.Target.Bounds,
                    member.Start.Bounds)
                    ? null
                    : "reveal-target-does-not-contain-start";

            case EdgeCapsuleQueueProxyMemberRole.ConcealSource:
                return EdgeCapsuleQueueProxyGeometry.Contains(
                           member.Source.Bounds,
                           member.Start.Bounds) &&
                       EdgeCapsuleQueueProxyGeometry.Contains(
                           member.Source.Bounds,
                           member.Target.Bounds)
                    ? null
                    : "conceal-source-does-not-contain-frames";

            default:
                return "unsupported-member-role";
        }
    }

    private static EdgeCapsuleQueueProxyMemberRole RoleFor(
        EdgeCapsuleQueueProxyCandidate candidate)
    {
        var start = candidate.Start;
        var source = candidate.Source;
        var target = candidate.Target;

        if (candidate.LegacyRegressionGeometry)
        {
            if (start.Surface != EdgeCapsuleSurfaceKind.DockedPreview &&
                target.Surface == EdgeCapsuleSurfaceKind.DockedPreview)
            {
                return EdgeCapsuleQueueProxyMemberRole.RevealTargetWithSnapshot;
            }
            if (start.Surface == EdgeCapsuleSurfaceKind.DockedPreview &&
                target.Surface != EdgeCapsuleSurfaceKind.DockedPreview)
            {
                return EdgeCapsuleQueueProxyMemberRole.ConcealSource;
            }
            return EdgeCapsuleQueueProxyMemberRole.MovingSource;
        }

        if (FramesVisuallyMatch(start, target))
        {
            return EdgeCapsuleQueueProxyMemberRole.MovingSource;
        }

        if (CanWrapMovingMemberLive(source, target) &&
            source.Bounds.Width == start.Bounds.Width &&
            source.Bounds.Height == start.Bounds.Height)
        {
            return EdgeCapsuleQueueProxyMemberRole.MovingSource;
        }

        var closesPreview =
            (start.Surface == EdgeCapsuleSurfaceKind.DockedPreview ||
             source.Surface == EdgeCapsuleSurfaceKind.DockedPreview) &&
            target.Surface != EdgeCapsuleSurfaceKind.DockedPreview;
        var shrinksNativeSource =
            source.Bounds.Width >= target.Bounds.Width &&
            source.Bounds.Height >= target.Bounds.Height &&
            (source.Bounds.Width > target.Bounds.Width ||
             source.Bounds.Height > target.Bounds.Height) &&
            EdgeCapsuleQueueProxyGeometry.Contains(
                source.Bounds,
                target.Bounds);
        if (closesPreview || shrinksNativeSource)
        {
            return EdgeCapsuleQueueProxyMemberRole.ConcealSource;
        }

        // When the real HWND already equals the requested endpoint (the usual successor case), its
        // native surface can be revealed directly from the sampled predecessor clip. Otherwise the
        // first exact start frame is held by a 1:1 snapshot while the real endpoint is prepared.
        return FramesVisuallyMatch(source, target)
            ? EdgeCapsuleQueueProxyMemberRole.RevealTarget
            : EdgeCapsuleQueueProxyMemberRole.RevealTargetWithSnapshot;
    }

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
        var transition = new EdgeCapsuleTransition(
            member.Start,
            new EdgeCapsuleTargetPresentation(
                member.Target.Visible,
                member.Target.Surface,
                member.Target.Bounds,
                member.Target.HostBounds,
                member.Target.InteractiveBounds,
                member.Target.Edge,
                member.Target.BodyWindowWidthDevice,
                member.Target.WallDeviceX,
                member.Target.DpiScaleX,
                member.Target.DpiScaleY,
                member.Target.MaximumCloseWidthDip,
                member.Target.Opacity,
                member.Target.ContentOpacity,
                member.Target.OutlineVisible,
                member.Target.IsHitTestVisible,
                member.Target.CloseSegmentActsAsContent),
            startedAtTimestamp,
            durationTicks,
            EdgeCapsuleTransitionReason.Preview);
        return EdgeCapsuleTransitionPolicy
            .Sample(transition, nowTimestamp)
            .Frame;
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

    private static bool FramesVisuallyMatch(
        EdgeCapsulePresentationFrame first,
        EdgeCapsulePresentationFrame second) =>
        first == second;

    private static bool CanWrapMovingMemberLive(
        EdgeCapsulePresentationFrame source,
        EdgeCapsulePresentationFrame target) =>
        source.Surface == target.Surface &&
        source.Bounds.Width == target.Bounds.Width &&
        source.Bounds.Height == target.Bounds.Height &&
        source.BodyWindowWidthDevice == target.BodyWindowWidthDevice &&
        Math.Abs(source.Opacity - target.Opacity) < 0.001 &&
        Math.Abs(source.ContentOpacity - target.ContentOpacity) < 0.001 &&
        Math.Abs(
            source.MaximumCloseWidthDip -
            target.MaximumCloseWidthDip) < 0.001 &&
        source.OutlineVisible == target.OutlineVisible &&
        source.IsHitTestVisible == target.IsHitTestVisible &&
        source.CloseSegmentActsAsContent ==
            target.CloseSegmentActsAsContent &&
        InteractiveBoundsTranslateWithVisual(source, target);

    private static bool InteractiveBoundsTranslateWithVisual(
        EdgeCapsulePresentationFrame source,
        EdgeCapsulePresentationFrame target)
    {
        if (source.InteractiveBounds.IsEmpty ||
            target.InteractiveBounds.IsEmpty)
        {
            return source.InteractiveBounds.IsEmpty &&
                target.InteractiveBounds.IsEmpty;
        }
        var deltaX = target.Bounds.Left - source.Bounds.Left;
        var deltaY = target.Bounds.Top - source.Bounds.Top;
        return target.InteractiveBounds == new DeviceScreenRect(
            source.InteractiveBounds.Left + deltaX,
            source.InteractiveBounds.Top + deltaY,
            source.InteractiveBounds.Right + deltaX,
            source.InteractiveBounds.Bottom + deltaY);
    }

    private static EdgeCapsuleQueueProxyPlan? Reject(
        string queueKey,
        string reason,
        IReadOnlyList<EdgeCapsuleQueueProxyCandidate> candidates,
        EdgeCapsuleQueueProxyCandidate? offending = null)
    {
#if DEBUG
        var changed = candidates
            .Where(candidate =>
                !FramesVisuallyMatch(
                    candidate.Start,
                    candidate.Target))
            .ToArray();
        var detail = offending is { } candidate
            ? $" paper={EdgeCapsulePerformanceDiagnostics.ShortId(candidate.PaperId)} " +
              $"motion={candidate.Motion.Kind}/{candidate.Motion.Reason} " +
              $"retained={candidate.RetainedByCurrentProxy} " +
              $"hostReady={candidate.HostReady} topmost={candidate.Topmost} " +
              $"start={candidate.Start.Surface}:{candidate.Start.Bounds.Left},{candidate.Start.Bounds.Top}," +
              $"{candidate.Start.Bounds.Width}x{candidate.Start.Bounds.Height} " +
              $"source={candidate.Source.Surface}:{candidate.Source.Bounds.Left},{candidate.Source.Bounds.Top}," +
              $"{candidate.Source.Bounds.Width}x{candidate.Source.Bounds.Height} " +
              $"target={candidate.Target.Surface}:{candidate.Target.Bounds.Left},{candidate.Target.Bounds.Top}," +
              $"{candidate.Target.Bounds.Width}x{candidate.Target.Bounds.Height}"
            : changed.Length == 0
                ? string.Empty
                : $" motions={string.Join(',', changed.Select(candidate => $"{EdgeCapsulePerformanceDiagnostics.ShortId(candidate.PaperId)}:{candidate.Motion.Kind}/{candidate.Motion.Reason}:{candidate.Start.Surface}->{candidate.Target.Surface}"))}";
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.admission outcome=rejected queue={queueKey} reason={reason} " +
            $"candidates={candidates.Count} changed={changed.Length}{detail}");
#endif
        return null;
    }
}
