namespace PaperTodo.Plugin;

/// <summary>Result of an explicit request to accept the current data.json file.</summary>
public sealed record DataReloadResult
{
    /// <summary>applied, pending, conflict, unchanged, invalid, busy, or failed.</summary>
    public string Outcome { get; init; } = "failed";
    public bool Applied { get; init; }
    public int AppliedExternalChanges { get; init; }
    public int ConflictCount { get; init; }
    public string? ConflictFile { get; init; }
    public string? ConflictDiffFile { get; init; }
    public bool RestartRequired { get; init; }
    public string? Error { get; init; }
}

public sealed record DataReloadStatus
{
    /// <summary>Active workspace identity. Reload/merge never switches this identity.</summary>
    public int WorkspaceNumber { get; init; } = 1;
    public string DataFileName { get; init; } = "data.json";
    public bool FollowsMainSettings { get; init; }
    public bool IsReloading { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DataReloadResult? LastResult { get; init; }
}
