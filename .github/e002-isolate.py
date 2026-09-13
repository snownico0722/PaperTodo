from pathlib import Path
import sys

root = Path(sys.argv[1])
action = sys.argv[2]

def read(path):
    return (root / path).read_text(encoding='utf-8')

def write(path, text):
    (root / path).write_text(text, encoding='utf-8', newline='\n')

def replace_once(path, old, new):
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'{path}: expected one match, got {count}')
    write(path, text.replace(old, new, 1))

if action == 'menu':
    replace_once(
        'src/PaperWindow.EdgeCapsule.cs',
        '        _edgeCapsuleHost.SetContextMenu(BuildDeepCapsuleSlotContextMenu());\n        RefreshDeepCapsuleSlotLabel();',
        '        ScheduleDeepCapsuleSlotContextMenuInitialization();\n        RefreshDeepCapsuleSlotLabel();')

    path = 'src/PaperWindow.EdgeCapsuleContextMenu.cs'
    text = read(path)
    anchor = '''public sealed partial class PaperWindow\n{\n    private ContextMenu BuildDeepCapsuleSlotContextMenu()'''
    replacement = '''public sealed partial class PaperWindow\n{\n    private bool _deepCapsuleContextMenuInitializationQueued;\n    private bool _deepCapsuleContextMenuInitialized;\n\n    private void ScheduleDeepCapsuleSlotContextMenuInitialization()\n    {\n        if (_deepCapsuleContextMenuInitializationQueued ||\n            _deepCapsuleContextMenuInitialized ||\n            _edgeCapsuleHost == null)\n        {\n            return;\n        }\n\n        var host = _edgeCapsuleHost;\n        _deepCapsuleContextMenuInitializationQueued = true;\n        Dispatcher.BeginInvoke(\n            (Action)(() =>\n            {\n                _deepCapsuleContextMenuInitializationQueued = false;\n                if (IsClosed ||\n                    _deepCapsuleContextMenuInitialized ||\n                    !ReferenceEquals(host, _edgeCapsuleHost))\n                {\n                    return;\n                }\n\n                host.SetContextMenu(BuildDeepCapsuleSlotContextMenu());\n                _deepCapsuleContextMenuInitialized = true;\n            }),\n            System.Windows.Threading.DispatcherPriority.SystemIdle);\n    }\n\n    private ContextMenu BuildDeepCapsuleSlotContextMenu()'''
    if text.count(anchor) != 1:
        raise SystemExit('context menu anchor missing')
    write(path, text.replace(anchor, replacement, 1))

elif action == 'profile':
    path = 'tests/PaperTodo.LifecycleChecks/Program.cs'
    old = '''            var baseline = args.Contains("--baseline");\n            var repetitions = args.Contains("--profile") ? 3 : 1;\n            var cases = args.Contains("--profile")\n                ? new[] { "capsules-1", "capsules-5", "capsules-10", "missing-monitor", "scripts" }\n                : Cases;'''
    new = '''            var baseline = args.Contains("--baseline");\n            var capsules10Profile = args.Contains("--capsules10-profile");\n            var repetitions = capsules10Profile ? 5 : args.Contains("--profile") ? 3 : 1;\n            var cases = capsules10Profile\n                ? new[] { "capsules-10" }\n                : args.Contains("--profile")\n                    ? new[] { "capsules-1", "capsules-5", "capsules-10", "missing-monitor", "scripts" }\n                    : Cases;'''
    replace_once(path, old, new)
else:
    raise SystemExit(f'unknown action: {action}')
