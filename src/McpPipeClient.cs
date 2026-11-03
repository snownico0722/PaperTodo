using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;

namespace PaperTodo;

internal sealed class McpPipeClient
{
    private readonly string _pipeName;
    private readonly SemaphoreSlim _bindingGate = new(1, 1);
    private string? _workspaceToken;
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
        if (_workspaceToken == null)
        {
            await _bindingGate.WaitAsync(cancellationToken);
            try
            {
                if (_workspaceToken == null)
                {
                    var binding = await InvokeCoreAsync("get_workspace_binding", new { }, null,
                        cancellationToken);
                    if (!binding.TryGetProperty("token", out var token) ||
                        token.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(token.GetString()))
                        throw new McpException("PaperTodo did not supply a workspace binding.");
                    _workspaceToken = token.GetString();
                }
            }
            finally { _bindingGate.Release(); }
        }
        return await InvokeCoreAsync(method, parameters, _workspaceToken, cancellationToken);
    }

    private async Task<JsonElement> InvokeCoreAsync(
        string method,
        object parameters,
        string? workspaceToken,
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
            @params = parameters,
            workspace_token = workspaceToken
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

        // An explicit workspace-status query is the caller's acknowledgement of the
        // current slot. Refresh the bridge lease with that SAME response, never retry
        // a stale mutation with a newly acquired token.
        if (method == "get_data_reload_status" &&
            root.TryGetProperty("workspace_token", out var binding) &&
            binding.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(binding.GetString()))
            _workspaceToken = binding.GetString();

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
