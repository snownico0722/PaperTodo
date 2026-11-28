using System.Text.Json;
using PaperTodo;
using PaperTodo.Plugin;

if (args.SequenceEqual(new[] { "--benchmark" }))
{
    using var scope = new InjectionScope();
    UserInjectionMicrobenchmark.Run();
    return;
}
if (args.Length != 0)
    throw new ArgumentException("Usage: PaperTodo.UserInjectionChecks [--benchmark]");

var checks = new (string Name, Action<InjectionScope> Run)[]
{
    ("Explicit default remains an override; removing it restores the default", ExplicitDefault),
    ("Dynamic overrides preserve the current formula after removal", DynamicValue),
    ("External saves stay pending until Apply and do not get rewritten", ExternalFile),
    ("Restart values stay pending through Apply, including removal", RestartValue),
    ("Revision conflicts preserve the external file; fresh patches preserve other keys", PatchConflicts),
    ("A failed runtime refresh is never reported as applied", FailedRefresh),
    ("Suggestions use draft values and defaults without changing files or runtime", Suggestions),
    ("Injection edits and reset do not modify ordinary application data", SeparateStorage)
};
foreach (var check in checks)
{
    using var scope = new InjectionScope();
    check.Run(scope);
    Console.WriteLine($"PASS {check.Name}");
}
Console.WriteLine($"All {checks.Length} user-injection behavior checks passed.");

static void ExplicitDefault(InjectionScope scope)
{
    const string id = "todo.paste.maxItems";
    var initial = UserInjection.Read();
    Equal("default", Row(initial, id).Status);
    True(!File.Exists(scope.FilePath), "Reading an absent override file must not create it.");

    var applied = Set(initial, id, 200);
    True(Row(applied, id).HasFileOverride && Row(applied, id).HasAppliedOverride,
        "An explicit value equal to the built-in value must remain explicit.");
    Equal("applied", Row(applied, id).Status);
    True(UserInjection.HasOverride(id), "The runtime must retain explicit ownership.");
    Equal(200, InjectionParameters.TodoPasteLineLimit.Value);

    var removed = UserInjection.Apply(new PaperInjectionPatch
    {
        ExpectedRevision = applied.Revision,
        Remove = [id]
    }, _ => { }).Snapshot;
    True(!removed.Overrides.ContainsKey(id) && !UserInjection.HasOverride(id),
        "Removal clears the key and runtime override instead of writing a default value.");
    Equal("default", Row(removed, id).Status);
    Equal(200, InjectionParameters.TodoPasteLineLimit.Value);
}

static void DynamicValue(InjectionScope scope)
{
    const string id = "todo.selection.effectiveEdgeDip";
    var slot = InjectionParameters.TodoSelectionEdge;
    Equal(28d, slot.Resolve(28d));
    Equal(17d, slot.Resolve(17d));
    var applied = Set(UserInjection.Read(), id, 28d);
    Equal(28d, slot.Resolve(17d));
    True(UserInjection.HasOverride(id), "Matching an earlier formula result is still an explicit override.");
    UserInjection.Apply(new PaperInjectionPatch { ExpectedRevision = applied.Revision, Remove = [id] }, _ => { });
    Equal(17d, slot.Resolve(17d));
    Equal(41d, slot.Resolve(41d));
}

static void ExternalFile(InjectionScope scope)
{
    const string id = "markdown.parse.fullThresholdChars";
    const string saved = "{\n  // Manually edited configuration\n  \"markdown.parse.fullThresholdChars\": 1024,\n}\n";
    File.WriteAllText(scope.FilePath, saved);
    var pending = UserInjection.Read();
    Equal("pending", Row(pending, id).Status);
    Equal(8000, InjectionParameters.MarkdownFullParseThreshold.Value);
    Equal(saved, File.ReadAllText(scope.FilePath));

    IReadOnlySet<string>? refreshed = null;
    var applied = UserInjection.Apply(null, changed => refreshed = changed);
    Equal(1024, InjectionParameters.MarkdownFullParseThreshold.Value);
    Equal("applied", Row(applied.Snapshot, id).Status);
    True(refreshed?.Contains(id) == true, "The changed runtime consumer must be refreshed.");
    Equal(saved, File.ReadAllText(scope.FilePath));
}

static void RestartValue(InjectionScope scope)
{
    const string id = "note.maxTextChars";
    File.WriteAllText(scope.FilePath, "{\"note.maxTextChars\":150000}");
    Equal("pending", Row(UserInjection.Read(), id).Status);
    var applied = UserInjection.Apply(null, _ => { }).Snapshot;
    Equal("restart", Row(applied, id).Status);
    Equal(100000, InjectionParameters.NoteTextLimit.Value);
    True(Row(applied, id).HasFileOverride && !Row(applied, id).HasAppliedOverride,
        "Saved startup-only values must not pretend to be running values.");
    UserInjection.Apply(null, _ => { });
    Equal(100000, InjectionParameters.NoteTextLimit.Value);

    UserInjection.LoadStartup(scope.FilePath);
    Equal(150000, InjectionParameters.NoteTextLimit.Value);
    Equal("applied", Row(UserInjection.Read(), id).Status);

    File.WriteAllText(scope.FilePath, "{}");
    Equal("pending", Row(UserInjection.Read(), id).Status);
    var removed = UserInjection.Apply(null, _ => { }).Snapshot;
    Equal("restart", Row(removed, id).Status);
    True(!Row(removed, id).HasFileOverride && Row(removed, id).HasAppliedOverride,
        "Deleting a startup-only override remains pending until restart.");
    Equal(150000, InjectionParameters.NoteTextLimit.Value);
    UserInjection.LoadStartup(scope.FilePath);
    Equal(100000, InjectionParameters.NoteTextLimit.Value);
    Equal("default", Row(UserInjection.Read(), id).Status);
}

static void PatchConflicts(InjectionScope scope)
{
    const string edited = "todo.paste.maxItems";
    const string external = "markdown.parse.fullThresholdChars";
    var baseline = Set(UserInjection.Read(), edited, 220);
    const string handEdited = "{\"todo.paste.maxItems\":240,\"markdown.parse.fullThresholdChars\":1200,\"future.plugin.option\":{\"width\":901,\"enabled\":true}}";
    File.WriteAllText(scope.FilePath, handEdited);
    try
    {
        Set(baseline, edited, 260);
        throw new Exception("A stale whole-file revision must be rejected.");
    }
    catch (UserInjectionException exception)
    {
        Equal("injection_conflict", exception.Code);
    }
    Equal(handEdited, File.ReadAllText(scope.FilePath));
    Equal(220, InjectionParameters.TodoPasteLineLimit.Value);

    // The caller rereads before rebasing its own edit. Patches do not serialize a stale full snapshot.
    var current = UserInjection.Read();
    var unknownBefore = current.Overrides["future.plugin.option"];
    var merged = Set(current, edited, 260);
    Equal(260, merged.Overrides[edited].GetInt32());
    Equal(1200, merged.Overrides[external].GetInt32());
    True(JsonElement.DeepEquals(unknownBefore, merged.Overrides["future.plugin.option"]),
        "Unknown entries must survive edits to a known key.");
    True(!Row(merged, "future.plugin.option").Known, "Unknown keys remain discoverable, not silently dropped.");
    Equal("applied", Row(merged, edited).Status);
    Equal("applied", Row(merged, external).Status);
}

static void FailedRefresh(InjectionScope scope)
{
    const string id = "markdown.parse.fullThresholdChars";
    try
    {
        UserInjection.Apply(new PaperInjectionPatch
        {
            ExpectedRevision = UserInjection.Read().Revision,
            Set = new Dictionary<string, JsonElement> { [id] = JsonSerializer.SerializeToElement(4096) }
        }, _ => throw new InvalidOperationException("Simulated runtime refresh failure."));
        throw new Exception("The refresh failure must reach the caller.");
    }
    catch (InvalidOperationException exception) when (exception.Message == "Simulated runtime refresh failure.") { }
    var saved = UserInjection.Read();
    Equal(4096, saved.Overrides[id].GetInt32());
    Equal("pending", Row(saved, id).Status);
    var adopted = UserInjection.Apply(null, _ => { }).Snapshot;
    Equal("applied", Row(adopted, id).Status);

    // A failed attempt may have bound slots before a later consumer rejected its refresh.
    // Removing that request must not be mistaken for an unchanged last-successful value.
    UserInjection.LoadStartup(Path.Combine(scope.DirectoryPath, "absent.json"));
    try
    {
        UserInjection.Apply(new PaperInjectionPatch
        {
            ExpectedRevision = UserInjection.Read().Revision,
            Set = new Dictionary<string, JsonElement> { [id] = JsonSerializer.SerializeToElement(4096) }
        }, _ => throw new InvalidOperationException("Simulated runtime refresh failure."));
        throw new Exception("The refresh failure must reach the caller.");
    }
    catch (InvalidOperationException exception) when (exception.Message == "Simulated runtime refresh failure.") { }
    File.WriteAllText(UserInjection.FilePath, "{}");
    Equal("pending", Row(UserInjection.Read(), id).Status);
    IReadOnlySet<string>? reverted = null;
    var restored = UserInjection.Apply(null, keys => reverted = keys).Snapshot;
    True(reverted?.Contains(id) == true, "Reverting a failed binding still refreshes its runtime consumer.");
    Equal(8000, InjectionParameters.MarkdownFullParseThreshold.Value);
    Equal("default", Row(restored, id).Status);
}

static void Suggestions(InjectionScope scope)
{
    const string compressed = "note.images.autoCompressedDimensionPx";
    const string stored = "note.images.maxStoredDimensionPx";
    Set(UserInjection.Read(), stored, 1024);
    var bytesBefore = File.ReadAllBytes(scope.FilePath);
    var runtimeBefore = InjectionParameters.NoteImageCompressedDimension.Value;
    var draft = new Dictionary<string, JsonElement> { [stored] = JsonSerializer.SerializeToElement(800) };
    var proposal = UserInjection.Suggest(compressed, JsonSerializer.SerializeToElement(2048), draft);
    True(proposal != null, "The compression suggestion should use the draft storage size.");
    Equal(800, proposal!.Value.GetInt32());
    True(UserInjection.Suggest(compressed, JsonSerializer.SerializeToElement(3000), new Dictionary<string, JsonElement>()) == null,
        "A key removed from the complete draft resolves to its built-in default, not the current running override.");
    True(UserInjection.Suggest(compressed, JsonSerializer.SerializeToElement(true)) == null &&
         UserInjection.Suggest(compressed, JsonSerializer.SerializeToElement("value")) == null,
        "Suggestions for non-numeric input should be absent.");
    True(bytesBefore.AsSpan().SequenceEqual(File.ReadAllBytes(scope.FilePath)), "Suggest must not write the file.");
    Equal(runtimeBefore, InjectionParameters.NoteImageCompressedDimension.Value);
    Equal(1024, InjectionParameters.NoteImageStoredDimension.Value);
}

static void SeparateStorage(InjectionScope scope)
{
    var ordinary = new Dictionary<string, byte[]>
    {
        ["data.json"] = "{\"Papers\":[{\"Id\":\"keep\"}],\"OverallFontScale\":1.2}"u8.ToArray(),
        ["data2.json"] = "{\"Content\":\"preserve workspace\"}"u8.ToArray(),
        ["paper-background.json"] = "{\"BlendWithTheme\":true}"u8.ToArray()
    };
    foreach (var (name, bytes) in ordinary)
        File.WriteAllBytes(Path.Combine(scope.DirectoryPath, name), bytes);
    Set(UserInjection.Read(), "todo.paste.maxItems", 301);
    UserInjection.Apply(new PaperInjectionPatch
    {
        ExpectedRevision = UserInjection.Read().Revision,
        ResetAll = true
    }, _ => { });
    True(UserInjection.Read().Overrides.Count == 0, "Reset clears only the injection document.");
    foreach (var (name, bytes) in ordinary)
        True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(scope.DirectoryPath, name))),
            $"Ordinary data changed: {name}");
}

static PaperInjectionSnapshot Set<T>(PaperInjectionSnapshot basis, string id, T value) =>
    UserInjection.Apply(new PaperInjectionPatch
    {
        ExpectedRevision = basis.Revision,
        Set = new Dictionary<string, JsonElement> { [id] = JsonSerializer.SerializeToElement(value) }
    }, _ => { }).Snapshot;

static PaperInjectionParameterSnapshot Row(PaperInjectionSnapshot snapshot, string id) =>
    snapshot.Parameters.Single(row => row.Id == id);

static void True(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}; got {actual}.");
}

sealed class InjectionScope : IDisposable
{
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "PaperTodo.UserInjectionChecks", Guid.NewGuid().ToString("N"));
    internal string FilePath => Path.Combine(DirectoryPath, "user.json");

    internal InjectionScope()
    {
        Directory.CreateDirectory(DirectoryPath);
        UserInjection.LoadStartup(FilePath);
    }

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
