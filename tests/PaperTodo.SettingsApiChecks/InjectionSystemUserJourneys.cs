using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PaperTodo;

internal static partial class InjectionSystemRunner
{
    private static void PrepareUserJourney(AppState state, string scenario, bool edge)
    {
        state.Papers[0].IsVisible = scenario is "user-editor" or "user-preview";
        var todo = state.Papers[1];
        todo.X = 70; todo.Y = 90; todo.Width = 420; todo.Height = 320;
        todo.Items = Enumerable.Range(0, scenario == "user-drag" ? 40 : scenario == "user-preview" ? 5 : 1)
            .Select(i => new PaperItem { Id = "task-" + i, Text = "original task " + i, Order = i }).ToList();
        state.ShowTodoBottomBar = true;
        state.ExperimentalEdgeCapsuleHoverPreview = scenario == "user-preview";
        if (scenario != "user-preview") return;
        // Exercise both physical edges, not two identical right-edge fixtures.
        state.UseDeepCapsuleMode = true;
        state.UseCapsuleCollapseAll = false;
        state.ExperimentalEdgeCapsuleHoverIntent = false;
        state.Papers[0].Content = "# Native preview\n\n" + string.Join("\n\n", Enumerable.Range(0, 10).Select(i => "Preview paragraph " + i));
        foreach (var paper in state.Papers)
        {
            paper.IsCollapsed = true;
            paper.CapsuleSide = edge ? DeepCapsuleSides.Right : DeepCapsuleSides.Left;
            paper.CapsuleMonitorDeviceName = "";
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "preview-stage.txt"), "0");
    }

    private static async Task<bool> RunUserJourney(AppController c, string scenario)
    {
        JourneyLog("BEGIN " + scenario);
        switch (scenario)
        {
            case "user-editor": await UserEditor(c); break;
            case "user-paste": await UserPaste(c); break;
            case "user-drag": await UserDrag(c); break;
            case "user-preview": return await UserPreview(c);
            default: throw new ArgumentException(scenario);
        }
        JourneyLog($"PASS {scenario} assertions={_assertions}");
        return false;
    }

    private static async Task UserEditor(AppController c)
    {
        var window = Windows(c)["note"];
        var box = Field<MarkdownPaperBodySession>(window, "_markdownBodySession").NoteBox!;
        var originalFont = box.FontSize;
        var document = box.Document;
        using var view = new GoodLockView(c);
        await ExternalJourneyApply(view, ("appearance.fontScale", 1.15), ("app.saveIdleMs", 200d));
        await JourneyInput.Click(box);
        await UntilJourney(() => box.IsKeyboardFocusWithin, "Native editor receives keyboard focus");
        Clipboard.SetText("实际键盘粘贴 / user journey\nsecond line");
        JourneyInput.Chord(0x11, 0x41); JourneyInput.Chord(0x11, 0x56);
        await UntilJourney(() => box.Text.Contains("user journey"), "Ctrl+V changes the real note");
        Check(ReferenceEquals(document, box.Document) && Math.Abs(box.FontSize - originalFont * 1.15) < .11,
            "Applied font did not reach the editable surface.");
        var edited = box.Text;
        await UntilJourney(() => PersistedNoteEquals(edited), "Changed idle-save interval actually saves native keyboard input");
        CaptureJourney(window, "editor-injected");
        await ExternalJourneyApply(view);
        await JourneyInput.Click(box);
        Check(Math.Abs(box.FontSize - originalFont) < .11 && box.Text == edited, "Removing overrides changed text or failed to restore font.");
        JourneyInput.Chord(0x11, 0x5A);
        await UntilJourney(() => box.Text == "original note", "Native Ctrl+Z survives config application and removal");
        JourneyInput.Chord(0x11, 0x59);
        await UntilJourney(() => box.Text == edited, "Native Ctrl+Y restores the user edit");
        await UntilJourney(() => PersistedNoteEquals(edited), "Default save path still works after removal");
    }

    private static bool PersistedNoteEquals(string expected)
    {
        try
        {
            var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return state?.Papers.Single(p => p.Id == "note").Content == expected;
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
    }

    private static async Task UserPaste(AppController c)
    {
        var window = Windows(c)["todo"];
        var paper = c.State.Papers.Single(p => p.Id == "todo");
        using var view = new GoodLockView(c);
        foreach (var budget in new[] { 2, 3, 0 })
        {
            if (budget == 0) await ExternalJourneyApply(view);
            else await ExternalJourneyApply(view, ("todo.paste.maxItems", budget));
            var editor = Field<Dictionary<string, TodoTextBox>>(window, "_todoEditors")[paper.Items[0].Id];
            await JourneyInput.Click(editor);
            Clipboard.SetText("one\ntwo\nthree\nfour");
            JourneyInput.Chord(0x11, 0x41); JourneyInput.Chord(0x11, 0x56);
            var expected = budget == 0 ? 4 : budget;
            await UntilJourney(() => paper.Items.Count == expected, "Native multi-line paste uses budget " + expected);
            Check(paper.Items.OrderBy(p => p.Order).Select(p => p.Text).SequenceEqual(new[] { "one", "two", "three", "four" }.Take(expected)),
                "Paste count changed but actual item text/order is wrong.");
            Check(Field<List<Border>>(window, "_todoRows").Count == expected, "Visible todo rows disagree with pasted items.");
            CaptureJourney(window, "paste-" + expected);
            JourneyInput.Chord(0x11, 0x5A);
            await UntilJourney(() => paper.Items.Count == 1 && paper.Items[0].Text == "original task 0",
                "One native undo restores the entire multi-line paste");
            JourneyLog("PASTE budget=" + expected + " native-undo=pass");
        }
    }

    private static async Task UserDrag(AppController c)
    {
        var window = Windows(c)["todo"];
        var paper = c.State.Papers.Single(p => p.Id == "todo");
        var originals = paper.Items.ToDictionary(p => p.Id, p => p.Text);
        using var view = new GoodLockView(c);
        foreach (var phase in new[] { "baseline", "disabled", "restored" })
        {
            if (phase == "disabled") await ExternalJourneyApply(view, ("todo.drag.autoScroll", false));
            else await ExternalJourneyApply(view);
            window.Activate();
            var panel = Field<StackPanel>(window, "_todoPanel");
            DependencyObject? parent = panel;
            while (parent is not ScrollViewer && parent != null) parent = VisualTreeHelper.GetParent(parent);
            var scroll = (ScrollViewer)parent!;
            scroll.ScrollToTop(); await Settle();
            Check(scroll.ScrollableHeight > 100, "Drag fixture must overflow the viewport.");
            var row = Field<List<Border>>(window, "_todoRows")[0];
            var draggedId = (string)row.Tag;
            var handle = JourneyDescendants(row).OfType<Border>().Single(b => b.Cursor == Cursors.SizeAll);
            await JourneyInput.Aim(handle);
            var down = handle.PointToScreen(new Point(handle.ActualWidth / 2, handle.ActualHeight / 2));
            try
            {
                JourneyInput.Button(true);
                await Task.Delay(50);
                JourneyInput.Move(new Point(down.X + 12, down.Y + 14));
                await UntilJourney(() => ReferenceEquals(Mouse.Captured, window), "Native drag owns mouse capture");
                var bottom = scroll.PointToScreen(new Point(scroll.ActualWidth / 2, scroll.ActualHeight - 8));
                JourneyInput.Move(bottom);
                if (phase == "disabled")
                {
                    await Task.Delay(450);
                    Check(scroll.VerticalOffset < 1 && ReferenceEquals(Mouse.Captured, window),
                        "Disabled auto-scroll moved the viewport or cancelled the drag.");
                }
                else await UntilJourney(() => scroll.VerticalOffset > 25, phase + " native drag actually scrolls the viewport");
                JourneyLog($"DRAG {phase} offset={scroll.VerticalOffset:F1}");
                JourneyInput.Button(false);
                await UntilJourney(() => !ReferenceEquals(Mouse.Captured, window), "Dropping releases capture");
                Check(paper.Items.Count == originals.Count && paper.Items.All(p => originals.GetValueOrDefault(p.Id) == p.Text),
                    "Drag lost an item or changed its content.");
                Check(paper.Items.OrderBy(p => p.Order).First().Id != draggedId, "Physical drop did not reorder the dragged task.");
            }
            finally { JourneyInput.Release(); }
        }
    }
}
