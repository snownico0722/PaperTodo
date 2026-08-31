using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PaperTodo;

public sealed class SingleInstanceHelper : IDisposable
{
    private const int SignalRetryCount = 6;
    private const int SignalConnectTimeoutMs = 180;
    private const int SignalRetryDelayMs = 70;

    private readonly string _mutexName;
    private readonly string _pipeName;
    private Mutex? _mutex;
    private bool _ownsMutex;
    private CancellationTokenSource? _listenerCts;
    private bool _disposed;

    public SingleInstanceHelper(string mutexName, string pipeName)
    {
        _mutexName = mutexName;
        _pipeName = pipeName;
    }

    public bool TryAcquire()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SingleInstanceHelper));

        try
        {
            _mutex = new Mutex(true, _mutexName, out var createdNew);
            _ownsMutex = createdNew;
            return createdNew;
        }
        catch
        {
            _mutex?.Dispose();
            _mutex = null;
            _ownsMutex = false;
            return false;
        }
    }

    public void SignalPrimaryInstance(IReadOnlyList<string> args)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SingleInstanceHelper));

        var encodedArgs = EncodeArgs(args);
        for (var attempt = 0; attempt < SignalRetryCount; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);

                client.Connect(SignalConnectTimeoutMs);

                using var writer = new StreamWriter(client);
                writer.WriteLine(encodedArgs);
                writer.Flush();
                return;
            }
            catch
            {
                if (attempt == SignalRetryCount - 1)
                {
                    return;
                }

                try
                {
                    Thread.Sleep(SignalRetryDelayMs);
                }
                catch
                {
                    return;
                }
            }
        }
    }

    public void StartListener(Action<IReadOnlyList<string>> onCommandSignal)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SingleInstanceHelper));

        _listenerCts = new CancellationTokenSource();
        var token = _listenerCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token);

                    using var reader = new StreamReader(server);
                    var message = await reader.ReadLineAsync();
                    onCommandSignal?.Invoke(DecodeArgs(message));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    try
                    {
                        await Task.Delay(200, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }, token);
    }

    private static string EncodeArgs(IReadOnlyList<string> args)
    {
        var json = JsonSerializer.Serialize(args);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static IReadOnlyList<string> DecodeArgs(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) || string.Equals(message, "SHOW", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(message));
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _listenerCts?.Cancel();
            _listenerCts?.Dispose();
        }
        catch
        {
            // ignored
        }

        try
        {
            if (_ownsMutex)
            {
                _mutex?.ReleaseMutex();
            }
        }
        catch
        {
            // ignored
        }

        try
        {
            _mutex?.Dispose();
        }
        catch
        {
            // ignored
        }
    }
}
