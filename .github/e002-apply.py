from pathlib import Path

ROOT = Path('target')

def read(path):
    return (ROOT / path).read_text(encoding='utf-8')

def write(path, text):
    p = ROOT / path
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding='utf-8', newline='\n')

def replace_once(path, old, new):
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'{path}: expected one match, got {count}: {old[:120]!r}')
    write(path, text.replace(old, new, 1))

# 1) Context menu is not first-frame work. Queue it below ApplicationIdle so startup's own
# ApplicationIdle continuation never waits for menu construction.
replace_once(
    'src/PaperWindow.EdgeCapsule.cs',
    '''        _edgeCapsuleHost.SetContextMenu(BuildDeepCapsuleSlotContextMenu());\n        RefreshDeepCapsuleSlotLabel();''',
    '''        ScheduleDeepCapsuleSlotContextMenuInitialization();\n        RefreshDeepCapsuleSlotLabel();''')

context_path = ROOT / 'src/PaperWindow.EdgeCapsuleContextMenu.cs'
context = context_path.read_text(encoding='utf-8')
anchor = '''public sealed partial class PaperWindow\n{\n    private ContextMenu BuildDeepCapsuleSlotContextMenu()'''
replacement = '''public sealed partial class PaperWindow\n{\n    private bool _deepCapsuleContextMenuInitializationQueued;\n    private bool _deepCapsuleContextMenuInitialized;\n\n    private void ScheduleDeepCapsuleSlotContextMenuInitialization()\n    {\n        if (_deepCapsuleContextMenuInitializationQueued ||\n            _deepCapsuleContextMenuInitialized ||\n            _edgeCapsuleHost == null)\n        {\n            return;\n        }\n\n        var host = _edgeCapsuleHost;\n        _deepCapsuleContextMenuInitializationQueued = true;\n        Dispatcher.BeginInvoke(\n            (Action)(() =>\n            {\n                _deepCapsuleContextMenuInitializationQueued = false;\n                if (IsClosed ||\n                    _deepCapsuleContextMenuInitialized ||\n                    !ReferenceEquals(host, _edgeCapsuleHost))\n                {\n                    return;\n                }\n\n                host.SetContextMenu(BuildDeepCapsuleSlotContextMenu());\n                _deepCapsuleContextMenuInitialized = true;\n            }),\n            System.Windows.Threading.DispatcherPriority.SystemIdle);\n    }\n\n    private ContextMenu BuildDeepCapsuleSlotContextMenu()'''
if context.count(anchor) != 1:
    raise SystemExit('context menu class anchor missing or duplicated')
context_path.write_text(context.replace(anchor, replacement, 1), encoding='utf-8', newline='\n')

# 2) Startup Arrange already computes the whole queue together. Replace the final per-paper
# synchronous Flush loop with a startup batch that stages every first-show host transparent,
# crosses one shared hidden Render, reveals all hosts, then crosses one visible Render.
old_flush = '''        if (flushInitialPresentations)\n        {\n            foreach (var queue in plan.Queues)\n            {\n                foreach (var paper in queue.Papers)\n                {\n                    if (_windows.TryGetValue(paper.Id, out var window) &&\n                        ShouldPaperOccupyDeepCapsuleSlot(paper, window))\n                    {\n                        window.FlushStartupDeepCapsulePresentation();\n                    }\n                }\n            }\n        }'''
new_flush = '''        if (flushInitialPresentations)\n        {\n            FlushStartupDeepCapsulePresentations(plan);\n        }'''
replace_once('src/AppController.cs', old_flush, new_flush)

write('src/AppController.StartupEdgePresentation.cs', '''using System.Windows;\nusing System.Windows.Threading;\n\nnamespace PaperTodo;\n\npublic sealed partial class AppController\n{\n    private void FlushStartupDeepCapsulePresentations(EdgeCapsuleQueuePlan plan)\n    {\n        var staged = new List<PaperWindow>();\n\n        foreach (var queue in plan.Queues)\n        {\n            foreach (var paper in queue.Papers)\n            {\n                if (!_windows.TryGetValue(paper.Id, out var window) ||\n                    !ShouldPaperOccupyDeepCapsuleSlot(paper, window))\n                {\n                    continue;\n                }\n\n                if (window.StageStartupDeepCapsulePresentation())\n                {\n                    staged.Add(window);\n                }\n                else\n                {\n                    // Keep the existing per-paper path as a bounded recovery for a failed first\n                    // stage. A normal startup should not enter this branch.\n                    window.FlushStartupDeepCapsulePresentation();\n                }\n            }\n        }\n\n        if (staged.Count == 0)\n        {\n            return;\n        }\n\n        var dispatcher = Application.Current.Dispatcher;\n\n        // All first-show HWNDs exist but remain transparent. Let WPF measure/arrange/render the\n        // complete set once instead of forcing Root.UpdateLayout separately for every host.\n        dispatcher.Invoke(DispatcherPriority.Render, static () => { });\n\n        foreach (var window in staged)\n        {\n            if (!window.RevealStartupDeepCapsulePresentation())\n            {\n                window.RecoverStartupDeepCapsulePresentation();\n            }\n        }\n\n        // This is the only visible startup Render boundary for the staged hosts. DWM remains\n        // asynchronous here; the external startup benchmark owns any DwmFlush measurement.\n        dispatcher.Invoke(DispatcherPriority.Render, static () => { });\n    }\n}\n''')

# 3) Presenter still computes and commits the exact same immutable frame. During the startup-only
# staging scope, the physical host applies that frame transparently and defers forced layout.
old_apply = '''        EnsureDeepCapsuleSlotHost();\n        return _edgeCapsuleHost?.Apply(frame) == true;'''
new_apply = '''        EnsureDeepCapsuleSlotHost();\n        if (_stagingStartupEdgeCapsuleFirstPresentation &&\n            _edgeCapsuleHost?.IsVisible != true)\n        {\n            var staged = _edgeCapsuleHost?.ApplyStartupFirstPresentation(frame) == true;\n            if (staged)\n            {\n                _startupStagedEdgeCapsuleFrame = frame;\n            }\n            return staged;\n        }\n        return _edgeCapsuleHost?.Apply(frame) == true;'''
replace_once('src/PaperWindow.EdgeCapsule.cs', old_apply, new_apply)

write('src/PaperWindow.StartupEdgePresentation.cs', '''namespace PaperTodo;\n\npublic sealed partial class PaperWindow\n{\n    private bool _stagingStartupEdgeCapsuleFirstPresentation;\n    private EdgeCapsulePresentationFrame? _startupStagedEdgeCapsuleFrame;\n\n    internal bool StageStartupDeepCapsulePresentation()\n    {\n        if (!HasDeepCapsuleSlotPlacement || IsClosed)\n        {\n            return false;\n        }\n\n        _startupStagedEdgeCapsuleFrame = null;\n        _stagingStartupEdgeCapsuleFirstPresentation = true;\n        try\n        {\n            FlushStartupDeepCapsulePresentation();\n        }\n        finally\n        {\n            _stagingStartupEdgeCapsuleFirstPresentation = false;\n        }\n\n        return _startupStagedEdgeCapsuleFrame is { Visible: true };\n    }\n\n    internal bool RevealStartupDeepCapsulePresentation()\n    {\n        if (_startupStagedEdgeCapsuleFrame is not { } frame ||\n            _edgeCapsuleHost == null)\n        {\n            return false;\n        }\n\n        var revealed = _edgeCapsuleHost.RevealStartupFirstPresentation(frame);\n        if (revealed)\n        {\n            _startupStagedEdgeCapsuleFrame = null;\n        }\n        return revealed;\n    }\n\n    internal void RecoverStartupDeepCapsulePresentation()\n    {\n        _startupStagedEdgeCapsuleFrame = null;\n        _edgeCapsule.ForceApplyCurrentPresentation();\n        FlushStartupDeepCapsulePresentation();\n    }\n}\n''')

# 4) Host.Apply remains the single physical renderer. Startup staging only changes when the first\n# expensive UpdateLayout and visible opacity are committed.
replace_once(
    'src/EdgeCapsuleHost.cs',
    '''            RefreshNativeMetricsLayout();''',
    '''            RefreshNativeMetricsLayout(updateNow: !_startupFirstPresentationBatchActive);''')

old_visual_state = '''        var contentOpacity = Math.Clamp(frame.ContentOpacity, 0, 1);\n        if (Math.Abs(root.Opacity - contentOpacity) > 0.001)\n        {\n            root.Opacity = contentOpacity;\n        }\n        if (root.IsHitTestVisible != frame.IsHitTestVisible)\n        {\n            root.IsHitTestVisible = frame.IsHitTestVisible;\n        }\n        var outlineVisibility = frame.OutlineVisible\n            ? Visibility.Visible\n            : Visibility.Collapsed;\n        if (Outline.Visibility != outlineVisibility)\n        {\n            Outline.Visibility = outlineVisibility;\n        }\n        var opacity = Math.Clamp(frame.Opacity, 0, 1);\n        if (Math.Abs(window.Opacity - opacity) > 0.001)\n        {\n            window.Opacity = opacity;\n        }'''
replace_once(
    'src/EdgeCapsuleHost.cs',
    old_visual_state,
    '''        ApplyCommittedVisualState(\n            frame,\n            reveal: !_startupFirstPresentationBatchActive);''')

replace_once(
    'src/EdgeCapsuleHost.cs',
    '''    private void RefreshNativeMetricsLayout()\n    {\n        VisualSurface.InvalidateMeasure();\n        VisualSurface.InvalidateArrange();\n        Root.InvalidateMeasure();\n        Root.InvalidateArrange();\n        Root.UpdateLayout();\n    }''',
    '''    private void RefreshNativeMetricsLayout(bool updateNow = true)\n    {\n        VisualSurface.InvalidateMeasure();\n        VisualSurface.InvalidateArrange();\n        Root.InvalidateMeasure();\n        Root.InvalidateArrange();\n        if (updateNow)\n        {\n            Root.UpdateLayout();\n        }\n    }''')

write('src/EdgeCapsuleHost.StartupBatch.cs', '''using System.Windows;\n\nnamespace PaperTodo;\n\ninternal sealed partial class EdgeCapsuleHost\n{\n    private bool _startupFirstPresentationBatchActive;\n\n    internal bool ApplyStartupFirstPresentation(EdgeCapsulePresentationFrame frame)\n    {\n        if (_disposed || Window.IsVisible || !frame.Visible)\n        {\n            return false;\n        }\n\n        _startupFirstPresentationBatchActive = true;\n        try\n        {\n            return Apply(frame);\n        }\n        finally\n        {\n            _startupFirstPresentationBatchActive = false;\n        }\n    }\n\n    internal bool RevealStartupFirstPresentation(EdgeCapsulePresentationFrame frame)\n    {\n        if (_disposed ||\n            !Window.IsVisible ||\n            _appliedFrame != frame ||\n            !MatchesNativePresentationLayout(frame))\n        {\n            if (!_disposed)\n            {\n                ResetForFreshApply();\n            }\n            return false;\n        }\n\n        ApplyCommittedVisualState(frame, reveal: true);\n        return true;\n    }\n\n    private void ApplyCommittedVisualState(\n        EdgeCapsulePresentationFrame frame,\n        bool reveal)\n    {\n        var contentOpacity = reveal\n            ? Math.Clamp(frame.ContentOpacity, 0, 1)\n            : 0;\n        if (Math.Abs(Root.Opacity - contentOpacity) > 0.001)\n        {\n            Root.Opacity = contentOpacity;\n        }\n\n        var hitTestVisible = reveal && frame.IsHitTestVisible;\n        if (Root.IsHitTestVisible != hitTestVisible)\n        {\n            Root.IsHitTestVisible = hitTestVisible;\n        }\n\n        var outlineVisibility = frame.OutlineVisible\n            ? Visibility.Visible\n            : Visibility.Collapsed;\n        if (Outline.Visibility != outlineVisibility)\n        {\n            Outline.Visibility = outlineVisibility;\n        }\n\n        var opacity = reveal\n            ? Math.Clamp(frame.Opacity, 0, 1)\n            : 0;\n        if (Math.Abs(Window.Opacity - opacity) > 0.001)\n        {\n            Window.Opacity = opacity;\n        }\n    }\n}\n''')

# User-visible behavior is only startup responsiveness; fold into the existing Unreleased entry.
changelog = read('CHANGELOG.md')
needle = '启动/退出响应更快'
if needle in changelog and '边缘胶囊首帧' not in changelog:
    changelog = changelog.replace(
        needle,
        '启动/退出响应更快，边缘胶囊首帧改为批量呈现并延后右键菜单初始化',
        1)
    write('CHANGELOG.md', changelog)

print('E-002 production patch applied')
