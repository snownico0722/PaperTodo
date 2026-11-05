using System.IO;
using System.Text.Json;
using ModelContextProtocol;

namespace PaperTodo;

// A one-shot transport for local agents: raw stdin -> existing MCP pipe command.
// Image bytes never become model-generated arguments or console output.
internal static class McpImageImportCommand
{
    internal const string CommandLineSwitch = "--import-note-image";
    private const string Usage =
        "Usage: PaperTodo.exe --import-note-image --paper-id <id> [--image-name <name>]\n" +
        "Send raw image bytes (up to 4 MiB) through redirected stdin, then close stdin. File paths are not accepted.";

    internal static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(value => string.Equals(value, CommandLineSwitch, StringComparison.OrdinalIgnoreCase));

    internal static Task<int> RunAsync(string[] args) => RunAsync(
        args, new McpPipeClient(), Console.OpenStandardInput(), Console.Out, Console.Error,
        Console.IsInputRedirected);

    internal static async Task<int> RunAsync(
        IReadOnlyList<string> args, McpPipeClient client, Stream input,
        TextWriter output, TextWriter error, bool inputRedirected,
        CancellationToken cancellationToken = default)
    {
        if (!TryParse(args, out var paperId, out var imageName) || !inputRedirected)
        {
            await error.WriteLineAsync(Usage);
            return 2;
        }

        try
        {
            // Bind and validate the target BEFORE waiting for the producer. Use this same
            // client for the write so a workspace switch during input cannot retarget it.
            _ = await client.InvokeAsync("get_paper", new { paper_id = paperId }, cancellationToken)
                .ConfigureAwait(false);

            using var image = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var remaining = McpCommandService.MaxBase64ImageBytes + 1 - (int)image.Length;
                var count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                    cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                image.Write(buffer, 0, count);
                if (image.Length > McpCommandService.MaxBase64ImageBytes)
                    throw new ArgumentException("Image input cannot exceed 4 MiB.");
            }
            if (image.Length == 0)
                throw new ArgumentException("Image input is empty; send raw image bytes through stdin.");

            var result = await client.InvokeAsync("insert_note_image", new
            {
                paper_id = paperId,
                image_base64 = Convert.ToBase64String(image.GetBuffer(), 0, (int)image.Length),
                image_name = imageName
            }, cancellationToken).ConfigureAwait(false);

            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                paper_id = result.GetProperty("paper_id").GetString(),
                image_id = result.GetProperty("image_id").GetString(),
                markdown_reference = result.GetProperty("markdown_reference").GetString()
            }));
            return 0;
        }
        catch (Exception ex) when (ex is McpException or IOException or ArgumentException or OperationCanceledException)
        {
            // Never retry a write: the GUI may have committed before a response was lost.
            await error.WriteLineAsync($"PaperTodo image import failed: {ex.Message}");
            return 1;
        }
    }

    private static bool TryParse(IReadOnlyList<string> args, out string paperId, out string? imageName)
    {
        paperId = "";
        imageName = null;
        if (args.Count is not (3 or 5) ||
            !string.Equals(args[0], CommandLineSwitch, StringComparison.OrdinalIgnoreCase)) return false;
        for (var i = 1; i < args.Count; i += 2)
        {
            var value = args[i + 1].Trim();
            if (string.Equals(args[i], "--paper-id", StringComparison.OrdinalIgnoreCase) &&
                paperId.Length == 0 && value.Length is > 0 and <= 64)
                paperId = value;
            else if (string.Equals(args[i], "--image-name", StringComparison.OrdinalIgnoreCase) &&
                imageName == null && value.Length is > 0 and <= 200)
                imageName = value;
            else return false;
        }
        return paperId.Length > 0;
    }
}
