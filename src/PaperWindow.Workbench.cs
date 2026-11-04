using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private WorkbenchBodyLease? _workbenchBody;
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
            _shell.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(WorkbenchInteractionLock), true);
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
            _shell.RemoveHandler(PreviewKeyDownEvent, new KeyEventHandler(WorkbenchInteractionLock));
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

    private void WorkbenchInteractionLock(object sender, KeyEventArgs e)
    {
        if (_advancedInteractionLocked) e.Handled = true;
    }
    private void WorkbenchGotFocus(object sender, KeyboardFocusChangedEventArgs e) => NotifyCurrentPaperBodyActivated();
    private void WorkbenchDeactivated(object? sender, EventArgs e)
    {
        NotifyCurrentPaperBodyDeactivated();
        AbortAllInteractions(InteractionAbortReason.Deactivated);
    }

    internal ContextMenu CreateWorkbenchBodyMenu()
    {
        var menu = CreateContextMenu();
        if (_paper.Type == PaperTypes.Note)
        {
            menu.Items.Add(BuildPaperBodyProviderMenuItem());
            AttachPluginTopBarActionsToMenu(menu);
        }
        return menu;
    }

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
            _desktopWasVisible = owner.IsVisible;
            _paperWasVisibleOnDesktop = owner._paper.IsVisible;
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
            if (!owner._containerGrid.Children.Contains(owner._shell)) owner._containerGrid.Children.Add(owner._shell);
            if (owner._topBarHost != null) owner._topBarHost.Visibility = _topBarVisibility;
            // Neither desktop geometry nor collapsed/visible flags are copied from the workbench.
            owner._shell.Width = double.NaN; owner._shell.Height = double.NaN;
            owner._shell.Visibility = owner._paper.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            owner._shell.Opacity = owner._paper.IsCollapsed ? 0 : 1;
            owner.SyncNoteImagePresentationState();
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
