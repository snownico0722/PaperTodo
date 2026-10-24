namespace PaperTodo;

/// <summary>
/// 一条位于某个物理边上的队列，以及它在静止状态下的可见起始位置。
/// </summary>
internal readonly record struct EdgeCapsuleShortcutQueue(
    string QueueKey,
    double StartTopDip,
    IReadOnlyList<PaperData> Papers);

/// <summary>
/// 数字快捷键的序号解析。纯函数：调用方先把队列过滤到同一物理边，
/// 这里按可见顺序跨队列连续计数；主胶囊与队列内的 master slot 不占序号。
/// </summary>
internal static class EdgeCapsuleShortcutResolver
{
    /// <summary>
    /// 解析第 <paramref name="ordinal"/> 个（1 起）真实胶囊。
    /// 等 top 时按 QueueKey 稳定排序，与静止避让的约定一致。
    /// </summary>
    internal static PaperData? Resolve(
        IReadOnlyList<EdgeCapsuleShortcutQueue> queues,
        int ordinal)
    {
        if (ordinal is < 1 or > 9)
        {
            return null;
        }

        var remaining = ordinal - 1;
        foreach (var queue in queues
                     .Where(queue => queue.Papers.Count > 0)
                     .OrderBy(queue => queue.StartTopDip)
                     .ThenBy(queue => queue.QueueKey, StringComparer.Ordinal))
        {
            if (remaining < queue.Papers.Count)
            {
                return queue.Papers[remaining];
            }

            remaining -= queue.Papers.Count;
        }

        return null;
    }
}
