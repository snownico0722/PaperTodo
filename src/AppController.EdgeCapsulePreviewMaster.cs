namespace PaperTodo;

public sealed partial class AppController
{
    internal void SuppressEdgeCapsulePreviewForMasterQueueLayout(
        string monitorDeviceName,
        EdgeCapsuleEdge edge) =>
        SuppressEdgeCapsulePreviewForMasterQueueLayout(MasterCapsuleQueueKey(monitorDeviceName, edge));

    internal void SuppressEdgeCapsulePreviewForMasterQueueLayout(string queueKey)
    {
        if (IsExiting)
        {
            return;
        }

        // Master collapse/expand moves real capsule HWNDs under a stationary pointer. Cancel any
        // activation work that was based on the pre-toggle layout before arming the same
        // layout-induced-hover suppression used by preview compaction.
        ResetEdgeCapsulePreviewActivationIntent();
        _edgeCapsulePreviewQueuedTransferPaperId = null;
        _edgeCapsulePreviewTransferGeneration++;
        ForgetEdgeCapsulePreviewPointerResolution();

        var target = _windows.Values.FirstOrDefault(window =>
            string.Equals(
                QueueKey(window.EdgeCapsulePreviewPaper),
                queueKey,
                StringComparison.Ordinal));
        if (target == null)
        {
            _edgeCapsulePreviewLayoutSuppressionAnchor = null;
            _edgeCapsulePreviewIntentPredictor.Reset();
            return;
        }

        RecordEdgeCapsulePreviewTransferPointer(target, queueKey);

        // Collapse-all is one queue-wide visual operation, just like Preview transfer. Open the
        // controller-owned transaction before ToggleCapsuleCollapseAllActive mutates the queue and
        // synchronously calls ArrangeDeepCapsules. Every RetractIntoMaster/ApplyDeepCapsulePlacement
        // in that arrange can then stage into the same Send-priority commit and start from one QPC
        // timestamp instead of seven independent per-paper clocks.
        foreach (var window in _windows.Values.Where(w =>
                     QueueKey(w.EdgeCapsulePreviewPaper) == queueKey).ToArray())
            BeginEdgeCapsuleVisualTransaction(window);
    }
}
