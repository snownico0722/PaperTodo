using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperTodo;

internal static partial class InjectionSystemRunner
{
    private sealed record PreviewObservation(double NoteWidth, double NoteHeight, double TodoWidth, double TodoHeight, double TodoRenderedWidth, double QueueSpacing);

    // Three actual processes: baseline -> modified restart configuration -> removed configuration.
    // No LoadStartup call or in-process cache reset is used to simulate either restart.
    private static async Task<bool> UserPreview(AppController c)
    {
        var stagePath = Path.Combine(AppContext.BaseDirectory, "preview-stage.txt");
        var stage = int.Parse(File.ReadAllText(stagePath));
        JourneyLog("PREVIEW_STAGE " + stage);
        var note = Windows(c)["note"];
        var todo = Windows(c)["todo"];
        foreach (var window in new[] { note, todo }) window.SetCollapsedState(true, animate: false);
        await ParkPreviewPointer(c);
        var current = await ObservePreviews(c, stage, stage == 1 ? 3 : 5, delayed: stage == 1);
        var baselinePath = Path.Combine(AppContext.BaseDirectory, "preview-baseline.json");
        if (stage == 0) File.WriteAllText(baselinePath, JsonSerializer.Serialize(current));
        var baseline = JsonSerializer.Deserialize<PreviewObservation>(File.ReadAllText(baselinePath))!;
        if (stage == 1)
        {
            Check(Math.Abs(current.TodoWidth - 480) < 1 && Math.Abs(current.TodoHeight - 360) < 1 &&
                Math.Abs(current.NoteWidth - 460) < 1 && Math.Abs(current.NoteHeight - 350) < 1,
                "Restarted previews did not use the requested size.");
            Check(current.TodoRenderedWidth > baseline.TodoRenderedWidth + 30,
                "Preview descriptor grew but the actual rendered body stayed at the old width.");
        }
        if (stage == 2)
        {
            Check(Math.Abs(current.NoteWidth - baseline.NoteWidth) < 1 && Math.Abs(current.NoteHeight - baseline.NoteHeight) < 1 &&
                Math.Abs(current.TodoWidth - baseline.TodoWidth) < 1 && Math.Abs(current.TodoHeight - baseline.TodoHeight) < 1 &&
                Math.Abs(current.TodoRenderedWidth - baseline.TodoRenderedWidth) < 1 && Math.Abs(current.QueueSpacing - baseline.QueueSpacing) < 2,
                "Removing overrides and restarting did not restore original geometry/content capacity.");
            Check(UserInjection.Read().Overrides.Count == 0, "The final process still has a file override.");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, Result), "PASS native preview journey: three processes, size/content/gap restored, native hover/checkbox/transfer verified");
            return false;
        }

        var view = new GoodLockView(c);
        if (stage == 0)
            await ExternalJourneyApply(view,
                ("edge.preview.todo.widthDip", 480d), ("edge.preview.todo.heightDip", 360d),
                ("edge.preview.markdown.widthDip", 460d), ("edge.preview.markdown.heightDip", 350d),
                ("edge.preview.todo.itemBudget", 3), ("capsule.gap.finalDip", 20d),
                ("edge.preview.activation.transferResidenceMs", 1500d));
        else await ExternalJourneyApply(view);
        var pending = await ObservePreviews(c, stage + 10, stage == 1 ? 3 : 5, delayed: stage == 0);
        Check(Math.Abs(pending.NoteWidth - current.NoteWidth) < 1 && Math.Abs(pending.TodoWidth - current.TodoWidth) < 1,
            "Restart-only preview width leaked into the current process.");
        Check(stage == 0 ? pending.QueueSpacing > current.QueueSpacing + 8 : Math.Abs(pending.QueueSpacing - baseline.QueueSpacing) < 2,
            "Live gap changed in the slot but not in the actual physical queue.");
        await ParkPreviewPointer(c);
        File.WriteAllText(stagePath, (stage + 1).ToString());
        view.Window.Show(); view.Window.Activate(); view.Session.OnActivated();
        var restart = JourneyDescendants(view.View).OfType<Button>().Single(b => Equals(b.Content, view.Label("Restart")));
        await JourneyInput.Aim(restart);
        // Let Child mark its successful, intentionally restarting state before native mouse delivery
        // enters the plugin's actual Apply -> Restart button handler.
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            JourneyInput.Button(true); JourneyInput.Button(false);
        }));
        return true;
    }

    private static async Task ParkPreviewPointer(AppController c)
    {
        var area = SystemParameters.WorkArea;
        JourneyInput.Move(new Point(area.Left + area.Width / 2, area.Top + area.Height / 2));
        await UntilJourney(() => Windows(c).Values.All(w => !w.IsEdgeCapsulePreviewOpen), "Leaving the physical queue closes its preview");
        await Settle();
    }

    private static async Task HoverPreview(PaperWindow window)
    {
        await UntilJourney(() => window.CanEnterEdgeCapsulePreview && window.TryGetEdgeCapsuleInteractiveGeometry(out _),
            "A real interactive edge capsule is ready: " + window.PaperId);
        window.TryGetEdgeCapsuleInteractiveGeometry(out var geometry);
        JourneyInput.Move(new Point(geometry.Bounds.Left + geometry.Bounds.Width / 2, geometry.Bounds.Top + geometry.Bounds.Height / 2));
    }

    private static async Task<PreviewObservation> ObservePreviews(AppController c, int stage, int itemCount, bool delayed)
    {
        var note = Windows(c)["note"]; var todo = Windows(c)["todo"];
        await ParkPreviewPointer(c);
        note.TryGetEdgeCapsuleInteractiveGeometry(out var ng);
        todo.TryGetEdgeCapsuleInteractiveGeometry(out var tg);
        var spacing = Math.Abs(ng.Bounds.Top - tg.Bounds.Top);
        await HoverPreview(note);
        await UntilJourney(() => note.IsEdgeCapsulePreviewOpen && note.HasEdgeCapsulePreviewContent, "Native hover opens Markdown preview");
        await Settle();
        var nr = Field<EdgeCapsulePreviewRequest>(note, "_edgeCapsulePreviewRequest");
        Check(nr.Content.IsLoaded && nr.Content.IsVisible && nr.Content.ActualWidth > 0 && nr.Content.ActualHeight > 0,
            "Markdown preview has no laid-out visual body.");
        CaptureJourney(nr.Content, "preview-" + stage + "-note");
        var watch = Stopwatch.StartNew();
        await HoverPreview(todo);
        if (delayed)
        {
            await Task.Delay(250);
            Check(!todo.IsEdgeCapsulePreviewOpen, "Injected transfer residence did not delay real pointer transfer.");
        }
        await UntilJourney(() => todo.IsEdgeCapsulePreviewOpen && !note.IsEdgeCapsulePreviewOpen, "Native pointer transfers preview ownership");
        JourneyLog($"HOVER_TRANSFER stage={stage} ms={watch.ElapsedMilliseconds} delayed={delayed}");
        await Settle();
        var tr = Field<EdgeCapsulePreviewRequest>(todo, "_edgeCapsulePreviewRequest");
        Check(tr.Content.IsLoaded && tr.Content.IsVisible && tr.Content.ActualWidth > 0, "Todo preview has no rendered body.");
        var checks = JourneyDescendants(tr.Content).OfType<CheckBox>().ToArray();
        Check(checks.Length == itemCount, $"Actual preview rows={checks.Length}, expected={itemCount}.");
        var item = c.State.Papers.Single(p => p.Id == "todo").Items.OrderBy(p => p.Order).First();
        var original = item.Done;
        await JourneyInput.Click(checks[0], activate: false);
        await UntilJourney(() => item.Done != original, "Native checkbox updates actual todo without expanding the paper");
        Check(c.State.Papers.Single(p => p.Id == "todo").IsCollapsed && todo.IsEdgeCapsulePreviewOpen,
            "Preview checkbox triggered expansion or closed the active preview.");
        await Settle();
        var updated = JourneyDescendants(tr.Content).OfType<CheckBox>().First();
        await JourneyInput.Click(updated, activate: false);
        await UntilJourney(() => item.Done == original, "Native checkbox remains usable after live content invalidation");
        CaptureJourney(tr.Content, "preview-" + stage + "-todo");
        var result = new PreviewObservation(nr.Size.WidthDip, nr.Size.HeightDip, tr.Size.WidthDip, tr.Size.HeightDip, tr.Content.ActualWidth, spacing);
        JourneyLog("PREVIEW " + stage + " " + JsonSerializer.Serialize(result));
        await ParkPreviewPointer(c);
        return result;
    }
}
