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
        EdgeCapsuleProxySourceLease? sourceLease = null;
        EdgeCapsuleProxyNativeShape? shape = null;
        try
        {
            if (ShapeEnabled &&
                member.Window.TryAcquireProxySource(out sourceLease))
                sourceHandle = sourceLease!.SourceHandle;
#if DEBUG
            if (ShapeEnabled && sourceLease == null)
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"proxy.shape phase=source-unavailable session={_sessionOrdinal} paper={member.Plan.PaperId}");
#endif
#if DEBUG
            using (var observation = EdgeDiagnosticObservation.Begin("proxy.surface-acquire", member.Window))
#endif
                surface = AcquireLiveSurface(member, sourceHandle, sourceBounds);

            _device.CreateVisual(
                out IDCompositionVisual2 createdVisual).CheckError();
            visual = createdVisual;
            if (sourceLease == null) visual.SetContent(surface).CheckError();
            visual.SetBitmapInterpolationMode(
                BitmapInterpolationMode.Linear).CheckError();
            visual.SetBorderMode(BorderMode.Soft).CheckError();
            if (sourceLease != null)
            {
#if DEBUG
                using (var observation = EdgeDiagnosticObservation.Begin("proxy.shape-build", member.Window))
#endif
                    shape = new EdgeCapsuleProxyNativeShape(_device, visual, surface, sourceLease.Description);
#if DEBUG
                using (var observation = EdgeDiagnosticObservation.Begin("proxy.shape-static", member.Window))
#endif
                    shape.SetStatic(member.Plan.Start, sourceLease.Description);
            }

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
                TargetOffsetY = targetOffsetY,
                ShapeSource = sourceLease,
                Shape = shape,
                SourceInvalidated = OnProxySourceInvalidated,
                SourceUpdated = OnProxySourceUpdated
            };
            if (sourceLease != null)
            {
                sourceLease.CanReplacePreview = () => !_starting && !_finishing && !_successorHeld &&
                    _successorAdmissionCover == null &&
                    state.Shape?.CanReplacePreview(System.Diagnostics.Stopwatch.GetTimestamp()) == true;
                sourceLease.Invalidated += state.SourceInvalidated;
                sourceLease.Updated += state.SourceUpdated;
            }
            _visuals.Add(state);
            sourceLease = null;
            shape = null;
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
            try
            {
                try { shape?.Dispose(); } catch { }
                try { visual?.Dispose(); } catch { }
                try { surface?.Dispose(); } catch { }
            }
            finally { sourceLease?.Dispose(); }
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
            predecessor._cloakedRealSourceHandles.Contains(member.SourceHandle) &&
            member.Window.EdgeCapsuleQueueProxySourceHandle == member.SourceHandle)
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
        IntPtr OwnerHandle,
        DeviceScreenRect PresentedBounds,
        EdgeCapsulePresentationFrame Frame,
        EdgeCapsuleProxySourceDescription? ShapeDescription);

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
            if (!TrySamplePresentation(state.Member, timestamp, out var frame))
                throw new InvalidOperationException("A predecessor has no current published presentation.");
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
                state.Member.SourceHandle,
                bounds,
                frame,
                state.Shape?.Description));
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
                    !coveredHandles.Add(source.OwnerHandle))
                {
                    continue;
                }
                AddStaticCoverVisual(
                    resources,
                    source.Handle,
                    source.PresentedBounds,
                    ref reference,
                    source.Frame,
                    source.ShapeDescription);
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
                var visualState = _visuals.First(state => ReferenceEquals(state.Member.Window, member.Window));
                AddStaticCoverVisual(
                    resources,
                    visualState.PresentedSourceHandle,
                    bounds,
                    ref reference,
                    member.Plan.Start,
                    visualState.Shape?.Description);
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
        ref IDCompositionVisual? reference,
        EdgeCapsulePresentationFrame frame,
        EdgeCapsuleProxySourceDescription? shapeDescription)
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
            if (shapeDescription == null) visual.SetContent(surface).CheckError();
            visual.SetBitmapInterpolationMode(
                BitmapInterpolationMode.Linear).CheckError();
            visual.SetBorderMode(BorderMode.Soft).CheckError();
            if (shapeDescription != null)
            {
                var shape = new EdgeCapsuleProxyNativeShape(_device, visual, surface, shapeDescription);
                resources.Shapes.Add(shape);
                shape.SetStatic(frame, shapeDescription);
            }
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
            if (state.Shape != null && state.ShapeSource != null)
                state.Shape.SetStatic(frame, state.ShapeSource.Description);
        }
    }

    private void ConfigureAnimations(long absoluteBeginTimestamp)
    {
        foreach (var state in _visuals)
        {
            if (state.Shape != null)
            {
                var frame = state.Member.Window.TryGetEdgeCapsuleQueueProxyAppliedPresentation(out var applied)
                    ? applied : state.Member.Plan.Start;
                state.Shape.Update(frame, state.Member.Window.EdgeCapsuleTransitionSnapshot,
                    state.ShapeSource!.Description);
                TraceProxyShape(state, "start");
            }
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
        int durationMilliseconds) => CreateEaseOutCubicAnimation(
            _device, from, to, absoluteBeginTimestamp, durationMilliseconds);

    internal static IDCompositionAnimation CreateEaseOutCubicAnimation(
        IDCompositionDesktopDevice device,
        float from,
        float to,
        long absoluteBeginTimestamp,
        double durationMilliseconds)
    {
        var durationSeconds =
            Math.Max(0.001, durationMilliseconds / 1000.0);
        var delta = to - from;
        var animation = device.CreateAnimation();
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
