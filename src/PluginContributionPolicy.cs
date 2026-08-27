using System.Windows.Media;
using PaperTodo.Plugin;

namespace PaperTodo;

internal static class PluginContributionPolicy
{
    internal const int MaximumTodoActionsPerItem = 64;
    internal const int MaximumTopBarLabelsPerPaper = 8;
    private const int MaximumIdentifierLength = 64;
    private const int MaximumActionTextLength = 64;
    private const int MaximumLabelTextLength = 80;
    private const int MaximumToolTipLength = 160;
    private const int MaximumCharacterIconLength = 8;
    private const int MaximumSvgPathLength = 4096;
    private const double MinimumSvgStrokeWidth = 0.1;
    private const double MaximumSvgStrokeWidth = 4.0;

    internal static PaperTodoAction[] NormalizeTodoActions(
        IReadOnlyList<PaperTodoAction>? actions)
    {
        actions ??= [];
        if (actions.Count > MaximumTodoActionsPerItem)
        {
            throw new PaperTodoPluginException(
                "too_many_todo_actions",
                $"A plugin can contribute at most {MaximumTodoActionsPerItem} actions to one Todo.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PaperTodoAction>(actions.Count);
        foreach (var source in actions)
        {
            if (source == null)
            {
                throw new PaperTodoPluginException(
                    "invalid_todo_action",
                    "Todo actions cannot contain null entries.");
            }

            var id = NormalizeIdentifier(source.Id, "invalid_todo_action_id", "Todo action");
            if (!seen.Add(id))
            {
                throw new PaperTodoPluginException(
                    "invalid_todo_action_id",
                    "Todo action ids must be unique for one Todo contribution.");
            }

            var text = NormalizeText(
                source.Text,
                MaximumActionTextLength,
                required: true,
                "invalid_todo_action_text",
                "Todo action text");
            var tooltip = NormalizeText(
                source.ToolTip,
                MaximumToolTipLength,
                required: false,
                "invalid_todo_action_tooltip",
                "Todo action tooltip");
            var icon = NormalizeIcon(source.Icon, required: true, "invalid_todo_action_icon");
            const PaperTodoActionPlacement supported =
                PaperTodoActionPlacement.Inline |
                PaperTodoActionPlacement.ContextMenu;
            if (source.Placement == PaperTodoActionPlacement.None ||
                (source.Placement & ~supported) != 0)
            {
                throw new PaperTodoPluginException(
                    "invalid_todo_action_placement",
                    "Todo action placement must include inline and/or contextMenu.");
            }

            result.Add(source with
            {
                Id = id,
                Text = text,
                ToolTip = tooltip,
                Icon = icon,
                Placement = source.Placement & supported
            });
        }
        return result.ToArray();
    }

    internal static PaperTopBarLabel[] NormalizeTopBarLabels(
        IReadOnlyList<PaperTopBarLabel>? labels)
    {
        labels ??= [];
        if (labels.Count > MaximumTopBarLabelsPerPaper)
        {
            throw new PaperTodoPluginException(
                "too_many_topbar_labels",
                $"A plugin can contribute at most {MaximumTopBarLabelsPerPaper} top-bar labels to one Paper.");
        }

        var result = new List<PaperTopBarLabel>(labels.Count);
        foreach (var label in labels)
        {
            if (label == null)
            {
                throw new PaperTodoPluginException(
                    "invalid_topbar_label",
                    "Top-bar labels cannot contain null entries.");
            }

            result.Add(label with
            {
                Text = NormalizeText(
                    label.Text,
                    MaximumLabelTextLength,
                    required: true,
                    "invalid_topbar_label_text",
                    "Top-bar label text"),
                ToolTip = NormalizeText(
                    label.ToolTip,
                    MaximumToolTipLength,
                    required: false,
                    "invalid_topbar_label_tooltip",
                    "Top-bar label tooltip"),
                Icon = label.Icon == null
                    ? null
                    : NormalizeIcon(label.Icon, required: false, "invalid_topbar_label_icon")
            });
        }

        return result
            .OrderByDescending(label => label.Priority)
            .ToArray();
    }

    private static string NormalizeIdentifier(
        string? value,
        string code,
        string noun)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > MaximumIdentifierLength ||
            normalized.Any(ch =>
                !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')))
        {
            throw new PaperTodoPluginException(
                code,
                $"{noun} ids must contain 1-{MaximumIdentifierLength} ASCII letters, digits, '.', '_' or '-'.");
        }
        return normalized;
    }

    private static string NormalizeText(
        string? value,
        int maximumLength,
        bool required,
        string code,
        string noun)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if ((required && normalized.Length == 0) ||
            normalized.Length > maximumLength ||
            normalized.Any(char.IsControl))
        {
            throw new PaperTodoPluginException(
                code,
                $"{noun} must be {(required ? "1-" : "0-")}{maximumLength} characters and contain no control characters.");
        }
        return normalized;
    }

    private static PaperTopBarIcon NormalizeIcon(
        PaperTopBarIcon? source,
        bool required,
        string code)
    {
        if (source == null)
        {
            if (!required)
            {
                return new PaperTopBarIcon();
            }
            throw new PaperTodoPluginException(code, "An icon is required.");
        }

        var value = source.Value?.Trim() ?? string.Empty;
        switch (source.Kind)
        {
            case PaperTopBarIconKind.Character:
                if (value.Length == 0 && !required)
                {
                    return source with { Value = string.Empty };
                }
                if (value.Length is 0 or > MaximumCharacterIconLength || value.Any(char.IsControl))
                {
                    throw new PaperTodoPluginException(
                        code,
                        $"Character icons must contain 1-{MaximumCharacterIconLength} UTF-16 characters and no control characters.");
                }
                break;

            case PaperTopBarIconKind.SvgPath:
                if (value.Length is 0 or > MaximumSvgPathLength)
                {
                    throw new PaperTodoPluginException(
                        code,
                        $"SVG path data must contain 1-{MaximumSvgPathLength} characters.");
                }
                if (!Enum.IsDefined(source.RenderMode))
                {
                    throw new PaperTodoPluginException(code, "Unknown SVG render mode.");
                }
                if (source.RenderMode == PaperTopBarSvgRenderMode.Stroke &&
                    (!double.IsFinite(source.StrokeWidth) ||
                     source.StrokeWidth < MinimumSvgStrokeWidth ||
                     source.StrokeWidth > MaximumSvgStrokeWidth))
                {
                    throw new PaperTodoPluginException(
                        code,
                        $"SVG strokeWidth must be between {MinimumSvgStrokeWidth} and {MaximumSvgStrokeWidth}.");
                }
                try
                {
                    _ = Geometry.Parse(value);
                }
                catch (Exception ex)
                {
                    throw new PaperTodoPluginException(
                        code,
                        $"SVG path data is invalid: {ex.GetBaseException().Message}");
                }
                break;

            default:
                throw new PaperTodoPluginException(code, "Unknown icon kind.");
        }

        return source with { Value = value };
    }
}
