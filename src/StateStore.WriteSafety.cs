using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace PaperTodo;

public sealed partial class StateStore
{
    internal PersistenceWriteLifetime WriteLifetime { get; set; } = new();

    internal void StopWritesForTermination() => WriteLifetime.Stop();

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(true, true);
    // Test UTF-32 first because its little-endian BOM starts with the UTF-16 LE BOM.
    private static readonly Encoding[] StateEncodings =
    [
        new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true),
        new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true),
        StrictUtf8
    ];

    private static string DecodeStateBytes(byte[] bytes)
    {
        ReadOnlySpan<byte> data = bytes;
        try
        {
            foreach (var encoding in StateEncodings)
                if (data.StartsWith(encoding.Preamble))
                    return encoding.GetString(data[encoding.Preamble.Length..]);
            return StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException ex)
        {
            throw new JsonException("The state file contains an invalid text encoding.", ex);
        }
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

    // Only explicit merge/reload and workspace-catalog transactions use source validation.
    // Ordinary autosaves do not observe or compare an external data.json generation.
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
    }

    private void BeginExternalRead(long version)
    {
        WriteLifetime.ThrowIfStopped();
        // An explicitly requested reload/merge supersedes snapshots queued before it.
        _latestWrittenVersion = version;
    }

    private void ValidatePrimaryHash(byte[]? expected)
    {
        WriteLifetime.ThrowIfStopped();
        var actual = ReadPrimaryHash();
        if (expected == null ? actual == null : actual != null && expected.AsSpan().SequenceEqual(actual))
            return;
        throw new ExternalDocumentChangedException();
    }

    private void ValidateExplicitSource(string observed, byte[] source)
    {
        WriteLifetime.ThrowIfStopped();
        try
        {
            if (string.Equals(DecodeStateBytes(source), observed, StringComparison.Ordinal)) return;
        }
        catch (JsonException) { /* The previously valid source changed. */ }
        throw new ExternalDocumentChangedException();
    }
}

/// <summary>
/// One process/session write admission signal, shared by all of its workspace stores.
/// Stop never waits on I/O or dispatches UI work, so it is safe at the crash boundary.
/// An already admitted OS replacement may finish; no queued writer may start afterwards.
/// </summary>
internal sealed class PersistenceWriteLifetime
{
    private int _stopped;
    internal bool IsStopped => Volatile.Read(ref _stopped) != 0;
    internal void Stop() => Interlocked.Exchange(ref _stopped, 1);
    internal void ThrowIfStopped()
    {
        if (IsStopped) throw new OperationCanceledException("Persistence is stopping; no new write was accepted.");
    }
}

internal static class StateJsonReadPolicy
{
    public const JsonCommentHandling CommentHandling = JsonCommentHandling.Skip;
    public const bool AllowTrailingCommas = true;

    public static JsonDocumentOptions DocumentOptions => new()
    {
        CommentHandling = CommentHandling,
        AllowTrailingCommas = AllowTrailingCommas
    };
}
