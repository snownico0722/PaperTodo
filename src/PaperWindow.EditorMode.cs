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
    internal bool IsExpandedEditorMaximized => _expandedEditorHostWindow?.WindowState == WindowState.Maximized;

    private void BuildExpandedEditorTopBarControls(StackPanel buttons)
    {
        // Fixed mode/window controls live after the existing user-ordered top-bar group.
        // Do not insert them into PaperTopBarShortcuts or alter the pinned divider.
        _editorModeButton = IconButton("⤢", Strings.Get("EditorOpen"));
        _editorModeButton.Click += (_, _) =>
        {
            if (_isExpandedEditorMode) _expandedEditorHostWindow?.ReturnToPaper();
            else _controller.OpenExpandedEditorFor(this);
        };

        _editorSidebarButton = IconButton("☷", Strings.Get("EditorSidebarShow"));
        _editorSidebarButton.Click += (_, _) =>
        {
            _isExpandedEditorSidebarOpen = !_isExpandedEditorSidebarOpen;
            RefreshExpandedEditorPresentation();
        };

        _editorMinimizeButton = IconButton("−", Strings.Get("EditorMinimize"));
        _editorMinimizeButton.Click += (_, _) =>
        {
            if (_expandedEditorHostWindow != null)
                _expandedEditorHostWindow.WindowState = WindowState.Minimized;
        };

        _editorMaximizeButton = IconButton("□", Strings.Get("EditorMaximizeRestore"));
        _editorMaximizeButton.Click += (_, _) => ToggleExpandedEditorMaximized();

        _editorCloseButton = IconButton("×", Strings.Get("EditorClosePaper"));
        _editorCloseButton.Click += (_, _) => _expandedEditorHostWindow?.CloseCurrentPaper();

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
            _editorMaximizeButton.Content = IsExpandedEditorMaximized ? "❐" : "□";
        }
        if (_editorCloseButton != null)
            _editorCloseButton.Visibility = _isExpandedEditorMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleExpandedEditorMaximized()
    {
        _expandedEditorHostWindow?.ToggleMaximized();
        RefreshExpandedEditorTopBarControls();
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
        AddTool("</>", Strings.Get("EditorCode"), () => _noteBox?.WrapSelection("```\n", "\n```"));
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

}
