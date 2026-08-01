namespace PaperTodo.Plugin.ReviewArchive;

internal sealed record ReviewArchiveSettings(
    bool TrackCreated,
    bool KeepDeleted,
    bool ShowOpenItems,
    string InitialImport,
    string DefaultFilter,
    int RetentionDays,
    int MaxRecords,
    string ExportEncoding,
    string ExportDateFormat,
    bool IncludePaperTitle,
    bool ShowDeletedBadge,
    bool ConfirmClear,
    string TitleMode,
    string FixedTitle);

internal sealed class ReviewArchiveViewState
{
    public string Filter { get; set; } = "completed";
    public string Search { get; set; } = "";
}

internal sealed class ReviewArchiveDocument
{
    public int StorageVersion { get; set; } = 1;
    public Dictionary<string, ReviewArchiveRecord> Records { get; set; } =
        new(StringComparer.Ordinal);
}

internal sealed class ReviewArchiveRecord
{
    public string PaperId { get; set; } = "";
    public string PaperTitle { get; set; } = "";
    public string TodoId { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public bool SourceDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset LastChangedAt { get; set; }
    public bool CreatedAtEstimated { get; set; }
    public bool CompletedAtEstimated { get; set; }
    public string Origin { get; set; } = "user";
}
