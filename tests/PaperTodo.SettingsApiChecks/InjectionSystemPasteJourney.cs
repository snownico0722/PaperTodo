using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PaperTodo;

internal static partial class InjectionSystemRunner
{
    private static async Task UserPaste(AppController c)
    {
        var window = Windows(c)["todo"];
        var paper = c.State.Papers.Single(p => p.Id == "todo");
        using var view = new GoodLockView(c);
        // Include an unmodified baseline before any override, as well as removal afterwards.
        foreach (var budget in new[] { 0, 2, 3, 1, 0 })
        {
            if (budget == 0) await ExternalJourneyApply(view);
            else await ExternalJourneyApply(view, ("todo.paste.maxItems", budget));
            var editors = Field<Dictionary<string, TodoTextBox>>(window, "_todoEditors");
            await JourneyInput.Click(editors[paper.Items[0].Id]);
            Clipboard.SetText("one\ntwo\nthree\nfour");
            JourneyInput.Chord(0x11, 0x41); JourneyInput.Chord(0x11, 0x56);
            var expected = budget == 0 ? 4 : budget;
            await UntilJourney(() => paper.Items.Count == expected && paper.Items[0].Text != "original task 0",
                "Native multi-line paste uses budget " + expected);
            if (budget != 0)
            {
                Window? notice = null;
                await UntilJourney(() => (notice = Application.Current.Windows.Cast<Window>().SingleOrDefault(w =>
                    w.IsVisible && ReferenceEquals(w.Owner, window) && w.Title == Strings.Get("TodoPasteTruncatedTitle"))) != null,
                    "Over-budget paste presents its real truncation notice");
                Check(JourneyDescendants(notice!).OfType<TextBlock>().Any(t =>
                        t.Text == Strings.Format("TodoPasteItemLimitMessage", budget)),
                    "Truncation notice did not describe the configured item budget.");
                CaptureJourney(notice!, "paste-notice-" + budget);
                var ok = JourneyDescendants(notice!).OfType<Button>().Single(b => Equals(b.Content, Strings.Get("CommonOk")));
                await JourneyInput.Click(ok);
                await UntilJourney(() => !notice!.IsVisible && window.IsActive, "Native OK returns to the paste owner");
                JourneyLog("PASTE_NOTICE confirmed budget=" + budget);
            }
            Check(paper.Items.OrderBy(p => p.Order).Select(p => p.Text).SequenceEqual(new[] { "one", "two", "three", "four" }.Take(expected)),
                "Paste count changed but actual item text/order is wrong: " + JsonSerializer.Serialize(paper.Items.Select(p => p.Text)));
            Check(Field<List<Border>>(window, "_todoRows").Count == expected, "Visible todo rows disagree with pasted items.");
            // Model mutation precedes the queued layout/focus handoff. Wait for the actual
            // editor to become ready; never inject Ctrl+Z into the outgoing paste target.
            await Settle();
            var targetId = paper.Items.OrderBy(p => p.Order).Last().Id;
            await UntilJourney(() => editors.TryGetValue(targetId, out var target) && target.IsKeyboardFocused,
                "Paste hands keyboard focus to its last resulting item");
            JourneyLog($"PASTE_READY budget={budget} nativeUndo={editors[targetId].CanUndo} " +
                "items=" + JsonSerializer.Serialize(paper.Items.Select(p => p.Text)));
            CaptureJourney(window, "paste-" + expected);
            JourneyInput.Chord(0x11, 0x5A);
            await Settle();
            JourneyLog("PASTE_UNDO items=" + JsonSerializer.Serialize(paper.Items.Select(p => p.Text)));
            await UntilJourney(() => paper.Items.Count == 1 && paper.Items[0].Text == "original task 0",
                "One native undo restores the entire multi-line paste");
            JourneyLog("PASTE budget=" + expected + " native-undo=pass");
        }
    }
}
