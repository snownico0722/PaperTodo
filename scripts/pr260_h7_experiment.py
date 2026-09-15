from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8-sig")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{path}: expected exactly one match, found {count}")
    p.write_text(text.replace(old, new), encoding="utf-8", newline="")


replace_once(
    "src/EdgeCapsulePreview.cs",
    """internal sealed record EdgeCapsulePreviewRequest(
    EdgeCapsulePreviewSize Size,
    FrameworkElement Content,
    Action<bool>? SetVisibility = null,
    Action? PrepareForActivation = null,
    Func<FrameworkElement>? CreateDeferredContent = null);""",
    """internal sealed record EdgeCapsulePreviewRequest(
    EdgeCapsulePreviewSize Size,
    FrameworkElement Content,
    Action<bool>? SetVisibility = null,
    Action? PrepareForActivation = null,
    Func<FrameworkElement>? CreateDeferredContent = null,
    EdgeCapsulePreviewSize? RequestedSize = null,
    Func<EdgeCapsulePreviewSize, FrameworkElement>? CreateContentForSize = null);""",
)

replace_once(
    "src/EdgeCapsulePreview.cs",
    """        else
        {
            return previous! with { Size = size };
        }

        var offsets = new Dictionary<string, double>(StringComparer.Ordinal);""",
    """        else
        {
            var previousHeight = Math.Max(compactHeight, previous!.Size.HeightDip);
            if (newHeight <= previousHeight + 0.001)
            {
                return previous with { Size = size };
            }

            // A preview may initially open at a constrained source size while a retained HWND is
            // being handed back. When the same owner later recovers its requested height, keep the
            // owner top fixed and only push followers far enough to make room.
            tops[newIndex] = currentTops[newIndex];
            PushFollowingMembers(
                tops,
                currentTops,
                newIndex,
                newHeight,
                compactHeight,
                gap);
        }

        var offsets = new Dictionary<string, double>(StringComparer.Ordinal);""",
)

replace_once(
    "src/PaperWindow.EdgeCapsulePreview.cs",
    """        var monitor = DeepCapsuleMonitorGeometry().LocalWorkAreaDip;
        var size = descriptor.Size.Normalize(
            Math.Max(1, monitor.Width - 16),
            Math.Max(1, monitor.Height - 16));
        if (!PrepareEdgeCapsuleHostCapacity(size))""",
    """        var monitor = DeepCapsuleMonitorGeometry().LocalWorkAreaDip;
        var requestedSize = descriptor.Size.Normalize(
            Math.Max(1, monitor.Width - 16),
            Math.Max(1, monitor.Height - 16));
        var size = requestedSize;
        if (!PrepareEdgeCapsuleHostCapacity(size))""",
)

replace_once(
    "src/PaperWindow.EdgeCapsulePreview.cs",
    """            deferProviderContent
                ? () => descriptor.CreateContent(size)
                : null);""",
    """            deferProviderContent
                ? () => descriptor.CreateContent(size)
                : null,
            requestedSize,
            descriptor.CreateContent);""",
)

replace_once(
    "src/PaperWindow.EdgeCapsule.cs",
    """        if (pendingCapacity.HasValue &&
            pendingCapacity.Value.IsValid)
        {
            var size = pendingCapacity.Value;
            if (TryReserveEdgeCapsuleHostCapacity(size, out var changed))
            {
                if (_edgeCapsulePendingPreviewCapacity == pendingCapacity)
                {
                    _edgeCapsulePendingPreviewCapacity = null;
                }
                if (changed)
                {
                    InvalidateEdgeCapsule(
                        EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Measure);
                }
            }
        }""",
    """        if (pendingCapacity.HasValue &&
            pendingCapacity.Value.IsValid)
        {
            var size = pendingCapacity.Value;
            if (TryReserveEdgeCapsuleHostCapacity(size, out var changed))
            {
                var restoredPreview =
                    TryRestoreConstrainedEdgeCapsulePreviewAfterCapacityRelease(
                        pendingCapacity.Value);
                if (_edgeCapsulePendingPreviewCapacity == pendingCapacity)
                {
                    _edgeCapsulePendingPreviewCapacity = null;
                }
                if (changed || restoredPreview)
                {
                    InvalidateEdgeCapsule(
                        EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Measure);
                }
            }
        }""",
)

Path("src/PaperWindow.EdgeCapsulePreviewCapacityRecovery.cs").write_text(
    r'''using System.Windows;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool TryRestoreConstrainedEdgeCapsulePreviewAfterCapacityRelease(
        EdgeCapsulePreviewSize fulfilledCapacity)
    {
        var request = _edgeCapsulePreviewRequest;
        if (request == null ||
            request.RequestedSize is not { } requestedSize ||
            requestedSize != fulfilledCapacity ||
            requestedSize == request.Size ||
            request.CreateContentForSize is not { } createContent ||
            !CurrentEdgeCapsuleHostCapacityContains(requestedSize) ||
            !IsEdgeCapsulePreviewOpen ||
            !_controller.IsEdgeCapsulePreviewOwner(this) ||
            _edgeCapsuleHost is not { } host ||
            !host.OwnsPreviewContent(request.Content))
        {
            return false;
        }

        var contentGeneration = _edgeCapsulePreviewContentGeneration;
        var bodyGeneration = _bodySessionGeneration;
        FrameworkElement? replacement;
        try
        {
            replacement = createContent(requestedSize);
        }
        catch (Exception ex)
        {
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.capacity.restore-fail paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"phase=create exception={ex.GetType().Name}");
            return false;
        }

        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            bodyGeneration != _bodySessionGeneration ||
            contentGeneration != _edgeCapsulePreviewContentGeneration ||
            !ReferenceEquals(_edgeCapsulePreviewRequest, request) ||
            !IsEdgeCapsulePreviewOpen ||
            !_controller.IsEdgeCapsulePreviewOwner(this) ||
            replacement == null ||
            !IsValidEdgeCapsulePreviewContent(replacement))
        {
            return false;
        }

        replacement.HorizontalAlignment = HorizontalAlignment.Stretch;
        replacement.VerticalAlignment = VerticalAlignment.Stretch;
        var targetContentSize = requestedSize.ContentSize;
        if (!host.ReplacePreviewContent(
                request.Content,
                replacement,
                targetContentSize.Width,
                targetContentSize.Height))
        {
            return false;
        }

        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            bodyGeneration != _bodySessionGeneration ||
            contentGeneration != _edgeCapsulePreviewContentGeneration ||
            !ReferenceEquals(_edgeCapsulePreviewRequest, request) ||
            !IsEdgeCapsulePreviewOpen ||
            !_controller.IsEdgeCapsulePreviewOwner(this))
        {
            TryRollbackConstrainedPreviewContent(host, request, replacement);
            return false;
        }

        var restoredRequest = request with
        {
            Size = requestedSize,
            Content = replacement,
            CreateDeferredContent = null
        };
        _edgeCapsulePreviewRequest = restoredRequest;
        if (!_controller.TryRestoreEdgeCapsulePreviewSessionSize(
                this,
                request.Size,
                requestedSize))
        {
            if (TryRollbackConstrainedPreviewContent(host, request, replacement))
            {
                _edgeCapsulePreviewRequest = request;
            }
            return false;
        }

        NotifyEdgeCapsulePreviewVisibility(restoredRequest, visible: true);
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"preview.capacity.restore paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
            $"from={request.Size.WidthDip:F1}x{request.Size.HeightDip:F1} " +
            $"to={requestedSize.WidthDip:F1}x{requestedSize.HeightDip:F1} " +
            $"generation={contentGeneration}");
        return true;
    }

    private bool TryRollbackConstrainedPreviewContent(
        EdgeCapsuleHost host,
        EdgeCapsulePreviewRequest request,
        FrameworkElement replacement)
    {
        if (!ReferenceEquals(_edgeCapsulePreviewRequest, request) ||
            !host.OwnsPreviewContent(replacement))
        {
            return false;
        }
        var oldSize = request.Size.ContentSize;
        return host.ReplacePreviewContent(
            replacement,
            request.Content,
            oldSize.Width,
            oldSize.Height);
    }
}
''',
    encoding="utf-8",
    newline="",
)

Path("src/AppController.EdgeCapsulePreviewCapacityRecovery.cs").write_text(
    r'''namespace PaperTodo;

public sealed partial class AppController
{
    internal bool TryRestoreEdgeCapsulePreviewSessionSize(
        PaperWindow window,
        EdgeCapsulePreviewSize previousSize,
        EdgeCapsulePreviewSize requestedSize)
    {
        if (IsExiting ||
            _edgeCapsulePreviewSession is not { } session ||
            !string.Equals(
                session.OwnerPaperId,
                window.EdgeCapsulePreviewPaperId,
                StringComparison.Ordinal) ||
            session.Size != previousSize ||
            !_windows.TryGetValue(window.EdgeCapsulePreviewPaperId, out var current) ||
            !ReferenceEquals(current, window))
        {
            return false;
        }

        var basePlan = BuildCurrentEdgeCapsuleQueuePlan();
        var queueKey = QueueKey(window.EdgeCapsulePreviewPaper);
        var next = EdgeCapsulePreviewLayoutCoordinator.OpenOrTransfer(
            basePlan,
            session,
            queueKey,
            window.EdgeCapsulePreviewPaperId,
            requestedSize,
            PaperLayoutDefaults.CapsuleHeight,
            DeepCapsuleGap);
        if (next == null || !ReferenceEquals(_edgeCapsulePreviewSession, session))
        {
            return false;
        }

        _edgeCapsulePreviewSession = next;
        BeginEdgeCapsuleVisualTransaction(window);
        ArrangeDeepCapsules(animate: State.EnableAnimations);
        return true;
    }
}
''',
    encoding="utf-8",
    newline="",
)

# Focused A/B lives inside the existing real-WPF maximum-capacity checks so it can run against
# both the candidate tree and a pinned #260 worktree by copying this one test source file.
p = Path("tests/PaperTodo.EdgeTitleChecks/ProxyMaximumCapacityChecks.cs")
text = p.read_text(encoding="utf-8-sig")
old = '''        NativeMaximumCapacityGrowth(products[3]);
        VisibleMaximumCapacityPreacquisition(products[0]);
        Console.WriteLine("PASS proxy-product-maximum-source-and-output-capacity");'''
new = '''        NativeMaximumCapacityGrowth(products[3]);
        VisibleMaximumCapacityPreacquisition(products[0]);
        ConstrainedLivePreviewCapacityRecovery(products[0]);
        Console.WriteLine("PASS proxy-product-maximum-source-and-output-capacity");'''
if text.count(old) != 1:
    raise SystemExit("ProxyMaximumCapacityChecks: call insertion mismatch")
text = text.replace(old, new)
marker = "    private static void NativeMaximumCapacityGrowth(MaximumCapacityProduct product)\n    {"
method = r'''    private static void ConstrainedLivePreviewCapacityRecovery(MaximumCapacityProduct product)
    {
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor),
            "Constrained live preview recovery test monitor");
        using var fixture = new MaximumCapacityFixture(product, monitor, EdgeCapsuleEdge.Right);
        using var proxy = new ProxyLifecycleFixture();
        var controller = GetCapacityCheckField<AppController>(fixture.Window, "_controller");
        var windows = GetCapacityCheckField<Dictionary<string, PaperWindow>>(controller, "_windows");
        windows[fixture.Paper.Id] = fixture.Window;

        EdgeCapsuleDirty Reconcile(EdgeCapsuleDirty dirty) => fixture.Presenter.Reconcile(
            dirty, fixture.Capture, () => null, frame => frame, fixture.Host.Apply);
        fixture.Presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State));
        fixture.Presenter.Flush(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation,
            fixture.Host.Dispatcher, Reconcile);
        var initial = fixture.Presenter.AppliedPresentation;
        Check(initial.IsUsable && initial.Visible && fixture.Host.MatchesPresentation(initial),
            "Constrained recovery starts from an actually applied compact source");

        var member = new EdgeCapsuleQueueProxyMemberPlan(fixture.Paper.Id, initial, initial, initial);
        proxy.Set("_members", new[] { new EdgeCapsuleQueueCompositionProxyMember(fixture.Window, member, fixture.Host.Handle) });
        proxy.Set("_plan", new EdgeCapsuleQueueProxyPlan("capacity-recovery", initial.HostBounds,
            initial.Edge, initial.WallDeviceX, initial.DpiScaleX, initial.DpiScaleY,
            200, false, new[] { member }));
        proxy.Proxy.RetainForQueueBrowsing();
        fixture.Retained.Add(fixture.Window, proxy.Proxy);

        var before = fixture.Capture();
        var target = new EdgeCapsulePreviewSize(
            Math.Min(Math.Max(before.HostCapacityWidthDip + 120, 320), monitor.LocalWorkAreaDip.Width - 16),
            Math.Min(Math.Max(before.HostCapacityHeightDip + 140, 240), monitor.LocalWorkAreaDip.Height - 16));
        Check(target.WidthDip > before.HostCapacityWidthDip || target.HeightDip > before.HostCapacityHeightDip,
            "Recovery target exceeds the retained source capacity");
        var prepare = typeof(PaperWindow).GetMethod("PrepareEdgeCapsuleHostCapacity", CapacityCheckFields)!
            .CreateDelegate<Func<EdgeCapsulePreviewSize, bool>>(fixture.Window);
        Check(!prepare(target), "Retained source defers the larger preview capacity demand");

        var constrained = new EdgeCapsulePreviewSize(
            Math.Min(target.WidthDip, before.HostCapacityWidthDip),
            Math.Min(target.HeightDip, before.HostCapacityHeightDip));
        var oldContent = new System.Windows.Controls.Border { Tag = constrained };
        Check(fixture.Host.StagePreviewContent(oldContent,
                constrained.ContentSize.Width, constrained.ContentSize.Height),
            "Constrained recovery stages live preview content at the available size");
        Check(fixture.Presenter.Dispatch(EdgeCapsuleIntent.PreviewChanged(true)).Accepted,
            "Constrained recovery opens the presenter preview state");

        Func<EdgeCapsulePreviewSize, System.Windows.FrameworkElement> factory = size =>
            new System.Windows.Controls.Border { Tag = size };
        var ctor = typeof(EdgeCapsulePreviewRequest).GetConstructors(CapacityCheckFields).Single();
        var args = ctor.GetParameters().Length > 5
            ? new object?[] { constrained, oldContent, null, null, null, target, factory }
            : new object?[] { constrained, oldContent, null, null, null };
        var request = (EdgeCapsulePreviewRequest)ctor.Invoke(args);
        SetCapacityCheckField(fixture.Window, "_edgeCapsulePreviewRequest", request);
        const int contentGeneration = 37;
        SetCapacityCheckField(fixture.Window, "_edgeCapsulePreviewContentGeneration", contentGeneration);

        var queueKeyMethod = typeof(AppController).GetMethod("QueueKey", CapacityCheckFields)!;
        var queueKey = (string)queueKeyMethod.Invoke(controller, new object[] { fixture.Paper })!;
        SetCapacityCheckField(controller, "_edgeCapsulePreviewSession",
            new EdgeCapsulePreviewLayoutSession(queueKey, fixture.Paper.Id, constrained,
                new[] { fixture.Paper.Id }, new Dictionary<string, double>(StringComparer.Ordinal)
                { [fixture.Paper.Id] = 0 }));

        fixture.Retained.Clear();
        fixture.Window.ResumeEdgeCapsuleSourceInvalidationsAfterProxyRelease();
        fixture.Host.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        var actual = GetCapacityCheckField<EdgeCapsulePreviewRequest?>(fixture.Window,
            "_edgeCapsulePreviewRequest");
        var generationAfter = GetCapacityCheckField<int>(fixture.Window,
            "_edgeCapsulePreviewContentGeneration");
        var hasRecoveryCode = typeof(PaperWindow).GetMethod(
            "TryRestoreConstrainedEdgeCapsulePreviewAfterCapacityRelease", CapacityCheckFields) != null;
        if (hasRecoveryCode)
        {
            Check(actual != null && actual.Size == target,
                "Candidate restores the same live preview request to its requested size after source release");
            Check(generationAfter == contentGeneration,
                "Capacity recovery keeps the same preview content generation");
            Check(!ReferenceEquals(actual!.Content, oldContent) && fixture.Host.OwnsPreviewContent(actual.Content),
                "Candidate rebuilds size-sensitive content at the recovered size");
            var session = GetCapacityCheckField<EdgeCapsulePreviewLayoutSession?>(controller,
                "_edgeCapsulePreviewSession");
            Check(session is { } restored && restored.Size == target,
                "Controller session adopts the recovered size instead of resetting the preview");
        }
        else
        {
            Check(actual != null && actual.Size == constrained && ReferenceEquals(actual.Content, oldContent),
                "Pinned #260 reproduces H7: source capacity grows but live preview stays constrained");
            Check(generationAfter == contentGeneration,
                "Pinned #260 keeps the constrained content generation");
        }
        Check(GetCapacityCheckField<EdgeCapsulePreviewSize?>(fixture.Window,
                "_edgeCapsulePendingPreviewCapacity") == null,
            "Capacity demand is consumed after the source grows");
        Console.WriteLine($"RESULT pr260-capacity-recovery fixed={hasRecoveryCode} from={constrained.WidthDip:F1}x{constrained.HeightDip:F1} to={actual?.Size.WidthDip:F1}x{actual?.Size.HeightDip:F1} generation={generationAfter}");
        fixture.Presenter.ClearDeferredWork();
    }

'''
if text.count(marker) != 1:
    raise SystemExit("ProxyMaximumCapacityChecks: method marker mismatch")
text = text.replace(marker, method + marker)
p.write_text(text, encoding="utf-8", newline="")
