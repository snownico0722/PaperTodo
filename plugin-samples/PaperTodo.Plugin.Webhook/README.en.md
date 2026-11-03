# Webhook Event Forwarder

[简体中文](README.md) | English

A standalone Native plugin that sends PaperTodo paper, todo, and note events to a configured Webhook URL for each event using HTTP POST. It requires a PaperTodo version that supports plugin API 2.2.

## Installation

Run the following command from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File plugin-samples/Build-And-Install-NativePlugin.ps1 -ProjectPath plugin-samples/PaperTodo.Plugin.Webhook/PaperTodo.Plugin.Webhook.csproj
```

The script builds the plugin and installs it to `plugins/sample.webhook.native`. Exit and restart PaperTodo, then enter a JSON object in the plugin's **事件路由** (Event Routes) setting. Each key is an event name and each value is an HTTP or HTTPS URL. For example:

```json
{"todoCreated":"https://example.com/created","todoChanged":"https://example.com/changes"}
```

The current event names are `paperCreated`, `paperChanged`, `paperDeleted`, `todoCreated`, `todoChanged`, `todoDeleted`, `todoReminderUpcoming`, and `noteChanged`. Names are case-insensitive. Events omitted from the object are not sent; use `{}` to stop all forwarding. Invalid JSON or URLs stop all forwarding until the configuration is corrected, so enter complete JSON when saving.

When the host adds a compatible event type, you can add a route using its enum name with the first letter lowercased without changing the plugin code. Whether the event is delivered still depends on the host's subscription and permission contracts.

Settings take effect immediately: changing routes discards events queued under the previous configuration that have not started sending. An HTTP request that has already started cannot be recalled. The plugin does not subscribe to workspace events when no recognized event routes are configured. By default, the plugin creates or restores a hidden Paper at startup to keep its Runtime active; you can also create a plugin Paper manually. Disabling **启动时自动创建纸片** (Create Paper at startup) does not stop an existing plugin instance. Set Event Routes to `{}` to stop forwarding immediately.

## Configuration and verification

Set **Event Routes** to a JSON mapping of event names to HTTP(S) destinations, for example:

```json
{"todoChanged":"https://example.com/todo"}
```

`todoReminderUpcoming` is an advance notice sent about five seconds before due with a `notificationId`. This sample forwards the notice but cannot suppress the host presentation on behalf of a remote HTTP receiver. A `todoChanged` event may indicate a text edit, a reminder reschedule, or a reminder trigger. To identify **a reminder trigger**, filter for `metadata.origin === "system"`, `before.reminderTriggered === false`, and `after.reminderTriggered === true`. Configure your own trusted HTTP receiver.

## Request format

Each event produces a POST request with `Content-Type: application/json`, for example:

```json
{
  "todo": { "paperId": "paper-id", "paperTitle": "Todos", "id": "todo-id", "text": "Drink water", "done": false, "order": 0, "reminderAt": null, "reminderTriggered": false },
  "kind": "todoCreated",
  "metadata": { "eventId": "00000000-0000-0000-0000-000000000001", "operationId": "00000000-0000-0000-0000-000000000002", "occurredAt": "2026-01-01T12:00:00+08:00", "origin": "user", "sourcePluginId": null }
}
```

The data fields depend on the host event type: change events include `before`, `after`, and `changedFields`; note change events include paper information and content lengths, but not the note text. Reminder triggers are also represented by `todoChanged`: compare `before.reminderTriggered` with `after.reminderTriggered`. Completing a todo also emits `todoChanged` (check `changedFields` and `after.done`). See the [host contracts](../../PaperTodo.Plugin.Abstractions/PaperTodoHostContracts.cs) for event fields.

The plugin sends events in receive order through a single background queue, which holds up to 256 events. Each request times out after 15 seconds. Events are not retried if the queue is full, a request fails, or the server responds with a non-2xx status. When the Runtime stops, pending requests and queued events are discarded. Host events also have no offline replay, so this plugin is not suitable for workflows that require guaranteed delivery. Failures are written to .NET Trace without logging URLs or event bodies. Redirects are not followed automatically.

Webhook URLs containing user-info (`https://user:password@host/path`) are rejected; this sample does not implement Basic Auth. Webhook URLs are stored in the host's plugin settings; anyone who can read the plugin data file can see tokens included in a URL. Events may contain paper titles, todo text, and linked paths, so configure only trusted endpoints. The plugin runs only while PaperTodo is running and at least one real Paper uses this plugin.
