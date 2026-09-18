using System.Text.Json;
using PaperTodo.Plugin.CodexCliBridge;
using PaperTodo;

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
var defaults = CodexBridgeSettings.Read("{}");
const string executable = @"C:\PaperTodo O'Brien\PaperTodo.exe";
var expectedPath = Path.Combine(pluginDirectory, "skills", CodexPromptState.SkillName, "SKILL.md");

var composedZh = initial.PrependTo(task, pluginDirectory, "zh-CN", defaults, executable);
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

var composedEn = initial.PrependTo(task, pluginDirectory, "en-US", defaults, executable);
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
    var composed = restored.PrependTo(task, pluginDirectory, language, defaults, executable);
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
        Check(cleared.PrependTo(task, pluginDirectory, language,
                defaults with { EnablePluginSkill = false, EnableOperationSkill = false, AllowMcp = false }, executable) == task,
            "Empty prompt with all switches off must send exactly the task.");
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

// Each switch is independent of the other two and of the editable default prompt.
Check(defaults.EnablePluginSkill && defaults.EnableOperationSkill && defaults.AllowMcp,
    "All three new settings must default to enabled for new and existing installations.");
Check(!CodexPromptState.BuiltInDefaultPromptZh.Contains(CodexPromptState.SkillName) &&
      !CodexPromptState.BuiltInDefaultPromptEn.Contains(CodexPromptState.SkillName),
    "The editable built-in prompt must not embed a plugin skill.");
Check(CodexPromptState.BuiltInDefaultPromptZh.Contains("Note") &&
      CodexPromptState.BuiltInDefaultPromptZh.Contains("完整写入") &&
      CodexPromptState.BuiltInDefaultPromptZh.Contains("避免过度冗长"),
    "Chinese built-in prompt must keep todos concise, allow linked notes, and avoid verbose notes.");
Check(CodexPromptState.BuiltInDefaultPromptEn.Contains("link that Note to the todo") &&
      CodexPromptState.BuiltInDefaultPromptEn.Contains("full writes") &&
      CodexPromptState.BuiltInDefaultPromptEn.Contains("avoid unnecessary verbosity"),
    "English built-in prompt must carry the same todo/note length guidance.");
for (var mask = 0; mask < 8; mask++)
foreach (var prompt in new string?[] { null, "", "custom instruction" })
foreach (var language in new[] { "zh-CN", "en-US" })
{
    var settings = CodexBridgeSettings.Read(JsonSerializer.Serialize(new
    {
        enablePluginSkill = (mask & 1) != 0,
        enableOperationSkill = (mask & 2) != 0,
        allowMcp = (mask & 4) != 0
    }));
    var state = new CodexPromptState(prompt);
    var composed = state.PrependTo(task, pluginDirectory, language, settings, executable);
    Check(composed.Contains(JsonSerializer.Serialize(expectedPath)) == settings.EnablePluginSkill,
        "Plugin skill path must follow its own switch.");
    Check(composed.Contains("get_paper") == settings.EnableOperationSkill,
        "Operation instructions must follow their own switch.");
    Check(composed.Contains("--enable-mcp-for-codex") == settings.AllowMcp,
        "MCP activation instructions must follow their own permission switch.");
    Check(composed.EndsWith(task, StringComparison.Ordinal), "Task content must remain intact.");
    Check(state.DefaultPrompt == prompt, "Runtime injection must not modify the editable prompt.");
    if (settings.AllowMcp)
        Check(composed.Contains("O''Brien"), "PowerShell activation must quote apostrophes safely.");
}
foreach (var invalid in new[] { "false", "null", "0", "\"true\"" })
{
    var json = "{\"allowMcp\":" + invalid + "}";
    Check(!CodexBridgeSettings.Read(json).AllowMcp, "Invalid permission values cannot grant activation.");
    Check(!CodexMcpPermission.CanEnable(true, true, json), "The host must reject invalid permission values.");
}
foreach (var invalid in new[] { "{}", "{broken", "null", "[]", "" })
    Check(!CodexMcpPermission.CanEnable(true, true, invalid), "Missing/old/corrupt settings must not grant activation.");
Check(CodexMcpPermission.CanEnable(true, true, "{\"allowMcp\":true}"), "A live authorized plugin may activate MCP.");
Check(!CodexMcpPermission.CanEnable(false, true, "{\"allowMcp\":true}"), "An exiting app cannot activate MCP.");
Check(!CodexMcpPermission.CanEnable(true, false, "{\"allowMcp\":true}"), "An inactive plugin cannot activate MCP.");
Check(!CodexMcpPermission.CanEnable(true, true, "{\"allowMcp\":false}"), "Revoking the setting must deny later activation.");
foreach (var prefix in new[] { "", "--", "/" })
    Check(StartupCommand.Parse(new[] { prefix + "enable-mcp-for-codex" }).Kind == StartupCommandKind.EnableMcpForCodex,
        "Activation must route through the existing single-instance command parser.");
Check(!new StartupCommand(StartupCommandKind.EnableMcpForCodex).CreatesPaper,
    "MCP activation must not create a paper.");
try
{
    CodexBridgeSettings.Read("{broken");
    throw new InvalidOperationException("Broken settings cannot fall back to enabled permissions.");
}
catch (JsonException) { checks++; }

Console.WriteLine($"Codex CLI Bridge: {checks} checks passed.");
