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
        public TitleBarDragSession(FrameworkElement source, Point startPosition)
        {
            Source = source;
            StartPosition = startPosition;
        }

        public FrameworkElement Source { get; }
        public Point StartPosition { get; }
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
        if (_paper.Type != PaperTypes.Note || _noteBox == null || !_noteContentDirty)
        {
            return;
        }

        _paper.Content = _noteBox.PersistentText;
        _noteContentDirty = false;
    }

    internal void PrepareForHide()
    {
        CommitPendingEditsForSave();
        SettlePaperFormPresentation();
        AbortAllInteractions(InteractionAbortReason.Hiding);
    }

    internal void SettleAnimationsForDisabledSetting()
    {
        CancelPendingVisibilityTransitions();
        SettlePaperFormPresentation();
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

        CommitPendingEditsForSave();
        _windowLifecycle = PaperWindowLifecycleState.Closing;
        _presentationState = PaperPresentationState.Closing;
        _collapseTransitionGeneration++;
        CancelPaperFormAnimationClocks();
        AbortAllInteractions(InteractionAbortReason.Closing);
    }

    private void CompletePaperWindowClose()
    {
        WindowNative.ReleaseWindowSwitcherOwner(ref _windowSwitcherHiddenOwner);
        _windowSwitcherHiddenOwnerApplied = false;
        _windowLifecycle = PaperWindowLifecycleState.Closed;
        _presentationState = PaperPresentationState.Closed;
        CancelPendingTitleEditIntent();
    }

    private void AbortAllInteractions(InteractionAbortReason reason)
    {
        CancelPendingTitleEditIntent();
        CancelNotePresenterDeferredWork();
        EndTitleBarDragGesture();
        CancelCapsulePointerInteraction();

        if (_todoDrag != null)
        {
            EndTodoMouseDrag(commit: false);
        }

        if (_noteLinkDrag != null)
        {
            EndNoteLinkMouseGesture(commit: false);
        }

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
        CompletePaperFormTransition(_paper.IsCollapsed);
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
            if (Width <= DesiredCapsuleWindowWidth + 8 ||
                Height <= PaperLayoutDefaults.CapsuleHeight + 8)
            {
                Width = Math.Max(_paper.Width, PaperLayoutDefaults.MinWidth);
                Height = Math.Max(_paper.Height, PaperLayoutDefaults.MinHeight);
            }
        });
    }
}
