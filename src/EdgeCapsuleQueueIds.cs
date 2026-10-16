namespace PaperTodo;

/// <summary>
/// 自由队列模式下「队列实例身份」的编解码。
///
/// 队列实例 key 形如 <c>"{monitor}|{side}#{queueId}"</c>；当 queueId 为空时退化为
/// <c>"{monitor}|{side}"</c>，与 D-006 旧的「一条边恒等于一条队列」语义逐字节相同，
/// 因此旧数据与旧断言均不受影响。
///
/// 纯字符串逻辑，不依赖 WPF / AppState / 任何窗口，可独立单测。
/// </summary>
internal static class EdgeCapsuleQueueIds
{
    /// <summary>edge 与队列实例 id 之间的分隔符。</summary>
    internal const char Separator = '#';

    /// <summary>归一化后实例 id 的最大长度。</summary>
    internal const int MaxLength = 16;

    /// <summary>
    /// 归一化实例 id：去空白、转小写、丢弃非法字符、截断。
    /// 全部被丢弃时返回空串，调用方据此回落到「一条边一条队列」的旧语义。
    /// </summary>
    public static string Normalize(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return "";
        }

        Span<char> buffer = stackalloc char[Math.Min(id.Length, MaxLength)];
        var length = 0;
        foreach (var raw in id)
        {
            if (length == buffer.Length)
            {
                break;
            }

            var c = char.ToLowerInvariant(raw);
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                buffer[length++] = c;
            }
        }

        return length == 0 ? "" : new string(buffer[..length]);
    }

    /// <summary>生成一个新的短实例 id。碰撞概率在单用户场景可忽略。</summary>
    public static string New() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// 组合 edge key 与实例 id。queueId 为空时原样返回 edgeKey ——
    /// 这是「旧数据零迁移」得以成立的关键不变量。
    /// </summary>
    public static string Compose(string edgeKey, string? queueId)
    {
        var normalized = Normalize(queueId);
        return normalized.Length == 0 ? edgeKey : $"{edgeKey}{Separator}{normalized}";
    }

    /// <summary>
    /// 拆分队列实例 key。找不到分隔符时 queueKey 为空串、edgeKey 为整个 key。
    /// </summary>
    public static bool TrySplit(string? key, out string edgeKey, out string queueId)
    {
        var value = key ?? "";
        var separator = value.LastIndexOf(Separator);
        if (separator < 0)
        {
            edgeKey = value;
            queueId = "";
            return false;
        }

        edgeKey = value[..separator];
        queueId = value[(separator + 1)..];
        return true;
    }

    /// <summary>
    /// 从任意（可能带实例后缀的）key 中取出纯 edge 部分。
    /// 供那些语义上只关心「哪条边」的调用点使用，如主胶囊与显示器断连回退。
    /// </summary>
    public static string StripInstance(string? key)
    {
        var value = key ?? "";
        var separator = value.LastIndexOf(Separator);
        return separator < 0 ? value : value[..separator];
    }
}
