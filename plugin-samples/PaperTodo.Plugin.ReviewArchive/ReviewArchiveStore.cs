using System.Text;
using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.ReviewArchive;

internal static class ReviewArchiveStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly HashSet<Guid> SeenEventIds = [];
    private static readonly Queue<Guid> SeenEventOrder = [];
    private static ReviewArchiveDocument? _document;
    private static string? _path;

    public static event Action? Changed;
    public static string LastSaveError { get; private set; } = "";

    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_document != null)
            {
                return;
            }

            var pluginDirectory = Path.GetDirectoryName(
                typeof(ReviewArchivePlugin).Assembly.Location) ?? AppContext.BaseDirectory;
            var runtimeDirectory = Path.Combine(pluginDirectory, ".runtime");
            _path = Path.Combine(runtimeDirectory, "review-archive.json");
            _document = ReadDocument(_path) ??
                ReadDocument(_path + ".bak") ??
                new ReviewArchiveDocument();
        }
    }

    public static IReadOnlyList<ReviewArchiveRecord> Snapshot()
    {
        EnsureLoaded();
        lock (Gate)
        {
            return _document!.Records.Values
                .Select(Clone)
                .ToArray();
        }
    }

    public static bool ImportCurrent(
        IPaperTodoHostApi host,
        ReviewArchiveSettings settings,
        bool manual)
    {
        if (!settings.TrackCreated ||
            (!manual && string.Equals(settings.InitialImport, "none", StringComparison.Ordinal)))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var changed = false;
        foreach (var todo in host.ListTodos(includeBlank: false))
        {
            if (!manual &&
                string.Equals(settings.InitialImport, "completed", StringComparison.Ordinal) &&
                !todo.Done)
            {
                continue;
            }

            lock (Gate)
            {
                var key = Key(todo.PaperId, todo.Id);
                if (_document!.Records.ContainsKey(key))
                {
                    continue;
                }

                _document.Records[key] = FromSnapshot(
                    todo,
                    now,
                    createdEstimated: true,
                    completedEstimated: todo.Done,
                    origin: manual ? "manual-import" : "initial-import");
                changed = true;
            }
        }

        if (changed)
        {
            SaveAndNotify(settings);
        }
        return changed;
    }

    public static void Apply(PaperTodoEvent value, ReviewArchiveSettings settings)
    {
        EnsureLoaded();
        lock (Gate)
        {
            if (!SeenEventIds.Add(value.Metadata.EventId))
            {
                return;
            }
            SeenEventOrder.Enqueue(value.Metadata.EventId);
            while (SeenEventOrder.Count > 2048)
            {
                SeenEventIds.Remove(SeenEventOrder.Dequeue());
            }
        }

        var changed = value switch
        {
            TodoCreatedEvent item => ApplyTodoCreated(item, settings),
            TodoChangedEvent item => ApplyTodoChanged(item, settings),
            TodoDeletedEvent item => ApplyTodoDeleted(item, settings),
            PaperChangedEvent item => ApplyPaperChanged(item),
            PaperDeletedEvent item => ApplyPaperDeleted(item, settings),
            _ => false
        };

        if (changed)
        {
            SaveAndNotify(settings);
        }
    }

    public static void Clear(ReviewArchiveSettings settings)
    {
        EnsureLoaded();
        lock (Gate)
        {
            _document!.Records.Clear();
        }
        SaveAndNotify(settings);
    }

    public static void ApplyRetention(ReviewArchiveSettings settings)
    {
        EnsureLoaded();
        if (Prune(settings))
        {
            SaveAndNotify(settings, prune: false);
        }
    }

    private static bool ApplyTodoCreated(TodoCreatedEvent item, ReviewArchiveSettings settings)
    {
        if (!settings.TrackCreated)
        {
            return false;
        }

        lock (Gate)
        {
            var key = Key(item.Todo.PaperId, item.Todo.Id);
            if (_document!.Records.TryGetValue(key, out var existing))
            {
                UpdateFromSnapshot(existing, item.Todo, item.Metadata.OccurredAt);
                return true;
            }

            _document.Records[key] = FromSnapshot(
                item.Todo,
                item.Metadata.OccurredAt,
                createdEstimated: false,
                completedEstimated: false,
                origin: OriginText(item.Metadata));
            return true;
        }
    }

    private static bool ApplyTodoChanged(TodoChangedEvent item, ReviewArchiveSettings settings)
    {
        lock (Gate)
        {
            var key = Key(item.After.PaperId, item.After.Id);
            if (!_document!.Records.TryGetValue(key, out var record))
            {
                if (!settings.TrackCreated && !item.After.Done)
                {
                    return false;
                }
                record = FromSnapshot(
                    item.Before,
                    item.Metadata.OccurredAt,
                    createdEstimated: true,
                    completedEstimated: item.Before.Done,
                    origin: OriginText(item.Metadata));
                _document.Records[key] = record;
            }

            UpdateFromSnapshot(record, item.After, item.Metadata.OccurredAt);
            if ((item.ChangedFields & TodoChangedFields.Completion) != 0)
            {
                if (item.After.Done)
                {
                    record.CompletedAt = item.Metadata.OccurredAt;
                    record.CompletedAtEstimated = false;
                }
                else
                {
                    record.CompletedAt = null;
                    record.CompletedAtEstimated = false;
                }
            }
            return true;
        }
    }

    private static bool ApplyTodoDeleted(TodoDeletedEvent item, ReviewArchiveSettings settings)
    {
        lock (Gate)
        {
            var key = Key(item.Todo.PaperId, item.Todo.Id);
            if (!_document!.Records.TryGetValue(key, out var record))
            {
                if (!settings.KeepDeleted || (!settings.TrackCreated && !item.Todo.Done))
                {
                    return false;
                }
                record = FromSnapshot(
                    item.Todo,
                    item.Metadata.OccurredAt,
                    createdEstimated: true,
                    completedEstimated: item.Todo.Done,
                    origin: OriginText(item.Metadata));
                _document.Records[key] = record;
            }

            if (!settings.KeepDeleted)
            {
                return _document.Records.Remove(key);
            }

            UpdateFromSnapshot(record, item.Todo, item.Metadata.OccurredAt);
            record.SourceDeleted = true;
            record.DeletedAt = item.Metadata.OccurredAt;
            return true;
        }
    }

    private static bool ApplyPaperChanged(PaperChangedEvent item)
    {
        if ((item.ChangedFields & PaperChangedFields.Title) == 0)
        {
            return false;
        }

        var changed = false;
        lock (Gate)
        {
            foreach (var record in _document!.Records.Values.Where(record =>
                         string.Equals(record.PaperId, item.After.Id, StringComparison.Ordinal)))
            {
                if (string.Equals(record.PaperTitle, item.After.Title, StringComparison.Ordinal))
                {
                    continue;
                }
                record.PaperTitle = item.After.Title;
                record.LastChangedAt = item.Metadata.OccurredAt;
                changed = true;
            }
        }
        return changed;
    }

    private static bool ApplyPaperDeleted(PaperDeletedEvent item, ReviewArchiveSettings settings)
    {
        var changed = false;
        lock (Gate)
        {
            foreach (var pair in _document!.Records
                         .Where(pair => string.Equals(
                             pair.Value.PaperId,
                             item.Paper.Id,
                             StringComparison.Ordinal))
                         .ToArray())
            {
                if (!settings.KeepDeleted)
                {
                    _document.Records.Remove(pair.Key);
                    changed = true;
                    continue;
                }

                pair.Value.SourceDeleted = true;
                pair.Value.DeletedAt ??= item.Metadata.OccurredAt;
                pair.Value.LastChangedAt = item.Metadata.OccurredAt;
                changed = true;
            }
        }
        return changed;
    }

    private static ReviewArchiveRecord FromSnapshot(
        TodoSnapshot todo,
        DateTimeOffset observedAt,
        bool createdEstimated,
        bool completedEstimated,
        string origin) => new()
    {
        PaperId = todo.PaperId,
        PaperTitle = todo.PaperTitle,
        TodoId = todo.Id,
        Text = todo.Text,
        Done = todo.Done,
        CreatedAt = observedAt,
        CompletedAt = todo.Done ? observedAt : null,
        LastChangedAt = observedAt,
        CreatedAtEstimated = createdEstimated,
        CompletedAtEstimated = completedEstimated,
        Origin = origin
    };

    private static void UpdateFromSnapshot(
        ReviewArchiveRecord record,
        TodoSnapshot todo,
        DateTimeOffset changedAt)
    {
        record.PaperId = todo.PaperId;
        record.PaperTitle = todo.PaperTitle;
        record.TodoId = todo.Id;
        record.Text = todo.Text;
        record.Done = todo.Done;
        record.LastChangedAt = changedAt;
    }

    private static string OriginText(PaperTodoEventMetadata metadata) =>
        metadata.Origin switch
        {
            PaperTodoEventOrigin.Mcp => "mcp",
            PaperTodoEventOrigin.Plugin => "plugin",
            PaperTodoEventOrigin.System => "system",
            PaperTodoEventOrigin.Import => "import",
            _ => "user"
        };

    private static string Key(string paperId, string todoId) => paperId + "/" + todoId;

    private static ReviewArchiveRecord Clone(ReviewArchiveRecord value) => new()
    {
        PaperId = value.PaperId,
        PaperTitle = value.PaperTitle,
        TodoId = value.TodoId,
        Text = value.Text,
        Done = value.Done,
        SourceDeleted = value.SourceDeleted,
        CreatedAt = value.CreatedAt,
        CompletedAt = value.CompletedAt,
        DeletedAt = value.DeletedAt,
        LastChangedAt = value.LastChangedAt,
        CreatedAtEstimated = value.CreatedAtEstimated,
        CompletedAtEstimated = value.CompletedAtEstimated,
        Origin = value.Origin
    };

    private static ReviewArchiveDocument? ReadDocument(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var document = JsonSerializer.Deserialize<ReviewArchiveDocument>(
                File.ReadAllText(path),
                JsonOptions);
            if (document == null)
            {
                return null;
            }
            NormalizeDocument(document);
            return document;
        }
        catch
        {
            return null;
        }
    }

    private static void NormalizeDocument(ReviewArchiveDocument document)
    {
        document.Records ??= new Dictionary<string, ReviewArchiveRecord>(StringComparer.Ordinal);
        if (document.StorageVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported review archive version {document.StorageVersion}.");
        }
    }

    private static bool Prune(ReviewArchiveSettings settings)
    {
        var changed = false;
        lock (Gate)
        {
            if (settings.RetentionDays > 0)
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-settings.RetentionDays);
                foreach (var key in _document!.Records
                             .Where(pair => EffectiveTime(pair.Value) < cutoff)
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    _document.Records.Remove(key);
                    changed = true;
                }
            }

            var overflow = _document!.Records.Count - settings.MaxRecords;
            if (overflow > 0)
            {
                foreach (var key in _document.Records
                             .OrderBy(pair => EffectiveTime(pair.Value))
                             .Take(overflow)
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    _document.Records.Remove(key);
                    changed = true;
                }
            }
        }
        return changed;
    }

    private static DateTimeOffset EffectiveTime(ReviewArchiveRecord value) =>
        value.CompletedAt ?? value.DeletedAt ?? value.LastChangedAt;

    private static void SaveAndNotify(ReviewArchiveSettings settings, bool prune = true)
    {
        EnsureLoaded();
        if (prune)
        {
            _ = Prune(settings);
        }

        string json;
        string path;
        lock (Gate)
        {
            json = JsonSerializer.Serialize(_document, JsonOptions);
            path = _path!;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }
            File.Move(temp, path, overwrite: true);
            LastSaveError = "";
        }
        catch (Exception ex)
        {
            LastSaveError = ex.GetBaseException().Message;
        }
        Changed?.Invoke();
    }
}
