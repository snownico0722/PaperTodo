from pathlib import Path


def replace_once(path: str, old: str, new: str):
    p = Path(path)
    text = p.read_text(encoding='utf-8')
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{path}: expected exactly 1 match, found {count}: {old[:120]!r}')
    p.write_text(text.replace(old, new, 1), encoding='utf-8')


def insert_before_root(path: str, block: str):
    p = Path(path)
    text = p.read_text(encoding='utf-8')
    marker = '</root>'
    if text.count(marker) != 1:
        raise RuntimeError(f'{path}: expected one </root>')
    p.write_text(text.replace(marker, block + '\n' + marker, 1), encoding='utf-8')


replace_once(
    'src/Models.cs',
    '''    public Dictionary<string, string> GlobalHotkeys { get; set; } = new();\n    public Dictionary<string, bool> GlobalHotkeyEnabled { get; set; } = new();\n    public bool DistinguishNumpadShortcutDigits { get; set; }\n    // When true, edge-queue shortcuts expand the paper centered under the current mouse pointer\n''',
    '''    public Dictionary<string, string> GlobalHotkeys { get; set; } = new();\n    public Dictionary<string, bool> GlobalHotkeyEnabled { get; set; } = new();\n    public bool DistinguishNumpadShortcutDigits { get; set; }\n    public bool PreserveLinkedPaperHiddenStateInVisibilityShortcuts { get; set; } = true;\n    public bool SmartShowHideVisibilityShortcuts { get; set; } = true;\n    // When true, edge-queue shortcuts expand the paper centered under the current mouse pointer\n''')

replace_once(
    'src/AppController.Shortcuts.cs',
    '''            ExecuteStartupCommand(new StartupCommand(definition.StartupCommandKind));\n''',
    '''            ExecuteGlobalShortcutCommand(definition);\n''')

replace_once(
    'src/AppController.Shortcuts.cs',
    '''            State.OpenEdgeCapsuleShortcutAtCursor = true;\n            _shortcutRecordingCommandId = null;\n            ApplyShortcutDraft();\n''',
    '''            State.OpenEdgeCapsuleShortcutAtCursor = true;\n            State.PreserveLinkedPaperHiddenStateInVisibilityShortcuts = true;\n            State.SmartShowHideVisibilityShortcuts = true;\n            ClearVisibilityShortcutRestoreSnapshot();\n            _shortcutRecordingCommandId = null;\n            ApplyShortcutDraft();\n''')

replace_once(
    'src/AppController.Shortcuts.cs',
    '''        foreach (var definition in GlobalShortcutCatalog.DefinitionsInGroup(GlobalShortcutGroup.General))\n        {\n            rows.Children.Add(BuildShortcutRow(definition));\n        }\n        var distinguishNumpadToggle = SettingsToggle(\n''',
    '''        foreach (var definition in GlobalShortcutCatalog.DefinitionsInGroup(GlobalShortcutGroup.General))\n        {\n            rows.Children.Add(BuildShortcutRow(definition));\n            if (definition.Id == GlobalShortcutCatalog.Hide)\n            {\n                if (State.AdvancedSettingsMode)\n                {\n                    rows.Children.Add(SettingsToggle(\n                        Strings.Get("ShortcutPreserveLinkedPaperHiddenState"),\n                        State.PreserveLinkedPaperHiddenStateInVisibilityShortcuts,\n                        TogglePreserveLinkedPaperHiddenStateInVisibilityShortcuts));\n                }\n\n                rows.Children.Add(SettingsToggle(\n                    Strings.Get("ShortcutSmartShowHideVisibilityToggle"),\n                    State.SmartShowHideVisibilityShortcuts,\n                    ToggleSmartShowHideVisibilityShortcuts));\n            }\n        }\n        var distinguishNumpadToggle = SettingsToggle(\n''')

replace_once(
    'src/AppController.cs',
    '''    public void ShowPaper(PaperData paper, bool activate = true)\n    {\n        if (IsExiting)\n        {\n            return;\n        }\n\n        if (!_suppressDirty)\n''',
    '''    public void ShowPaper(PaperData paper, bool activate = true)\n    {\n        if (IsExiting)\n        {\n            return;\n        }\n\n        InvalidateVisibilityShortcutSnapshotForExternalCommand();\n        if (!_suppressDirty)\n''')

replace_once(
    'src/AppController.cs',
    '''    public void HidePaper(PaperData paper)\n    {\n        _windows.TryGetValue(paper.Id, out var window);\n''',
    '''    public void HidePaper(PaperData paper)\n    {\n        InvalidateVisibilityShortcutSnapshotForExternalCommand();\n        _windows.TryGetValue(paper.Id, out var window);\n''')

old_show_all = '''    public void ShowAllPapers()\n    {\n        if (IsExiting)\n        {\n            return;\n        }\n\n        // A runtime show supersedes any startup restore still waiting below Render/Loaded.\n        _paperSurfaceRestoreGeneration++;\n        _startupShellPrewarmGeneration++;\n        _isPreparingStartupEdgeCapsules = false;\n        EnsurePapersOnScreen();\n\n        // Runtime show-all must follow the normal per-paper restore path so remembered expanded\n        // geometry wins before edge placement. Batch-only work is deferred until every paper has\n        // restored, avoiding one full queue arrange and one shell-prewarm task per paper.\n        var papersToShow = State.Papers.ToList();\n        var wasSuppressingDirty = _suppressDirty;\n        var wasRestoringRuntimePaperBatch = _isRestoringRuntimePaperBatch;\n        _suppressDirty = true;\n        _trayRefreshSuppressionDepth++;\n        _isRestoringRuntimePaperBatch = true;\n        try\n        {\n            var activationPaper = papersToShow.LastOrDefault(paper =>\n                !(State.UseCapsuleMode && State.UseDeepCapsuleMode && paper.IsCollapsed && CanPaperDisplayAsCapsule(paper)));\n            foreach (var paper in papersToShow)\n            {\n                ShowPaper(paper, activate: ReferenceEquals(paper, activationPaper));\n            }\n        }\n        finally\n        {\n            _isRestoringRuntimePaperBatch = wasRestoringRuntimePaperBatch;\n            _trayRefreshSuppressionDepth--;\n            _suppressDirty = wasSuppressingDirty;\n        }\n\n        ArrangeDeepCapsules(animate: State.EnableAnimations);\n        ScheduleStartupShellPrewarm(papersToShow);\n        RefreshAllTodoRowsForPaperVisibility();\n        RefreshTrayMenu();\n        MarkDirty();\n    }\n'''
new_show_all = '''    public void ShowAllPapers()\n    {\n        InvalidateVisibilityShortcutSnapshotForExternalCommand();\n        ShowPapersBatch(State.Papers.ToList());\n    }\n\n    private void ShowPapersBatch(IReadOnlyList<PaperData> papersToShow)\n    {\n        if (IsExiting)\n        {\n            return;\n        }\n\n        // A runtime show supersedes any startup restore still waiting below Render/Loaded.\n        _paperSurfaceRestoreGeneration++;\n        _startupShellPrewarmGeneration++;\n        _isPreparingStartupEdgeCapsules = false;\n        EnsurePapersOnScreen();\n\n        // Runtime show-all and shortcut restore deliberately share this path so remembered expanded\n        // geometry wins before edge placement. Shortcut restore only filters which papers enter it.\n        var wasSuppressingDirty = _suppressDirty;\n        var wasRestoringRuntimePaperBatch = _isRestoringRuntimePaperBatch;\n        _suppressDirty = true;\n        _trayRefreshSuppressionDepth++;\n        _isRestoringRuntimePaperBatch = true;\n        try\n        {\n            var activationPaper = papersToShow.LastOrDefault(paper =>\n                !(State.UseCapsuleMode && State.UseDeepCapsuleMode && paper.IsCollapsed && CanPaperDisplayAsCapsule(paper)));\n            foreach (var paper in papersToShow)\n            {\n                ShowPaper(paper, activate: ReferenceEquals(paper, activationPaper));\n            }\n        }\n        finally\n        {\n            _isRestoringRuntimePaperBatch = wasRestoringRuntimePaperBatch;\n            _trayRefreshSuppressionDepth--;\n            _suppressDirty = wasSuppressingDirty;\n        }\n\n        ArrangeDeepCapsules(animate: State.EnableAnimations);\n        ScheduleStartupShellPrewarm(papersToShow);\n        RefreshAllTodoRowsForPaperVisibility();\n        RefreshTrayMenu();\n        MarkDirty();\n    }\n'''
replace_once('src/AppController.cs', old_show_all, new_show_all)

replace_once(
    'src/AppController.cs',
    '''    public void HideAllPapers()\n    {\n        _paperSurfaceRestoreGeneration++;\n''',
    '''    public void HideAllPapers()\n    {\n        InvalidateVisibilityShortcutSnapshotForExternalCommand();\n        _paperSurfaceRestoreGeneration++;\n''')

visibility_shortcuts = '''using System;\nusing System.Collections.Generic;\nusing System.Linq;\n\nnamespace PaperTodo;\n\npublic sealed partial class AppController\n{\n    private HashSet<string>? _visibilityShortcutVisibleLinkedPaperIds;\n    private bool _executingVisibilityShortcutCommand;\n\n    private void ExecuteGlobalShortcutCommand(GlobalShortcutDefinition definition)\n    {\n        var commandKind = definition.StartupCommandKind;\n        if (commandKind is not (\n                StartupCommandKind.Show or\n                StartupCommandKind.Hide or\n                StartupCommandKind.Toggle))\n        {\n            ExecuteStartupCommand(new StartupCommand(commandKind));\n            return;\n        }\n\n        ExecuteVisibilityShortcut(commandKind);\n    }\n\n    private void ExecuteVisibilityShortcut(StartupCommandKind commandKind)\n    {\n        var anyShown = State.Papers.Any(IsPaperShown);\n        var effectiveKind = commandKind;\n\n        if (commandKind == StartupCommandKind.Toggle ||\n            (State.SmartShowHideVisibilityShortcuts &&\n             commandKind is StartupCommandKind.Show or StartupCommandKind.Hide))\n        {\n            effectiveKind = anyShown ? StartupCommandKind.Hide : StartupCommandKind.Show;\n        }\n\n        _executingVisibilityShortcutCommand = true;\n        try\n        {\n            switch (effectiveKind)\n            {\n                case StartupCommandKind.Hide:\n                    CaptureVisibilityShortcutRestoreSnapshot();\n                    HideAllPapers();\n                    break;\n                case StartupCommandKind.Show:\n                    ShowAllPapersForVisibilityShortcut();\n                    break;\n            }\n        }\n        finally\n        {\n            _executingVisibilityShortcutCommand = false;\n            if (effectiveKind == StartupCommandKind.Show)\n            {\n                ClearVisibilityShortcutRestoreSnapshot();\n            }\n        }\n    }\n\n    private bool IsLinkedPaperProtectedFromVisibilityShortcutRestore(PaperData paper)\n    {\n        return State.EnableTodoPaperLinks &&\n            State.HideLinkedPapersFromCapsules &&\n            IsPaperLinkedToAnyTodo(paper);\n    }\n\n    private void CaptureVisibilityShortcutRestoreSnapshot()\n    {\n        if (!State.PreserveLinkedPaperHiddenStateInVisibilityShortcuts)\n        {\n            ClearVisibilityShortcutRestoreSnapshot();\n            return;\n        }\n\n        _visibilityShortcutVisibleLinkedPaperIds = State.Papers\n            .Where(paper =>\n                IsLinkedPaperProtectedFromVisibilityShortcutRestore(paper) &&\n                paper.IsVisible)\n            .Select(paper => paper.Id)\n            .ToHashSet(StringComparer.Ordinal);\n    }\n\n    private void ShowAllPapersForVisibilityShortcut()\n    {\n        if (!State.PreserveLinkedPaperHiddenStateInVisibilityShortcuts ||\n            !State.EnableTodoPaperLinks ||\n            !State.HideLinkedPapersFromCapsules)\n        {\n            ShowAllPapers();\n            return;\n        }\n\n        // A shortcut hide records only the linked papers that were actually visible. Papers that\n        // were already hidden stay hidden, while ordinary papers retain Show All's existing semantics.\n        var linkedPapersToRestore = _visibilityShortcutVisibleLinkedPaperIds ??\n            new HashSet<string>(StringComparer.Ordinal);\n        var papersToShow = State.Papers\n            .Where(paper =>\n                !IsLinkedPaperProtectedFromVisibilityShortcutRestore(paper) ||\n                linkedPapersToRestore.Contains(paper.Id))\n            .ToList();\n\n        ShowPapersBatch(papersToShow);\n    }\n\n    private void InvalidateVisibilityShortcutSnapshotForExternalCommand()\n    {\n        if (!_executingVisibilityShortcutCommand)\n        {\n            ClearVisibilityShortcutRestoreSnapshot();\n        }\n    }\n\n    private void ClearVisibilityShortcutRestoreSnapshot()\n    {\n        _visibilityShortcutVisibleLinkedPaperIds = null;\n    }\n\n    private void TogglePreserveLinkedPaperHiddenStateInVisibilityShortcuts()\n    {\n        State.PreserveLinkedPaperHiddenStateInVisibilityShortcuts =\n            !State.PreserveLinkedPaperHiddenStateInVisibilityShortcuts;\n        if (!State.PreserveLinkedPaperHiddenStateInVisibilityShortcuts)\n        {\n            ClearVisibilityShortcutRestoreSnapshot();\n        }\n        MarkDirty();\n    }\n\n    private void ToggleSmartShowHideVisibilityShortcuts()\n    {\n        State.SmartShowHideVisibilityShortcuts =\n            !State.SmartShowHideVisibilityShortcuts;\n        MarkDirty();\n    }\n}\n'''
path = Path('src/AppController.VisibilityShortcuts.cs')
if path.exists():
    raise RuntimeError(f'{path}: already exists')
path.write_text(visibility_shortcuts, encoding='utf-8')

string_blocks = {
    'Resources/Strings.resx': '''  <data name="ShortcutPreserveLinkedPaperHiddenState" xml:space="preserve">\n    <value>保留关联纸片的隐藏状态</value>\n  </data>\n  <data name="ShortcutSmartShowHideVisibilityToggle" xml:space="preserve">\n    <value>显示 / 隐藏快捷键智能切换</value>\n  </data>''',
    'Resources/Strings.en.resx': '''  <data name="ShortcutPreserveLinkedPaperHiddenState" xml:space="preserve">\n    <value>Preserve hidden state of linked papers</value>\n  </data>\n  <data name="ShortcutSmartShowHideVisibilityToggle" xml:space="preserve">\n    <value>Smart toggle for Show / Hide shortcuts</value>\n  </data>''',
    'Resources/Strings.ja.resx': '''  <data name="ShortcutPreserveLinkedPaperHiddenState" xml:space="preserve">\n    <value>関連付けた紙片の非表示状態を保持</value>\n  </data>\n  <data name="ShortcutSmartShowHideVisibilityToggle" xml:space="preserve">\n    <value>表示 / 非表示ショートカットをスマート切替</value>\n  </data>''',
    'Resources/Strings.ko.resx': '''  <data name="ShortcutPreserveLinkedPaperHiddenState" xml:space="preserve">\n    <value>연결된 종이의 숨김 상태 유지</value>\n  </data>\n  <data name="ShortcutSmartShowHideVisibilityToggle" xml:space="preserve">\n    <value>표시 / 숨기기 단축키 스마트 전환</value>\n  </data>''',
}
for file, block in string_blocks.items():
    insert_before_root(file, block)

replace_once(
    'CHANGELOG.md',
    '''- **高级全局快捷键**：新增快捷键一键锁定全部便签、切换全部纸片透明度、全部胶囊透明度或当前焦点纸片透明度。\n''',
    '''- **高级全局快捷键**：新增快捷键一键锁定全部便签、切换全部纸片透明度、全部胶囊透明度或当前焦点纸片透明度。\n- **显隐快捷键语义优化**：显示 / 隐藏快捷键可根据当前显隐状态智能反向切换；高级模式可保留关联纸片在快捷隐藏前的隐藏状态，避免恢复时意外弹出关联窗口。\n''')

assert 'ExecuteGlobalShortcutCommand(definition);' in Path('src/AppController.Shortcuts.cs').read_text(encoding='utf-8')
assert 'ShowPapersBatch(papersToShow);' in Path('src/AppController.VisibilityShortcuts.cs').read_text(encoding='utf-8')
assert Path('src/AppController.cs').read_text(encoding='utf-8').count('private void ShowPapersBatch(') == 1
print('visibility shortcut patch applied')
