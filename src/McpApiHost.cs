using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace PaperTodo;

internal sealed class McpApiHost : IDisposable
{
    public const string PipeName = "PaperTodo-Mcp-Api-v1";
    // 200 normal-size todo rows can exceed one million JSON characters after escaping.
    private const int MaxRequestCharacters = 8_000_000;
    private static readonly TimeSpan RequestReadTimeout =
        TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResponseWriteTimeout =
        TimeSpan.FromSeconds(10);

    private readonly Dispatcher _dispatcher;
    private readonly Func<JsonElement, object?> _execute;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _acceptCts = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenerTask;
    private bool _disposed;

    public McpApiHost(
        Dispatcher dispatcher,
        McpCommandService commands)
        : this(dispatcher, commands.Execute, PipeName)
    {
    }

    internal McpApiHost(Dispatcher dispatcher, Func<JsonElement, object?> execute, string pipeName)
    {
        _dispatcher = dispatcher;
        _execute = execute;
        _pipeName = pipeName;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _listenerTask ??= Task.Run(() => ListenAsync(_cts.Token));
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && !_acceptCts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(_acceptCts.Token);
                await ProcessConnectionAsync(server, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || _acceptCts.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                await DelayAfterFailureAsync(token);
            }
            catch (UnauthorizedAccessException)
            {
                await DelayAfterFailureAsync(token);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                // Keep the local interface alive after an isolated connection failure.
                await DelayAfterFailureAsync(token);
            }
        }
    }

    private static async Task DelayAfterFailureAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(200, token);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task ProcessConnectionAsync(
        Stream stream,
        CancellationToken token)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        await using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(false),
            leaveOpen: true)
        {
            AutoFlush = true
        };

        object response;
        try
        {
            var line = await ReadRequestLineAsync(reader, token);
            response = await DispatchAsync(line, token);
        }
        catch (McpApiException ex)
        {
            response = ErrorResponse(null, ex.Code, ex.Message);
        }

        var serializedResponse = JsonSerializer.Serialize(response);
        using var writeTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(token);
        writeTimeout.CancelAfter(ResponseWriteTimeout);
        await writer.WriteLineAsync(
            serializedResponse.AsMemory(),
            writeTimeout.Token);
    }

    private static async Task<string> ReadRequestLineAsync(
        StreamReader reader,
        CancellationToken token)
    {
        using var readTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(token);
        readTimeout.CancelAfter(RequestReadTimeout);

        var buffer = new char[4096];
        var text = new StringBuilder();
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(
                    buffer,
                    readTimeout.Token);
                if (read == 0)
                {
                    return text.Length == 0 ? "" : text.ToString();
                }

                for (var index = 0; index < read; index++)
                {
                    var character = buffer[index];
                    if (character == '\n')
                    {
                        if (text.Length > 0 && text[^1] == '\r')
                        {
                            text.Length--;
                        }
                        return text.ToString();
                    }

                    text.Append(character);
                    if (text.Length > MaxRequestCharacters)
                    {
                        throw new McpApiException(
                            "request_too_large",
                            "The MCP request is too large.");
                    }
                }
            }
        }
        catch (OperationCanceledException)
            when (!token.IsCancellationRequested)
        {
            throw new McpApiException(
                "request_timeout",
                "The MCP client did not finish its request in time.");
        }
    }

    private async Task<object> DispatchAsync(
        string json,
        CancellationToken token)
    {
        JsonElement? requestId = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var request = document.RootElement.Clone();
            if (request.ValueKind != JsonValueKind.Object)
            {
                throw new McpApiException(
                    "invalid_request",
                    "The request must be a JSON object.");
            }

            if (request.TryGetProperty("id", out var idElement))
            {
                requestId = idElement.Clone();
            }

            var operation = _dispatcher.InvokeAsync(
                () => _execute(request),
                DispatcherPriority.Normal,
                token);
            var result = await operation.Task;
            return new { id = requestId, ok = true, result };
        }
        catch (McpApiException ex)
        {
            return ErrorResponse(requestId, ex.Code, ex.Message);
        }
        catch (JsonException)
        {
            return ErrorResponse(
                requestId,
                "invalid_json",
                "The request is not valid JSON.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ErrorResponse(
                requestId,
                "internal_error",
                "PaperTodo could not complete the request.");
        }
    }

    private static object ErrorResponse(
        JsonElement? id,
        string code,
        string message)
        => new { id, ok = false, error = new { code, message } };

    // Stop accepting new requests but let the one already executing report its committed result.
    // The existing read/write deadlines still bound a stalled client; app exit uses forceful Dispose.
    internal bool IsStopping => _disposed;

    internal void StopAfterResponse() => Stop(cancelResponse: false);

    public void Dispose() => Stop(cancelResponse: true);

    private void Stop(bool cancelResponse)
    {
        if (_disposed)
        {
            if (cancelResponse)
            {
                try { _cts.Cancel(); }
                catch (ObjectDisposedException) { } // graceful completion already released it
            }
            return;
        }
        _disposed = true;
        _acceptCts.Cancel();
        if (cancelResponse) _cts.Cancel();
        var listener = _listenerTask;
        if (listener == null)
        {
            _acceptCts.Dispose();
            _cts.Dispose();
            return;
        }
        _ = listener.ContinueWith(completed =>
        {
            _ = completed.Exception;
            _acceptCts.Dispose();
            _cts.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

}

internal sealed class McpApiException : Exception
{
    public McpApiException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
