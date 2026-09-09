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
    // Outlast the primary's two-second stalled-read deadline, including retry delays:
    // 12 * 180 + 11 * 70 = 2930 ms. Successful connections still return immediately.
    private const int SignalRetryCount = 12;
    private const int SignalConnectTimeoutMs = 180;
    private const int SignalRetryDelayMs = 70;

    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly TimeSpan _commandReadTimeout;
    private Mutex? _mutex;
    private bool _ownsMutex;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private bool _disposed;

    public SingleInstanceHelper(string mutexName, string pipeName)
        : this(mutexName, pipeName, TimeSpan.FromSeconds(2))
    {
    }

    internal SingleInstanceHelper(string mutexName, string pipeName, TimeSpan commandReadTimeout)
    {
        if (commandReadTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(commandReadTimeout));
        }
        _mutexName = mutexName;
        _pipeName = pipeName;
        _commandReadTimeout = commandReadTimeout;
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

        _listenerTask = Task.Run(async () =>
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

                    // One tiny local command per connection. A stalled peer must not monopolize
                    // the listener, and shutdown must cancel a peer already connected to the pipe.
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readTimeout.CancelAfter(_commandReadTimeout);
                    using var reader = new StreamReader(server);
                    string? message;
                    try
                    {
                        message = await reader.ReadLineAsync(readTimeout.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        // Only this peer timed out. Dispose its pipe and accept the next client.
                        continue;
                    }
                    token.ThrowIfCancellationRequested();
                    onCommandSignal?.Invoke(DecodeArgs(message));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
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
            var listenerCts = _listenerCts;
            _listenerCts = null;
            listenerCts?.Cancel();
            if (listenerCts != null)
            {
                if (_listenerTask == null)
                {
                    listenerCts.Dispose();
                }
                else
                {
                    // Do not join here: a completed command can itself be waiting on the UI.
                    _ = _listenerTask.ContinueWith(completed =>
                    {
                        _ = completed.Exception;
                        listenerCts.Dispose();
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
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
