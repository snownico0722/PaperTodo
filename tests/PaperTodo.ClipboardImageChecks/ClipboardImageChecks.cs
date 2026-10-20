using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

internal static partial class ClipboardImageChecks
{
    // BGRA pixels include transparent, translucent, opaque black and opaque color.
    private static readonly byte[] MixedPixels =
    [
        0, 0, 0, 0,       40, 80, 160, 128,   0, 0, 0, 255,
        255, 255, 255, 255, 20, 80, 220, 255, 12, 34, 56, 200
    ];

    private static readonly byte[] TransparentPixels =
    [
        16, 32, 64, 0, 32, 64, 128, 0, 0, 0, 0, 0,
        255, 128, 64, 0, 10, 20, 30, 0, 100, 200, 250, 0
    ];

    public static void Run(Action<string, Action> check)
    {
        check("PNG paste preserves transparent, translucent and opaque black pixels", () =>
        {
            var data = new DataObject();
            data.SetData("PNG", EncodePng(MixedPixels), autoConvert: false);
            AssertInsertionAndReload(data, MixedPixels);
        });

        check("Fully transparent PNG keeps its alpha even when RGB is nonzero", () =>
        {
            using var stream = new MemoryStream(EncodePng(TransparentPixels));
            var data = new DataObject();
            data.SetData("PNG", stream, autoConvert: false);
            AssertInsertionAndReload(data, TransparentPixels);
        });

        check("PNG takes priority over the degraded Bitmap clipboard representation", () =>
        {
            using var stream = new MemoryStream(EncodePng(MixedPixels));
            var degraded = MakeOpaque(MixedPixels);
            var data = new DataObject();
            data.SetData(DataFormats.Bitmap, CreateBitmap(degraded), autoConvert: true);
            data.SetData("PNG", stream, autoConvert: false);
            AssertInsertionAndReload(data, MixedPixels);
        });

        check("Legacy Bitmap screenshots with an unused alpha plane remain visible", () =>
        {
            var data = new DataObject();
            data.SetData(DataFormats.Bitmap, CreateBitmap(TransparentPixels), autoConvert: true);
            AssertInsertionAndReload(data, MakeOpaque(TransparentPixels));
        });

        check("Adding image formats preserves existing text, HTML, RTF and custom data", () =>
        {
            var data = new DataObject();
            var formats = new Dictionary<string, string>
            {
                [DataFormats.UnicodeText] = "![image|100%](i:1)",
                [DataFormats.Html] = "<div>existing HTML</div>",
                [DataFormats.Rtf] = @"{\rtf1 existing RTF}",
                ["PaperTodo.Checks.Custom"] = "existing custom payload"
            };
            foreach (var pair in formats)
                data.SetData(pair.Key, pair.Value, autoConvert: false);
            ClipboardHelper.AddImageFormats(data, CreateBitmap(MixedPixels));
            foreach (var pair in formats)
                Require(data.GetData(pair.Key, autoConvert: false) as string == pair.Value,
                    $"Adding image formats changed {pair.Key}.");
            Require(data.GetDataPresent(DataFormats.Bitmap, autoConvert: true),
                "Image formats did not include the compatibility Bitmap.");
            AssertInsertionAndReload(data, MixedPixels);
        });
    }

    // Deliberately opt-in: this writes a synthetic image to the shared Windows clipboard.
    // Run sequentially, with no other clipboard checks or user clipboard operations in flight.
    public static bool TryRunClipboardMode(string[] args, out int result)
    {
        result = 0;
        if (args.Length != 1 || args[0] is not ("--clipboard" or "--clipboard-child"))
            return false;

        try
        {
            new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (args[0] == "--clipboard-child")
            {
                ClipboardHelper.SetImage(CreateBitmap(MixedPixels));
                AssertSystemClipboard();
                Console.WriteLine("PASS clipboard PNG and Bitmap are readable after SetImage returns");
                CheckSelectionCopy();
            }
            else
            {
                RunClipboardWriterProcess();
                AssertSystemClipboard();
                Console.WriteLine("PASS clipboard PNG pixels survive the writer process exiting");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL persistent system clipboard image: {ex}");
            result = 1;
        }
        return true;
    }

    static partial void CheckSelectionCopy();

    private static void RunClipboardWriterProcess()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate the check process.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--clipboard-child");
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start clipboard writer.");
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Clipboard writer did not finish within 15 seconds.");
        }
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Console.Write(output);
        Require(process.ExitCode == 0, $"Clipboard writer failed: {error}");
    }

    private static void AssertSystemClipboard()
    {
        var data = Clipboard.GetDataObject()
            ?? throw new InvalidOperationException("System clipboard is empty.");
        Require(data.GetDataPresent("PNG", autoConvert: false), "PNG clipboard format is missing.");
        Require(data.GetDataPresent(DataFormats.Bitmap, autoConvert: true), "Bitmap compatibility format is missing.");
        Require(data.GetData(DataFormats.Bitmap, autoConvert: true) is BitmapSource,
            "Bitmap compatibility format cannot be read.");
        AssertInsertionAndReload(data, MixedPixels);
    }

    private static void AssertInsertionAndReload(IDataObject data, byte[] expected)
    {
        using var directory = new FixtureDirectory();
        var databasePath = Path.Combine(directory.Path, "note-assets.lmdb");
        string imageId;
        using (var store = new NoteImageStore(databasePath))
        {
            store.Load();
            var box = new MarkdownTextBox { Text = "before" };
            box.ConfigureNoteImages("clipboard-test-note", store);
            box.CaretIndex = box.Text.Length;
            box.Document.UndoStack.ClearAll();
            Exception? importFailure = null;
            box.ImageImportFailed += exception => importFailure = exception;

            Require(box.TryInsertImagesFromDataObject(data), "Image data was not handled.");
            Require(importFailure == null, $"Image import failed: {importFailure}");
            Pump();
            var insertedText = box.Text;
            var references = MarkdownImageReferences.CollectImageIds(insertedText);
            Require(references.Count == 1, "One paste must insert exactly one image reference.");
            imageId = references.Single();
            AssertStoredPixels(store, imageId, expected);

            box.Undo();
            Pump();
            Require(box.Text == "before", "One undo must restore the exact text before pasting.");
            Require(!box.CanUndo, "Image paste added extra undo operations.");
            box.Redo();
            Pump();
            Require(box.Text == insertedText, "Redo must restore the same image reference.");
            store.SetViewportProtectedBitmapIds("clipboard-test-note", Array.Empty<string>());
        }

        // A fresh store must decode the persisted bytes; a cached BitmapSource is not evidence.
        using var reopened = new NoteImageStore(databasePath);
        reopened.Load();
        Require(!reopened.IsWriteDisabled, "The image database could not be reopened.");
        AssertStoredPixels(reopened, imageId, expected);
    }

    private static void AssertStoredPixels(NoteImageStore store, string imageId, byte[] expected)
    {
        var bitmap = store.GetBitmapSourceForClipboard(imageId)
            ?? throw new InvalidOperationException("Stored image could not be decoded.");
        Require(bitmap.PixelWidth == 3 && bitmap.PixelHeight == 2, "Stored image dimensions changed.");
        BitmapSource bgra = bitmap.Format == PixelFormats.Bgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var actual = new byte[expected.Length];
        bgra.CopyPixels(actual, 3 * 4, 0);
        Require(actual.SequenceEqual(expected),
            $"Stored BGRA pixels differ. Expected {Convert.ToHexString(expected)}, actual {Convert.ToHexString(actual)}.");
    }

    private static BitmapSource CreateBitmap(byte[] pixels)
    {
        var bitmap = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 3 * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] EncodePng(byte[] pixels)
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(CreateBitmap(pixels)));
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] MakeOpaque(byte[] pixels)
    {
        var result = (byte[])pixels.Clone();
        for (var index = 3; index < result.Length; index += 4)
            result[index] = byte.MaxValue;
        return result;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class FixtureDirectory : IDisposable
    {
        private static readonly string Root = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PaperTodo.ClipboardImageChecks"));

        public string Path { get; } = System.IO.Path.Combine(Root, Guid.NewGuid().ToString("N"));

        public FixtureDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            Require(resolved.StartsWith(Root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Refusing to delete a directory outside the clipboard test fixtures.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
