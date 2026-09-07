using System.Text.Json;
using PaperTodo;

internal static class TodoRetentionChecks
{
    internal static void CalendarPolicy()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test-8", TimeSpan.FromHours(8), "test", "test");
        var sunday = DateTimeOffset.Parse("2026-09-06T23:59:59+08:00");
        var monday = sunday.AddSeconds(1);
        var item = new PaperItem();
        TodoRules.SetDone(item, true, sunday);
        Check(!TodoRules.IsRemovalDue(item, TodoRules.RemoveNextWeek, sunday, zone), "Sunday removed early");
        Check(TodoRules.IsRemovalDue(item, TodoRules.RemoveNextWeek, monday, zone), "Sunday did not expire on Monday");
        TodoRules.SetDone(item, false, monday);
        Check(item.CompletedAt == null && !TodoRules.IsRemovalDue(item, TodoRules.RemoveImmediately, monday, zone), "reopen did not cancel removal");
        TodoRules.SetDone(item, true, monday);
        Check(!TodoRules.IsRemovalDue(item, TodoRules.RemoveNextWeek, monday.AddDays(6), zone), "Monday work expired in same week");
        Check(TodoRules.IsRemovalDue(item, TodoRules.RemoveNextWeek, monday.AddDays(7), zone), "natural week became rolling seven days");
        Check(!TodoRules.IsRemovalDue(item, TodoRules.RemoveNextDay, monday.AddHours(23), zone), "same day removed");
        Check(TodoRules.IsRemovalDue(item, TodoRules.RemoveNextDay, monday.AddDays(1), zone), "next day not removed");
        Check(!TodoRules.IsRemovalDue(new PaperItem { Done = true }, TodoRules.RemoveNextDay, monday, zone), "legacy completion date invented");
        Check(!TodoRules.IsRemovalDue(item, TodoRules.KeepCompleted, monday.AddYears(5), zone), "keep policy removed work");
        foreach (var boundary in new[] { "2027-01-01T00:00:00+08:00", "2026-10-01T00:00:00+08:00" })
        {
            var at = DateTimeOffset.Parse(boundary);
            item.CompletedAt = at.AddSeconds(-1);
            Check(TodoRules.IsRemovalDue(item, TodoRules.RemoveNextDay, at, zone), "month/year boundary failed");
        }
        var old = JsonSerializer.Deserialize<AppState>("{\"papers\":[],\"autoClearCompletedTodos\":true}", new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        Check(TodoRules.RemovalMode(old) == TodoRules.RemoveImmediately, "old enabled setting lost");
        old.CompletedTodoRemoval = TodoRules.RemoveNextDay;
        var roundtrip = JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(old, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        Check(TodoRules.RemovalMode(roundtrip) == TodoRules.RemoveNextDay, "new setting did not override legacy bool");
        Check(TodoRules.Clone(item).CompletedAt == item.CompletedAt, "clone lost completion time");
        var restored = TodoRules.Clone(item);
        var unchangedAt = restored.CompletedAt;
        TodoRules.RestoreCompletionTimes([restored], [item], monday.AddDays(20));
        Check(restored.CompletedAt == unchangedAt, "text-only undo reset completion time");
        var reopened = TodoRules.Clone(item);
        TodoRules.SetDone(reopened, false, monday);
        TodoRules.RestoreCompletionTimes([restored], [reopened], monday.AddDays(20));
        Check(restored.CompletedAt == monday.AddDays(20), "redo did not renew completion time");
        var due = new PaperItem { Id = "due", Done = true, CompletedAt = sunday, Order = 0 };
        var keep = new PaperItem { Id = "keep", Done = true, CompletedAt = monday, Order = 1 };
        var legacy = new PaperItem { Id = "legacy", Done = true, Order = 2 };
        var items = new List<PaperItem> { due, keep, legacy };
        var dueIds = items.Where(i => TodoRules.IsRemovalDue(i, TodoRules.RemoveNextWeek, monday, zone)).Select(i => i.Id).ToArray();
        TodoRules.ApplyCompletionPolicy(items, dueIds, true, true, false);
        Check(items.Select(i => i.Id).SequenceEqual(new[] { "keep", "legacy" }), "removal affected retained rows");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
