using PaperTodo;

internal static partial class Program
{
    // 数字快捷键按物理边汇总全部队列后的序号解析。自由队列的 key 带 #instance，
    // 这里锁住“跨队列连续计数、按可见 top 排序、等 top 稳定 tie-break”。
    private static void EdgeCapsuleShortcutOrdinalChecks()
    {
        static PaperData Paper(string id) => new() { Id = id };

        static EdgeCapsuleShortcutQueue Queue(
            string key,
            double top,
            params string[] ids) =>
            new(key, top, ids.Select(Paper).ToArray());

        static string? Resolve(
            IReadOnlyList<EdgeCapsuleShortcutQueue> queues,
            int ordinal) =>
            EdgeCapsuleShortcutResolver.Resolve(queues, ordinal)?.Id;

        var queues = new[]
        {
            Queue("|right#qa", 48, "a1", "a2"),
            Queue("|right#qb", 320, "b1", "b2"),
        };
        Check(Resolve(queues, 1) == "a1", "Ordinal 1 selects the first member of the top queue");
        Check(Resolve(queues, 2) == "a2", "Ordinal 2 stays inside the first queue");
        Check(Resolve(queues, 3) == "b1", "Ordinal 3 crosses into the next instance queue");
        Check(Resolve(queues, 4) == "b2", "Ordinal 4 selects the last member");
        Check(Resolve(queues, 5) == null, "An ordinal past the edge total selects nothing");

        var reversed = new[] { queues[1], queues[0] };
        Check(Resolve(reversed, 3) == "b1", "Input order does not override the visible top order");

        // 同 top 时与静止避让一致：先按 QueueKey Ordinal。
        var tied = new[]
        {
            Queue("|right#qb", 48, "b1"),
            Queue("|right#qa", 48, "a1"),
        };
        Check(Resolve(tied, 1) == "a1", "Equal tops are broken by QueueKey ordinal");

        var mixed = new[]
        {
            Queue("|right", 48, "d1", "d2"),
            Queue("|right#q1", 320, "q1"),
        };
        Check(Resolve(mixed, 2) == "d2", "The default queue participates in the shared ordinal");
        Check(Resolve(mixed, 3) == "q1", "An instance queue continues the default queue's count");

        Check(Resolve(queues, 0) == null, "Ordinal 0 is out of range");
        Check(Resolve(queues, 10) == null, "Ordinal 10 is out of range");
    }
}
