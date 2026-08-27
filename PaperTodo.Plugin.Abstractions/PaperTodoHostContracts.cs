using System.Collections.Frozen;

namespace PaperTodo.Plugin;

public static class PaperTodoPermissionNames
{
    public const string PapersRead = "papers.read";
    public const string PapersObserve = "papers.observe";
    public const string PapersCreate = "papers.create";
    public const string PapersDelete = "papers.delete";

    public const string TodosRead = "todos.read";
    public const string TodosObserve = "todos.observe";
    public const string TodosAppend = "todos.append";
    public const string TodosUpdate = "todos.update";
    public const string TodosDelete = "todos.delete";

    public const string NotesRead = "notes.read";
    public const string NotesObserve = "notes.observe";
    public const string NotesAppend = "notes.append";
    public const string NotesReplace = "notes.replace";

    public static IReadOnlySet<string> None { get; } =
        Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> All { get; } = new[]
    {
        PapersRead,
        PapersObserve,
        PapersCreate,
        PapersDelete,
        TodosRead,
        TodosObserve,
        TodosAppend,
        TodosUpdate,
        TodosDelete,
        NotesRead,
        NotesObserve,
        NotesAppend,
        NotesReplace
    }.ToFrozenSet(StringComparer.Ordinal);
}

public enum PaperTodoEventKind
{
    PaperCreated,
    PaperChanged,
    PaperDeleted,
    TodoCreated,
    TodoChanged,
    TodoDeleted,
    NoteChanged
}

public enum PaperTodoEventOrigin
{
    User,
    Mcp,
    Plugin,
    System,
    Import
}

[Flags]
public enum PaperChangedFields
{
    None = 0,
    Title = 1 << 0,
    Visibility = 1 << 1,
    Collapsed = 1 << 2,
    AlwaysOnTop = 1 << 3,
    BodyProvider = 1 << 4
}

[Flags]
public enum TodoChangedFields
{
    None = 0,
    Text = 1 << 0,
    Completion = 1 << 1,
    Order = 1 << 2,
    Reminder = 1 << 3,
    LinkedPaper = 1 << 4,
    LinkedPath = 1 << 5
}

public sealed record PaperTodoEventMetadata(
    Guid EventId,
    Guid OperationId,
    DateTimeOffset OccurredAt,
    PaperTodoEventOrigin Origin,
    string? SourcePluginId);

public sealed record PaperSnapshot(
    string Id,
    string Type,
    string Title,
    bool IsVisible,
    bool IsCollapsed,
    bool AlwaysOnTop,
    string BodyProviderId);

public sealed record TodoSnapshot(
    string PaperId,
    string PaperTitle,
    string Id,
    string Text,
    bool Done,
    int Order,
    string? LinkedPaperId,
    string? LinkedPath,
    DateTimeOffset? ReminderAt)
{
    /// <summary>
    /// Protocol 2.1 additive metadata for LinkedPath. Null means there is no linked path or the
    /// host cannot classify it; true means directory and false means file.
    /// </summary>
    public bool? LinkedPathIsDirectory { get; init; }
}

public sealed record NoteSnapshot(
    string PaperId,
    string PaperTitle,
    string BodyProviderId,
    bool ContentAvailable,
    string Content);

public abstract record PaperTodoEvent(
    PaperTodoEventKind Kind,
    PaperTodoEventMetadata Metadata);

public sealed record PaperCreatedEvent(
    PaperSnapshot Paper,
    PaperTodoEventMetadata EventMetadata)
    : PaperTodoEvent(PaperTodoEventKind.PaperCreated, EventMetadata);

public sealed record PaperChangedEvent(
    PaperSnapshot Before,
    PaperSnapshot After,
    PaperChangedFields ChangedFields,
    PaperTodoEventMetadata EventMetadata)
    : PaperTodoEvent(PaperTodoEventKind.PaperChanged, EventMetadata);

public sealed record PaperDeletedEvent(
    PaperSnapshot Paper,
    PaperTodoEventMetadata EventMetadata)
    : PaperTodoEvent(PaperTodoEventKind.PaperDeleted, EventMetadata);

public sealed record TodoCreatedEvent(
    TodoSnapshot Todo,
    PaperTodoEventMetadata EventMetadata)
    : PaperTodoEvent(PaperTodoEventKind.TodoCreated, EventMetadata);

public sealed record TodoChangedEvent(
    TodoSnapshot Before,
    TodoSnapshot After,
    TodoChangedFields ChangedFields,
    PaperTodoEventMetadata EventMetadata)
    : PaperTodoEvent(PaperTodoEventKind.TodoChanged, EventMetadata);

public sealed record TodoDeletedEvent(
    TodoSnapshot Todo,
    PaperTodoEventMetadata EventMetadata)
    : PaperTodoEvent(PaperTodoEventKind.TodoDeleted, EventMetadata);

public sealed record NoteChangedEvent(
    string PaperId,
    string PaperTitle,
    int PreviousLength,
    int CurrentLength,
    PaperTodoEventMetadata EventMetadata)
    : PaperTodoEvent(PaperTodoEventKind.NoteChanged, EventMetadata);

public sealed class PaperTodoEventFilter
{
    public IReadOnlySet<PaperTodoEventKind>? Kinds { get; init; }
    public IReadOnlySet<string>? PaperIds { get; init; }
    public bool ExcludeOwnOperations { get; init; } = true;
}

public sealed record TodoCreateItem
{
    public string Text { get; init; } = "";
    public bool Done { get; init; }
    public string? LinkedPaperId { get; init; }
    public DateTimeOffset? ReminderAt { get; init; }
}

public sealed record CreatePaperRequest
{
    public string Type { get; init; } = "todo";
    public string? Title { get; init; }
    public bool Show { get; init; } = true;
    public string? Content { get; init; }
    public IReadOnlyList<TodoCreateItem>? Todos { get; init; }
}

public sealed record AppendTodosRequest
{
    public string PaperId { get; init; } = "";
    public IReadOnlyList<TodoCreateItem> Todos { get; init; } = [];
}

public sealed record UpdateTodoRequest
{
    public string PaperId { get; init; } = "";
    public string TodoId { get; init; } = "";
    public string? Text { get; init; }
    public bool? Done { get; init; }
    public int? Order { get; init; }
    public bool UpdateLinkedPaper { get; init; }
    public string? LinkedPaperId { get; init; }
}

public sealed record SetTodoReminderRequest
{
    public string PaperId { get; init; } = "";
    public string TodoId { get; init; } = "";
    public DateTimeOffset? ReminderAt { get; init; }
}

public enum NoteWriteMode
{
    FillBlank,
    Append,
    Replace
}

public sealed record WriteNoteRequest
{
    public string PaperId { get; init; } = "";
    public string Content { get; init; } = "";
    public NoteWriteMode Mode { get; init; } = NoteWriteMode.FillBlank;
}

public sealed record DeleteTodoRequest
{
    public string PaperId { get; init; } = "";
    public string TodoId { get; init; } = "";
}

public sealed record PaperMutationResult(
    string PaperId,
    string Type,
    bool Created);

public sealed record AppendTodosResult(
    string PaperId,
    IReadOnlyList<string> TodoIds);

public sealed record TodoMutationResult(
    string PaperId,
    string TodoId);

public sealed record NoteMutationResult(
    string PaperId,
    int ContentLength);

public sealed record DeleteMutationResult(
    string Id,
    bool Deleted);

public interface IPaperTodoHostApi
{
    IReadOnlySet<string> GrantedPermissions { get; }

    IReadOnlyList<PaperSnapshot> ListPapers(string? type = null);
    PaperSnapshot? GetPaper(string paperId);
    IReadOnlyList<TodoSnapshot> ListTodos(
        string? paperId = null,
        bool includeBlank = false);
    NoteSnapshot? GetNote(string paperId);

    PaperMutationResult CreatePaper(CreatePaperRequest request);
    AppendTodosResult AppendTodos(AppendTodosRequest request);
    TodoMutationResult UpdateTodo(UpdateTodoRequest request);
    TodoMutationResult SetTodoReminder(SetTodoReminderRequest request);
    NoteMutationResult WriteNote(WriteNoteRequest request);
    DeleteMutationResult DeleteTodo(DeleteTodoRequest request);
    DeleteMutationResult DeletePaper(string paperId);

    IDisposable Subscribe(
        PaperTodoEventFilter filter,
        Action<PaperTodoEvent> handler);
}

public sealed class PaperTodoPluginException : Exception
{
    public PaperTodoPluginException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
