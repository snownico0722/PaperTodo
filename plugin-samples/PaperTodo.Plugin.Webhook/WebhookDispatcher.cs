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
    private readonly Channel<(Uri Endpoint, PaperTodoEvent Event)> _queue =
        Channel.CreateBounded<(Uri, PaperTodoEvent)>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly CancellationTokenSource _stopping = new();
    private int _disposed;
    private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public WebhookDispatcher() => _ = Task.Run(ProcessAsync);

    public void Enqueue(Uri endpoint, PaperTodoEvent value)
    {
        if (!_queue.Writer.TryWrite((endpoint, value)))
        {
            Trace.TraceWarning("Webhook queue full or stopped; event {0} was dropped.", value.Metadata.EventId);
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var (endpoint, value) in _queue.Reader.ReadAllAsync(_stopping.Token))
            {
                try
                {
                    var json = JsonSerializer.Serialize(value, value.GetType(), JsonOptions);
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
                    using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _stopping.Token).ConfigureAwait(false);
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
