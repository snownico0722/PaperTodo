using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    // Presentation only: folding never changes Order, Done, or the saved data model.
    private bool _completedTodoSectionCollapsed;
    private readonly HashSet<string> _hiddenCompletedTodoItemIds = new(StringComparer.Ordinal);
    private Border? _completedTodoSectionHeader;
    private Border? _completedTodoSectionChevron;
    private TextBlock? _completedTodoSectionArrow;
    private TextBlock? _completedTodoSectionLabel;

    private void ResetCompletedTodoSectionHeader()
    {
        // The full row rebuild can follow a typography/skin change. Recreate the
        // section header with the same current metrics as the new rows.
        _completedTodoSectionHeader = null;
        _completedTodoSectionChevron = null;
        _completedTodoSectionArrow = null;
        _completedTodoSectionLabel = null;
    }

    private void RemoveCompletedTodoSectionHeaderFromPanel()
    {
        if (_todoPanel != null && _completedTodoSectionHeader != null)
            _todoPanel.Children.Remove(_completedTodoSectionHeader);
    }

    private void SyncCompletedTodoSection()
    {
        if (_todoPanel == null) return;

        RemoveCompletedTodoSectionHeaderFromPanel();
        var ordered = OrderedItems().ToList();
        var firstDone = ordered.FindIndex(item => item.Done);
        // If a different ordering path interleaved completed and active items,
        // do not silently sort a manually arranged list for the sake of folding.
        var hasCompletedGroup = _controller.State.AutoMoveCompletedTodosToBottom &&
            !_controller.State.AutoClearCompletedTodos &&
            firstDone >= 0 &&
            ordered.Skip(firstDone).All(item => item.Done);

        if (!hasCompletedGroup)
        {
            _completedTodoSectionCollapsed = false;
        }
        else
        {
            EnsureCompletedTodoSectionHeader();
            _completedTodoSectionLabel!.Text = Strings.Format(
                "TodoCompletedSectionCount", ordered.Count - firstDone);
            _todoPanel.Children.Insert(firstDone, _completedTodoSectionHeader!);
        }

        var completedIds = ordered.Where(item => item.Done)
            .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        _hiddenCompletedTodoItemIds.Clear();
        if (hasCompletedGroup && _completedTodoSectionCollapsed)
            _hiddenCompletedTodoItemIds.UnionWith(completedIds);
        foreach (var row in _todoRows)
        {
            row.Visibility = row.Tag is string id &&
                             _hiddenCompletedTodoItemIds.Contains(id)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        UpdateCompletedTodoSectionVisuals();
    }

    private void EnsureCompletedTodoSectionHeader()
    {
        if (_completedTodoSectionHeader != null) return;

        var arrow = new TextBlock
        {
            Text = "▾",
            FontSize = AppTypography.Scale(13),
            FontFamily = AppTypography.SymbolFontFamily,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var chevron = new Border
        {
            Width = AppTypography.Scale(19),
            Height = AppTypography.Scale(19),
            CornerRadius = new CornerRadius(Theme.IsPixelSkin ? 0 : RadiusSmall),
            Background = Brushes.Transparent,
            Child = arrow
        };
        var label = new TextBlock
        {
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(11),
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(AppTypography.Scale(4), 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(chevron);
        content.Children.Add(label);
        var header = new Border
        {
            Margin = new Thickness(0, 4, 0, 2),
            Padding = new Thickness(AppTypography.Scale(4), AppTypography.Scale(3),
                                    AppTypography.Scale(4), AppTypography.Scale(3)),
            CornerRadius = new CornerRadius(Theme.IsPixelSkin ? 0 : RadiusControl),
            Cursor = Cursors.Hand,
            Focusable = false,
            Child = content
        };
        header.MouseEnter += (_, _) => UpdateCompletedTodoSectionVisuals();
        header.MouseLeave += (_, _) => UpdateCompletedTodoSectionVisuals();
        header.MouseLeftButtonUp += (_, e) =>
        {
            _completedTodoSectionCollapsed = !_completedTodoSectionCollapsed;
            SyncCompletedTodoSection();
            RefreshTodoFindCues();
            if (IsBuiltInFindOpen) ApplyCurrentFindMatch();
            e.Handled = true;
        };
        _completedTodoSectionHeader = header;
        _completedTodoSectionChevron = chevron;
        _completedTodoSectionArrow = arrow;
        _completedTodoSectionLabel = label;
    }

    private bool IsCompletedTodoItemHidden(string itemId) =>
        _hiddenCompletedTodoItemIds.Contains(itemId);

    private void UpdateCompletedTodoSectionVisuals()
    {
        if (_completedTodoSectionHeader == null ||
            _completedTodoSectionChevron == null ||
            _completedTodoSectionArrow == null ||
            _completedTodoSectionLabel == null)
            return;

        var highlighted = _completedTodoSectionCollapsed && _findHiddenCompletedTodoMatch;
        _completedTodoSectionHeader.ToolTip = Strings.Get(
            _completedTodoSectionCollapsed
                ? "TodoCompletedSectionExpand"
                : "TodoCompletedSectionCollapse");
        _completedTodoSectionArrow.Text = _completedTodoSectionCollapsed ? "▸" : "▾";
        _completedTodoSectionHeader.Background = highlighted
            ? Theme.Tint((byte)(Theme.IsDark ? 42 : 28))
            : _completedTodoSectionHeader.IsMouseOver ? HoverBrush : Brushes.Transparent;
        _completedTodoSectionChevron.Background = highlighted
            ? Theme.Tint((byte)(Theme.IsDark ? 96 : 78)) : Brushes.Transparent;
        _completedTodoSectionArrow.Foreground = highlighted ? TextBrush : WeakTextBrush;
        _completedTodoSectionLabel.Foreground = highlighted ? TextBrush : WeakTextBrush;
    }
}
