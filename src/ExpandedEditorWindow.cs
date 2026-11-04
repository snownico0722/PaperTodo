using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace PaperTodo;

/// <summary>
/// One long-lived WPF HWND and taskbar entry. Its single content presenter borrows the
/// existing PaperWindow shell for whichever paper is selected; no second document/editor.
/// The borrowed shell already includes the user's ordered PaperTodo top bar.
/// </summary>
internal sealed class ExpandedEditorWindow : Window
{
    private readonly AppController _controller;
    private readonly Border _frame;
    private readonly Decorator _body = new();
    private PaperWindow? _current;
    private PaperWindow.ExpandedEditorVisualLease? _lease;
    private bool _finishing;

    internal PaperWindow? CurrentPaper => _current;
    internal string? CurrentPaperId => _current?.PaperId;

    internal ExpandedEditorWindow(AppController controller, PaperWindow initial)
    {
        _controller = controller;
        Title = initial.ExpandedEditorTitle;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        AllowsTransparency = false;
        Background = Brushes.Transparent;
        MinWidth = 600;
        MinHeight = 400;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            UseAeroCaptionButtons = false
        });

        _frame = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = _body
        };
        Content = _frame;
        SetFrameTheme(initial);

        var area = WindowWorkAreaHelper.WorkAreaFor(initial);
        Width = Math.Min(area.Width, Math.Max(600, Math.Min(1320, area.Width * 0.84)));
        Height = Math.Min(area.Height, Math.Max(400, Math.Min(920, area.Height * 0.86)));
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;

        StateChanged += (_, _) =>
        {
            _frame.CornerRadius = WindowState == WindowState.Maximized
                ? new CornerRadius(0) : new CornerRadius(12);
        };

        SwitchTo(initial);
    }

    private void SetFrameTheme(PaperWindow source)
    {
        _frame.Background = source.Resources["PaperSurfaceBrushKey"] as Brush
            ?? Theme.PaperBrush;
        _frame.BorderBrush = source.Resources["PaperBorderBrushKey"] as Brush
            ?? Brushes.Gray;
    }

    internal void SwitchTo(PaperWindow target)
    {
        if (_finishing || target.IsClosed || ReferenceEquals(_current, target))
            return;
        if (target.IsExpandedEditorHosted)
        {
            target.ExpandedEditorHostWindow?.Activate();
            return;
        }
        var keepSidebarOpen = _current?.IsExpandedEditorSidebarOpen ?? false;
        var former = _lease;
        _lease = null;
        _current = null;
        former?.Return(restoreDesktop: true);
        _lease = target.BorrowExpandedEditorVisual(this, _body);
        _current = target;
        target.SetExpandedEditorSidebarOpen(keepSidebarOpen);
        Title = target.ExpandedEditorTitle;
        SetFrameTheme(target);
        if (IsVisible) Activate();
    }

    internal void RefreshPaperTitle(PaperWindow paper)
    {
        if (ReferenceEquals(_current, paper))
            Title = paper.ExpandedEditorTitle;
    }

    internal void ToggleMaximized()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal : WindowState.Maximized;
    }

    internal void ReturnToPaper()
    {
        if (_finishing) return;
        _finishing = true;
        var current = _current;
        ReleaseCurrent(restoreDesktop: false);
        Close();
        if (current != null && !current.IsClosed)
            _controller.RestoreExpandedEditorPaper(current.PaperId);
    }

    internal void CloseCurrentPaper()
    {
        if (_finishing) return;
        _finishing = true;
        var current = _current;
        ReleaseCurrent(restoreDesktop: false);
        Close();
        if (current != null && !current.IsClosed)
            _controller.HideExpandedEditorPaper(current.PaperId);
    }

    // Called before a workspace teardown, deletion, or external hide: do not
    // resurrect a desktop paper while the caller is withdrawing its presentation.
    internal void CloseWithoutPaperAction()
    {
        if (_finishing) return;
        _finishing = true;
        ReleaseCurrent(restoreDesktop: false);
        Close();
    }

    private void ReleaseCurrent(bool restoreDesktop)
    {
        var lease = _lease;
        _lease = null;
        _current = null;
        lease?.Return(restoreDesktop);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_finishing)
        {
            _finishing = true;
            var paper = _current;
            ReleaseCurrent(restoreDesktop: false);
            if (paper != null && !paper.IsClosed)
                _controller.HideExpandedEditorPaper(paper.PaperId);
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ReleaseCurrent(restoreDesktop: false);
        _controller.UnregisterExpandedEditor(this);
        base.OnClosed(e);
    }
}
