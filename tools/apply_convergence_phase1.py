from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]

def text(path):
    return (ROOT / path).read_text(encoding='utf-8')

def write(path, value):
    (ROOT / path).write_text(value, encoding='utf-8', newline='')

def replace_once(path, old, new):
    value = text(path)
    count = value.count(old)
    if count != 1:
        raise RuntimeError(f'{path}: expected one exact match, got {count}: {old[:80]!r}')
    write(path, value.replace(old, new, 1))

def sub_once(path, pattern, repl, flags=re.S):
    value = text(path)
    value2, count = re.subn(pattern, repl, value, count=1, flags=flags)
    if count != 1:
        raise RuntimeError(f'{path}: expected one regex match, got {count}: {pattern[:100]!r}')
    write(path, value2)

# 1) Todo invariant: one small business rule shared by UI and command paths.
p = 'src/TodoRules.cs'
v = text(p)
insert = r'''

    public static bool ApplyCompletedOrdering(
        List<PaperItem> items,
        bool enabled)
    {
        if (!enabled || items.Count < 2)
        {
            return false;
        }

        var reordered = items
            .Where(item => !item.Done)
            .Concat(items.Where(item => item.Done))
            .ToList();
        if (items.Select(item => item.Id)
            .SequenceEqual(reordered.Select(item => item.Id)))
        {
            return false;
        }

        items.Clear();
        items.AddRange(reordered);
        NormalizeOrders(items);
        return true;
    }

    public static bool ApplyCompletionPolicy(
        List<PaperItem> items,
        IReadOnlyCollection<string> changedItemIds,
        bool done,
        bool autoClearCompleted,
        bool autoMoveCompletedToBottom)
    {
        if (changedItemIds.Count == 0)
        {
            return false;
        }

        if (done && autoClearCompleted)
        {
            var changed = changedItemIds.ToHashSet(StringComparer.Ordinal);
            items.RemoveAll(item => changed.Contains(item.Id) && item.Done);
            if (items.Count == 0)
            {
                items.Add(new PaperItem());
            }
            NormalizeOrders(items);
            return true;
        }

        return ApplyCompletedOrdering(items, autoMoveCompletedToBottom);
    }
'''
idx = v.rfind('\n}')
if idx < 0:
    raise RuntimeError('TodoRules closing brace not found')
v = v[:idx] + insert + v[idx:]
write(p, v)

sub_once(
    'src/PaperWindow.TodoLinksAndSelection.cs',
    r'''    private bool MoveTodoItemsAfterDoneChange\(\n        IReadOnlyCollection<PaperItem> changedItems,\n        bool done\)\n    \{.*?\n    \}\n\n    private void ApplyDoneToSelectedTodos''',
    '''    private bool MoveTodoItemsAfterDoneChange(\n        IReadOnlyCollection<PaperItem> changedItems,\n        bool done)\n    {\n        _ = changedItems;\n        _ = done;\n        return TodoRules.ApplyCompletedOrdering(\n            _paper.Items,\n            _controller.State.AutoMoveCompletedTodosToBottom);\n    }\n\n    private void ApplyDoneToSelectedTodos''')

# New rows may never sit below the completed block when the option is enabled.
replace_once(
    'src/PaperWindow.Todo.cs',
    '''        _paper.Items = ordered;\n        NormalizeTodoItems();\n        NormalizeOrders();\n        _controller.MarkDirty();\n\n        return newItem;''',
    '''        _paper.Items = ordered;\n        NormalizeTodoItems();\n        NormalizeOrders();\n        TodoRules.ApplyCompletedOrdering(\n            _paper.Items,\n            _controller.State.AutoMoveCompletedTodosToBottom);\n        _controller.MarkDirty();\n\n        return newItem;''')
replace_once(
    'src/PaperWindow.Todo.cs',
    '''        ordered.InsertRange(insertIndex, newItems);\n        _paper.Items = ordered;\n        NormalizeTodoItems();\n        NormalizeOrders();\n        _controller.MarkDirty();''',
    '''        ordered.InsertRange(insertIndex, newItems);\n        _paper.Items = ordered;\n        NormalizeTodoItems();\n        NormalizeOrders();\n        TodoRules.ApplyCompletedOrdering(\n            _paper.Items,\n            _controller.State.AutoMoveCompletedTodosToBottom);\n        _controller.MarkDirty();''')

# External/plugin/MCP mutations obey the same completion invariant.
sub_once(
    'src/PaperCommandService.cs',
    r'''(    private IReadOnlyList<PaperItem> AddTodoInputs\(\n        PaperData paper,\n        IReadOnlyList<TodoCreateItem> inputs\)\n    \{.*?        \}\n)(        NormalizeOrders\(paper\);\n        return added;\n    \})''',
    r'''\1        TodoRules.ApplyCompletedOrdering(\n            paper.Items,\n            _controller.State.AutoMoveCompletedTodosToBottom);\n\2''')
replace_once(
    'src/PaperCommandService.cs',
    '''            if (request.Order.HasValue)\n            {\n                MoveTodo(paper, item, request.Order.Value);\n            }\n            NormalizeOrders(paper);''',
    '''            if (request.Order.HasValue)\n            {\n                MoveTodo(paper, item, request.Order.Value);\n            }\n            if (request.Done == true && _controller.State.AutoClearCompletedTodos)\n            {\n                TodoRules.ApplyCompletionPolicy(\n                    paper.Items,\n                    [item.Id],\n                    done: true,\n                    autoClearCompleted: true,\n                    autoMoveCompletedToBottom:\n                        _controller.State.AutoMoveCompletedTodosToBottom);\n            }\n            else\n            {\n                TodoRules.ApplyCompletedOrdering(\n                    paper.Items,\n                    _controller.State.AutoMoveCompletedTodosToBottom);\n            }\n            NormalizeOrders(paper);''')

# 2) Plugin instance limit is a Paper/provider product rule, not a Web runtime rule.
replace_once(
    'src/PaperBodyPluginRegistry.cs',
    '''    public int StateVersion { get; set; } = 1;\n    public string[] Requires { get; set; } = [];''',
    '''    public int StateVersion { get; set; } = 1;\n    public int MaxPaperInstances { get; set; } = 1;\n    public string[] Requires { get; set; } = [];''')
replace_once(
    'src/PaperBodyPluginRegistry.Permissions.cs',
    '''    private static void ValidateProtocolFeatures(PaperBodyPluginManifest manifest)\n    {\n        manifest.Permissions ??= [];\n        NormalizeProtocolFeatures(manifest);''',
    '''    private static void ValidateProtocolFeatures(PaperBodyPluginManifest manifest)\n    {\n        manifest.Permissions ??= [];\n        NormalizeProtocolFeatures(manifest);\n        if (manifest.MaxPaperInstances < 0)\n        {\n            throw new InvalidDataException(\n                "maxPaperInstances must be 0 (unlimited) or a positive integer.");\n        }''')

new_file = ROOT / 'src/AppController.PluginInstances.cs'
new_file.write_text('''namespace PaperTodo;\n\npublic sealed partial class AppController\n{\n    internal bool CanAssignPluginProvider(\n        PaperData paper,\n        PaperBodyPluginDescriptor descriptor)\n    {\n        var limit = descriptor.Manifest?.MaxPaperInstances ?? 1;\n        if (limit == 0)\n        {\n            return true;\n        }\n\n        var otherInstances = State.Papers.Count(candidate =>\n            !ReferenceEquals(candidate, paper) &&\n            candidate.Type == PaperTypes.Note &&\n            string.Equals(\n                candidate.BodyProviderId,\n                descriptor.Id,\n                StringComparison.Ordinal));\n        return otherInstances < limit;\n    }\n\n    internal bool CanCreatePluginPaper(PaperBodyPluginDescriptor descriptor)\n    {\n        var limit = descriptor.Manifest?.MaxPaperInstances ?? 1;\n        return limit == 0 || State.Papers.Count(candidate =>\n            candidate.Type == PaperTypes.Note &&\n            string.Equals(\n                candidate.BodyProviderId,\n                descriptor.Id,\n                StringComparison.Ordinal)) < limit;\n    }\n}\n''', encoding='utf-8', newline='')

replace_once(
    'src/PaperWindow.PluginBodies.cs',
    '''        var normalized = NormalizeBodyProviderId(providerId);\n        if (string.Equals(\n                NormalizeBodyProviderId(_paper.BodyProviderId),\n                normalized,\n                StringComparison.Ordinal))\n        {\n            return;\n        }\n\n        CommitPendingEditsForSave();''',
    '''        var normalized = NormalizeBodyProviderId(providerId);\n        if (string.Equals(\n                NormalizeBodyProviderId(_paper.BodyProviderId),\n                normalized,\n                StringComparison.Ordinal))\n        {\n            return;\n        }\n        if (_controller.PaperBodyPlugins.TryGet(normalized, out var targetDescriptor) &&\n            targetDescriptor.Kind != PaperBodyPluginKind.BuiltIn &&\n            !_controller.CanAssignPluginProvider(_paper, targetDescriptor))\n        {\n            MessageBox.Show(\n                this,\n                Strings.Format(\n                    "PluginInstanceLimitMessage",\n                    targetDescriptor.DisplayName,\n                    targetDescriptor.Manifest?.MaxPaperInstances ?? 1),\n                Strings.Get("PluginInstanceLimitTitle"),\n                MessageBoxButton.OK,\n                MessageBoxImage.Information);\n            return;\n        }\n\n        CommitPendingEditsForSave();''')
replace_once(
    'src/AppController.PluginStartup.cs',
    '''            if (paper == null)\n            {\n                paper = CreatePaper(PaperTypes.Note, show: false);''',
    '''            if (paper == null)\n            {\n                if (!CanCreatePluginPaper(descriptor))\n                {\n                    continue;\n                }\n                paper = CreatePaper(PaperTypes.Note, show: false);''')

# Official clock is the reference plugin that explicitly supports multiple independent Papers.
for manifest_path in [
    'plugin-samples/PaperTodo.Plugin.OfficialClockWeb/plugin.json',
    'plugins/official.clock.web/plugin.json',
]:
    replace_once(
        manifest_path,
        '  "stateVersion": 1,\n',
        '  "stateVersion": 1,\n  "maxPaperInstances": 0,\n')

# Localized instance-limit message.
resources = {
    'Resources/Strings.resx': ('插件实例已达上限', '“{0}”最多允许存在 {1} 张纸片。请先删除已有实例后再创建。'),
    'Resources/Strings.en.resx': ('Plugin instance limit reached', '“{0}” allows at most {1} paper instance(s). Delete an existing instance before creating another.'),
    'Resources/Strings.ja.resx': ('プラグインのインスタンス上限', '「{0}」で作成できる紙片は最大 {1} 個です。既存のインスタンスを削除してから作成してください。'),
    'Resources/Strings.ko.resx': ('플러그인 인스턴스 한도', '“{0}”은(는) 최대 {1}개의 Paper 인스턴스만 허용합니다. 기존 인스턴스를 삭제한 뒤 다시 만드세요.'),
}
for path, (title, message) in resources.items():
    value = text(path)
    block = f'''  <data name="PluginInstanceLimitTitle" xml:space="preserve">\n    <value>{title}</value>\n  </data>\n  <data name="PluginInstanceLimitMessage" xml:space="preserve">\n    <value>{message}</value>\n  </data>\n'''
    if 'name="PluginInstanceLimitTitle"' not in value:
        value = value.replace('</root>', block + '</root>')
    write(path, value)

# 3) Delete obsolete persisted shadow states. Unknown old JSON fields are already ignored by StateStore.
replace_once('src/Models.cs', '    public bool CapsuleCollapseAllActive { get; set; }\n', '')
sub_once(
    'src/Models.cs',
    r'''    public double DeepCapsuleStartTopMargin \{ get; set; \} = EdgeCapsuleLayout\.StartTopMargin;\n\n    // Per-queue vertical start margin, keyed by "monitorDevice\|side"\. A missing key falls back to\n    // the legacy global DeepCapsuleStartTopMargin, so dragging one queue's master only slides that\n    // queue\. Old configs \(no per-queue entries\) keep behaving exactly as the single global margin\.\n''',
    '''    // Per-queue vertical start margin, keyed by "monitorDevice|side". Missing entries use\n    // EdgeCapsuleLayout.StartTopMargin directly; there is no second persisted global authority.\n''')

# StateStore no longer normalizes or synthesizes removed shadow fields.
v = text('src/StateStore.cs')
v = re.sub(r'''\n        if \(!IsFinite\(state\.DeepCapsuleStartTopMargin\)\)\n        \{\n            state\.DeepCapsuleStartTopMargin = EdgeCapsuleLayout\.StartTopMargin;\n        \}\n''', '\n', v, count=1)
v = v.replace('            state.CapsuleCollapseAllActive = false;\n', '')
v = re.sub(r'''\n            if \(state\.CapsuleCollapseAllActiveQueues\.Count > 0\)\n            \{\n                state\.CapsuleCollapseAllActive = true;\n            \}\n''', '\n', v, count=1)
v = re.sub(r'''\n        var keepDeepCapsuleStartTopMargins = state\.UseCapsuleMode && state\.UseDeepCapsuleMode && state\.UseCapsuleCollapseAll;\n        state\.DeepCapsuleStartTopMargin = keepDeepCapsuleStartTopMargins\n            \? NormalizeDeepCapsuleStartTopMargin\(\n                state\.DeepCapsuleStartTopMargin,\n                state\.DeepCapsuleMonitorDeviceName,\n                DeepCapsuleGapSizes\.Value\(state\.DeepCapsuleGapSize\)\)\n            : EdgeCapsuleLayout\.StartTopMargin;\n''', '\n        var keepDeepCapsuleStartTopMargins = state.UseCapsuleMode && state.UseDeepCapsuleMode && state.UseCapsuleCollapseAll;\n', v, count=1)
v = v.replace('        // here). A null dict (older config) becomes empty => every queue falls back to the global.\n', '        // here). Missing entries use the built-in layout default.\n')
write('src/StateStore.cs', v)

# AppController: queue dictionary is the sole collapse-all authority.
v = text('src/AppController.cs')
v = v.replace('State.CapsuleCollapseAllActive || State.CapsuleCollapseAllActiveQueues.Count > 0', 'State.CapsuleCollapseAllActiveQueues.Count > 0')
v = v.replace('            State.CapsuleCollapseAllActive = false;\n', '')
v = v.replace('        SyncLegacyCollapseAllActiveSummary();\n', '')
v = v.replace('        MigrateLegacyCollapseAllActiveQueues(queueKeys);\n', '')
v = re.sub(r'''\n    private void MigrateLegacyCollapseAllActiveQueues\(IEnumerable<string> liveQueueKeys\)\n    \{.*?\n    \}\n''', '\n', v, count=1, flags=re.S)
v = re.sub(r'''\n    private void SyncLegacyCollapseAllActiveSummary\(\)\n    \{.*?\n    \}\n''', '\n', v, count=1, flags=re.S)
# Replace reads of the retired global top margin with the layout default; assignments are handled below.
v = v.replace('State.DeepCapsuleStartTopMargin', 'EdgeCapsuleLayout.StartTopMargin')
# Remove any now-invalid assignment to the constant-like default expression from reset helpers.
v = re.sub(r'''\n        EdgeCapsuleLayout\.StartTopMargin = .*?;''', '', v)
write('src/AppController.cs', v)

# Other consumers read the default directly instead of a persisted global shadow value.
for path in ['src/PaperWindow.cs', 'src/PaperWindow.EdgeCapsule.cs', 'src/MasterCapsuleWindow.cs', 'src/AppController.Settings.cs']:
    value = text(path).replace('_controller.State.DeepCapsuleStartTopMargin', 'EdgeCapsuleLayout.StartTopMargin')\
                      .replace('State.DeepCapsuleStartTopMargin', 'EdgeCapsuleLayout.StartTopMargin')
    # No caller should try to assign the default constant after the field removal.
    value = re.sub(r'''\n\s*EdgeCapsuleLayout\.StartTopMargin\s*=\s*[^;]+;''', '', value)
    write(path, value)

# 4) Documentation: current behavior only; no retired Strict/Virtual Desktop/1.8 claims.
v = text('CHANGELOG.md')
v = v.replace('；宿主升级为 2.0 协议并向下兼容 1.8 插件', '；宿主使用 2.0 协议')
v = re.sub(r'''- \*\*失焦严格收起\*\*：[^\n]*\n''', '- **失焦自动收起**：可让展开纸片在真正失去焦点后自动收起为胶囊；编辑、拖拽、菜单与 Passive 等交互状态不会误触发。\n', v)
v = re.sub(r'''- \*\*虚拟桌面唤醒\*\*：[^\n]*\n''', '', v)
write('CHANGELOG.md', v)

v = text('ARCHITECTURE.md')
v = v.replace('tray / hotkeys / reminders / fullscreen / virtual desktop runtime', 'tray / hotkeys / reminders / fullscreen runtime')
v = re.sub(r'''插件协议以 \*\*2\.0\*\* 为新开发目标，同时兼容加载既有 \*\*1\.8\*\* 插件。''', '插件协议当前只接受 **2.0**；旧 1.8 兼容路径已经删除。', v)
v = v.replace('兼容 1.8 不意味着向旧协议开放 2.0 Top Bar。', '')
write('ARCHITECTURE.md', v)

v = text('plugin-samples/README.md')
if '| `maxPaperInstances` |' not in v:
    v = v.replace('| `stateVersion` | per-paper state 版本，至少为 1 |\n', '| `stateVersion` | per-paper state 版本，至少为 1 |\n| `maxPaperInstances` | 可选；同一 Provider 最多允许存在的真实 Paper 数。省略默认 `1`，`0` 表示不限制；隐藏/折叠 Paper 仍计数 |\n')
    marker = '未知 `requires` 或 `permissions` 会拒绝加载。'
    v = v.replace(marker, '`maxPaperInstances` 是 Paper/provider 级产品约束，对 Native 与 Web 一致生效；插件更新后如果已有实例超过新上限，宿主不会删除现有 Paper，只会阻止继续新增。\n\n' + marker)
write('plugin-samples/README.md', v)

print('phase1 patches applied')
