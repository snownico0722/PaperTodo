namespace PaperTodo;

// Shared by the MCP settings copy action and the Codex plugin's optional prompt injection.
internal static class PaperTodoOperationSkill
{
    internal static string For(bool chinese, string serverName = "papertodo") => chinese
        ? $"""
          仅在任务需要操作 PaperTodo 时，使用 `{serverName}` MCP 管理纸片、待办和笔记。
          - 优先使用本次上下文中的纸片/待办 ID；不确定目标时先 `list_papers`，修改已有内容前先 `get_paper`。
          - 新建用 `create_todo_paper` / `create_note`；追加用 `add_todos` / `write_note(mode="append")`；修改用 `update_todo` / `write_note(mode="replace")`。只改用户要求的内容，保留其余内容和结构。
          - 显示/隐藏已有纸片用 `show_paper` / `hide_paper`，展开/折叠用 `expand_paper` / `collapse_paper`，激活用 `activate_paper`；可用 `toggle_paper_visibility` / `toggle_paper_collapsed` 切换，但结果不明时先查询状态，不能盲目重试 toggle。显示保留折叠状态，隐藏不删除内容；不抢焦点时给显示/展开传 `activate=false`。仅按用户要求操作窗口。
          - 软件设置先 `list_settings` / `get_setting` 查询公开 ID、类型、范围和 writable；只有任务明确要求更改时才 `set_setting`。这不同于插件自己的设置，禁止修改内部字段或自行提升未获准权限。
          - 长待办需要关联笔记时，先 `get_setting(id="todo.paper_links")`；关闭或未知则跳过关联方案，不先创建额外 Note。解绑用 `update_todo(clear_linked_paper=true)`，不可同时传绑定 ID。
          - 提醒用 `set_todo_reminder`，时间包含日期和时区；删除用 `delete_paper` / `delete_todo`，仅在用户要求删除时使用。
          - 遵循 MCP 权限；权限不足或连接失败时说明原因，不直接修改 data.json 或插件数据文件。工具确认成功后再报告完成；写入结果不明时先读取核对，避免重复追加。
          """
        : $"""
          Use the `{serverName}` MCP server for papers, todos, and notes only when the task needs PaperTodo operations.
          - Prefer paper/todo IDs in the current context; use `list_papers` if the target is unclear and `get_paper` before changing existing content.
          - Create with `create_todo_paper` / `create_note`; append with `add_todos` / `write_note(mode="append")`; edit with `update_todo` / `write_note(mode="replace")`. Change only what the user requests and preserve the rest of the content and structure.
          - Show/hide existing papers with `show_paper` / `hide_paper`, expand/collapse with `expand_paper` / `collapse_paper`, and activate with `activate_paper`. `toggle_paper_visibility` / `toggle_paper_collapsed` invert state: read uncertain results before retrying, never blindly replay a toggle. Show preserves folding; hide never deletes content; pass `activate=false` to show/expand without requesting focus. Change presentation only when requested.
          - Discover public application setting IDs, types, limits, and writable with `list_settings` / `get_setting`; use `set_setting` only for a requested settings change. These are not plugin-private settings. Never edit internal fields or grant yourself an unauthorized permission.
          - Before creating a linked detail Note, call `get_setting(id="todo.paper_links")`; if disabled or unknown, skip that approach rather than creating an extra Note first. Unlink with `update_todo(clear_linked_paper=true)`, without a linked paper ID.
          - Use `set_todo_reminder` with a future date, time, and UTC offset; use `delete_paper` / `delete_todo` only when the user requests deletion.
          - Respect MCP permissions. Explain permission or connection failures; do not edit data.json or plugin data files directly. Report completion only after tool success; read back uncertain writes before retrying to avoid duplicate appends.
          """;
}
