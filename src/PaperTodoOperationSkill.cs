namespace PaperTodo;

// Shared by the MCP settings copy action and the Codex plugin's optional prompt injection.
internal static class PaperTodoOperationSkill
{
    internal static string For(bool chinese, string serverName = "papertodo") => chinese
        ? $"""
          仅在任务需要操作 PaperTodo 时，使用 `{serverName}` MCP 管理纸片、待办和笔记。
          - 优先使用本次上下文中的纸片/待办 ID；不确定目标时先 `list_papers`，修改已有内容前先 `get_paper`。
          - 新建用 `create_todo_paper` / `create_note`；追加用 `add_todos` / `write_note(mode="append")`；修改用 `update_todo` / `write_note(mode="replace")`。只改用户要求的内容，保留其余内容和结构。
          - 提醒用 `set_todo_reminder`，时间包含日期和时区；删除用 `delete_paper` / `delete_todo`，仅在用户要求删除时使用。
          - 遵循 MCP 权限；权限不足或连接失败时说明原因，不直接修改 data.json 或插件数据文件。工具确认成功后再报告完成；写入结果不明时先读取核对，避免重复追加。
          """
        : $"""
          Use the `{serverName}` MCP server for papers, todos, and notes only when the task needs PaperTodo operations.
          - Prefer paper/todo IDs in the current context; use `list_papers` if the target is unclear and `get_paper` before changing existing content.
          - Create with `create_todo_paper` / `create_note`; append with `add_todos` / `write_note(mode="append")`; edit with `update_todo` / `write_note(mode="replace")`. Change only what the user requests and preserve the rest of the content and structure.
          - Use `set_todo_reminder` with a future date, time, and UTC offset; use `delete_paper` / `delete_todo` only when the user requests deletion.
          - Respect MCP permissions. Explain permission or connection failures; do not edit data.json or plugin data files directly. Report completion only after tool success; read back uncertain writes before retrying to avoid duplicate appends.
          """;
}
