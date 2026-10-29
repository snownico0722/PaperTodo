using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private ThemePalette? _appearance;
    private ThemePalette Appearance => _appearance ??= ThemePalette.ForPaper(_controller.State, _paper);
    private Button? _colorSlotButton;
    private Border? _colorSlotSwatch;
    private readonly Dictionary<string, Action> _todoColorRefreshers = new(StringComparer.Ordinal);

    private void AddColorSlotButton(Panel buttons)
    {
        if (_paper.Type != PaperTypes.Todo) return;
        _colorSlotButton = IconButton("", Strings.Get("TodoColorsChoose"));
        _colorSlotButton.Width = AppTypography.Scale(23);
        _colorSlotSwatch = new Border
        {
            Width = AppTypography.Scale(13), Height = AppTypography.Scale(13),
            BorderThickness = new Thickness(1), IsHitTestVisible = false
        };
        _colorSlotButton.Content = _colorSlotSwatch;
        AutomationProperties.SetName(_colorSlotButton, Strings.Get("TodoColorsChoose"));
        _colorSlotButton.Click += (_, _) => OpenColorSlotMenu();
        buttons.Children.Add(_colorSlotButton);
        RefreshColorSlotButton();
    }

    private void RefreshColorSlotButton()
    {
        if (_colorSlotButton == null || _colorSlotSwatch == null) return;
        _colorSlotButton.Width = AppTypography.Scale(23);
        _colorSlotSwatch.Width = _colorSlotSwatch.Height = AppTypography.Scale(13);
        _colorSlotSwatch.Background = Appearance.ActiveBrush;
        _colorSlotSwatch.BorderBrush = Appearance.PaperBorderBrush;
        _colorSlotSwatch.CornerRadius = new CornerRadius(Theme.IsPixelSkin ? 0 : AppTypography.Scale(3));
        _colorSlotButton.ToolTip = Strings.Get("TodoColorsChoose") + ": " +
            (TodoColorSlots.Resolve(_controller.State, _paper) is { } slot
                ? TodoColorSlots.DisplayName(slot) : Strings.Get("TodoColorsGlobal"));
    }

    private void OpenColorSlotMenu()
    {
        if (_colorSlotButton == null || _paper.IsCollapsed) return;
        var menu = CreateContextMenu();
        menu.Template = BuildColorSlotMenuTemplate();
        menu.PlacementTarget = _colorSlotButton;
        menu.Placement = PlacementMode.Bottom;
        var selected = TodoColorSlots.Resolve(_controller.State, _paper)?.Id;
        void Add(string? id, string name, ThemePalette colors)
        {
            // The shared compact menu template renders Header, not MenuItem.Icon.
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new Border
            {
                Width = AppTypography.Scale(13), Height = AppTypography.Scale(13),
                CornerRadius = new CornerRadius(AppTypography.Scale(3)),
                Background = colors.ActiveBrush, BorderBrush = colors.PaperBorderBrush,
                BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0)
            });
            header.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem
            {
                Header = header, IsCheckable = true, IsChecked = selected == id
            };
            AutomationProperties.SetName(item, name);
            item.Click += (_, _) => _controller.SetTodoColorSlot(_paper, id);
            menu.Items.Add(item);
        }
        Add(null, Strings.Get("TodoColorsGlobal"), Theme.Global);
        foreach (var slot in _controller.State.TodoColorSlots?.Where(TodoColorSlots.IsValid).DistinctBy(slot => slot.Id) ?? [])
            Add(slot.Id, TodoColorSlots.DisplayName(slot), ThemePalette.ForSlot(slot, Theme.IsDark));
        _colorSlotButton.ContextMenu = menu;
        menu.Closed += (_, _) => _colorSlotButton.ContextMenu = null;
        BeginPaperContextMenuOpening();
        menu.IsOpen = true;
    }

    private static ControlTemplate BuildColorSlotMenuTemplate()
    {
        var border = new FrameworkElementFactory(typeof(SkinBorder));
        border.SetValue(SkinBorder.IsMenuProperty, true);
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(RadiusBlock));
        border.SetValue(Border.PaddingProperty, new Thickness(4));
        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroll.SetValue(FrameworkElement.MaxHeightProperty, Math.Max(64, Math.Min(420, SystemParameters.WorkArea.Height - 96)));
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        border.AppendChild(scroll);
        return new ControlTemplate(typeof(ContextMenu)) { VisualTree = border };
    }

    // Refresh presentation only: never detach a live TodoTextBox or touch its history.
    private void RefreshTodoColors()
    {
        foreach (var refresh in _todoColorRefreshers.Values) refresh();
        foreach (var id in _linkedPaperTitleRefreshers.Keys.ToArray()) RefreshLinkedPaperTitle(id);
        ShowAppendAreaAsTrashBin(_todoDrag != null, _appendArea?.IsMouseOver == true);
        if (_capsuleIconText != null) _capsuleIconText.Foreground = BrightWeakTextBrush;
        if (_capsuleLabelText != null) _capsuleLabelText.Foreground = WeakTextBrush;
        if (_capsuleCloseGlyph != null) _capsuleCloseGlyph.Foreground = WeakTextBrush;
        RefreshTodoFindCues();
        InvalidateEdgeCapsulePreviewContent();
    }
}
