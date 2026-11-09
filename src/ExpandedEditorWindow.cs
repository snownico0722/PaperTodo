using System;
using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace PaperTodo;

/// <summary>
/// One long-lived WPF HWND and taskbar entry. Its single content presenter borrows the
/// existing PaperWindow shell for whichever paper is selected; no second document/editor.
/// The borrowed shell already includes the user's ordered PaperTodo top bar.
/// </summary>
internal sealed partial class ExpandedEditorWindow : Window
{
    private readonly AppController _controller;
    private readonly Border _frame;
    private readonly RectangleGeometry _frameClip = new();
    private readonly Decorator _body = new();
    private PaperWindow? _current;
    private PaperWindow.ExpandedEditorVisualLease? _lease;
    private bool _finishing;
    private string? _collapseAfterSystemClose;
    private bool _lastNonMinimizedMaximized;

    internal PaperWindow? CurrentPaper => _current;
    internal string? CurrentPaperId => _current?.PaperId;

    internal ExpandedEditorWindow(AppController controller, PaperWindow? initial = null)
    {
        _controller = controller;
        Title = StudioTitle(initial);
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        AllowsTransparency = false;
        Background = Theme.PaperBrush;
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
            BorderThickness = new Thickness(0),
            ClipToBounds = true,
            Clip = _frameClip,
            Child = _body
        };
        Content = _frame;
        if (initial != null) SetFrameTheme(initial);
        else _frame.Background = Background;

        var area = WindowWorkAreaHelper.WorkAreaFor(initial);
        Width = Math.Min(area.Width, Math.Max(600, Math.Min(1320, area.Width * 0.84)));
        Height = Math.Min(area.Height, Math.Max(400, Math.Min(920, area.Height * 0.86)));
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;

        if (initial?.SavedExpandedEditorState is { } saved &&
            double.IsFinite(saved.X) && double.IsFinite(saved.Y) &&
            double.IsFinite(saved.Width) && double.IsFinite(saved.Height) &&
            saved.Width > 0 && saved.Height > 0)
        {
            var savedBounds = new Rect(saved.X, saved.Y, saved.Width, saved.Height);
            area = WindowWorkAreaHelper.WorkAreaFor(savedBounds);
            Width = Math.Min(area.Width, Math.Max(MinWidth, saved.Width));
            Height = Math.Min(area.Height, Math.Max(MinHeight, saved.Height));
            Left = Math.Clamp(saved.X, area.Left, Math.Max(area.Left, area.Right - Width));
            Top = Math.Clamp(saved.Y, area.Top, Math.Max(area.Top, area.Bottom - Height));
            _lastNonMinimizedMaximized = saved.Maximized;
            if (saved.Maximized) WindowState = WindowState.Maximized;
        }

        _frame.SizeChanged += (_, _) => RefreshEditorWindowCorners();
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized)
                _lastNonMinimizedMaximized = WindowState == WindowState.Maximized;
            RefreshEditorWindowCorners();
            _current?.MarkExpandedEditorViewDirty();
        };
        LocationChanged += (_, _) => _current?.MarkExpandedEditorViewDirty();
        SizeChanged += (_, _) => _current?.MarkExpandedEditorViewDirty();
        SourceInitialized += (_, _) =>
        {
            // DWM clips the real non-layered HWND, while WPF clips the inner visual.
            // Rounding only an extra Border left black square corners on the host.
            try
            {
                var rounding = 2u; // DWMWCP_ROUND
                _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle,
                    33, ref rounding, sizeof(uint));
            }
            catch (DllNotFoundException) { } // Older Windows without DWM.
            catch (EntryPointNotFoundException) { }
        };

        Activated += (_, _) => _controller.OnStudioActivated(this);
        BuildPaperSidebar();
        IsSidebarOpen = initial == null || (initial.SavedExpandedEditorState?.SidebarOpen ?? false);
        if (initial != null) SwitchTo(initial);
        else ShowEmptyStudio();
    }

    private static string StudioTitle(PaperWindow? paper) => paper == null
        ? $"{Strings.Get("StudioTitle")} — PaperTodo"
        : $"{paper.ExpandedEditorTitle} — {Strings.Get("StudioTitle")}";

    private void SetFrameTheme(PaperWindow source)
    {
        Background = source.Resources["PaperBrushKey"] as Brush ?? Theme.PaperBrush;
        _frame.Background = Background;
        _frame.BorderBrush = source.Resources["PaperBorderBrushKey"] as Brush
            ?? Brushes.Gray;
    }

    internal void SwitchTo(PaperWindow target)
    {
        if (_finishing || target.IsClosed || ReferenceEquals(_current, target))
            return;
        if (target.IsExpandedEditorHosted)
        {
            target.ExpandedEditorHostWindow?.ActivateEditor();
            return;
        }
        DetachEmptyStudio();
        var former = _lease;
        var formerPaperId = _current?.PaperId;
        _lease = null;
        _current = null;
        former?.Return(restoreDesktop: false);
        if (formerPaperId != null) _controller.CollapseExpandedEditorPaper(formerPaperId);
        _lease = target.BorrowExpandedEditorVisual(this, _body);
        _current = target;
        RefreshPaperList();
        Title = StudioTitle(target);
        SetFrameTheme(target);
        SetPaperTopmost(target.ExpandedEditorShouldTopmost);
        if (IsVisible) Activate();
        target.QueueExpandedEditorFocus();
    }

    internal void RefreshEditorTheme(PaperWindow paper)
    {
        if (ReferenceEquals(_current, paper))
        {
            SetFrameTheme(paper);
            RefreshPaperList();
        }
    }

    internal void RefreshPaperTitle(PaperWindow paper)
    {
        if (ReferenceEquals(_current, paper))
            Title = StudioTitle(paper);
    }

    internal void SetPaperTopmost(bool topmost)
    {
        if (Topmost != topmost) Topmost = topmost;
    }

    internal void ActivateEditor()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = _lastNonMinimizedMaximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
    }

    internal ExpandedEditorState CaptureWindowState()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        return new ExpandedEditorState
        {
            X = bounds.Left, Y = bounds.Top, Width = bounds.Width, Height = bounds.Height,
            Maximized = _lastNonMinimizedMaximized,
            SidebarOpen = IsSidebarOpen
        };
    }

    private void RefreshEditorWindowCorners()
    {
        var radius = WindowState == WindowState.Maximized ? 0d : 12d;
        _frame.CornerRadius = new CornerRadius(radius);
        _frameClip.Rect = new Rect(0, 0,
            Math.Max(0, _frame.ActualWidth), Math.Max(0, _frame.ActualHeight));
        _frameClip.RadiusX = radius;
        _frameClip.RadiusY = radius;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, uint attribute, ref uint value, int size);

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

    // The original far-right × always folds the editor's current paper to a
    // capsule (or hides it when capsules are disabled). It is not a second close button.
    internal void CollapseCurrentPaper()
    {
        if (_finishing) return;
        _finishing = true;
        var current = _current;
        ReleaseCurrent(restoreDesktop: false);
        Close();
        if (current != null && !current.IsClosed)
            _controller.CollapseExpandedEditorPaper(current.PaperId);
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
        base.OnClosing(e);
        if (e.Cancel || _finishing) return;

        // A system close is already in progress. Calling Close() again from here is
        // re-entrant and can throw on WPF; finish the host close once, then fold the paper.
        _finishing = true;
        var current = _current;
        _collapseAfterSystemClose = current?.PaperId;
        ReleaseCurrent(restoreDesktop: false);
    }

    protected override void OnClosed(EventArgs e)
    {
        ReleaseCurrent(restoreDesktop: false);
        _controller.UnregisterExpandedEditor(this);
        base.OnClosed(e);
        if (_collapseAfterSystemClose is { } paperId)
        {
            _collapseAfterSystemClose = null;
            _controller.CollapseExpandedEditorPaper(paperId);
        }
    }
}
