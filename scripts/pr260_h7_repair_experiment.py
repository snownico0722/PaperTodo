from pathlib import Path

p = Path("scripts/pr260_h7_experiment.py")
text = p.read_text(encoding="utf-8")
old = '''replace_once(
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
'''
new = '''replace_once(
    "src/PaperWindow.EdgeCapsule.cs",
    """        if (HasDeepCapsuleSlotPlacement &&
            _edgeCapsulePendingPreviewCapacity is { } pendingCapacity)
        {
            var workArea = DeepCapsuleMonitorGeometry().LocalWorkAreaDip;
            var size = pendingCapacity.Normalize(
                Math.Max(1, workArea.Width - 16),
                Math.Max(1, workArea.Height - 16));
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
    """        if (HasDeepCapsuleSlotPlacement &&
            _edgeCapsulePendingPreviewCapacity is { } pendingCapacity)
        {
            var workArea = DeepCapsuleMonitorGeometry().LocalWorkAreaDip;
            var size = pendingCapacity.Normalize(
                Math.Max(1, workArea.Width - 16),
                Math.Max(1, workArea.Height - 16));
            if (TryReserveEdgeCapsuleHostCapacity(size, out var changed))
            {
                var restoredPreview =
                    TryRestoreConstrainedEdgeCapsulePreviewAfterCapacityRelease(size);
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
'''
if text.count(old) != 1:
    raise SystemExit(f"capacity-release repair anchor count={text.count(old)}")
text = text.replace(old, new)

# The production indentation is nested inside the request constructor; the first draft of the
# experiment script used an under-indented exact anchor, so repair the script before applying it.
old = '''    """            deferProviderContent
                ? () => descriptor.CreateContent(size)
                : null);""",
    """            deferProviderContent
                ? () => descriptor.CreateContent(size)
                : null,
            requestedSize,
            descriptor.CreateContent);""",
)'''
new = '''    """                deferProviderContent
                    ? () => descriptor.CreateContent(size)
                    : null);""",
    """                deferProviderContent
                    ? () => descriptor.CreateContent(size)
                    : null,
                requestedSize,
                descriptor.CreateContent);""",
)'''
if text.count(old) != 1:
    raise SystemExit(f"deferred-content repair anchor count={text.count(old)}")
text = text.replace(old, new)

p.write_text(text, encoding="utf-8", newline="")
