using System;
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
    private const double ExpandedEditorSidebarWidth = ExpandedEditorWindow.SidebarWidth;

    private bool _isExpandedEditorMode;
    private Button? _editorModeButton;
    private Button? _editorSidebarButton;
    private Button? _editorMinimizeButton;
    private Button? _editorMaximizeButton;
    private Border? _editorMarkdownToolbar;
    private FrameworkElement? _editorContentElement;
    private Thickness _editorContentOriginalMargin;
    private Action? _enterCurrentNoteEditor;
    private Action? _noteEditorImageAction;

    internal bool IsExpandedEditorMode => _isExpandedEditorMode;
    internal bool IsExpandedEditorSidebarOpen => _expandedEditorHostWindow?.IsSidebarOpen ?? false;
    internal void SetExpandedEditorSidebarOpen(bool open) => _expandedEditorHostWindow?.SetSidebarOpen(open);
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
        _editorSidebarButton.Content = StudioIcons.Create(_editorSidebarButton, StudioIcon.Sidebar);
        _editorSidebarButton.Click += (_, _) =>
            SetExpandedEditorSidebarOpen(!IsExpandedEditorSidebarOpen);

        _editorMinimizeButton = StudioIcons.Button(StudioIcon.Minimize, Strings.Get("EditorMinimize"));
        _editorMinimizeButton.Click += (_, _) =>
        {
            if (_expandedEditorHostWindow != null)
                _expandedEditorHostWindow.WindowState = WindowState.Minimized;
        };
        _editorMaximizeButton = StudioIcons.Button(StudioIcon.Maximize, Strings.Get("EditorMaximizeRestore"));
        _editorMaximizeButton.Click += (_, _) => ToggleExpandedEditorMaximized();

        windowButtons.Children.Add(_editorSidebarButton);
        windowButtons.Children.Add(_editorMinimizeButton);
        windowButtons.Children.Add(_editorMaximizeButton);
        RefreshExpandedEditorTopBarControls();
    }

    private static FrameworkElement CreateExpandedEditorModeIcon(Button owner, bool expanded) =>
        StudioIcons.Create(owner, expanded ? StudioIcon.ReturnToPaper : StudioIcon.Studio,
            16 * TopBarIconScale());

    private void RefreshExpandedEditorTopBarControls()
    {
        if (_editorModeButton == null) return;
        RefreshPaperIconButton();
        if (_runScriptButton != null) _runScriptButton.Content = CreateTopBarRunScriptIcon(_runScriptButton, studio: _isExpandedEditorMode);
        if (_newTodoButton != null) _newTodoButton.Content = CreateTopBarNewTodoIcon(_newTodoButton, studio: _isExpandedEditorMode);
        if (_newNoteButton != null) _newNoteButton.Content = CreateTopBarNewNoteIcon(_newNoteButton, studio: _isExpandedEditorMode);
        if (_windowBindingButton != null)
            _windowBindingButton.Content = CreateTopBarAssociationIcon(_windowBindingButton,
                HasExperimentalWindowTether || (_controller.State.EnableTodoPaperLinks && _controller.IsPaperLinkedToAnyTodo(_paper)),
                studio: _isExpandedEditorMode);
        if (_moreActionsButton != null)
            _moreActionsButton.Content = _isExpandedEditorMode
                ? StudioIcons.Create(_moreActionsButton, StudioIcon.More) : "⋯";
        if (_openMarkdownButton != null)
            _openMarkdownButton.Content = _isExpandedEditorMode
                ? StudioIcons.Create(_openMarkdownButton, StudioIcon.ExternalOpen) : ExternalOpenButtonLabel(_controller.State.ExternalMarkdownExtension);
        _editorModeButton.Content =
            CreateExpandedEditorModeIcon(_editorModeButton, _isExpandedEditorMode);
        _editorModeButton.ToolTip = _isExpandedEditorMode
            ? Strings.Get("EditorReturn") : Strings.Get("EditorOpen");

        if (_editorSidebarButton != null)
        {
            _editorSidebarButton.Visibility = _isExpandedEditorMode
                ? Visibility.Visible : Visibility.Collapsed;
            _editorSidebarButton.ToolTip = IsExpandedEditorSidebarOpen
                ? Strings.Get("EditorSidebarHide") : Strings.Get("EditorSidebarShow");
            _editorSidebarButton.Foreground = IsExpandedEditorSidebarOpen
                ? Theme.ActiveBrush : WeakTextBrush;
        }
        if (_editorMinimizeButton != null)
            _editorMinimizeButton.Visibility = _isExpandedEditorMode
                ? Visibility.Visible : Visibility.Collapsed;
        if (_editorMaximizeButton != null)
        {
            _editorMaximizeButton.Visibility = _isExpandedEditorMode
                ? Visibility.Visible : Visibility.Collapsed;
            _editorMaximizeButton.Content = StudioIcons.Create(_editorMaximizeButton,
                IsExpandedEditorMaximized ? StudioIcon.Restore : StudioIcon.Maximize);
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
        var tools = StudioMarkdownToolbar.Create(
            () => _isExpandedEditorMode && IsCurrentBodyProviderMarkdown ? _noteBox : null,
            () => _enterCurrentNoteEditor?.Invoke(),
            () => _noteEditorImageAction?.Invoke(),
            ExportNoteAsImage);

        _editorMarkdownToolbar = new Border
        {
            Height = ExpandedEditorToolbarHeight,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = tools
            },
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Top
        };
        _editorMarkdownToolbar.SetResourceReference(Border.BackgroundProperty, "TitleBarBrushKey");
        _editorMarkdownToolbar.SetResourceReference(Border.BorderBrushProperty, "TitleBarDividerBrushKey");
        Grid.SetRow(_editorMarkdownToolbar, 1);
        Panel.SetZIndex(_editorMarkdownToolbar, 80);
        _shell.Children.Add(_editorMarkdownToolbar);

        BuildExpandedEditorStatistics();
    }

    internal void RefreshExpandedEditorPresentation()
    {
        if (_editorMarkdownToolbar == null) return;
        var showMarkdownTools = _isExpandedEditorMode &&
            _paper.Type == PaperTypes.Note && IsCurrentBodyProviderMarkdown && _noteBox != null;
        if (_noteBox != null)
        {
            _noteBox.ShowLineNumbers = showMarkdownTools;
            _noteBox.SetResourceReference(MarkdownTextBox.LineNumbersForegroundProperty, "WeakTextBrushKey");
        }
        RefreshExpandedEditorStatistics(showMarkdownTools);
        _editorMarkdownToolbar.Visibility = showMarkdownTools ? Visibility.Visible : Visibility.Collapsed;
        _editorMarkdownToolbar.Margin = new Thickness(
            _isExpandedEditorMode && IsExpandedEditorSidebarOpen ? ExpandedEditorSidebarWidth : 0, 0, 0, 0);
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
                m.Left + (_isExpandedEditorMode && IsExpandedEditorSidebarOpen ? ExpandedEditorSidebarWidth : 0),
                m.Top + (showMarkdownTools ? ExpandedEditorToolbarHeight : 0),
                m.Right,
                m.Bottom + (showMarkdownTools ? ExpandedEditorStatusHeight : 0));
        }
        if (_textZoomIndicator?.Parent is Border zoomHost)
            zoomHost.Margin = new Thickness(0, 0,
                12,
                7 + (showMarkdownTools ? ExpandedEditorStatusHeight : 0));
        RefreshExpandedEditorTopBarControls();
        UpdateTopBarResponsiveLayout();
    }


    internal ExpandedEditorState? SavedExpandedEditorState => _paper.ExpandedEditor;

    internal void MarkExpandedEditorViewDirty()
    {
        if (IsExpandedEditorHosted && !_controller.SuppressDataReloadEditorCommit)
            _controller.MarkDirty();
    }

    internal void CaptureExpandedEditorState(bool markDirty)
    {
        if (_expandedEditorHostWindow is not { IsLoaded: true } host ||
            !_controller.IsRunning || _controller.SuppressDataReloadEditorCommit)
            return;

        // Only window geometry and sidebar visibility are persisted. The live editor
        // already owns its caret/selection; never replay stale document offsets.
        var state = host.CaptureWindowState();
        if (_paper.ExpandedEditor == state) return;
        _paper.ExpandedEditor = state;
        if (markDirty) _controller.MarkDirty();
    }
}
