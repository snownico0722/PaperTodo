using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    // Expanded editor is a presentation of an existing Paper, never a second document.
    // Keep its window geometry separate from PaperData's ordinary/capsule geometry.
    private const double ExpandedEditorCornerRadius = 12;
    private const double ExpandedEditorToolbarHeight = 36;
    private const double ExpandedEditorSidebarWidth = 228;

    private bool _isExpandedEditorMode;
    private bool _isExpandedEditorSidebarOpen;
    private bool _editorRestoreCollapsedPaper;
    private Button? _editorModeButton;
    private Button? _editorSidebarButton;
    private Button? _editorMinimizeButton;
    private Button? _editorMaximizeButton;
    private Button? _editorCloseButton;
    private Border? _editorMarkdownToolbar;
    private Border? _editorPaperSidebar;
    private StackPanel? _editorPaperList;
    private FrameworkElement? _editorContentElement;
    private Thickness _editorContentOriginalMargin;
    private Action? _enterCurrentNoteEditor;
    private Action? _noteEditorImageAction;

    internal bool IsExpandedEditorMode => _isExpandedEditorMode;
    internal bool IsExpandedEditorSidebarOpen => _isExpandedEditorSidebarOpen;
    internal bool IsExpandedEditorMaximized => WindowState == WindowState.Maximized;

    internal Rect ExpandedEditorWindowBounds()
    {
        var bounds = WindowState == WindowState.Maximized ? RestoreBounds :
            new Rect(Left, Top, Width, Height);
        return IsValidEditorBounds(bounds) ? bounds : Rect.Empty;
    }

    private static bool IsValidEditorBounds(Rect bounds) =>
        !bounds.IsEmpty && double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
        double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
        bounds.Width > 300 && bounds.Height > 200;

    private void BuildExpandedEditorTopBarControls(StackPanel buttons)
    {
        // Fixed mode/window controls live after the existing user-ordered top-bar group.
        // Do not insert them into PaperTopBarShortcuts or alter the pinned divider.
        _editorModeButton = IconButton("⤢", Strings.Get("EditorOpen"));
        _editorModeButton.Click += (_, _) =>
        {
            if (_isExpandedEditorMode) ReturnFromExpandedEditor();
            else EnterExpandedEditorMode();
        };

        _editorSidebarButton = IconButton("☷", Strings.Get("EditorSidebarShow"));
        _editorSidebarButton.Click += (_, _) =>
        {
            _isExpandedEditorSidebarOpen = !_isExpandedEditorSidebarOpen;
            RefreshExpandedEditorPresentation();
        };

        _editorMinimizeButton = IconButton("−", Strings.Get("EditorMinimize"));
        _editorMinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;

        _editorMaximizeButton = IconButton("□", Strings.Get("EditorMaximizeRestore"));
        _editorMaximizeButton.Click += (_, _) => ToggleExpandedEditorMaximized();

        _editorCloseButton = IconButton("×", Strings.Get("EditorClosePaper"));
        _editorCloseButton.Click += (_, _) => _controller.HidePaper(_paper);

        buttons.Children.Add(_editorModeButton);
        buttons.Children.Add(_editorSidebarButton);
        buttons.Children.Add(_editorMinimizeButton);
        buttons.Children.Add(_editorMaximizeButton);
        buttons.Children.Add(_editorCloseButton);
        RefreshExpandedEditorTopBarControls();
    }

    private void RefreshExpandedEditorTopBarControls()
    {
        if (_editorModeButton == null) return;
        _editorModeButton.Content = _isExpandedEditorMode ? "⤡" : "⤢";
        _editorModeButton.ToolTip = _isExpandedEditorMode ? Strings.Get("EditorReturn") : Strings.Get("EditorOpen");
        if (_editorSidebarButton != null)
        {
            _editorSidebarButton.Visibility = _isExpandedEditorMode ? Visibility.Visible : Visibility.Collapsed;
            _editorSidebarButton.ToolTip = _isExpandedEditorSidebarOpen ? Strings.Get("EditorSidebarHide") : Strings.Get("EditorSidebarShow");
            _editorSidebarButton.Foreground = _isExpandedEditorSidebarOpen ? Theme.ActiveBrush : WeakTextBrush;
        }
        if (_editorMinimizeButton != null)
            _editorMinimizeButton.Visibility = _isExpandedEditorMode ? Visibility.Visible : Visibility.Collapsed;
        if (_editorMaximizeButton != null)
        {
            _editorMaximizeButton.Visibility = _isExpandedEditorMode ? Visibility.Visible : Visibility.Collapsed;
            _editorMaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        }
        if (_editorCloseButton != null)
            _editorCloseButton.Visibility = _isExpandedEditorMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleExpandedEditorMaximized()
    {
        if (!_isExpandedEditorMode) return;
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        RefreshExpandedEditorTopBarControls();
        RefreshSnappedPresentation(forceApply: true);
    }

    // This is deliberately layered over the existing body row. The Note/Plugin host
    // can replace its body at runtime without acquiring a second editor or visual tree.
    private void BuildExpandedEditorChrome()
    {
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 6, 0)
        };

        void AddTool(string name, string hint, Action action)
        {
            var button = IconButton(name, hint);
            button.FontSize = AppTypography.Scale(12);
            button.Click += (_, _) =>
            {
                if (!_isExpandedEditorMode || !IsCurrentBodyProviderMarkdown || _noteBox == null)
                    return;
                _enterCurrentNoteEditor?.Invoke();
                action();
                _noteBox?.Focus();
            };
            tools.Children.Add(button);
        }

        AddTool("↶", Strings.Get("EditorUndo"), () => _noteBox?.Undo());
        AddTool("↷", Strings.Get("EditorRedo"), () => _noteBox?.Redo());
        AddTool("H", Strings.Get("EditorHeading"), () => _noteBox?.InsertLinePrefix("# "));
        AddTool("B", Strings.Get("EditorBold"), () => _noteBox?.WrapSelection("**", "**"));
        AddTool("I", Strings.Get("EditorItalic"), () => _noteBox?.WrapSelection("*", "*"));
        AddTool("S", Strings.Get("EditorStrike"), () => _noteBox?.WrapSelection("~~", "~~"));
        AddTool("•", Strings.Get("EditorBullets"), () => _noteBox?.InsertLinePrefix("- "));
        AddTool("1.", Strings.Get("EditorNumbers"), () => _noteBox?.InsertLinePrefix("1. "));
        AddTool("❝", Strings.Get("EditorQuote"), () => _noteBox?.InsertLinePrefix("> "));
        AddTool("</>", Strings.Get("EditorCode"), () => _noteBox?.WrapSelection(" " + "```" + "\n", "\n" + "```"));
        AddTool("↗", Strings.Get("EditorLink"), () => _noteBox?.InsertMarkdownLink());
        AddTool("▧", Strings.Get("EditorImage"), () => _noteEditorImageAction?.Invoke());

        _editorMarkdownToolbar = new Border
        {
            Height = ExpandedEditorToolbarHeight,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = tools,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Top
        };
        _editorMarkdownToolbar.SetResourceReference(Border.BackgroundProperty, "TitleBarBrushKey");
        _editorMarkdownToolbar.SetResourceReference(Border.BorderBrushProperty, "TitleBarDividerBrushKey");
        Grid.SetRow(_editorMarkdownToolbar, 1);
        Panel.SetZIndex(_editorMarkdownToolbar, 80);
        _shell.Children.Add(_editorMarkdownToolbar);

        _editorPaperList = new StackPanel { Orientation = Orientation.Vertical };
        var listHeader = new TextBlock
        {
            Text = Strings.Get("EditorAllPapers"),
            FontWeight = FontWeights.SemiBold,
            FontSize = AppTypography.Scale(12),
            Margin = new Thickness(12, 12, 8, 8),
            Foreground = TextBrush
        };
        var sidebarContents = new DockPanel();
        DockPanel.SetDock(listHeader, Dock.Top);
        sidebarContents.Children.Add(listHeader);
        sidebarContents.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _editorPaperList
        });
        _editorPaperSidebar = new Border
        {
            Width = ExpandedEditorSidebarWidth,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = sidebarContents
        };
        _editorPaperSidebar.SetResourceReference(Border.BackgroundProperty, "PaperSurfaceBrushKey");
        _editorPaperSidebar.SetResourceReference(Border.BorderBrushProperty, "TitleBarDividerBrushKey");
        Grid.SetRow(_editorPaperSidebar, 1);
        Panel.SetZIndex(_editorPaperSidebar, 90);
        _shell.Children.Add(_editorPaperSidebar);
    }

    private void RefreshExpandedEditorPaperList()
    {
        if (_editorPaperList == null || !_isExpandedEditorMode || !_isExpandedEditorSidebarOpen)
            return;
        _editorPaperList.Children.Clear();
        foreach (var paper in _controller.State.Papers.ToArray())
        {
            var current = paper.Id == _paper.Id;
            var title = _controller.PaperDisplayTitle(paper);
            var kind = paper.Type == PaperTypes.Todo ? Strings.Get("EditorTodo") :
                paper.Type == PaperTypes.Note ? Strings.Get("EditorNote") : Strings.Get("EditorPaper");
            var line = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(4, 1, 4, 1)
            };
            line.Children.Add(new TextBlock
            {
                Text = title,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = TextBrush
            });
            line.Children.Add(new TextBlock
            {
                Text = kind,
                FontSize = AppTypography.Scale(10),
                Foreground = WeakTextBrush
            });
            var button = new Button
            {
                Content = line,
                ToolTip = _controller.PaperTitleText(paper),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(9, 7, 9, 7),
                Margin = new Thickness(4, 1, 4, 1),
                BorderThickness = new Thickness(0),
                Background = current ? HoverBrush : Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            button.Click += (_, _) => _controller.SwitchExpandedEditorPaper(this, paper.Id);
            _editorPaperList.Children.Add(button);
        }
    }

    private void RefreshExpandedEditorPresentation()
    {
        if (_editorMarkdownToolbar == null || _editorPaperSidebar == null) return;
        var showMarkdownTools = _isExpandedEditorMode &&
            _paper.Type == PaperTypes.Note && IsCurrentBodyProviderMarkdown && _noteBox != null;
        _editorMarkdownToolbar.Visibility = showMarkdownTools ? Visibility.Visible : Visibility.Collapsed;
        _editorMarkdownToolbar.Margin = new Thickness(
            0, 0, _isExpandedEditorMode && _isExpandedEditorSidebarOpen ? ExpandedEditorSidebarWidth : 0, 0);
        _editorPaperSidebar.Visibility = _isExpandedEditorMode && _isExpandedEditorSidebarOpen
            ? Visibility.Visible : Visibility.Collapsed;
        _editorPaperSidebar.Margin = new Thickness(
            0, showMarkdownTools ? ExpandedEditorToolbarHeight : 0, 0, 0);

        // A plugin provider can swap out _bodyElement after the shell was constructed.
        var body = (_paper.Type == PaperTypes.Note ? _bodyElement : null) as FrameworkElement
            ?? _editorContentElement;
        if (body != null && !ReferenceEquals(body, _editorContentElement))
        {
            if (_editorContentElement != null)
                _editorContentElement.Margin = _editorContentOriginalMargin;
            _editorContentElement = body;
            _editorContentOriginalMargin = body.Margin;
        }
        if (_editorContentElement != null)
        {
            var m = _editorContentOriginalMargin;
            _editorContentElement.Margin = new Thickness(
                m.Left,
                m.Top + (showMarkdownTools ? ExpandedEditorToolbarHeight : 0),
                m.Right + (_isExpandedEditorMode && _isExpandedEditorSidebarOpen ? ExpandedEditorSidebarWidth : 0),
                m.Bottom);
        }
        if (_isExpandedEditorSidebarOpen)
            RefreshExpandedEditorPaperList();
        RefreshExpandedEditorTopBarControls();
        UpdateTopBarResponsiveLayout();
    }

    internal void EnterExpandedEditorMode(
        Rect? preferredBounds = null,
        bool openSidebar = false,
        bool restoreCollapsedPaper = false,
        bool maximized = false)
    {
        if (IsClosed || !_paper.IsVisible) return;
        if (_isExpandedEditorMode)
        {
            Activate();
            return;
        }
        restoreCollapsedPaper |= _paper.IsCollapsed;
        if (_paper.IsCollapsed)
            SetCollapsedState(false, animate: false, saveGeometry: false);
        EnsureShellBuilt();
        CancelExperimentalAutoCollapse();
        _isExpandedEditorMode = true;
        _editorRestoreCollapsedPaper = restoreCollapsedPaper;
        _isExpandedEditorSidebarOpen = openSidebar;
        var workArea = WindowWorkAreaHelper.WorkAreaFor(this);
        Rect desired;
        if (preferredBounds.HasValue && IsValidEditorBounds(preferredBounds.Value))
        {
            desired = preferredBounds.Value;
        }
        else
        {
            var width = Math.Min(1320, Math.Max(560, workArea.Width * 0.84));
            var height = Math.Min(920, Math.Max(400, workArea.Height * 0.86));
            width = Math.Min(width, workArea.Width);
            height = Math.Min(height, workArea.Height);
            desired = new Rect(
                workArea.Left + (workArea.Width - width) / 2,
                workArea.Top + (workArea.Height - height) / 2,
                width, height);
        }
        _suppressGeometrySave = true;
        try
        {
            WindowState = WindowState.Normal;
            MinWidth = Math.Min(640, desired.Width);
            MinHeight = Math.Min(420, desired.Height);
            ResizeMode = ResizeMode.CanResize;
            Left = desired.Left;
            Top = desired.Top;
            Width = desired.Width;
            Height = desired.Height;
            if (maximized) WindowState = WindowState.Maximized;
        }
        finally
        {
            _suppressGeometrySave = false;
        }

        ApplySystemVisibility(reapplyTaskbarShellState: true);
        RefreshEffectiveTopmost();
        RefreshSnappedPresentation(forceApply: true);
        RefreshExpandedEditorPresentation();
        _enterCurrentNoteEditor?.Invoke();
        Activate();
    }

    internal void ReturnFromExpandedEditor()
    {
        if (!_isExpandedEditorMode) return;
        _suppressGeometrySave = true;
        try
        {
            _isExpandedEditorMode = false;
            _isExpandedEditorSidebarOpen = false;
            WindowState = WindowState.Normal;
            MinWidth = PaperLayoutDefaults.MinWidth;
            MinHeight = PaperLayoutDefaults.MinHeight;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Left = _paper.X;
            Top = _paper.Y;
            Width = _paper.Width;
            Height = _paper.Height;
            if (_editorRestoreCollapsedPaper && CanDisplayAsCapsule())
                SetCollapsedState(true, animate: false, saveGeometry: false);
            _editorRestoreCollapsedPaper = false;
        }
        finally
        {
            _suppressGeometrySave = false;
        }
        ApplySystemVisibility(reapplyTaskbarShellState: true);
        RefreshEffectiveTopmost();
        RefreshSnappedPresentation(forceApply: true);
        RefreshExpandedEditorPresentation();
        if (!_paper.IsCollapsed) _showNotePreview?.Invoke();
    }

    private void OnExpandedEditorVisibilityChanged()
    {
        // Closing an editor is the ordinary HidePaper action. Reset only once it is
        // actually hidden so its exit does not flash a shrunken Paper during fade-out.
        if (_isExpandedEditorMode && !IsVisible && !_paper.IsVisible)
            ReturnFromExpandedEditor();
    }
}
