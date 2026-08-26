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
    public const double MinWidth = 96;
    public const double MinHeight = 120;
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

public static class ImageReferenceTextModes
{
    public const string Always = "always";
    public const string Editing = "editing";
    public const string Hidden = "hidden";

    public static string Normalize(string? mode)
    {
        return mode is Always or Editing or Hidden ? mode : Always;
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

/// <summary>
/// Paper ResizeGrip presentation: system-contrast corner dots, or hidden dots with edge resizing.
/// </summary>
public static class ResizeGripModes
{
    public const string Standard = "standard";
    public const string Soft = "soft";
    public const string Hidden = "hidden";

    public static string Normalize(string? mode) => mode switch
    {
        Standard => Standard,
        Hidden => Hidden,
        _ => Soft
    };
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

public static class DeepCapsuleGapSizes
{
    public const string Narrow = "narrow";
    public const string Standard = "standard";
    public const string Wide = "wide";
    public const double StandardGap = 4;
    public const double VariantDelta = 4;

    public static string Normalize(string? size) =>
        size is Narrow or Wide ? size : Standard;

    public static double Value(string? size) => Normalize(size) switch
    {
        Narrow => StandardGap - VariantDelta,
        Wide => StandardGap + VariantDelta,
        _ => StandardGap
    };
}

public static class TodoVisualSizes
{
    public const string Small = "small";
    public const string Medium = "medium";
    public const string Large = "large";
    public const string ExtraLarge = "extraLarge";

    public static string Normalize(string? size)
    {
        // extraLarge was offered by older builds. Keep accepting the serialized value, but
        // migrate it to the new three-level visual scale instead of retaining a hidden fourth
        // state that cannot be selected in Settings.
        return size is Small ? Small : size is Large or ExtraLarge ? Large : Medium;
    }

    public static TodoVisualMetrics Metrics(string? size)
    {
        var metrics = Normalize(size) switch
        {
            Small => new TodoVisualMetrics(12, 2.5, 28, 13, 12, 9.5, 11.5, 21, 13, 23),
            Large => new TodoVisualMetrics(14, 3.5, 32, 15, 14, 11.5, 13.5, 24, 15, 26),
            _ => new TodoVisualMetrics(13, 3, 30, 14, 13, 10.5, 12.5, 22, 14, 24)
        };

        return metrics with
        {
            TextFontSize = AppTypography.Scale(metrics.TextFontSize),
            TextVerticalPadding = AppTypography.Scale(metrics.TextVerticalPadding),
            AppendMinHeight = AppTypography.Scale(metrics.AppendMinHeight),
            AppendGlyphFontSize = AppTypography.Scale(metrics.AppendGlyphFontSize),
            TrashGlyphFontSize = AppTypography.Scale(metrics.TrashGlyphFontSize),
            LinkedPaperNameFontSize = AppTypography.Scale(metrics.LinkedPaperNameFontSize),
            LinkedPaperIconFontSize = AppTypography.Scale(metrics.LinkedPaperIconFontSize),
            CheckColumnWidth = AppTypography.Scale(metrics.CheckColumnWidth),
            GhostTextFontSize = AppTypography.Scale(metrics.GhostTextFontSize),
            RowMinHeight = AppTypography.Scale(metrics.RowMinHeight)
        };
    }
}

public static class VisualTextSizes
{
    public const string Small = "small";
    public const string Medium = "medium";
    public const string Large = "large";

    public static string Normalize(string? size)
    {
        return size is Small or Large ? size : Medium;
    }

    public static double Correction(string? size)
    {
        return Normalize(size) switch
        {
            Small => -1,
            Large => 1,
            _ => 0
        };
    }

    public static double FontSize(double mediumSize, string? size)
    {
        return AppTypography.Scale(mediumSize + Correction(size));
    }
}

public static class OverallFontScales
{
    public const double Minimum = 0.8;
    public const double Maximum = 1.2;
    public const double Step = 0.05;

    public static double Normalize(double scale)
    {
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            return 1.0;
        }

        var clamped = Math.Clamp(scale, Minimum, Maximum);
        return Math.Round(
            Math.Round(clamped / Step, MidpointRounding.AwayFromZero) * Step,
            2,
            MidpointRounding.AwayFromZero);
    }
}

public static class ExperimentalOpacityLevels
{
    public const double Minimum = 0.3;
    public const double Maximum = 1.0;
    public const double Step = 0.05;
    public const double DefaultInactivePaper = 0.7;
    public const double DefaultRestingCapsule = 0.6;

    public static double Normalize(double opacity, double fallback)
    {
        if (double.IsNaN(opacity) || double.IsInfinity(opacity))
        {
            opacity = fallback;
        }

        var clamped = Math.Clamp(opacity, Minimum, Maximum);
        return Math.Round(
            Math.Round(clamped / Step, MidpointRounding.AwayFromZero) * Step,
            2,
            MidpointRounding.AwayFromZero);
    }
}

public static class EdgeCapsuleHoverIntentSensitivities
{
    public const string VeryLow = "veryLow";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string VeryHigh = "veryHigh";

    public static string Normalize(string? sensitivity)
    {
        return sensitivity is VeryLow or Low or High or VeryHigh
            ? sensitivity
            : Medium;
    }
}

public static class ExperimentalTodoReminderOptions
{
    public const int MinimumQuickMinutes = 5;
    public const int MaximumQuickMinutes = 240;
    public const int QuickMinutesStep = 5;
    public const int DefaultQuickMinutes = 30;

    public static int NormalizeQuickMinutes(int minutes)
    {
        var clamped = Math.Clamp(
            minutes,
            MinimumQuickMinutes,
            MaximumQuickMinutes);
        return (int)Math.Round(
            clamped / (double)QuickMinutesStep,
            MidpointRounding.AwayFromZero) * QuickMinutesStep;
    }
}

public static class TodoReminderSoundOptions
{
    public const string Asterisk = "asterisk";
    public const string Beep = "beep";
    public const string Exclamation = "exclamation";
    public const string Hand = "hand";
    public const string Question = "question";

    public static string Normalize(string? sound)
    {
        return sound is Beep or Exclamation or Hand or Question
            ? sound
            : Asterisk;
    }
}

public static class ExperimentalWindowAttachmentOptions
{
    public const int MinimumSnapDistance = 6;
    public const int MaximumSnapDistance = 48;
    public const int SnapDistanceStep = 2;
    public const int DefaultSnapDistance = 18;
    public const double DefaultWindowGap = 6;

    public static int NormalizeSnapDistance(int distance)
    {
        var clamped = Math.Clamp(
            distance,
            MinimumSnapDistance,
            MaximumSnapDistance);
        return (int)Math.Round(
            clamped / (double)SnapDistanceStep,
            MidpointRounding.AwayFromZero) * SnapDistanceStep;
    }
}

public static class ExperimentalWindowTetherOptions
{
    public const string Auto = "auto";
    public const string Left = "left";
    public const string Right = "right";
    public const string Top = "top";
    public const string Bottom = "bottom";
    public const int MinimumGap = 0;
    public const int MaximumGap = 24;
    public const int GapStep = 2;
    public const int DefaultGap = 8;

    public static string NormalizeEdge(string? edge)
    {
        return edge is Left or Right or Top or Bottom ? edge : Auto;
    }

    public static int NormalizeGap(int gap)
    {
        var clamped = Math.Clamp(gap, MinimumGap, MaximumGap);
        return (int)Math.Round(
            clamped / (double)GapStep,
            MidpointRounding.AwayFromZero) * GapStep;
    }
}

public static class ExperimentalTetherVisibilityModes
{
    public const string Hide = "hide";
    public const string Capsule = "capsule";

    public static string Normalize(string? mode)
    {
        return mode == Capsule ? Capsule : Hide;
    }
}

public static class UiFontPresets
{
    public const string Default = "default";
    public const string YaHei = "yahei";
    public const string DengXian = "dengxian";

    public static string Normalize(string? preset)
    {
        return preset is YaHei or DengXian ? preset : Default;
    }
}

public static class TextRenderingProfiles
{
    // Keep the two existing stored values so updating does not change the selected rendering.
    // "enhancedGrayscale" is now presented as the softer profile; Sharp is the new option.
    public const string Standard = "system";
    public const string Soft = "enhancedGrayscale";
    public const string Sharp = "sharpGrayscale";

    public static string Normalize(string? profile)
    {
        return profile is Soft or Sharp ? profile : Standard;
    }
}

public readonly record struct TodoVisualMetrics(
    double TextFontSize,
    double TextVerticalPadding,
    double AppendMinHeight,
    double AppendGlyphFontSize,
    double TrashGlyphFontSize,
    double LinkedPaperNameFontSize,
    double LinkedPaperIconFontSize,
    double CheckColumnWidth,
    double GhostTextFontSize,
    double RowMinHeight);

public sealed class AppState
{
    [JsonRequired]
    public List<PaperData> Papers { get; set; } = new();
    [JsonPropertyOrder(-100)]
    public string UiLanguage { get; set; } = UiLanguages.Default;
    public string Theme { get; set; } = "system";
    public string ColorScheme { get; set; } = ColorSchemes.Warm;
    public string MarkdownRenderMode { get; set; } = MarkdownRenderModes.Enhanced;
    public string ImageReferenceTextMode { get; set; } = ImageReferenceTextModes.Always;
    public string TodoVisualSize { get; set; } = TodoVisualSizes.Medium;
    public bool AutoClearCompletedTodos { get; set; }
    public bool AutoMoveCompletedTodosToBottom { get; set; }
    public bool AutoCompressLargeImages { get; set; } = true;
    public string UiFontPreset { get; set; } = UiFontPresets.Default;
    public string TextRenderingProfile { get; set; } = TextRenderingProfiles.Standard;
    public bool AdvancedSettingsMode { get; set; }
    /// <summary>
    /// When a custom papertodo font is present, bold styles load papertodo_bold / PaperTodo_Bold instead of synthetic SemiBold.
    /// </summary>
    public bool CustomFontEnhancedBold { get; set; }
    public string ExternalMarkdownExtension { get; set; } = ExternalMarkdownFileExtensions.Default;
    public double Zoom { get; set; } = 1.0;
    public string NoteTextSize { get; set; } = VisualTextSizes.Medium;
    public bool NoteTextBold { get; set; }
    public bool TodoTextBold { get; set; }
    public string TitleTextSize { get; set; } = VisualTextSizes.Medium;
    public bool TitleTextBold { get; set; } = true;
    public string CapsuleTextSize { get; set; } = VisualTextSizes.Medium;
    public bool CapsuleTextBold { get; set; }
    public bool UseCapsuleMode { get; set; } = true;
    public bool UseDeepCapsuleMode { get; set; } = true;
    public string DeepCapsuleGapSize { get; set; } = DeepCapsuleGapSizes.Standard;
    public bool ShowTopBarNewTodoButton { get; set; } = true;
    public bool ShowTopBarNewNoteButton { get; set; } = true;
    public bool ShowTopBarExternalOpenButton { get; set; } = true;
    public bool HidePapersFromTaskbar { get; set; } = true;
    public bool HidePapersFromWindowSwitcher { get; set; } = true;
    [JsonPropertyName("enableTodoNoteLinks")]
    public bool EnableTodoPaperLinks { get; set; } = true;
    [JsonPropertyName("showLinkedNoteName")]
    public bool ShowLinkedPaperName { get; set; }
    [JsonPropertyName("allowLongLinkedNoteTitles")]
    public bool AllowLongLinkedPaperTitles { get; set; }
    public bool ShowLinkedPathExtensionOnly { get; set; }
    [JsonPropertyName("hideLinkedNotesFromCapsules")]
    public bool HideLinkedPapersFromCapsules { get; set; }
    public bool RunLinkedScriptCapsulesOnClick { get; set; }
    public int MaxTitleLength { get; set; } = PaperTitles.DefaultMaxTitleLength;
    public bool UseCapsuleCollapseAll { get; set; } = true;
    public Dictionary<string, bool> CapsuleCollapseAllActiveQueues { get; set; } = new();
    public bool ShowDeepCapsuleWhileExpanded { get; set; } = true;
    public bool HideEdgeCapsuleCloseButtonOnHover { get; set; }
    public bool CollapseExpandedDeepCapsuleOnClick { get; set; }
    public bool EnableAnimations { get; set; } = true;
    public bool EnableToolTips { get; set; } = true;
    public bool ExperimentalInactivePaperOpacity { get; set; }
    public double ExperimentalInactivePaperOpacityLevel { get; set; } =
        ExperimentalOpacityLevels.DefaultInactivePaper;
    public bool ExperimentalRestingCapsuleOpacity { get; set; }
    public double ExperimentalRestingCapsuleOpacityLevel { get; set; } =
        ExperimentalOpacityLevels.DefaultRestingCapsule;
    public bool ExperimentalRestingCapsuleOpacityIncludesMaster { get; set; }
    public bool ExperimentalRestingCapsuleOpacityAlways { get; set; }
    public bool ExperimentalCollapsePaperOnDeactivate { get; set; }
    public bool ExperimentalHideInactiveTopBarButtons { get; set; }
    public bool ExperimentalHideInactiveTitleBar { get; set; }
    public bool ExperimentalDockedCapsulesNonTopmost { get; set; }
    public bool ExperimentalEdgeCapsuleHoverPreview { get; set; } = true;
    public bool ExperimentalEdgeCapsuleHoverIntent { get; set; } = true;
    public string ExperimentalEdgeCapsuleHoverIntentSensitivity { get; set; } =
        EdgeCapsuleHoverIntentSensitivities.Medium;
    public bool ExperimentalAllowLockIconUnlock { get; set; } = true;
    public double ExperimentalShortcutOpacityLevel { get; set; } = 0.35;
    public bool ExperimentalTodoReminders { get; set; }
    public bool ExperimentalTodoReminderShowButton { get; set; } = true;
    public int ExperimentalTodoReminderQuickMinutes { get; set; } =
        ExperimentalTodoReminderOptions.DefaultQuickMinutes;
    public bool ExperimentalTodoReminderSoundEnabled { get; set; }
    public string ExperimentalTodoReminderSound { get; set; } =
        TodoReminderSoundOptions.Asterisk;
    public bool McpEnabled { get; set; }
    public bool McpAllowBlankWrites { get; set; }
    public bool McpAllowFullWrites { get; set; }
    public bool McpAllowDeletes { get; set; }
    public bool ExperimentalCapsuleMagnetism { get; set; }
    public bool ExperimentalCapsuleMagnetScreenEdges { get; set; } = true;
    public bool ExperimentalCapsuleMagnetWindowEdges { get; set; } = true;
    public int ExperimentalCapsuleMagnetDistance { get; set; } =
        ExperimentalWindowAttachmentOptions.DefaultSnapDistance;
    public bool ExperimentalWindowTethering { get; set; }
    public string ExperimentalWindowTetherPreferredEdge { get; set; } =
        ExperimentalWindowTetherOptions.Auto;
    public int ExperimentalWindowTetherGap { get; set; } =
        ExperimentalWindowTetherOptions.DefaultGap;
    public bool ExperimentalTetherVisibilityLink { get; set; }
    public string ExperimentalTetherMinimizedBehavior { get; set; } =
        ExperimentalTetherVisibilityModes.Hide;
    /// <summary>
    /// Paper ResizeGrip: standard / soft (50% transparent) / hidden (no dots; all edges resize).
    /// Dot color is Windows ControlDark with a light scheme tint.
    /// </summary>
    public string ResizeGripMode { get; set; } = ResizeGripModes.Soft;
    public string FullscreenTopmostMode { get; set; } = FullscreenTopmostModes.Avoid;
    public bool UsePersistentPowerShellProcess { get; set; }
    public bool PreferPowerShell7 { get; set; } = true;
    public bool HideScriptRunWindow { get; set; } = true;
    public int DeepCapsuleTitleMeasureCharacterLimit { get; set; }
    public Dictionary<string, string> GlobalHotkeys { get; set; } = new();
    public Dictionary<string, bool> GlobalHotkeyEnabled { get; set; } = new();
    public bool DistinguishNumpadShortcutDigits { get; set; }
    public bool PreserveLinkedPaperHiddenStateInVisibilityShortcuts { get; set; } = true;
    // When true, edge-queue shortcuts expand the paper centered under the current mouse pointer
    // instead of the docked edge / remembered expanded geometry.
    public bool OpenEdgeCapsuleShortcutAtCursor { get; set; } = true;
    // Per-queue vertical start margin, keyed by "monitorDevice|side". Missing entries use
    // EdgeCapsuleLayout.StartTopMargin directly; there is no second persisted global authority.
    public Dictionary<string, double> DeepCapsuleQueueStartTopMargins { get; set; } = new();
    public bool RememberDeepCapsuleExpandedPosition { get; set; } = true;

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

    // Note body provider.
    public string BodyProviderId { get; set; } = PaperBodyProviderIds.Markdown;
    // Expanded runtime header and folded capsule fallback are independent lightweight caches.
    // They keep the last plugin presentation before the body session is recreated.
    [JsonPropertyName("bodyHeaderText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string BodyHeaderText { get; set; } = "";

    [JsonPropertyName("bodyCapsuleText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string BodyCapsuleText { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string StartupOwnerPluginId { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string StartupInstanceKey { get; set; } = "";

    // Which edge-queue this paper's capsule belongs to. A queue is identified by
    // (CapsuleMonitorDeviceName, CapsuleSide): every docked capsule sharing the same pair
    // forms one vertical stack with its own master pill. Empty CapsuleSide means "not yet
    // assigned" — on load it inherits the legacy global anchor so existing capsules keep place.
    public string CapsuleSide { get; set; } = "";
    public string CapsuleMonitorDeviceName { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DeepCapsuleExpandedX { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DeepCapsuleExpandedY { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DeepCapsuleExpandedWidth { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DeepCapsuleExpandedHeight { get; set; }

    public string DeepCapsuleExpandedSide { get; set; } = "";
    public string DeepCapsuleExpandedMonitorDeviceName { get; set; } = "";

    public List<PaperItem> Items { get; set; } = new();
    public string Content { get; set; } = "";
}

public sealed class PaperItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public int Order { get; set; }

    [JsonInclude]
    [JsonPropertyName("linkedNoteId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LinkedPaperId { get; private set; }

    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LinkedPath { get; private set; }

    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LinkedPathIsDirectory { get; private set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ReminderAt { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReminderTriggered { get; set; }

    public void LinkPaper(string? paperId)
    {
        LinkedPaperId = NormalizeQuickLaunchValue(paperId);
        LinkedPath = null;
        LinkedPathIsDirectory = null;
    }

    public void LinkPath(string? path, bool? isDirectory = null)
    {
        LinkedPath = NormalizeQuickLaunchValue(path);
        LinkedPathIsDirectory = LinkedPath == null ? null : isDirectory;
        LinkedPaperId = null;
    }

    public void ClearQuickLaunch()
    {
        LinkedPaperId = null;
        LinkedPath = null;
        LinkedPathIsDirectory = null;
    }

    internal void RestoreQuickLaunch(string? paperId, string? path, bool? pathIsDirectory = null)
    {
        var normalizedPaperId = NormalizeQuickLaunchValue(paperId);
        if (normalizedPaperId != null)
        {
            LinkedPaperId = normalizedPaperId;
            LinkedPath = null;
            LinkedPathIsDirectory = null;
            return;
        }

        LinkedPaperId = null;
        LinkedPath = NormalizeQuickLaunchValue(path);
        LinkedPathIsDirectory = LinkedPath == null ? null : pathIsDirectory;
    }

    private static string? NormalizeQuickLaunchValue(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
