namespace PaperTodo;

internal static class EdgeCapsuleReducer
{
    public static EdgeCapsuleDispatchResult Reduce(
        EdgeCapsuleModel model,
        EdgeCapsuleIntent intent)
    {
        var result = intent switch
        {
            EdgeCapsuleIntent.AttachQueue attach => Attach(model, attach),
            EdgeCapsuleIntent.ChangeQueuePlacement placement =>
                UpdatePlacement(model, placement.Placement),
            EdgeCapsuleIntent.ChangePaperForm form => ChangePaperForm(model, form),
            EdgeCapsuleIntent.BeginRetraction => BeginRetraction(model),
            EdgeCapsuleIntent.DetachQueue => Accept(model, DetachedModel(model)),
            EdgeCapsuleIntent.CompleteRetraction => CompleteRetraction(model),
            EdgeCapsuleIntent.SamplePointer pointer =>
                SamplePointer(model, pointer.OverInteractiveSurface),
            EdgeCapsuleIntent.ChangeContextMenu menu => SetContextMenu(model, menu.Open),
            EdgeCapsuleIntent.BeginPeerReorder => ChangePeerReorder(model, active: true),
            EdgeCapsuleIntent.FinishPeerReorder => ChangePeerReorder(model, active: false),
            EdgeCapsuleIntent.BeginPointer pointer => BeginPointer(model, pointer.Point),
            EdgeCapsuleIntent.BeginDockedReorder reorder => BeginDockedReorder(model, reorder),
            EdgeCapsuleIntent.MoveDockedReorder reorder => MoveDockedReorder(model, reorder),
            EdgeCapsuleIntent.MoveDragPointer pointer => UpdateDragPointer(model, pointer.Point),
            EdgeCapsuleIntent.ChangePreviewIndex preview =>
                UpdatePreviewIndex(model, preview.Index),
            EdgeCapsuleIntent.BeginFloatingTransfer transfer =>
                BeginFloatingTransfer(model, transfer.Point),
            EdgeCapsuleIntent.BeginFloatingReorder => BeginFloatingReorder(model),
            EdgeCapsuleIntent.FinishPointer => FinishPointer(model),
            EdgeCapsuleIntent.MarkOpenedFromEdge => Accept(model, model with
            {
                State = model.State with { OpenOrigin = EdgeCapsuleOpenOrigin.EdgeSlot }
            }),
            EdgeCapsuleIntent.ClearOpenOrigin => Accept(model, model with
            {
                State = model.State with { OpenOrigin = EdgeCapsuleOpenOrigin.Normal }
            }),
            EdgeCapsuleIntent.Reset => Accept(model, EdgeCapsuleModel.Initial),
            _ => Reject(model, $"Unknown edge-capsule intent {intent.GetType().Name}.")
        };

        if (result.Accepted && !HasStructuralInvariants(result.Model))
        {
            return Reject(
                model,
                $"Edge-capsule intent {intent.GetType().Name} violated a structural invariant.");
        }
        return result;
    }

    private static EdgeCapsuleDispatchResult Attach(
        EdgeCapsuleModel model,
        EdgeCapsuleIntent.AttachQueue intent)
    {
        var placement = intent.Placement.Normalize();
        if (!placement.IsPlaced)
        {
            return Reject(model, "Attaching an edge capsule requires a queue placement.");
        }

        var expanded = intent.PaperForm == EdgeCapsulePaperForm.Expanded;
        var slot = intent.Retracted
            ? expanded
                ? EdgeCapsuleSlotState.RetractedExpanded
                : EdgeCapsuleSlotState.RetractedCollapsed
            : expanded
                ? EdgeCapsuleSlotState.ExpandedReserved
                : EdgeCapsuleSlotState.CollapsedDocked;
        var visual = !intent.Retracted && expanded
            ? EdgeCapsuleVisualState.Active
            : EdgeCapsuleVisualState.Resting;
        return Accept(model, model with
        {
            State = model.State with { Slot = slot, Visual = visual },
            Placement = placement,
            DockedDragTopDipOverride = null
        });
    }

    private static EdgeCapsuleDispatchResult UpdatePlacement(
        EdgeCapsuleModel model,
        EdgeCapsulePlacement placement)
    {
        if (model.State.Slot == EdgeCapsuleSlotState.None)
        {
            return Reject(model, "A detached edge capsule cannot receive a queue placement.");
        }
        placement = placement.Normalize();
        return placement.IsPlaced
            ? Accept(model, model with { Placement = placement })
            : Reject(model, "A queue placement must be valid.");
    }

    private static EdgeCapsuleDispatchResult ChangePaperForm(
        EdgeCapsuleModel model,
        EdgeCapsuleIntent.ChangePaperForm intent)
    {
        if (model.State.Slot == EdgeCapsuleSlotState.None)
        {
            return Unchanged(model);
        }

        var expanded = intent.PaperForm == EdgeCapsulePaperForm.Expanded;
        var retracted = model.State.Slot is
            EdgeCapsuleSlotState.RetractedCollapsed or
            EdgeCapsuleSlotState.RetractedExpanded;
        if (retracted)
        {
            return Accept(model, model with
            {
                State = model.State with
                {
                    Slot = expanded
                        ? EdgeCapsuleSlotState.RetractedExpanded
                        : EdgeCapsuleSlotState.RetractedCollapsed,
                    Visual = EdgeCapsuleVisualState.Resting
                }
            });
        }

        if (!expanded)
        {
            return Accept(model, model with
            {
                State = model.State with
                {
                    Slot = EdgeCapsuleSlotState.CollapsedDocked,
                    Visual = EdgeCapsuleVisualState.Resting
                }
            });
        }
        if (intent.ReserveWhileExpanded)
        {
            return Accept(model, model with
            {
                State = model.State with
                {
                    Slot = EdgeCapsuleSlotState.ExpandedReserved,
                    Visual = EdgeCapsuleVisualState.Active
                }
            });
        }
        return Accept(model, DetachedModel(model));
    }

    private static EdgeCapsuleDispatchResult BeginRetraction(EdgeCapsuleModel model)
    {
        var target = model.State.Slot switch
        {
            EdgeCapsuleSlotState.CollapsedDocked => EdgeCapsuleSlotState.RetractingCollapsed,
            EdgeCapsuleSlotState.ExpandedReserved => EdgeCapsuleSlotState.RetractingExpanded,
            _ => EdgeCapsuleSlotState.None
        };
        if (target == EdgeCapsuleSlotState.None)
        {
            return Reject(model, $"Cannot retract an edge capsule from {model.State.Slot}.");
        }
        return Accept(model, model with
        {
            State = model.State with
            {
                Slot = target,
                Visual = EdgeCapsuleVisualState.Resting
            }
        });
    }

    private static EdgeCapsuleDispatchResult CompleteRetraction(EdgeCapsuleModel model)
    {
        if (model.State.Slot is not (
            EdgeCapsuleSlotState.RetractingCollapsed or
            EdgeCapsuleSlotState.RetractingExpanded))
        {
            return Reject(model, $"Cannot complete retraction from {model.State.Slot}.");
        }
        return Accept(model, DetachedModel(model));
    }

    private static EdgeCapsuleDispatchResult SamplePointer(
        EdgeCapsuleModel model,
        bool overInteractiveSurface)
    {
        overInteractiveSurface &= !model.PeerReorderActive;
        var visual = model.State.Slot switch
        {
            EdgeCapsuleSlotState.ExpandedReserved => EdgeCapsuleVisualState.Active,
            EdgeCapsuleSlotState.CollapsedDocked
                when model.State.Gesture is EdgeCapsuleGestureState.Idle or EdgeCapsuleGestureState.PendingClick =>
                model.ContextMenuOpen || overInteractiveSurface
                    ? EdgeCapsuleVisualState.Hovered
                    : EdgeCapsuleVisualState.Resting,
            EdgeCapsuleSlotState.CollapsedDocked
                when model.State.Gesture == EdgeCapsuleGestureState.DockedReordering =>
                EdgeCapsuleVisualState.Hovered,
            _ => EdgeCapsuleVisualState.Resting
        };
        return Accept(model, model with { State = model.State with { Visual = visual } });
    }

    private static EdgeCapsuleDispatchResult ChangePeerReorder(
        EdgeCapsuleModel model,
        bool active)
    {
        if (active && model.State.Slot == EdgeCapsuleSlotState.None)
        {
            return Unchanged(model);
        }

        return Accept(model, model with { PeerReorderActive = active });
    }

    private static EdgeCapsuleDispatchResult SetContextMenu(
        EdgeCapsuleModel model,
        bool open)
    {
        var visual = open && model.State.Slot == EdgeCapsuleSlotState.CollapsedDocked
            ? EdgeCapsuleVisualState.Hovered
            : model.State.Visual;
        return Accept(model, model with
        {
            ContextMenuOpen = open,
            State = model.State with { Visual = visual }
        });
    }

    private static EdgeCapsuleDispatchResult BeginPointer(
        EdgeCapsuleModel model,
        DeviceScreenPoint point)
    {
        if (model.State.Gesture != EdgeCapsuleGestureState.Idle ||
            model.State.Slot == EdgeCapsuleSlotState.None)
        {
            return Reject(model, $"Cannot begin pointer interaction from {model.State.Gesture}/{model.State.Slot}.");
        }
        return Accept(model, model with
        {
            State = model.State with { Gesture = EdgeCapsuleGestureState.PendingClick },
            DragSession = EdgeCapsuleDragSession.Begin(point),
            DockedDragTopDipOverride = null
        });
    }

    private static EdgeCapsuleDispatchResult BeginDockedReorder(
        EdgeCapsuleModel model,
        EdgeCapsuleIntent.BeginDockedReorder intent)
    {
        if (model.State.Gesture != EdgeCapsuleGestureState.PendingClick ||
            model.DragSession is not { } session)
        {
            return Reject(model, "Docked reorder requires a pending pointer interaction.");
        }
        session = session with
        {
            LastScreenPosition = intent.Point,
            StartMonitorDeviceName = intent.StartMonitorDeviceName,
            DockedPointerOffsetY = intent.PointerOffsetY,
            PreviewIndex = -1
        };
        return Accept(model, model with
        {
            State = model.State with
            {
                Gesture = EdgeCapsuleGestureState.DockedReordering,
                Visual = model.State.Visual == EdgeCapsuleVisualState.Active
                    ? EdgeCapsuleVisualState.Active
                    : EdgeCapsuleVisualState.Hovered
            },
            DragSession = session,
            DockedDragTopDipOverride = intent.TopDip
        });
    }

    private static EdgeCapsuleDispatchResult MoveDockedReorder(
        EdgeCapsuleModel model,
        EdgeCapsuleIntent.MoveDockedReorder intent)
    {
        if (model.State.Gesture != EdgeCapsuleGestureState.DockedReordering ||
            model.DragSession is not { } session)
        {
            return Reject(model, "Docked reorder movement requires a docked reorder session.");
        }
        return Accept(model, model with
        {
            DragSession = session with { LastScreenPosition = intent.Point },
            DockedDragTopDipOverride = intent.TopDip
        });
    }

    private static EdgeCapsuleDispatchResult UpdateDragPointer(
        EdgeCapsuleModel model,
        DeviceScreenPoint point)
    {
        if (model.DragSession is not { } session ||
            model.State.Gesture is not (
                EdgeCapsuleGestureState.DockedReordering or
                EdgeCapsuleGestureState.FloatingTransfer or
                EdgeCapsuleGestureState.FloatingReordering))
        {
            return Reject(model, "Drag pointer update requires an active reorder session.");
        }
        return Accept(model, model with
        {
            DragSession = session with { LastScreenPosition = point }
        });
    }

    private static EdgeCapsuleDispatchResult UpdatePreviewIndex(
        EdgeCapsuleModel model,
        int index)
    {
        if (model.DragSession is not { } session ||
            model.State.Gesture is not (
                EdgeCapsuleGestureState.DockedReordering or
                EdgeCapsuleGestureState.FloatingReordering))
        {
            return Reject(model, "Preview index requires an active reorder session.");
        }
        return Accept(model, model with
        {
            DragSession = session with { PreviewIndex = index }
        });
    }

    private static EdgeCapsuleDispatchResult BeginFloatingTransfer(
        EdgeCapsuleModel model,
        DeviceScreenPoint point)
    {
        if (model.State.Gesture != EdgeCapsuleGestureState.DockedReordering ||
            model.DragSession is not { } session)
        {
            return Reject(model, "Floating transfer requires a docked reorder session.");
        }
        return Accept(model, model with
        {
            State = model.State with { Gesture = EdgeCapsuleGestureState.FloatingTransfer },
            DragSession = session with { LastScreenPosition = point },
            DockedDragTopDipOverride = null
        });
    }

    private static EdgeCapsuleDispatchResult BeginFloatingReorder(EdgeCapsuleModel model)
    {
        if (model.State.Gesture != EdgeCapsuleGestureState.FloatingTransfer ||
            model.DragSession == null)
        {
            return Reject(model, "Floating reorder requires a floating transfer session.");
        }
        return Accept(model, model with
        {
            State = model.State with { Gesture = EdgeCapsuleGestureState.FloatingReordering }
        });
    }

    private static EdgeCapsuleDispatchResult FinishPointer(EdgeCapsuleModel model) =>
        Accept(model, model with
        {
            State = model.State with { Gesture = EdgeCapsuleGestureState.Idle },
            DragSession = null,
            DockedDragTopDipOverride = null
        });

    private static EdgeCapsuleModel DetachedModel(EdgeCapsuleModel model) => model with
    {
        State = model.State with
        {
            Slot = EdgeCapsuleSlotState.None,
            Visual = EdgeCapsuleVisualState.Resting,
            Gesture = EdgeCapsuleGestureState.Idle
        },
        Placement = EdgeCapsulePlacement.None,
        DragSession = null,
        ContextMenuOpen = false,
        PeerReorderActive = false,
        DockedDragTopDipOverride = null
    };

    private static bool HasStructuralInvariants(EdgeCapsuleModel model)
    {
        var attached = model.State.Slot != EdgeCapsuleSlotState.None;
        return attached == model.Placement.IsPlaced &&
            (attached || !model.PeerReorderActive) &&
            ((model.State.Gesture == EdgeCapsuleGestureState.Idle) == (model.DragSession == null)) &&
            (model.State.Slot is not (
                EdgeCapsuleSlotState.RetractedCollapsed or
                EdgeCapsuleSlotState.RetractedExpanded or
                EdgeCapsuleSlotState.RetractingCollapsed or
                EdgeCapsuleSlotState.RetractingExpanded) ||
                model.State.Visual == EdgeCapsuleVisualState.Resting) &&
            (model.State.Gesture is not (
                EdgeCapsuleGestureState.DockedReordering or
                EdgeCapsuleGestureState.FloatingTransfer or
                EdgeCapsuleGestureState.FloatingReordering) || attached);
    }

    private static EdgeCapsuleDispatchResult Accept(
        EdgeCapsuleModel previous,
        EdgeCapsuleModel next) =>
        next == previous
            ? Unchanged(previous)
            : new EdgeCapsuleDispatchResult(EdgeCapsuleDispatchStatus.Applied, next);

    private static EdgeCapsuleDispatchResult Unchanged(EdgeCapsuleModel model) =>
        new(EdgeCapsuleDispatchStatus.Unchanged, model);

    private static EdgeCapsuleDispatchResult Reject(EdgeCapsuleModel model, string error) =>
        new(EdgeCapsuleDispatchStatus.Rejected, model, error);
}
