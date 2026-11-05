using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static async Task ImageImportCommandBehavior()
    {
        var pipeName = "PaperTodo.ImageImportChecks." + Guid.NewGuid().ToString("N");
        var workspace = "original";
        var writes = 0;
        var calls = 0;
        var allowWrite = true;
        byte[]? received = null;
        string? receivedName = null;
        using var host = new McpApiHost(Dispatcher.CurrentDispatcher, request =>
        {
            calls++;
            var method = request.GetProperty("method").GetString();
            if (method == "get_workspace_binding") return new { token = workspace };
            if (request.GetProperty("workspace_token").GetString() != workspace)
                throw new McpApiException("workspace_changed", "The workspace changed.");
            if (method == "get_paper") return new { paper_id = "note" };
            if (method != "insert_note_image") throw new InvalidOperationException("Unexpected command.");
            if (!allowWrite) throw new McpApiException("additive_writes_disabled", "Writes are disabled.");
            var parameters = request.GetProperty("params");
            Check(!parameters.TryGetProperty("image_path", out _), "The import transport never sends a source path.");
            received = Convert.FromBase64String(parameters.GetProperty("image_base64").GetString()!);
            receivedName = parameters.GetProperty("image_name").GetString();
            writes++;
            return new { paper_id = "note", image_id = "001", markdown_reference = "![](i:001)" };
        }, pipeName);
        host.Start();

        async Task<(int Code, string Output, string Error)> Run(Stream input,
            string[]? args = null, bool redirected = true)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = await McpImageImportCommand.RunAsync(
                args ?? ["--import-note-image", "--paper-id", "note", "--image-name", "截图.png"],
                new McpPipeClient(pipeName), input, output, error, redirected);
            return (code, output.ToString(), error.ToString());
        }

        var bytes = new byte[McpCommandService.MaxBase64ImageBytes];
        new Random(31).NextBytes(bytes);
        using (var input = new MemoryStream(bytes))
        {
            var result = await Run(input);
            Check(result.Code == 0 && result.Error.Length == 0 && result.Output.Length < 512,
                "A 4 MiB binary input returns only a short successful response.");
            Check(received != null && received.SequenceEqual(bytes) && receivedName == "截图.png" && writes == 1,
                "Binary stdin reaches the existing pipe unchanged, including optional Unicode filename metadata.");
            using var response = JsonDocument.Parse(result.Output);
            Check(response.RootElement.GetProperty("image_id").GetString() == "001",
                "Import stdout is a single machine-readable result.");
        }

        foreach (var size in new[] { 0, McpCommandService.MaxBase64ImageBytes + 1 })
        {
            using var input = new MemoryStream(new byte[size]);
            var result = await Run(input);
            Check(result.Code == 1 && result.Output.Length == 0 && result.Error.Length > 0 && writes == 1,
                "Empty or oversized input fails without an image mutation or payload output.");
        }

        var beforeInvalid = calls;
        foreach (var args in new[]
        {
            new[] { "--import-note-image", "--image-path", "C:\\private.png" },
            new[] { "--import-note-image", "--paper-id", "note", "--paper-id", "other" },
            new[] { "--import-note-image", "--paper-id", "" },
            new[] { "--mcp", "--import-note-image", "--paper-id", "note" }
        })
        {
            using var input = new MemoryStream([1, 2, 3]);
            Check((await Run(input, args)).Code == 2 && input.Position == 0,
                "Invalid arguments are rejected before consuming input.");
        }
        using (var input = new MemoryStream([1, 2, 3]))
            Check((await Run(input, redirected: false)).Code == 2 && input.Position == 0,
                "An interactive stdin cannot leave the importer waiting for binary keyboard input.");
        Check(calls == beforeInvalid, "Invalid usage never connects to the GUI.");

        allowWrite = false;
        using (var input = new MemoryStream([1, 2, 3]))
        {
            var result = await Run(input);
            Check(result.Code == 1 && result.Output.Length == 0 &&
                  result.Error.Contains("additive_writes_disabled") && writes == 1,
                "The command preserves the GUI's permission error and does not retry a write.");
        }
        allowWrite = true;
        using (var input = new WorkspaceSwitchInput(() => workspace = "changed"))
        {
            var result = await Run(input);
            Check(result.Code == 1 && result.Error.Contains("workspace_changed") && writes == 1,
                "A workspace switch after binding but before EOF cannot retarget the import.");
        }
    }

    private sealed class WorkspaceSwitchInput(Action switchWorkspace) : MemoryStream(new byte[] { 1, 2, 3 })
    {
        private bool _switched;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_switched) { _switched = true; switchWorkspace(); }
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
