namespace PaperTodo.Plugin;

/// <summary>One attempt to consume the application's data.json. No plugin code is reloaded.</summary>
public sealed record DataReloadResult
{
    /// <summary>unchanged, applied, conflict, invalid, busy, or failed.</summary>
    public string Outcome { get; init; } = "unchanged";
    public bool Applied { get; init; }
    public int AppliedExternalChanges { get; init; }
    public int ConflictCount { get; init; }
    public string? ConflictFile { get; init; }
    public string? ConflictDiffFile { get; init; }
    public string? Revision { get; init; }
    public bool RestartRequired { get; init; }
    public string? Error { get; init; }
}

public sealed record DataReloadStatus
{
    public bool IsReloading { get; init; }
    public bool PendingExternalChange { get; init; }
    public string? BaselineRevision { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DataReloadResult? LastResult { get; init; }
}
