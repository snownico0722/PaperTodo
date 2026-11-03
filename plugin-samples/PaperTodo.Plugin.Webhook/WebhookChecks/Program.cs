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
if (WebhookRoutes.TryRead("{\"routes\":\"{\"}", out _, out _))
    throw new Exception("Invalid JSON was accepted");
if (WebhookRoutes.EventName(PaperTodoEventKind.TodoReminderTriggered) != "todoReminderTriggered")
    throw new Exception("Event naming changed");

var activeSubscriptions = 0;
var filters = new List<PaperTodoEventFilter>();
using (var subscriptions = new WebhookSubscriptions((filter, handler) =>
{
    filters.Add(filter);
    activeSubscriptions++;
    return new CallbackDisposable(() => activeSubscriptions--);
}, new WebhookDispatcher()))
{
    subscriptions.Update("{}");
    if (activeSubscriptions != 0) throw new Exception("Empty routes subscribed to events");
    subscriptions.Update("{\"routes\":\"{\\\"todoReminderTriggered\\\":\\\"http://localhost:1234\\\"}\"}");
    if (activeSubscriptions != 1 || !filters[^1].Kinds!.SetEquals(new[] { PaperTodoEventKind.TodoReminderTriggered }))
        throw new Exception("Reminder route did not subscribe to the reminder event");
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
var todo = new TodoSnapshot("paper", "Title", "todo", "Test", false, 0, null, null, DateTimeOffset.Now) { ReminderTriggered = true };
webhook.Enqueue(new Uri($"http://127.0.0.1:{port}/reminder"), new TodoReminderTriggeredEvent(todo, metadata));
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
using var client = await listener.AcceptTcpClientAsync(timeout.Token);
await using var stream = client.GetStream();
using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
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
using var payload = JsonDocument.Parse(new string(body));
if (payload.RootElement.GetProperty("kind").GetString() != "todoReminderTriggered" ||
    payload.RootElement.GetProperty("todo").GetProperty("text").GetString() != "Test" ||
    !payload.RootElement.GetProperty("todo").GetProperty("reminderTriggered").GetBoolean() ||
    payload.RootElement.GetProperty("metadata").GetProperty("eventId").GetGuid() != metadata.EventId)
    throw new Exception("Unexpected webhook payload");
for (var i = 0; i < 256; i++)
    webhook.Enqueue(new Uri($"http://127.0.0.1:{port}/old"), new TodoReminderTriggeredEvent(todo, metadata));
webhook.InvalidatePending();
webhook.Enqueue(new Uri($"http://127.0.0.1:{port}/new"), new TodoReminderTriggeredEvent(todo, metadata));
await stream.WriteAsync("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nSet-Cookie: webhook_test=source; Path=/\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
client.Close();
using var nextClient = await listener.AcceptTcpClientAsync(timeout.Token);
using var nextReader = new StreamReader(nextClient.GetStream(), Encoding.UTF8);
var nextRequest = await nextReader.ReadLineAsync(timeout.Token);
if (nextRequest != "POST /new HTTP/1.1") throw new Exception($"Stale queued request was sent: {nextRequest}");
while (!string.IsNullOrEmpty(line = await nextReader.ReadLineAsync(timeout.Token)))
{
    if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
        throw new Exception("Webhook route received a Cookie from an earlier endpoint.");
}
await nextClient.GetStream().WriteAsync(
    "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
// Hold the first request open while changing only credentials on the configured route.
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
    routes = JsonSerializer.Serialize(new { todoReminderTriggered = $"http://user:{password}@127.0.0.1:{credentialPort}/hook" })
});
credentialSubscriptions.Update(Settings("old"));
deliver!(new TodoReminderTriggeredEvent(todo, metadata));
using var firstCredentialClient = await credentialListener.AcceptTcpClientAsync(timeout.Token);
await using var firstCredentialStream = firstCredentialClient.GetStream();
using var firstCredentialReader = new StreamReader(firstCredentialStream, Encoding.UTF8, leaveOpen: true);
if (await firstCredentialReader.ReadLineAsync(timeout.Token) != "POST /hook HTTP/1.1")
    throw new Exception("First credential request was not received");
while (!string.IsNullOrEmpty(await firstCredentialReader.ReadLineAsync(timeout.Token))) { }
deliver(new TodoReminderTriggeredEvent(todo, metadata));
credentialSubscriptions.Update(Settings("new"));
deliver(new TodoReminderTriggeredEvent(todo with { Text = "New route event" }, metadata));
await firstCredentialStream.WriteAsync("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
firstCredentialClient.Close();
using var nextCredentialClient = await credentialListener.AcceptTcpClientAsync(timeout.Token);
using var nextCredentialReader = new StreamReader(nextCredentialClient.GetStream(), Encoding.UTF8);
if (await nextCredentialReader.ReadLineAsync(timeout.Token) != "POST /hook HTTP/1.1")
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
using var nextPayload = JsonDocument.Parse(new string(nextBody));
if (nextPayload.RootElement.GetProperty("todo").GetProperty("text").GetString() != "New route event")
    throw new Exception("Queued event used old credentials after route change");
Console.WriteLine("Webhook POST, reminder payload and subscriptions verified.");

sealed class CallbackDisposable(Action callback) : IDisposable
{
    public void Dispose() => callback();
}
