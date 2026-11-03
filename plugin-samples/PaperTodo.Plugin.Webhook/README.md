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
{"todoCreated":"https://example.com/created","todoChanged":"https://example.com/changes"}
```

当前事件名称为 `paperCreated`、`paperChanged`、`paperDeleted`、`todoCreated`、`todoChanged`、`todoDeleted`、`todoReminderUpcoming` 和 `noteChanged`。名称不区分大小写；未列出的事件不发送，填 `{}` 可停止全部转发。无效 JSON 或地址会停止全部转发，修正设置后恢复；保存时必须填写完整 JSON。未来宿主添加兼容事件类型时，可按其枚举名称首字母小写添加路由，不需要改插件代码；能否收到仍取决于宿主事件订阅与权限契约。

设置即时生效：路由变化时，旧配置下尚未开始发送的事件会丢弃；已经开始的 HTTP 请求无法撤回。没有可识别的事件路由时，插件不订阅工作区事件。插件默认在启动时创建或恢复一张隐藏纸片来运行；也可以手动创建该插件纸片。关闭「启动时自动创建纸片」不会停止已经存在的插件实例；要立即停止转发，请把「事件路由」设为 `{}`。

## 配置与验证

将「事件路由」设置为事件名称到 HTTP(S) 接收地址的 JSON 映射，例如：

```json
{"todoChanged":"https://example.com/todo"}
```

`todoReminderUpcoming` 是提醒到期前约五秒的前置通知，附带 `notificationId`；Webhook 只转发该事件，无法替接收端调用宿主取消接口。收到的 `todoChanged` 可以表示文本修改、提醒改期或提醒触发。接收端若要识别**提醒已触发**，应同时判断 `metadata.origin == "system"`、`before.reminderTriggered == false` 和 `after.reminderTriggered == true`。请配置自己信任的 HTTP 接收服务。

## 请求格式

每次事件发送一次 `Content-Type: application/json` 的 POST，例如：

```json
{
  "todo": { "paperId": "paper-id", "paperTitle": "待办", "id": "todo-id", "text": "喝水", "done": false, "order": 0, "reminderAt": null, "reminderTriggered": false },
  "kind": "todoCreated",
  "metadata": { "eventId": "00000000-0000-0000-0000-000000000001", "operationId": "00000000-0000-0000-0000-000000000002", "occurredAt": "2026-01-01T12:00:00+08:00", "origin": "user", "sourcePluginId": null }
}
```

各事件的数据字段依宿主事件类型而异：变更事件包含 `before`、`after` 和 `changedFields`；笔记变更事件包含纸片信息和内容长度，不包含正文。提醒触发也是 `todoChanged`，需要比较 `before.reminderTriggered` 和 `after.reminderTriggered`；完成待办同样属于 `todoChanged`，可检查 `changedFields` 与 `after.done`。事件字段见 [宿主接口](../../PaperTodo.Plugin.Abstractions/PaperTodoHostContracts.cs)。

插件使用单个后台队列按接收顺序发送，最多缓存 256 条，每次请求超时 15 秒；队列满、请求失败或非 2xx 响应不会重试，关闭 Runtime 时未完成的请求和队列会丢弃。事件本身也不提供离线重放；因此不适合要求必达的业务。失败信息写入 .NET Trace，不记录 URL 或事件正文。重定向不会自动跟随。

Webhook URL 不支持 `https://user:password@host/path` 形式的用户名/密码，配置中包含此类信息会被拒绝（不会自动生成 Basic Auth 请求头）。URL 存在宿主插件设置中，URL 若包含令牌，能读取插件数据文件的人也能看到。事件可能包含纸片标题、待办文本和关联路径，请只配置可信的接收端。插件仅在 PaperTodo 运行且至少有一张该插件的实体纸片时运行。
