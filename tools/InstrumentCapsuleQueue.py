from pathlib import Path

def change(path, before, after):
    file = Path(path)
    text = file.read_text(encoding="utf-8-sig")
    assert text.count(before) == 1, (file, text.count(before))
    file.write_text(text.replace(before, after), encoding="utf-8")

change('src/AppController.EdgeCapsuleFreeQueues.cs', '        if (!EdgeCapsuleFreeQueuesEnabled) return ApplyQueueAnchorOverrides(plan, anchors);', '        CapsuleAuditCounts.Plans++;\n        if (!EdgeCapsuleFreeQueuesEnabled) return ApplyQueueAnchorOverrides(plan, anchors);')
for path, counter in [('src/PaperWindow.cs', 'Titles'), ('src/MasterCapsuleWindow.cs', 'MasterTexts')]:
    change(path, '            var formatted = new FormattedText(', '            CapsuleAuditCounts.' + counter + '++;\n            var formatted = new FormattedText(')
change('src/EdgeCapsuleQueueDropGhost.cs', '        if (bandChanged) _window.Topmost = topmost;', '        CapsuleAuditCounts.GhostWrites++;\n        if (bandChanged) _window.Topmost = topmost;')
Path('src/CapsuleAuditCounts.cs').write_text('namespace PaperTodo; internal static class CapsuleAuditCounts { internal static int Plans, Titles, MasterTexts, GhostWrites; }')
fixture = 'tests/PaperTodo.LifecycleChecks/NativeFreeQueueReviewChecks.cs'
change(fixture, 'internal static class NativeFreeQueueReviewChecks', 'internal static partial class NativeFreeQueueReviewChecks')
change(fixture, '        CheckAnchorReconstruction();','        if (Environment.GetEnvironmentVariable("CAPSULE_AUDIT") == "1") { await RunAudit(c, windows); return; }\n        CheckAnchorReconstruction();')
