using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Input;

namespace PaperTodo;

internal sealed partial class PaperBodyPluginRegistry
{
    private static readonly Regex SettingIdPattern = new(
        "^[A-Za-z0-9._-]{1,80}$",
        RegexOptions.CultureInvariant);

    private readonly PaperBodyPluginDataStore _dataStore =
        new(Path.Combine(AppContext.BaseDirectory, "plugins"));

    internal PaperBodyPluginDataStore DataStore => _dataStore;

    private static void ValidateSettings(PaperBodyPluginManifest manifest)
    {
        manifest.Settings ??= [];
        manifest.SettingCategories ??= [];
        if (!manifest.AdvancedSettings &&
            (manifest.PrimarySettings.HasValue || manifest.SettingCategories.Length > 0))
        {
            throw new InvalidDataException(
                "primarySettings and settingCategories require advancedSettings: true.");
        }
        if (manifest.PrimarySettings is < 1 or > 3)
        {
            throw new InvalidDataException("primarySettings must be between 1 and 3.");
        }

        var categoryNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var category in manifest.SettingCategories)
        {
            category.Name = category.Name?.Trim() ?? "";
            category.Column = category.Column?.Trim().ToLowerInvariant() ?? "";
            if (category.Name.Length == 0 || !categoryNames.Add(category.Name))
            {
                throw new InvalidDataException(
                    "Plugin setting categories must have unique non-empty names.");
            }
            if (category.Column is not ("" or "left" or "right"))
            {
                throw new InvalidDataException(
                    $"Plugin setting category '{category.Name}' has unsupported column '{category.Column}'.");
            }
        }
        // Settings may depend on provider-level capabilities (for example custom shortcut actions
        // require runtime). Normalize and validate the capability list before reading it here so
        // every feature consumes one canonical manifest representation.
        NormalizeProtocolFeatures(manifest);
        var hasPluginRuntime = manifest.Capabilities.Contains("runtime", StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var quickCount = 0;
        foreach (var setting in manifest.Settings)
        {
            setting.Id = setting.Id?.Trim() ?? "";
            setting.Type = setting.Type?.Trim().ToLowerInvariant() ?? "";
            setting.Name = string.IsNullOrWhiteSpace(setting.Name)
                ? setting.Id
                : setting.Name.Trim();
            setting.Description = setting.Description?.Trim() ?? "";
            setting.Category = setting.Category?.Trim() ?? "";
            setting.Suffix = setting.Suffix?.Trim() ?? "";
            setting.Placeholder = setting.Placeholder?.Trim() ?? "";
            setting.ShortcutAction = string.IsNullOrWhiteSpace(setting.ShortcutAction)
                ? PluginShortcutActions.Default
                : PluginShortcutActions.Normalize(setting.ShortcutAction);
            setting.Options ??= [];

            if (setting.Category.Length > 0 && !manifest.AdvancedSettings)
            {
                throw new InvalidDataException(
                    $"Plugin setting '{setting.Id}' category requires advancedSettings: true.");
            }

            if (!SettingIdPattern.IsMatch(setting.Id) || !ids.Add(setting.Id))
            {
                throw new InvalidDataException(
                    $"Plugin setting id '{setting.Id}' is invalid or duplicated.");
            }
            if (setting.Type is not ("boolean" or "string" or "number" or "select" or "shortcut"))
            {
                throw new InvalidDataException(
                    $"Plugin setting '{setting.Id}' has unsupported type '{setting.Type}'.");
            }
            if (setting.Type == "shortcut" && setting.ShortcutAction.Length == 0)
            {
                throw new InvalidDataException(
                    $"Plugin shortcut setting '{setting.Id}' has an invalid shortcutAction.");
            }
            if (setting.Type == "shortcut" &&
                PluginShortcutActions.IsCustomAction(setting.ShortcutAction) &&
                !hasPluginRuntime)
            {
                throw new InvalidDataException(
                    $"Plugin shortcut setting '{setting.Id}' uses custom action '{setting.ShortcutAction}' but the plugin does not declare the runtime capability.");
            }
            if (!manifest.AdvancedSettings && setting.Quick && ++quickCount > 3)
            {
                throw new InvalidDataException("A plugin may expose at most three quick settings.");
            }
            if (setting.MaxLength is < 0)
            {
                throw new InvalidDataException(
                    $"Plugin setting '{setting.Id}' maxLength cannot be negative.");
            }
            if (setting.Min.HasValue && setting.Max.HasValue && setting.Min > setting.Max)
            {
                throw new InvalidDataException(
                    $"Plugin setting '{setting.Id}' min cannot exceed max.");
            }
            if (setting.Step is <= 0)
            {
                throw new InvalidDataException(
                    $"Plugin setting '{setting.Id}' step must be greater than zero.");
            }

            if (setting.Type == "select")
            {
                if (setting.Options.Length == 0)
                {
                    throw new InvalidDataException(
                        $"Select setting '{setting.Id}' must declare at least one option.");
                }
                var optionValues = new HashSet<string>(StringComparer.Ordinal);
                foreach (var option in setting.Options)
                {
                    option.Value = option.Value?.Trim() ?? "";
                    option.Name = string.IsNullOrWhiteSpace(option.Name)
                        ? option.Value
                        : option.Name.Trim();
                    if (option.Value.Length == 0 || !optionValues.Add(option.Value))
                    {
                        throw new InvalidDataException(
                            $"Select setting '{setting.Id}' contains an empty or duplicated option.");
                    }
                }
            }

            if (setting.Default.ValueKind != JsonValueKind.Undefined)
            {
                ValidateDeclaredDefault(setting);
            }
        }
    }

    internal static JsonElement DefaultSettingValue(PaperBodyPluginSettingManifest setting)
    {
        if (setting.Default.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            return NormalizeSettingValue(setting, setting.Default);
        }

        return DefaultSettingValueWithoutDeclaredDefault(setting);
    }

    internal static JsonElement NormalizeSettingValue(
        PaperBodyPluginSettingManifest setting,
        JsonElement value)
    {
        return setting.Type switch
        {
            "boolean" when value.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                JsonSerializer.SerializeToElement(value.GetBoolean()),
            "string" when value.ValueKind == JsonValueKind.String =>
                JsonSerializer.SerializeToElement(NormalizeString(setting, value.GetString() ?? "")),
            "number" when value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) =>
                JsonSerializer.SerializeToElement(NormalizeNumber(setting, number)),
            "select" when value.ValueKind == JsonValueKind.String =>
                NormalizeSelect(setting, value.GetString() ?? ""),
            "shortcut" when value.ValueKind == JsonValueKind.String =>
                JsonSerializer.SerializeToElement(NormalizeShortcut(value.GetString() ?? "")),
            _ => DefaultSettingValueWithoutDeclaredDefault(setting)
        };
    }

    private static JsonElement DefaultSettingValueWithoutDeclaredDefault(
        PaperBodyPluginSettingManifest setting) => setting.Type switch
    {
        "boolean" => JsonSerializer.SerializeToElement(false),
        "number" => JsonSerializer.SerializeToElement(NormalizeNumber(setting, 0d)),
        "select" => JsonSerializer.SerializeToElement(setting.Options[0].Value),
        "shortcut" => JsonSerializer.SerializeToElement(""),
        _ => JsonSerializer.SerializeToElement(NormalizeString(setting, ""))
    };

    private static string NormalizeString(
        PaperBodyPluginSettingManifest setting,
        string value)
    {
        if (setting.MaxLength is > 0 && value.Length > setting.MaxLength.Value)
        {
            return value[..setting.MaxLength.Value];
        }
        return value;
    }

    private static string NormalizeShortcut(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return ShortcutGesture.TryParse(value, out var gesture) && gesture.Key != Key.None
            ? gesture.ToStorageString()
            : "";
    }

    private static double NormalizeNumber(
        PaperBodyPluginSettingManifest setting,
        double value)
    {
        if (!double.IsFinite(value))
        {
            value = 0;
        }
        if (setting.Min.HasValue)
        {
            value = Math.Max(setting.Min.Value, value);
        }
        if (setting.Max.HasValue)
        {
            value = Math.Min(setting.Max.Value, value);
        }
        if (setting.Step is > 0)
        {
            var origin = setting.Min ?? 0;
            value = origin + Math.Round(
                (value - origin) / setting.Step.Value,
                MidpointRounding.AwayFromZero) * setting.Step.Value;
            if (setting.Min.HasValue)
            {
                value = Math.Max(setting.Min.Value, value);
            }
            if (setting.Max.HasValue)
            {
                value = Math.Min(setting.Max.Value, value);
            }
        }
        return value;
    }

    private static JsonElement NormalizeSelect(
        PaperBodyPluginSettingManifest setting,
        string value)
    {
        var selected = setting.Options.Any(option =>
            string.Equals(option.Value, value, StringComparison.Ordinal))
            ? value
            : setting.Options[0].Value;
        return JsonSerializer.SerializeToElement(selected);
    }
}

internal sealed class PaperBodyPluginSettingManifest
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public JsonElement Default { get; set; }
    public bool Quick { get; set; }
    public string Category { get; set; } = "";
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Step { get; set; }
    public int? MaxLength { get; set; }
    public string Suffix { get; set; } = "";
    public string Placeholder { get; set; } = "";
    public string ShortcutAction { get; set; } = PluginShortcutActions.Default;
    public PaperBodyPluginSettingOptionManifest[] Options { get; set; } = [];
}

internal sealed class PaperBodyPluginSettingCategoryManifest
{
    public string Name { get; set; } = "";
    public string Column { get; set; } = "";
}

internal sealed class PaperBodyPluginSettingOptionManifest
{
    public string Value { get; set; } = "";
    public string Name { get; set; } = "";
}
