using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperTodo;

internal sealed class WorkbenchTransferException : Exception
{
    public WorkbenchTransferException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}

public sealed partial class StateStore
{
    internal const int WorkbenchMaxPapersPerDocument = 200;

    /// <summary>
    /// Read-only snapshot of this document for the workbench overview of an inactive
    /// workspace. It never falls back to the backup, never normalizes into the store, and never
    /// writes. A missing primary file is reported as <see cref="FileNotFoundException"/>.
    /// </summary>
    internal AppState ReadDocumentForWorkbenchPreview()
    {
        byte[] bytes;
        using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }
        return ReadCoreState(DecodeStateBytes(bytes))
            ?? throw new JsonException($"{Path.GetFileName(FilePath)} deserialized to null.");
    }

    /// <summary>
    /// Appends one already-serialized paper to this (inactive) workspace document under this
    /// store's writer lock and version fence. Like the catalog update, only the papers array is
    /// touched: unknown properties, settings and every other paper are preserved byte-for-node,
    /// and the replace is rejected if the file changed after it was read.
    /// </summary>
    internal void AppendWorkbenchTransferredPaper(JsonObject paper, long version)
    {
        ArgumentNullException.ThrowIfNull(paper);
        var paperId = paper["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var id)
            ? id
            : throw new ArgumentException("A transferred paper requires an id.", nameof(paper));
        _writeLock.Wait();
        try
        {
            if (version < _latestWrittenVersion)
                throw new WorkbenchTransferException("busy", "A newer save of the target workspace already completed.");
            WriteLifetime.ThrowIfStopped();
            if (!File.Exists(FilePath))
                throw new WorkbenchTransferException("missing", "The target workspace has not been created yet.");

            var bytes = File.ReadAllBytes(FilePath);
            var json = DecodeStateBytes(bytes);
            var document = JsonNode.Parse(json, documentOptions: StateJsonReadPolicy.DocumentOptions)
                as JsonObject ?? throw new JsonException("The target workspace must contain an object.");
            ValidateExternalDocument(document);
            var papers = (JsonArray)document["papers"]!;
            if (papers.Count >= WorkbenchMaxPapersPerDocument)
                throw new WorkbenchTransferException("paper_limit", "The target workspace is full.");
            foreach (var node in papers)
            {
                if (node?["id"] is JsonValue existing &&
                    existing.TryGetValue<string>(out var existingId) &&
                    string.Equals(existingId, paperId, StringComparison.Ordinal))
                {
                    throw new WorkbenchTransferException("duplicate", "The target workspace already contains this paper.");
                }
            }

            papers.Add(paper.DeepClone());
            // The appended paper must still be readable by the ordinary loader.
            _ = ReadCoreState(document.ToJsonString(JsonOptions))
                ?? throw new JsonException("The transferred paper made the document unreadable.");
            var sourceHash = System.Security.Cryptography.SHA256.HashData(bytes);
            WriteJsonInternal(document.ToJsonString(JsonOptions),
                beforeReplace: () => ValidatePrimaryHash(sourceHash));
            _latestWrittenVersion = version;
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>The persisted JSON shape of one paper, with save-time repairs applied.</summary>
    internal static JsonObject SerializePaperForWorkbenchTransfer(PaperData paper)
    {
        WorkbenchPaperState.RepairForSave(paper);
        return JsonSerializer.SerializeToNode(paper, JsonOptions)!.AsObject();
    }
}
