using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

internal sealed partial class ExpandedEditorWindow
{
    internal const double SidebarWidth = 228;
    private readonly ObservableCollection<PaperListEntry> _paperEntries = [];
    private Border _editorPaperSidebar = null!;
    private ListBox _editorPaperList = null!;
    internal bool IsSidebarOpen { get; private set; }

    private sealed record PaperListEntry(string Id, string Title, string Kind, string ToolTip, bool IsCurrent);

    private void BuildPaperSidebar()
    {
        var buttonStyle = new Style(typeof(Button));
        buttonStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 7, 9, 7)));
        buttonStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        buttonStyle.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        buttonStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        var surface = new FrameworkElementFactory(typeof(Border), "ItemSurface");
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        surface.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        surface.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        surface.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("HoverBrushKey"), "ItemSurface"));
        template.Triggers.Add(hover);
        buttonStyle.Setters.Add(new Setter(Control.TemplateProperty, template));
        var current = new DataTrigger { Binding = new Binding(nameof(PaperListEntry.IsCurrent)), Value = true };
        current.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("HoverBrushKey")));
        current.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        buttonStyle.Triggers.Add(current);

        var button = new FrameworkElementFactory(typeof(Button));
        button.SetValue(FrameworkElement.StyleProperty, buttonStyle);
        button.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 1, 4, 1));
        button.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch);
        button.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(PaperListEntry.ToolTip)));
        button.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is FrameworkElement { DataContext: PaperListEntry entry } && _current is { } paper)
                _controller.SwitchExpandedEditorPaper(paper, entry.Id);
        }));
        var labels = new FrameworkElementFactory(typeof(StackPanel));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(PaperListEntry.Title)));
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushKey");
        labels.AppendChild(title);
        var kind = new FrameworkElementFactory(typeof(TextBlock));
        kind.SetBinding(TextBlock.TextProperty, new Binding(nameof(PaperListEntry.Kind)));
        kind.SetResourceReference(TextBlock.FontSizeProperty, "EditorListKindSize");
        kind.SetValue(TextBlock.FontWeightProperty, FontWeights.Normal);
        kind.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        labels.AppendChild(kind);
        button.AppendChild(labels);

        // Buttons provide the row interaction. The ListBox only supplies a recycling
        // viewport; it must not add a second selected-row appearance or focus target.
        var containerStyle = new Style(typeof(ListBoxItem));
        containerStyle.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        containerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        containerStyle.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem))
        {
            VisualTree = new FrameworkElementFactory(typeof(ContentPresenter))
        }));
        _editorPaperList = new ListBox
        {
            ItemsSource = _paperEntries,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            ItemTemplate = new DataTemplate { VisualTree = button },
            ItemContainerStyle = containerStyle,
            ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)))
        };
        ScrollViewer.SetCanContentScroll(_editorPaperList, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_editorPaperList, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(_editorPaperList, true);
        VirtualizingPanel.SetVirtualizationMode(_editorPaperList, VirtualizationMode.Recycling);
        var header = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(12, 12, 8, 8)
        };
        header.SetResourceReference(TextBlock.TextProperty, "EditorListHeading");
        header.SetResourceReference(TextBlock.FontSizeProperty, "EditorListTitleSize");
        header.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushKey");
        var contents = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        contents.Children.Add(header);
        contents.Children.Add(_editorPaperList);
        _editorPaperSidebar = new Border
        {
            Width = SidebarWidth,
            HorizontalAlignment = HorizontalAlignment.Right,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = contents
        };
        _editorPaperSidebar.SetResourceReference(Border.BackgroundProperty, "PaperSurfaceBrushKey");
        _editorPaperSidebar.SetResourceReference(Border.BorderBrushProperty, "TitleBarDividerBrushKey");
        Grid.SetRow(_editorPaperSidebar, 1);
        Panel.SetZIndex(_editorPaperSidebar, 90);
    }

    // The host owns this one sidebar for its entire lifetime. Mount it in the
    // borrowed shell's body row to reuse the existing top-bar/body layout exactly.
    internal void AttachPaperSidebar(Grid shell)
    {
        shell.Children.Add(_editorPaperSidebar);
        _editorPaperSidebar.Visibility = IsSidebarOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void DetachPaperSidebar(Grid shell) => shell.Children.Remove(_editorPaperSidebar);

    internal void SetSidebarOpen(bool open)
    {
        if (IsSidebarOpen == open) return;
        IsSidebarOpen = open;
        _editorPaperSidebar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open) RefreshPaperList();
        _current?.RefreshExpandedEditorPresentation();
        _current?.MarkExpandedEditorViewDirty();
    }

    internal void RefreshPaperList()
    {
        if (!IsSidebarOpen) return;
        _editorPaperSidebar.Resources["EditorListHeading"] = Strings.Get("EditorAllPapers");
        _editorPaperSidebar.Resources["EditorListTitleSize"] = AppTypography.Scale(12);
        _editorPaperSidebar.Resources["EditorListKindSize"] = AppTypography.Scale(10);
        var index = 0;
        foreach (var paper in _controller.State.Papers)
        {
            var entry = new PaperListEntry(paper.Id, _controller.PaperDisplayTitle(paper),
                Strings.Get(paper.Type == PaperTypes.Todo ? "EditorTodo" :
                    paper.Type == PaperTypes.Note ? "EditorNote" : "EditorPaper"),
                _controller.PaperTitleText(paper), paper.Id == CurrentPaperId);
            if (index >= _paperEntries.Count || _paperEntries[index].Id != paper.Id)
            {
                var found = -1;
                for (var i = index + 1; i < _paperEntries.Count; i++)
                    if (_paperEntries[i].Id == paper.Id) { found = i; break; }
                if (found >= 0) _paperEntries.Move(found, index);
                else _paperEntries.Insert(index, entry);
            }
            if (_paperEntries[index] != entry) _paperEntries[index] = entry;
            index++;
        }
        while (_paperEntries.Count > index) _paperEntries.RemoveAt(_paperEntries.Count - 1);
    }
}
