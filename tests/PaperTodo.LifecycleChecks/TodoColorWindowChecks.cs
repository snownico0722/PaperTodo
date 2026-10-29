using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static class TodoColorWindowChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task Run()
    {
        var state = new AppState
        {
            TelemetryEnabled = false, EnableAnimations = false, UseCapsuleMode = true, UseDeepCapsuleMode = true,
            ExperimentalEdgeCapsuleHoverPreview = true, UsePersistentPowerShellProcess = false, McpEnabled = false,
            Theme = "light", PaperSkin = PaperSkins.Paper, ColorScheme = ColorSchemes.Warm
        };
        var sharedId = state.TodoColorSlots![2].Id;
        var area = SystemParameters.WorkArea;
        for (var i = 0; i < 7; i++) state.Papers.Add(new PaperData
        {
            Id = "color-" + i, Title = "Color " + i, ColorSlotId = i < 4 ? sharedId : null,
            X = area.Left + 40 + i * 25, Y = area.Top + 40 + i * 25, Width = 340, Height = 260,
            IsVisible = true, IsCollapsed = i == 3, CapsuleSide = DeepCapsuleSides.Right,
            Items = [new PaperItem { Id = "task-" + i, Text = "original editable task " + i }, new PaperItem { Text = "done", Done = true }]
        });
        state.Papers.Add(new PaperData { Id = "note", Type = PaperTypes.Note, IsVisible = false, Content = "unchanged" });
        var store = new StateStore();
        store.SaveJsonSync(store.SerializeState(state), 1);
        using var controller = new AppController();
        await controller.StartAsync(createDefaultPaper: false);
        await Idle();
        var papers = controller.State.Papers.Where(paper => paper.Type == PaperTypes.Todo).ToArray();
        var windows = (Dictionary<string, PaperWindow>)Field(controller, "_windows");
        foreach (var paper in papers) windows[paper.Id].EnsureShellBuilt();
        var first = windows[papers[0].Id];
        var editor = Editors(first)["task-0"];
        first.Activate(); editor.Focus();
        editor.Select(editor.Text.Length, 0); editor.SelectedText = " pending";
        editor.Select(2, 5);
        var selection = (editor.SelectionStart, editor.SelectionLength);
        var beforeText = editor.Text;
        Require(editor.CanUndo, "native edit history was not recorded");
        Require(controller.SetTodoColorSlot(papers[0], controller.State.TodoColorSlots![0].Id), "local selection failed");
        await Idle();
        var refreshed = Editors(first)["task-0"];
        Require(refreshed.Text == beforeText && refreshed.SelectionStart == selection.Item1 && refreshed.SelectionLength == selection.Item2 && refreshed.IsKeyboardFocused,
            "changing color lost editor state or keyboard focus");
        Require(refreshed.CanUndo, "changing color lost native undo");
        refreshed.Undo(); Require(!refreshed.Text.EndsWith(" pending"), "native undo failed after recoloring");
        refreshed.Redo(); Require(refreshed.Text == beforeText, "native redo failed after recoloring");
        Require(controller.SetTodoColorSlot(papers[0], sharedId), "returning to shared slot failed");
        var unrelatedColors = papers.Skip(4).Select(paper => Hex(windows[paper.Id].Resources["PaperBrushKey"])).ToArray();
        var custom = controller.State.TodoColorSlots.Single(slot => slot.Id == sharedId).Copy();
        custom.Name = "科研"; custom.Preset = null; custom.PrimaryColor = "#2468AC";
        Require(controller.SaveTodoColorSlot(custom), "shared edit failed");
        await Idle();
        var expected = ThemePalette.ForSlot(custom, false);
        foreach (var paper in papers.Take(4))
        {
            var window = windows[paper.Id];
            Require(Hex(window.Resources["PaperBrushKey"]) == Hex(expected.PaperBrush), "shared window did not update");
            Require(Hex(ThemePalette.For(window).ActiveBrush) == Hex(expected.ActiveBrush), "window scope diverged");
        }
        Require(papers.Skip(4).Select(paper => Hex(windows[paper.Id].Resources["PaperBrushKey"])).SequenceEqual(unrelatedColors), "unrelated windows recolored");
        Require(Hex(Theme.PaperBrush) == Hex(windows[papers[4].Id].Resources["PaperBrushKey"]), "global theme changed");
        var edgeHost = Field(windows[papers[3].Id], "_edgeCapsuleHost");
        var edgeWindow = (Window)edgeHost.GetType().GetProperty("Window", Private)!.GetValue(edgeHost)!;
        Require(Hex(edgeWindow.Resources["PaperBrushKey"]) == Hex(expected.PaperBrush) && Hex(ThemePalette.For(edgeWindow).ActiveBrush) == Hex(expected.ActiveBrush), "edge palette diverged");

        first.Width = PaperLayoutDefaults.MinWidth;
        await Idle();
        var swatch = (Button)Field(first, "_colorSlotButton");
        Require(swatch.IsVisible && ((Button)Field(first, "_moreActionsButton")).IsVisible && ((Button)Field(first, "_closeButton")).IsVisible, "narrow title bar lost required controls");
        typeof(PaperWindow).GetMethod("OpenColorSlotMenu", Private)!.Invoke(first, null);
        await Idle();
        Require(swatch.ContextMenu?.Items.OfType<MenuItem>().Count() == 5, "picker missing global choice or presets");
        Require(swatch.ContextMenu!.Items.OfType<MenuItem>().Count(item => item.IsChecked) == 1, "picker selection is ambiguous");
        Require(Descendants<Border>(swatch.ContextMenu).Any(border => border.Background is SolidColorBrush brush && Hex(brush) == Hex(expected.ActiveBrush)),
            "picker did not render the selected palette swatch");
        swatch.ContextMenu.IsOpen = false;

        var hobby = new TodoColorSlot { Name = "爱好", Preset = null, PrimaryColor = "#8A45B0" };
        Require(controller.SaveTodoColorSlot(hobby) && controller.State.TodoColorSlots.Count == 5, "adding a custom slot failed");
        Require(controller.SetTodoColorSlot(papers[6], hobby.Id), "selecting a new slot failed");
        Require(Hex(windows[papers[6].Id].Resources["PaperBrushKey"]) == Hex(ThemePalette.ForSlot(hobby, false).PaperBrush), "new custom slot not applied");
        Require(controller.RemoveTodoColorSlot(hobby.Id) && papers[6].ColorSlotId == null, "deleting a new slot did not restore inheritance");

        TodoColorSlot? savedDraft = null;
        var dialog = new TodoColorSlotEditor(custom, draft => { savedDraft = draft; return true; }) { Owner = first };
        Exception? dialogFailure = null;
        dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var hex = Descendants<TextBox>(dialog).Single(box => box.Name == "PrimaryColor");
                var save = Descendants<Button>(dialog).Single(button => button.Name == "SavePalette");
                hex.Text = "#GG0000";
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(savedDraft == null && dialog.IsVisible, "invalid draft was accepted");
                hex.Text = "#D08030";
                Require(controller.State.TodoColorSlots.Single(slot => slot.Id == sharedId).PrimaryColor == "#2468AC", "draft changed shared palette before save");
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception ex) { dialogFailure = ex; dialog.Close(); }
        }), DispatcherPriority.ApplicationIdle);
        Require(dialog.ShowDialog() == true && dialogFailure == null && savedDraft?.PrimaryColor == "#D08030", "dialog validation/save failed: " + dialogFailure);
        var cancelDraft = new TodoColorSlotEditor(custom, _ => throw new InvalidOperationException("cancel saved draft")) { Owner = first };
        cancelDraft.Loaded += (_, _) => cancelDraft.Dispatcher.BeginInvoke(new Action(() =>
        {
            Descendants<TextBox>(cancelDraft).Single(box => box.Name == "PrimaryColor").Text = "#FF0000";
            cancelDraft.Close();
        }), DispatcherPriority.ApplicationIdle);
        cancelDraft.ShowDialog();
        Require(custom.PrimaryColor == "#2468AC", "cancel mutated original slot");

        controller.State.Theme = "dark"; Theme.Invalidate();
        foreach (var paper in papers) windows[paper.Id].UpdateTheme();
        var dark = ThemePalette.ForSlot(custom, true);
        Require(Hex(first.Resources["PaperBrushKey"]) == Hex(dark.PaperBrush), "dark mode ignored local slot");
        Require(controller.RemoveTodoColorSlot(sharedId), "slot deletion failed");
        Require(papers.Take(4).All(paper => paper.ColorSlotId == null), "deletion left slot references");
        Require(Hex(first.Resources["PaperBrushKey"]) == Hex(Theme.PaperBrush), "deleted slot did not inherit global");
        var reloaded = store.Load();
        Require(reloaded.Papers.Take(4).All(paper => paper.ColorSlotId == null) && reloaded.TodoColorSlots?.Count == 3, "restart lost committed selection");
        controller.Exit();
        Console.WriteLine("PASS todo colors: seven windows, shared edits, isolated surfaces, editor history, narrow picker, draft validation/cancel and restart");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var descendant in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return descendant;
    }
    private static object Field(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static Dictionary<string, TodoTextBox> Editors(PaperWindow window) => (Dictionary<string, TodoTextBox>)Field(window, "_todoEditors");
    private static string Hex(object brush) { var c = ((SolidColorBrush)brush).Color; return $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle).Task;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
