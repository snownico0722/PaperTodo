using System.Text.Json;
using PaperTodo.Plugin.CodexCliBridge;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

foreach (var json in new[] { "", "{}", "{\"DefaultPrompt\":null}" })
{
    var state = CodexPromptState.Read(json);
    Check(state.DefaultPrompt is null, "Untouched state must stay unset.");
    Check(state.EffectivePrompt == CodexPromptState.BuiltInDefaultPrompt, "Untouched paper must display the built-in prompt.");
    Check(CodexPromptState.Read(JsonSerializer.Serialize(state)).DefaultPrompt is null,
        "Reading or restoring a paper must not freeze the built-in default as a user edit.");
}

// The old bridge only saved this property after a user change; both spellings existed.
foreach (var key in new[] { "DefaultPrompt", "defaultPrompt" })
foreach (var prompt in new[] { "", "  ", "只输出代码\n保留我的规则", CodexPromptState.BuiltInDefaultPrompt })
{
    var state = CodexPromptState.Read(JsonSerializer.Serialize(new Dictionary<string, string> { [key] = prompt }));
    Check(state.EffectivePrompt == prompt, "An existing edit, even empty or equal to the default, must take precedence.");
    Check(CodexPromptState.Read(JsonSerializer.Serialize(state)).EffectivePrompt == prompt,
        "A user edit must survive serialization and restart.");
}

var pluginDirectory = Path.Combine(Path.GetTempPath(), "PaperTodo 中文 'quoted' path", "plugins", "tools.codex-cli-bridge.native");
const string task = "做个番茄钟插件\n保留本次内容";
var initial = CodexPromptState.Read("{}");
var composed = initial.PrependTo(task, pluginDirectory);
var expectedPath = Path.Combine(pluginDirectory, "skills", CodexPromptState.SkillName, "SKILL.md");
Check(composed.Contains(CodexPromptState.BuiltInDefaultPrompt), "The prompt sent to Codex must match the untouched editor.");
Check(composed.Contains(JsonSerializer.Serialize(expectedPath)), "The bundled Skill must resolve against the actual installation path.");
Check(composed.EndsWith(task, StringComparison.Ordinal), "Task content must be preserved after prompt injection.");

var edited = initial with { DefaultPrompt = "自定义提示词" };
var restored = CodexPromptState.Read(JsonSerializer.Serialize(edited));
Check(restored.PrependTo(task, pluginDirectory).Contains("自定义提示词"), "Saved custom prompt must be sent.");
Check(!restored.PrependTo(task, pluginDirectory).Contains(CodexPromptState.BuiltInDefaultPrompt),
    "Custom prompts must not have built-in instructions appended back.");
foreach (var prompt in new[] { "", "  " })
{
    var cleared = CodexPromptState.Read(JsonSerializer.Serialize(edited with { DefaultPrompt = prompt }));
    Check(cleared.PrependTo(task, pluginDirectory) == task, "Clearing the prompt must send only the task, including after restart.");
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
