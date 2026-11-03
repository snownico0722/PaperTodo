using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PaperTodo.Plugin;
using PaperTodo.Plugin.Webhook;

if (!WebhookRoutes.TryRead("{\"routes\":\"{\\\"futureEvent\\\":\\\"https://example.com/hook\\\"}\"}", out var routes, out _) ||
    !routes.ContainsKey("futureEvent"))
    throw new Exception("Unknown future event route was not retained");
if (WebhookRoutes.TryRead("{\"routes\":\"{\\\"todoCreated\\\":\\\"file:///tmp\\\"}\"}", out _, out _))
    throw new Exception("Invalid URL was accepted");
var basicAuthRoute = JsonSerializer.Serialize(new {
    routes = JsonSerializer.Serialize(new { todoChanged = "https://user:password@example.com/hook" }) });
if (WebhookRoutes.TryRead(basicAuthRoute, out _, out var basicAuthError) ||
    basicAuthError == null || !basicAuthError.Contains("username or password", StringComparison.OrdinalIgnoreCase))
    throw new Exception("URL credentials were not rejected with a useful error.");
if (WebhookRoutes.TryRead("{\"routes\":\"{\"}", out _, out _))
    throw new Exception("Invalid JSON was accepted");
if (WebhookRoutes.EventName(PaperTodoEventKind.TodoChanged) != "todoChanged" ||
    WebhookRoutes.EventName(PaperTodoEventKind.TodoReminderUpcoming) != "todoReminderUpcoming")
    throw new Exception("Event naming changed");

var activeSubscriptions = 0;
var rejectNextSubscription = false;
var filters = new List<PaperTodoEventFilter>();
using (var subscriptions = new WebhookSubscriptions((filter, handler) =>
{
    if (rejectNextSubscription)
    {
        rejectNextSubscription = false;
        throw new InvalidOperationException("Injected subscription failure");
    }
    filters.Add(filter);
    activeSubscriptions++;
    return new CallbackDisposable(() => activeSubscriptions--);
}, new WebhookDispatcher()))
{
    subscriptions.Update("{}");
    if (activeSubscriptions != 0) throw new Exception("Empty routes subscribed to events");
    subscriptions.Update("{\"routes\":\"{\\\"todoChanged\\\":\\\"http://localhost:1234\\\"}\"}");
    if (activeSubscriptions != 1 || !filters[^1].Kinds!.SetEquals(new[] { PaperTodoEventKind.TodoChanged }))
        throw new Exception("Todo change route did not subscribe to TodoChanged");
    rejectNextSubscription = true;
    try
    {
        subscriptions.Update("{\"routes\":\"{\\\"noteChanged\\\":\\\"http://localhost:1234\\\"}\"}");
        throw new Exception("Failed Webhook subscription replacement was accepted");
    }
    catch (InvalidOperationException ex) when (ex.Message == "Injected subscription failure") { }
    if (activeSubscriptions != 1 ||
        !filters[^1].Kinds!.SetEquals(new[] { PaperTodoEventKind.TodoChanged }))
        throw new Exception("Failed Webhook replacement disposed its active subscription");
    subscriptions.Update("{\"routes\":\"{\\\"todoCreated\\\":\\\"http://localhost:1234\\\"}\"}");
    if (activeSubscriptions != 1 || !filters[^1].Kinds!.SetEquals(new[] { PaperTodoEventKind.TodoCreated }))
        throw new Exception("Subscription was not replaced");
    subscriptions.Update("{\"routes\":\"{}\"}");
    if (activeSubscriptions != 0) throw new Exception("Clearing routes left a subscription active");
}

using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
using var webhook = new WebhookDispatcher();
var metadata = new PaperTodoEventMetadata(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now, PaperTodoEventOrigin.System, null);
var before = new TodoSnapshot("paper", "Title", "todo", "喝水🙂", false, 0, null, null, DateTimeOffset.Now);
var todo = before with { ReminderTriggered = true };
var reminderChange = new TodoChangedEvent(before, todo, TodoChangedFields.Reminder, metadata);
webhook.Enqueue(new Uri($"http://127.0.0.1:{port}/reminder"), reminderChange);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
using var client = await listener.AcceptTcpClientAsync(timeout.Token);
await using var stream = client.GetStream();
// Latin1 maps one incoming byte to one char, so HTTP Content-Length stays a byte count.
using var reader = new StreamReader(stream, Encoding.Latin1, leaveOpen: true);
var request = await reader.ReadLineAsync(timeout.Token);
if (request != "POST /reminder HTTP/1.1") throw new Exception($"Unexpected request: {request}");
var length = 0;
string? line;
while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(timeout.Token)))
{
    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
        length = int.Parse(line[15..].Trim());
}
if (length <= 0) throw new Exception("Missing Content-Length");
var body = new char[length];
var read = 0;
while (read < body.Length)
{
    var count = await reader.ReadAsync(body.AsMemory(read), timeout.Token);
    if (count == 0) throw new Exception("Incomplete request body");
    read += count;
}
using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(body)));
if (payload.RootElement.GetProperty("kind").GetString() != "todoChanged" ||
    payload.RootElement.GetProperty("before").GetProperty("reminderTriggered").GetBoolean() ||
    payload.RootElement.GetProperty("after").GetProperty("text").GetString() != "喝水🙂" ||
    !payload.RootElement.GetProperty("after").GetProperty("reminderTriggered").GetBoolean() ||
    payload.RootElement.GetProperty("metadata").GetProperty("eventId").GetGuid() != metadata.EventId)
    throw new Exception("Unexpected webhook payload");
for (var i = 0; i < 256; i++)
    webhook.Enqueue(new Uri($"http://127.0.0.1:{port}/old"), reminderChange);
webhook.InvalidatePending();
webhook.Enqueue(new Uri($"http://127.0.0.1:{port}/new"), reminderChange);
await stream.WriteAsync("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nSet-Cookie: webhook_test=source; Path=/\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
client.Close();
using var nextClient = await listener.AcceptTcpClientAsync(timeout.Token);
using var nextReader = new StreamReader(nextClient.GetStream(), Encoding.Latin1);
var nextRequest = await nextReader.ReadLineAsync(timeout.Token);
if (nextRequest != "POST /new HTTP/1.1") throw new Exception($"Stale queued request was sent: {nextRequest}");
while (!string.IsNullOrEmpty(line = await nextReader.ReadLineAsync(timeout.Token)))
{
    if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
        throw new Exception("Webhook route received a Cookie from an earlier endpoint.");
}
await nextClient.GetStream().WriteAsync(
    "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
// Hold the first request open while rotating only a URL query token.
using var credentialListener = new TcpListener(IPAddress.Loopback, 0);
credentialListener.Start();
var credentialPort = ((IPEndPoint)credentialListener.LocalEndpoint).Port;
Action<PaperTodoEvent>? deliver = null;
using var credentialSubscriptions = new WebhookSubscriptions((filter, handler) =>
{
    deliver = handler;
    return new CallbackDisposable(() => { });
}, new WebhookDispatcher());
string Settings(string password) => JsonSerializer.Serialize(new
{
    routes = JsonSerializer.Serialize(new { todoChanged = $"http://127.0.0.1:{credentialPort}/hook?token={password}" })
});
credentialSubscriptions.Update(Settings("old"));
deliver!(reminderChange);
using var firstCredentialClient = await credentialListener.AcceptTcpClientAsync(timeout.Token);
await using var firstCredentialStream = firstCredentialClient.GetStream();
using var firstCredentialReader = new StreamReader(firstCredentialStream, Encoding.Latin1, leaveOpen: true);
if (await firstCredentialReader.ReadLineAsync(timeout.Token) != "POST /hook?token=old HTTP/1.1")
    throw new Exception("First credential request was not received");
while (!string.IsNullOrEmpty(await firstCredentialReader.ReadLineAsync(timeout.Token))) { }
deliver(reminderChange);
credentialSubscriptions.Update(Settings("new"));
deliver(new TodoChangedEvent(before, todo with { Text = "New route event" },
    TodoChangedFields.Reminder | TodoChangedFields.Text, metadata));
await firstCredentialStream.WriteAsync("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
firstCredentialClient.Close();
using var nextCredentialClient = await credentialListener.AcceptTcpClientAsync(timeout.Token);
using var nextCredentialReader = new StreamReader(nextCredentialClient.GetStream(), Encoding.Latin1);
if (await nextCredentialReader.ReadLineAsync(timeout.Token) != "POST /hook?token=new HTTP/1.1")
    throw new Exception("New credential request was not received");
var nextLength = 0;
while (!string.IsNullOrEmpty(line = await nextCredentialReader.ReadLineAsync(timeout.Token)))
    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
        nextLength = int.Parse(line[15..].Trim());
var nextBody = new char[nextLength];
var nextRead = 0;
while (nextRead < nextBody.Length)
{
    var count = await nextCredentialReader.ReadAsync(nextBody.AsMemory(nextRead), timeout.Token);
    if (count == 0) throw new Exception("Incomplete credential request body");
    nextRead += count;
}
using var nextPayload = JsonDocument.Parse(Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(nextBody)));
if (nextPayload.RootElement.GetProperty("after").GetProperty("text").GetString() != "New route event")
    throw new Exception("Queued event used old URL token after route change");
Console.WriteLine("Webhook POST, TodoChanged reminder transition and subscriptions verified.");

sealed class CallbackDisposable(Action callback) : IDisposable
{
    public void Dispose() => callback();
}
