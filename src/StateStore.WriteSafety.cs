using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PaperTodo;

internal class StateWriteBlockedException : InvalidOperationException
{
    internal StateWriteBlockedException() : base(Strings.Get("DataSavePausedExternal")) { }
}

public sealed partial class StateStore
{
    internal PersistenceWriteLifetime WriteLifetime { get; set; } = new();
    private int _ordinaryWritesPaused;
    // Only a fingerprint of the last accepted bytes, NOT a merge baseline or history.
    // Access under _writeLock (Load is called only before binding a live session).
    private byte[]? _acceptedPrimaryHash;
    private bool _hasPrimaryObservation;
    internal bool OrdinaryWritesPaused => Volatile.Read(ref _ordinaryWritesPaused) != 0;

    internal void StopWritesForTermination() => WriteLifetime.Stop();

    private static string DecodeStateBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static AppState? ReadCoreState(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json, StateJsonReadPolicy.DocumentOptions);
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("papers", out var papers) ||
            papers.ValueKind != System.Text.Json.JsonValueKind.Array ||
            papers.EnumerateArray().Any(p => p.ValueKind != System.Text.Json.JsonValueKind.Object))
            throw new System.Text.Json.JsonException("A state document requires a papers array of objects.");
        return System.Text.Json.JsonSerializer.Deserialize<AppState>(json, JsonOptions);
    }

    private byte[]? ReadPrimaryHash()
    {
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return SHA256.HashData(stream);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        // Access denied, a directory at the path, etc. are NOT an absent document.
    }

    private void AcceptPrimaryBytes(byte[]? bytes)
    {
        _acceptedPrimaryHash = bytes == null ? null : SHA256.HashData(bytes);
        _hasPrimaryObservation = true;
        Volatile.Write(ref _ordinaryWritesPaused, 0);
    }

    private void BeginExternalRead(long version)
    {
        WriteLifetime.ThrowIfStopped();
        // Fence even invalid/unreadable imports. An older queued snapshot must not erase
        // an input just because validation failed; a newer ordinary save cannot bypass this.
        Volatile.Write(ref _ordinaryWritesPaused, 1);
        _latestWrittenVersion = version;
    }

    private void ValidateOrdinaryWriteSource()
    {
        WriteLifetime.ThrowIfStopped();
        if (OrdinaryWritesPaused) throw new StateWriteBlockedException();
        // Preserve StateStore's write-without-Load API for fresh/bootstrap/test writers.
        // Every live application workspace binds its accepted bytes during Load first.
        if (!_hasPrimaryObservation)
        {
            _acceptedPrimaryHash = ReadPrimaryHash();
            _hasPrimaryObservation = true;
        }
        ValidatePrimaryHash(_acceptedPrimaryHash);
    }

    private void ValidatePrimaryHash(byte[]? expected)
    {
        WriteLifetime.ThrowIfStopped();
        var actual = ReadPrimaryHash();
        if (expected == null ? actual == null : actual != null && expected.AsSpan().SequenceEqual(actual))
            return;
        Volatile.Write(ref _ordinaryWritesPaused, 1);
        throw new ExternalDocumentChangedException();
    }

    private void ValidateExplicitSource(string observed, byte[] source)
    {
        WriteLifetime.ThrowIfStopped();
        if (!string.Equals(DecodeStateBytes(source), observed, StringComparison.Ordinal))
            throw new ExternalDocumentChangedException();
    }
}
