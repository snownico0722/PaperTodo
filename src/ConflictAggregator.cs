using System.Text.Json.Nodes;

namespace PaperTodo;

/// <summary>
/// 弹窗循环派发的冲突桶。按 ID 聚合 leaf path 冲突,统一派发 paper 级弹窗;
/// 不可聚合的特殊路径($、$order)单独成桶,继续走既有 leaf 弹窗。
/// </summary>
internal abstract record ConflictBucket;

/// <summary>
/// 同一 paper ID 下的所有 leaf 冲突聚合到一个桶,UI 一次弹窗让用户二选一整张 paper。
/// </summary>
internal sealed record PaperBucket(
    string PaperId,
    string PaperType,
    IReadOnlyList<StateMergeConflict> Conflicts,
    JsonNode? MemoryPaper,
    JsonNode? ExternalPaper) : ConflictBucket;

/// <summary>
/// 数组顺序冲突(papers.$order 或 papers[id=xxx].items.$order),
/// 不与具体字段冲突共存时单独成桶,继续走既有 leaf 弹窗。
/// </summary>
internal sealed record OrderBucket(string Scope, StateMergeConflict Conflict) : ConflictBucket;

/// <summary>
/// 未知基线整状态冲突(path == "$"),保留既有行为,走 leaf 弹窗。
/// </summary>
internal sealed record UnknownBaselineBucket(StateMergeConflict Conflict) : ConflictBucket;

/// <summary>
/// 既不属于任何 paper 也不属于特殊路径的孤立 leaf 冲突,继续走 leaf 弹窗。
/// 当前不会产生,但保留桶类型便于后续异常路径扩展。
/// </summary>
internal sealed record SingleLeafBucket(StateMergeConflict Conflict) : ConflictBucket;

/// <summary>
/// 把 <see cref="StateJsonMerge.Merge"/> 输出的 leaf path 冲突列表按 paper ID 聚合,
/// 让 AppController 一次弹一个 paper 级弹窗而不是 N 个 leaf 弹窗。
/// 单侧 ID 缺失的情况已被 StateJsonMerge.MergeEntities 自动处理(不进入冲突列表),
/// 因此这里不会看到"只在 memory"或"只在 external"的 paper。
/// </summary>
internal static class ConflictAggregator
{
    /// <summary>
    /// 按 paper ID 聚合冲突。同一 paper 的所有 leaf 冲突(含 items[id].field 与 items.$order)
    /// 进入同一个 PaperBucket;papers.$order / $ / 其它孤立 leaf 各自单独成桶。
    /// </summary>
    public static IReadOnlyList<ConflictBucket> Group(
        IReadOnlyList<StateMergeConflict> conflicts,
        JsonObject? externalJson)
    {
        if (conflicts.Count == 0) return Array.Empty<ConflictBucket>();

        // 按出现顺序保留桶,先来的 paper 桶先弹窗
        var paperBuckets = new Dictionary<string, PaperBucket>(StringComparer.Ordinal);
        var orderBuckets = new List<OrderBucket>();
        var unknownBuckets = new List<UnknownBaselineBucket>();
        var singleBuckets = new List<SingleLeafBucket>();

        foreach (var conflict in conflicts)
        {
            var path = conflict.Path;
            if (string.IsNullOrEmpty(path) || path == "$")
            {
                unknownBuckets.Add(new UnknownBaselineBucket(conflict));
                continue;
            }

            if (!ConflictPathClassifier.TryParse(path, out var classified))
            {
                // 解析失败(异常路径)当作单 leaf
                singleBuckets.Add(new SingleLeafBucket(conflict));
                continue;
            }

            switch (classified.Kind)
            {
                case PathKind.PapersOrder:
                    orderBuckets.Add(new OrderBucket("papers", conflict));
                    break;

                case PathKind.PaperLeaf when classified.PaperId != null:
                {
                    if (!paperBuckets.TryGetValue(classified.PaperId, out var bucket))
                    {
                        bucket = CreatePaperBucket(classified.PaperId, externalJson);
                        paperBuckets[classified.PaperId] = bucket;
                    }
                    // 用新冲突列表替换桶中的旧冲突列表(record 不可变)
                    paperBuckets[classified.PaperId] = bucket with
                    {
                        Conflicts = AppendConflict(bucket.Conflicts, conflict)
                    };
                    break;
                }

                case PathKind.PaperItemsOrder when classified.PaperId != null:
                {
                    // items.$order 优先并入 paper 桶;桶尚无冲突也新建一个(避免被独立弹窗)
                    if (!paperBuckets.TryGetValue(classified.PaperId, out var bucket))
                    {
                        bucket = CreatePaperBucket(classified.PaperId, externalJson);
                        paperBuckets[classified.PaperId] = bucket;
                    }
                    paperBuckets[classified.PaperId] = bucket with
                    {
                        Conflicts = AppendConflict(bucket.Conflicts, conflict)
                    };
                    break;
                }

                default:
                    singleBuckets.Add(new SingleLeafBucket(conflict));
                    break;
            }
        }

        // 拼接结果:order/unknown/single 先弹(冲突数小),paper 桶按出现顺序
        var result = new List<ConflictBucket>(
            orderBuckets.Count + unknownBuckets.Count + singleBuckets.Count + paperBuckets.Count);
        result.AddRange(orderBuckets);
        result.AddRange(unknownBuckets);
        result.AddRange(singleBuckets);
        result.AddRange(paperBuckets.Values);
        return result;
    }

    private static PaperBucket CreatePaperBucket(string paperId, JsonObject? externalJson)
    {
        // 反查外部 paper 节点用于 UI 提示;
        // 若 externalJson 缺失或不含此 ID,ExternalPaper 留 null,UI 用 "(缺失)" 占位
        JsonNode? externalPaper = null;
        if (externalJson != null && externalJson["papers"] is JsonArray papers)
        {
            foreach (var node in papers)
            {
                if (node is JsonObject obj && obj["id"] is JsonValue idVal &&
                    idVal.TryGetValue<string>(out var id) &&
                    string.Equals(id, paperId, StringComparison.Ordinal))
                {
                    externalPaper = obj;
                    break;
                }
            }
        }

        // PaperType 优先从 externalPaper 取(展示给用户看传入类型);
        // 都没有则空串,UI 占位
        string paperType = "";
        if (externalPaper is JsonObject ep && ep["type"] is JsonValue typeVal &&
            typeVal.TryGetValue<string>(out var t))
        {
            paperType = t;
        }
        else if (externalJson == null)
        {
            // fallback:从 path 解析不出类型,留空
        }

        return new PaperBucket(paperId, paperType, Array.Empty<StateMergeConflict>(),
            MemoryPaper: null, ExternalPaper: externalPaper);
    }

    private static IReadOnlyList<StateMergeConflict> AppendConflict(
        IReadOnlyList<StateMergeConflict> existing,
        StateMergeConflict next)
    {
        var combined = new List<StateMergeConflict>(existing.Count + 1);
        combined.AddRange(existing);
        combined.Add(next);
        return combined;
    }

    private enum PathKind
    {
        Unknown,
        PapersOrder,
        PaperLeaf,
        PaperItemsOrder,
    }

    private readonly record struct ClassifiedPath(PathKind Kind, string? PaperId, string? ItemId);

    private static class ConflictPathClassifier
    {
        /// <summary>
        /// 解析 leaf conflict path。
        /// 形态:
        ///   - "papers.$order"               → PapersOrder
        ///   - "papers[id=X]"               → PaperLeaf(整节点)
        ///   - "papers[id=X].field"         → PaperLeaf
        ///   - "papers[id=X].items[id=Y]"   → PaperLeaf
        ///   - "papers[id=X].items[id=Y].f" → PaperLeaf
        ///   - "papers[id=X].items.$order"  → PaperItemsOrder
        /// </summary>
        public static bool TryParse(string path, out ClassifiedPath result)
        {
            result = default;
            if (string.IsNullOrEmpty(path)) return false;

            // 用 dot 拆分;只拆一层(papers[id=xxx].field.field2)
            var firstDot = path.IndexOf('.');
            if (firstDot < 0)
            {
                // 整节点,如 "papers[id=xxx]"
                if (TryReadIdSegment(path, "papers", out var paperId) && paperId != null)
                {
                    result = new ClassifiedPath(PathKind.PaperLeaf, paperId, null);
                    return true;
                }
                return false;
            }

            var head = path.Substring(0, firstDot);
            var tail = path.Substring(firstDot + 1);

            // 顶层 papers.$order
            if (string.Equals(head, "papers", StringComparison.Ordinal) &&
                string.Equals(tail, "$order", StringComparison.Ordinal))
            {
                result = new ClassifiedPath(PathKind.PapersOrder, null, null);
                return true;
            }

            // papers[id=X]... 后续必须是 .field 或 .items...
            if (!TryReadIdSegment(head, "papers", out var pid) || pid == null)
                return false;

            // 拆 tail:tail 是 "field" / "items" / "items.$order" / "items[id=Y]" / "items[id=Y].f" 等
            var secondDot = tail.IndexOf('.');
            if (secondDot < 0)
            {
                // 整 paper 节点加 .something? 不存在这种形态
                // papers[id=X].field (tail 不含 dot) 或 papers[id=X].items
                if (string.Equals(tail, "items", StringComparison.Ordinal))
                {
                    // papers[id=X].items 整数组节点
                    result = new ClassifiedPath(PathKind.PaperLeaf, pid, null);
                    return true;
                }
                // 普通字段
                result = new ClassifiedPath(PathKind.PaperLeaf, pid, null);
                return true;
            }

            var tailHead = tail.Substring(0, secondDot);
            var tailTail = tail.Substring(secondDot + 1);

            if (string.Equals(tailHead, "items", StringComparison.Ordinal))
            {
                if (string.Equals(tailTail, "$order", StringComparison.Ordinal))
                {
                    result = new ClassifiedPath(PathKind.PaperItemsOrder, pid, null);
                    return true;
                }
                // items[id=Y] 或 items[id=Y].field
                if (TryReadIdSegment(tailHead, "items", out _))
                {
                    result = new ClassifiedPath(PathKind.PaperLeaf, pid, null);
                    return true;
                }
                // items[id=Y].field 之类
                if (tailHead.StartsWith("items[id=", StringComparison.Ordinal))
                {
                    result = new ClassifiedPath(PathKind.PaperLeaf, pid, null);
                    return true;
                }
            }

            // 普通字段路径
            result = new ClassifiedPath(PathKind.PaperLeaf, pid, null);
            return true;
        }

        private static bool TryReadIdSegment(string segment, string expectedName, out string? id)
        {
            id = null;
            if (!segment.StartsWith(expectedName + "[id=", StringComparison.Ordinal)) return false;
            if (!segment.EndsWith("]", StringComparison.Ordinal)) return false;
            var start = expectedName.Length + "[id=".Length;
            id = segment.Substring(start, segment.Length - start - 1);
            return true;
        }
    }
}