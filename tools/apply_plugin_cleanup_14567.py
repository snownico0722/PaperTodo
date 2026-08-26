from pathlib import Path
import json
import re
import shutil
import xml.etree.ElementTree as ET


def read(path: str) -> str:
    return Path(path).read_text(encoding="utf-8")


def write(path: str, text: str) -> None:
    Path(path).write_text(text, encoding="utf-8")


def replace_once(path: str, old: str, new: str) -> None:
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{path}: expected exactly one literal match, found {count}")
    write(path, text.replace(old, new, 1))


def regex_once(path: str, pattern: str, replacement: str, flags=0) -> None:
    text = read(path)
    result, count = re.subn(pattern, replacement, text, count=1, flags=flags)
    if count != 1:
        raise SystemExit(f"{path}: expected exactly one regex match, found {count}: {pattern}")
    write(path, result)


def remove_native_metadata(path: str) -> None:
    text = read(path)
    patterns = [
        r'^    public string Id =>[^\n]*;\n',
        r'^    public string DisplayName =>[^\n]*;\n',
        r'^    public string Description =>[^\n]*;\n',
        r'^    public Version Version =>[^\n]*;\n',
        r'^    public string ApiVersion =>[^\n]*;\n',
        r'^    public int StateVersion =>[^\n]*;\n',
        r'^    public PaperBodyCapabilities Capabilities =>[^\n]*;\n',
        r'^    public PaperBodyRuntimeRequirements RuntimeRequirements =>[^\n]*;\n',
        r'^    public PaperBodyRuntimeRequirements RuntimeRequirements =>\n        PaperBodyRuntimeRequirements\.[A-Za-z]+;\n',
    ]
    removed = 0
    for pattern in patterns:
        text, count = re.subn(pattern, '', text, flags=re.MULTILINE)
        removed += count
    if removed < 7:
        raise SystemExit(f"{path}: expected native manifest metadata declarations, removed only {removed}")
    write(path, text)


# 6) Native plugin.json is the single metadata authority.
contracts = "PaperTodo.Plugin.Abstractions/PaperBodyPluginContracts.cs"
regex_once(
    contracts,
    r'public interface IPaperBodyPlugin\n\{.*?\n\}\n\n/// <summary>\n/// One live body instance',
    '''public interface IPaperBodyPlugin\n{\n    /// <summary>\n    /// Migrate persisted JSON before Create is called. The target version comes from plugin.json.\n    /// The default implementation keeps the old JSON unchanged.\n    /// </summary>\n    string MigrateState(string stateJson, int fromVersion) => stateJson;\n\n    IPaperBodySession Create(PaperBodyContext context);\n}\n\n/// <summary>\n/// One live body instance''',
    flags=re.DOTALL)
replace_once(
    contracts,
    '''/// A fully trusted, unsandboxed native plugin loaded from one self-contained\n/// plugins/&lt;plugin-id&gt;/ folder with the current user's permissions. Implementations must provide a\n/// public parameterless constructor and act as stateless factories. PaperTodo creates a fresh plugin\n/// object for every body session.\n''',
    '''/// A fully trusted, unsandboxed native plugin loaded from one self-contained\n/// plugins/&lt;plugin-id&gt;/ folder with the current user's permissions. plugin.json is the single\n/// authority for id/name/version/protocol/state/capability/runtime metadata. Implementations provide\n/// only behavior, must have a public parameterless constructor and act as stateless factories.\n/// PaperTodo creates a fresh plugin object for every body session or app-runtime activation.\n''')

for native_source in [
    "plugin-samples/PaperTodo.Plugin.SampleClock/SampleClockPlugin.cs",
    "plugin-samples/PaperTodo.Plugin.FocusTimer/FocusTimerPlugin.cs",
    "plugin-samples/PaperTodo.Plugin.CloudGenshin/CloudGenshinPlugin.cs",
]:
    remove_native_metadata(native_source)

# Registry: startup-only discovery, no runtime rescan/reload compatibility path.
registry = "src/PaperBodyPluginRegistry.cs"
replace_once(
    registry,
    '''internal sealed record PaperBodyPluginLoadIssue(\n    string SourcePath,\n    string Message,\n    bool RestartRequired = false);\n''',
    '''internal sealed record PaperBodyPluginLoadIssue(\n    string SourcePath,\n    string Message);\n''')
replace_once(registry, '    private HashSet<string> _lastChangedProviderIds = new(StringComparer.Ordinal);\n', '')
replace_once(registry, '        Reload(scanPluginContents: false);\n', '        LoadInitial();\n')
replace_once(registry, '    public IReadOnlySet<string> LastChangedProviderIds => _lastChangedProviderIds;\n\n', '')

regex_once(
    registry,
    r'    public void Reload\(\) => Reload\(scanPluginContents: true\);\n\n    private void Reload\(bool scanPluginContents\)\n    \{.*?\n    \}\n\n    private IEnumerable<string> EnumeratePluginDirectories\(\)',
    '''    private void LoadInitial()\n    {\n        ObjectDisposedException.ThrowIf(_disposed, this);\n        _issues.Clear();\n        _descriptors.Clear();\n\n        _descriptors[PaperBodyProviderIds.Markdown] = new PaperBodyPluginDescriptor(\n            PaperBodyProviderIds.Markdown,\n            Strings.Get("BodyProviderMarkdown"),\n            Strings.Get("BodyProviderMarkdownDescription"),\n            typeof(PaperWindow).Assembly.GetName().Version ?? new Version(1, 0),\n            SupportedPluginApiVersion,\n            1,\n            PaperBodyPluginKind.BuiltIn,\n            PaperBodyCapabilities.TextZoom | PaperBodyCapabilities.NoteLinks,\n            PaperTodoPermissionNames.None,\n            PaperBodyRuntimeRequirements.None,\n            AppContext.BaseDirectory,\n            typeof(PaperWindow).Assembly.Location,\n            "builtin");\n\n        var pluginDirectories = Directory.Exists(PluginRoot)\n            ? EnumeratePluginDirectories()\n            : Array.Empty<string>();\n        foreach (var directory in pluginDirectories)\n        {\n            var manifestPath = Path.Combine(directory, "plugin.json");\n            if (!File.Exists(manifestPath))\n            {\n                continue;\n            }\n\n            try\n            {\n                var manifest = ReadManifest(manifestPath, directory);\n                var descriptor = NormalizeKind(manifest.Kind) switch\n                {\n                    PaperBodyPluginKind.Web => LoadWebDescriptor(manifest, manifestPath),\n                    PaperBodyPluginKind.Native => LoadNativeDescriptor(manifest, manifestPath),\n                    _ => throw new InvalidDataException("Built-in plugins cannot be loaded from disk.")\n                };\n                AddDescriptor(_descriptors, descriptor);\n            }\n            catch (Exception ex)\n            {\n                _issues.Add(new PaperBodyPluginLoadIssue(\n                    manifestPath,\n                    ex.GetBaseException().Message));\n            }\n        }\n    }\n\n    private IEnumerable<string> EnumeratePluginDirectories()''',
    flags=re.DOTALL)

regex_once(
    registry,
    r'    private PaperBodyPluginDescriptor LoadWebDescriptor\(\n        PaperBodyPluginManifest manifest,\n        string manifestPath,\n        bool scanPluginContents\)\n    \{\n        var fingerprint = scanPluginContents\n            \? PluginFolderFingerprint\(manifest.DirectoryPath\)\n            : DiscoveryFingerprint\(\n                manifestPath,\n                manifest.EntryPath,\n                manifest.MiniEntryPath,\n                manifest.RuntimePath,\n                manifest.PaperRuntimePath\);',
    '''    private PaperBodyPluginDescriptor LoadWebDescriptor(\n        PaperBodyPluginManifest manifest,\n        string manifestPath)\n    {\n        var fingerprint = DiscoveryFingerprint(\n            manifestPath,\n            manifest.EntryPath,\n            manifest.MiniEntryPath,\n            manifest.RuntimePath,\n            manifest.PaperRuntimePath);''')

regex_once(
    registry,
    r'    private PaperBodyPluginDescriptor LoadOrReuseNativeDescriptor\(.*?\n    \}\n\n    private PaperBodyNativePluginActivation LoadNativePlugin',
    '''    private PaperBodyPluginDescriptor LoadNativeDescriptor(\n        PaperBodyPluginManifest manifest,\n        string manifestPath)\n    {\n        var directory = manifest.DirectoryPath;\n        if (!string.Equals(\n                Path.GetExtension(manifest.EntryPath),\n                ".dll",\n                StringComparison.OrdinalIgnoreCase))\n        {\n            throw new InvalidDataException("A native plugin entry must be a .dll file.");\n        }\n\n        return new PaperBodyPluginDescriptor(\n            manifest.Id.Trim(),\n            string.IsNullOrWhiteSpace(manifest.Name)\n                ? manifest.Id.Trim()\n                : manifest.Name.Trim(),\n            manifest.Description?.Trim() ?? "",\n            ParseVersion(manifest.Version),\n            manifest.ApiVersion,\n            manifest.StateVersion,\n            PaperBodyPluginKind.Native,\n            ParseCapabilities(manifest.Capabilities),\n            ParsePermissions(manifest.Permissions),\n            ParseRuntimeRequirements(manifest.Requires),\n            directory,\n            manifestPath,\n            DiscoveryFingerprint(manifestPath, manifest.EntryPath),\n            Manifest: manifest);\n    }\n\n    private PaperBodyNativePluginActivation LoadNativePlugin''',
    flags=re.DOTALL)

regex_once(
    registry,
    r'            ValidatePluginId\(plugin.Id\);.*?            var descriptor = new PaperBodyPluginDescriptor\(.*?                Manifest: manifest\);',
    '''            // plugin.json is the sole metadata authority; the CLR type contributes behavior only.\n            var descriptor = discoveredDescriptor with\n            {\n                Fingerprint = fingerprint,\n                NativePluginType = pluginType\n            };''',
    flags=re.DOTALL)

regex_once(
    registry,
    r'\n    private void ReplaceDescriptors\(.*?\n    private static void AddDescriptor\(',
    '\n    private static void AddDescriptor(',
    flags=re.DOTALL)

# Remove now-useless restart-required issue decoration and unused provider-refresh hook.
replace_once(
    "src/AppController.Plugins.cs",
    '''    private UIElement BuildPluginIssueCard(PaperBodyPluginLoadIssue issue)\n    {\n        var label = issue.RestartRequired\n            ? $"{issue.Message} · {Strings.Get("PluginsRestartRequired")}"\n            : issue.Message;\n        return new Border\n''',
    '''    private UIElement BuildPluginIssueCard(PaperBodyPluginLoadIssue issue)\n    {\n        return new Border\n''')
replace_once(
    "src/AppController.Plugins.cs",
    '                        Text = label,\n',
    '                        Text = issue.Message,\n')
regex_once(
    "src/PaperWindow.PluginBodies.cs",
    r'\n    internal void RefreshPaperBodyProviderAvailability\(IReadOnlySet<string> changedProviderIds\)\n    \{.*?\n    \}\n\n    private void AttachCurrentPaperBody\(\)',
    '\n    private void AttachCurrentPaperBody()',
    flags=re.DOTALL)

# Remove strings that existed only for the deleted in-process rescan/reload path.
for resx in [
    "Resources/Strings.resx",
    "Resources/Strings.en.resx",
    "Resources/Strings.ja.resx",
    "Resources/Strings.ko.resx",
]:
    tree = ET.parse(resx)
    root = tree.getroot()
    dead = {
        "PluginsReload",
        "PluginsRestartRequired",
        "PluginsNativeChangedRestart",
        "PluginsNativeRemovedRestart",
    }
    removed = 0
    for node in list(root.findall("data")):
        if node.attrib.get("name") in dead:
            root.remove(node)
            removed += 1
    if removed == 0:
        raise SystemExit(f"{resx}: no retired plugin reload resource keys found")
    ET.indent(tree, space="  ")
    tree.write(resx, encoding="utf-8", xml_declaration=True)

# 1) ReviewArchive background work belongs to provider AppRuntime, not a PaperWindow body session.
review_plugin = '''using PaperTodo.Plugin;\n\nnamespace PaperTodo.Plugin.ReviewArchive;\n\npublic sealed class ReviewArchivePlugin : IPaperBodyPlugin, IPaperAppRuntimeProvider\n{\n    public IPaperBodySession Create(PaperBodyContext context) =>\n        new ReviewArchiveSession(context);\n\n    public IPaperAppRuntime CreateAppRuntime(PaperAppRuntimeContext context) =>\n        new Runtime(context);\n\n    private sealed class Runtime : IPaperAppRuntime\n    {\n        private readonly IDisposable _subscription;\n\n        public Runtime(PaperAppRuntimeContext context)\n        {\n            ReviewArchiveStore.EnsureLoaded();\n            var settings = ReviewArchiveSettingsReader.ReadSettings(context.Settings.Json);\n            _ = ReviewArchiveStore.ImportCurrent(\n                context.Workspace,\n                settings,\n                manual: false);\n            ReviewArchiveStore.ApplyRetention(settings);\n\n            _subscription = context.Workspace.Subscribe(\n                new PaperTodoEventFilter\n                {\n                    Kinds = new HashSet<PaperTodoEventKind>\n                    {\n                        PaperTodoEventKind.PaperChanged,\n                        PaperTodoEventKind.PaperDeleted,\n                        PaperTodoEventKind.TodoCreated,\n                        PaperTodoEventKind.TodoChanged,\n                        PaperTodoEventKind.TodoDeleted\n                    }\n                },\n                value =>\n                {\n                    var current = ReviewArchiveSettingsReader.ReadSettings(\n                        context.Settings.Json);\n                    ReviewArchiveStore.Apply(value, current);\n                });\n        }\n\n        public void Dispose()\n        {\n            _subscription.Dispose();\n            ReviewArchiveStore.Flush();\n        }\n    }\n}\n'''
write("plugin-samples/PaperTodo.Plugin.ReviewArchive/ReviewArchivePlugin.cs", review_plugin)

review_manifest_path = Path("plugin-samples/PaperTodo.Plugin.ReviewArchive/plugin.json")
review_manifest = json.loads(review_manifest_path.read_text(encoding="utf-8"))
review_manifest["capabilities"] = ["textZoom", "appRuntime"]
review_manifest.pop("requires", None)
review_manifest_path.write_text(json.dumps(review_manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

session = "plugin-samples/PaperTodo.Plugin.ReviewArchive/ReviewArchiveSession.cs"
replace_once(session, '    private readonly IDisposable _subscription;\n', '')
regex_once(
    session,
    r'\n        _subscription = context.Workspace.Subscribe\(.*?\n        ReviewArchiveStore.Changed \+= OnArchiveChanged;',
    '\n        ReviewArchiveStore.Changed += OnArchiveChanged;',
    flags=re.DOTALL)
replace_once(session, '        _ = ReviewArchiveStore.ImportCurrent(context.Workspace, _settings, manual: false);\n', '')
replace_once(session, '        _subscription.Dispose();\n', '')

# 4/5) Official Web Clock is one real configurable instance; remove demo-only Mini pause.
clock_source = Path("plugin-samples/PaperTodo.Plugin.OfficialClockWeb")
clock_manifest_path = clock_source / "plugin.json"
clock_manifest = json.loads(clock_manifest_path.read_text(encoding="utf-8"))
clock_manifest["maxPaperInstances"] = 1
clock_manifest_path.write_text(json.dumps(clock_manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

mini_path = clock_source / "web" / "mini.html"
mini = mini_path.read_text(encoding="utf-8")
mini, count = re.subn(r'\n    \.mini-action \{.*?\n    \}', '', mini, count=1, flags=re.DOTALL)
if count != 1:
    raise SystemExit("official clock mini: base action css not found")
mini, count = re.subn(r'\n    \.mini-action:hover,.*?\n    \}', '', mini, count=1, flags=re.DOTALL)
if count != 1:
    raise SystemExit("official clock mini: action state css not found")
mini = mini.replace('''      <button id="pause" class="mini-action" type="button"\n              data-papertodo-interactive aria-pressed="false" title="Web Mini 交互热区示例">暂停</button>\n''', '')
mini = mini.replace('    let paused = false;\n', '')
mini = mini.replace('''    function updateTimer() {\n      clearInterval(timer);\n      if (visible && !paused) timer = setInterval(render, settings.showSeconds ? 250 : 1000);\n      if (!paused) render();\n    }\n\n    function updatePauseButton() {\n      $('pause').textContent = paused ? '继续' : '暂停';\n      $('pause').setAttribute('aria-pressed', String(paused));\n    }\n\n''', '''    function updateTimer() {\n      clearInterval(timer);\n      if (visible) timer = setInterval(render, settings.showSeconds ? 250 : 1000);\n      render();\n    }\n\n''')
mini, count = re.subn(r"\n    \$\('pause'\)\.addEventListener\('click', \(\) => \{.*?\n    \}\);\n", '\n', mini, count=1, flags=re.DOTALL)
if count != 1:
    raise SystemExit("official clock mini: pause click handler not found")
mini = mini.replace('    updatePauseButton();\n', '')
if 'pause' in mini.lower() or 'paused' in mini:
    raise SystemExit("official clock mini: pause residue remains")
mini_path.write_text(mini, encoding="utf-8")

# Keep committed runtime Web plugin identical to its source sample.
clock_runtime = Path("plugins/official.clock.web")
shutil.copy2(clock_source / "plugin.json", clock_runtime / "plugin.json")
for source_file in (clock_source / "web").rglob("*"):
    if source_file.is_file():
        target = clock_runtime / "web" / source_file.relative_to(clock_source / "web")
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source_file, target)

# Documentation: Native behavior comes from code; all metadata lives in plugin.json.
readme = "plugin-samples/README.md"
text = read(readme)
for pattern in [
    r'    public string Id => "com\.example\.hello-native";\n',
    r'    public string DisplayName => "Hello Native";\n',
    r'    public Version Version => new\(1, 0, 0\);\n',
    r'    public string ApiVersion => "2\.0";\n',
    r'    public int StateVersion => 1;\n',
    r'    public PaperBodyRuntimeRequirements RuntimeRequirements =>\n        PaperBodyRuntimeRequirements\.None;\n',
    r'    public PaperBodyCapabilities Capabilities =>\n        PaperBodyCapabilities\.None;\n\n',
]:
    text, count = re.subn(pattern, '', text)
    if count != 1:
        raise SystemExit(f"README native metadata snippet not found: {pattern}")
old = '`id`、`version`、`apiVersion`、`stateVersion` 和 `requires` 对应的 runtime requirements 必须与入口 DLL 实现一致，否则宿主拒绝激活。'
new = '`plugin.json` 是 Native 插件元数据的唯一来源；入口 DLL 不再重复声明 ID、名称、版本、协议版本、状态版本、能力或后台需求，只实现插件行为。'
if old not in text:
    raise SystemExit("README native duplicate metadata sentence not found")
text = text.replace(old, new, 1)
write(readme, text)

review_readme = "plugin-samples/PaperTodo.Plugin.ReviewArchive/README.md"
rr = read(review_readme)
rr += '\n\n## 后台生命周期\n\n复盘记录由 provider 级 App Runtime 持续监听；只要仍存在一张复盘插件 Paper，即使该纸片隐藏、折叠或当前没有窗口，记录器仍保持工作。正文 Session 只负责读取和展示记录。\n'
write(review_readme, rr)

print("plugin cleanup 1/4/5/6/7 applied")
