using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;

namespace PaperTodo;

internal sealed class McpPipeClient
{
    private readonly string _pipeName;
    public McpPipeClient() : this(McpApiHost.PipeName) { }
    internal McpPipeClient(string pipeName) => _pipeName = pipeName;

    private const int ConnectTimeoutMilliseconds = 2500;
    private const int ResponseTimeoutMilliseconds = 10_000;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<JsonElement> InvokeAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            await pipe.ConnectAsync(
                ConnectTimeoutMilliseconds,
                cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new McpException(
                "PaperTodo is not running, or its MCP interface is disabled.");
        }

        using var reader = new StreamReader(
            pipe,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        await using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(false),
            leaveOpen: true)
        {
            AutoFlush = true
        };

        var request = new
        {
            id = Guid.NewGuid().ToString("N"),
            method,
            @params = parameters
        };
        await writer.WriteLineAsync(
            JsonSerializer.Serialize(request, JsonOptions));

        string? responseLine;
        using (var responseTimeout =
               CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken))
        {
            responseTimeout.CancelAfter(ResponseTimeoutMilliseconds);
            try
            {
                responseLine = await reader.ReadLineAsync(
                    responseTimeout.Token);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new McpException(
                    "PaperTodo did not return an MCP response in time.");
            }
        }

        if (string.IsNullOrWhiteSpace(responseLine))
        {
            throw new McpException(
                "PaperTodo closed the MCP connection without returning a response.");
        }

        using var response = JsonDocument.Parse(responseLine);
        var root = response.RootElement;
        if (!root.TryGetProperty("ok", out var ok) ||
            ok.ValueKind != JsonValueKind.True)
        {
            var error = root.TryGetProperty("error", out var errorElement)
                ? errorElement
                : default;
            var code = TryReadString(error, "code") ?? "request_failed";
            var message = TryReadString(error, "message") ??
                "PaperTodo rejected the MCP request.";
            throw new McpException($"PaperTodo error ({code}): {message}");
        }

        return root.TryGetProperty("result", out var result)
            ? result.Clone()
            : JsonSerializer.SerializeToElement<object?>(null);
    }

    private static string? TryReadString(JsonElement parent, string name)
    {
        return parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
