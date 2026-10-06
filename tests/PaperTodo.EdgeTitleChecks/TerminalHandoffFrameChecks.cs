using System.Diagnostics;
using PaperTodo;

internal static partial class Program
{
    private static void TerminalHandoffFrames()
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        foreach (var edge in new[] { EdgeCapsuleEdge.Left, EdgeCapsuleEdge.Right })
        {
            var presenter = new EdgeCapsulePresenter();
            var placement = new EdgeCapsulePlacement(0, 1, 1);
            var layout = new EdgeCapsuleLayoutSnapshot(
                new MonitorGeometry("terminal-check", new DeviceScreenRect(0, 0, 2560, 1440), scale, scale),
                edge, 100, 20, 100, 28, 32, 200, 240, false, 0.7, null, 228, 240);
            var applied = EdgeCapsulePresentationFrame.Hidden;
            EdgeCapsuleDirty Reconcile(EdgeCapsuleDirty dirty, long now) => presenter.Reconcile(
                dirty, () => layout, () => null, frame => frame,
                frame => { applied = frame; return true; }, now);
            var clock = Stopwatch.GetTimestamp();
            Check(presenter.Dispatch(EdgeCapsuleIntent.Attach(placement,
                EdgeCapsulePaperForm.Collapsed, false)).Accepted, "Attach terminal test presenter");
            presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State));
            Reconcile(EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Measure, clock);
            foreach (var retracted in new[] { true, false })
            {
                clock += Stopwatch.Frequency;
                Check(presenter.Dispatch(EdgeCapsuleIntent.Attach(placement,
                    EdgeCapsulePaperForm.Collapsed, retracted)).Accepted, "Retarget master collapse/expand");
                presenter.RequestPresentation(EdgeCapsuleMotion.Animate(EdgeCapsuleTransitionReason.Retraction, 200));
                Reconcile(EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Measure, clock);
                Reconcile(EdgeCapsuleDirty.Frame, clock + Stopwatch.Frequency * 190 / 1000);
                var target = presenter.PlanTargetPresentation(layout);
                var exact = target.ToFrame();
                Check(presenter.HasActiveTransition && presenter.AppliedPresentation != exact &&
                    EdgeCapsuleTransitionPolicy.FramesMatch(presenter.AppliedPresentation, target),
                    "Near-terminal frame is visually equal but not the exact authority endpoint");

                // Queue completion cancels the timeline before flushing its final endpoint. A
                // tolerant animation comparison must not preserve the sampled opacity tail here.
                presenter.CancelTransition();
                presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.Preview));
                Reconcile(EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Pointer,
                    clock + Stopwatch.Frequency * 195 / 1000);
                Check(!presenter.HasActiveTransition && presenter.AppliedPresentation == exact && applied == exact,
                    $"Explicit master handoff commits the exact target, retracted={retracted}, edge={edge}, dpi={scale}");
            }
        }
        Console.WriteLine("PASS exact terminal handoff after near-complete collapse and expand (8 edge/DPI combinations)");
    }
}
