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
    private Border? _editorMarkdownToolbar;
    private Border? _editorPaperSidebar;
    private StackPanel? _editorPaperList;
    private FrameworkElement? _editorContentElement;
    private Thickness _editorContentOriginalMargin;
    private Action? _enterCurrentNoteEditor;
    private Action? _noteEditorImageAction;

    internal bool IsExpandedEditorMode => _isExpandedEditorMode;
    internal bool IsExpandedEditorSidebarOpen => _isExpandedEditorSidebarOpen;
    internal void SetExpandedEditorSidebarOpen(bool open)
    {
        _isExpandedEditorSidebarOpen = open;
        RefreshExpandedEditorPresentation();
    }
    internal bool IsExpandedEditorMaximized => _expandedEditorHostWindow?.WindowState == WindowState.Maximized;

    // The mode action participates in PaperTopBarShortcuts' saved order like any other
    // user action. Only system-window controls and the sidebar switch remain fixed.
    private void BuildExpandedEditorTopBarControls(
        StackPanel windowButtons, StackPanel userShortcuts)
    {
        _editorModeButton = IconButton("", Strings.Get("EditorOpen"));
        _editorModeButton.Content = CreateExpandedEditorModeIcon(_editorModeButton, expanded: false);
        _editorModeButton.Click += (_, _) =>
        {
            if (_isExpandedEditorMode) _expandedEditorHostWindow?.ReturnToPaper();
            else _controller.OpenExpandedEditorFor(this);
        };
        userShortcuts.Children.Add(_editorModeButton);

        _editorSidebarButton = IconButton("", Strings.Get("EditorSidebarShow"));
        _editorSidebarButton.Content = CreateEditorVectorIcon(_editorSidebarButton,
            "M 5 4 H 19 A 2 2 0 0 1 21 6 V 18 A 2 2 0 0 1 19 20 H 5 " +
            "A 2 2 0 0 1 3 18 V 6 A 2 2 0 0 1 5 4 Z " +
            "M 15 4 V 20 M 7 9 H 11 M 7 12 H 11 M 7 15 H 11");
        _editorSidebarButton.Click += (_, _) =>
            SetExpandedEditorSidebarOpen(!_isExpandedEditorSidebarOpen);

        _editorMinimizeButton = IconButton("−", Strings.Get("EditorMinimize"));
        _editorMinimizeButton.Click += (_, _) =>
        {
            if (_expandedEditorHostWindow != null)
                _expandedEditorHostWindow.WindowState = WindowState.Minimized;
        };
        _editorMaximizeButton = IconButton("□", Strings.Get("EditorMaximizeRestore"));
        _editorMaximizeButton.Click += (_, _) => ToggleExpandedEditorMaximized();

        windowButtons.Children.Add(_editorSidebarButton);
        windowButtons.Children.Add(_editorMinimizeButton);
        windowButtons.Children.Add(_editorMaximizeButton);
        RefreshExpandedEditorTopBarControls();
    }

    private static FrameworkElement CreateExpandedEditorModeIcon(Button owner, bool expanded) =>
        CreateEditorVectorIcon(owner, expanded
            // Two arrows face toward the center when returning to a paper.
            ? "M 4 10 H 10 V 4 M 10 10 L 4 4 M 20 14 H 14 V 20 M 14 14 L 20 20"
            // Two arrows open toward opposite corners in paper mode.
            : "M 8 3 H 3 V 8 M 3 3 L 9 9 M 16 21 H 21 V 16 M 21 21 L 15 15");

    // WPF Paths use SVG-compatible geometry commands: centered in the same 24-unit
    // canvas used by PaperTodo's native top-bar vectors, independent of font metrics.
    private static FrameworkElement CreateEditorVectorIcon(Button owner, string geometry)
    {
        var iconCanvas = new Canvas
        {
            Width = 24,
            Height = 24,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false
        };
        var outline = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(geometry),
            StrokeThickness = 1.9,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
            SnapsToDevicePixels = true
        };
        outline.SetBinding(
            System.Windows.Shapes.Shape.StrokeProperty, CreateForegroundBinding(owner));
        iconCanvas.Children.Add(outline);
        return new Viewbox
        {
            Width = Math.Round(16 * TopBarIconScale(), 1),
            Height = Math.Round(16 * TopBarIconScale(), 1),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Child = iconCanvas
        };
    }

    private void RefreshExpandedEditorTopBarControls()
    {
        if (_editorModeButton == null) return;
        _editorModeButton.Content =
            CreateExpandedEditorModeIcon(_editorModeButton, _isExpandedEditorMode);
        _editorModeButton.ToolTip = _isExpandedEditorMode
            ? Strings.Get("EditorReturn") : Strings.Get("EditorOpen");

        if (_editorSidebarButton != null)
        {
            _editorSidebarButton.Visibility = _isExpandedEditorMode
                ? Visibility.Visible : Visibility.Collapsed;
            _editorSidebarButton.ToolTip = _isExpandedEditorSidebarOpen
                ? Strings.Get("EditorSidebarHide") : Strings.Get("EditorSidebarShow");
            _editorSidebarButton.Foreground = _isExpandedEditorSidebarOpen
                ? Theme.ActiveBrush : WeakTextBrush;
        }
        if (_editorMinimizeButton != null)
            _editorMinimizeButton.Visibility = _isExpandedEditorMode
                ? Visibility.Visible : Visibility.Collapsed;
        if (_editorMaximizeButton != null)
        {
            _editorMaximizeButton.Visibility = _isExpandedEditorMode
                ? Visibility.Visible : Visibility.Collapsed;
            _editorMaximizeButton.Content = IsExpandedEditorMaximized ? "❐" : "□";
        }
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

    private Style BuildEditorPaperListButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 7, 9, 7)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.Name = "ItemSurface";
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        surface.SetValue(Border.BackgroundProperty,
            new TemplateBindingExtension(Control.BackgroundProperty));
        surface.SetValue(Border.PaddingProperty,
            new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentProperty,
            new TemplateBindingExtension(ContentControl.ContentProperty));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        surface.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
        var hovering = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        hovering.Setters.Add(new Setter(Border.BackgroundProperty, HoverBrush, "ItemSurface"));
        template.Triggers.Add(hovering);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    internal void RefreshExpandedEditorPaperList()
    {
        if (_editorPaperList == null || !_isExpandedEditorMode || !_isExpandedEditorSidebarOpen)
            return;
        _editorPaperList.Children.Clear();
        var itemStyle = BuildEditorPaperListButtonStyle();
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
                Style = itemStyle
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
        // The sidebar spans the whole body area, independently of Markdown's toolbar.
        _editorPaperSidebar.Margin = new Thickness(0);

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
