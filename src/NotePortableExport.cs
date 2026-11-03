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
    private sealed record ExportImage(string EntryPath, string MarkdownUrl, byte[] Bytes);
    private sealed record MarkdownOutput(string Text, IReadOnlyList<ExportImage> Images);

    internal static NoteExportResult Export(string destination, IReadOnlyList<NoteExportDocument> notes,
        NoteExportFormat format, bool all, NoteImageStore images, string missingImageLabel,
        IProgress<int>? progress = null)
    {
        if (notes.Count == 0) throw new ArgumentException("No notes to export.", nameof(notes));
        return all
            ? ExportZip(destination, notes, format, images, missingImageLabel, progress)
            : ExportSingle(destination, notes[0], format, images, missingImageLabel, progress);
    }

    private static NoteExportResult ExportSingle(string path, NoteExportDocument note,
        NoteExportFormat format, NoteImageStore images, string missingLabel, IProgress<int>? progress)
    {
        var missing = new HashSet<string>(StringComparer.Ordinal);
        if (format == NoteExportFormat.Html)
        {
            var html = NoteHtmlExport.Render(note.Id, note.Title, note.Content, images,
                missingLabel, missing);
            NoteHtmlExport.Save(path, html);
        }
        else
        {
            var folderName = AvailableImageFolder(path);
            var result = RenderMarkdown(note, folderName, images, missingLabel, missing);
            var folderPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, folderName);
            var madeFolder = false;
            try
            {
                if (result.Images.Count != 0)
                {
                    Directory.CreateDirectory(folderPath);
                    madeFolder = true;
                    foreach (var image in result.Images)
                    {
                        var fileName = Path.GetFileName(image.EntryPath);
                        using var file = new FileStream(Path.Combine(folderPath, fileName),
                            FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        file.Write(image.Bytes);
                    }
                }
                SaveAtomic(path, Encoding.UTF8.GetBytes(result.Text));
            }
            catch
            {
                if (madeFolder)
                {
                    try { Directory.Delete(folderPath, recursive: true); }
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
        NoteExportFormat format, NoteImageStore images, string missingLabel, IProgress<int>? progress)
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
                        var note = notes[index];
                        var stem = UniqueName(SafeName(note.Title), names);
                        var missing = new HashSet<string>(StringComparer.Ordinal);
                        MarkdownOutput? markdown = null;
                        string? html = null;
                        try
                        {
                            if (format == NoteExportFormat.Markdown)
                                markdown = RenderMarkdown(note, stem + "-images", images,
                                    missingLabel, missing);
                            else
                                html = NoteHtmlExport.Render(note.Id, note.Title, note.Content,
                                    images, missingLabel, missing);
                        }
                        catch (Exception ex)
                        {
                            failures.Add(note.Title + ": " + ex.Message);
                            progress?.Report(index + 1);
                            continue;
                        }

                        if (format == NoteExportFormat.Markdown)
                        {
                            foreach (var image in markdown!.Images)
                                WriteEntry(zip, image.EntryPath, image.Bytes);
                            WriteEntry(zip, stem + ".md", Encoding.UTF8.GetBytes(markdown.Text));
                        }
                        else
                        {
                            WriteEntry(zip, stem + ".html", Encoding.UTF8.GetBytes(html!));
                        }

                        exported++;
                        missingCount += missing.Count;
                        if (missing.Count > 0) affectedNotes++;
                        progress?.Report(index + 1);
                    }
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        finally { TryDelete(tempPath); }

        return new NoteExportResult(exported, missingCount, affectedNotes, failures);
    }

    private static MarkdownOutput RenderMarkdown(NoteExportDocument note, string imageFolder,
        NoteImageStore store, string missingLabel, ISet<string> missing)
    {
        var files = new Dictionary<string, ExportImage>(StringComparer.Ordinal);
        var builder = new StringBuilder(note.Content.Length);
        var position = 0;
        foreach (var reference in MarkdownImageReferences.Enumerate(note.Content))
        {
            builder.Append(note.Content, position, reference.LineStart - position);
            if (store.TryGetEncodedImageBytes(reference.ImageId, out var asset, out var bytes) &&
                string.Equals(asset.NoteId, note.Id, StringComparison.Ordinal))
            {
                if (!files.TryGetValue(reference.ImageId, out var image))
                {
                    var entryPath = imageFolder + "/" + reference.ImageId + ExtensionFromMime(asset.Mime);
                    var markdownUrl = "./" + Uri.EscapeDataString(imageFolder) + "/" +
                        reference.ImageId + ExtensionFromMime(asset.Mime);
                    image = new ExportImage(entryPath, markdownUrl, bytes);
                    files.Add(reference.ImageId, image);
                }
                builder.Append(reference.WithUrl(image.MarkdownUrl));
            }
            else
            {
                missing.Add(reference.ImageId);
                builder.Append('[').Append(missingLabel).Append(": ")
                    .Append(reference.ImageId).Append(']');
            }
            position = reference.LineStart + reference.LineLength;
        }
        builder.Append(note.Content, position, note.Content.Length - position);
        return new MarkdownOutput(builder.ToString(), files.Values.ToArray());
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes);
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

    internal static void SaveAtomic(string path, byte[] bytes)
    {
        var tempPath = TempPath(path);
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
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
