using System.Collections.Frozen;
using System.IO;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed class PaperBodyPluginStartupManifest
{
    // Startup timing is host-owned; plugins only declare intent and presentation.
    public string EnabledSetting { get; set; } = "";
    public string InstanceKey { get; set; } = "main";
    public string Presentation { get; set; } = "capsule";
    public string Title { get; set; } = "";
}

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
        if (!ApiAtLeast(manifest.ApiVersion, "2.2") &&
            manifest.Permissions.Any(permission =>
                (permission?.Trim() ?? "").StartsWith("settings.", StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "Application settings permissions require apiVersion 2.2 or later.");
        }
        if (manifest.MaxPaperInstances < 0)
        {
            throw new InvalidDataException(
                "maxPaperInstances must be 0 (unlimited) or a positive integer.");
        }
    }

    private static void ValidateStartupPaper(PaperBodyPluginManifest manifest)
    {
        var startup = manifest.StartupPaper;
        if (startup == null)
        {
            return;
        }

        startup.EnabledSetting = startup.EnabledSetting?.Trim() ?? "";
        startup.InstanceKey = startup.InstanceKey?.Trim() ?? "";
        startup.Presentation = startup.Presentation?.Trim().ToLowerInvariant() ?? "";
        startup.Title = startup.Title?.Trim() ?? "";

        if (startup.InstanceKey.Length is < 1 or > 80 ||
            !startup.InstanceKey.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-'))
        {
            throw new InvalidDataException(
                "startupPaper.instanceKey must contain 1-80 ASCII letters, digits, '.', '_' or '-'.");
        }
        if (startup.Presentation is not ("capsule" or "expanded" or "hidden"))
        {
            throw new InvalidDataException(
                "startupPaper.presentation must be 'capsule', 'expanded', or 'hidden'.");
        }
        if (startup.Presentation == "hidden" && !ApiAtLeast(manifest.ApiVersion, "2.2"))
        {
            throw new InvalidDataException(
                "startupPaper.presentation='hidden' requires apiVersion 2.2 or later.");
        }
        if (startup.Title.Length > 120)
        {
            throw new InvalidDataException(
                "startupPaper.title cannot exceed 120 characters.");
        }
        if (startup.EnabledSetting.Length == 0)
        {
            throw new InvalidDataException(
                "startupPaper.enabledSetting must name a boolean plugin setting.");
        }

        var setting = manifest.Settings.FirstOrDefault(item =>
            string.Equals(
                item.Id,
                startup.EnabledSetting,
                StringComparison.Ordinal));
        if (setting == null || setting.Type != "boolean")
        {
            throw new InvalidDataException(
                $"startupPaper.enabledSetting '{startup.EnabledSetting}' must reference a boolean setting.");
        }
    }
}
