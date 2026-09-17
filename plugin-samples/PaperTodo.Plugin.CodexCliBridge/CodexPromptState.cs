using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        当用户提出为 PaperTodo 制作、创建或开发插件（例如“做个番茄钟插件”）时，先调用内置 papertodo-plugin-creator Skill：读取“内置 Skill”提供的 SKILL.md，并按其中的说明完成插件制作、验证和交付。其他任务按用户要求正常处理。
        """;

    internal const string BuiltInDefaultPromptEn = """
        Complete the task under "Current content" below. Prefer English for communication.
        When the user asks to make, create, or develop a plugin for PaperTodo (for example, "make a Pomodoro plugin"), first use the built-in papertodo-plugin-creator Skill: read the SKILL.md referenced under "Built-in Skill" and follow its instructions to implement, verify, and deliver the plugin. Handle other tasks normally according to the user's request.
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

    internal string PrependTo(string content, string pluginDirectory, string? uiLanguage)
    {
        var prompt = EffectivePrompt(uiLanguage);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return content;
        }

        var skillPath = Path.Combine(pluginDirectory, "skills", SkillName, "SKILL.md");
        var builder = new StringBuilder();
        builder.AppendLine(CodexBridgeText.For(
            uiLanguage,
            "[PaperTodo 默认提示词]",
            "[PaperTodo default prompt]"));
        builder.AppendLine(prompt.Trim());
        builder.AppendLine();
        builder.AppendLine(CodexBridgeText.For(
            uiLanguage,
            "[内置 Skill]",
            "[Built-in Skill]"));
        builder.AppendLine(CodexBridgeText.For(
            uiLanguage,
            $"{SkillName}：制作 PaperTodo 插件的说明与开发手册。",
            $"{SkillName}: instructions and development guide for creating PaperTodo plugins."));
        builder.AppendLine(CodexBridgeText.For(
            uiLanguage,
            $"SKILL.md 路径：{JsonSerializer.Serialize(skillPath)}",
            $"SKILL.md path: {JsonSerializer.Serialize(skillPath)}"));
        builder.AppendLine();
        builder.AppendLine(CodexBridgeText.For(
            uiLanguage,
            "[本次内容]",
            "[Current content]"));
        builder.Append(content);
        return builder.ToString();
    }
}
