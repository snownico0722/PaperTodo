using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PaperTodo;

public sealed partial class StateStore
{
    internal PersistenceWriteLifetime WriteLifetime { get; set; } = new();

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
        if (!string.Equals(DecodeStateBytes(source), observed, StringComparison.Ordinal))
            throw new ExternalDocumentChangedException();
    }
}
