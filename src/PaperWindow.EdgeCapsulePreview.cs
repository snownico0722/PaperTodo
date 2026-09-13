using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private readonly EdgeCapsulePreviewInvalidationSource
        _edgeCapsulePreviewInvalidationSource = new();
    private EdgeCapsulePreviewRequest? _edgeCapsulePreviewRequest;
    private EdgeCapsulePreviewSize? _edgeCapsulePendingPreviewCapacity;
    private int _edgeCapsulePreviewContentGeneration;
    private EventHandler? _edgeCapsulePreviewDeferredContentRenderHandler;

    internal PaperData EdgeCapsulePreviewPaper => _paper;
    internal string EdgeCapsulePreviewPaperId => _paper.Id;
    internal bool IsEdgeCapsulePointerOver =>
        _edgeCapsule.PointerOverSurface;
    internal bool IsEdgeCapsulePreviewOpen =>
        _edgeCapsule.Preview == EdgeCapsulePreviewState.Open;
    internal bool HasEdgeCapsulePreviewContent =>
        _edgeCapsulePreviewRequest != null ||
        _edgeCapsuleHost?.HasPreviewContent == true;
    internal bool EdgeCapsulePreviewPointerCaptureActive =>
        _edgeCapsuleHost?.IsPreviewPointerCaptureActive == true;

    internal bool CanEnterEdgeCapsulePreview =>
        _controller.State.ExperimentalEdgeCapsuleHoverPreview &&
        _windowLifecycle == PaperWindowLifecycleState.Alive &&
        _paper.IsVisible &&
        !IsExperimentalPassive &&
        !_advancedInteractionLocked &&
        HasDeepCapsuleSlotPlacement &&
        !IsDeepCapsuleRetractedIntoMaster &&
        !IsDeepCapsuleSlotRetracting &&
        !IsDeepCapsuleReordering &&
        !_edgeCapsule.PeerReorderActive &&
        !IsDeepCapsuleDockingHandoff &&
        CurrentEdgeCapsuleVisualAuthority is (
            EdgeCapsuleVisualAuthority.RealDocked or
            EdgeCapsuleVisualAuthority.QueueTranslation) &&
        (!_edgeCapsule.ContextMenuOpen || IsEdgeCapsulePreviewOpen) &&
        (EdgeCapsuleGesture == EdgeCapsuleGestureState.Idle ||
         (IsEdgeCapsulePreviewOpen &&
          EdgeCapsuleGesture == EdgeCapsuleGestureState.PendingClick)) &&
        (EdgeCapsuleSlot is
            EdgeCapsuleSlotState.CollapsedDocked or
            EdgeCapsuleSlotState.ExpandedReserved);

    internal EdgeCapsulePreviewRequest? PrepareEdgeCapsulePreview()
    {
        if (!CanEnterEdgeCapsulePreview)
        {
            return null;
        }
        MarkdownEdgePreviewPreload.For(Dispatcher).BeginDemand();
        var prepareStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        var bodySessionGeneration = _bodySessionGeneration;

        IEdgeCapsulePreviewProvider? provider = null;
        try
        {
            provider = ResolveEdgeCapsulePreviewProvider();
            var context = CreateEdgeCapsulePreviewContext();
            var describeStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
            var descriptor = provider.Describe(context);
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.prepare.describe paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"provider={provider.GetType().Name} " +
                $"ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(describeStartedAt):F3} " +
                $"deferred={descriptor.DeferContentCreation}");
            if (bodySessionGeneration != _bodySessionGeneration ||
                !CanEnterEdgeCapsulePreview)
            {
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"preview.prepare.cancel paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                    "phase=describe-invalidated");
                return null;
            }
            var monitor = DeepCapsuleMonitorGeometry().LocalWorkAreaDip;
            var size = descriptor.Size.Normalize(
                Math.Max(1, monitor.Width - 16),
                Math.Max(1, monitor.Height - 16));
            if (!PrepareEdgeCapsuleHostCapacity(size))
            {
                if (!TryConstrainEdgeCapsulePreviewToCurrentHostCapacity(
                        size,
                        out var constrainedSize))
                {
                    EdgeCapsulePerformanceDiagnostics.Trace(
                        $"preview.prepare.cancel paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                        "phase=capacity-unavailable");
                    return null;
                }

                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"preview.prepare.capacity-constrained paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                    $"requested={size.WidthDip:F1}x{size.HeightDip:F1} " +
                    $"effective={constrainedSize.WidthDip:F1}x{constrainedSize.HeightDip:F1}");
                RememberEdgeCapsulePreviewCapacityRequest(size);
                size = constrainedSize;
            }
            // Growing a retained live source crosses the verified handoff boundary, which may
            // pump pending input/layout. Revalidate before invoking provider content creation.
            if (bodySessionGeneration != _bodySessionGeneration ||
                !CanEnterEdgeCapsulePreview)
            {
                return null;
            }
            // Native/Web/migration providers may execute arbitrary plugin code or construct a
            // WebView. Paint the host-owned 1.6/1.7 fallback first, then replace it only after the
            // visual transaction has produced a committed compositor frame. Creation failure keeps
            // this useful fallback instead of leaving a permanent loading ellipsis.
            var deferProviderContent = descriptor.DeferContentCreation;
            var contentStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
            var content = deferProviderContent
                ? BuildPluginCapsuleEdgePreviewContent(context, size)
                : descriptor.CreateContent(size);
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.prepare.content paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"provider={provider.GetType().Name} deferred={deferProviderContent} " +
                $"ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(contentStartedAt):F3} " +
                $"type={content?.GetType().Name ?? "<null>"}");
            if (bodySessionGeneration != _bodySessionGeneration ||
                !CanEnterEdgeCapsulePreview ||
                content == null ||
                !IsValidEdgeCapsulePreviewContent(content))
            {
                Trace.TraceWarning(
                    "Edge capsule preview provider returned invalid content. " +
                    "PaperId={0}; PaperType={1}; Provider={2}; ContentType={3}",
                    _paper.Id,
                    _paper.Type,
                    provider.GetType().FullName,
                    content?.GetType().FullName ?? "<null>");
                return null;
            }

            content.HorizontalAlignment = HorizontalAlignment.Stretch;
            content.VerticalAlignment = VerticalAlignment.Stretch;
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.prepare.ready paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(prepareStartedAt):F3} " +
                $"size={size.WidthDip:F1}x{size.HeightDip:F1} deferred={deferProviderContent}");
            return new EdgeCapsulePreviewRequest(
                size,
                content,
                descriptor.SetVisibility,
                descriptor.PrepareForActivation,
                deferProviderContent
                    ? () => descriptor.CreateContent(size)
                    : null);
        }
        catch (Exception ex)
        {
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.prepare.fail paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(prepareStartedAt):F3} " +
                $"provider={provider?.GetType().Name ?? "<unresolved>"} " +
                $"exception={ex.GetType().Name}");
            Trace.TraceError(
                "Edge capsule preview creation failed. " +
                "PaperId={0}; PaperType={1}; Provider={2}; Exception={3}",
                _paper.Id,
                _paper.Type,
                provider?.GetType().FullName ?? "<unresolved>",
                ex);
            return null;
        }
    }

    private bool TryConstrainEdgeCapsulePreviewToCurrentHostCapacity(
        EdgeCapsulePreviewSize requested,
        out EdgeCapsulePreviewSize constrained)
    {
        constrained = default;
        if (_edgeCapsuleHost?.IsVisible != true ||
            !HasDeepCapsuleSlotPlacement)
        {
            return false;
        }

        var monitor = DeepCapsuleMonitorGeometry();
        var generationMatches =
            string.Equals(
                _edgeCapsuleHostCapacityMonitor,
                monitor.DeviceName,
                StringComparison.Ordinal) &&
            _edgeCapsuleHostCapacityEdge == MyDeepCapsuleEdge &&
            Math.Abs(_edgeCapsuleHostCapacityDpiX - monitor.DpiScaleX) < 0.001 &&
            Math.Abs(_edgeCapsuleHostCapacityDpiY - monitor.DpiScaleY) < 0.001;
        if (!generationMatches)
        {
            return false;
        }

        var availableWidth = Math.Max(1, _edgeCapsuleHostCapacityWidthDip);
        var availableHeight = Math.Max(1, _edgeCapsuleHostCapacityHeightDip);
        constrained = new EdgeCapsulePreviewSize(
            Math.Min(requested.WidthDip, availableWidth),
            Math.Min(requested.HeightDip, availableHeight));
        return double.IsFinite(constrained.WidthDip) &&
            double.IsFinite(constrained.HeightDip) &&
            constrained.WidthDip > 0 &&
            constrained.HeightDip > 0;
    }

    private void RememberEdgeCapsulePreviewCapacityRequest(
        EdgeCapsulePreviewSize requested)
    {
        if (_edgeCapsulePendingPreviewCapacity is not { } pending)
        {
            _edgeCapsulePendingPreviewCapacity = requested;
            return;
        }

        _edgeCapsulePendingPreviewCapacity = new EdgeCapsulePreviewSize(
            Math.Max(pending.WidthDip, requested.WidthDip),
            Math.Max(pending.HeightDip, requested.HeightDip));
    }

    private EdgeCapsulePreviewContext CreateEdgeCapsulePreviewContext() =>
        new(
            _paper,
            () => _controller.PaperTitleText(_paper),
            !_paper.IsCollapsed,
            CurrentMarkdownTextForEdgeCapsulePreview,
            () => _controller.State.MarkdownRenderMode,
            SetTodoDoneFromEdgeCapsulePreview,
            OpenTodoLinkedTargetFromEdgeCapsulePreview,
            CurrentTodoCheckBoxStyle,
            CurrentPluginStatusForEdgeCapsulePreview,
            OpenExternalFromEdgeCapsulePreview,
            _edgeCapsulePreviewInvalidationSource,
            () => CanPreloadMarkdownText && _controller.PreloadAllEdgeMarkdownNotes);

    // Source HWNDs and the queue output reserve the same product maximum before publication.
    // Reading a smaller current descriptor here would force the first transfer to a larger legal
    // mini through source resize even when the output already has enough capacity.
    private void ReserveEdgeCapsulePreviewCapacityBeforeFirstShow()
    {
        // This reads capacity metadata only, never Native PreferredMiniViewSize or preview content.
        // An undeclared Native maximum can still grow at first describe through verified handoff.
        if (_edgeCapsuleHost?.IsVisible == true)
        {
            return;
        }
        TryReserveEdgeCapsulePreviewMaximumCapacity(out _);
    }

    internal bool PrepareEdgeCapsulePreacquisitionCapacity(out bool changed)
    {
        changed = false;
        if (!CanPreacquireEdgeCapsuleSource ||
            !TryReserveEdgeCapsulePreviewMaximumCapacity(out changed)) return false;
        if (changed) InvalidateEdgeCapsule(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation);
        // Growing an already visible source needs a fresh applied/native frame before wrapping it.
        return !changed;
    }

    private bool TryReserveEdgeCapsulePreviewMaximumCapacity(out bool changed)
    {
        changed = false;
        if (!_controller.State.ExperimentalEdgeCapsuleHoverPreview ||
            _windowLifecycle != PaperWindowLifecycleState.Alive ||
            !_paper.IsVisible ||
            !HasDeepCapsuleSlotPlacement ||
            IsDeepCapsuleRetractedIntoMaster ||
            IsDeepCapsuleSlotRetracting)
        {
            return false;
        }

        ScheduleEdgeCapsuleCompositionPrewarm();

        var generation = _bodySessionGeneration;
        try
        {
            var workArea = DeepCapsuleMonitorGeometry().LocalWorkAreaDip;
            var maximumWidth = Math.Max(1, workArea.Width - 16);
            var maximumHeight = Math.Max(1, workArea.Height - 16);
            var size = ResolveEdgeCapsulePreviewMaximumCapacity().Normalize(
                maximumWidth,
                maximumHeight);
            if (generation != _bodySessionGeneration ||
                !HasDeepCapsuleSlotPlacement)
            {
                return false;
            }

            var pendingCapacity = _edgeCapsulePendingPreviewCapacity;
            if (pendingCapacity is { } pending)
            {
                var pendingSize = pending.Normalize(maximumWidth, maximumHeight);
                // A pending title or dynamic mini demand may exceed the metadata default; a
                // smaller pending request must not replace the product maximum reservation.
                size = new EdgeCapsulePreviewSize(
                    Math.Max(size.WidthDip, pendingSize.WidthDip),
                    Math.Max(size.HeightDip, pendingSize.HeightDip));
            }
            var reserved = TryReserveEdgeCapsuleHostCapacity(
                size,
                out changed);
            if (reserved && pendingCapacity.HasValue &&
                _edgeCapsulePendingPreviewCapacity == pendingCapacity)
            {
                _edgeCapsulePendingPreviewCapacity = null;
            }
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.capacity.reserve paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"provider=MaximumCapacity size={size.WidthDip:F1}x{size.HeightDip:F1} " +
                $"reserved={reserved} changed={changed} " +
                $"hostVisible={_edgeCapsuleHost?.IsVisible == true}");
            return reserved;
        }
        catch (Exception ex)
        {
            // Capacity warmup is opportunistic. Opening still performs a fresh descriptor read;
            // a larger request may resize the live host, or temporarily use the current capacity
            // when a queue proxy is already retaining that HWND as its live source.
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.capacity.reserve-fail paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                "provider=MaximumCapacity " +
                $"exception={ex.GetType().Name}");
            return false;
        }
    }

    private bool IsValidEdgeCapsulePreviewContent(FrameworkElement content) =>
        content is not Window &&
        (content.Parent == null ||
         _edgeCapsuleHost?.OwnsPreviewContent(content) == true);

    // Built-in papers and protocol 1.8 plugin mini/fallback adapters enter through the same seam;
    // queue, host, transition and input policy stay independent of the content provider.
    private IEdgeCapsulePreviewProvider ResolveEdgeCapsulePreviewProvider()
    {
        if (_paper.Type == PaperTypes.Todo)
        {
            return TodoEdgeCapsulePreviewProvider.Instance;
        }
        if (_paper.Type == PaperTypes.Note && IsCurrentBodyProviderMarkdown)
        {
            return MarkdownEdgeCapsulePreviewProvider.Instance;
        }
        if (_paper.Type == PaperTypes.Note &&
            !IsCurrentBodyProviderMarkdown &&
            !_bodyFailed &&
            _bodyDescriptor != null)
        {
            return new PluginEdgeCapsulePreviewProvider(this);
        }
        return DefaultEdgeCapsulePreviewProvider.Instance;
    }

    internal bool SetEdgeCapsulePreviewOpen(
        EdgeCapsulePreviewRequest request,
        bool animate)
    {
        if (!PrepareEdgeCapsuleHostCapacity(request.Size))
        {
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.open.reject paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                "reason=active-proxy-capacity-growth");
            return false;
        }
        if (!CanEnterEdgeCapsulePreview ||
            !_controller.IsEdgeCapsulePreviewOwner(this))
        {
            return false;
        }

        // Keep an in-flight queue translation alive. The visual transaction below promotes
        // the preview generation as its successor, avoiding an exposed real-HWND frame between
        // hover and expansion.
        var openStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        if (!DispatchEdgeCapsuleIntent(
                EdgeCapsuleIntent.PreviewChanged(open: true),
                EdgeCapsuleDirty.None))
        {
            return false;
        }

        if (!ReferenceEquals(_edgeCapsulePreviewRequest, request))
        {
            var previousRequest = _edgeCapsulePreviewRequest;
            var previousGeneration = _edgeCapsulePreviewContentGeneration;
            NotifyEdgeCapsulePreviewVisibility(previousRequest, visible: false);
            if (previousGeneration != _edgeCapsulePreviewContentGeneration ||
                !ReferenceEquals(_edgeCapsulePreviewRequest, previousRequest))
            {
                if (ReferenceEquals(
                        _edgeCapsulePreviewRequest,
                        previousRequest) &&
                    IsEdgeCapsulePreviewOpen)
                {
                    _ = DispatchEdgeCapsuleIntent(
                        EdgeCapsuleIntent.PreviewChanged(open: false),
                        EdgeCapsuleDirty.None);
                }
                return false;
            }
        }
        CancelDeferredEdgeCapsulePreviewContentRenderWait();
        var contentGeneration = ++_edgeCapsulePreviewContentGeneration;
        _edgeCapsulePreviewRequest = request;
        var previewContentSize = request.Size.ContentSize;
        var host = EnsureDeepCapsuleSlotHost();
        var stageStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        var staged = host.StagePreviewContent(
            request.Content,
            previewContentSize.Width,
            previewContentSize.Height);
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"preview.open.stage paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
            $"ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(stageStartedAt):F3} " +
            $"content={request.Content.GetType().Name} staged={staged} " +
            $"layoutValid={request.Content.IsMeasureValid && request.Content.IsArrangeValid}");
        var requestStillCurrent =
            _windowLifecycle == PaperWindowLifecycleState.Alive &&
            contentGeneration == _edgeCapsulePreviewContentGeneration &&
            ReferenceEquals(_edgeCapsulePreviewRequest, request) &&
            IsEdgeCapsulePreviewOpen &&
            _controller.IsEdgeCapsulePreviewOwner(this);
        if (!staged || !requestStillCurrent)
        {
            if (ReferenceEquals(_edgeCapsulePreviewRequest, request))
            {
                if (host.OwnsPreviewContent(request.Content))
                {
                    host.ClearPreviewContent();
                }
                if (contentGeneration != _edgeCapsulePreviewContentGeneration ||
                    !ReferenceEquals(_edgeCapsulePreviewRequest, request))
                {
                    return false;
                }
                _edgeCapsulePreviewRequest = null;
                _edgeCapsulePreviewContentGeneration++;
                if (IsEdgeCapsulePreviewOpen)
                {
                    _ = DispatchEdgeCapsuleIntent(
                        EdgeCapsuleIntent.PreviewChanged(open: false),
                        EdgeCapsuleDirty.None);
                }
            }
            return false;
        }
        if (request.CreateDeferredContent == null)
        {
            var visibilityStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
            NotifyEdgeCapsulePreviewVisibility(request, visible: true);
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.open.visibility paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"visible=true ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(visibilityStartedAt):F3}");
            if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
                contentGeneration != _edgeCapsulePreviewContentGeneration ||
                !ReferenceEquals(_edgeCapsulePreviewRequest, request) ||
                !IsEdgeCapsulePreviewOpen ||
                !_controller.IsEdgeCapsulePreviewOwner(this))
            {
                if (ReferenceEquals(_edgeCapsulePreviewRequest, request))
                {
                    if (host.OwnsPreviewContent(request.Content))
                    {
                        host.ClearPreviewContent();
                    }
                    if (contentGeneration == _edgeCapsulePreviewContentGeneration &&
                        ReferenceEquals(_edgeCapsulePreviewRequest, request))
                    {
                        NotifyEdgeCapsulePreviewVisibility(request, visible: false);
                    }
                }
                if (contentGeneration == _edgeCapsulePreviewContentGeneration &&
                    ReferenceEquals(_edgeCapsulePreviewRequest, request))
                {
                    _edgeCapsulePreviewRequest = null;
                    _edgeCapsulePreviewContentGeneration++;
                    if (IsEdgeCapsulePreviewOpen)
                    {
                        _ = DispatchEdgeCapsuleIntent(
                            EdgeCapsuleIntent.PreviewChanged(open: false),
                            EdgeCapsuleDirty.None);
                    }
                }
                return false;
            }
        }

        // Preview open/transfer/close is one cross-window visual transaction. The owner state is
        // staged now; the immediate ArrangeDeepCapsules call stages every peer placement into the
        // same transaction, then one Send-priority commit creates all transitions before rendering.
        _controller.BeginEdgeCapsuleVisualTransaction(this);
        _ = TryStageEdgeCapsuleVisualTransaction(
            animate,
            EdgeCapsuleTransitionReason.Preview,
            EdgeCapsuleLayout.SlotMoveMilliseconds,
            refreshLayout: true);
        QueueDeferredEdgeCapsulePreviewContent(
            request,
            contentGeneration,
            _edgeCapsule.NativeBatchCommitVersion);
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"preview.open.queued paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
            $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(openStartedAt):F3} " +
            $"animate={animate} deferred={request.CreateDeferredContent != null}");
        return true;
    }

    private void QueueDeferredEdgeCapsulePreviewContent(
        EdgeCapsulePreviewRequest request,
        int contentGeneration,
        int queuedAtNativeBatchCommitVersion)
    {
        if (request.CreateDeferredContent is not { } createContent)
        {
            return;
        }
        var deferredQueuedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();

        CancelDeferredEdgeCapsulePreviewContentRenderWait();
        EventHandler? renderHandler = null;
        renderHandler = (_, _) =>
        {
            if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
                contentGeneration != _edgeCapsulePreviewContentGeneration ||
                !ReferenceEquals(_edgeCapsulePreviewRequest, request) ||
                !IsEdgeCapsulePreviewOpen ||
                !_controller.IsEdgeCapsulePreviewOwner(this))
            {
                if (renderHandler != null)
                {
                    CompositionTarget.Rendering -= renderHandler;
                }
                if (ReferenceEquals(
                        _edgeCapsulePreviewDeferredContentRenderHandler,
                        renderHandler))
                {
                    _edgeCapsulePreviewDeferredContentRenderHandler = null;
                }
                return;
            }

            // The fallback must have belonged to a successfully committed native batch before
            // this Rendering can count as its first compositor pass. A failed transaction keeps
            // the handler armed across coordinated retries; exhaustion leaves it armed until a
            // later explicit successful replay or request cancellation.
            if (_edgeCapsule.NativeBatchCommitVersion ==
                queuedAtNativeBatchCommitVersion)
            {
                return;
            }

            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.deferred.first-frame paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"waitMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(deferredQueuedAt):F3}");

            if (renderHandler != null)
            {
                CompositionTarget.Rendering -= renderHandler;
            }
            if (ReferenceEquals(
                    _edgeCapsulePreviewDeferredContentRenderHandler,
                    renderHandler))
            {
                _edgeCapsulePreviewDeferredContentRenderHandler = null;
            }

            // Run after the Render-priority event returns, so the host-owned fallback has
            // completed one genuine WPF composition pass before arbitrary plugin code can block UI.
            _ = Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
                        contentGeneration != _edgeCapsulePreviewContentGeneration ||
                        !ReferenceEquals(_edgeCapsulePreviewRequest, request) ||
                        !IsEdgeCapsulePreviewOpen ||
                        !_controller.IsEdgeCapsulePreviewOwner(this))
                    {
                        return;
                    }

                    FrameworkElement? content;
                    var createStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
                    try
                    {
                        content = createContent();
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceError(
                            "Deferred edge capsule preview creation failed. " +
                            "PaperId={0}; PaperType={1}; Exception={2}",
                            _paper.Id,
                            _paper.Type,
                            ex);
                        return;
                    }
                    EdgeCapsulePerformanceDiagnostics.Trace(
                        $"preview.deferred.create paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                        $"ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(createStartedAt):F3} " +
                        $"content={content?.GetType().Name ?? "<null>"}");

                    // Plugin code is allowed to pump messages. A close or another transfer may have
                    // invalidated this request while CreateContent was on the stack.
                    if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
                        contentGeneration != _edgeCapsulePreviewContentGeneration ||
                        !ReferenceEquals(_edgeCapsulePreviewRequest, request) ||
                        !IsEdgeCapsulePreviewOpen ||
                        !_controller.IsEdgeCapsulePreviewOwner(this))
                    {
                        return;
                    }

                    if (content == null ||
                        !IsValidEdgeCapsulePreviewContent(content))
                    {
                        Trace.TraceWarning(
                            "Deferred edge capsule preview provider returned invalid content. " +
                            "PaperId={0}; PaperType={1}; ContentType={2}",
                            _paper.Id,
                            _paper.Type,
                            content?.GetType().FullName ?? "<null>");
                        return;
                    }

                    content.HorizontalAlignment = HorizontalAlignment.Stretch;
                    content.VerticalAlignment = VerticalAlignment.Stretch;
                    var previewContentSize = request.Size.ContentSize;
                    var host = EnsureDeepCapsuleSlotHost();
                    var replaceStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
                    if (!host.ReplacePreviewContent(
                            request.Content,
                            content,
                            previewContentSize.Width,
                            previewContentSize.Height))
                    {
                        return;
                    }
                    EdgeCapsulePerformanceDiagnostics.Trace(
                        $"preview.deferred.replace paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                        $"ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(replaceStartedAt):F3} " +
                        $"layoutValid={content.IsMeasureValid && content.IsArrangeValid}");

                    // Stage/Prepare and WPF dependency-property callbacks may re-enter application
                    // code. Only the still-current request may adopt and reveal this tree.
                    if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
                        contentGeneration != _edgeCapsulePreviewContentGeneration ||
                        !ReferenceEquals(_edgeCapsulePreviewRequest, request) ||
                        !IsEdgeCapsulePreviewOpen ||
                        !_controller.IsEdgeCapsulePreviewOwner(this))
                    {
                        if (ReferenceEquals(_edgeCapsulePreviewRequest, request) &&
                            host.OwnsPreviewContent(content))
                        {
                            host.ClearPreviewContent();
                        }
                        return;
                    }

                    var resolvedRequest = request with
                    {
                        Content = content,
                        CreateDeferredContent = null
                    };
                    _edgeCapsulePreviewRequest = resolvedRequest;
                    if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
                        contentGeneration != _edgeCapsulePreviewContentGeneration ||
                        !ReferenceEquals(_edgeCapsulePreviewRequest, resolvedRequest) ||
                        !IsEdgeCapsulePreviewOpen ||
                        !_controller.IsEdgeCapsulePreviewOwner(this))
                    {
                        if (ReferenceEquals(
                                _edgeCapsulePreviewRequest,
                                resolvedRequest) &&
                            host.OwnsPreviewContent(content))
                        {
                            host.ClearPreviewContent();
                        }
                        return;
                    }
                    var visibilityStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
                    NotifyEdgeCapsulePreviewVisibility(resolvedRequest, visible: true);
                    EdgeCapsulePerformanceDiagnostics.Trace(
                        $"preview.deferred.visibility paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                        $"ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(visibilityStartedAt):F3} " +
                        $"totalWaitMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(deferredQueuedAt):F3}");
                }),
                DispatcherPriority.Background);
        };
        _edgeCapsulePreviewDeferredContentRenderHandler = renderHandler;
        CompositionTarget.Rendering += renderHandler;
    }

    private void CancelDeferredEdgeCapsulePreviewContentRenderWait()
    {
        var handler = _edgeCapsulePreviewDeferredContentRenderHandler;
        _edgeCapsulePreviewDeferredContentRenderHandler = null;
        if (handler != null)
        {
            CompositionTarget.Rendering -= handler;
        }
    }

    internal void SetEdgeCapsulePreviewClosed(bool animate)
    {
        // The staged close can replace the current queue root directly; completing it here would
        // insert an unnecessary reveal/hide cycle before the conceal generation is ready.
        if (!IsEdgeCapsulePreviewOpen)
        {
            _ = TryReleaseEdgeCapsulePreviewRequest();
            return;
        }

        if (!DispatchEdgeCapsuleIntent(
                EdgeCapsuleIntent.PreviewChanged(open: false),
                EdgeCapsuleDirty.None))
        {
            return;
        }

        if (!TryReleaseEdgeCapsulePreviewRequest())
        {
            return;
        }
        _controller.BeginEdgeCapsuleVisualTransaction(this);
        _ = TryStageEdgeCapsuleVisualTransaction(
            animate,
            EdgeCapsuleTransitionReason.Preview,
            EdgeCapsuleLayout.SlotMoveMilliseconds,
            refreshLayout: true);
    }

    internal void ClearEdgeCapsulePreviewContent()
    {
        if (!TryReleaseEdgeCapsulePreviewRequest())
        {
            return;
        }
        _edgeCapsuleHost?.ClearPreviewContent();
    }

    private bool TryReleaseEdgeCapsulePreviewRequest()
    {
        CancelDeferredEdgeCapsulePreviewContentRenderWait();
        var request = _edgeCapsulePreviewRequest;
        var generation = ++_edgeCapsulePreviewContentGeneration;
        NotifyEdgeCapsulePreviewVisibility(request, visible: false);
        if (generation != _edgeCapsulePreviewContentGeneration ||
            !ReferenceEquals(_edgeCapsulePreviewRequest, request))
        {
            return false;
        }
        _edgeCapsulePreviewRequest = null;
        return true;
    }

    internal void ResetEdgeCapsulePreviewForBodySessionChange()
    {
        if (!IsEdgeCapsulePreviewOpen &&
            _edgeCapsulePreviewRequest == null &&
            _edgeCapsuleHost?.HasPreviewContent != true)
        {
            return;
        }

        SetEdgeCapsulePreviewClosed(animate: false);
        FlushEdgeCapsulePreviewCompactPresentation();
        ClearEdgeCapsulePreviewContent();
    }

    private static void NotifyEdgeCapsulePreviewVisibility(
        EdgeCapsulePreviewRequest? request,
        bool visible)
    {
        try
        {
            request?.SetVisibility?.Invoke(visible);
        }
        catch
        {
            // Mini presentation lifecycle is optional and cannot disable the paper body.
        }
    }

    private void PrepareEdgeCapsulePreviewForActivation()
    {
        try
        {
            _edgeCapsulePreviewRequest?.PrepareForActivation?.Invoke();
        }
        catch
        {
            // A migration hand-off cannot block the normal paper activation path.
        }
    }

    internal EdgeCapsulePreviewSize? CurrentEdgeCapsulePreviewSize =>
        _edgeCapsulePreviewRequest?.Size;

    internal bool TryGetEdgeCapsuleAppliedGeometry(
        out EdgeCapsulePreviewScreenGeometry geometry)
    {
        if (_controller.TryGetEdgeCapsuleQueueProxyPresentation(this, out var proxyFrame))
        {
            geometry = new EdgeCapsulePreviewScreenGeometry(
                proxyFrame.Bounds,
                proxyFrame.DpiScaleX,
                proxyFrame.DpiScaleY);
            return proxyFrame.Visible && !proxyFrame.Bounds.IsEmpty;
        }
        var frame = EdgeCapsulePresentationFrame.Hidden;
        var hasFrame = _edgeCapsuleHost?.TryGetAppliedPresentation(
            out frame) == true;
        geometry = new EdgeCapsulePreviewScreenGeometry(
            frame.Bounds,
            frame.DpiScaleX,
            frame.DpiScaleY);
        return hasFrame && frame.Visible && !frame.Bounds.IsEmpty;
    }

    internal bool IsEdgeCapsuleInteractiveAt(DeviceScreenPoint pointer)
    {
        if (_controller.TryGetEdgeCapsuleQueueProxyPresentation(this, out var proxyFrame))
        {
            return proxyFrame.Visible &&
                proxyFrame.IsHitTestVisible &&
                EdgeCapsuleGeometry.Contains(proxyFrame.InteractiveBounds, pointer);
        }
        var frame = EdgeCapsulePresentationFrame.Hidden;
        return _edgeCapsuleHost?.TryGetAppliedPresentation(out frame) == true &&
            frame.Visible &&
            frame.IsHitTestVisible &&
            EdgeCapsuleGeometry.Contains(frame.InteractiveBounds, pointer);
    }

    internal bool TryGetEdgeCapsuleInteractiveGeometry(
        out EdgeCapsulePreviewScreenGeometry geometry)
    {
        if (_controller.TryGetEdgeCapsuleQueueProxyPresentation(this, out var proxyFrame))
        {
            geometry = new EdgeCapsulePreviewScreenGeometry(
                proxyFrame.InteractiveBounds,
                proxyFrame.DpiScaleX,
                proxyFrame.DpiScaleY);
            return proxyFrame.Visible &&
                proxyFrame.IsHitTestVisible &&
                !proxyFrame.InteractiveBounds.IsEmpty;
        }
        var frame = EdgeCapsulePresentationFrame.Hidden;
        var hasFrame = _edgeCapsuleHost?.TryGetAppliedPresentation(
            out frame) == true;
        geometry = new EdgeCapsulePreviewScreenGeometry(
            frame.InteractiveBounds,
            frame.DpiScaleX,
            frame.DpiScaleY);
        return hasFrame && frame.Visible &&
            frame.IsHitTestVisible &&
            !frame.InteractiveBounds.IsEmpty;
    }

    private void ScheduleEdgeCapsuleCompositionPrewarm()
    {
        if (!_controller.State.ExperimentalEdgeCapsuleHoverPreview ||
            _windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            (Action)(() =>
            {
                if (_controller.State.ExperimentalEdgeCapsuleHoverPreview &&
                    _windowLifecycle == PaperWindowLifecycleState.Alive &&
                    !IsClosed)
                {
                    EdgeCapsuleQueueCompositionProxy.PrewarmLightweight(Dispatcher);
                }
            }));
    }

    internal void RefreshEdgeCapsuleHoverIntentSettings()
    {
        if (_controller.State.ExperimentalEdgeCapsuleHoverPreview)
        {
            ScheduleEdgeCapsuleCompositionPrewarm();
        }
        else
        {
        }
        InvalidateEdgeCapsulePointer();
    }

    internal void FlushEdgeCapsulePreviewCompactPresentation()
    {
        if (TryStageEdgeCapsuleVisualTransaction(
                animate: false,
                EdgeCapsuleTransitionReason.Preview,
                EdgeCapsuleLayout.SlotMoveMilliseconds,
                refreshLayout: true))
        {
            return;
        }

        FlushEdgeCapsulePresentation(
            EdgeCapsuleTransitionReason.Preview,
            EdgeCapsuleDirty.Presentation |
            EdgeCapsuleDirty.Measure);
    }

}
