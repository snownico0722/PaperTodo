using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static partial class Program
{
    // Runs inside the isolated real-AppController WPF fixture, so all storage/persistence
    // boundaries here are production code, not a mock image database.
    private static void McpImageLiveChecks(AppController controller)
    {
        var note = controller.State.Papers.Single(p => p.Id == "note-b");
        var originalEnabled = controller.State.McpEnabled;
        var originalAdditive = controller.State.McpAllowBlankWrites;
        var originalFull = controller.State.McpAllowFullWrites;
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 0, 0, 0, 255 }, 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        var png = buffer.ToArray();
        var base64 = Convert.ToBase64String(png);
        var command = new McpCommandService(controller, controller.PaperCommands);
        var imageAssets = Field<Dictionary<string, NoteImageAsset>>(controller.ImageStore, "_images");

        JsonElement Invoke(string method, object parameters) =>
            JsonSerializer.SerializeToElement(command.Execute(
                JsonSerializer.SerializeToElement(new { method, @params = parameters }))!);
        void Rejected(string code, Action action)
        {
            try { action(); }
            catch (McpApiException ex)
            {
                Require(ex.Code == code, $"Expected {code}, got {ex.Code}.");
                return;
            }
            throw new InvalidOperationException("Expected MCP error: " + code);
        }

        try
        {
            controller.State.McpEnabled = true;
            controller.State.McpAllowBlankWrites = false;
            controller.State.McpAllowFullWrites = false;
            var originalText = note.Content;
            var originalCount = imageAssets.Count;
            Rejected("blank_writes_disabled",
                () => Invoke("insert_note_image", new { paper_id = note.Id, image_base64 = base64 }));
            Require(note.Content == originalText && imageAssets.Count == originalCount,
                "Denied MCP image import changed note or image store.");

            controller.State.McpAllowBlankWrites = true;
            var added = Invoke("insert_note_image",
                new { paper_id = note.Id, image_base64 = base64, image_name = "fixture.png" });
            var imageId = added.GetProperty("image_id").GetString()!;
            var reference = added.GetProperty("markdown_reference").GetString()!;
            Require(reference == MarkdownImageReferences.CreateReference(imageId) &&
                    note.Content.EndsWith(reference, StringComparison.Ordinal) &&
                    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data.json"))
                        .Contains(reference, StringComparison.Ordinal),
                "Base64 import did not persist the Markdown reference alongside the LMDB image.");
            var read = Invoke("read_note_image", new { paper_id = note.Id, image_id = imageId });
            Require(read.GetProperty("mime").GetString() == "image/png" &&
                    Convert.FromBase64String(read.GetProperty("image_base64").GetString()!)
                        .SequenceEqual(png),
                "MCP read did not return the same encoded image bytes.");
            var otherNote = new PaperData { Id = "fixture-other-note", Type = PaperTypes.Note };
            controller.State.Papers.Add(otherNote);
            try
            {
                Rejected("asset_not_found",
                    () => Invoke("read_note_image",
                        new { paper_id = otherNote.Id, image_id = imageId }));
            }
            finally { controller.State.Papers.Remove(otherNote); }

            var second = Invoke("insert_note_image",
                new { paper_id = note.Id, image_base64 = base64 });
            Require(second.GetProperty("image_id").GetString() != imageId &&
                    note.Content.EndsWith(
                        second.GetProperty("markdown_reference").GetString()!,
                        StringComparison.Ordinal),
                "Repeated in-memory import did not append a distinct image reference.");
            controller.State.McpAllowBlankWrites = false;
            Rejected("blank_writes_disabled", () => Invoke("insert_note_image",
                new { paper_id = note.Id, image_base64 = base64 }));
            // Image reads are not gated by modification permissions.
            Require(Invoke("read_note_image", new { paper_id = note.Id, image_id = imageId })
                        .GetProperty("mime").GetString() == "image/png",
                "Disabling writes prevented authorized image reads.");

            // A removed reference must immediately stop being readable through MCP,
            // even though the underlying asset is retained for the normal undo path.
            controller.State.McpAllowFullWrites = true;
            var contentWithImage = note.Content;
            var withoutFirstImage = contentWithImage.Replace(reference, "", StringComparison.Ordinal);
            Invoke("write_note", new { paper_id = note.Id, content = withoutFirstImage, mode = "replace" });
            Require(imageAssets.ContainsKey(imageId),
                "Deleting a reference should not force-delete undo/recovery image bytes.");
            Rejected("asset_not_found", () => Invoke("read_note_image",
                new { paper_id = note.Id, image_id = imageId }));
            Invoke("write_note", new { paper_id = note.Id, content = contentWithImage, mode = "replace" });
            Require(Invoke("read_note_image", new { paper_id = note.Id, image_id = imageId })
                        .GetProperty("mime").GetString() == "image/png",
                "Restoring the image reference did not restore MCP visibility.");

            // A trailing unclosed fence would turn the inserted reference into code.
            // Reject before touching LMDB or committing the target note.
            var unclosedFence = contentWithImage + Environment.NewLine + "```text" +
                                Environment.NewLine + "still inside fence";
            Invoke("write_note", new { paper_id = note.Id, content = unclosedFence, mode = "replace" });
            var beforeFenceAssets = imageAssets.Count;
            Rejected("image_reference_not_renderable", () => Invoke("insert_note_image",
                new { paper_id = note.Id, image_base64 = base64 }));
            Require(note.Content == unclosedFence && imageAssets.Count == beforeFenceAssets,
                "An image in an unclosed code block mutated the note or LMDB.");
            Invoke("write_note", new { paper_id = note.Id, content = contentWithImage, mode = "replace" });

            controller.State.McpAllowBlankWrites = true;
            var contentBeforeBad = note.Content;
            var countBeforeBad = imageAssets.Count;
            Rejected("invalid_params", () => Invoke("insert_note_image",
                new { paper_id = note.Id, image_base64 = "not base64!" }));
            Rejected("invalid_params", () => Invoke("insert_note_image",
                new { paper_id = note.Id, image_base64 = "data:image/png;base64," + base64 }));
            Rejected("invalid_params", () => Invoke("insert_note_image",
                new { paper_id = note.Id, image_base64 = base64, image_path = @"C:\fixture\picture.png" }));
            Rejected("invalid_params", () => Invoke("insert_note_image",
                new { paper_id = note.Id, image_path = @"C:\fixture\picture.png" }));
            Rejected("invalid_params", () => Invoke("insert_note_image",
                new { paper_id = note.Id }));
            Rejected("image_import_failed", () => Invoke("insert_note_image",
                new { paper_id = note.Id, image_base64 = Convert.ToBase64String(new byte[] { 1, 2, 3 }) }));
            Require(note.Content == contentBeforeBad && imageAssets.Count == countBeforeBad,
                "Invalid images or conflicting inputs modified the note/image store.");

            // An exception after the atomic replace is not proof that the new JSON
            // was never committed. Both early and late save failures must retain the
            // imported LMDB blob, or a durable reference can become a broken image.
            var store = Field<StateStore>(controller, "_store");
            var writer = typeof(StateStore).GetField("_atomicWriter", Private)!;
            var priorWriter = writer.GetValue(store);
            var ignore = typeof(AppController).GetField("_ignoreSaveFailures", Private)!;
            var priorIgnore = ignore.GetValue(controller);
            foreach (var failureStage in new[]
                     { DurableAtomicWriteStage.BeforeTempOpen, DurableAtomicWriteStage.AfterReplace })
            {
                var originalContent = note.Content;
                var diskBefore = File.ReadAllText(store.FilePath);
                var idsBefore = imageAssets.Keys.ToHashSet(StringComparer.Ordinal);
                try
                {
                    ignore.SetValue(controller, true);
                    writer.SetValue(store, new DurableAtomicFileWriter((stage, target) =>
                    {
                        if (stage == failureStage &&
                            string.Equals(target, store.FilePath, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Injected MCP image save failure at " + stage);
                    }));
                    Rejected("save_failed", () => Invoke("insert_note_image",
                        new { paper_id = note.Id, image_base64 = base64 }));
                }
                finally
                {
                    writer.SetValue(store, priorWriter);
                    ignore.SetValue(controller, priorIgnore);
                }

                var retainedIds = imageAssets.Keys.Where(id => !idsBefore.Contains(id)).ToArray();
                Require(note.Content == originalContent && retainedIds.Length == 1,
                    "Failed note save must restore its in-memory content and retain one new image.");
                var retainedId = retainedIds[0];
                Require(controller.ImageStore.TryReadOwnedImage(
                            note.Id, retainedId, 16 * 1024 * 1024,
                            out var retainedMime, out var retainedBytes, out var tooLarge) &&
                        !tooLarge && retainedMime == "image/png" &&
                        retainedBytes.SequenceEqual(png),
                    "The image backing a potentially committed reference was deleted or corrupted.");

                var diskAfter = File.ReadAllText(store.FilePath);
                if (failureStage == DurableAtomicWriteStage.BeforeTempOpen)
                    Require(diskAfter == diskBefore,
                        "The early failed save unexpectedly replaced the persisted note.");
                else
                    Require(diskAfter.Contains(
                            MarkdownImageReferences.CreateReference(retainedId), StringComparison.Ordinal),
                        "The late failed save did not exercise a committed image reference.");

                // Normal persistence recovers without requiring the MCP caller to retry.
                controller.SaveNow(sync: true);
                Require(Str(LivePaper(ReadLive(), note.Id)["content"]) == originalContent,
                    "The next normal save did not reconcile the restored in-memory note.");
                Require(controller.ImageStore.TryReadOwnedImage(
                            note.Id, retainedId, 16 * 1024 * 1024,
                            out _, out _, out _),
                    "A later normal save unexpectedly purged an image kept for recovery.");
            }
            Console.WriteLine("PASS MCP image in-memory import, image-content read, permissions, ownership and early/late save failure safety");
        }
        finally
        {
            controller.State.McpEnabled = originalEnabled;
            controller.State.McpAllowBlankWrites = originalAdditive;
            controller.State.McpAllowFullWrites = originalFull;
        }
    }
}
