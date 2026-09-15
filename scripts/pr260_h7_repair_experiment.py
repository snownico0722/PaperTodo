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
    raise SystemExit(f"repair anchor count={text.count(old)}")
p.write_text(text.replace(old, new), encoding="utf-8", newline="")
