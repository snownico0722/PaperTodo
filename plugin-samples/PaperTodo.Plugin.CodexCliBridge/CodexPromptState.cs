using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaperTodo.Plugin.CodexCliBridge;

// Null means never edited; an empty string is an intentional user override.
internal sealed record CodexPromptState(string? DefaultPrompt)
{
    internal const string SkillName = "papertodo-plugin-creator";
    internal const string BuiltInDefaultPrompt = """
        请完成下方“本次内容”中的任务，优先使用中文交流。
        当用户提出为 PaperTodo 制作、创建或开发插件（例如“做个番茄钟插件”）时，先调用内置 papertodo-plugin-creator Skill：读取“内置 Skill”提供的 SKILL.md，并按其中的说明完成插件制作、验证和交付。其他任务按用户要求正常处理。
        """;

    [JsonIgnore]
    internal string EffectivePrompt => DefaultPrompt ?? BuiltInDefaultPrompt;

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

    internal string PrependTo(string content, string pluginDirectory)
    {
        var prompt = EffectivePrompt;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return content;
        }

        var skillPath = Path.Combine(pluginDirectory, "skills", SkillName, "SKILL.md");
        var builder = new StringBuilder();
        builder.AppendLine("[PaperTodo 默认提示词]");
        builder.AppendLine(prompt.Trim());
        builder.AppendLine();
        builder.AppendLine("[内置 Skill]");
        builder.AppendLine($"{SkillName}：制作 PaperTodo 插件的说明与开发手册。");
        builder.AppendLine($"SKILL.md 路径：{JsonSerializer.Serialize(skillPath)}");
        builder.AppendLine();
        builder.AppendLine("[本次内容]");
        builder.Append(content);
        return builder.ToString();
    }
}
