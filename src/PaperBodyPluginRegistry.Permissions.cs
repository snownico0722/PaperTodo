using System.Collections.Frozen;
using System.IO;
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
}
