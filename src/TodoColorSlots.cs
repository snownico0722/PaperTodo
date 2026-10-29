using System.Globalization;
using System.Windows.Media;

namespace PaperTodo;

public sealed class TodoColorSlot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    // Empty names on the four initial slots use the localized preset name.
    public string Name { get; set; } = "";
    public string? Preset { get; set; } = ColorSchemes.Warm;
    public string? PrimaryColor { get; set; }

    public TodoColorSlot Copy() => new() { Id = Id, Name = Name, Preset = Preset, PrimaryColor = PrimaryColor };
}

internal static class TodoColorSlots
{
    internal static List<TodoColorSlot> Defaults() =>
        new[] { ColorSchemes.Warm, ColorSchemes.Ink, ColorSchemes.Forest, ColorSchemes.Rose }
            .Select(preset => new TodoColorSlot { Id = "default-" + preset, Preset = preset }).ToList();

    internal static bool TryColor(string? hex, out Color color)
    {
        color = default;
        if (hex is not { Length: 7 } || hex[0] != '#' ||
            !uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    internal static bool IsValid(TodoColorSlot? slot) => slot != null &&
        !string.IsNullOrWhiteSpace(slot.Id) && slot.Id.Length <= 128 &&
        slot.Name != null && slot.Name.Length <= 40 &&
        (slot.PrimaryColor == null ? ColorSchemes.IsValid(slot.Preset) : TryColor(slot.PrimaryColor, out _));

    internal static TodoColorSlot? Resolve(AppState state, PaperData paper) =>
        paper.Type == PaperTypes.Todo && paper.ColorSlotId != null
            ? state.TodoColorSlots?.FirstOrDefault(slot => IsValid(slot) && slot.Id == paper.ColorSlotId) : null;

    internal static string DisplayName(TodoColorSlot slot) => string.IsNullOrWhiteSpace(slot.Name)
        ? Strings.Get(slot.Preset switch
        {
            ColorSchemes.Ink => "ColorSchemeInk", ColorSchemes.Forest => "ColorSchemeForest",
            ColorSchemes.Rose => "ColorSchemeRose", ColorSchemes.Neutral => "ColorSchemeNeutral",
            _ => "ColorSchemeWarm"
        }) : slot.Name;

    internal static void Normalize(AppState state)
    {
        state.TodoColorSlots ??= Defaults();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        // Preserve invalid entries for recovery, but never resolve them for painting.
        foreach (var slot in state.TodoColorSlots)
        {
            if (slot == null || !IsValid(slot) || !ids.Add(slot.Id)) continue;
            slot.Name = slot.Name.Trim();
            if (slot.PrimaryColor != null) slot.PrimaryColor = slot.PrimaryColor.ToUpperInvariant();
        }
    }

    internal static PaperSettingChange Change(AppState state, List<TodoColorSlot> slots,
        IReadOnlyDictionary<PaperData, string?> assignments, Action publish)
    {
        if (slots.Any(slot => !IsValid(slot)) || slots.Select(slot => slot.Id).Distinct(StringComparer.Ordinal).Count() != slots.Count)
            throw new ArgumentException("Invalid or duplicate color slot.", nameof(slots));
        var previous = state.TodoColorSlots;
        var oldAssignments = assignments.Keys.ToDictionary(paper => paper, paper => paper.ColorSlotId);
        return new PaperSettingChange(
            Apply: () => { state.TodoColorSlots = slots; foreach (var row in assignments) row.Key.ColorSlotId = row.Value; },
            Rollback: () => { state.TodoColorSlots = previous; foreach (var row in oldAssignments) row.Key.ColorSlotId = row.Value; },
            Publish: publish);
    }
}
