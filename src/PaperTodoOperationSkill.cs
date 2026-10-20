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
          - 用户明确授权外部编辑时，先查询 `get_data_reload_status` 的 `WorkspaceNumber` / `DataFileName`，等待 `IsReloading=false`，只编辑对应工作区文件，不默认写主 data.json；写完调用 `reload_data` 接受文件，再查询状态；这会覆盖尚未保存的内存修改。没有主动重载时，正常自动保存可以覆盖外部文件。普通内容修改仍优先使用领域工具。
          - 需要合并独立修改时，保留开始编辑前实际读取的完整原始 JSON，调用 `merge_data(baseline_json=原始JSON字符串)`；不要用修改后或后来保存的文件替代原始版本。没有基线时只能由用户选择整份当前/外部数据；冲突默认询问，也可按当前设置只对冲突采用外部或内部优先；不要临时改变用户策略来免除询问。再查询 `get_data_reload_status` 确认最终结果。该功能不启用自动文件监听。
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
          - After explicit authorization to edit a data file, query `get_data_reload_status` for WorkspaceNumber/DataFileName and wait for IsReloading=false. Edit that active-workspace file, not always main data.json; then call `reload_data` and check status. Unsaved in-memory edits may be replaced; without an explicit reload, ordinary saves may overwrite disk edits. Prefer domain tools for normal edits.
          - For independent changes, keep the complete original JSON actually read before editing and pass it to `merge_data(baseline_json=originalJson)`; never substitute the edited file or a later save. Without a baseline, the user must choose the entire current or external state. Conflicts follow the current ask/external/internal preference; do not change the user preference just to avoid a prompt. Check `get_data_reload_status` for the final outcome. No automatic file watcher is enabled.
          - Respect MCP permissions. Explain permission or connection failures; do not edit data.json or plugin data files directly. Report completion only after tool success; read back uncertain writes before retrying to avoid duplicate appends.
          """;
}
