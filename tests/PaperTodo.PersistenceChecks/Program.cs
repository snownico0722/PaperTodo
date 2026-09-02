using System.IO;
using System.Text.Json;
using PaperTodo;

var checks = new (string Name, Action Run)[]
{
    ("primary-save-faults-keep-a-loadable-generation", PrimarySaveFaultsKeepALoadableGeneration),
    ("backup-refresh-fault-keeps-old-backup", BackupRefreshFaultKeepsOldBackup),
    ("corrupt-primary-never-refreshes-backup", CorruptPrimaryNeverRefreshesBackup),
    ("older-save-version-cannot-overwrite-newer", OlderSaveVersionCannotOverwriteNewer),
    ("backup-recovery-is-preserved-until-normal-save", BackupRecoveryIsPreservedUntilNormalSave),
    ("temp-validator-failure-keeps-old-target", TempValidatorFailureKeepsOldTarget),
    ("flush-failure-keeps-old-target", FlushFailureKeepsOldTarget),
    ("replace-retries-transient-sharing-failures", ReplaceRetriesTransientSharingFailures)
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

    var operations = new TestFileOperations
    {
        FlushFailure = new IOException("Injected flush failure")
    };
    var writer = new DurableAtomicFileWriter(fileOperations: operations);

    AssertThrows<IOException>(
        () => writer.Write(target, "new"u8.ToArray()),
        "flush failure should fail the write");
    Assert(File.ReadAllText(target) == "old", "flush failure replaced the old target");
    Assert(operations.ReplaceCount == 0, "replace ran after flush failure");
}

static void ReplaceRetriesTransientSharingFailures()
{
    using var scope = new TempDirectory();
    var target = Path.Combine(scope.Path, "state.json");
    File.WriteAllText(target, "old");

    var operations = new TestFileOperations
    {
        ReplaceFailuresRemaining = 2
    };
    var writer = new DurableAtomicFileWriter(fileOperations: operations);
    writer.Write(target, "new"u8.ToArray());

    Assert(File.ReadAllText(target) == "new", "successful retry did not commit new content");
    Assert(operations.ReplaceCount == 3, $"expected 3 replace attempts, got {operations.ReplaceCount}");
    Assert(operations.DelayCount == 2, $"expected 2 retry delays, got {operations.DelayCount}");
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

internal sealed class TestFileOperations : IDurableAtomicFileOperations
{
    public Exception? FlushFailure { get; init; }
    public int ReplaceFailuresRemaining { get; set; }
    public int ReplaceCount { get; private set; }
    public int DelayCount { get; private set; }

    public void FlushToDisk(FileStream stream)
    {
        if (FlushFailure != null)
        {
            throw FlushFailure;
        }

        stream.Flush(flushToDisk: true);
    }

    public void Replace(string tempPath, string targetPath)
    {
        ReplaceCount++;
        if (ReplaceFailuresRemaining > 0)
        {
            ReplaceFailuresRemaining--;
            throw new IOException("Injected transient sharing failure");
        }

        File.Move(tempPath, targetPath, overwrite: true);
    }

    public void Delay(TimeSpan delay)
    {
        DelayCount++;
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
            "PaperTodo.PersistenceChecks.3x",
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
