using SharpGen.Runtime;
using Vortice.DirectComposition;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
#if DEBUG
    private int _reusedLiveSurfaceCount;
#endif

    private VisualState AddVisual(
        EdgeCapsuleQueueCompositionProxyMember member,
        IntPtr sourceHandle,
        DeviceScreenRect sourceBounds,
        DeviceScreenRect startVisualBounds,
        DeviceScreenRect targetVisualBounds,
        IDCompositionVisual? referenceVisual)
    {
        IUnknown? surface = null;
        IDCompositionVisual? visual = null;
        try
        {
            surface = AcquireLiveSurface(member, sourceHandle, sourceBounds);

            _device.CreateVisual(
                out IDCompositionVisual2 createdVisual).CheckError();
            visual = createdVisual;
            visual.SetContent(surface).CheckError();
            visual.SetBitmapInterpolationMode(
                BitmapInterpolationMode.Linear).CheckError();
            visual.SetBorderMode(BorderMode.Soft).CheckError();

            var startOffsetX =
                startVisualBounds.Left - _outputBounds.Left;
            var startOffsetY =
                startVisualBounds.Top - _outputBounds.Top;
            var targetOffsetX =
                targetVisualBounds.Left - _outputBounds.Left;
            var targetOffsetY =
                targetVisualBounds.Top - _outputBounds.Top;
            visual.SetOffsetX(startOffsetX).CheckError();
            visual.SetOffsetY(startOffsetY).CheckError();

            _root.AddVisual(
                visual,
                insertAbove: true,
                referenceVisual!).CheckError();

            var state = new VisualState
            {
                Member = member,
                PresentedSourceHandle = sourceHandle,
                SourceBounds = sourceBounds,
                Surface = surface,
                Visual = visual,
                StartOffsetX = startOffsetX,
                StartOffsetY = startOffsetY,
                TargetOffsetX = targetOffsetX,
                TargetOffsetY = targetOffsetY
            };
            _visuals.Add(state);
            surface = null;
            visual = null;
#if DEBUG
            if (_visuals.Count == _members.Count)
            {
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"proxy.surface phase=acquired session={_sessionOrdinal} " +
                    $"queue={_plan.QueueKey} reused={_reusedLiveSurfaceCount} " +
                    $"created={_visuals.Count - _reusedLiveSurfaceCount}");
            }
#endif
            return state;
        }
        finally
        {
            visual?.Dispose();
            surface?.Dispose();
        }
    }

    private IUnknown AcquireLiveSurface(
        EdgeCapsuleQueueCompositionProxyMember member,
        IntPtr sourceHandle,
        DeviceScreenRect sourceBounds)
    {
        // Admission has already verified the live host's capacity. Reuse is narrower still:
        // the same paper HWND must remain owned by the reserved predecessor on this runtime.
        if (_predecessor is { } predecessor &&
            !predecessor._disposed &&
            !predecessor._coverLost &&
            !predecessor._sourcesReleased &&
            predecessor._successorHeld &&
            ReferenceEquals(predecessor._runtime, _runtime) &&
            predecessor._cloakedRealSourceHandles.Contains(sourceHandle) &&
            member.Window.EdgeCapsuleQueueProxySourceHandle == sourceHandle)
        {
            foreach (var previous in predecessor._visuals)
            {
                if (!ReferenceEquals(previous.Member.Window, member.Window) ||
                    previous.PresentedSourceHandle != sourceHandle ||
                    previous.SourceBounds.Width != sourceBounds.Width ||
                    previous.SourceBounds.Height != sourceBounds.Height ||
                    previous.Surface is not ComObject existing ||
                    existing.NativePointer == IntPtr.Zero)
                {
                    continue;
                }

                // QueryInterface(IUnknown) AddRefs the live surface and creates a distinct
                // wrapper. The new generation owns that reference; retiring the predecessor
                // releases only its reference. A later AddVisual failure disposes ours in its
                // existing finally block, without changing the predecessor or its pixels.
                var retained = existing.QueryInterface<ComObject>();
#if DEBUG
                _reusedLiveSurfaceCount++;
#endif
                return retained;
            }
        }

        _device.CreateSurfaceFromHwnd(sourceHandle, out var surface).CheckError();
        return surface;
    }

    private readonly record struct StaticCoverSource(
        IntPtr Handle,
        DeviceScreenRect PresentedBounds);

    private IReadOnlyList<StaticCoverSource> SnapshotStaticCoverSources(
        long timestamp)
    {
        if (_disposed || _coverLost || _sourcesReleased)
        {
            throw new InvalidOperationException(
                "A retired predecessor cannot seed a successor cover.");
        }

        var sources = new List<StaticCoverSource>(_visuals.Count);
        foreach (var state in _visuals)
        {
            var frame = EdgeCapsuleQueueProxyPolicy.SampleLogicalFrame(
                state.Member.Plan,
                AnimationStartedAtTimestamp,
                _plan.DurationMilliseconds,
                timestamp);
            var bounds =
                EdgeCapsuleQueueProxyPolicy.PresentedHostBounds(frame);
            if (bounds.IsEmpty ||
                bounds.Width != state.SourceBounds.Width ||
                bounds.Height != state.SourceBounds.Height)
            {
                throw new InvalidOperationException(
                    "A predecessor cover source changed live surface identity.");
            }

            sources.Add(new StaticCoverSource(
                state.PresentedSourceHandle,
                bounds));
        }
        return sources;
    }

    private StaticCoverResources CreateSuccessorAdmissionCover(
        long timestamp,
        IReadOnlySet<IntPtr> newHandles)
    {
        if (_predecessor == null || newHandles.Count == 0)
        {
            throw new InvalidOperationException(
                "A successor admission cover requires new real sources.");
        }

        IDCompositionVisual? root = null;
        StaticCoverResources? resources = null;
        try
        {
            _device.CreateVisual(
                out IDCompositionVisual2 rootVisual).CheckError();
            root = rootVisual;
            resources = new StaticCoverResources
            {
                Root = rootVisual
            };
            root = null;

            IDCompositionVisual? reference = null;
            var coveredHandles = new HashSet<IntPtr>();
            foreach (var source in
                     _predecessor.SnapshotStaticCoverSources(timestamp))
            {
                if (source.Handle == IntPtr.Zero ||
                    !coveredHandles.Add(source.Handle))
                {
                    continue;
                }
                AddStaticCoverVisual(
                    resources,
                    source.Handle,
                    source.PresentedBounds,
                    ref reference);
            }

            foreach (var member in _members)
            {
                if (!newHandles.Contains(member.SourceHandle) ||
                    !coveredHandles.Add(member.SourceHandle))
                {
                    continue;
                }
                var bounds = EdgeCapsuleQueueProxyPolicy
                    .PresentedHostBounds(member.Plan.Start);
                if (bounds.IsEmpty ||
                    bounds.Width != member.Plan.Source.HostBounds.Width ||
                    bounds.Height != member.Plan.Source.HostBounds.Height)
                {
                    throw new InvalidOperationException(
                        "A new successor source has no stable admission bounds.");
                }
                AddStaticCoverVisual(
                    resources,
                    member.SourceHandle,
                    bounds,
                    ref reference);
            }

            foreach (var handle in newHandles)
            {
                if (!coveredHandles.Contains(handle))
                {
                    throw new InvalidOperationException(
                        "A new successor source is missing from the union cover.");
                }
            }

            return resources;
        }
        catch
        {
            resources?.Dispose();
            root?.Dispose();
            throw;
        }
    }

    private void AddStaticCoverVisual(
        StaticCoverResources resources,
        IntPtr sourceHandle,
        DeviceScreenRect presentedBounds,
        ref IDCompositionVisual? reference)
    {
        IUnknown? surface = null;
        IDCompositionVisual? visual = null;
        try
        {
            _device.CreateSurfaceFromHwnd(
                sourceHandle,
                out var createdSurface).CheckError();
            surface = createdSurface;
            _device.CreateVisual(
                out IDCompositionVisual2 createdVisual).CheckError();
            visual = createdVisual;
            visual.SetContent(surface).CheckError();
            visual.SetBitmapInterpolationMode(
                BitmapInterpolationMode.Linear).CheckError();
            visual.SetBorderMode(BorderMode.Soft).CheckError();
            visual.SetOffsetX(
                presentedBounds.Left - _outputBounds.Left).CheckError();
            visual.SetOffsetY(
                presentedBounds.Top - _outputBounds.Top).CheckError();
            resources.Root.AddVisual(
                visual,
                insertAbove: true,
                reference!).CheckError();
            resources.Surfaces.Add(surface);
            resources.Visuals.Add(visual);
            reference = visual;
            surface = null;
            visual = null;
        }
        finally
        {
            visual?.Dispose();
            surface?.Dispose();
        }
    }

    private void ReleaseSuccessorAdmissionCover()
    {
        var cover = _successorAdmissionCover;
        _successorAdmissionCover = null;
        cover?.Dispose();
    }

    private void RebaseVisualStarts(long timestamp)
    {
        foreach (var state in _visuals)
        {
            var frame = state.Member.Plan.Start;
            if (_predecessor?.TryGetPresentationAt(
                    state.Member.Window,
                    timestamp,
                    out var sampled) == true)
            {
                frame = sampled;
            }

            var startBounds =
                EdgeCapsuleQueueProxyPolicy.PresentedHostBounds(frame);
            if (startBounds.IsEmpty ||
                startBounds.Width != state.SourceBounds.Width ||
                startBounds.Height != state.SourceBounds.Height)
            {
                throw new InvalidOperationException(
                    "A successor cannot change live surface identity.");
            }

            state.StartOffsetX =
                startBounds.Left - _outputBounds.Left;
            state.StartOffsetY =
                startBounds.Top - _outputBounds.Top;
            state.Visual.SetOffsetX(
                state.StartOffsetX).CheckError();
            state.Visual.SetOffsetY(
                state.StartOffsetY).CheckError();
        }
    }

    private EdgeCapsuleQueueInputAnimationTicket? ConfigureAnimations(long absoluteBeginTimestamp)
    {
        foreach (var state in _visuals)
        {
            state.OffsetXAnimation = ApplyAnimatedValue(
                state.StartOffsetX,
                state.TargetOffsetX,
                value => state.Visual.SetOffsetX(value),
                animation => state.Visual.SetOffsetX(animation),
                absoluteBeginTimestamp);
            state.OffsetYAnimation = ApplyAnimatedValue(
                state.StartOffsetY,
                state.TargetOffsetY,
                value => state.Visual.SetOffsetY(value),
                animation => state.Visual.SetOffsetY(animation),
                absoluteBeginTimestamp);
        }

        if (!RoutesPointerInput) return null;
        var inputMembers = _members
            .Where(member => member.Window.CanRouteEdgeCapsuleQueueProxyInput)
            .Select(member => member.Plan)
            .ToArray();
        return inputMembers.Length == 0
            ? null
            : new EdgeCapsuleQueueInputAnimationTicket(
                absoluteBeginTimestamp,
                _plan.DurationMilliseconds,
                inputMembers);
    }

    private IDCompositionAnimation? ApplyAnimatedValue(
        float from,
        float to,
        Func<float, SharpGen.Runtime.Result> applyStatic,
        Func<IDCompositionAnimation, SharpGen.Runtime.Result>
            applyAnimation,
        long absoluteBeginTimestamp)
    {
        if (Math.Abs(from - to) < 0.001f)
        {
            applyStatic(to).CheckError();
            return null;
        }

        var animation = CreateEaseOutCubicAnimation(
            from,
            to,
            absoluteBeginTimestamp,
            _plan.DurationMilliseconds);
        try
        {
            applyAnimation(animation).CheckError();
            return animation;
        }
        catch
        {
            animation.Dispose();
            throw;
        }
    }

    private IDCompositionAnimation CreateEaseOutCubicAnimation(
        float from,
        float to,
        long absoluteBeginTimestamp,
        int durationMilliseconds)
    {
        var durationSeconds =
            Math.Max(0.001, durationMilliseconds / 1000.0);
        var delta = to - from;
        var animation = _device.CreateAnimation();
        try
        {
            animation.SetAbsoluteBeginTime(
                absoluteBeginTimestamp).CheckError();
            animation.AddCubic(
                0,
                from,
                (float)(3 * delta / durationSeconds),
                (float)(-3 * delta /
                    (durationSeconds * durationSeconds)),
                (float)(delta /
                    (durationSeconds * durationSeconds *
                     durationSeconds))).CheckError();
            animation.End(durationSeconds, to).CheckError();
            return animation;
        }
        catch
        {
            animation.Dispose();
            throw;
        }
    }
}