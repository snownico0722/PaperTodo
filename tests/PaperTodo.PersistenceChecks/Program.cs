using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PaperTodo;

var checks = new (string Name, Action Run)[]
{
    ("primary-save-faults-keep-a-loadable-generation", PrimarySaveFaultsKeepALoadableGeneration),
    ("backup-refresh-fault-keeps-old-backup", BackupRefreshFaultKeepsOldBackup),
    ("corrupt-primary-never-refreshes-backup", CorruptPrimaryNeverRefreshesBackup),
    ("older-save-version-cannot-overwrite-newer", OlderSaveVersionCannotOverwriteNewer),
    ("backup-recovery-is-preserved-until-normal-save", BackupRecoveryIsPreservedUntilNormalSave),
    ("plugin-data-uses-one-normal-file", PluginDataUsesOneNormalFile),
    ("unreadable-plugin-data-does-not-become-empty", UnreadablePluginDataDoesNotBecomeEmpty),
    ("plugin-data-non-file-path-is-not-treated-as-missing", PluginDataNonFilePathIsNotTreatedAsMissing),
    ("legacy-plugin-recovery-file-is-not-selected", LegacyPluginRecoveryFileIsNotSelected),
    ("plugin-system-shutdown-skips-final-flush", PluginSystemShutdownSkipsFinalFlush),
    ("plugin-normal-dispose-still-final-flushes", PluginNormalDisposeStillFinalFlushes),
    ("shutdown-skips-deferred-plugin-cleanup", ShutdownSkipsDeferredPluginCleanup),
    ("temp-validator-failure-keeps-old-target", TempValidatorFailureKeepsOldTarget),
    ("flush-failure-keeps-old-target", FlushFailureKeepsOldTarget),
    ("replace-retries-transient-sharing-failures", ReplaceRetriesTransientSharingFailures),
    ("markdown-modes-migrate-and-roundtrip", MarkdownModesMigrateAndRoundTrip),
    ("obsolete-topbar-aggregate-is-ignored", ObsoleteTopBarAggregateIsIgnored),
    ("disabled-capsule-mode-keeps-queue-layout-memory", DisabledCapsuleModeKeepsQueueLayoutMemory),
    ("downward-preview-default-and-roundtrip", DownwardPreviewDefaultAndRoundTrip)
};

var failed = 0;
foreach (var check in checks)
{
    try
    {
        check.Run();
        Console.WriteLine($"PASS {check.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {check.Name}: {ex}");
    }
}

if (failed != 0)
{
    Console.Error.WriteLine($"Persistence checks failed: {failed}/{checks.Length}");
    return 1;
}

Console.WriteLine($"Persistence checks passed: {checks.Length}/{checks.Length}");
return 0;

static void PrimarySaveFaultsKeepALoadableGeneration()
{
    var stages = new[]
    {
        DurableAtomicWriteStage.BeforeTempOpen,
        DurableAtomicWriteStage.AfterTempWrite,
        DurableAtomicWriteStage.AfterFlush,
        DurableAtomicWriteStage.BeforeReplace
    };

    foreach (var stage in stages)
    {
        using var scope = new TempDirectory();
        var seed = NewStore(scope.Path, DurableAtomicFileWriter.Shared);
        seed.SaveJsonSync(seed.SerializeState(NewState("light")), version: 1);
        Assert(seed.TryRefreshBackupFromPrimary(), $"seed backup failed for {stage}");

        var failingWriter = new DurableAtomicFileWriter((current, _) =>
        {
            if (current == stage)
            {
                throw new IOException($"Injected failure at {stage}");
            }
        });
        var store = NewStore(scope.Path, failingWriter);
        var nextJson = store.SerializeState(NewState("dark"));

        AssertThrows<IOException>(
            () => store.SaveJsonSync(nextJson, version: 2),
            $"save should fail at {stage}");

        var recovered = NewStore(scope.Path, DurableAtomicFileWriter.Shared).Load();
        Assert(recovered.Theme == "light", $"old generation was not recoverable after {stage}");
    }
}

static void BackupRefreshFaultKeepsOldBackup()
{
    using var scope = new TempDirectory();
    var seed = NewStore(scope.Path, DurableAtomicFileWriter.Shared);
    seed.SaveJsonSync(seed.SerializeState(NewState("light")), version: 1);
    Assert(seed.TryRefreshBackupFromPrimary(), "initial backup refresh failed");
    seed.SaveJsonSync(seed.SerializeState(NewState("dark")), version: 2);

    var failingWriter = new DurableAtomicFileWriter((stage, target) =>
    {
        if (stage == DurableAtomicWriteStage.BeforeReplace &&
            target.EndsWith("data.backup.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Injected backup replace failure");
        }
    });

    var store = NewStore(scope.Path, failingWriter);
    Assert(!store.TryRefreshBackupFromPrimary(), "failed backup refresh unexpectedly succeeded");
    Assert(ReadTheme(store.BackupPath) == "light", "old backup was overwritten on failed refresh");
}

static void CorruptPrimaryNeverRefreshesBackup()
{
    using var scope = new TempDirectory();
    var store = NewStore(scope.Path, DurableAtomicFileWriter.Shared);
    store.SaveJsonSync(store.SerializeState(NewState("light")), version: 1);
    Assert(store.TryRefreshBackupFromPrimary(), "initial backup refresh failed");

    File.WriteAllBytes(store.FilePath, Enumerable.Repeat((byte)0, 4096).ToArray());
    Assert(!store.TryRefreshBackupFromPrimary(), "corrupt primary was accepted for backup refresh");
    Assert(ReadTheme(store.BackupPath) == "light", "healthy backup changed after corrupt primary");
}

static void OlderSaveVersionCannotOverwriteNewer()
{
    using var scope = new TempDirectory();
    var store = NewStore(scope.Path, DurableAtomicFileWriter.Shared);
    store.SaveJsonSync(store.SerializeState(NewState("dark")), version: 2);
    store.SaveJsonSync(store.SerializeState(NewState("light")), version: 1);

    Assert(NewStore(scope.Path, DurableAtomicFileWriter.Shared).Load().Theme == "dark",
        "older save version overwrote the newer state");
}

static void BackupRecoveryIsPreservedUntilNormalSave()
{
    using var scope = new TempDirectory();
    var seed = NewStore(scope.Path, DurableAtomicFileWriter.Shared);
    seed.SaveJsonSync(seed.SerializeState(NewState("light")), version: 1);
    Assert(seed.TryRefreshBackupFromPrimary(), "initial backup refresh failed");

    File.WriteAllText(seed.FilePath, "not-json");
    var recoveredStore = NewStore(scope.Path, DurableAtomicFileWriter.Shared);
    Assert(recoveredStore.Load().Theme == "light", "backup recovery did not load");
    Assert(!recoveredStore.TryRefreshBackupFromPrimary(),
        "backup used for recovery was allowed to refresh immediately");
    Assert(ReadTheme(recoveredStore.BackupPath) == "light", "recovery backup was changed");
}

static void DisabledCapsuleModeKeepsQueueLayoutMemory()
{
    using var scope = new TempDirectory();
    var state = NewState("light");
    state.UseCapsuleMode = false;
    state.UseDeepCapsuleMode = false;
    state.UseCapsuleCollapseAll = false;
    state.DeepCapsuleQueueStartTopMargins["DISPLAY1|left"] = 37.5;

    var store = NewStore(scope.Path, DurableAtomicFileWriter.Shared);
    store.SaveJsonSync(store.SerializeState(state), version: 1);
    var loaded = NewStore(scope.Path, DurableAtomicFileWriter.Shared).Load();

    Assert(loaded.DeepCapsuleQueueStartTopMargins.TryGetValue("DISPLAY1|left", out var margin) &&
           Math.Abs(margin - 37.5) < 0.001,
        "disabled capsule mode discarded remembered queue layout");
}

static void PluginDataUsesOneNormalFile()
{
    using var scope = new TempDirectory();
    var path = Path.Combine(scope.Path, "data", "sample.plugin.json");
    using (var store = new PaperBodyPluginDataStore(scope.Path))
    {
        Assert(!store.TryReadPaperState("sample.plugin", "p", out _), "missing file is a new plugin");
        store.SavePaperState("sample.plugin", "p", 1, "{\"value\":7}");
    }
    using var loaded = new PaperBodyPluginDataStore(scope.Path);
    Assert(loaded.TryReadPaperState("sample.plugin", "p", out var state), "normal file was not persisted");
    using var json = JsonDocument.Parse(state.Json);
    Assert(json.RootElement.GetProperty("value").GetInt32() == 7, "saved state did not roundtrip");
    Assert(Directory.GetFiles(Path.GetDirectoryName(path)!).Single() == path, "created an alternate state file");
}

static void UnreadablePluginDataDoesNotBecomeEmpty()
{
    using var scope = new TempDirectory();
    var path = Path.Combine(scope.Path, "data", "sample.plugin.json");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    const string original = "{broken plugin data";
    File.WriteAllText(path, original);
    using (var store = new PaperBodyPluginDataStore(scope.Path))
    {
        AssertThrows<JsonException>(() => store.ReadPaperState("sample.plugin", "p"), "unreadable data was hidden");
        AssertThrows<JsonException>(() => store.SavePaperState("sample.plugin", "p", 1, "{}"), "write accepted an unreadable document");
    }
    Assert(File.ReadAllText(path) == original, "failed read replaced original data");
    Assert(Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1, "failed read created a recovery file");
}

static void PluginDataNonFilePathIsNotTreatedAsMissing()
{
    using var scope = new TempDirectory();
    var path = Path.Combine(scope.Path, "data", "sample.plugin.json");
    Directory.CreateDirectory(path);
    using var store = new PaperBodyPluginDataStore(scope.Path);
    try
    {
        _ = store.ReadPaperState("sample.plugin", "p");
        throw new InvalidOperationException(
            "A non-file plugin data path was treated as a missing file.");
    }
    catch (UnauthorizedAccessException)
    {
    }
    catch (IOException)
    {
    }
    Assert(Directory.Exists(path),
        "Plugin data read failure replaced the existing path.");
}

static void LegacyPluginRecoveryFileIsNotSelected()
{
    using var scope = new TempDirectory();
    var path = Path.Combine(scope.Path, "data", "sample.plugin.json");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    const string legacy = "legacy contents left for manual use";
    File.WriteAllText(path + ".recovered", legacy);
    using (var store = new PaperBodyPluginDataStore(scope.Path))
    {
        Assert(!store.TryReadPaperState("sample.plugin", "p", out _), "legacy file was selected");
        store.SavePaperState("sample.plugin", "p", 1, "{}");
    }
    Assert(File.Exists(path), "normal state path was not used");
    Assert(File.ReadAllText(path + ".recovered") == legacy, "legacy file was modified or removed");
}

static void PluginSystemShutdownSkipsFinalFlush()
{
    using var scope = new TempDirectory();
    var writer = new RecordingWriter();
    var store = new PaperBodyPluginDataStore(scope.Path, writer);
    store.SavePaperState("sample.plugin", "paper-1", 1, "{\"value\":1}");
    store.SuppressFinalFlushOnDispose();
    store.Dispose();

    Assert(writer.WriteCount == 0, "system-shutdown disposal started a plugin final write");
}

static void PluginNormalDisposeStillFinalFlushes()
{
    using var scope = new TempDirectory();
    var writer = new RecordingWriter();
    var store = new PaperBodyPluginDataStore(scope.Path, writer);
    store.SavePaperState("sample.plugin", "paper-1", 1, "{\"value\":1}");
    store.Dispose();

    Assert(writer.WriteCount == 1, "normal plugin disposal did not flush dirty state exactly once");
}

static void ShutdownSkipsDeferredPluginCleanup()
{
    var controller = (AppController)RuntimeHelpers.GetUninitializedObject(typeof(AppController));
    var lifecycleField = typeof(AppController).GetField(
        "_lifecycleState",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("AppController lifecycle field not found.");
    lifecycleField.SetValue(controller, Enum.Parse(lifecycleField.FieldType, "Exiting"));

    // An uninitialized controller has no plugin/store collections. The shutdown gate must return
    // before touching any of them; otherwise this call throws and the check fails.
    controller.TryFlushPendingPluginPaperStateDeletes();
}

static void TempValidatorFailureKeepsOldTarget()
{
    using var scope = new TempDirectory();
    var target = Path.Combine(scope.Path, "state.json");
    File.WriteAllText(target, "old");

    var writer = new DurableAtomicFileWriter();
    AssertThrows<InvalidDataException>(
        () => writer.Write(target, "new"u8.ToArray(), _ => false),
        "validator failure should reject the temp file");

    Assert(File.ReadAllText(target) == "old", "validator failure replaced the old target");
}

static void FlushFailureKeepsOldTarget()
{
    using var scope = new TempDirectory();
    var target = Path.Combine(scope.Path, "state.json");
    File.WriteAllText(target, "old");

    var operations = new ScriptedFileOperations
    {
        FailFlush = true
    };
    var writer = new DurableAtomicFileWriter(fileOperations: operations);

    AssertThrows<IOException>(
        () => writer.Write(target, "new"u8.ToArray()),
        "flush failure should abort the durable write");
    Assert(File.ReadAllText(target) == "old", "flush failure replaced the old target");
    Assert(operations.ReplaceCalls == 0, "replace ran after flush failure");
}

static void ReplaceRetriesTransientSharingFailures()
{
    using var scope = new TempDirectory();
    var target = Path.Combine(scope.Path, "state.json");
    File.WriteAllText(target, "old");

    var operations = new ScriptedFileOperations
    {
        ReplaceFailuresRemaining = 2
    };
    var writer = new DurableAtomicFileWriter(fileOperations: operations);
    writer.Write(target, "new"u8.ToArray());

    Assert(File.ReadAllText(target) == "new", "transient replace failures did not eventually commit");
    Assert(operations.ReplaceCalls == 3, $"expected 3 replace attempts, got {operations.ReplaceCalls}");
    Assert(operations.DelayCalls == 2, $"expected 2 retry delays, got {operations.DelayCalls}");
}

static void MarkdownModesMigrateAndRoundTrip()
{
    Assert(new AppState().MarkdownRenderMode == MarkdownRenderModes.Basic,
        "new state should default to Basic");
    var cases = new (string? Input, string Expected)[]
    {
        ("\"off\"", MarkdownRenderModes.Off),
        ("\"basic\"", MarkdownRenderModes.Basic),
        ("\"enhanced\"", MarkdownRenderModes.Basic),
        ("\"full\"", MarkdownRenderModes.Full),
        ("\"unknown\"", MarkdownRenderModes.Basic),
        ("null", MarkdownRenderModes.Basic),
        (null, MarkdownRenderModes.Basic)
    };

    foreach (var (input, expected) in cases)
    {
        using var scope = new TempDirectory();
        var store = NewStore(scope.Path, DurableAtomicFileWriter.Shared);
        var setting = input == null ? "" : ",\"markdownRenderMode\":" + input;
        File.WriteAllText(store.FilePath,
            "{\"papers\":[{\"id\":\"mode-note\",\"type\":\"note\",\"content\":\"# Keep **source**\"}]" + setting + "}");
        var state = store.Load();
        Assert(state.MarkdownRenderMode == expected, $"wrong migration for {input ?? "missing"}");
        Assert(state.Papers.Single().Content == "# Keep **source**", "mode migration changed note content");

        store.SaveJsonSync(store.SerializeState(state), version: 1);
        using var saved = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        Assert(saved.RootElement.GetProperty("markdownRenderMode").GetString() == expected,
            "save retained a legacy/invalid Markdown mode");
        Assert(NewStore(scope.Path, DurableAtomicFileWriter.Shared).Load().MarkdownRenderMode == expected,
            "Markdown mode changed after reload");
    }
}

static void ObsoleteTopBarAggregateIsIgnored()
{
    using var scope = new TempDirectory();
    var store = NewStore(scope.Path, DurableAtomicFileWriter.Shared);
    File.WriteAllText(
        store.FilePath,
        "{\"papers\":[],\"showTopBarNewTodoButton\":false,\"showTopBarNewNoteButton\":true,\"showTopBarNewPaperButtons\":false}");

    var state = store.Load();
    Assert(!state.ShowTopBarNewTodoButton,
        "real todo-button setting changed while ignoring the obsolete aggregate field");
    Assert(state.ShowTopBarNewNoteButton,
        "obsolete aggregate field overrode the real note-button setting");

    store.SaveJsonSync(store.SerializeState(state), version: 1);
    using var saved = JsonDocument.Parse(File.ReadAllText(store.FilePath));
    Assert(!saved.RootElement.TryGetProperty("showTopBarNewPaperButtons", out _),
        "obsolete aggregate field was written back to current state");
}

static void DownwardPreviewDefaultAndRoundTrip()
{
    Assert(!new AppState().EdgeCapsulePreviewPreferDownward, "new state should not prefer downward preview");
    foreach (var input in new string?[] { null, "true", "false" })
    {
        using var scope = new TempDirectory();
        var store = NewStore(scope.Path, DurableAtomicFileWriter.Shared);
        var setting = input == null ? "" : ",\"edgeCapsulePreviewPreferDownward\":" + input;
        File.WriteAllText(store.FilePath, "{\"papers\":[]" + setting + "}");
        var state = store.Load();
        var expected = input == "true";
        Assert(state.EdgeCapsulePreviewPreferDownward == expected,
            $"wrong preview preference for {input ?? "missing"}");

        store.SaveJsonSync(store.SerializeState(state), version: 1);
        using var saved = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        Assert(saved.RootElement.GetProperty("edgeCapsulePreviewPreferDownward").GetBoolean() == expected,
            "save lost the preview preference");
        Assert(NewStore(scope.Path, DurableAtomicFileWriter.Shared).Load().EdgeCapsulePreviewPreferDownward == expected,
            "preview preference changed after reload");
    }
}

static StateStore NewStore(string directory, IDurableAtomicFileWriter writer) =>
    new(directory, writer);

static AppState NewState(string theme) => new()
{
    Theme = theme,
    Papers = []
};

static string ReadTheme(string path)
{
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    return document.RootElement.GetProperty("theme").GetString()
        ?? throw new InvalidDataException($"Missing theme in {path}");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

internal sealed class RecordingWriter : IDurableAtomicFileWriter
{
    public int WriteCount { get; private set; }

    public void Write(
        string targetPath,
        byte[] bytes,
        Func<string, bool>? validateTemp = null)
    {
        WriteCount++;
    }
}

internal sealed class ScriptedFileOperations : IDurableAtomicFileOperations
{
    public bool FailFlush { get; init; }
    public int ReplaceFailuresRemaining { get; set; }
    public int ReplaceCalls { get; private set; }
    public int DelayCalls { get; private set; }

    public void FlushToDisk(FileStream stream)
    {
        if (FailFlush)
        {
            throw new IOException("Injected Flush(true) failure.");
        }

        stream.Flush(flushToDisk: true);
    }

    public void Replace(string tempPath, string targetPath)
    {
        ReplaceCalls++;
        if (ReplaceFailuresRemaining > 0)
        {
            ReplaceFailuresRemaining--;
            throw new IOException("Injected transient sharing violation.");
        }

        File.Move(tempPath, targetPath, overwrite: true);
    }

    public void Delay(TimeSpan delay)
    {
        DelayCalls++;
    }

    public void Delete(string path)
    {
        File.Delete(path);
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "PaperTodo.PersistenceChecks",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
        }
    }
}
