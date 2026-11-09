using System.IO;
using System.IO.Compression;
using System.Text;

namespace PaperTodo;

internal enum NoteExportFormat { Markdown, Html }
internal sealed record NoteExportDocument(string Id, string Title, string Content);
internal sealed record NoteExportResult(
    int Exported, int MissingImages, int NotesWithMissingImages,
    IReadOnlyList<string> FailedNotes);

internal static class NotePortableExport
{
    internal static NoteExportDocument FromTodoItems(string id, string title, IReadOnlyList<PaperItem> items)
    {
        // Work on the captured items, off the UI thread; reuse the clipboard's
        // literal-text and multiline Markdown rules.
        var content = TodoClipboardFormatter.ToMarkdown(items
            .OrderBy(item => item.Order)
            .Where(TodoRules.HasMeaningfulContent)
            .Select(item => (item.Text, item.Done)));
        return new NoteExportDocument(id, title, content);
    }

    internal static NoteExportResult Export(string destination, IReadOnlyList<NoteExportDocument> notes,
        NoteExportFormat format, bool all, NoteImageStore images, string missingImageLabel,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (notes.Count == 0) throw new ArgumentException("No notes to export.", nameof(notes));
        return all
            ? ExportZip(destination, notes, format, images, missingImageLabel, progress, cancellationToken)
            : ExportSingle(destination, notes[0], format, images, missingImageLabel, progress, cancellationToken);
    }

    private static NoteExportResult ExportSingle(string path, NoteExportDocument note,
        NoteExportFormat format, NoteImageStore images, string missingLabel,
        IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var missing = new HashSet<string>(StringComparer.Ordinal);
        if (format == NoteExportFormat.Html)
        {
            SaveAtomic(path, stream => WriteHtml(stream, note, images, missingLabel, missing, cancellationToken), cancellationToken);
        }
        else
        {
            var folderName = AvailableImageFolder(path);
            var folderPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, folderName);
            var createdFiles = new List<string>();
            var madeFolder = false;
            try
            {
                var markdown = RenderMarkdown(note, folderName, images, missingLabel, missing, cancellationToken,
                    (entryPath, bytes) =>
                    {
                        if (!madeFolder)
                        {
                            Directory.CreateDirectory(folderPath);
                            madeFolder = true;
                        }
                        var imagePath = Path.Combine(folderPath, Path.GetFileName(entryPath));
                        using var file = new FileStream(imagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        createdFiles.Add(imagePath);
                        WriteBytes(file, bytes, cancellationToken);
                    });
                SaveAtomic(path, stream => WriteBytes(stream, Encoding.UTF8.GetBytes(markdown), cancellationToken),
                    cancellationToken);
            }
            catch
            {
                if (madeFolder)
                {
                    foreach (var imagePath in createdFiles) TryDelete(imagePath);
                    try { Directory.Delete(folderPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                throw;
            }
        }

        progress?.Report(1);
        return new NoteExportResult(1, missing.Count, missing.Count > 0 ? 1 : 0, []);
    }

    private static NoteExportResult ExportZip(string path, IReadOnlyList<NoteExportDocument> notes,
        NoteExportFormat format, NoteImageStore images, string missingLabel,
        IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingCount = 0;
        var affectedNotes = 0;
        var failures = new List<string>();
        var exported = 0;
        var tempPath = TempPath(path);
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    for (var index = 0; index < notes.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var note = notes[index];
                        var stem = UniqueName(SafeName(note.Title), names);
                        var missing = new HashSet<string>(StringComparer.Ordinal);
                        try
                        {
                            if (format == NoteExportFormat.Markdown)
                            {
                                var markdown = RenderMarkdown(note, stem + "-images", images,
                                    missingLabel, missing, cancellationToken, (entryPath, bytes) =>
                                        WriteEntry(zip, entryPath, bytes, cancellationToken));
                                WriteEntry(zip, stem + ".md", Encoding.UTF8.GetBytes(markdown), cancellationToken);
                            }
                            else
                            {
                                WriteEntry(zip, stem + ".html", stream =>
                                    WriteHtml(stream, note, images, missingLabel, missing, cancellationToken));
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (IOException) { throw; } // A broken ZIP or destination is a whole-export failure.
                        catch (UnauthorizedAccessException) { throw; }
                        catch (Exception ex)
                        {
                            failures.Add(note.Title + ": " + ex.Message);
                            progress?.Report(index + 1);
                            continue;
                        }

                        exported++;
                        missingCount += missing.Count;
                        if (missing.Count > 0) affectedNotes++;
                        progress?.Report(index + 1);
                    }
                }
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, path, overwrite: true);
        }
        finally { TryDelete(tempPath); }

        return new NoteExportResult(exported, missingCount, affectedNotes, failures);
    }

    private static string RenderMarkdown(NoteExportDocument note, string imageFolder,
        NoteImageStore store, string missingLabel, ISet<string> missing, CancellationToken cancellationToken,
        Action<string, byte[]> writeImage)
    {
        // Only paths are retained. Image bytes leave memory as soon as each image
        // has been written, including when exporting to a ZIP archive.
        var urls = new Dictionary<string, string?>(StringComparer.Ordinal);
        var builder = new StringBuilder(note.Content.Length);
        var position = 0;
        foreach (var reference in MarkdownImageReferences.Enumerate(note.Content))
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append(note.Content, position, reference.LineStart - position);
            if (!urls.TryGetValue(reference.ImageId, out var url))
            {
                if (store.TryGetEncodedImageBytes(reference.ImageId, out var asset, out var bytes) &&
                    string.Equals(asset.NoteId, note.Id, StringComparison.Ordinal))
                {
                    var name = reference.ImageId + ExtensionFromMime(asset.Mime);
                    var entryPath = imageFolder + "/" + name;
                    url = "./" + Uri.EscapeDataString(imageFolder) + "/" + name;
                    writeImage(entryPath, bytes);
                }
                else
                {
                    missing.Add(reference.ImageId);
                    url = null;
                }
                urls.Add(reference.ImageId, url);
            }

            if (url != null)
                builder.Append(reference.WithUrl(url));
            else
            {
                var line = note.Content.AsSpan(reference.LineStart, reference.LineLength);
                builder.Append(line[..(line.Length - line.TrimStart().Length)]);
                builder.Append('[').Append(missingLabel).Append(": ").Append(reference.ImageId).Append(']');
                builder.Append(line[line.TrimEnd().Length..]);
            }
            position = reference.LineStart + reference.LineLength;
        }
        builder.Append(note.Content, position, note.Content.Length - position);
        return builder.ToString();
    }

    private static void WriteHtml(Stream stream, NoteExportDocument note, NoteImageStore images,
        string missingLabel, ISet<string> missing, CancellationToken cancellationToken)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 16 * 1024, leaveOpen: true);
        NoteHtmlExport.Write(writer, note.Id, note.Title, note.Content, images, missingLabel, missing, cancellationToken);
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes, CancellationToken cancellationToken) =>
        WriteEntry(zip, name, stream => WriteBytes(stream, bytes, cancellationToken));

    private static void WriteBytes(Stream stream, byte[] bytes, CancellationToken cancellationToken)
    {
        const int chunkSize = 64 * 1024;
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Write(bytes, offset, Math.Min(chunkSize, bytes.Length - offset));
        }
    }

    private static void WriteEntry(ZipArchive zip, string name, Action<Stream> write)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        write(stream);
    }

    private static string AvailableImageFolder(string documentPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(documentPath))!;
        var name = SafeName(Path.GetFileNameWithoutExtension(documentPath)) + "-images";
        var candidate = name;
        for (var suffix = 2; Directory.Exists(Path.Combine(directory, candidate)); suffix++)
            candidate = name + " (" + suffix + ")";
        return candidate;
    }

    private static string UniqueName(string name, HashSet<string> used)
    {
        var candidate = name;
        for (var suffix = 2; !used.Add(candidate); suffix++)
            candidate = name + " (" + suffix + ")";
        return candidate;
    }

    internal static string SafeName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(title.Select(c =>
            invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (name.Length > 80) name = name[..80].TrimEnd(' ', '.');
        if (name.Length == 0) name = "Note";
        var baseName = name.Split('.')[0];
        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (baseName.Length == 4 &&
             (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
              baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             baseName[3] is >= '1' and <= '9'))
            name = "_" + name;
        return name;
    }

    private static string ExtensionFromMime(string mime) => mime switch
    {
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        "image/tiff" => ".tif",
        "image/webp" => ".webp",
        _ => ".png"
    };

    private static string TempPath(string path) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
            "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

    internal static void SaveAtomic(string path, byte[] bytes) =>
        SaveAtomic(path, stream => stream.Write(bytes));

    internal static void SaveAtomic(string path, Action<Stream> write, CancellationToken cancellationToken = default)
    {
        var tempPath = TempPath(path);
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, path, overwrite: true);
        }
        finally { TryDelete(tempPath); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
