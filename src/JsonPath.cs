using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperTodo;

/// <summary>
/// 把用户对 data.json 冲突的选择应用到 merged state 上。
/// 路径格式参见 <see cref="StateJsonMerge.Merge"/>:
///   - "papers" / "papers[id=xxx]" / "papers[id=xxx].content"
///   - "papers[id=xxx].items" / "papers[id=xxx].items[id=yyy]"
///   - "papers.$order" / "papers[id=xxx].items.$order"(数组顺序冲突)
///   - "$"(未知基线,整状态冲突)
/// </summary>
internal static class JsonPath
{
    /// <summary>
    /// 按 choices 修改 merged state。返回新 AppState(不修改原 next / conflicts)。
    /// choices[path] == KeepExternal → 用 conflict.External 覆盖对应路径;
    /// KeepMemory / 不在 choices 里 → 保留 memory(默认,与 StateMergeConflict.Resolution 一致)。
    /// </summary>
    public static AppState ApplyChoices(
        AppState next,
        IReadOnlyList<StateMergeConflict> conflicts,
        IReadOnlyDictionary<string, ConflictChoice> choices)
    {
        if (choices.Count == 0) return next;

        var json = JsonSerializer.SerializeToNode(next, StateStore.JsonOptions)!.AsObject();
        foreach (var conflict in conflicts)
        {
            if (!choices.TryGetValue(conflict.Path, out var choice)) continue;
            if (choice != ConflictChoice.KeepExternal) continue;
            ApplyExternalToPath(json, conflict.Path, conflict.External);
        }
        return JsonSerializer.Deserialize<AppState>(json.ToJsonString(StateStore.JsonOptions), StateStore.JsonOptions)!;
    }

    /// <summary>
    /// 按 paper ID 级选择整替换 paper。paperChoices[id] == KeepExternal → 从 externalJson.papers
    /// 按 id 找节点,整节点覆盖 next.papers 中同 id 节点;KeepMemory / 不在字典里 → 保留 next。
    /// 替换顺序按字典迭代顺序(Stable);先调用 ApplyChoices 处理 leaf path,再用本方法叠加 paper 级决策。
    /// </summary>
    public static AppState ApplyPaperChoices(
        AppState next,
        IReadOnlyDictionary<string, PaperConflictChoice> paperChoices,
        JsonObject externalJson)
    {
        if (paperChoices.Count == 0) return next;

        var json = JsonSerializer.SerializeToNode(next, StateStore.JsonOptions)!.AsObject();
        var nextPapers = json["papers"] as JsonArray;
        var externalPapers = externalJson["papers"] as JsonArray;
        if (nextPapers == null || externalPapers == null) return next;

        // 建立 externalPapers 按 id 索引,避免每条 paper 线性扫描
        var externalById = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in externalPapers)
        {
            if (node is JsonObject obj && obj["id"] is JsonValue idVal &&
                idVal.TryGetValue<string>(out var id))
            {
                externalById[id] = obj;
            }
        }

        // 按 id 替换 nextPapers 数组中的对应节点;不在 choices 里或 KeepMemory 的不动
        for (var i = 0; i < nextPapers.Count; i++)
        {
            if (nextPapers[i] is not JsonObject target) continue;
            if (target["id"] is not JsonValue targetIdVal ||
                !targetIdVal.TryGetValue<string>(out var targetId)) continue;
            if (!paperChoices.TryGetValue(targetId, out var choice)) continue;
            if (choice != PaperConflictChoice.KeepExternal) continue;
            if (!externalById.TryGetValue(targetId, out var externalNode)) continue;

            // 整 paper 替换,DeepClone 一份避免后续引用污染 externalJson
            nextPapers[i] = (JsonNode?)externalNode.DeepClone();
        }

        return JsonSerializer.Deserialize<AppState>(json.ToJsonString(StateStore.JsonOptions), StateStore.JsonOptions)!;
    }

    private static void ApplyExternalToPath(JsonObject root, string path, JsonNode? externalNode)
    {
        if (externalNode == null) return;

        // 整状态冲突($):External 是整个 external state 的副本,
        // 调用方应改用整个 external 替换 next;此处忽略,ApplyChoices 不应到达此分支
        // (因为 externalNode == null 时已 return)。
        if (path == "$" || path.Length == 0) return;

        var segments = ParsePath(path);

        // 数组顺序冲突:路径以 .$order 结尾
        if (segments.Count > 0 && segments[^1].IsOrderMarker)
        {
            var orderPath = segments.Take(segments.Count - 1).ToList();
            if (externalNode is JsonArray orderArray)
            {
                ReorderArray(root, orderPath, orderArray);
            }
            return;
        }

        // 普通字段值替换
        var (parent, leafName, _) = NavigateToLeaf(root, segments);
        if (parent == null || leafName == null) return;
        parent[leafName] = externalNode.DeepClone();
    }

    private static void ReorderArray(JsonObject root, IReadOnlyList<PathSegment> arrayPath, JsonArray desiredOrder)
    {
        var array = NavigateToArray(root, arrayPath);
        if (array == null) return;

        var idToNode = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item is JsonObject obj && obj["id"] is JsonNode idNode)
            {
                idToNode[idNode.GetValue<string>()] = item;
            }
        }

        var ordered = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in desiredOrder)
        {
            if (entry is JsonValue val && val.TryGetValue<string>(out var id) && idToNode.TryGetValue(id, out var node))
            {
                ordered.Add(node!.DeepClone());
                seen.Add(id);
            }
        }
        // 保留 desiredOrder 没提到的现有元素(防御)
        foreach (var item in array)
        {
            if (item is JsonObject obj && obj["id"] is JsonNode idNode)
            {
                var id = idNode.GetValue<string>();
                if (!seen.Contains(id)) ordered.Add(item.DeepClone());
            }
        }

        // 写回原数组位置
        if (arrayPath.Count == 0)
        {
            // 路径为空,不该发生(因为有 $order 后缀,路径不可能空)
            return;
        }
        var (parent, leafName, _) = NavigateToLeaf(root, arrayPath);
        if (parent != null && leafName != null)
        {
            parent[leafName] = ordered;
        }
    }

    private static JsonArray? NavigateToArray(JsonObject root, IReadOnlyList<PathSegment> segments)
    {
        JsonNode? current = root;
        foreach (var seg in segments)
        {
            if (current == null) return null;
            if (seg.IsArrayById)
            {
                if (current is not JsonArray arr) return null;
                JsonNode? match = null;
                foreach (var item in arr)
                {
                    if (item is JsonObject obj && obj["id"] is JsonNode idNode &&
                        string.Equals(idNode.GetValue<string>(), seg.Id, StringComparison.Ordinal))
                    {
                        match = item;
                        break;
                    }
                }
                current = match;
            }
            else
            {
                if (current is not JsonObject obj) return null;
                current = obj.TryGetPropertyValue(seg.Name, out var n) ? n : null;
            }
        }
        return current as JsonArray;
    }

    private static (JsonObject? parent, string? leafName, JsonNode? leafValue) NavigateToLeaf(
        JsonObject root, IReadOnlyList<PathSegment> segments)
    {
        if (segments.Count == 0) return (null, null, null);
        JsonNode? current = root;
        JsonObject? parent = null;
        string? leafName = null;
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (current == null) return (null, null, null);
            if (seg.IsArrayById)
            {
                if (current is not JsonArray arr) return (null, null, null);
                JsonNode? match = null;
                foreach (var item in arr)
                {
                    if (item is JsonObject obj && obj["id"] is JsonNode idNode &&
                        string.Equals(idNode.GetValue<string>(), seg.Id, StringComparison.Ordinal))
                    {
                        match = item;
                        break;
                    }
                }
                if (match == null) return (null, null, null);
                parent = match as JsonObject;
                current = match;
            }
            else
            {
                if (current is not JsonObject obj) return (null, null, null);
                if (i == segments.Count - 1)
                {
                    parent = obj;
                    leafName = seg.Name;
                    current = obj.TryGetPropertyValue(seg.Name, out var n) ? n : null;
                }
                else
                {
                    current = obj.TryGetPropertyValue(seg.Name, out var n) ? n : null;
                }
            }
        }
        return (parent, leafName, current);
    }

    private static IReadOnlyList<PathSegment> ParsePath(string path)
    {
        var segments = new List<PathSegment>();
        // 拆分 "papers[id=xxx].content" → ["papers[id=xxx]", "content"]
        // 或 "papers.$order" → ["papers", "$order"]
        var dotParts = path.Split('.');
        foreach (var part in dotParts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            if (part == "$order")
            {
                segments.Add(PathSegment.OrderMarker());
            }
            else if (part.EndsWith("]", StringComparison.Ordinal) && part.Contains("[id="))
            {
                var nameEnd = part.IndexOf('[');
                var name = part.Substring(0, nameEnd);
                var idStart = "[id=".Length;
                var idEnd = part.Length - 1; // 去掉 ]
                var id = part.Substring(nameEnd + idStart, idEnd - (nameEnd + idStart));
                if (name.Length > 0) segments.Add(PathSegment.Field(name));
                segments.Add(PathSegment.ArrayById(id));
            }
            else
            {
                segments.Add(PathSegment.Field(part));
            }
        }
        return segments;
    }

    private readonly record struct PathSegment(string Name, string Id, bool IsArrayById, bool IsOrderMarker)
    {
        public static PathSegment Field(string name) => new(name, "", false, false);
        public static PathSegment ArrayById(string id) => new("", id, true, false);
        public static PathSegment OrderMarker() => new("$order", "", false, true);
    }
}
