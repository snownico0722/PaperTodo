using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static partial class Program
{
    private static async Task McpImageImportProcessChecks(AppController controller)
    {
        var note = controller.State.Papers.Single(p => p.Id == "note-b");
        var previousAdditive = controller.State.McpAllowBlankWrites;
        var previousFull = controller.State.McpAllowFullWrites;
        var pixels = new byte[800 * 600 * 4];
        new Random(77).NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var bitmap = BitmapSource.Create(800, 600, 96, 96, PixelFormats.Bgra32, null, pixels, 800 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var encoded = new MemoryStream();
        encoder.Save(encoded);
        var png = encoded.ToArray();
        Require(png.Length > 1024 * 1024 && png.Length < McpCommandService.MaxBase64ImageBytes,
            "The process fixture must transfer an ordinary-size PNG larger than 1 MiB.");

        async Task<(int Code, string Output, string Error)> Import(byte[] bytes)
        {
            // Launch the real WPF entry point, not a direct helper call. dotnet also works
            // when ProjectReference copies the assembly without its Windows apphost.
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            start.ArgumentList.Add(typeof(App).Assembly.Location);
            start.ArgumentList.Add("--import-note-image");
            start.ArgumentList.Add("--paper-id");
            start.ArgumentList.Add(note.Id);
            start.ArgumentList.Add("--image-name");
            start.ArgumentList.Add("截图.png");
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(bytes, timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                return (process.ExitCode, await output, await error);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
        }

        try
        {
            controller.State.McpAllowBlankWrites = true;
            var result = await Import(png);
            Require(result.Code == 0 && result.Error.Length == 0 && result.Output.Length < 512,
                "Real importer failed or echoed payload: " + result.Error);
            using var response = JsonDocument.Parse(result.Output);
            var imageId = response.RootElement.GetProperty("image_id").GetString()!;
            var reference = response.RootElement.GetProperty("markdown_reference").GetString()!;
            Require(note.Content.EndsWith(reference, StringComparison.Ordinal) &&
                    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data.json")).Contains(reference, StringComparison.Ordinal),
                "The real importer did not persist the note reference.");
            Require(controller.ImageStore.TryReadOwnedImage(note.Id, imageId, 4 * 1024 * 1024,
                        out _, out var stored, out _) && stored.SequenceEqual(png),
                "The real importer changed or lost image bytes before LMDB persistence.");

            var originalText = note.Content;
            controller.State.McpAllowBlankWrites = false;
            controller.State.McpAllowFullWrites = false;
            var denied = await Import(png);
            Require(denied.Code == 1 && denied.Output.Length == 0 &&
                    denied.Error.Contains("blank_writes_disabled") && note.Content == originalText,
                "The real importer bypassed additive-write permission or reported false success.");

            controller.State.McpAllowBlankWrites = true;
            var invalid = await Import([1, 2, 3, 4]);
            Require(invalid.Code == 1 && invalid.Output.Length == 0 && invalid.Error.Length > 0 &&
                    note.Content == originalText,
                "The real importer accepted invalid image bytes or altered note content.");
            Console.WriteLine($"PASS real image-import process: {png.Length} PNG bytes over binary stdin, LMDB/reference persistence, permission and invalid-image rejection");
        }
        finally
        {
            controller.State.McpAllowBlankWrites = previousAdditive;
            controller.State.McpAllowFullWrites = previousFull;
        }
    }
}
