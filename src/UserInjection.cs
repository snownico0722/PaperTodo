using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed class UserInjectionException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}

/// <summary>
/// Process-wide sparse overrides. File work, reflection and string-key lookup stay at the
/// startup/inspection/Apply boundary; application consumers hold typed slots directly.
/// </summary>
internal static class UserInjection
{
    private static readonly Lazy<Dictionary<string, InjectionParameter>> Catalog = new(() =>
        typeof(InjectionParameters).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => typeof(InjectionParameter).IsAssignableFrom(field.FieldType))
            .Select(field => (InjectionParameter)field.GetValue(null)!)
            .ToDictionary(parameter => parameter.Id, StringComparer.Ordinal));
    private static readonly Lazy<JsonSerializerOptions> WriteOptions = new(() => new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    });
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
    private static string _filePath = Path.Combine(AppContext.BaseDirectory, "user.json");

    internal static Func<string, string, string>? TextResolver { get; set; }
    internal static string FilePath => _filePath;
    internal static bool HasOverride(string id) =>
        Catalog.IsValueCreated && Catalog.Value.TryGetValue(id, out var parameter) && parameter.HasOverride;

    // Call before constructing AppController: its instance fields may create objects
    // whose dimensions or capacities are intentionally bound only on construction.
    internal static void LoadStartup(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
        var file = ReadFile();
        if (file.Values.Count == 0 && !Catalog.IsValueCreated) return;
        Adopt(file, startup: true, applyRuntime: null);
    }

    internal static PaperInjectionSnapshot Read() => Snapshot(ReadFile());

    internal static PaperInjectionApplyResult Apply(PaperInjectionPatch? patch,
        Action<IReadOnlySet<string>> applyRuntime)
    {
        var file = ReadFile();
        var edits = patch != null && (patch.ResetAll || patch.Set.Count != 0 || patch.Remove.Count != 0);
        if (edits)
        {
            if (!string.Equals(patch!.ExpectedRevision, file.Revision, StringComparison.Ordinal))
                throw FileChanged();
            var values = patch.ResetAll
                ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                : new Dictionary<string, JsonElement>(file.Values, StringComparer.Ordinal);
            foreach (var key in patch.Remove) values.Remove(key);
            foreach (var pair in patch.Set) values[pair.Key] = pair.Value.Clone();
            // Type conversion finishes before writing. Suggested ranges deliberately do not
            // participate: technically representable injections are the user's choice.
            _ = Prepare(values);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(values, WriteOptions.Value);
            var expected = file.Revision;
            DurableAtomicFileWriter.Shared.Write(_filePath, bytes, beforeReplace: () =>
            {
                if (!string.Equals(ReadRevision(), expected, StringComparison.Ordinal)) throw FileChanged();
            });
            file = new FileSnapshot(Revision(bytes), values);
        }
        var changed = Adopt(file, startup: false, applyRuntime);
        return new PaperInjectionApplyResult(Snapshot(file), changed.ToArray());
    }

    internal static PaperInjectionSuggestion? Suggest(string id, JsonElement value,
        IReadOnlyDictionary<string, JsonElement>? draftOverrides = null)
    {
        if (!Catalog.Value.TryGetValue(id, out var parameter) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var number) || !double.IsFinite(number)) return null;
        var suggested = number;
        var explanationKey = "suggestion.range";
        var explanation = "调整到声明的建议范围内，仍保留为显式覆盖。";
        if (parameter.RecommendedMinimum is { } minimum) suggested = Math.Max(minimum, suggested);
        if (parameter.RecommendedMaximum is { } maximum) suggested = Math.Min(maximum, suggested);

        // A few local relationships are useful without building an optimizer. The optional map
        // is the caller's complete proposed file: an omitted key has its original default, even
        // when the running process still has an older override for that key.
        IReadOnlyDictionary<string, JsonElement>? proposed = draftOverrides;
        double? RelatedNumber(string key)
        {
            proposed ??= ReadFile().Values;
            var json = proposed.TryGetValue(key, out var candidate) ? candidate :
                Catalog.Value.TryGetValue(key, out var related) ? related.DefaultValue : null;
            return json is { ValueKind: JsonValueKind.Number } element &&
                element.TryGetDouble(out var result) && double.IsFinite(result) ? result : null;
        }

        switch (id)
        {
            case "edge.queue.releaseDistanceDip":
                if (RelatedNumber("edge.queue.snapDistanceDip") is { } snap && suggested < snap)
                {
                    suggested = snap;
                    explanationKey = "suggestion.queueRelease";
                    explanation = "让队列释放距离不小于吸附距离。";
                }
                break;
            case "edge.queue.snapDistanceDip":
                if (RelatedNumber("edge.queue.releaseDistanceDip") is { } release && suggested > release)
                {
                    suggested = release;
                    explanationKey = "suggestion.queueSnap";
                    explanation = "让队列吸附距离不大于释放距离。";
                }
                break;
            case "note.images.autoCompressedDimensionPx":
                if (RelatedNumber("note.images.maxStoredDimensionPx") is { } stored && stored > 0 && suggested > stored)
                {
                    suggested = stored;
                    explanationKey = "suggestion.imageCompression";
                    explanation = "让自动压缩目标边长不超过当前图片存储边长上限。";
                }
                break;
            case "capsule.labelFontDip":
                if (RelatedNumber("window.layout.capsuleHeightDip") is { } height &&
                    RelatedNumber("edge.layout.windowChromeMargin") is { } margin)
                {
                    var bodyHeight = height - 2 * margin;
                    if (bodyHeight > 0 && suggested > bodyHeight)
                    {
                        suggested = bodyHeight;
                        explanationKey = "suggestion.capsuleFont";
                        explanation = "按胶囊内容区高度缩小字号。这只是按字身高度的粗调，不保证所有字体都不会裁切。";
                    }
                }
                break;
        }
        if (suggested == number) return null;
        var replacement = parameter.NumericSuggestion(suggested);
        return replacement == null ? null : new PaperInjectionSuggestion(id, replacement.Value,
            Text(explanationKey, explanation));
    }

    private static HashSet<string> Adopt(FileSnapshot file, bool startup,
        Action<IReadOnlySet<string>>? applyRuntime)
    {
        var prepared = Prepare(file.Values);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var adopted = new List<(InjectionParameter Parameter, JsonElement? Value)>();
        foreach (var parameter in Catalog.Value.Values)
        {
            var next = file.Values.TryGetValue(parameter.Id, out var value) ? value : (JsonElement?)null;
            if (!startup && (parameter.Application == "restart" ||
                Same(parameter.AppliedOverride, next) && Same(parameter.BoundOverride, next)))
                continue;
            parameter.BindOverride(next.HasValue ? prepared[parameter.Id] : null, next);
            adopted.Add((parameter, next));
            changed.Add(parameter.Id);
        }
        if (changed.Count != 0) applyRuntime?.Invoke(changed);
        // An unsuccessful runtime refresh must not be reported as a green applied value.
        // There is no rollback or attempt to repair dangerous user configurations.
        foreach (var (parameter, value) in adopted) parameter.MarkApplied(value);
        foreach (var parameter in Catalog.Value.Values)
            parameter.MarkAccepted(file.Values.TryGetValue(parameter.Id, out var value) ? value : null);
        return changed;
    }

    private static Dictionary<string, object> Prepare(IReadOnlyDictionary<string, JsonElement> values)
    {
        var prepared = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            if (!Catalog.Value.TryGetValue(pair.Key, out var parameter)) continue;
            try { prepared[pair.Key] = parameter.Parse(pair.Value); }
            catch (Exception ex) when (ex is JsonException or FormatException or OverflowException)
            {
                throw new UserInjectionException("injection_value_invalid", $"{pair.Key}: {ex.Message}");
            }
        }
        return prepared;
    }

    private static PaperInjectionSnapshot Snapshot(FileSnapshot file)
    {
        var rows = Catalog.Value.Values.Select(parameter =>
        {
            var hasFileValue = file.Values.TryGetValue(parameter.Id, out var fileValue);
            var requested = hasFileValue ? fileValue : (JsonElement?)null;
            var status = Same(parameter.AppliedOverride, requested) && Same(parameter.BoundOverride, requested)
                ? hasFileValue ? "applied" : "default"
                : parameter.Application == "restart" && Same(parameter.AcceptedOverride, requested)
                    ? "restart" : "pending";
            var outside = hasFileValue && fileValue.ValueKind == JsonValueKind.Number &&
                fileValue.TryGetDouble(out var number) &&
                (parameter.RecommendedMinimum is { } minimum && number < minimum ||
                 parameter.RecommendedMaximum is { } maximum && number > maximum);
            return new PaperInjectionParameterSnapshot
            {
                Id = parameter.Id,
                Title = Text(parameter.Id + ".title", parameter.Title),
                Description = Text(parameter.Id + ".description", parameter.Description),
                Type = parameter.Type,
                Unit = parameter.Unit,
                DefaultValue = parameter.DefaultValue,
                DefaultDescription = parameter.DefaultDescription == null ? null :
                    Text(parameter.Id + ".defaultDescription", parameter.DefaultDescription),
                Application = parameter.Application,
                HasFileOverride = hasFileValue,
                FileValue = requested,
                HasAppliedOverride = parameter.AppliedOverride.HasValue,
                AppliedValue = parameter.AppliedOverride,
                Status = status,
                RecommendedMinimum = parameter.RecommendedMinimum,
                RecommendedMaximum = parameter.RecommendedMaximum,
                OutsideRecommendedRange = outside
            };
        }).ToList();
        foreach (var pair in file.Values.Where(pair => !Catalog.Value.ContainsKey(pair.Key)))
            rows.Add(new PaperInjectionParameterSnapshot
            {
                Id = pair.Key, Title = pair.Key, Type = "unknown", Known = false,
                Description = Text("unknown.description", "当前版本没有此名称的注入点。"),
                Application = "unknown", HasFileOverride = true, FileValue = pair.Value, Status = "pending"
            });
        return new PaperInjectionSnapshot
        {
            FilePath = _filePath,
            Revision = file.Revision,
            Overrides = file.Values,
            Parameters = rows.OrderBy(row => row.Id, StringComparer.Ordinal).ToArray()
        };
    }

    private static FileSnapshot ReadFile()
    {
        var bytes = File.Exists(_filePath) ? File.ReadAllBytes(_filePath) : null;
        if (bytes == null) return new FileSnapshot("missing", new(StringComparer.Ordinal));
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        if (string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset)))
            return new FileSnapshot(Revision(bytes), values);
        try
        {
            using var document = JsonDocument.Parse(bytes.AsMemory(offset), ReadOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("user.json must contain an object of named overrides.");
            foreach (var property in document.RootElement.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        }
        catch (JsonException ex)
        {
            throw new UserInjectionException("injection_file_invalid", ex.Message);
        }
        return new FileSnapshot(Revision(bytes), values);
    }

    private static string ReadRevision() =>
        File.Exists(_filePath) ? Revision(File.ReadAllBytes(_filePath)) : "missing";
    private static string Revision(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool Same(JsonElement? first, JsonElement? second) =>
        first.HasValue == second.HasValue && (!first.HasValue || JsonElement.DeepEquals(first.Value, second!.Value));
    private static UserInjectionException FileChanged() => new("injection_conflict",
        "user.json changed outside this page. Read the latest file before applying the draft again.");
    private static string Text(string key, string fallback) => TextResolver?.Invoke(key, fallback) ?? fallback;
    private sealed record FileSnapshot(string Revision, Dictionary<string, JsonElement> Values);
}
