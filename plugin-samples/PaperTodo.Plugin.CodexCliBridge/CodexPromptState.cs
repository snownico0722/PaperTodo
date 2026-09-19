using System.IO;
using System.Text;
using System.Text.Json;

namespace PaperTodo.Plugin.CodexCliBridge;

internal static class CodexBridgeText
{
    internal static bool IsChinese(string? uiLanguage) =>
        !string.IsNullOrWhiteSpace(uiLanguage) &&
        uiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    internal static string For(string? uiLanguage, string zh, string en) =>
        IsChinese(uiLanguage) ? zh : en;
}

// Null means never edited; an empty string is an intentional user override.
internal sealed record CodexPromptState(string? DefaultPrompt)
{
    internal const string SkillName = "papertodo-plugin-creator";

    internal const string BuiltInDefaultPromptZh = """
        请完成下方“本次内容”中的任务，优先使用中文交流。
        写入待办项时不要篇幅过长；如果内容过长，可以把详细内容写入一张 Note 并绑定到该待办；先用 get_setting(id="todo.paper_links") 查询，如果该功能未开启，则不要使用绑定 Note 的方案，状态未知时也跳过。
        写入笔记时也要控制篇幅，不要过度冗长，但也不需要过度精简，保留完成任务所需的信息。
        """;

    internal const string BuiltInDefaultPromptEn = """
        Complete the task under "Current content" below. Prefer English for communication.
        Keep todo items concise; if the content is too long, put the details in a Note and link that Note to the todo. Check get_setting(id="todo.paper_links") first; if linking is disabled, do not use the linked-Note approach, and also skip it when its state is unknown.
        Keep notes reasonably concise as well: avoid unnecessary verbosity, but do not over-compress them; preserve the information needed to complete the task.
        """;

    internal string EffectivePrompt(string? uiLanguage) =>
        DefaultPrompt ?? BuiltInDefaultPrompt(uiLanguage);

    internal static string BuiltInDefaultPrompt(string? uiLanguage) =>
        CodexBridgeText.IsChinese(uiLanguage)
            ? BuiltInDefaultPromptZh
            : BuiltInDefaultPromptEn;

    internal static CodexPromptState Read(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = document.RootElement;
        var prompt = root.TryGetProperty("DefaultPrompt", out var pascal) &&
                     pascal.ValueKind == JsonValueKind.String
            ? pascal.GetString()
            : root.TryGetProperty("defaultPrompt", out var camel) &&
              camel.ValueKind == JsonValueKind.String
                ? camel.GetString()
                : null;
        return new CodexPromptState(prompt);
    }

    internal string PrependTo(
        string content, string pluginDirectory, string? uiLanguage,
        CodexBridgeSettings settings, string executable)
    {
        var prompt = EffectivePrompt(uiLanguage);
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            builder.AppendLine(CodexBridgeText.For(uiLanguage,
                "[PaperTodo 默认提示词]", "[PaperTodo default prompt]"));
            builder.AppendLine(prompt.Trim());
            builder.AppendLine();
        }
        if (settings.EnablePluginSkill)
        {
            var skillPath = Path.Combine(pluginDirectory, "skills", SkillName, "SKILL.md");
            builder.AppendLine(CodexBridgeText.For(uiLanguage,
                "[插件技能]", "[Plugin skill]"));
            builder.AppendLine(CodexBridgeText.For(uiLanguage,
                $"仅在用户要求制作、创建或开发 PaperTodo 插件时，先读取并遵循 {SkillName}：{JsonSerializer.Serialize(skillPath)}。",
                $"Only when the user asks to create or develop a PaperTodo plugin, first read and follow {SkillName}: {JsonSerializer.Serialize(skillPath)}."));
            builder.AppendLine();
        }
        if (settings.EnableOperationSkill)
        {
            builder.AppendLine(CodexBridgeText.For(uiLanguage,
                "[PaperTodo 操作技能]", "[PaperTodo operation skill]"));
            builder.AppendLine(PaperTodoOperationSkill.For(
                CodexBridgeText.IsChinese(uiLanguage), "papertodo_bridge"));
            builder.AppendLine();
        }
        if (settings.AllowMcp)
        {
            // This is a capability hint, not an instruction to enable MCP on every task.
            var command = "& '" + executable.Replace("'", "''") + "' --enable-mcp-for-codex";
            builder.AppendLine(CodexBridgeText.For(uiLanguage,
                "[MCP 按需开启]", "[On-demand MCP activation]"));
            builder.AppendLine(CodexBridgeText.For(uiLanguage,
                $"当本次任务需要通过 PaperTodo MCP 操作，而 MCP 或所需 MCP 权限尚未开启时，直接在 PowerShell 运行：{command}。随后通过 papertodo_bridge MCP 执行；宿主会重新检查插件开关，并自动开启 MCP 总开关、新增/空白写入、完整写入、直接删除和敏感设置控制权限。无需 MCP 时不要开启。",
                $"When this task needs PaperTodo MCP and MCP or a required MCP permission is disabled, run this in PowerShell: {command}. Then use the papertodo_bridge MCP server. The host rechecks the plugin setting and automatically enables the MCP master switch plus additive/blank writes, full writes, direct-delete permission, and sensitive-settings control. Do not enable MCP when the task does not need it."));
            builder.AppendLine();
        }
        else if (settings.EnableOperationSkill)
        {
            builder.AppendLine(CodexBridgeText.For(uiLanguage,
                "本插件未获准自动开启 MCP；仅使用已开启的 MCP，连接不可用时说明原因。",
                "This plugin may not automatically enable MCP. Use it only if already enabled; explain unavailable connections."));
            builder.AppendLine();
        }
        if (builder.Length == 0) return content;
        builder.AppendLine(CodexBridgeText.For(
            uiLanguage,
            "[本次内容]",
            "[Current content]"));
        builder.Append(content);
        return builder.ToString();
    }
}
