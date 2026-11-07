using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PaperTodo;

internal sealed partial class ExpandedEditorWindow
{
    private Grid? _emptyShell;
    private StackPanel? _emptyActions;
    private Button? _emptyMaximize;

    private void ShowEmptyStudio()
    {
        _emptyShell = new Grid();
        _emptyShell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        _emptyShell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new DockPanel { Margin = new Thickness(10, 4, 8, 4), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        void Add(StudioIcon icon, string key, Action action)
        {
            var button = StudioIcons.Button(icon, Strings.Get(key));
            button.Click += (_, _) => action();
            buttons.Children.Add(button);
            if (icon == StudioIcon.Maximize) _emptyMaximize = button;
        }
        Add(StudioIcon.Sidebar, "EditorSidebarHide", () => SetSidebarOpen(!IsSidebarOpen));
        Add(StudioIcon.Settings, "SettingsTitleText", _controller.OpenSettingsWindow);
        Add(StudioIcon.Minimize, "EditorMinimize", () => WindowState = WindowState.Minimized);
        Add(StudioIcon.Maximize, "EditorMaximizeRestore", ToggleMaximized);
        Add(StudioIcon.Close, "EditorClose", Close);
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.TextProperty, "StudioTitle");
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushKey");
        var dragArea = new Border { Background = System.Windows.Media.Brushes.Transparent, Child = title };
        dragArea.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) ToggleMaximized();
            else if (e.ButtonState == MouseButtonState.Pressed) DragMove();
            e.Handled = true;
        };
        header.Children.Add(dragArea);
        _emptyShell.Children.Add(header);
        _emptyActions = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        void AddNew(StudioIcon icon, string key, string type)
        {
            var button = StudioIcons.Button(icon, Strings.Get(key));
            button.Width = 176;
            button.Height = 42;
            button.Margin = new Thickness(0, 6, 0, 6);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(StudioIcons.Create(button, icon, 19));
            var label = new TextBlock { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.TextProperty, key);
            content.Children.Add(label);
            button.Content = content;
            button.Click += (_, _) => _controller.CreateStudioPaper(this, type);
            _emptyActions.Children.Add(button);
        }
        AddNew(StudioIcon.NewNote, "TrayNewNote", PaperTypes.Note);
        AddNew(StudioIcon.NewTodo, "TrayNewTodo", PaperTypes.Todo);
        Grid.SetRow(_emptyActions, 1);
        _emptyShell.Children.Add(_emptyActions);
        _body.Child = _emptyShell;
        AttachPaperSidebar(_emptyShell);
        RefreshEmptyStudio();
        RefreshPaperList();
        RefreshEmptyStudioLayout();
        StateChanged += OnEmptyStudioStateChanged;
    }

    private void OnEmptyStudioStateChanged(object? sender, EventArgs e)
    {
        if (_emptyMaximize != null)
            _emptyMaximize.Content = StudioIcons.Create(_emptyMaximize,
                WindowState == WindowState.Maximized ? StudioIcon.Restore : StudioIcon.Maximize);
    }

    internal void RefreshEmptyStudio()
    {
        if (_emptyShell == null) return;
        Title = StudioTitle(null);
        Background = _frame.Background = Theme.PaperBrush;
        Resources["TextBrushKey"] = Theme.TextBrush;
        Resources["WeakTextBrushKey"] = Theme.WeakTextBrush;
        Resources["HoverBrushKey"] = Theme.HoverBrush;
        Resources["PaperSurfaceBrushKey"] = Theme.PaperBrush;
        Resources["TitleBarDividerBrushKey"] = Theme.PaperBorderBrush;
        foreach (var key in new[] { "StudioTitle", "TrayNewNote", "TrayNewTodo" })
            Resources[key] = Strings.Get(key);
        FontFamily = AppTypography.UiFontFamily;
        FontSize = AppTypography.Scale(13);
    }

    private void RefreshEmptyStudioLayout()
    {
        if (_emptyActions != null)
            _emptyActions.Margin = new Thickness(IsSidebarOpen ? SidebarWidth : 0, 0, 0, 0);
    }

    private void DetachEmptyStudio()
    {
        if (_emptyShell == null) return;
        StateChanged -= OnEmptyStudioStateChanged;
        DetachPaperSidebar(_emptyShell);
        _body.Child = null;
        _emptyShell = null;
        _emptyActions = null;
        _emptyMaximize = null;
    }
}
