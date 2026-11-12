using System.Reflection;
using System.Text;
using System.Text.Json;
using PaperTodo;
using PaperTodo.Plugin;

// Data-driven contract campaign against the production sources, not a parallel implementation.
// Consumer/UI behavior is intentionally checked separately in the real WPF host.
internal static class SystematicChecks
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "PaperTodo.InjectionSystem", Guid.NewGuid().ToString("N"));
    private static readonly string FilePath = Path.Combine(Root, "user.json");
    private static readonly InjectionParameter[] Slots = typeof(InjectionParameters)
        .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(f => typeof(InjectionParameter).IsAssignableFrom(f.FieldType))
        .Select(f => (InjectionParameter)f.GetValue(null)!).OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
    private static int _cases, _assertions;
    private static readonly List<string> Failures = [];
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static int Main()
    {
        Directory.CreateDirectory(Root);
        try
        {
            Case("catalog / machine-readable metadata and both languages", Catalog);
            foreach (var p in Slots)
            {
                Case("lifecycle / " + p.Id, () => Lifecycle(p));
                Case("type rejection / " + p.Id, () => InvalidTypes(p));
                if (p.RecommendedMinimum.HasValue || p.RecommendedMaximum.HasValue)
                    Case("advisory ranges / " + p.Id, () => AdvisoryRanges(p));
            }
            Case("JSON / BOM, comments, trailing commas, duplicate and unknown keys", JsonFiles);
            Case("JSON / malformed documents preserve runtime and original bytes", MalformedFiles);
            Case("patch / conflict, unknown preservation, reset and set precedence", PatchBehavior);
            Case("failure / retry, removal and unchanged restart targets", RefreshFailure);
            Case("mixed state machine / 300 deterministic operations", StateMachine);
            Console.WriteLine($"SYSTEMATIC_RESULT parameters={Slots.Length} dynamic={Slots.Count(p => p.DefaultValue == null)} " +
                $"cases={_cases} assertions={_assertions} failures={Failures.Count}");
            foreach (var group in Slots.GroupBy(p => p.Application))
                Console.WriteLine($"APPLICATION {group.Key}={group.Count()}");
            foreach (var failure in Failures) Console.Error.WriteLine("FAIL " + failure);
            return Failures.Count == 0 ? 0 : 1;
        }
        finally
        {
            File.Delete(FilePath);
            UserInjection.LoadStartup(FilePath);
            Directory.Delete(Root, recursive: true);
        }
    }

    private static void Case(string name, Action test)
    {
        _cases++;
        try { Reset(); test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { Failures.Add(name + ": " + ex.GetBaseException().Message); }
    }

    private static void Reset()
    {
        File.Delete(FilePath);
        UserInjection.LoadStartup(FilePath);
    }

    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Raw(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }
    private static void Require(bool ok, string message)
    {
        _assertions++;
        if (!ok) throw new InvalidOperationException(message);
    }
    private static bool Same(JsonElement? a, JsonElement? b) => a.HasValue == b.HasValue &&
        (!a.HasValue || JsonElement.DeepEquals(a.Value, b!.Value));
    private static object? Value(InjectionParameter p) => p.GetType().GetProperty("Value", Instance)!.GetValue(p);
    private static object? Resolve(InjectionParameter p, JsonElement original) =>
        p.GetType().GetMethod("Resolve", Instance)!.Invoke(p, [p.Parse(original)]);
    private static PaperInjectionParameterSnapshot Row(PaperInjectionSnapshot s, string id) => s.Parameters.Single(p => p.Id == id);
    private static PaperInjectionApplyResult Apply(PaperInjectionPatch? patch = null) => UserInjection.Apply(patch, _ => { });
    private static PaperInjectionApplyResult Set(string id, JsonElement value) => Apply(new()
    {
        ExpectedRevision = UserInjection.Read().Revision,
        Set = new Dictionary<string, JsonElement> { [id] = value }
    });
    private static PaperInjectionApplyResult Remove(string id) => Apply(new()
    {
        ExpectedRevision = UserInjection.Read().Revision, Remove = [id]
    });
    private static void Write(IReadOnlyDictionary<string, JsonElement> values) =>
        File.WriteAllText(FilePath, JsonSerializer.Serialize(values));
    private static void Error(string code, Action action)
    {
        try { action(); }
        catch (UserInjectionException ex) { Require(ex.Code == code, $"Expected {code}, got {ex.Code}."); return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static JsonElement Probe(InjectionParameter p, int variant = 0) => p.Type switch
    {
        "boolean" => J(variant % 2 == 0),
        "string" => J("probe-" + variant),
        "integer" => J(27 + variant),
        "number" => J(27.25 + variant),
        _ => throw new InvalidOperationException("Unknown type: " + p.Type)
    };

    private static void Catalog()
    {
        var snapshot = UserInjection.Read();
        Require(Slots.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() == Slots.Length, "Duplicate registered IDs.");
        Require(snapshot.Parameters.Count == Slots.Length && snapshot.Overrides.Count == 0, "Catalog and runtime disagree.");
        Require(!File.Exists(FilePath), "Inspection created user.json.");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "user-injection-catalog.json")));
        var rows = doc.RootElement.GetProperty("parameters").EnumerateArray().ToArray();
        var byId = rows.ToDictionary(p => p.GetProperty("id").GetString()!, StringComparer.Ordinal);
        Require(byId.Keys.Order().SequenceEqual(Slots.Select(p => p.Id).Order()), "Machine catalog key set differs from the actual registry.");
        foreach (var p in Slots)
        {
            var row = byId[p.Id];
            Require(row.GetProperty("type").GetString() == p.Type, p.Id + " type drift");
            Require(row.GetProperty("application").GetString() == p.Application, p.Id + " application drift");
            Require(row.GetProperty("unit").GetString() == p.Unit, p.Id + " unit drift");
            Require(p.Application is "live" or "next_use" or "restart", p.Id + " invalid lifecycle");
            JsonElement? documented = row.GetProperty("default_value");
            if (documented.Value.ValueKind == JsonValueKind.Null) documented = null;
            Require(Same(documented, p.DefaultValue), p.Id + " default drift");
            Require(row.GetProperty("recommended_minimum").Deserialize<double?>() == p.RecommendedMinimum &&
                    row.GetProperty("recommended_maximum").Deserialize<double?>() == p.RecommendedMaximum,
                p.Id + " advisory range drift");
            if (p.DefaultValue is { } value) Require(Equals(Value(p), p.Parse(value)), p.Id + " default not consumed by slot");
            var text = row.GetProperty("text");
            foreach (var language in new[] { "zh-CN", "en-US" })
            {
                var localized = text.GetProperty(language);
                Require(!string.IsNullOrWhiteSpace(localized.GetProperty("title").GetString()), p.Id + " missing " + language + " title");
                Require(!string.IsNullOrWhiteSpace(localized.GetProperty("description").GetString()), p.Id + " missing " + language + " description");
            }
            Require(text.GetProperty("zh-CN").GetProperty("title").GetString() == p.Title &&
                    text.GetProperty("zh-CN").GetProperty("description").GetString() == p.Description,
                p.Id + " Chinese metadata drift");
        }
    }

    private static void Lifecycle(InjectionParameter p)
    {
        var baseline = Value(p);
        var requested = Probe(p);
        Write(new Dictionary<string, JsonElement> { [p.Id] = requested });
        var bytes = File.ReadAllBytes(FilePath);
        var pending = Row(UserInjection.Read(), p.Id);
        Require(pending.Status == "pending" && !p.HasOverride, "External edit was silently adopted.");
        Require(Equals(Value(p), baseline), "Read mutated the typed slot.");
        HashSet<string>? changed = null;
        var first = UserInjection.Apply(null, ids => changed = ids.ToHashSet(StringComparer.Ordinal));
        var restart = p.Application == "restart";
        Require(first.ChangedKeys.SequenceEqual(restart ? Array.Empty<string>() : [p.Id]), "Wrong changed-key set.");
        Require(restart ? changed == null : changed?.SetEquals([p.Id]) == true, "Wrong refresh callback ownership.");
        Require(Row(first.Snapshot, p.Id).Status == (restart ? "restart" : "applied"), "Wrong post-Apply status.");
        Require(Equals(Value(p), restart ? baseline : p.Parse(requested)), "Wrong post-Apply runtime value.");
        var repeated = UserInjection.Apply(new PaperInjectionPatch(), _ => throw new Exception("Unchanged Apply refreshed consumers."));
        Require(repeated.ChangedKeys.Count == 0, "Repeated Apply changed keys.");
        Require(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(FilePath)), "External Apply rewrote file formatting.");
        UserInjection.LoadStartup(FilePath);
        Require(p.HasOverride && Equals(Value(p), p.Parse(requested)), "Startup failed to adopt a typed value.");
        Require(Row(UserInjection.Read(), p.Id).Status == "applied", "Startup failed to mark applied.");
        if (p.DefaultValue == null)
        {
            Require(Equals(Resolve(p, Probe(p, 1)), p.Parse(requested)), "Dynamic override did not replace a changed formula.");
            Require(Equals(Resolve(p, Probe(p, 2)), p.Parse(requested)), "Dynamic override leaked its input formula.");
        }
        var removed = Remove(p.Id).Snapshot;
        Require(!removed.Overrides.ContainsKey(p.Id), "Remove wrote a default instead of deleting the key.");
        Require(Row(removed, p.Id).Status == (restart ? "restart" : "default"), "Wrong removal status.");
        Require(restart == p.HasOverride, "Removal used the wrong runtime lifecycle.");
        UserInjection.LoadStartup(FilePath);
        Require(!p.HasOverride && Equals(Value(p), baseline), "Restart/removal did not restore the original slot.");
        if (p.DefaultValue == null)
        {
            foreach (var variant in new[] { 1, 2 })
                Require(Equals(Resolve(p, Probe(p, variant)), p.Parse(Probe(p, variant))), "Removed dynamic override froze an old formula result.");
        }
        else
        {
            Set(p.Id, p.DefaultValue.Value);
            UserInjection.LoadStartup(FilePath);
            Require(p.HasOverride && Row(UserInjection.Read(), p.Id).HasAppliedOverride, "Explicit default lost override ownership.");
            Remove(p.Id);
            UserInjection.LoadStartup(FilePath);
            Require(!p.HasOverride, "Explicit default could not be removed.");
        }
    }

    private static void InvalidTypes(InjectionParameter p)
    {
        var invalid = new List<JsonElement> { Raw("null"), Raw("[]"), Raw("{}"), p.Type == "string" ? J(123) : J("not-a-typed-value") };
        if (p.Type == "integer") { invalid.Add(Raw("0.5")); invalid.Add(Raw("999999999999999999999999999999999999")); }
        if (p.Type == "number") invalid.Add(Raw("1e9999"));
        if (p.Type == "boolean") invalid.Add(J(1));
        var before = Value(p);
        File.WriteAllText(FilePath, "{\"unknown.keep\":{\"text\":\"中文\",\"items\":[1,null,true]}}");
        var bytes = File.ReadAllBytes(FilePath);
        foreach (var bad in invalid)
        {
            var callbacks = 0;
            Error("injection_value_invalid", () => UserInjection.Apply(new PaperInjectionPatch
            {
                ExpectedRevision = UserInjection.Read().Revision,
                Set = new Dictionary<string, JsonElement> { [p.Id] = bad }
            }, _ => callbacks++));
            Require(callbacks == 0 && Equals(Value(p), before) && !p.HasOverride, "Rejected input changed runtime state.");
            Require(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(FilePath)), "Rejected input replaced user.json.");
        }
    }

    private static void AdvisoryRanges(InjectionParameter p)
    {
        foreach (var number in new[] { p.RecommendedMinimum - 1, p.RecommendedMaximum + 1 }.Where(v => v.HasValue).Select(v => v!.Value))
        {
            var candidate = p.Type == "integer" ? J((long)Math.Round(number)) : J(number);
            // The declaration's representable CLR type, not the recommendation, is the only boundary.
            object parsed;
            try { parsed = p.Parse(candidate); }
            catch (Exception ex) when (ex is JsonException or OverflowException) { continue; }
            Set(p.Id, candidate);
            UserInjection.LoadStartup(FilePath);
            var row = Row(UserInjection.Read(), p.Id);
            Require(row.OutsideRecommendedRange && Same(row.FileValue, candidate), "Range advice silently clamped a value.");
            Require(Equals(Value(p), parsed), "A representable out-of-range value was not bound.");
            var bytes = File.ReadAllBytes(FilePath);
            var suggestion = UserInjection.Suggest(p.Id, candidate, new Dictionary<string, JsonElement>());
            if (suggestion != null) _ = p.Parse(suggestion.Value);
            Require(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(FilePath)) && Equals(Value(p), parsed), "Suggest mutated file or runtime.");
        }
    }

    private static void JsonFiles()
    {
        const string id = "todo.paste.maxItems";
        foreach (var text in new[] { "", " \r\n\t", "\uFEFF{}", "{ /* comment */ \"todo.paste.maxItems\":201, }", "{\"todo.paste.maxItems\":200,\"todo.paste.maxItems\":202}" })
        {
            Reset();
            File.WriteAllText(FilePath, text, new UTF8Encoding(false));
            var bytes = File.ReadAllBytes(FilePath);
            var expected = text.Contains("202") ? 202 : text.Contains("201") ? 201 : 200;
            UserInjection.LoadStartup(FilePath);
            Require(InjectionParameters.TodoPasteLineLimit.Value == expected, "JSON variant did not load correctly.");
            Apply();
            Require(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(FilePath)), "Read/Apply rewrote a JSON variant.");
        }
        Write(new Dictionary<string, JsonElement> { ["unknown.object"] = Raw("{\"中文\":[true,null,{\"x\":1}]}"), [id] = J(203) });
        var old = UserInjection.Read();
        Set(id, J(204));
        var now = UserInjection.Read();
        Require(Same(old.Overrides["unknown.object"], now.Overrides["unknown.object"]), "Unknown nested data was lost.");
        Require(!Row(now, "unknown.object").Known, "Unknown key became a known binding.");
    }

    private static void MalformedFiles()
    {
        foreach (var text in new[] { "null", "[]", "true", "5", "\"x\"", "{broken", "{\"todo.paste.maxItems\":}" })
        {
            Reset(); Set("todo.paste.maxItems", J(321));
            File.WriteAllText(FilePath, text);
            Error("injection_file_invalid", () => Apply());
            Require(InjectionParameters.TodoPasteLineLimit.Value == 321 && File.ReadAllText(FilePath) == text,
                "A malformed document reset runtime or replaced the original bytes.");
        }
        Reset();
        const string invalidMixed = "{\"todo.paste.maxItems\":322,\"markdown.parse.fullThresholdChars\":\"invalid\"}";
        File.WriteAllText(FilePath, invalidMixed);
        Error("injection_value_invalid", () => Apply());
        Require(InjectionParameters.TodoPasteLineLimit.Value == 200, "Parsing partially bound an earlier valid key.");
        Require(File.ReadAllText(FilePath) == invalidMixed, "Parsing silently repaired the file.");
    }

    private static void PatchBehavior()
    {
        var stale = UserInjection.Read().Revision;
        File.WriteAllText(FilePath, "{\"future.keep\":true}");
        var external = File.ReadAllBytes(FilePath);
        Error("injection_conflict", () => Apply(new() { ExpectedRevision = stale, ResetAll = true }));
        Error("injection_conflict", () => Apply(new() { Set = new Dictionary<string, JsonElement> { ["todo.paste.maxItems"] = J(201) } }));
        Require(external.AsSpan().SequenceEqual(File.ReadAllBytes(FilePath)), "Conflicting edits overwrote external bytes.");
        Set("todo.paste.maxItems", J(201));
        Require(UserInjection.Read().Overrides.ContainsKey("future.keep"), "Sparse edit removed another key.");
        var result = Apply(new() { ExpectedRevision = UserInjection.Read().Revision, ResetAll = true,
            Remove = ["todo.paste.maxItems"], Set = new Dictionary<string, JsonElement> { ["todo.paste.maxItems"] = J(202) } });
        Require(result.Snapshot.Overrides.Count == 1 && InjectionParameters.TodoPasteLineLimit.Value == 202,
            "Reset/remove/set precedence did not retain the explicit replacement.");
        var same = UserInjection.Read();
        File.WriteAllText(FilePath, "{ \"todo.paste.maxItems\" : 202 }\n");
        Error("injection_conflict", () => Apply(new() { ExpectedRevision = same.Revision, Remove = ["todo.paste.maxItems"] }));
        Require(Apply().ChangedKeys.Count == 0, "Format-only edits caused consumer refresh.");
    }

    private static void RefreshFailure()
    {
        Write(new Dictionary<string, JsonElement> { ["todo.paste.maxItems"] = J(333), ["note.maxTextChars"] = J(123456) });
        try { UserInjection.Apply(null, _ => throw new ApplicationException("expected")); }
        catch (ApplicationException) { }
        var failed = UserInjection.Read();
        Require(Row(failed, "todo.paste.maxItems").Status == "pending" && Row(failed, "note.maxTextChars").Status == "pending",
            "Failed refresh reported applied or accepted restart values.");
        Require(InjectionParameters.TodoPasteLineLimit.Value == 333, "Test must cover a partially bound failure, not a rollback.");
        var result = Apply();
        Require(result.ChangedKeys.Contains("todo.paste.maxItems") && Row(result.Snapshot, "note.maxTextChars").Status == "restart", "Retry did not finish adoption.");
        Write(new Dictionary<string, JsonElement> { ["todo.paste.maxItems"] = J(334) });
        try { UserInjection.Apply(null, _ => throw new ApplicationException("expected")); }
        catch (ApplicationException) { }
        File.WriteAllText(FilePath, "{}");
        Require(Apply().ChangedKeys.Contains("todo.paste.maxItems") && InjectionParameters.TodoPasteLineLimit.Value == 200,
            "Removing a failed binding did not refresh the real previous value.");
    }

    private static void StateMachine()
    {
        var selected = Slots.GroupBy(p => (p.Application, p.Type)).SelectMany(g => g.Take(3)).ToArray();
        var file = new Dictionary<string, JsonElement>();
        var bound = new Dictionary<string, JsonElement>();
        var applied = new Dictionary<string, JsonElement>();
        var accepted = new Dictionary<string, JsonElement>();
        var defaults = selected.ToDictionary(p => p.Id, Value);
        JsonElement? Get(Dictionary<string, JsonElement> map, string id) => map.TryGetValue(id, out var v) ? v : null;
        var random = new Random(451);
        for (var step = 0; step < 300; step++)
        {
            var p = selected[random.Next(selected.Length)];
            var operation = random.Next(6);
            if (operation <= 2)
            {
                if (operation == 0) file[p.Id] = Probe(p, random.Next(5));
                else if (operation == 1) file.Remove(p.Id);
                else file.Clear();
                Write(file);
            }
            else if (operation == 3)
            {
                UserInjection.LoadStartup(FilePath);
                bound = new(file); applied = new(file); accepted = new(file);
            }
            else
            {
                var changes = selected.Where(s => s.Application != "restart" &&
                    (!Same(Get(applied, s.Id), Get(file, s.Id)) || !Same(Get(bound, s.Id), Get(file, s.Id))))
                    .Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
                var fail = operation == 5 && changes.Count != 0;
                foreach (var id in changes)
                    if (file.TryGetValue(id, out var v)) bound[id] = v; else bound.Remove(id);
                var observed = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    UserInjection.Apply(null, ids =>
                    {
                        observed.UnionWith(ids);
                        if (fail) throw new ApplicationException("state-machine failure");
                    });
                    Require(!fail, "Expected runtime refresh failure was swallowed.");
                }
                catch (ApplicationException) when (fail) { }
                Require(observed.SetEquals(changes), $"State-machine changed-key mismatch at {step}.");
                if (!fail)
                {
                    foreach (var id in changes)
                        if (file.TryGetValue(id, out var v)) applied[id] = v; else applied.Remove(id);
                    accepted = new(file);
                }
            }
            var snapshot = UserInjection.Read();
            foreach (var s in selected)
            {
                var requested = Get(file, s.Id);
                var expected = Same(Get(applied, s.Id), requested) && Same(Get(bound, s.Id), requested)
                    ? requested.HasValue ? "applied" : "default"
                    : s.Application == "restart" && Same(Get(accepted, s.Id), requested) ? "restart" : "pending";
                var row = Row(snapshot, s.Id);
                Require(row.Status == expected && Same(row.FileValue, requested) && Same(row.AppliedValue, Get(applied, s.Id)),
                    $"State-machine snapshot mismatch at {step}: {s.Id} expected {expected}, got {row.Status}.");
                var active = Get(bound, s.Id);
                Require(s.HasOverride == active.HasValue && Equals(Value(s), active.HasValue ? s.Parse(active.Value) : defaults[s.Id]),
                    $"State-machine typed value mismatch at {step}: {s.Id}.");
            }
        }
    }
}
