using System.IO;
using System.Text.Json.Serialization;

namespace PaperTodo;

public static class PaperTypes
{
    public const string Todo = "todo";
    public const string Note = "note";
}

public static class PaperLayoutDefaults
{
    public const double MinWidth = 220;
    public const double MinHeight = 160;
    public const double TopBarHeight = 23.5;

    public const double CapsuleWidth = 92; // 包含阴影边框边距
    public const double CapsuleHeight = 46;

    public const double TodoDefaultWidth = 280;
    public const double TodoDefaultHeight = 340;

    public const double NoteDefaultWidth = 320;
    public const double NoteDefaultHeight = 360;
}

public static class MarkdownRenderModes
{
    public const string Off = "off";
    public const string Basic = "basic";
    public const string Enhanced = "enhanced";

    public static bool IsValid(string? mode)
    {
        return mode is Off or Basic or Enhanced;
    }
}

public static class ExternalMarkdownFileExtensions
{
    public const string Default = ".md";

    public static string Normalize(string? extension)
    {
        var value = (extension ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return Default;
        }

        if (value.StartsWith("*.", StringComparison.Ordinal))
        {
            value = value[1..];
        }
        if (!value.StartsWith(".", StringComparison.Ordinal))
        {
            value = "." + value;
        }

        if (value.Length is < 2 or > 32 ||
            value.Contains("..", StringComparison.Ordinal) ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return Default;
        }

        return value.ToLowerInvariant();
    }
}

public static class FullscreenTopmostModes
{
    public const string Avoid = "avoid";
    public const string StayOnTop = "stayOnTop";

    public static string Normalize(string? mode)
    {
        return mode is StayOnTop ? StayOnTop : Avoid;
    }
}

public static class DeepCapsuleSides
{
    public const string Left = "left";
    public const string Right = "right";

    public static string Normalize(string? side)
    {
        return side is Left ? Left : Right;
    }
}

public static class TodoVisualSizes
{
    public const string Small = "small";
    public const string Medium = "medium";
    public const string Large = "large";
    public const string ExtraLarge = "extraLarge";

    public static string Normalize(string? size)
    {
        return size is Small or Large or ExtraLarge ? size : Medium;
    }

    public static TodoVisualMetrics Metrics(string? size)
    {
        return Normalize(size) switch
        {
            Small => new TodoVisualMetrics(12, 2.5, 28, 13, 12, 9.5, 11.5, 21, 13, 23),
            Large => new TodoVisualMetrics(14, 3.5, 32, 15, 14, 11.5, 13.5, 24, 15, 26),
            ExtraLarge => new TodoVisualMetrics(15.5, 4.5, 36, 16.5, 15.5, 13, 15, 27, 17, 30),
            _ => new TodoVisualMetrics(13, 3, 30, 14, 13, 10.5, 12.5, 22, 14, 24)
        };
    }
}

public readonly record struct TodoVisualMetrics(
    double TextFontSize,
    double TextVerticalPadding,
    double AppendMinHeight,
    double AppendGlyphFontSize,
    double TrashGlyphFontSize,
    double LinkedNoteNameFontSize,
    double LinkedNoteIconFontSize,
    double CheckColumnWidth,
    double GhostTextFontSize,
    double RowMinHeight);

public sealed class AppState
{
    public List<PaperData> Papers { get; set; } = new();
    public string Theme { get; set; } = "system";
    public string ColorScheme { get; set; } = ColorSchemes.Warm;
    public string MarkdownRenderMode { get; set; } = MarkdownRenderModes.Enhanced;
    public string TodoVisualSize { get; set; } = TodoVisualSizes.Medium;
    public string ExternalMarkdownExtension { get; set; } = ExternalMarkdownFileExtensions.Default;
    public double Zoom { get; set; } = 1.0;
    public bool UseCapsuleMode { get; set; } = true;
    public bool UseDeepCapsuleMode { get; set; } = true;
    public bool ShowTopBarNewTodoButton { get; set; } = true;
    public bool ShowTopBarNewNoteButton { get; set; } = true;
    public bool ShowTopBarExternalOpenButton { get; set; } = true;
    public bool EnableTodoNoteLinks { get; set; } = true;
    public bool ShowLinkedNoteName { get; set; }
    public bool HideLinkedNotesFromCapsules { get; set; }
    public bool RunLinkedScriptCapsulesOnClick { get; set; }
    public int MaxTitleLength { get; set; } = PaperTitles.DefaultMaxTitleLength;
    public bool UseCapsuleCollapseAll { get; set; }
    public bool CapsuleCollapseAllActive { get; set; }
    public Dictionary<string, bool> CapsuleCollapseAllActiveQueues { get; set; } = new();
    public bool ShowDeepCapsuleWhileExpanded { get; set; } = true;
    public bool EnableAnimations { get; set; } = true;
    public bool EnableToolTips { get; set; } = true;
    public string FullscreenTopmostMode { get; set; } = FullscreenTopmostModes.Avoid;
    public bool UsePersistentPowerShellProcess { get; set; }
    public bool PreferPowerShell7 { get; set; } = true;
    public bool HideScriptRunWindow { get; set; } = true;
    public double DeepCapsuleStartTopMargin { get; set; } = DeepCapsuleLayout.StartTopMargin;

    // Per-queue vertical start margin, keyed by "monitorDevice|side". A missing key falls back to
    // the legacy global DeepCapsuleStartTopMargin, so dragging one queue's master only slides that
    // queue. Old configs (no per-queue entries) keep behaving exactly as the single global margin.
    public Dictionary<string, double> DeepCapsuleQueueStartTopMargins { get; set; } = new();

    // Which screen edge the deep-capsule stack docks to. "left" or "right" (default).
    public string DeepCapsuleSide { get; set; } = DeepCapsuleSides.Right;

    // Device name (e.g. "\\\\.\\DISPLAY1") of the monitor hosting the deep-capsule stack.
    // Empty means "the primary monitor"; resolved with a nearest-monitor fallback on load,
    // so unplugging the anchored monitor gracefully lands the stack on a surviving screen.
    public string DeepCapsuleMonitorDeviceName { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double TopBarHeight { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowTopBarNewPaperButtons { get; set; }
}

public sealed class PaperData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = PaperTypes.Todo;
    public string Title { get; set; } = "";

    public double X { get; set; } = 120;
    public double Y { get; set; } = 120;
    public double Width { get; set; } = 280;
    public double Height { get; set; } = 360;

    public bool IsVisible { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool IsCollapsed { get; set; } = false;
    public double TextZoom { get; set; } = 1.0;

    // Which edge-queue this paper's capsule belongs to. A queue is identified by
    // (CapsuleMonitorDeviceName, CapsuleSide): every docked capsule sharing the same pair
    // forms one vertical stack with its own master pill. Empty CapsuleSide means "not yet
    // assigned" — on load it inherits the legacy global anchor so existing capsules keep place.
    public string CapsuleSide { get; set; } = "";
    public string CapsuleMonitorDeviceName { get; set; } = "";

    public List<PaperItem> Items { get; set; } = new();
    public string Content { get; set; } = "";
}

public sealed class PaperItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public int Order { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LinkedNoteId { get; set; }
}
