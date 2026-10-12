namespace PaperTodo.Plugin;

/// <summary>Result of an explicit request to accept the current data.json file.</summary>
public sealed record DataReloadResult
{
    /// <summary>applied, invalid, busy, or failed.</summary>
    public string Outcome { get; init; } = "failed";
    public bool Applied { get; init; }
    public bool RestartRequired { get; init; }
    public string? Error { get; init; }
}

public sealed record DataReloadStatus
{
    public bool IsReloading { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DataReloadResult? LastResult { get; init; }
}
