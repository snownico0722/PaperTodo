from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8-sig")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{path}: expected exactly one match, found {count}: {old.splitlines()[0]!r}")
    p.write_text(text.replace(old, new), encoding="utf-8", newline="")

# Keep the production wrapper unchanged semantically, but isolate the host/request transaction
# from the controller's WPF placement side effect. The capacity fixture intentionally uses an
# uninitialized PaperWindow and cannot legally execute DependencyObject.SetValue; the extracted
# core lets the test exercise success and rollback with a real host while a separate callback
# validates the layout-session decision.
replace_once(
    "src/PaperWindow.EdgeCapsulePreviewCapacityRecovery.cs",
    """    private bool TryRestoreConstrainedEdgeCapsulePreviewAfterCapacityRelease(\n        EdgeCapsulePreviewSize fulfilledCapacity)\n    {""",
    """    private bool TryRestoreConstrainedEdgeCapsulePreviewAfterCapacityRelease(\n        EdgeCapsulePreviewSize fulfilledCapacity) =>\n        TryRestoreConstrainedEdgeCapsulePreviewAfterCapacityReleaseCore(\n            fulfilledCapacity,\n            (previousSize, requestedSize) =>\n                _controller.TryRestoreEdgeCapsulePreviewSessionSize(\n                    this,\n                    previousSize,\n                    requestedSize));\n\n    private bool TryRestoreConstrainedEdgeCapsulePreviewAfterCapacityReleaseCore(\n        EdgeCapsulePreviewSize fulfilledCapacity,\n        Func<EdgeCapsulePreviewSize, EdgeCapsulePreviewSize, bool> tryRestoreSessionSize)\n    {""",
)
replace_once(
    "src/PaperWindow.EdgeCapsulePreviewCapacityRecovery.cs",
    """        if (!_controller.TryRestoreEdgeCapsulePreviewSessionSize(\n                this,\n                request.Size,\n                requestedSize))""",
    """        if (!tryRestoreSessionSize(request.Size, requestedSize))""",
)

p = Path("tests/PaperTodo.EdgeTitleChecks/ProxyMaximumCapacityChecks.cs")
text = p.read_text(encoding="utf-8-sig")
old = '''        fixture.Retained.Clear();
        fixture.Window.ResumeEdgeCapsuleSourceInvalidationsAfterProxyRelease();

        var actual = GetCapacityCheckField<EdgeCapsulePreviewRequest?>(fixture.Window,
            "_edgeCapsulePreviewRequest");
        var generationAfter = GetCapacityCheckField<int>(fixture.Window,
            "_edgeCapsulePreviewContentGeneration");
        var hasRecoveryCode = typeof(PaperWindow).GetMethod(
            "TryRestoreConstrainedEdgeCapsulePreviewAfterCapacityRelease", CapacityCheckFields) != null;
'''
new = '''        fixture.Retained.Clear();
        var hasRecoveryCode = typeof(PaperWindow).GetMethod(
            "TryRestoreConstrainedEdgeCapsulePreviewAfterCapacityRelease", CapacityCheckFields) != null;
        MethodInfo? recoveryCore = null;
        if (hasRecoveryCode)
        {
            // Grow the real source capacity through the production release path while temporarily
            // removing the preview session so that the uninitialized Window test double never
            // enters ArrangeDeepCapsules/DependencyObject.SetValue.
            var savedSession = GetCapacityCheckField<EdgeCapsulePreviewLayoutSession?>(controller,
                "_edgeCapsulePreviewSession");
            SetCapacityCheckField<EdgeCapsulePreviewLayoutSession?>(controller,
                "_edgeCapsulePreviewSession", null);
            fixture.Window.ResumeEdgeCapsuleSourceInvalidationsAfterProxyRelease();
            SetCapacityCheckField(controller, "_edgeCapsulePreviewSession", savedSession);

            recoveryCore = typeof(PaperWindow).GetMethod(
                "TryRestoreConstrainedEdgeCapsulePreviewAfterCapacityReleaseCore", CapacityCheckFields)!;
            Func<EdgeCapsulePreviewSize, EdgeCapsulePreviewSize, bool> adoptSession =
                (previousSize, requestedSize) =>
                {
                    if (savedSession == null || savedSession.Size != previousSize) return false;
                    var basePlan = EdgeCapsuleQueueCoordinator.Build(
                        new[] { new EdgeCapsuleQueueMember(fixture.Paper, queueKey) },
                        collapseAll: false);
                    var next = EdgeCapsulePreviewLayoutCoordinator.OpenOrTransfer(
                        basePlan,
                        savedSession,
                        queueKey,
                        fixture.Paper.Id,
                        requestedSize,
                        PaperLayoutDefaults.CapsuleHeight,
                        gap: 8);
                    if (next == null) return false;
                    SetCapacityCheckField(controller, "_edgeCapsulePreviewSession", next);
                    return true;
                };
            Check((bool)recoveryCore.Invoke(fixture.Window,
                    new object?[] { target, adoptSession })!,
                "Candidate host/request recovery core accepts the requested size");
        }
        else
        {
            fixture.Window.ResumeEdgeCapsuleSourceInvalidationsAfterProxyRelease();
        }

        var actual = GetCapacityCheckField<EdgeCapsulePreviewRequest?>(fixture.Window,
            "_edgeCapsulePreviewRequest");
        var generationAfter = GetCapacityCheckField<int>(fixture.Window,
            "_edgeCapsulePreviewContentGeneration");
'''
if text.count(old) != 1:
    raise SystemExit(f"focused recovery invocation anchor count={text.count(old)}")
text = text.replace(old, new)

old = '''            fixture.Window.ResumeEdgeCapsuleSourceInvalidationsAfterProxyRelease();
            var rolledBack = GetCapacityCheckField<EdgeCapsulePreviewRequest?>(fixture.Window,
                "_edgeCapsulePreviewRequest");
'''
new = '''            Func<EdgeCapsulePreviewSize, EdgeCapsulePreviewSize, bool> rejectSession =
                (_, _) => false;
            Check(recoveryCore != null && !(bool)recoveryCore.Invoke(fixture.Window,
                    new object?[] { target, rejectSession })!,
                "Controller/session rejection makes the recovery core fail synchronously");
            var rolledBack = GetCapacityCheckField<EdgeCapsulePreviewRequest?>(fixture.Window,
                "_edgeCapsulePreviewRequest");
'''
if text.count(old) != 1:
    raise SystemExit(f"focused rollback invocation anchor count={text.count(old)}")
text = text.replace(old, new)
p.write_text(text, encoding="utf-8", newline="")
