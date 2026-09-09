using System.Windows;
using System.Windows.Input;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private enum PaperWindowLifecycleState
    {
        Alive,
        Closing,
        Closed
    }

    private enum PaperPresentationState
    {
        Expanded,
        Collapsing,
        Collapsed,
        Expanding,
        Closing,
        Closed
    }

    private enum InteractionAbortReason
    {
        Deactivated,
        Hiding,
        FormChanging,
        Closing
    }

    private sealed class TitleBarDragSession
    {
        public TitleBarDragSession(
            FrameworkElement source,
            Point startPosition,
            DeviceScreenPoint startScreenPosition,
            ExperimentalTetherDragAnchor? tetherAnchor)
        {
            Source = source;
            StartPosition = startPosition;
            StartScreenPosition = startScreenPosition;
            TetherAnchor = tetherAnchor;
        }

        public FrameworkElement Source { get; }
        public Point StartPosition { get; }
        public DeviceScreenPoint StartScreenPosition { get; }
        public ExperimentalTetherDragAnchor? TetherAnchor { get; }
        public bool TetherMoved { get; set; }
    }

    private PaperWindowLifecycleState _windowLifecycle = PaperWindowLifecycleState.Alive;
    private PaperPresentationState _presentationState;
    private TitleBarDragSession? _titleBarDragSession;
    private int _titleEditIntentGeneration;

    public bool IsClosed => _windowLifecycle == PaperWindowLifecycleState.Closed;
    private bool IsPaperFormTransitioning => _presentationState is
        PaperPresentationState.Collapsing or
        PaperPresentationState.Expanding;

    private void InitializePaperPresentationState()
    {
        _presentationState = _paper.IsCollapsed
            ? PaperPresentationState.Collapsed
            : PaperPresentationState.Expanded;
    }

    private void BeginPaperFormTransition(bool collapsed)
    {
        _presentationState = collapsed
            ? PaperPresentationState.Collapsing
            : PaperPresentationState.Expanding;
        RefreshNativeMica();
    }

    private void CompletePaperFormTransition(bool collapsed)
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive)
        {
            return;
        }

        _presentationState = collapsed
            ? PaperPresentationState.Collapsed
            : PaperPresentationState.Expanded;

        RefreshNativeMica();

        // Collapse: release images only after the form transition finishes so the fading shell
        // still shows bitmaps. Expand restores rendering earlier (when the shell becomes visible).
        if (collapsed && _paper.Type == PaperTypes.Note)
        {
            ReleaseHiddenNoteImages();
        }
    }

    private void CancelPendingTitleEditIntent()
    {
        _titleEditIntentGeneration++;
    }

    internal void CommitPendingEditsForSave()
    {
        CancelPendingTitleEditIntent();
        if (_isEditingTitle)
        {
            EndTitleEdit(commit: true);
        }

        CommitPendingNoteContent();
    }

    internal void CommitPendingNoteContentForSave()
        => CommitPendingNoteContent();

    private void CommitPendingNoteContent()
    {
        if (_paper.Type == PaperTypes.Note)
        {
            CommitCurrentPaperBody();
        }
    }

    internal void PrepareForHide()
    {
        _controller.CompleteEdgeCapsuleQueueCompositionProxyFor(this);
        CommitPendingEditsForSave();
        CancelExperimentalTetherPresentation(showMain: false);
        DetachExperimentalWindowAttachment(savePosition: false);
        SettlePaperFormPresentation();
        AbortAllInteractions(InteractionAbortReason.Hiding);
    }

    internal void PrepareForShow()
        => SyncNoteImagePresentationState();

    internal void ReleaseHiddenNoteImages()
    {
        if (_paper.Type != PaperTypes.Note)
        {
            return;
        }

        if (_paperBodyHost.HasCurrent)
        {
            NotifyCurrentPaperBodyVisibility(false);
            return;
        }

        _noteBox?.SetImageRenderingSuspended(true);
        _controller.ImageStore.ReleaseNoteBitmapCache(_paper.Id);
    }

    /// <summary>
    /// Align image render/cache with whether the note body is actually on screen.
    /// Uses paper visibility (not window.IsVisible) so ShowPaper can run before Show().
    /// </summary>
    internal void SyncNoteImagePresentationState()
    {
        if (_paper.Type != PaperTypes.Note)
        {
            return;
        }

        var visible =
            _paper.IsVisible &&
            !_paper.IsCollapsed &&
            WindowState != WindowState.Minimized;
        if (_paperBodyHost.HasCurrent)
        {
            NotifyCurrentPaperBodyVisibility(visible);
            return;
        }

        _noteBox?.SetImageRenderingSuspended(!visible);
        if (!visible)
        {
            _controller.ImageStore.ReleaseNoteBitmapCache(_paper.Id);
        }
    }

    internal void PrepareForCapsulePresentationModeChange()
    {
        _controller.CompleteEdgeCapsuleQueueCompositionProxyFor(this);
        CommitPendingEditsForSave();
        SettlePaperFormPresentation();
        AbortAllInteractions(InteractionAbortReason.FormChanging);
        DetachExperimentalWindowAttachment(savePosition: true);
        if (!_paper.IsVisible)
        {
            CancelPendingVisibilityTransitions();
            HideWithoutGeometrySave();
        }
    }

    internal void SettleAnimationsForDisabledSetting()
    {
        _controller.CompleteEdgeCapsuleQueueCompositionProxyFor(this);
        CancelPendingVisibilityTransitions();
        SettlePaperFormPresentation();
        RefreshExperimentalOpacity(animate: false);
        if (!_paper.IsVisible)
        {
            HideWithoutGeometrySave();
            return;
        }

        if (_paper.IsCollapsed &&
            _controller.State.UseCapsuleMode &&
            _controller.State.UseDeepCapsuleMode &&
            HasDeepCapsuleSlotPlacement)
        {
            HideMainWindowForDeepCapsuleRest();
        }
    }

    private void PrepareForFormTransition()
    {
        CommitPendingEditsForSave();
        AbortAllInteractions(InteractionAbortReason.FormChanging);
    }

    private void BeginPaperWindowClose()
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive)
        {
            return;
        }

        // The compositor proxy can only hand off while this window still accepts endpoint frames.
        // Reveal the small real host before changing the lifecycle state to Closing.
        _controller.CompleteEdgeCapsuleQueueCompositionProxyFor(this);
        CommitPendingEditsForSave();
        _windowLifecycle = PaperWindowLifecycleState.Closing;
        _presentationState = PaperPresentationState.Closing;
        _collapseTransitionGeneration++;
        CancelPaperFormAnimationClocks();
        AbortAllInteractions(InteractionAbortReason.Closing);
    }

    private void CompletePaperWindowClose()
    {
        CancelExperimentalTetherPresentation(showMain: false);
        DetachExperimentalWindowAttachment(savePosition: false);
        WindowNative.ReleaseWindowSwitcherOwner(ref _windowSwitcherHiddenOwner);
        _windowSwitcherHiddenOwnerApplied = false;
        ReleaseHiddenNoteImages();
        DisposeCurrentPaperBody();
        _windowLifecycle = PaperWindowLifecycleState.Closed;
        _presentationState = PaperPresentationState.Closed;
        CancelPendingTitleEditIntent();
    }

    private void AbortAllInteractions(InteractionAbortReason reason)
    {
        CancelPendingTitleEditIntent();
        CancelCurrentPaperBodyInteractions();
        EndTitleBarDragGesture();
        CancelCapsulePointerInteraction();
        EndExperimentalCapsuleMagnetDragPreview();

        CancelTodoSweepSelection(
            clearSelection: reason != InteractionAbortReason.Deactivated);

        if (_todoDrag != null)
        {
            EndTodoMouseDrag(commit: false);
        }

        EndTopBarDragGesture(commit: false);

        CancelDeepCapsuleReorderDrag();
        _suppressTodoBackspaceUntilKeyUp = false;
        Mouse.OverrideCursor = null;

        if (reason == InteractionAbortReason.Deactivated)
        {
            return;
        }

        CloseDeepCapsuleSlotContextMenu();
    }

    private void CancelPaperFormAnimationClocks()
    {
        BeginAnimation(TransitionProgressProperty, null);
        _shell?.BeginAnimation(UIElement.OpacityProperty, null);
        _capsuleShell?.BeginAnimation(UIElement.OpacityProperty, null);
    }

    private void SettlePaperFormPresentation()
    {
        if (!IsPaperFormTransitioning)
        {
            return;
        }

        _collapseTransitionGeneration++;
        CancelPaperFormAnimationClocks();
        ResetTransitionVisuals();
        _shell.Width = double.NaN;
        _shell.Height = double.NaN;

        MoveWindowWithoutGeometrySave(() =>
        {
            if (_paper.IsCollapsed)
            {
                _shell.Visibility = Visibility.Collapsed;
                _shell.Opacity = 0;
                _capsuleShell.Visibility = Visibility.Visible;
                _capsuleShell.Opacity = 1;
                MinWidth = CapsuleWindowWidth();
                MinHeight = PaperLayoutDefaults.CapsuleHeight;
                ResizeMode = ResizeMode.NoResize;
                Width = CapsuleWindowWidth();
                Height = PaperLayoutDefaults.CapsuleHeight;
                return;
            }

            _shell.Visibility = Visibility.Visible;
            _shell.Opacity = 1;
            _capsuleShell.Visibility = Visibility.Collapsed;
            _capsuleShell.Opacity = 0;
            MinWidth = PaperLayoutDefaults.MinWidth;
            MinHeight = PaperLayoutDefaults.MinHeight;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            if (_controller.UsesNativeMicaWindows)
            {
                Width = Math.Max(_targetTransitionWidth, PaperLayoutDefaults.MinWidth);
                Height = Math.Max(_targetTransitionHeight, PaperLayoutDefaults.MinHeight);
            }
            else if (Width <= DesiredCapsuleWindowWidth + 8 ||
                Height <= PaperLayoutDefaults.CapsuleHeight + 8)
            {
                Width = Math.Max(_paper.Width, PaperLayoutDefaults.MinWidth);
                Height = Math.Max(_paper.Height, PaperLayoutDefaults.MinHeight);
            }
        });
        CompletePaperFormTransition(_paper.IsCollapsed);
    }
}
