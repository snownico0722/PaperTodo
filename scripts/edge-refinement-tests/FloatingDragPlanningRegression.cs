extern alias PaperTodoApp;

using System.Runtime.CompilerServices;
using AppEdge = PaperTodoApp::PaperTodo.EdgeCapsuleEdge;
using AppGeometry = PaperTodoApp::PaperTodo.EdgeCapsuleGeometry;
using AppGeometryInput = PaperTodoApp::PaperTodo.EdgeCapsuleGeometryInput;
using AppGesture = PaperTodoApp::PaperTodo.EdgeCapsuleGestureState;
using AppLayout = PaperTodoApp::PaperTodo.EdgeCapsuleLayoutSnapshot;
using AppModel = PaperTodoApp::PaperTodo.EdgeCapsuleModel;
using AppMonitor = PaperTodoApp::PaperTodo.MonitorGeometry;
using AppOpenOrigin = PaperTodoApp::PaperTodo.EdgeCapsuleOpenOrigin;
using AppPlacement = PaperTodoApp::PaperTodo.EdgeCapsulePlacement;
using AppPlanner = PaperTodoApp::PaperTodo.EdgeCapsuleTargetPlanner;
using AppPoint = PaperTodoApp::PaperTodo.DeviceScreenPoint;
using AppPreview = PaperTodoApp::PaperTodo.EdgeCapsulePreviewState;
using AppProxyPolicy = PaperTodoApp::PaperTodo.EdgeCapsuleQueueProxyPolicy;
using AppRect = PaperTodoApp::PaperTodo.DeviceScreenRect;
using AppSlot = PaperTodoApp::PaperTodo.EdgeCapsuleSlotState;
using AppState = PaperTodoApp::PaperTodo.EdgeCapsuleState;
using AppSurface = PaperTodoApp::PaperTodo.EdgeCapsuleSurfaceKind;
using AppVisual = PaperTodoApp::PaperTodo.EdgeCapsuleVisualState;
using AppDragSession = PaperTodoApp::PaperTodo.EdgeCapsuleDragSession;

namespace PaperTodo;

internal static class FloatingDragPlanningRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        var monitor = new AppMonitor(
            "display",
            new AppRect(0, 0, 5120, 1440),
            1,
            1);
        var layout = new AppLayout(
            monitor,
            AppEdge.Right,
            NormalTopDip: 240,
            MasterTopDip: 60,
            RestingWidthDip: 140,
            MaximumCloseWidthDip: 28,
            HeightDip: 58,
            PreviewWidthDip: 360,
            PreviewHeightDip: 240,
            CloseSegmentActsAsContent: false,
            RestingContentOpacity: 0.82,
            ForcedContentOpacity: null);
        var model = new AppModel(
            new AppState(
                AppSlot.CollapsedDocked,
                AppVisual.Hovered,
                AppGesture.FloatingTransfer,
                AppOpenOrigin.Normal),
            new AppPlacement(2, 0, 8, 0),
            AppDragSession.Begin(new AppPoint(5000, 320)),
            ContextMenuOpen: false,
            PeerReorderActive: false,
            Preview: AppPreview.Closed,
            PointerOverSurface: true,
            DockedDragTopDipOverride: 250);

        var plan = AppPlanner.Calculate(model, layout);
        var compact = AppGeometry.Calculate(
            new AppGeometryInput(
                monitor,
                AppEdge.Right,
                TopDip: 250,
                RestingWidthDip: 140,
                CloseWidthDip: 0,
                HeightDip: 58));
        Assert(
            plan.Docked.Surface == AppSurface.DockedSuppressed,
            "floating transfer must suppress the permanent docked surface");
        Assert(
            plan.Docked.Bounds == compact.Bounds,
            "suppressed docked source retained the hovered close-segment width");
        Assert(
            plan.Docked.InteractiveBounds.IsEmpty &&
            !plan.Docked.IsHitTestVisible &&
            Math.Abs(plan.Docked.ContentOpacity) < 0.001,
            "suppressed docked source remained interactive or visible");
        Assert(
            plan.Floating.Visible,
            "floating transfer did not produce its floating shape");

        Assert(
            AppProxyPolicy.AllowsQueueProxyOwnership(
                AppGesture.Idle,
                floatingCoverActive: false),
            "idle preview transaction was rejected");
        Assert(
            AppProxyPolicy.AllowsQueueProxyOwnership(
                AppGesture.PendingClick,
                floatingCoverActive: false),
            "pending-click preview transaction was rejected");
        Assert(
            !AppProxyPolicy.AllowsQueueProxyOwnership(
                AppGesture.FloatingTransfer,
                floatingCoverActive: false) &&
            !AppProxyPolicy.AllowsQueueProxyOwnership(
                AppGesture.FloatingReordering,
                floatingCoverActive: false) &&
            !AppProxyPolicy.AllowsQueueProxyOwnership(
                AppGesture.DockingHandoff,
                floatingCoverActive: false) &&
            !AppProxyPolicy.AllowsQueueProxyOwnership(
                AppGesture.DockingReveal,
                floatingCoverActive: false),
            "floating drag handoff was allowed to start a redundant queue proxy");
        Assert(
            !AppProxyPolicy.AllowsQueueProxyOwnership(
                AppGesture.Idle,
                floatingCoverActive: true),
            "an existing floating cover did not veto queue proxy ownership");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
