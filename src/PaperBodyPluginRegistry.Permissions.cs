using System.Collections.Frozen;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperBodyPluginRegistry
{
    private static IReadOnlySet<string> ParsePermissions(IEnumerable<string>? values)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in values ?? [])
        {
            var value = raw?.Trim() ?? "";
            if (value.Length == 0)
            {
                continue;
            }
            if (!PaperTodoPermissionNames.All.Contains(value))
            {
                throw new InvalidDataException(
                    $"Unknown plugin permission '{value}'.");
            }
            result.Add(value);
        }
        return result.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Canonicalizes manifest capability names exactly once before any feature-specific validator
    /// consumes them. Unknown values are rejected instead of being silently ignored, so a typo
    /// cannot produce a plugin that loads successfully with a missing feature.
    /// </summary>
    private static void NormalizeProtocolFeatures(PaperBodyPluginManifest manifest)
    {
        manifest.Capabilities ??= [];
        var normalized = new List<string>(manifest.Capabilities.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in manifest.Capabilities)
        {
            var value = raw?.Trim() ?? "";
            if (value.Length == 0)
            {
                continue;
            }

            var canonical = value.ToLowerInvariant() switch
            {
                "textzoom" => "textZoom",
                "notelinks" => "noteLinks",
                "runtime" => "runtime",
                _ => throw new InvalidDataException(
                    $"Unknown plugin capability '{value}'.")
            };
            if (seen.Add(canonical))
            {
                normalized.Add(canonical);
            }
        }

        manifest.Capabilities = normalized.ToArray();
    }

    private static void ValidateProtocolFeatures(PaperBodyPluginManifest manifest)
    {
        manifest.Permissions ??= [];
        NormalizeProtocolFeatures(manifest);
        if (manifest.MaxPaperInstances < 0)
        {
            throw new InvalidDataException(
                "maxPaperInstances must be 0 (unlimited) or a positive integer.");
        }
    }

    private static void ValidateDeclaredDefault(
        PaperBodyPluginSettingManifest setting)
    {
        var value = setting.Default;
        switch (setting.Type)
        {
            case "boolean" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return;

            case "string" when value.ValueKind == JsonValueKind.String:
                var text = value.GetString() ?? "";
                if (setting.MaxLength is > 0 && text.Length > setting.MaxLength.Value)
                {
                    throw new InvalidDataException(
                        $"Plugin setting '{setting.Id}' default exceeds maxLength.");
                }
                return;

            case "number" when value.ValueKind == JsonValueKind.Number &&
                               value.TryGetDouble(out var number) &&
                               double.IsFinite(number):
                if (setting.Min.HasValue && number < setting.Min.Value ||
                    setting.Max.HasValue && number > setting.Max.Value)
                {
                    throw new InvalidDataException(
                        $"Plugin setting '{setting.Id}' default is outside its range.");
                }
                if (setting.Step is > 0)
                {
                    var origin = setting.Min ?? 0;
                    var steps = (number - origin) / setting.Step.Value;
                    if (Math.Abs(steps - Math.Round(steps)) > 1e-9)
                    {
                        throw new InvalidDataException(
                            $"Plugin setting '{setting.Id}' default is not aligned to step.");
                    }
                }
                return;

            case "select" when value.ValueKind == JsonValueKind.String:
                var selected = value.GetString() ?? "";
                if (!setting.Options.Any(option =>
                        string.Equals(option.Value, selected, StringComparison.Ordinal)))
                {
                    throw new InvalidDataException(
                        $"Plugin setting '{setting.Id}' default is not a declared option.");
                }
                return;

            case "shortcut" when value.ValueKind == JsonValueKind.String:
                var shortcut = value.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(shortcut))
                {
                    return;
                }
                if (!ShortcutGesture.TryParse(shortcut, out var gesture) || gesture.Key == Key.None)
                {
                    throw new InvalidDataException(
                        $"Plugin setting '{setting.Id}' default is not a valid shortcut gesture.");
                }
                return;
        }

        throw new InvalidDataException(
            $"Plugin setting '{setting.Id}' default does not match type '{setting.Type}'.");
    }
}
