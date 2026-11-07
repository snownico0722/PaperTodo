using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private WorkbenchBodyLease? _workbenchBody;
    // A reload can return and immediately reborrow the visual while the original
    // HWND remains hidden. The first lease owns the real desktop presentation.
    private bool? _workbenchDesktopVisibleBeforeSuspension;
    private bool? _workbenchPaperVisibleBeforeSuspension;
    internal bool IsWorkbenchHosted => _workbenchBody != null;
    private FrameworkElement InteractionRoot => IsWorkbenchHosted ? _shell : this;
    private Window InteractionWindow => _workbenchBody?.Window ?? this;
    internal string WorkbenchBindingTitle => HasExperimentalExternalWindowAttachment
        ? _experimentalWindowAttachment!.TargetTitle : "";
    internal string WorkbenchAffinity => HasExperimentalExternalWindowAttachment
        ? "app:" + _experimentalWindowAttachment!.ExternalWindow.Handle.ToString()
        : !IsCurrentBodyProviderMarkdown && _paper.Type == PaperTypes.Note ? "plugin:" + _paper.BodyProviderId : "";

    // A single visual lease, not a second editor/session/model. PaperWindow remains the lifetime
    // owner; the workbench must return the visual before reload, workspace teardown or disposal.
    internal WorkbenchBodyLease BorrowWorkbenchBody(Window window, Decorator destination)
    {
        EnsureShellBuilt();
        if (_workbenchBody != null) throw new InvalidOperationException("The paper already has a workbench host.");
        _controller.CompleteEdgeCapsuleQueueCompositionProxyFor(this);
        AbortAllInteractions(InteractionAbortReason.FormChanging);
        CancelExperimentalAutoCollapse();
        // Settle the original form before moving its visual: invalidates old
        // completion callbacks and restores resize/visibility endpoint state.
        SettlePaperFormPresentation();
        CancelPaperFormAnimationClocks();
        ResetTransitionVisuals();
        var lease = new WorkbenchBodyLease(this, window, destination);
        _workbenchBody = lease;
        try
        {
            HideWithoutGeometrySave();
            HideExpandedDeepCapsuleSlotHost(animate: false);
            _containerGrid.Children.Remove(_shell);
            _shell.Resources.MergedDictionaries.Add(Resources);
            _shell.Width = double.NaN;
            _shell.Height = double.NaN;
            _shell.Visibility = Visibility.Visible;
            _shell.Opacity = 1;
            if (_topBarHost != null) _topBarHost.Visibility = Visibility.Collapsed;
            HookWorkbenchInput(true);
            destination.Child = _shell;
            window.Deactivated += WorkbenchDeactivated;
            IsVisibleChanged += WorkbenchDesktopVisibilityChanged;
            NotifyCurrentPaperBodyVisibility(true);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private void HookWorkbenchInput(bool attach)
    {
        if (attach)
        {
            _shell.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(WorkbenchCopyTranslationPreviewKeyDown), true);
            _shell.AddHandler(ContextMenuService.ContextMenuOpeningEvent,
                new ContextMenuEventHandler(WorkbenchCopyTranslationContextMenuOpening), true);
            _shell.PreviewMouseMove += OnWindowPreviewMouseMove;
            _shell.PreviewMouseWheel += OnWindowPreviewMouseWheel;
            _shell.PreviewMouseLeftButtonUp += OnWindowPreviewMouseLeftButtonUp;
            _shell.LostMouseCapture += OnLostMouseCapture;
            _shell.PreviewKeyDown += OnWindowPreviewKeyDown;
            _shell.PreviewKeyUp += OnWindowPreviewKeyUp;
            _shell.GotKeyboardFocus += WorkbenchGotFocus;
            if (_paper.Type == PaperTypes.Todo)
            {
                _shell.PreviewMouseLeftButtonDown += OnTodoSelectionWindowPreviewMouseLeftButtonDown;
                _shell.PreviewKeyDown += OnTodoSelectionWindowPreviewKeyDown;
                _shell.PreviewMouseMove += OnTodoSweepPreviewMouseMove;
                _shell.PreviewMouseLeftButtonUp += OnTodoSweepPreviewMouseLeftButtonUp;
                _shell.LostMouseCapture += OnTodoSweepLostMouseCapture;
                _shell.LostMouseCapture += OnTodoDragLostMouseCapture;
            }
        }
        else
        {
            _shell.RemoveHandler(PreviewKeyDownEvent, new KeyEventHandler(WorkbenchCopyTranslationPreviewKeyDown));
            _shell.RemoveHandler(ContextMenuService.ContextMenuOpeningEvent,
                new ContextMenuEventHandler(WorkbenchCopyTranslationContextMenuOpening));
            _shell.PreviewMouseMove -= OnWindowPreviewMouseMove;
            _shell.PreviewMouseWheel -= OnWindowPreviewMouseWheel;
            _shell.PreviewMouseLeftButtonUp -= OnWindowPreviewMouseLeftButtonUp;
            _shell.LostMouseCapture -= OnLostMouseCapture;
            _shell.PreviewKeyDown -= OnWindowPreviewKeyDown;
            _shell.PreviewKeyUp -= OnWindowPreviewKeyUp;
            _shell.GotKeyboardFocus -= WorkbenchGotFocus;
            _shell.PreviewMouseLeftButtonDown -= OnTodoSelectionWindowPreviewMouseLeftButtonDown;
            _shell.PreviewKeyDown -= OnTodoSelectionWindowPreviewKeyDown;
            _shell.PreviewMouseMove -= OnTodoSweepPreviewMouseMove;
            _shell.PreviewMouseLeftButtonUp -= OnTodoSweepPreviewMouseLeftButtonUp;
            _shell.LostMouseCapture -= OnTodoSweepLostMouseCapture;
            _shell.LostMouseCapture -= OnTodoDragLostMouseCapture;
        }
    }

    private void WorkbenchDesktopVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsWorkbenchHosted && IsVisible) HideWithoutGeometrySave();
    }

    // Desktop lock is deliberately not applied to the editing workbench.
    // Keep original copy/translation commands on the borrowed editor surface.
    private void WorkbenchCopyTranslationPreviewKeyDown(object sender, KeyEventArgs e)
        => OnCopyTranslationPreviewKeyDown(this, e);
    private void WorkbenchCopyTranslationContextMenuOpening(object sender, ContextMenuEventArgs e)
        => OnCopyTranslationContextMenuOpening(this, e);
    private void WorkbenchGotFocus(object sender, KeyboardFocusChangedEventArgs e) => NotifyCurrentPaperBodyActivated();
    private void WorkbenchDeactivated(object? sender, EventArgs e)
    {
        NotifyCurrentPaperBodyDeactivated();
        AbortAllInteractions(InteractionAbortReason.Deactivated);
    }

    // Reuse the same native paper menu, styling, plugin actions and OnOpened refresh
    // that desktop papers use. Workbench-specific filtering is in PopulatePaperContextMenu.
    internal ContextMenu CreateWorkbenchBodyMenu() => BuildPaperContextMenu();

    internal sealed class WorkbenchBodyLease : IDisposable
    {
        private PaperWindow? _owner;
        private readonly Decorator _destination;
        private readonly bool _desktopWasVisible;
        private readonly bool _paperWasVisibleOnDesktop;
        private readonly Visibility _topBarVisibility;
        internal Window Window { get; }
        internal WorkbenchBodyLease(PaperWindow owner, Window window, Decorator destination)
        {
            _owner = owner; Window = window; _destination = destination;
            _desktopWasVisible = owner._workbenchDesktopVisibleBeforeSuspension ?? owner.IsVisible;
            _paperWasVisibleOnDesktop = owner._workbenchPaperVisibleBeforeSuspension ?? owner._paper.IsVisible;
            _topBarVisibility = owner._topBarHost?.Visibility ?? Visibility.Visible;
        }
        internal void Return(bool restoreDesktop)
        {
            var owner = _owner;
            if (owner == null) return;
            _owner = null;
            owner.AbortAllInteractions(InteractionAbortReason.FormChanging);
            Window.Deactivated -= owner.WorkbenchDeactivated;
            owner.IsVisibleChanged -= owner.WorkbenchDesktopVisibilityChanged;
            owner.HookWorkbenchInput(false);
            if (ReferenceEquals(_destination.Child, owner._shell)) _destination.Child = null;
            owner._shell.Resources.MergedDictionaries.Remove(owner.Resources);
            owner._workbenchBody = null;
            owner._workbenchDesktopVisibleBeforeSuspension = restoreDesktop ? null : _desktopWasVisible;
            owner._workbenchPaperVisibleBeforeSuspension = restoreDesktop ? null : _paperWasVisibleOnDesktop;
            if (!owner._containerGrid.Children.Contains(owner._shell)) owner._containerGrid.Children.Add(owner._shell);
            if (owner._topBarHost != null) owner._topBarHost.Visibility = _topBarVisibility;
            // Neither desktop geometry nor collapsed/visible flags are copied from the workbench.
            owner._shell.Width = double.NaN; owner._shell.Height = double.NaN;
            owner._shell.Visibility = owner._paper.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            owner._shell.Opacity = owner._paper.IsCollapsed ? 0 : 1;
            owner.SyncNoteImagePresentationState();
            if (restoreDesktop) owner.UpdateAdvancedInteractionLockVisuals();
            if (restoreDesktop && owner._paper.IsVisible && !owner.IsClosed &&
                (_desktopWasVisible || !_paperWasVisibleOnDesktop && !owner._paper.IsCollapsed))
            {
                var showActivated = owner.ShowActivated;
                owner.ShowActivated = false;
                try { owner.MoveWindowWithoutGeometrySave(owner.Show); }
                finally { owner.ShowActivated = showActivated; }
            }
        }
        public void Dispose() => Return(restoreDesktop: true);
    }
}
