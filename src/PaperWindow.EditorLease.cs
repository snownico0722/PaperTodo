using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private ExpandedEditorWindow? _expandedEditorHostWindow;
    private ExpandedEditorVisualLease? _expandedEditorVisualLease;

    internal ExpandedEditorWindow? ExpandedEditorHostWindow => _expandedEditorHostWindow;
    internal string ExpandedEditorTitle => _controller.PaperTitleText(_paper);
    internal bool IsExpandedEditorHosted => _expandedEditorVisualLease != null;
    internal bool ExpandedEditorShouldTopmost => _paper.AlwaysOnTop;
    private FrameworkElement InteractionRoot => IsExpandedEditorHosted ? _shell : this;
    private Window InteractionWindow => (_expandedEditorHostWindow as Window) ?? this;

    // Same visual-lease boundary as the directly editable workspace canvas (#399).
    // The original PaperWindow retains its document, Undo stack, Markdown session and plugin
    // callbacks; only its existing WPF shell is temporarily hosted by the editor HWND.
    internal ExpandedEditorVisualLease BorrowExpandedEditorVisual(
        ExpandedEditorWindow host, Decorator destination)
    {
        EnsureShellBuilt();
        if (_expandedEditorVisualLease != null)
            throw new InvalidOperationException("Paper already belongs to an expanded editor.");

        _controller.CompleteEdgeCapsuleQueueCompositionProxyFor(this);
        AbortAllInteractions(InteractionAbortReason.FormChanging);
        CancelExperimentalAutoCollapse();
        CancelPaperFormAnimationClocks();
        ResetTransitionVisuals();

        var wasCollapsed = _paper.IsCollapsed;
        if (wasCollapsed)
            SetCollapsedState(false, animate: false, saveGeometry: false);

        var lease = new ExpandedEditorVisualLease(this, host, destination, wasCollapsed);
        _expandedEditorVisualLease = lease;
        _expandedEditorHostWindow = host;
        _isExpandedEditorMode = true;
        host.SetPaperTopmost(_paper.AlwaysOnTop);
        RefreshCloseButton();
        ApplyPaperChromePresentation();
        try
        {
            HideWithoutGeometrySave();
            ApplySystemVisibility(reapplyTaskbarShellState: true);
            HideExpandedDeepCapsuleSlotHost(animate: false);
            _containerGrid.Children.Remove(_shell);
            _shell.Resources.MergedDictionaries.Add(Resources);
            _shell.Width = double.NaN;
            _shell.Height = double.NaN;
            _shell.Visibility = Visibility.Visible;
            _shell.Opacity = 1;
            if (_topBarHost != null) _topBarHost.Visibility = Visibility.Visible;
            HookExpandedEditorInput(true);
            destination.Child = _shell;
            host.Deactivated += OnExpandedEditorHostDeactivated;
            host.StateChanged += OnExpandedEditorHostStateChanged;
            host.LocationChanged += OnExpandedEditorHostLocationChanged;
            host.SizeChanged += OnExpandedEditorHostSizeChanged;
            IsVisibleChanged += OnExpandedEditorOwnerVisibilityChanged;
            NotifyCurrentPaperBodyVisibility(true);
            RefreshExpandedEditorPresentation();
            _enterCurrentNoteEditor?.Invoke();
            RefreshExpandedEditorTopBarControls();
            return lease;
        }
        catch
        {
            lease.Return(restoreDesktop: true);
            throw;
        }
    }

    private void HookExpandedEditorInput(bool attach)
    {
        if (attach)
        {
            _shell.AddHandler(PreviewKeyDownEvent,
                new KeyEventHandler(OnExpandedEditorInputLock), true);
            _shell.PreviewMouseMove += OnWindowPreviewMouseMove;
            _shell.PreviewMouseWheel += OnWindowPreviewMouseWheel;
            _shell.PreviewMouseLeftButtonUp += OnWindowPreviewMouseLeftButtonUp;
            _shell.LostMouseCapture += OnLostMouseCapture;
            _shell.PreviewKeyDown += OnWindowPreviewKeyDown;
            _shell.PreviewKeyUp += OnWindowPreviewKeyUp;
            _shell.PreviewKeyDown += OnExpandedEditorFindShortcut;
            _shell.GotKeyboardFocus += OnExpandedEditorBodyGotFocus;
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
            _shell.RemoveHandler(PreviewKeyDownEvent,
                new KeyEventHandler(OnExpandedEditorInputLock));
            _shell.PreviewMouseMove -= OnWindowPreviewMouseMove;
            _shell.PreviewMouseWheel -= OnWindowPreviewMouseWheel;
            _shell.PreviewMouseLeftButtonUp -= OnWindowPreviewMouseLeftButtonUp;
            _shell.LostMouseCapture -= OnLostMouseCapture;
            _shell.PreviewKeyDown -= OnWindowPreviewKeyDown;
            _shell.PreviewKeyUp -= OnWindowPreviewKeyUp;
            _shell.PreviewKeyDown -= OnExpandedEditorFindShortcut;
            _shell.GotKeyboardFocus -= OnExpandedEditorBodyGotFocus;
            _shell.PreviewMouseLeftButtonDown -= OnTodoSelectionWindowPreviewMouseLeftButtonDown;
            _shell.PreviewKeyDown -= OnTodoSelectionWindowPreviewKeyDown;
            _shell.PreviewMouseMove -= OnTodoSweepPreviewMouseMove;
            _shell.PreviewMouseLeftButtonUp -= OnTodoSweepPreviewMouseLeftButtonUp;
            _shell.LostMouseCapture -= OnTodoSweepLostMouseCapture;
            _shell.LostMouseCapture -= OnTodoDragLostMouseCapture;
        }
    }

    private void OnExpandedEditorInputLock(object sender, KeyEventArgs e)
    {
        if (_advancedInteractionLocked) e.Handled = true;
    }

    private void OnExpandedEditorFindShortcut(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.F ||
            Keyboard.Modifiers != ModifierKeys.Control || !CanUseBuiltInFind())
            return;
        e.Handled = true;
        ShowBuiltInFind();
    }

    private void OnExpandedEditorBodyGotFocus(object sender, KeyboardFocusChangedEventArgs e)
        => NotifyCurrentPaperBodyActivated();

    private void OnExpandedEditorHostLocationChanged(object? sender, EventArgs e)
        => RepositionBuiltInFindPopup();

    private void OnExpandedEditorHostSizeChanged(object? sender, SizeChangedEventArgs e)
        => RepositionBuiltInFindPopup();

    private void OnExpandedEditorHostDeactivated(object? sender, EventArgs e)
    {
        NotifyCurrentPaperBodyDeactivated();
        AbortAllInteractions(InteractionAbortReason.Deactivated);
    }

    private void OnExpandedEditorHostStateChanged(object? sender, EventArgs e)
    {
        if (_expandedEditorHostWindow is { } host)
        {
            var visible = host.WindowState != WindowState.Minimized;
            NotifyCurrentPaperBodyVisibility(visible);
            if (!visible) HideBuiltInFind(restoreFocus: false);
        }
        RefreshExpandedEditorTopBarControls();
    }

    private void OnExpandedEditorOwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Tray/show-all may try to reveal the original PaperWindow while its shell is
        // borrowed. The editor is the only visible owner until the lease is returned.
        if (IsExpandedEditorHosted && IsVisible)
            HideWithoutGeometrySave();
    }

    internal sealed class ExpandedEditorVisualLease : IDisposable
    {
        private PaperWindow? _owner;
        private readonly Decorator _destination;
        private readonly bool _desktopWasVisible;
        private readonly bool _wasCollapsed;
        private readonly Visibility _topBarVisibility;
        private readonly ExpandedEditorWindow _host;

        internal ExpandedEditorVisualLease(
            PaperWindow owner, ExpandedEditorWindow host,
            Decorator destination, bool wasCollapsed)
        {
            _owner = owner;
            _host = host;
            _destination = destination;
            _wasCollapsed = wasCollapsed;
            _desktopWasVisible = owner.IsVisible;
            _topBarVisibility = owner._topBarHost?.Visibility ?? Visibility.Visible;
        }

        internal void Return(bool restoreDesktop, Window? keepBehind = null)
        {
            var owner = _owner;
            if (owner == null) return;
            _owner = null;

            owner.CommitPendingEditsForSave();
            owner.HideBuiltInFind(restoreFocus: false);
            owner.AbortAllInteractions(InteractionAbortReason.FormChanging);
            _host.Deactivated -= owner.OnExpandedEditorHostDeactivated;
            _host.StateChanged -= owner.OnExpandedEditorHostStateChanged;
            _host.LocationChanged -= owner.OnExpandedEditorHostLocationChanged;
            _host.SizeChanged -= owner.OnExpandedEditorHostSizeChanged;
            owner.IsVisibleChanged -= owner.OnExpandedEditorOwnerVisibilityChanged;
            owner.HookExpandedEditorInput(false);
            if (ReferenceEquals(_destination.Child, owner._shell))
                _destination.Child = null;
            owner._shell.Resources.MergedDictionaries.Remove(owner.Resources);
            owner._expandedEditorVisualLease = null;
            owner._expandedEditorHostWindow = null;
            owner._isExpandedEditorMode = false;
            owner.RefreshCloseButton();
            if (!owner._containerGrid.Children.Contains(owner._shell))
                owner._containerGrid.Children.Add(owner._shell);
            if (owner._topBarHost != null)
                owner._topBarHost.Visibility = _topBarVisibility;
            owner._shell.Width = double.NaN;
            owner._shell.Height = double.NaN;
            owner._shell.Visibility = Visibility.Visible;
            owner._shell.Opacity = 1;
            owner.RefreshExpandedEditorPresentation();
            owner._showNotePreview?.Invoke();
            if (_wasCollapsed && owner.CanDisplayAsCapsule() && !owner._paper.IsCollapsed)
                owner.SetCollapsedState(true, animate: false, saveGeometry: false);
            owner.ApplyPaperChromePresentation();
            owner.ApplySystemVisibility(reapplyTaskbarShellState: true);
            owner.SyncNoteImagePresentationState();

            if (restoreDesktop && _desktopWasVisible && owner._paper.IsVisible && !owner.IsClosed)
            {
                var activated = owner.ShowActivated;
                owner.ShowActivated = false;
                try
                {
                    owner.MoveWindowWithoutGeometrySave(owner.Show);
                    if (keepBehind != null)
                        WindowNative.TryPlaceWindowBehindWithoutActivation(owner, keepBehind);
                }
                finally { owner.ShowActivated = activated; }
            }
        }

        public void Dispose() => Return(restoreDesktop: true);
    }
}
