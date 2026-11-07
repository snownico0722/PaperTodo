using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperTodo;

internal enum WorkspaceRecoveryChoice { Cancel, CreateEmpty, Restore, Exit, Fallback, Retry }

internal sealed class WorkspaceRecoveryExitException : OperationCanceledException { }

internal sealed class WorkspaceRecoveryFallbackException : OperationCanceledException { }

public sealed partial class StateStore
{
    private sealed class PendingStartupRecovery
    {
        internal string? SourcePath { get; set; }
        internal byte[] SourceHash { get; }
        internal byte[]? ExpectedPrimaryHash { get; set; }
        internal bool IsTemporary { get; }
        internal List<(string Path, byte[] Hash)> EquivalentTempPaths { get; }

        private PendingStartupRecovery(
            string? sourcePath, byte[] sourceHash, byte[]? originalPrimary,
            bool fromTemporary, IEnumerable<(string Path, byte[] Hash)> equivalentTempPaths)
        {
            SourcePath = sourcePath;
            SourceHash = sourceHash;
            ExpectedPrimaryHash = originalPrimary == null ? null : SHA256.HashData(originalPrimary);
            IsTemporary = fromTemporary;
            EquivalentTempPaths = equivalentTempPaths.ToList();
        }

        internal static PendingStartupRecovery FromFixedBackup(
            string path, byte[] backupBytes, byte[]? originalPrimary) =>
            new(path, SHA256.HashData(backupBytes), originalPrimary, false, []);

        internal static PendingStartupRecovery FromHistory(
            CoreHistoryBackup.WorkspaceSnapshot snapshot, byte[]? originalPrimary) =>
            new(snapshot.ArchivePath, snapshot.ArchiveHash, originalPrimary, false, []);

        internal static PendingStartupRecovery ForEmptyWorkspace(byte[]? originalPrimary) =>
            new(null, [], originalPrimary, false, []);

        internal static PendingStartupRecovery FromTemporary(
            string path, byte[] tempBytes, byte[]? originalPrimary,
            (string Path, byte[] Hash)[] equivalentPaths) =>
            new(path, SHA256.HashData(tempBytes), originalPrimary, true, equivalentPaths);
    }

    private PendingStartupRecovery? _pendingStartupRecovery;

    private string[] InterruptedWritePaths()
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        var result = new List<string>();
        foreach (var tmp in new[] { FilePath + ".tmp", BackupPath + ".tmp" })
        {
            if (FileExistsForCollection(tmp)) result.Add(tmp);
            result.AddRange(Directory.EnumerateFiles(
                directory, Path.GetFileNameWithoutExtension(tmp) + ".orphaned.*.tmp"));
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Caller holds the write lock. Orphans are only a fallback while the primary
    // is unavailable, not another history tier. Never retire a pending recovery
    // source before its commit/readback completes. Maintenance cannot reject a save.
    private void TryCleanupOrphanedTempsCore()
    {
        if (WriteLifetime.IsStopped || _pendingStartupRecovery != null) return;
        try
        {
            var directory = Path.GetDirectoryName(FilePath)!;
            var paths = new[] { FilePath, BackupPath }
                .SelectMany(path => Directory.EnumerateFiles(
                    directory, Path.GetFileName(path) + ".orphaned.*.tmp")).ToArray();
            if (paths.Length == 0 || !TryReadValidatedStateBytes(FilePath, out _)) return;

            foreach (var path in paths)
            {
                WriteLifetime.ThrowIfStopped();
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning("Orphaned temporary cleanup skipped: {0}", ex);
        }
    }

    private sealed record CompleteInterruptedWrite(
        string Path, byte[] Bytes, JsonObject Document, AppState State, bool NormalizedCollapsedState);

    // This is full-snapshot validation, not extraction or merging. Unknown JSON
    // properties stay in Document and participate in equality comparisons.
    private bool TryReadCompleteDocument(
        byte[] bytes, out JsonObject? document, out AppState? state)
    {
        document = null;
        state = null;
        try
        {
            var json = DecodeStateBytes(bytes);
            var root = JsonNode.Parse(json, documentOptions: StateJsonReadPolicy.DocumentOptions)
                as JsonObject ?? throw new JsonException("Recovery input must be an object.");
            ValidateExternalDocument(root);
            state = ReadLoadDocument(bytes);
            document = root;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return false; // Invalid whole document: never try to salvage fragments.
        }
    }

    private CompleteInterruptedWrite? FindLatestInterruptedWrite(string[] paths,
        out List<(string Path, byte[] Hash)> equivalentPaths, out Exception? readFailure)
    {
        readFailure = null;
        equivalentPaths = [];
        var ordered = new List<(string Path, DateTime LastWriteTimeUtc)>();
        foreach (var path in paths)
        {
            try { ordered.Add((path, File.GetLastWriteTimeUtc(path))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (ex is not (FileNotFoundException or DirectoryNotFoundException)) readFailure ??= ex;
            }
        }

        // Sort metadata first. Retain only the chosen full snapshot and hashes of
        // equivalent sources, instead of every candidate's bytes and parsed tree.
        CompleteInterruptedWrite? latest = null;
        foreach (var item in ordered.OrderByDescending(item => item.LastWriteTimeUtc)
                     .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = File.ReadAllBytes(item.Path);
                if (!TryReadCompleteDocument(bytes, out var document, out var state)) continue;
                latest ??= new CompleteInterruptedWrite(item.Path, bytes, document!, state!,
                    NormalizedCollapsedStateOnLoad);
                if (JsonNode.DeepEquals(latest.Document, document))
                    equivalentPaths.Add((item.Path, SHA256.HashData(bytes)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A failed candidate must not veto another complete snapshot.
                // Retain access errors only to prevent an unverified blank rebuild.
                if (ex is not (FileNotFoundException or DirectoryNotFoundException)) readFailure ??= ex;
            }
        }
        return latest;
    }

    // Normal saves and backup refresh use fixed .tmp names, even when the last
    // run left an uncommitted, fully valid snapshot at that path. A differing
    // *complete* document stays intact until the new primary is healthy.
    // Moving it within the same directory does not allocate a second backup.
    private bool PreserveDistinctTemporarySource(
        string tempPath, string committingJson, Action validateWrite)
    {
        if (!FileExistsForCollection(tempPath)) return false;
        var bytes = File.ReadAllBytes(tempPath);
        if (!TryReadCompleteDocument(bytes, out var document, out _))
            return false; // Broken temp is explicitly outside the recovery contract.

        var intended = JsonNode.Parse(
            committingJson, documentOptions: StateJsonReadPolicy.DocumentOptions);
        if (JsonNode.DeepEquals(document, intended))
            return false; // Complete duplicate; no need to retain another generation.

        validateWrite();
        // Validate the read source a second time so a racing writer cannot turn
        // the selected snapshot into an unchecked replacement.
        var originalHash = SHA256.HashData(bytes);
        if (!originalHash.AsSpan().SequenceEqual(ReadFileHash(tempPath)))
            throw new StateRecoveryException("WorkspaceFailureRecoveryChanged");
        var archived = MoveRecoverySource(tempPath, "orphaned", validateWrite);
        // Another equivalent temp may be selected as the recovery authority.
        // Keep the path fingerprint aligned so a successful full restore also
        // retires this byte-distinct but semantically equivalent source.
        if (_pendingStartupRecovery is { IsTemporary: true } pending)
        {
            for (var i = 0; i < pending.EquivalentTempPaths.Count; i++)
            {
                var item = pending.EquivalentTempPaths[i];
                if (string.Equals(item.Path, tempPath, StringComparison.OrdinalIgnoreCase))
                    pending.EquivalentTempPaths[i] = (archived, item.Hash);
            }
        }
        System.Diagnostics.Trace.TraceInformation(
            "Preserved a distinct complete interrupted-write snapshot: {0}",
            Path.GetFileName(archived));
        return true;
    }

    private static byte[] ReadFileHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(stream);
    }

    private void ValidatePendingStartupRecovery()
    {
        var pending = _pendingStartupRecovery;
        if (pending == null) return;
        var actualPrimary = ReadPrimaryHash();
        if (pending.ExpectedPrimaryHash == null
            ? actualPrimary != null
            : actualPrimary == null ||
              !pending.ExpectedPrimaryHash.AsSpan().SequenceEqual(actualPrimary))
            throw new StateRecoveryException("WorkspaceFailurePrimaryChanged");

        if (pending.SourcePath is { } source &&
            !pending.SourceHash.AsSpan().SequenceEqual(ReadFileHash(source)))
            throw new StateRecoveryException("WorkspaceFailureRecoveryChanged");
    }

    private void PreparePendingStartupRecoveryForSave(Action validateWrite)
    {
        var pending = _pendingStartupRecovery;
        if (pending == null || !pending.IsTemporary) return;
        var primaryTemp = FilePath + ".tmp";
        if (!string.Equals(pending.SourcePath, primaryTemp, StringComparison.OrdinalIgnoreCase))
            return;

        // This fixed path will be opened with FileMode.Create. Move the sole
        // recoverable source within the directory first, without duplicating it.
        validateWrite();
        var moved = MoveRecoverySource(primaryTemp, "orphaned", validateWrite);
        for (var index = 0; index < pending.EquivalentTempPaths.Count; index++)
        {
            var fingerprint = pending.EquivalentTempPaths[index];
            if (string.Equals(fingerprint.Path, primaryTemp,
                StringComparison.OrdinalIgnoreCase))
                pending.EquivalentTempPaths[index] = (moved, fingerprint.Hash);
        }
        pending.SourcePath = moved;
    }

    private void CompletePendingStartupRecovery(byte[] savedBytes)
    {
        var pending = _pendingStartupRecovery;
        if (pending == null) return;
        // The atomic writer has already committed these bytes. Advance the
        // expected primary BEFORE any fallible readback, so a retry recognizes
        // our own successful replacement instead of the pre-recovery document.
        // Keep the source protected until verification and cleanup complete.
        var committedHash = SHA256.HashData(savedBytes);
        pending.ExpectedPrimaryHash = committedHash;
        // Only retire a source after the committed primary is readable and contains
        // exactly the bytes that the normal durable writer was asked to commit.
        if (!ReadFileHash(FilePath).AsSpan().SequenceEqual(committedHash) ||
            !TryReadValidatedStateBytes(FilePath, out _))
            throw new StateRecoveryException("WorkspaceFailureRecoveryUnverified");

        _pendingStartupRecovery = null;
        if (!pending.IsTemporary) return; // Fixed backups and history ZIPs remain intact.

        foreach (var (path, hash) in pending.EquivalentTempPaths)
        {
            try
            {
                if (File.Exists(path) && hash.AsSpan().SequenceEqual(ReadFileHash(path)))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A cleanup failure cannot turn a committed recovery into data loss.
                System.Diagnostics.Trace.TraceWarning(
                    "Recovered temporary file could not be retired: {0}", ex);
            }
        }
    }
}
