from pathlib import Path
R=Path('.')
def rep(s,a,b):
    assert a in s,a[:100]
    return s.replace(a,b)
def save(p,s):
    (R/p).write_text(s,encoding='utf-8',newline='\n')
p='tests/PaperTodo.SettingsApiChecks/PresentationChecks.cs';s=(R/p).read_text();save(p,rep(s,'        await LinkedTitleTruncationBehavior(c, note, todo);','        await LinkedTitleTruncationBehavior(c, note, todo);\n        await PluginBoundaryBehavior(c, owner, note);'))
p='tests/PaperTodo.SettingsApiChecks/Program.cs';s=(R/p).read_text();save(p,rep(s,'ex is McpApiException c ? c.Code : "";','ex is McpApiException c ? c.Code : ex is PaperCommandException d ? d.Code : "";'))
p='tests/PaperTodo.SettingsApiChecks/PaperTodo.SettingsApiChecks.csproj';s=(R/p).read_text();save(p,rep(s,'<ProjectReference Include="../../PaperTodo.csproj" />','<ProjectReference Include="../../PaperTodo.csproj" />\n    <ProjectReference Include="../../plugin-samples/PaperTodo.Plugin.ReviewArchive/PaperTodo.Plugin.ReviewArchive.csproj" />'))
p='tests/PaperTodo.PersistenceChecks/Program.cs';s=(R/p).read_text()
s=rep(s,'    ("plugin-system-shutdown-skips-final-flush", PluginSystemShutdownSkipsFinalFlush),','''    ("plugin-data-uses-one-normal-file", PluginDataUsesOneNormalFile),
    ("unreadable-plugin-data-does-not-become-empty", UnreadablePluginDataDoesNotBecomeEmpty),
    ("legacy-plugin-recovery-file-is-not-selected", LegacyPluginRecoveryFileIsNotSelected),
    ("plugin-system-shutdown-skips-final-flush", PluginSystemShutdownSkipsFinalFlush),''')
i=s.index('static void PluginSystemShutdownSkipsFinalFlush()')
s=s[:i]+r'''static void PluginDataUsesOneNormalFile()
{
    using var scope = new TempDirectory();
    var path = Path.Combine(scope.Path, "data", "sample.plugin.json");
    using (var store = new PaperBodyPluginDataStore(scope.Path))
    {
        Assert(!store.TryReadPaperState("sample.plugin", "p", out _), "missing file is a new plugin");
        store.SavePaperState("sample.plugin", "p", 1, "{\"value\":7}");
    }
    using var loaded = new PaperBodyPluginDataStore(scope.Path);
    Assert(loaded.TryReadPaperState("sample.plugin", "p", out var state), "normal file was not persisted");
    using var json = JsonDocument.Parse(state.Json);
    Assert(json.RootElement.GetProperty("value").GetInt32() == 7, "saved state did not roundtrip");
    Assert(Directory.GetFiles(Path.GetDirectoryName(path)!).Single() == path, "created an alternate state file");
}

static void UnreadablePluginDataDoesNotBecomeEmpty()
{
    using var scope = new TempDirectory();
    var path = Path.Combine(scope.Path, "data", "sample.plugin.json");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    const string original = "{broken plugin data";
    File.WriteAllText(path, original);
    using (var store = new PaperBodyPluginDataStore(scope.Path))
    {
        AssertThrows<JsonException>(() => store.ReadPaperState("sample.plugin", "p"), "unreadable data was hidden");
        AssertThrows<JsonException>(() => store.SavePaperState("sample.plugin", "p", 1, "{}"), "write accepted an unreadable document");
    }
    Assert(File.ReadAllText(path) == original, "failed read replaced original data");
    Assert(Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1, "failed read created a recovery file");
}

static void LegacyPluginRecoveryFileIsNotSelected()
{
    using var scope = new TempDirectory();
    var path = Path.Combine(scope.Path, "data", "sample.plugin.json");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    const string legacy = "legacy contents left for manual use";
    File.WriteAllText(path + ".recovered", legacy);
    using (var store = new PaperBodyPluginDataStore(scope.Path))
    {
        Assert(!store.TryReadPaperState("sample.plugin", "p", out _), "legacy file was selected");
        store.SavePaperState("sample.plugin", "p", 1, "{}");
    }
    Assert(File.Exists(path), "normal state path was not used");
    Assert(File.ReadAllText(path + ".recovered") == legacy, "legacy file was modified or removed");
}

'''+s[i:];save(p,s)
p='doc/ARCHITECTURE.md';s=(R/p).read_text()
s=rep(s,'provider settings、provider Runtime state 与 per-paper frontend state 的独立保存/恢复','provider settings、provider Runtime state 与 per-paper frontend state 的独立读写')
s=rep(s,'插件后端、前端与核心状态解耦，独立迁移和恢复','插件后端、前端与核心状态解耦，提供单文件读写')
s=rep(s,'核心状态保存、图片回收和插件状态恢复具有不同失败语义','核心状态保存、图片回收和插件状态读写具有不同失败语义')
s=rep(s,'插件 settings 与 per-paper state 由 `PaperBodyPluginDataStore` 独立保存，不塞回 `data.json`。插件数据读失败时保留原始问题源，并通过受控 recovery 路径继续；插件数据故障不应把核心 Paper 数据变成不可加载。','插件 settings、Runtime state 与 per-paper state 由 `PaperBodyPluginDataStore` 独立保存在每个 provider 的普通 JSON 文件，不塞回 `data.json`。文件不存在才使用默认状态；已有文件读取失败则原样报告失败，不缓存空数据、不切换恢复文件。保存沿用写临时文件再替换的一次写入完整性。插件自己的备份、恢复或数据库由插件管理，宿主不为其维护第二数据路径。旧恢复文件不再自动选用，也不自动删除或迁移。插件页和快捷键注册使用已有的局部错误展示/失败状态，不让单个插件的数据错误终止其他插件配置。')
s=rep(s,'- 自动恢复 Backoff 期间保留最后一次 Runtime presentation，避免 UI 闪烁；进入最终 `Failed` 后清除 Runtime 动态 Header/Capsule 并回退到 Paper/插件的普通静态展示。','- 首次 Runtime 启动失败直接进入 `Failed`，不自动重试；修改设置或下次正常启动可再尝试。已经成功运行后发生的 Web 后台故障仍使用既有有界恢复；恢复 Backoff 期间保留最后一次 Runtime presentation，进入最终 `Failed` 后清除动态展示。')
s=rep(s,'跨纸片 show/hide/expand 等展示请求只建立事件来源边界并调用既有展示入口，不预先提交其他纸片的内容。','Paper/Todo/Note 及笔记图片查询只读取当前模型/资产，不提交任何正文、不主动同步编辑器。查询可能暂时落后于正在输入的文字，由原编辑/保存流程正常同步；内容 mutation 的准备、同步保存和失败回滚边界不变。\n\n跨纸片 show/hide/expand 等展示请求只建立事件来源边界并调用既有展示入口，不预先提交其他纸片的内容。')
s=rep(s,'### 5.3 外部读写','正文创建失败仍显示已有错误页；主题、字号、激活、失活、缩放等普通会话通知失败只记录本次错误，不提交或销毁现有正文。\n\n### 5.3 外部读写')
s=rep(s,'Web 弹窗复用可见 WebView 环境及本地 origin。','Web 弹窗复用可见 WebView 环境及本地 origin。普通网页/邮件链接交给与正文共用的系统外部打开入口，弹窗自身不导航；下载限制和消息作用域不变。');save(p,s)
p='doc/DECISIONS.md';s=(R/p).read_text()
for heading,note in [('## D-020 — 插件状态与核心 `data.json` 分域持久化','恢复分流部分由 D-040 替代；数据分域与附属清理边界保留'),('## D-021 — 插件与 MCP 共用 `PaperCommandService` 作为外部业务命令边界','读取前提交部分由 D-040 替代；共享 mutation 边界保留')]:
    i=s.index(heading);a=s.index('**Status:** Accepted',i);s=s[:a]+s[a:].replace('**Status:** Accepted','**Status:** Partially superseded by D-040（'+note+'）',1)
s+='''

---

## D-040 — 插件基础读写不承担业务恢复，查询与普通通知不扩大副作用

**Status:** Accepted

### Context

协议 1.2 的恢复分流在读取错误后生成空文档、改写另一条文件路径，并把恢复标记传播到插件页与后来的 MCP 启用条件。共享外部操作准备又让只读查询提交所有正文；复盘插件的提交会写入整个记录池。为小操作追加这些职责，会扩大正常调用和失败的影响范围。

### Decision

- 保留插件数据与核心数据分域及一次保存的临时文件替换；删除宿主 `.json.recovered` 路径、恢复标记和基于它的权限阻断。不存在的文件可默认初始化；已有文件读不出来就报告读取失败，不以空数据继续。旧恢复文件保留在磁盘但不自动选择、迁移或删除。
- 插件自己的长期业务数据、备份与恢复由插件负责。复盘示例移除自己的 `.bak` 回退和复制，使用原来的临时文件替换；本轮不改变关闭时保存和失败保存计时策略。
- Paper/Todo/Note/图片查询不触发正文提交或强制同步，接受模型相对实时编辑的短暂延迟；实际 mutation 的提交、回滚和事件来源处理保持不变。
- 普通正文通知异常不升级成正文销毁；新建待办的初始属性属于创建/追加权限；轻弹窗外链复用正文已有系统打开，不新增权限或桥接接口。
- 首次后台启动失败不重试，实际成功运行后的故障保留原有有界恢复，不创建新的错误分类或恢复状态。

### Why

宿主负责稳定接口与当前操作，不替每个插件定义业务数据恢复策略。删除扩大职责的分支，比在恢复文件、读取同步和失败补偿上继续加状态更可控。参数有效性、已有内容修改权限和一次保存的完整性不是本次删除对象。

### Evidence

- `src/PaperBodyPluginDataStore.cs`、`src/AppController.Mcp.cs`：单路径读写与当前有效设置。
- `src/PaperCommandService.cs`、`src/PaperCommandService.NoteAssets.cs`：纯读取；mutation 仍保留原准备。
- `src/PaperWindow.PluginBodies.cs`、`src/AppController.PluginRuntime.cs`：普通通知与首次启动失败。
- `tests/PaperTodo.PersistenceChecks/Program.cs`、`tests/PaperTodo.SettingsApiChecks/PluginBoundaryChecks.cs`：真实文件、调用次数、初始属性和后台生命周期回归。
''';save(p,s)
p='AGENTS.md';s=(R/p).read_text();s=rep(s,'历史安全取舍见 D-002、D-003、D-020。','历史取舍见 D-002、D-003、D-020、D-040。')
a='- provider settings / per-paper plugin state 由 `PaperBodyPluginDataStore` 管理；不要塞回 `data.json`，也不要让插件自行建立另一套会与宿主竞争的 authoritative state。'
s=rep(s,a,a+'宿主不提供插件业务数据恢复系统；只保留普通文件读写和一次写入完整性，不恢复 `.json.recovered` 分流。插件自己的业务存储、备份和恢复由插件负责。');save(p,s)
p='plugin-samples/README.md';s=(R/p).read_text();a=s.index('### 5.2 Recovery behavior');b=s.index('### 5.3 Global settings',a)
s=s[:a]+'''### 5.2 Basic storage and read failures

The host reads and writes one normal `<plugin ID>.json` file. A missing file uses default state; an existing unreadable file reports an error without substituting an empty document or changing the write path. Saving retains the existing temporary-file replacement. Old `.json.recovered` files are not selected, migrated, or deleted automatically.

Plugins own recovery/backup policies for their own business data; they can use their own files or database. A plugin-data failure does not invalidate PaperTodo's core `data.json`. Do not create a second writable copy of host-managed state.

'''+s[b:]
s=rep(s,'- creating/appending a Todo with completion state, reminder, or `linkedPaperId` also requires `todos.update`;','- creation/append permission covers a new Todo\'s initial completion state, reminder and `linkedPaperId`; updating an existing Todo still requires `todos.update`;')
s=rep(s,'The entry accepts local HTML only; external navigation, new windows, and downloads are cancelled.','The entry accepts local HTML only. HTTP/HTTPS/mailto links and external new-window requests open through the system default application without navigating the popup. Downloads remain cancelled.')
s=rep(s,'### 6.1 Body read/write boundary','Paper, Todo, Note and Note-image reads use the current host model/assets without committing editors. Recent typing may appear after the normal editor/save synchronization; reads do not force a flush. MCP creation/append likewise accepts the new Todo\'s initial fields under additive writes; changing existing content still needs full writes.\n\n### 6.1 Body read/write boundary')
s=rep(s,'The call reports only whether the current Runtime accepted the message.','A Runtime that fails its first startup is not automatically retried. A settings change or the next normal startup can try again. Existing bounded recovery after a successfully running Web Runtime fails is unchanged. Ordinary Body theme/font/activation notifications that throw are logged without replacing the live body; body creation failures still use the host error view.\n\nThe call reports only whether the current Runtime accepted the message.');save(p,s)
save('plugins/tools.codex-cli-bridge.native/skills/papertodo-plugin-creator/references/plugin-development.md',s)
p='plugin-samples/README.zh.md';s=(R/p).read_text();a=s.index('### 5.2');b=s.index('### 5.3',a)
s=s[:a]+'''### 5.2 基础存储与读取失败

宿主只读写普通的 `<插件 ID>.json`。文件不存在才采用默认状态；已有文件无法读取就报告错误，不假装是空数据，也不改写另一条路径。一次保存保留原有临时文件替换方式。旧 `.json.recovered` 文件不再自动选用，也不自动迁移或删除。

插件自己的业务数据备份、恢复或数据库由插件负责。单个插件的数据错误不会使核心 `data.json` 无法读取；不要另造一份与宿主管理状态竞争写入的副本。

'''+s[b:]
s=rep(s,'- 创建/追加带完成状态、提醒或 `linkedPaperId` 的 Todo：还需要 `todos.update`；','- 新建/追加待办的初始完成状态、提醒与 `linkedPaperId` 属于创建/追加权限；修改已有待办仍需要 `todos.update`；')
s=rep(s,'外部跳转、新窗口和下载被取消','普通网页/邮件链接和外部新窗口请求交给系统默认程序打开，弹窗自身不导航；下载仍取消')
s=rep(s,'### 6.1 正文读写边界','纸片、待办、笔记与图片查询只读当前模型/资产，不提交或强制同步编辑器；最新输入可能等待原有编辑/保存流程后再可见。MCP 新建/追加同样允许设置新待办的初始属性，不额外要求完整写入；修改已有内容的权限不变。\n\n### 6.1 正文读写边界')
a=s.index('## 5.');b=s.index('\n',a);s=s[:b]+'''\n
后台首次启动失败不自动重试；修改设置或下次正常启动可再尝试。已经成功运行后的网页后台故障仍沿用有界恢复。正文的普通主题、字体、激活等通知失败只记录错误，不替换当前正文；真正创建正文失败仍显示原有错误页。
'''+s[b:];save(p,s)
for p,text in [('CHANGELOG.zh.md','''- **插件读写与失败处理精简**：查询纸片、待办、笔记和图片不再提交其他正文。插件状态只使用普通数据文件，读取失败直接报告，不再自动从空状态创建恢复文件；旧恢复文件不会被自动删除。普通主题、字号或激活通知出错不再撤掉整个插件正文；后台首次启动失败不再自动重试。
- **插件交互与待办创建**：新建或追加待办时，初始完成状态、提醒和关联不再额外要求修改已有内容的权限。按钮和顶部标签的提示支持换行，小弹窗里的网页/邮件链接交给系统默认程序打开。复盘记录池不再依赖独立备份文件完成保存。
'''),('CHANGELOG.md','''- **Simpler plugin reads and failure handling**: Reading papers, todos, notes or images no longer commits other bodies. Plugin state uses only its normal data file; unreadable data reports an error instead of creating empty recovery state. Existing recovery files are not deleted automatically. Ordinary theme, font or activation callback errors no longer replace the plugin body; a first Runtime startup failure is no longer retried automatically.
- **Plugin interaction and todo creation**: New and appended todos can include their initial completion, reminder and link fields without permission to modify existing items. Action/label tooltips support multiple lines, and popup web/mail links open through the system default application. Review Archive saving no longer depends on a separate backup file.
''')]:
    s=(R/p).read_text();a=s.index('### Unreleased');b=s.index('\n',a);save(p,s[:b+1]+'\n'+text+s[b+1:])
print('docs and test entry points complete')
