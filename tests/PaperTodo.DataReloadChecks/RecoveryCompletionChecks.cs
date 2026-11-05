using System.IO;
using PaperTodo;

internal static partial class Program
{
    private static void RecoveryReadbackFailureCanRetry()
    {
        foreach (var number in new[] { 1, 2 })
        foreach (var sourceKind in new[] { "backup", "primary-temp", "backup-temp" })
        foreach (var asynchronous in new[] { false, true })
        {
            using var scope = new Scope();
            var seeded = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, number);
            seeded.SaveJsonSync(seeded.SerializeState(NewState()), 1);
            var sourceBytes = File.ReadAllBytes(seeded.FilePath);
            var sourcePath = sourceKind switch
            {
                "backup" => seeded.BackupPath,
                "primary-temp" => seeded.FilePath + ".tmp",
                _ => seeded.BackupPath + ".tmp"
            };
            File.WriteAllBytes(sourcePath, sourceBytes);
            File.WriteAllText(seeded.FilePath, "{corrupt before recovery");

            FileStream? lockedPrimary = null;
            var injectReadbackFailure = true;
            var writer = new DurableAtomicFileWriter((stage, target) =>
            {
                if (injectReadbackFailure && stage == DurableAtomicWriteStage.AfterReplace &&
                    string.Equals(target, seeded.FilePath, StringComparison.Ordinal))
                {
                    injectReadbackFailure = false;
                    // The replace really succeeded. Only its immediate readback fails.
                    lockedPrimary = new FileStream(target, FileMode.Open,
                        FileAccess.ReadWrite, FileShare.None);
                }
            });
            var store = new StateStore(scope.DirectoryPath, writer, number);
            var restored = store.Load();
            var staleJson = store.SerializeState(restored);
            restored.Papers[0].Content = "committed before readback failure";
            var committedJson = store.SerializeState(restored);
            try
            {
                Throws<IOException>(() =>
                {
                    if (asynchronous) store.SaveJsonAsync(committedJson, 10).GetAwaiter().GetResult();
                    else store.SaveJsonSync(committedJson, 10);
                });
            }
            finally { lockedPrimary?.Dispose(); }

            Require(!injectReadbackFailure &&
                    File.ReadAllText(store.FilePath) == committedJson &&
                    store.NeedsStartupRecoverySave &&
                    RecoverySourcePaths(store, sourceKind)
                        .Any(path => File.ReadAllBytes(path).SequenceEqual(sourceBytes)) &&
                    !store.TryCollectProtectedImageIds(restored, out _),
                "readback failure lost the committed primary, original source or image protection");

            // Both sync and queued async saves must respect the committed version,
            // even though the newer call reported its verification failure.
            store.SaveJsonSync(staleJson, 9);
            store.SaveJsonAsync(staleJson, 9).GetAwaiter().GetResult();
            Require(File.ReadAllText(store.FilePath) == committedJson,
                "a stale save rolled back a recovery whose replacement already succeeded");

            // A real external edit must still be rejected while recovery is pending.
            var external = J(committedJson);
            external["papers"]![0]!["content"] = "external repair after commit";
            var externalJson = external.ToJsonString();
            File.WriteAllText(store.FilePath, externalJson);
            Throws<IOException>(() => store.SaveJsonSync(committedJson, 11));
            Require(File.ReadAllText(store.FilePath) == externalJson,
                "retry ignored a real external change after the recovery commit");
            File.WriteAllText(store.FilePath, committedJson);

            restored.Papers[0].Content = "edit saved after readback retry";
            var retryJson = store.SerializeState(restored);
            if (asynchronous) store.SaveJsonAsync(retryJson, 12).GetAwaiter().GetResult();
            else store.SaveJsonSync(retryJson, 12);
            Require(File.ReadAllText(store.FilePath) == retryJson &&
                    !store.NeedsStartupRecoverySave &&
                    store.TryCollectProtectedImageIds(restored, out _),
                "recovery remained stuck after the temporary read failure ended");
            var remainingSources = RecoverySourcePaths(store, sourceKind);
            Require(sourceKind == "backup"
                    ? remainingSources.Length == 1 &&
                      File.ReadAllBytes(remainingSources[0]).SequenceEqual(sourceBytes)
                    : remainingSources.Length == 0,
                "verified recovery removed its fixed backup or left consumed temporary sources");
        }
        Console.WriteLine("PASS recovery readback failure retains sources, fences stale saves and retries safely");
    }

    private static void RecoveryReloadSupersedesPendingSource()
    {
        foreach (var number in new[] { 1, 2 })
        foreach (var sourceKind in new[] { "backup", "primary-temp", "backup-temp" })
        foreach (var afterCommit in new[] { false, true })
        {
            using var scope = new Scope();
            var seeded = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, number);
            seeded.SaveJsonSync(seeded.SerializeState(NewState()), 1);
            var sourceBytes = File.ReadAllBytes(seeded.FilePath);
            var sourcePath = sourceKind switch
            {
                "backup" => seeded.BackupPath,
                "primary-temp" => seeded.FilePath + ".tmp",
                _ => seeded.BackupPath + ".tmp"
            };
            File.WriteAllBytes(sourcePath, sourceBytes);
            File.WriteAllText(seeded.FilePath, "{corrupt before recovery");
            FileStream? lockedPrimary = null;
            var failReadback = afterCommit;
            var writer = new DurableAtomicFileWriter((stage, target) =>
            {
                if (failReadback && stage == DurableAtomicWriteStage.AfterReplace &&
                    string.Equals(target, seeded.FilePath, StringComparison.Ordinal))
                {
                    failReadback = false;
                    lockedPrimary = new FileStream(target, FileMode.Open,
                        FileAccess.ReadWrite, FileShare.None);
                }
            });
            var store = new StateStore(scope.DirectoryPath, writer, number);
            var restored = store.Load();
            var pendingJson = store.SerializeState(restored);
            if (afterCommit)
            {
                try { Throws<IOException>(() => store.SaveJsonSync(pendingJson, 10)); }
                finally { lockedPrimary?.Dispose(); }
                Require(!failReadback, "reload fixture did not reach committed recovery readback");
            }

            // Rejected reloads must not discard the recovery guard or its sources.
            File.WriteAllText(store.FilePath, "{different corrupt external edit");
            var invalid = store.ReloadExternalState(11);
            Require(invalid.Result.Outcome == "invalid" && store.NeedsStartupRecoverySave,
                "an invalid reload canceled pending recovery");
            Throws<IOException>(() => store.SaveJsonSync(pendingJson, 12));

            var external = J(pendingJson);
            external["papers"]![0]!["content"] = "explicitly accepted repaired primary";
            var externalJson = external.ToJsonString();
            File.WriteAllText(store.FilePath, externalJson);
            var accepted = store.ReloadExternalState(13);
            Require(accepted.Result.Applied && accepted.State != null &&
                    !store.NeedsStartupRecoverySave &&
                    File.ReadAllText(store.FilePath) == externalJson &&
                    (sourceKind == "primary-temp" && afterCommit
                        ? RecoverySourcePaths(store, sourceKind).Length == 0
                        : RecoverySourcePaths(store, sourceKind)
                            .Any(path => File.ReadAllBytes(path).SequenceEqual(sourceBytes))) &&
                    store.TryCollectProtectedImageIds(accepted.State!, out _),
                "accepted reload did not retire only orphaned sources beside its healthy primary");

            accepted.State!.Papers[0].Content = "normal edit after explicit reload";
            var editedJson = store.SerializeState(accepted.State!);
            store.SaveJsonSync(editedJson, 14);
            store.SaveJsonAsync(pendingJson, 12).GetAwaiter().GetResult();
            Require(File.ReadAllText(store.FilePath) == editedJson &&
                    (sourceKind == "primary-temp"
                        ? RecoverySourcePaths(store, sourceKind).Length == 0
                        : RecoverySourcePaths(store, sourceKind)
                            .Any(path => File.ReadAllBytes(path).SequenceEqual(sourceBytes))),
                "accepted reload could not save normally or cleaned a non-orphaned backup source");
        }
        Console.WriteLine("PASS only accepted reload ends pending recovery and retires orphaned sources");
    }

    private static string[] RecoverySourcePaths(StateStore store, string sourceKind)
    {
        var source = sourceKind switch
        {
            "backup" => store.BackupPath,
            "primary-temp" => store.FilePath + ".tmp",
            _ => store.BackupPath + ".tmp"
        };
        if (sourceKind == "backup") return File.Exists(source) ? [source] : [];
        return Directory.GetFiles(Path.GetDirectoryName(source)!,
                Path.GetFileNameWithoutExtension(source) + ".orphaned.*.tmp")
            .Concat(File.Exists(source) ? new[] { source } : Array.Empty<string>()).ToArray();
    }
}
