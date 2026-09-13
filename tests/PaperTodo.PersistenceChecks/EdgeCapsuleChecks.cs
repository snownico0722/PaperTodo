using System.IO;
using System.Reflection;
using System.Windows.Threading;
using PaperTodo;

// Owner: host edge-capsule regression checks. Pure checks use reducer/presenter inputs;
// window checks use an explicit isolated fixture. Rejected input or wrong state fails the check.
internal static class EdgeCapsuleChecks
{
    internal static void PointerOwnership()
    {
        var placement = new EdgeCapsulePlacement(1, 0, 2);
        var point = new DeviceScreenPoint(15, 115);
        EdgeCapsuleModel Send(EdgeCapsuleModel model, EdgeCapsuleIntent intent)
        {
            var result = EdgeCapsuleReducer.Reduce(model, intent);
            Check(result.Accepted, $"{model.State.Slot}/{intent.GetType().Name}: {result.Error}");
            return result.Model;
        }
        EdgeCapsuleModel Attach() => Send(EdgeCapsuleModel.Initial,
            EdgeCapsuleIntent.Attach(placement, EdgeCapsulePaperForm.Collapsed, false));
        void DetachedSamples(EdgeCapsuleModel model)
        {
            Check(model.State.Slot == EdgeCapsuleSlotState.None && !model.Placement.IsPlaced,
                "paper still owns a queue slot after detaching");
            foreach (var over in new[] { true, true, false, true, false })
            {
                var sampled = Send(model, EdgeCapsuleIntent.PointerSampled(over));
                Check(sampled == model && !sampled.PointerOverSurface,
                    "detached paper responded to the outgoing capsule's pointer sample");
            }
        }

        DetachedSamples(EdgeCapsuleModel.Initial);
        foreach (var reserve in new[] { false, true })
        {
            var model = Attach();
            for (var i = 0; i < 10; i++)
            {
                model = Send(model, EdgeCapsuleIntent.PointerSampled(true));
                Check(model.PointerOverSurface && model.State.Visual == EdgeCapsuleVisualState.Hovered,
                    "attached capsule lost hover");
                model = Send(model, EdgeCapsuleIntent.PointerPressed(point));
                model = Send(model, new EdgeCapsuleIntent.FinishPointer());
                model = Send(model, new EdgeCapsuleIntent.MarkOpenedFromEdge());
                model = Send(model, EdgeCapsuleIntent.PaperFormChanged(EdgeCapsulePaperForm.Expanded, reserve));
                if (reserve)
                {
                    foreach (var over in new[] { true, false })
                    {
                        model = Send(model, EdgeCapsuleIntent.PointerSampled(over));
                        Check(model.State.Slot == EdgeCapsuleSlotState.ExpandedReserved &&
                              model.State.Visual == EdgeCapsuleVisualState.Active && model.PointerOverSurface == over,
                            "reserved capsule lost its active state or pointer tracking");
                    }
                    model = Send(model, EdgeCapsuleIntent.PaperFormChanged(EdgeCapsulePaperForm.Collapsed, reserve));
                }
                else
                {
                    DetachedSamples(model);
                    model = Send(model, EdgeCapsuleIntent.Attach(placement, EdgeCapsulePaperForm.Collapsed, false));
                }
            }
        }
        DetachedSamples(Send(Send(Attach(), EdgeCapsuleIntent.PointerSampled(true)), EdgeCapsuleIntent.Detached()));
        foreach (var form in new[] { EdgeCapsulePaperForm.Collapsed, EdgeCapsulePaperForm.Expanded })
        {
            var model = Send(EdgeCapsuleModel.Initial, EdgeCapsuleIntent.Attach(placement, form, false));
            model = Send(model, EdgeCapsuleIntent.RetractionStarted());
            DetachedSamples(Send(model, EdgeCapsuleIntent.RetractionCompleted()));
        }

        var peer = Send(Attach(), EdgeCapsuleIntent.PeerReorderStarted());
        peer = Send(peer, EdgeCapsuleIntent.PointerSampled(true));
        Check(!peer.PointerOverSurface && peer.State.Visual == EdgeCapsuleVisualState.Resting,
            "peer reorder no longer suppresses hover");
        peer = Send(peer, EdgeCapsuleIntent.PeerReorderFinished());
        peer = Send(peer, EdgeCapsuleIntent.PointerSampled(true));
        Check(peer.PointerOverSurface, "hover did not resume after peer reorder");

        // Reconcile must still sample the actual outgoing frame, even after model detachment.
        var presenter = new EdgeCapsulePresenter();
        presenter.Dispatch(EdgeCapsuleIntent.Attach(placement, EdgeCapsulePaperForm.Collapsed, false));
        presenter.Dispatch(EdgeCapsuleIntent.Detached());
        var oldFrame = EdgeCapsulePresentationFrame.Hidden with
        {
            Visible = true, IsHitTestVisible = true,
            InteractiveBounds = new DeviceScreenRect(0, 100, 80, 130)
        };
        Check(EdgeCapsuleGeometry.Contains(oldFrame.InteractiveBounds, point), "fixture pointer must hit the outgoing frame");
        var sampledOldFrame = false;
        presenter.Reconcile(EdgeCapsuleDirty.Pointer,
            () => throw new InvalidOperationException("pointer-only reconcile should not measure"),
            () => point,
            _ => { sampledOldFrame = true; return oldFrame; },
            _ => throw new InvalidOperationException("detached pointer should not request presentation"));
        Check(sampledOldFrame && presenter.LastPointerSample == point && !presenter.PointerOverSurface &&
              presenter.State.Slot == EdgeCapsuleSlotState.None,
            "presenter retained pointer ownership across detach");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
