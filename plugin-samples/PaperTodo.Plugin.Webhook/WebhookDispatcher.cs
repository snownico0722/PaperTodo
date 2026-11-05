using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.Webhook;

internal sealed class WebhookDispatcher : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly Channel<(Uri Endpoint, PaperTodoEvent Event, long Generation)> _queue =
        Channel.CreateBounded<(Uri, PaperTodoEvent, long)>(new BoundedChannelOptions(256)
        {
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly CancellationTokenSource _stopping = new();
    private int _disposed;
    private readonly object _gate = new();
    private long _generation;
    private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public WebhookDispatcher() => _ = Task.Run(ProcessAsync);

    public void InvalidatePending()
    {
        lock (_gate)
        {
            _generation++;
            while (_queue.Reader.TryRead(out _)) { }
        }
    }

    public void Enqueue(Uri endpoint, PaperTodoEvent value)
    {
        lock (_gate)
        {
            if (!_queue.Writer.TryWrite((endpoint, value, _generation)))
                Trace.TraceWarning("Webhook queue full or stopped; event {0} was dropped.", value.Metadata.EventId);
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var (endpoint, value, generation) in _queue.Reader.ReadAllAsync(_stopping.Token))
            {
                try
                {
                    var json = JsonSerializer.Serialize(value, value.GetType(), JsonOptions);
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
                    Task<HttpResponseMessage> send;
                    lock (_gate)
                    {
                        if (generation != _generation) continue;
                        send = _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _stopping.Token);
                    }
                    using var response = await send.ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        Trace.TraceWarning("Webhook event {0} returned HTTP {1}.", value.Metadata.EventId, (int)response.StatusCode);
                    }
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("Webhook event {0} failed: {1}.", value.Metadata.EventId, ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        finally
        {
            _client.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        _stopping.Cancel();
    }
}
