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
        写入待办项时控制篇幅；如果内容过长，优先把详细内容写入一张 Note 并绑定到该待办。若绑定所需的 MCP“完整写入”权限未开启，提示用户在 PaperTodo 设置中手动开启后再继续。
        写入笔记时也控制篇幅，避免过度冗长，但不需要为了简短而丢失必要信息或过度压缩。
        """;

    internal const string BuiltInDefaultPromptEn = """
        Complete the task under "Current content" below. Prefer English for communication.
        Keep todo items concise; if the content is too long, put the details in a Note and link that Note to the todo. If the MCP "full writes" permission required for linking is disabled, ask the user to enable it manually in PaperTodo Settings before continuing.
        Keep notes reasonably concise as well: avoid unnecessary verbosity, but do not over-compress or omit useful information just to make them short.
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
                $"仅当本次任务需要写入 PaperTodo 且 MCP 未开启时，可在 PowerShell 运行：{command}。随后通过 papertodo_bridge MCP 执行；宿主会重新检查插件开关，原有写入/删除权限仍生效。无需写入时不要开启。",
                $"Only when this task requires writing to PaperTodo and MCP is disabled, you may run in PowerShell: {command}. Then use the papertodo_bridge MCP server. The host rechecks the plugin setting; existing write/delete permissions still apply. Do not enable it for tasks that need no writes."));
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
