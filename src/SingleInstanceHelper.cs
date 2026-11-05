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

    // 0: handled successfully; 1: host rejected/failed; 2: no confirmed response/delivery.
    // Only acknowledged commands wait for execution. Ordinary activation remains fire-and-forget.
    public int SignalPrimaryInstance(IReadOnlyList<string> args, bool waitForResult = false)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SingleInstanceHelper));
        var message = (waitForResult ? "RESULT " : "") + EncodeArgs(args);
        for (var attempt = 0; attempt < SignalRetryCount; attempt++)
        {
            using var client = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { client.Connect(SignalConnectTimeoutMs); }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                if (attempt == SignalRetryCount - 1) return 2;
                Thread.Sleep(SignalRetryDelayMs);
                continue;
            }

            // Once connected, never replay a command whose result might simply have been lost.
            try
            {
                using (var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true))
                {
                    writer.WriteLine(message);
                    writer.Flush();
                }
                if (!waitForResult) return 0;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var reader = new StreamReader(client);
                var reply = reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
                return bool.TryParse(reply, out var succeeded) ? (succeeded ? 0 : 1) : 2;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                return 2;
            }
        }
        return 2;
    }

    public void StartListener(Func<IReadOnlyList<string>, bool> onCommandSignal)
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
                        PipeDirection.InOut,
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
                    var wantsResult = message?.StartsWith("RESULT ", StringComparison.Ordinal) == true;
                    var args = DecodeArgs(wantsResult ? message![7..] : message);
                    bool succeeded;
                    try { succeeded = onCommandSignal(args); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"PaperTodo command failed: {ex}");
                        succeeded = false;
                    }
                    if (wantsResult)
                    {
                        using var writer = new StreamWriter(server, Encoding.UTF8, leaveOpen: true);
                        await writer.WriteLineAsync(succeeded.ToString());
                        await writer.FlushAsync(token);
                    }
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
