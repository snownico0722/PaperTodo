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
{"todoCreated":"https://example.com/created","todoReminderTriggered":"https://example.com/reminder"}
```

The current event names are `paperCreated`, `paperChanged`, `paperDeleted`, `todoCreated`, `todoChanged`, `todoDeleted`, `todoReminderTriggered`, and `noteChanged`. Names are case-insensitive. Events omitted from the object are not sent; use `{}` to stop all forwarding. Invalid JSON or URLs stop all forwarding until the configuration is corrected, so enter complete JSON when saving.

When the host adds a compatible event type, you can add a route using its enum name with the first letter lowercased without changing the plugin code. Whether the event is delivered still depends on the host's subscription and permission contracts.

Settings take effect immediately: changing routes discards events queued under the previous configuration that have not started sending. An HTTP request that has already started cannot be recalled. The plugin does not subscribe to workspace events when no recognized event routes are configured. By default, the plugin creates or restores a hidden Paper at startup to keep its Runtime active; you can also create a plugin Paper manually. Turning off **启动时运行** (Run at startup) only affects future startups and does not delete existing Papers.

## Try it locally

1. Install the plugin as described above and start PaperTodo.
2. Install Python, open PowerShell at the repository root, and start the local receiver included with the plugin:

   ```powershell
   python .\plugin-samples\PaperTodo.Plugin.Webhook\webhook_receiver.py
   ```

   Keep the terminal open. The receiver listens on `127.0.0.1:8765` by default and prints the method, path, headers, and body of each request. Press `Ctrl+C` to stop it.
3. Open PaperTodo's plugin settings and paste the following single-line JSON into the Webhook plugin's **Event Routes** field to listen for all current events:

   ```json
   {"paperCreated":"http://127.0.0.1:8765/paperCreated","paperChanged":"http://127.0.0.1:8765/paperChanged","paperDeleted":"http://127.0.0.1:8765/paperDeleted","todoCreated":"http://127.0.0.1:8765/todoCreated","todoChanged":"http://127.0.0.1:8765/todoChanged","todoDeleted":"http://127.0.0.1:8765/todoDeleted","todoReminderTriggered":"http://127.0.0.1:8765/todoReminderTriggered","noteChanged":"http://127.0.0.1:8765/noteChanged"}
   ```

4. Create a todo or modify a paper, then check the Python receiver's terminal for the request. Each event uses a different path so you can tell them apart.

## Request format

Each event produces a POST request with `Content-Type: application/json`, for example:

```json
{
  "todo": { "paperId": "paper-id", "paperTitle": "Todos", "id": "todo-id", "text": "Drink water", "done": false, "order": 0, "reminderAt": null, "reminderTriggered": false },
  "kind": "todoCreated",
  "metadata": { "eventId": "00000000-0000-0000-0000-000000000001", "operationId": "00000000-0000-0000-0000-000000000002", "occurredAt": "2026-01-01T12:00:00+08:00", "origin": "user", "sourcePluginId": null }
}
```

The data fields depend on the host event type: change events include `before`, `after`, and `changedFields`; note change events include paper information and content lengths, but not the note text. Reminder triggers use `todoReminderTriggered`. Completing a todo produces a `todoChanged` event; check `changedFields` and `after.done`. See the [host contracts](../../PaperTodo.Plugin.Abstractions/PaperTodoHostContracts.cs) for event fields.

The plugin sends events in receive order through a single background queue, which holds up to 256 events. Each request times out after 15 seconds. Events are not retried if the queue is full, a request fails, or the server responds with a non-2xx status. When the Runtime stops, pending requests and queued events are discarded. Host events also have no offline replay, so this plugin is not suitable for workflows that require guaranteed delivery. Failures are written to .NET Trace without logging URLs or event bodies. Redirects are not followed automatically.

Webhook URLs are stored in the host's plugin settings; anyone who can read the plugin data file can see tokens included in a URL. Events may contain paper titles, todo text, and linked paths, so configure only trusted endpoints. The plugin runs only while PaperTodo is running and at least one real Paper uses this plugin.
