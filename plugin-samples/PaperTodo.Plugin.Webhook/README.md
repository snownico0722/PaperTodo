# Webhook 事件转发

简体中文 | [English](README.en.md)

一个独立的 Native 插件，将 PaperTodo 的纸片、待办和笔记事件通过 HTTP POST 发送到各事件配置的 Webhook URL。需要支持插件 API 2.2 的 PaperTodo。

## 安装

在仓库根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File plugin-samples/Build-And-Install-NativePlugin.ps1 -ProjectPath plugin-samples/PaperTodo.Plugin.Webhook/PaperTodo.Plugin.Webhook.csproj
```

安装脚本会构建插件并写入 `plugins/sample.webhook.native`。退出并重新打开 PaperTodo 后，在插件设置的「事件路由」中填写 JSON 对象，键为事件名称，值为 HTTP 或 HTTPS 地址，例如：

```json
{"todoCreated":"https://example.com/created","todoReminderTriggered":"https://example.com/reminder"}
```

当前事件名称为 `paperCreated`、`paperChanged`、`paperDeleted`、`todoCreated`、`todoChanged`、`todoDeleted`、`todoReminderTriggered` 和 `noteChanged`。名称不区分大小写；未列出的事件不发送，填 `{}` 可停止全部转发。无效 JSON 或地址会停止全部转发，修正设置后恢复；保存时必须填写完整 JSON。未来宿主添加兼容事件类型时，可按其枚举名称首字母小写添加路由，不需要改插件代码；能否收到仍取决于宿主事件订阅与权限契约。

设置即时生效。插件默认在启动时创建或恢复一张隐藏纸片来运行；也可以手动创建该插件纸片。关闭「启动时运行」只影响后续启动，不会删除现有纸片。

## 本地体验

1. 按上面的步骤安装插件并启动 PaperTodo。
2. 安装 Python 后，在仓库根目录打开 PowerShell，启动随插件提供的本地接收服务：

   ```powershell
   python .\plugin-samples\PaperTodo.Plugin.Webhook\webhook_receiver.py
   ```

   保持终端打开。服务默认监听 `127.0.0.1:8765`，收到请求后会打印方法、路径、请求头和正文；按 `Ctrl+C` 停止。
3. 打开 PaperTodo 的插件设置，在「Webhook 事件转发」的「事件路由」输入框中粘贴以下单行 JSON，监听当前所有事件：

   ```json
   {"paperCreated":"http://127.0.0.1:8765/paperCreated","paperChanged":"http://127.0.0.1:8765/paperChanged","paperDeleted":"http://127.0.0.1:8765/paperDeleted","todoCreated":"http://127.0.0.1:8765/todoCreated","todoChanged":"http://127.0.0.1:8765/todoChanged","todoDeleted":"http://127.0.0.1:8765/todoDeleted","todoReminderTriggered":"http://127.0.0.1:8765/todoReminderTriggered","noteChanged":"http://127.0.0.1:8765/noteChanged"}
   ```

4. 新建一条待办或修改一张纸片，回到 Python 服务的终端查看收到的请求。不同事件使用不同路径，方便区分。

## 请求格式

每次事件发送一次 `Content-Type: application/json` 的 POST，例如：

```json
{
  "todo": { "paperId": "paper-id", "paperTitle": "待办", "id": "todo-id", "text": "喝水", "done": false, "order": 0, "reminderAt": null, "reminderTriggered": false },
  "kind": "todoCreated",
  "metadata": { "eventId": "00000000-0000-0000-0000-000000000001", "operationId": "00000000-0000-0000-0000-000000000002", "occurredAt": "2026-01-01T12:00:00+08:00", "origin": "user", "sourcePluginId": null }
}
```

各事件的数据字段依宿主事件类型而异：变更事件包含 `before`、`after` 和 `changedFields`；笔记变更事件包含纸片信息和内容长度，不包含正文。提醒触发使用 `todoReminderTriggered`。完成待办属于 `todoChanged`，可检查 `changedFields` 与 `after.done`。事件字段见 [宿主接口](../../PaperTodo.Plugin.Abstractions/PaperTodoHostContracts.cs)。

插件使用单个后台队列按接收顺序发送，最多缓存 256 条，每次请求超时 15 秒；队列满、请求失败或非 2xx 响应不会重试，关闭 Runtime 时未完成的请求和队列会丢弃。事件本身也不提供离线重放；因此不适合要求必达的业务。失败信息写入 .NET Trace，不记录 URL 或事件正文。重定向不会自动跟随。

Webhook URL 存在宿主插件设置中，URL 若包含令牌，能读取插件数据文件的人也能看到。事件可能包含纸片标题、待办文本和关联路径，请只配置可信的接收端。插件仅在 PaperTodo 运行且至少有一张该插件的实体纸片时运行。
