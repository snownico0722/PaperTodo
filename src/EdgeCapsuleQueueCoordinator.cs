namespace PaperTodo;

internal readonly record struct EdgeCapsuleQueueMember(
    PaperData Paper,
    string QueueKey);

internal sealed record EdgeCapsuleQueue(
    string Key,
    IReadOnlyList<PaperData> Papers,
    bool HasMaster);

internal sealed class EdgeCapsuleQueuePlan
{
    public EdgeCapsuleQueuePlan(
        IReadOnlyList<EdgeCapsuleQueue> queues,
        IReadOnlyDictionary<string, EdgeCapsulePlacement> placements)
    {
        Queues = queues;
        Placements = placements;
    }

    public IReadOnlyList<EdgeCapsuleQueue> Queues { get; }
    public IReadOnlyDictionary<string, EdgeCapsulePlacement> Placements { get; }
}

internal sealed class EdgeCapsuleArrangeGate
{
    public bool HasPending { get; private set; }
    private bool Animate { get; set; }

    public void Defer(bool animate)
    {
        HasPending = true;
        Animate |= animate;
    }

    public bool Consume(bool animate)
    {
        var result = animate || Animate;
        Clear();
        return result;
    }

    public void Clear()
    {
        HasPending = false;
        Animate = false;
    }
}

/// <summary>
/// Pure queue planner. AppController decides membership; this coordinator is the sole owner of
/// per-queue indices, master offsets and slot counts.
/// </summary>
internal static class EdgeCapsuleQueueCoordinator
{
    public static EdgeCapsuleQueuePlan Build(
        IEnumerable<EdgeCapsuleQueueMember> members,
        bool showMaster)
    {
        var queueMembers = new Dictionary<string, List<PaperData>>(StringComparer.Ordinal);
        var queueOrder = new List<string>();
        foreach (var member in members)
        {
            if (!queueMembers.TryGetValue(member.QueueKey, out var papers))
            {
                papers = new List<PaperData>();
                queueMembers[member.QueueKey] = papers;
                queueOrder.Add(member.QueueKey);
            }
            papers.Add(member.Paper);
        }

        var queues = new List<EdgeCapsuleQueue>(queueOrder.Count);
        var placements = new Dictionary<string, EdgeCapsulePlacement>(StringComparer.Ordinal);
        foreach (var key in queueOrder)
        {
            var papers = queueMembers[key];
            var hasMaster = showMaster && papers.Count > 0;
            var visualOffset = hasMaster ? 1 : 0;
            var slotCount = papers.Count + visualOffset;
            queues.Add(new EdgeCapsuleQueue(key, papers, hasMaster));

            for (var index = 0; index < papers.Count; index++)
            {
                placements[papers[index].Id] = new EdgeCapsulePlacement(
                    index,
                    visualOffset,
                    slotCount);
            }
        }

        return new EdgeCapsuleQueuePlan(queues, placements);
    }
}
