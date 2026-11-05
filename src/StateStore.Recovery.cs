using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperTodo;

public sealed partial class StateStore
{
    private sealed class PendingStartupRecovery
    {
        internal string SourcePath { get; set; }
        internal byte[] SourceHash { get; }
        internal byte[]? ExpectedPrimaryHash { get; }
        internal bool FromTemporary { get; }
        internal List<string> EquivalentTempPaths { get; }

        private PendingStartupRecovery(
            string sourcePath, byte[] sourceBytes, byte[]? originalPrimary,
            bool fromTemporary, IEnumerable<string> equivalentTempPaths)
        {
            SourcePath = sourcePath;
            SourceHash = SHA256.HashData(sourceBytes);
            ExpectedPrimaryHash = originalPrimary == null ? null : SHA256.HashData(originalPrimary);
            FromTemporary = fromTemporary;
            EquivalentTempPaths = equivalentTempPaths.ToList();
        }

        internal static PendingStartupRecovery FromFixedBackup(
            string path, byte[] backupBytes, byte[]? originalPrimary) =>
            new(path, backupBytes, originalPrimary, false, []);

        internal static PendingStartupRecovery FromTemporary(
            string path, byte[] tempBytes, byte[]? originalPrimary, string[] equivalentPaths) =>
            new(path, tempBytes, originalPrimary, true, equivalentPaths);
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

    private bool HasInterruptedWriteFiles() => InterruptedWritePaths().Length != 0;

    private List<(string Path, byte[] Bytes, AppState State)> FindValidInterruptedWrites()
    {
        var valid = new List<(string, byte[], AppState)>();
        foreach (var path in InterruptedWritePaths())
        {
            // Read errors are not corruption. An in-use file must not authorize
            // an empty replacement or be silently skipped as an obsolete snapshot.
            var bytes = File.ReadAllBytes(path);
            try
            {
                var json = DecodeStateBytes(bytes);
                var root = JsonNode.Parse(json, documentOptions: StateJsonReadPolicy.DocumentOptions)
                    as JsonObject ?? throw new JsonException("Recovery input must be a JSON object.");
                ValidateExternalDocument(root); // reject duplicate IDs / malformed array entries
                var state = ReadLoadDocument(bytes);
                valid.Add((path, bytes, state));
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                // Damaged fragments are deliberately not salvaged.
            }
        }
        return valid;
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
            throw new IOException("The primary file changed during automatic recovery; refusing overwrite.");

        if (!pending.SourceHash.AsSpan().SequenceEqual(ReadFileHash(pending.SourcePath)))
            throw new IOException("The automatic recovery source changed; refusing overwrite.");
    }

    private void PreparePendingStartupRecoveryForSave(Action validateWrite)
    {
        var pending = _pendingStartupRecovery;
        if (pending == null || !pending.FromTemporary) return;
        var primaryTemp = FilePath + ".tmp";
        if (!string.Equals(pending.SourcePath, primaryTemp, StringComparison.OrdinalIgnoreCase))
            return;

        // This fixed path will be opened with FileMode.Create. Move the sole
        // recoverable source within the directory first, without duplicating it.
        validateWrite();
        var moved = MoveRecoverySource(primaryTemp, "orphaned", validateWrite);
        for (var index = 0; index < pending.EquivalentTempPaths.Count; index++)
            if (string.Equals(pending.EquivalentTempPaths[index], primaryTemp,
                StringComparison.OrdinalIgnoreCase))
                pending.EquivalentTempPaths[index] = moved;
        pending.SourcePath = moved;
    }

    private void CompletePendingStartupRecovery(byte[] savedBytes)
    {
        var pending = _pendingStartupRecovery;
        if (pending == null) return;
        // Only retire a source after the committed primary is readable and contains
        // exactly the bytes that the normal durable writer was asked to commit.
        if (!ReadFileHash(FilePath).AsSpan().SequenceEqual(SHA256.HashData(savedBytes)) ||
            !TryReadValidatedStateBytes(FilePath, out _))
            throw new IOException("Recovery commit could not be verified; the source was retained.");

        _pendingStartupRecovery = null;
        if (!pending.FromTemporary) return; // A fixed backup remains the normal backup.

        foreach (var path in pending.EquivalentTempPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(path) &&
                    pending.SourceHash.AsSpan().SequenceEqual(ReadFileHash(path)))
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
