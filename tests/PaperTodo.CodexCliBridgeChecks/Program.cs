using System.Text.Json;
using PaperTodo.Plugin.CodexCliBridge;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

var untouchedLanguages = new Dictionary<string, string>
{
    ["zh-CN"] = CodexPromptState.BuiltInDefaultPromptZh,
    ["zh-Hans"] = CodexPromptState.BuiltInDefaultPromptZh,
    ["en-US"] = CodexPromptState.BuiltInDefaultPromptEn,
    ["ja-JP"] = CodexPromptState.BuiltInDefaultPromptEn
};

foreach (var json in new[] { "", "{}", "{\"DefaultPrompt\":null}" })
{
    var state = CodexPromptState.Read(json);
    Check(state.DefaultPrompt is null, "Untouched state must stay unset.");
    foreach (var (language, expected) in untouchedLanguages)
    {
        Check(state.EffectivePrompt(language) == expected,
            $"Untouched prompt must follow UI language: {language}.");
    }
    Check(CodexPromptState.Read(JsonSerializer.Serialize(state)).DefaultPrompt is null,
        "Reading or restoring a paper must not freeze the built-in default as a user edit.");
}

// The old bridge only saved this property after a user change; both spellings existed.
foreach (var key in new[] { "DefaultPrompt", "defaultPrompt" })
foreach (var prompt in new[] { "", "  ", "只输出代码\n保留我的规则", CodexPromptState.BuiltInDefaultPromptZh })
{
    var state = CodexPromptState.Read(JsonSerializer.Serialize(new Dictionary<string, string> { [key] = prompt }));
    foreach (var language in untouchedLanguages.Keys)
    {
        Check(state.EffectivePrompt(language) == prompt,
            "An existing edit, even empty or equal to a built-in default, must take precedence over UI language.");
    }
    Check(CodexPromptState.Read(JsonSerializer.Serialize(state)).EffectivePrompt("en-US") == prompt,
        "A user edit must survive serialization and restart.");
}

var pluginDirectory = Path.Combine(Path.GetTempPath(), "PaperTodo 中文 'quoted' path", "plugins", "tools.codex-cli-bridge.native");
const string task = "做个番茄钟插件\n保留本次内容";
var initial = CodexPromptState.Read("{}");
var expectedPath = Path.Combine(pluginDirectory, "skills", CodexPromptState.SkillName, "SKILL.md");

var composedZh = initial.PrependTo(task, pluginDirectory, "zh-CN");
Check(composedZh.Contains(CodexPromptState.BuiltInDefaultPromptZh),
    "Chinese UI must send the Chinese built-in prompt.");
Check(composedZh.Contains("[PaperTodo 默认提示词]"),
    "Chinese UI must localize prompt section labels.");
Check(composedZh.Contains("[本次内容]"),
    "Chinese UI must localize current-content label.");
Check(composedZh.Contains(JsonSerializer.Serialize(expectedPath)),
    "The bundled Skill must resolve against the actual installation path.");
Check(composedZh.EndsWith(task, StringComparison.Ordinal),
    "Task content must be preserved after Chinese prompt injection.");

var composedEn = initial.PrependTo(task, pluginDirectory, "en-US");
Check(composedEn.Contains(CodexPromptState.BuiltInDefaultPromptEn),
    "English UI must send the English built-in prompt.");
Check(composedEn.Contains("[PaperTodo default prompt]"),
    "English UI must localize prompt section labels.");
Check(composedEn.Contains("[Current content]"),
    "English UI must localize current-content label.");
Check(composedEn.Contains(JsonSerializer.Serialize(expectedPath)),
    "English prompt must reference the same bundled Skill path.");
Check(composedEn.EndsWith(task, StringComparison.Ordinal),
    "Task content must be preserved after English prompt injection.");

var edited = initial with { DefaultPrompt = "自定义提示词" };
var restored = CodexPromptState.Read(JsonSerializer.Serialize(edited));
foreach (var language in new[] { "zh-CN", "en-US" })
{
    var composed = restored.PrependTo(task, pluginDirectory, language);
    Check(composed.Contains("自定义提示词"), "Saved custom prompt must be sent.");
    Check(!composed.Contains(CodexPromptState.BuiltInDefaultPromptZh),
        "Custom prompts must not have the Chinese built-in instructions appended back.");
    Check(!composed.Contains(CodexPromptState.BuiltInDefaultPromptEn),
        "Custom prompts must not have the English built-in instructions appended back.");
}

foreach (var prompt in new[] { "", "  " })
{
    var cleared = CodexPromptState.Read(JsonSerializer.Serialize(edited with { DefaultPrompt = prompt }));
    foreach (var language in new[] { "zh-CN", "en-US" })
    {
        Check(cleared.PrependTo(task, pluginDirectory, language) == task,
            "Clearing the prompt must send only the task, including after restart and regardless of UI language.");
    }
}

try
{
    CodexPromptState.Read("{broken");
    throw new InvalidOperationException("Corrupt state must not become a writable default.");
}
catch (JsonException)
{
    checks++;
}

Console.WriteLine($"Codex CLI Bridge: {checks} checks passed.");
