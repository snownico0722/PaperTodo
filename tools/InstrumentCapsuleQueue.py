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
counter = Path('src/CapsuleAuditCounts.cs')
counter.write_text('''namespace PaperTodo;
internal static class CapsuleAuditCounts
{
    internal static int Plans, Titles, MasterTexts, GhostWrites;
    internal static bool InsideTick, Observe;
    internal static int Ticks, PaperPolls, Visited, Reconciles;
    internal static long ElapsedTicks;
    internal static bool Visit() { if (InsideTick) Visited++; return true; }
}
''')
routing = 'src/EdgeCapsuleQueueCompositionProxy.Routing.cs'
change(routing, '''        foreach (var member in _members)
        {
            if (member.Window is PaperWindow paper)
                paper.InvalidateEdgeCapsuleQueueProxyPointer();
        }''', '''        var audit = CapsuleAuditCounts.Observe;
        var start = audit ? Stopwatch.GetTimestamp() : 0;
        if (audit) { CapsuleAuditCounts.InsideTick = true; CapsuleAuditCounts.Ticks++; }
        try
        {
            foreach (var member in _members)
                if (member.Window is PaperWindow paper)
                    paper.InvalidateEdgeCapsuleQueueProxyPointer();
        }
        finally
        {
            if (audit) { CapsuleAuditCounts.ElapsedTicks += Stopwatch.GetTimestamp() - start; CapsuleAuditCounts.InsideTick = false; }
        }''')
change('src/AppController.EdgeCapsulePreviewPointerInput.cs', '            !candidate.IsClosed &&', '            CapsuleAuditCounts.Visit() && !candidate.IsClosed &&')
change('src/PaperWindow.EdgeCapsule.cs', '        return WindowNative.TryGetCursorScreenPosition(out var pointer)', '        if (CapsuleAuditCounts.InsideTick) CapsuleAuditCounts.PaperPolls++;\n        return WindowNative.TryGetCursorScreenPosition(out var pointer)')
change('src/PaperWindow.EdgeCapsule.cs', '        var wasRetracting = IsDeepCapsuleSlotRetracting;', '        if (CapsuleAuditCounts.Observe && (dirty & EdgeCapsuleDirty.Pointer) != 0) CapsuleAuditCounts.Reconciles++;\n        var wasRetracting = IsDeepCapsuleSlotRetracting;')
change(fixture, '        CheckAnchorReconstruction();', '        if (Environment.GetEnvironmentVariable("CAPSULE_AUDIT") == "2") { await RunQueueAnimationAudit(c, windows); return; }\n        CheckAnchorReconstruction();')
