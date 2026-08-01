using System.Text.Json;

namespace PaperTodo.Plugin.ReviewArchive;

internal static class ReviewArchiveSettingsReader
{
    internal static ReviewArchiveViewState ReadViewState(string json, string defaultFilter)
    {
        try
        {
            var state = JsonSerializer.Deserialize<ReviewArchiveViewState>(
                string.IsNullOrWhiteSpace(json) ? "{}" : json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new ReviewArchiveViewState();
            state.Filter = ValidFilter(state.Filter) ? state.Filter : defaultFilter;
            state.Search = (state.Search ?? "").Trim();
            return state;
        }
        catch
        {
            return new ReviewArchiveViewState { Filter = defaultFilter };
        }
    }

    private static bool ValidFilter(string value) => value is
        "completed" or "today" or "week" or "month" or "open" or "deleted" or "all";

    internal static ReviewArchiveSettings ReadSettings(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = document.RootElement;
            return new ReviewArchiveSettings(
                Bool(root, "trackCreated", true),
                Bool(root, "keepDeleted", true),
                Bool(root, "showOpenItems", false),
                Choice(root, "initialImport", "completed", "none", "completed", "all"),
                Choice(root, "defaultFilter", "completed", "completed", "today", "week", "month", "all"),
                Number(root, "retentionDays", 0, 0, 3650),
                Number(root, "maxRecords", 10000, 100, 50000),
                Choice(root, "exportEncoding", "utf8bom", "utf8bom", "utf8"),
                Choice(root, "exportDateFormat", "local", "local", "iso"),
                Bool(root, "includePaperTitle", true),
                Bool(root, "showDeletedBadge", true),
                Bool(root, "confirmClear", true),
                Choice(root, "titleMode", "summary", "summary", "today", "fixed"),
                Text(root, "fixedTitle", "复盘记录", 40));
        }
        catch
        {
            return new ReviewArchiveSettings(
                true, true, false, "completed", "completed", 0, 10000,
                "utf8bom", "local", true, true, true, "summary", "复盘记录");
        }
    }

    private static bool Bool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static int Number(
        JsonElement root,
        string name,
        int fallback,
        int min,
        int max) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? Math.Clamp(number, min, max)
            : fallback;

    private static string Choice(
        JsonElement root,
        string name,
        string fallback,
        params string[] choices)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }
        var text = value.GetString() ?? "";
        return choices.Contains(text, StringComparer.Ordinal) ? text : fallback;
    }

    private static string Text(
        JsonElement root,
        string name,
        string fallback,
        int maxLength)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }
        var text = (value.GetString() ?? "").Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
