using System.Reflection;
using PaperTodo;

/// <summary>
/// 数字快捷键的目标选择：自由队列的 key 带 #instance，序号必须按物理边上的
/// 可见队列顺序跨队列连续计数，而不是只查裸 edge key。
/// </summary>
internal static class EdgeCapsuleShortcutChecks
{
    private const BindingFlags Private =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    internal static async Task Run(
        AppController controller,
        IReadOnlyDictionary<string, PaperWindow> windows)
    {
        await Program.Until(
            () => windows.Values.All(window => window.IsDeepCapsuleSlotVisible),
            "shortcut fixture slots did not become visible");

        var papers = controller.State.Papers
            .Where(paper => paper.Id.StartsWith("fixture-", StringComparison.Ordinal))
            .ToList();

        // 该边共 5 个胶囊；第 6 个必须落空，不能回退到对侧或其它显示器。
        Invoke(controller, "ActivateEdgeCapsuleShortcut", DeepCapsuleSides.Right, 6);
        await Task.Delay(100);
        Program.Require(
            papers.All(paper => paper.IsCollapsed),
            "an out-of-range shortcut activated a paper");
        Program.Require(
            windows.Values.All(window => !window.HasExpandedPaperSurface),
            "an out-of-range shortcut expanded a window");

        // 可见顺序：qa=[0,1] @48、qb=[2,3] @320、qc=[4] @450。
        // 第 3 个应命中 qb 的首成员 fixture-2。
        Invoke(controller, "ActivateEdgeCapsuleShortcut", DeepCapsuleSides.Right, 3);
        await Program.Until(
            () => windows["fixture-2"].HasExpandedPaperSurface,
            "ordinal 3 did not activate the second queue's first member");

        foreach (var id in new[] { "fixture-0", "fixture-1", "fixture-3", "fixture-4" })
        {
            Program.Require(
                controller.State.Papers.Single(paper => paper.Id == id).IsCollapsed,
                $"ordinal 3 also activated {id}");
        }
    }

    private static void Invoke(object target, string name, params object?[] args)
    {
        var method = target.GetType().GetMethod(name, Private)
            ?? throw new MissingMethodException(target.GetType().Name, name);
        _ = method.Invoke(target, args);
    }
}
