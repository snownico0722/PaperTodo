using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaperTodo;

/// <summary>One recovery document and its temporary images, outside every workspace.</summary>
internal sealed class StandaloneStudioRecovery
{
    internal StandaloneStudioRecovery(string? rootDirectory = null)
    {
        var user = Environment.UserDomainName + "\\" + Environment.UserName;
        var userId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..16];
        RootDirectory = rootDirectory ?? Path.Combine(Path.GetTempPath(), "PaperTodo", "Studio-" + userId);
        AssetsDirectory = Path.Combine(RootDirectory, "images");
    }

    internal string RootDirectory { get; }
    internal string AssetsDirectory { get; }
    private string SnapshotPath => Path.Combine(RootDirectory, "document.json");
    internal bool HasSnapshot => Path.Exists(SnapshotPath);

    internal void Save(StandaloneStudioDocument document)
    {
        var snapshot = new Snapshot
        {
            FilePath = document.FilePath,
            Text = document.Text,
            SavedText = document.SavedText,
            EncodingCodePage = document.EncodingCodePage,
            HasBom = document.HasBom,
            NewLine = document.NewLine
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        Directory.CreateDirectory(RootDirectory);
        StandaloneStudioDocument.AtomicWriteBytes(SnapshotPath, bytes);
    }

    internal StandaloneStudioDocument? Load()
    {
        if (!HasSnapshot) return null;
        var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllBytes(SnapshotPath));
        if (snapshot == null || snapshot.Version != 1 || snapshot.Text == null ||
            snapshot.SavedText == null || snapshot.NewLine == null)
            throw new InvalidDataException("Invalid studio recovery document.");
        return StandaloneStudioDocument.Restore(snapshot.FilePath, snapshot.Text, snapshot.SavedText,
            snapshot.EncodingCodePage, snapshot.HasBom, snapshot.NewLine);
    }

    internal void Discard()
    {
        // Remove the manifest first: if its removal fails, its images stay intact.
        File.Delete(SnapshotPath);
        try
        {
            if (Directory.Exists(AssetsDirectory)) Directory.Delete(AssetsDirectory, recursive: true);
            if (Directory.Exists(RootDirectory) && !Directory.EnumerateFileSystemEntries(RootDirectory).Any())
                Directory.Delete(RootDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The user has already saved or discarded this scene. Cleanup must not
            // cancel close after some temporary images have already been removed.
            System.Diagnostics.Trace.WriteLine($"Studio temporary image cleanup failed: {ex.Message}");
        }
    }

    private sealed class Snapshot
    {
        public Snapshot() { }
        public int Version { get; set; } = 1;
        public string? FilePath { get; set; }
        public string? Text { get; set; }
        public string? SavedText { get; set; }
        public int EncodingCodePage { get; set; }
        public bool HasBom { get; set; }
        public string? NewLine { get; set; }
    }
}
