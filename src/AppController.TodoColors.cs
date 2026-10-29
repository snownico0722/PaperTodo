using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class AppController
{
    internal bool SetTodoColorSlot(PaperData paper, string? id)
    {
        if (paper.Type != PaperTypes.Todo || !State.Papers.Contains(paper)) return false;
        if (id != null && !ValidColorSlots().Any(slot => slot.Id == id)) return false;
        if (paper.ColorSlotId == id) return true;
        return CommitTodoColorChange(ValidColorSlots(), new Dictionary<PaperData, string?> { [paper] = id });
    }

    private List<TodoColorSlot> ValidColorSlots() =>
        State.TodoColorSlots?.Where(TodoColorSlots.IsValid).DistinctBy(slot => slot.Id).Select(slot => slot.Copy()).ToList() ?? [];

    private bool CommitTodoColorChange(List<TodoColorSlot> slots, IReadOnlyDictionary<PaperData, string?> assignments)
    {
        if (!IsRunning) return false;
        var previous = ValidColorSlots().ToDictionary(slot => slot.Id, StringComparer.Ordinal);
        var changed = slots.Where(slot => !previous.TryGetValue(slot.Id, out var old) ||
            old.Name != slot.Name || old.Preset != slot.Preset || old.PrimaryColor != slot.PrimaryColor)
            .Select(slot => slot.Id).Concat(previous.Keys.Except(slots.Select(slot => slot.Id))).ToHashSet(StringComparer.Ordinal);
        var affected = State.Papers.Where(paper => assignments.ContainsKey(paper) ||
            (paper.ColorSlotId != null && changed.Contains(paper.ColorSlotId))).Select(paper => paper.Id).ToHashSet(StringComparer.Ordinal);
        var change = TodoColorSlots.Change(State, slots, assignments, () =>
        {
            foreach (var window in _windows.Values.ToArray())
                if (affected.Contains(window.PaperId)) window.UpdateTheme();
            RefreshSettingsWindowContent();
        });
        try
        {
            PaperSettingsService.CommitChange(change, TryCommitExternalMutation, RunExternalPostCommitUi);
            return true;
        }
        catch (PaperSettingsException) { return false; } // Persistence owner already reports the failure.
    }

    private UIElement CreateTodoColorSlotSettings()
    {
        var panel = new StackPanel();
        panel.Children.Add(SettingsFieldLabel(Strings.Get("TodoColorsTitle")));
        panel.Children.Add(new TextBlock
        {
            Text = Strings.Get("TodoColorsHint"), TextWrapping = TextWrapping.Wrap,
            Foreground = Theme.WeakTextBrush, Margin = new Thickness(0, 2, 0, 8)
        });
        foreach (var slot in ValidColorSlots())
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var palette = ThemePalette.ForSlot(slot, Theme.IsDark);
            row.Children.Add(new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(4),
                Background = palette.ActiveBrush, BorderBrush = palette.PaperBorderBrush,
                BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0)
            });
            var name = new TextBlock
            {
                Text = TodoColorSlots.DisplayName(slot), Foreground = Theme.TextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 1); row.Children.Add(name);
            var edit = SettingsTextButton(Strings.Get("TodoColorsEdit"));
            edit.Click += (_, _) => EditTodoColorSlot(slot);
            Grid.SetColumn(edit, 2); row.Children.Add(edit);
            var delete = SettingsTextButton(Strings.Get("MenuDelete"));
            delete.Click += (_, _) => DeleteTodoColorSlot(slot);
            Grid.SetColumn(delete, 3); row.Children.Add(delete);
            panel.Children.Add(row);
        }
        var add = SettingsTextButton(Strings.Get("TodoColorsAdd"));
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 8, 0, 0);
        add.Click += (_, _) => EditTodoColorSlot(new TodoColorSlot
        {
            Name = Strings.Get("TodoColorsNew"), Preset = null, PrimaryColor = "#588260"
        });
        panel.Children.Add(add);
        return AdvancedSettingsBlock(panel);
    }

    private void EditTodoColorSlot(TodoColorSlot slot)
    {
        var editor = new TodoColorSlotEditor(slot, SaveTodoColorSlot);
        if (_settingsWindow?.IsVisible == true) editor.Owner = _settingsWindow;
        editor.ShowDialog();
    }

    internal bool SaveTodoColorSlot(TodoColorSlot value)
    {
        if (!TodoColorSlots.IsValid(value)) return false;
        var slots = ValidColorSlots();
        var index = slots.FindIndex(candidate => candidate.Id == value.Id);
        if (index < 0) slots.Add(value.Copy()); else slots[index] = value.Copy();
        return CommitTodoColorChange(slots, new Dictionary<PaperData, string?>());
    }

    internal bool RemoveTodoColorSlot(string id)
    {
        var assignments = State.Papers.Where(paper => paper.Type == PaperTypes.Todo && paper.ColorSlotId == id)
            .ToDictionary(paper => paper, _ => (string?)null);
        return CommitTodoColorChange(ValidColorSlots().Where(slot => slot.Id != id).ToList(), assignments);
    }

    private void DeleteTodoColorSlot(TodoColorSlot slot)
    {
        var affected = State.Papers.Where(paper => paper.Type == PaperTypes.Todo && paper.ColorSlotId == slot.Id)
            .ToDictionary(paper => paper, _ => (string?)null);
        var message = Strings.Format("TodoColorsDeleteQuestion", TodoColorSlots.DisplayName(slot), affected.Count);
        var result = _settingsWindow?.IsVisible == true
            ? MessageBox.Show(_settingsWindow, message, Strings.Get("TodoColorsTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question)
            : MessageBox.Show(message, Strings.Get("TodoColorsTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.OK)
            RemoveTodoColorSlot(slot.Id);
    }
}
