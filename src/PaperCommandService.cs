using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed class PaperCommandException : Exception
{
    public PaperCommandException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

internal sealed class PaperCommandService
{
    private readonly AppController _controller;

    public PaperCommandService(AppController controller)
    {
        _controller = controller;
    }

    public IReadOnlyList<PaperSnapshot> ListPapers(string? type = null)
    {
        EnsureRunning();
        _controller.PrepareExternalPaperOperation();
        if (type != null && type is not PaperTypes.Todo and not PaperTypes.Note)
        {
            throw Error("invalid_params", "type must be 'todo' or 'note'.");
        }

        return _controller.State.Papers
            .Where(paper => type == null || paper.Type == type)
            .Select(_controller.CapturePaperSnapshot)
            .ToArray();
    }

    public PaperSnapshot? GetPaper(string paperId)
    {
        EnsureRunning();
        _controller.PrepareExternalPaperOperation();
        var paper = FindPaper(RequiredId(paperId, "paperId"));
        return paper == null ? null : _controller.CapturePaperSnapshot(paper);
    }

    public IReadOnlyList<TodoSnapshot> ListTodos(
        string? paperId = null,
        bool includeBlank = false)
    {
        EnsureRunning();
        _controller.PrepareExternalPaperOperation();
        var normalizedPaperId = string.IsNullOrWhiteSpace(paperId)
            ? null
            : RequiredId(paperId, "paperId");
        if (normalizedPaperId != null)
        {
            _ = RequirePaper(normalizedPaperId, PaperTypes.Todo);
        }

        return _controller.State.Papers
            .Where(paper =>
                paper.Type == PaperTypes.Todo &&
                (normalizedPaperId == null ||
                 string.Equals(
                     paper.Id,
                     normalizedPaperId,
                     StringComparison.Ordinal)))
            .SelectMany(paper => paper.Items
                .OrderBy(item => item.Order)
                .Where(item => includeBlank || TodoRules.HasMeaningfulContent(item))
                .Select(item => _controller.CaptureTodoSnapshot(paper, item)))
            .ToArray();
    }

    public NoteSnapshot? GetNote(string paperId)
    {
        EnsureRunning();
        _controller.PrepareExternalPaperOperation();
        var paper = FindPaper(RequiredId(paperId, "paperId"));
        if (paper == null)
        {
            return null;
        }
        if (paper.Type != PaperTypes.Note)
        {
            throw Error("wrong_paper_type", "This operation requires a note paper.");
        }
        return _controller.CaptureNoteSnapshot(paper);
    }

    public PaperMutationResult CreatePaper(
        CreatePaperRequest request,
        PaperOperationContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureRunning();
        _controller.PrepareExternalPaperOperation();
        EnsurePaperCapacity();

        var type = request.Type?.Trim().ToLowerInvariant();
        if (type is not PaperTypes.Todo and not PaperTypes.Note)
        {
            throw Error("invalid_params", "type must be 'todo' or 'note'.");
        }
        var title = OptionalText(
            request.Title,
            _controller.State.MaxTitleLength,
            allowEmpty: false,
            "title");
        var content = request.Content ?? "";
        if (content.Length > PaperWindow.NoteTextMaxLength)
        {
            throw Error(
                "content_too_long",
                $"A note cannot exceed {PaperWindow.NoteTextMaxLength} characters.");
        }
        var todoInputs = request.Todos?.ToArray() ?? [];
        if (type == PaperTypes.Note && todoInputs.Length > 0)
        {
            throw Error("invalid_params", "A note paper cannot contain initial todos.");
        }
        if (type == PaperTypes.Todo && !string.IsNullOrEmpty(content))
        {
            throw Error("invalid_params", "A todo paper cannot contain note content.");
        }
        ValidateTodoInputs(todoInputs, allowEmpty: true);

        PaperData paper;
        using (_controller.SuppressPaperPluginEventScans())
        {
            paper = _controller.CreatePaper(type, show: false)
                ?? throw Error("paper_limit", "PaperTodo cannot create another paper.");
            paper.IsVisible = request.Show;
            if (title != null)
            {
                paper.Title = PaperTitles.CleanCustomTitle(
                    title,
                    _controller.State.MaxTitleLength);
            }

            if (type == PaperTypes.Note)
            {
                paper.Content = content;
            }
            else if (todoInputs.Length > 0)
            {
                paper.Items.Clear();
                AddTodoInputs(paper, todoInputs);
            }

            if (!_controller.TryCommitExternalMutation())
            {
                _controller.RollbackExternalCreatedPaper(paper);
                _controller.ResetPaperPluginEventBaseline();
                throw SaveFailed();
            }

            _controller.RunExternalPostCommitUi(
                () => _controller.FinalizeExternalPaperCreated(
                    paper,
                    request.Show));
        }

        _controller.PublishExternalPaperOperation(context);
        return new PaperMutationResult(paper.Id, paper.Type, Created: true);
    }

    public AppendTodosResult AppendTodos(
        AppendTodosRequest request,
        PaperOperationContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureRunning();
        _controller.PrepareExternalPaperOperation();
        var paper = RequirePaper(
            RequiredId(request.PaperId, "paperId"),
            PaperTypes.Todo);
        var inputs = request.Todos?.ToArray() ?? [];
        ValidateTodoInputs(inputs, allowEmpty: false);

        var snapshot = TodoPaperSnapshot.Capture(paper);
        var addedIds = new List<string>(inputs.Length);
        using (_controller.SuppressPaperPluginEventScans())
        {
            if (IsBlankPlaceholderPaper(paper))
            {
                paper.Items.Clear();
            }
            foreach (var item in AddTodoInputs(paper, inputs))
            {
                addedIds.Add(item.Id);
            }

            if (!_controller.TryCommitExternalMutation())
            {
                snapshot.Restore(paper);
                _controller.ResetPaperPluginEventBaseline();
                throw SaveFailed();
            }

            _controller.RunExternalPostCommitUi(
                () => _controller.RefreshExternalTodoPaper(paper));
        }

        _controller.PublishExternalPaperOperation(context);
        return new AppendTodosResult(paper.Id, addedIds);
    }

    public TodoMutationResult UpdateTodo(
        UpdateTodoRequest request,
        PaperOperationContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureRunning();
        _controller.PrepareExternalPaperOperation();
        if (request.Text == null &&
            !request.Done.HasValue &&
            !request.Order.HasValue &&
            !request.UpdateLinkedPaper)
        {
            throw Error(
                "invalid_params",
                "Provide text, done, order and/or updateLinkedPaper.");
        }

        var paper = RequirePaper(
            RequiredId(request.PaperId, "paperId"),
            PaperTypes.Todo);
        var item = RequireTodo(
            paper,
            RequiredId(request.TodoId, "todoId"));
        var wasDone = item.Done;
        var text = request.Text == null
            ? null
            : RequiredText(
                request.Text,
                PaperWindow.TodoTextMaxLength,
                allowEmpty: true,
                "text");
        var linkedPaperId = NormalizeLinkedPaperUpdate(request);
        var snapshot = TodoPaperSnapshot.Capture(paper);

        using (_controller.SuppressPaperPluginEventScans())
        {
            if (text != null)
            {
                item.Text = text;
                if (item.ReminderTriggered)
                {
                    item.ReminderAt = null;
                    item.ReminderTriggered = false;
                }
            }
            if (request.Done.HasValue)
            {
                item.Done = request.Done.Value;
                if (item.Done)
                {
                    item.ReminderAt = null;
                    item.ReminderTriggered = false;
                }
            }
            if (request.UpdateLinkedPaper)
            {
                item.LinkPaper(linkedPaperId);
            }
            if (request.Order.HasValue)
            {
                MoveTodo(paper, item, request.Order.Value);
            }
            var doneChanged = request.Done.HasValue && item.Done != wasDone;
            if (doneChanged && item.Done && _controller.State.AutoClearCompletedTodos)
            {
                TodoRules.ApplyCompletionPolicy(
                    paper.Items,
                    [item.Id],
                    done: true,
                    autoClearCompleted: true,
                    autoMoveCompletedToBottom:
                        _controller.State.AutoMoveCompletedTodosToBottom);
            }
            else if (doneChanged)
            {
                TodoRules.ApplyDoneTransitionOrdering(
                    paper.Items,
                    [item.Id],
                    item.Done,
                    _controller.State.AutoMoveCompletedTodosToBottom);
            }
            else
            {
                TodoRules.ApplyCompletedOrdering(
                    paper.Items,
                    _controller.State.AutoMoveCompletedTodosToBottom);
            }
            NormalizeOrders(paper);

            if (!_controller.TryCommitExternalMutation())
            {
                snapshot.Restore(paper);
                _controller.ResetPaperPluginEventBaseline();
                throw SaveFailed();
            }

            _controller.RunExternalPostCommitUi(
                () => _controller.RefreshExternalTodoPaper(paper));
        }

        _controller.PublishExternalPaperOperation(context);
        return new TodoMutationResult(paper.Id, item.Id);
    }

    public TodoMutationResult SetTodoReminder(
        SetTodoReminderRequest request,
        PaperOperationContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureRunning();
        _controller.PrepareExternalPaperOperation();
        if (!_controller.State.ExperimentalTodoReminders)
        {
            throw Error(
                "reminders_disabled",
                "Todo reminders are disabled in PaperTodo Labs.");
        }

        var paper = RequirePaper(
            RequiredId(request.PaperId, "paperId"),
            PaperTypes.Todo);
        var item = RequireTodo(
            paper,
            RequiredId(request.TodoId, "todoId"));
        if (item.Done)
        {
            throw Error(
                "todo_completed",
                "A reminder cannot be set on a completed todo.");
        }
        ValidateReminder(request.ReminderAt);

        var snapshot = TodoPaperSnapshot.Capture(paper);
        using (_controller.SuppressPaperPluginEventScans())
        {
            item.ReminderAt = request.ReminderAt;
            item.ReminderTriggered = false;
            if (!_controller.TryCommitExternalMutation())
            {
                snapshot.Restore(paper);
                _controller.ResetPaperPluginEventBaseline();
                throw SaveFailed();
            }

            _controller.RunExternalPostCommitUi(
                () => _controller.RefreshExternalTodoPaper(paper));
        }

        _controller.PublishExternalPaperOperation(context);
        return new TodoMutationResult(paper.Id, item.Id);
    }

    public NoteMutationResult WriteNote(
        WriteNoteRequest request,
        PaperOperationContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureRunning();
        _controller.PrepareExternalPaperOperation();
        var paper = RequirePaper(
            RequiredId(request.PaperId, "paperId"),
            PaperTypes.Note);
        if (!string.Equals(
                paper.BodyProviderId,
                PaperBodyProviderIds.Markdown,
                StringComparison.Ordinal))
        {
            throw Error(
                "note_body_not_markdown",
                "Writing note content only applies to the built-in Markdown body.");
        }
        var content = request.Content ?? "";
        if (content.Length > PaperWindow.NoteTextMaxLength)
        {
            throw Error(
                "content_too_long",
                $"A note cannot exceed {PaperWindow.NoteTextMaxLength} characters.");
        }

        var original = paper.Content ?? "";
        var result = request.Mode switch
        {
            NoteWriteMode.FillBlank when original.Length == 0 => content,
            NoteWriteMode.FillBlank => throw Error(
                "note_not_blank",
                "fillBlank can only write to an empty note."),
            NoteWriteMode.Append => AppendNoteText(original, content),
            NoteWriteMode.Replace => content,
            _ => throw Error("invalid_params", "Unknown note write mode.")
        };
        if (result.Length > PaperWindow.NoteTextMaxLength)
        {
            throw Error(
                "content_too_long",
                $"A note cannot exceed {PaperWindow.NoteTextMaxLength} characters.");
        }

        using (_controller.SuppressPaperPluginEventScans())
        {
            paper.Content = result;
            if (!_controller.TryCommitExternalMutation())
            {
                paper.Content = original;
                _controller.ResetPaperPluginEventBaseline();
                throw SaveFailed();
            }

            _controller.RunExternalPostCommitUi(
                () => _controller.RefreshExternalNotePaper(paper));
        }

        _controller.PublishExternalPaperOperation(context);
        return new NoteMutationResult(paper.Id, result.Length);
    }

    public DeleteMutationResult DeleteTodo(
        DeleteTodoRequest request,
        PaperOperationContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureRunning();
        _controller.PrepareExternalPaperOperation();
        var paper = RequirePaper(
            RequiredId(request.PaperId, "paperId"),
            PaperTypes.Todo);
        var item = RequireTodo(
            paper,
            RequiredId(request.TodoId, "todoId"));
        var snapshot = TodoPaperSnapshot.Capture(paper);

        using (_controller.SuppressPaperPluginEventScans())
        {
            paper.Items.Remove(item);
            if (paper.Items.Count == 0)
            {
                paper.Items.Add(new PaperItem());
            }
            NormalizeOrders(paper);

            if (!_controller.TryCommitExternalMutation())
            {
                snapshot.Restore(paper);
                _controller.ResetPaperPluginEventBaseline();
                throw SaveFailed();
            }

            _controller.RunExternalPostCommitUi(
                () => _controller.RefreshExternalTodoPaper(paper));
        }

        _controller.PublishExternalPaperOperation(context);
        return new DeleteMutationResult(item.Id, Deleted: true);
    }

    public DeleteMutationResult DeletePaper(
        string paperId,
        PaperOperationContext context)
    {
        EnsureRunning();
        _controller.PrepareExternalPaperOperation();
        var paper = RequirePaper(RequiredId(paperId, "paperId"));
        var papers = _controller.State.Papers;
        var originalIndex = papers.IndexOf(paper);
        var affectedLinks = papers
            .Where(candidate => candidate.Type == PaperTypes.Todo)
            .SelectMany(candidate => candidate.Items)
            .Where(item => string.Equals(
                item.LinkedPaperId,
                paper.Id,
                StringComparison.Ordinal))
            .Select(item => (Item: item, Link: item.LinkedPaperId))
            .ToList();
        PaperData? replacement = null;

        using (_controller.SuppressPaperPluginEventScans())
        {
            papers.RemoveAt(originalIndex);
            foreach (var (item, _) in affectedLinks)
            {
                item.ClearQuickLaunch();
            }

            if (papers.Count == 0)
            {
                try
                {
                    replacement = _controller.CreatePaper(
                        PaperTypes.Todo,
                        show: false);
                }
                catch
                {
                    RestoreDeletedPaper(
                        papers,
                        originalIndex,
                        paper,
                        affectedLinks,
                        replacement);
                    _controller.ResetPaperPluginEventBaseline();
                    throw;
                }
                if (replacement == null)
                {
                    RestoreDeletedPaper(
                        papers,
                        originalIndex,
                        paper,
                        affectedLinks,
                        replacement);
                    _controller.ResetPaperPluginEventBaseline();
                    throw Error(
                        "paper_limit",
                        "PaperTodo could not create the required replacement paper.");
                }
                replacement.IsVisible = true;
            }

            if (!_controller.TryCommitExternalMutation())
            {
                RestoreDeletedPaper(
                    papers,
                    originalIndex,
                    paper,
                    affectedLinks,
                    replacement);
                _controller.RefreshAfterExternalRollback();
                _controller.ResetPaperPluginEventBaseline();
                throw SaveFailed();
            }

            _controller.QueuePluginPaperStateDeletion(paper.Id);
            _controller.TryFlushPendingPluginPaperStateDeletes();
            _controller.RunExternalPostCommitUi(
                () => _controller.FinalizeExternalPaperDeletion(
                    paper,
                    replacement,
                    affectedLinks.Count > 0));
        }

        _controller.PublishExternalPaperOperation(context);
        return new DeleteMutationResult(paper.Id, Deleted: true);
    }

    private void RestoreDeletedPaper(
        IList<PaperData> papers,
        int originalIndex,
        PaperData paper,
        IEnumerable<(PaperItem Item, string? Link)> affectedLinks,
        PaperData? replacement)
    {
        if (!papers.Contains(paper))
        {
            papers.Insert(Math.Clamp(originalIndex, 0, papers.Count), paper);
        }
        foreach (var (item, link) in affectedLinks)
        {
            item.LinkPaper(link);
        }
        if (replacement != null)
        {
            _controller.RollbackExternalCreatedPaper(replacement);
        }
    }

    private IReadOnlyList<PaperItem> AddTodoInputs(
        PaperData paper,
        IReadOnlyList<TodoCreateItem> inputs)
    {
        var added = new List<PaperItem>(inputs.Count);
        foreach (var input in inputs)
        {
            var linkedPaperId = NormalizeLinkedPaper(input.LinkedPaperId, paper.Id);
            var item = new PaperItem
            {
                Text = input.Text,
                Done = input.Done,
                Order = paper.Items.Count,
                ReminderAt = input.Done ? null : input.ReminderAt
            };
            item.LinkPaper(linkedPaperId);
            paper.Items.Add(item);
            added.Add(item);
        }
        if (_controller.State.AutoClearCompletedTodos)
        {
            var completedIds = added
                .Where(item => item.Done)
                .Select(item => item.Id)
                .ToArray();
            if (completedIds.Length > 0)
            {
                TodoRules.ApplyCompletionPolicy(
                    paper.Items,
                    completedIds,
                    done: true,
                    autoClearCompleted: true,
                    autoMoveCompletedToBottom:
                        _controller.State.AutoMoveCompletedTodosToBottom);
                added.RemoveAll(item => item.Done);
            }
        }
        TodoRules.ApplyCompletedOrdering(
            paper.Items,
            _controller.State.AutoMoveCompletedTodosToBottom);
        NormalizeOrders(paper);
        return added;
    }

    private void ValidateTodoInputs(
        IReadOnlyList<TodoCreateItem> inputs,
        bool allowEmpty)
    {
        if (!allowEmpty && inputs.Count == 0)
        {
            throw Error("invalid_params", "todos cannot be empty.");
        }
        if (inputs.Count > PaperWindow.MaxPastedTodoLines)
        {
            throw Error(
                "invalid_params",
                $"todos cannot exceed {PaperWindow.MaxPastedTodoLines} items.");
        }

        foreach (var input in inputs)
        {
            _ = RequiredText(
                input.Text,
                PaperWindow.TodoTextMaxLength,
                allowEmpty: false,
                "todo.text");
            if (input.Done && input.ReminderAt.HasValue)
            {
                throw Error(
                    "invalid_params",
                    "A completed todo cannot start with a reminder.");
            }
            ValidateReminder(input.ReminderAt);
            _ = NormalizeLinkedPaper(input.LinkedPaperId);
        }
    }

    private string? NormalizeLinkedPaperUpdate(UpdateTodoRequest request) =>
        request.UpdateLinkedPaper
            ? NormalizeLinkedPaper(request.LinkedPaperId, request.PaperId)
            : null;

    private string? NormalizeLinkedPaper(
        string? value,
        string? sourcePaperId = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!_controller.State.EnableTodoPaperLinks)
        {
            throw Error(
                "paper_links_disabled",
                "Todo-paper links are disabled in PaperTodo Settings.");
        }
        var id = RequiredId(value, "linkedPaperId");
        if (!string.IsNullOrWhiteSpace(sourcePaperId) &&
            string.Equals(id, sourcePaperId.Trim(), StringComparison.Ordinal))
        {
            throw Error(
                "self_link_not_allowed",
                "A todo item cannot link to its own paper.");
        }
        if (_controller.State.Papers.All(paper =>
                !string.Equals(paper.Id, id, StringComparison.Ordinal)))
        {
            throw Error("paper_not_found", "The linked paper does not exist.");
        }
        return id;
    }

    private void ValidateReminder(DateTimeOffset? reminderAt)
    {
        if (!reminderAt.HasValue)
        {
            return;
        }
        if (!_controller.State.ExperimentalTodoReminders)
        {
            throw Error(
                "reminders_disabled",
                "Todo reminders are disabled in PaperTodo Labs.");
        }
        if (reminderAt.Value <= DateTimeOffset.Now)
        {
            throw Error(
                "invalid_params",
                "reminderAt must be in the future.");
        }
    }

    private static string AppendNoteText(string original, string content)
    {
        var separator =
            original.Length > 0 &&
            content.Length > 0 &&
            !original.EndsWith('\n')
                ? Environment.NewLine
                : "";
        return original + separator + content;
    }

    private static void MoveTodo(
        PaperData paper,
        PaperItem item,
        int requestedOrder)
    {
        var ordered = paper.Items
            .OrderBy(candidate => candidate.Order)
            .ToList();
        if (!ordered.Remove(item))
        {
            return;
        }
        var target = Math.Clamp(requestedOrder, 0, ordered.Count);
        ordered.Insert(target, item);
        paper.Items = ordered;
    }

    private static bool IsBlankPlaceholderPaper(PaperData paper) =>
        paper.Items.Count == 1 && TodoRules.IsPlaceholder(paper.Items[0]);

    private PaperData? FindPaper(string paperId) =>
        _controller.State.Papers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, paperId, StringComparison.Ordinal));

    private PaperData RequirePaper(
        string paperId,
        string? expectedType = null)
    {
        var paper = FindPaper(paperId)
            ?? throw Error("paper_not_found", "The requested paper does not exist.");
        if (expectedType != null && paper.Type != expectedType)
        {
            throw Error(
                "wrong_paper_type",
                $"This operation requires a {expectedType} paper.");
        }
        return paper;
    }

    private static PaperItem RequireTodo(PaperData paper, string todoId) =>
        paper.Items.FirstOrDefault(item =>
            string.Equals(item.Id, todoId, StringComparison.Ordinal))
        ?? throw Error("todo_not_found", "The requested todo does not exist.");

    private void EnsurePaperCapacity()
    {
        if (_controller.State.Papers.Count >= 100)
        {
            throw Error(
                "paper_limit",
                "PaperTodo supports at most 100 papers.");
        }
    }

    private void EnsureRunning()
    {
        if (!_controller.IsRunning)
        {
            throw Error("app_exiting", "PaperTodo is exiting.");
        }
    }

    private static string RequiredId(string? value, string name)
    {
        var normalized = value?.Trim() ?? "";
        if (normalized.Length is 0 or > 64)
        {
            throw Error("invalid_params", $"{name} is invalid.");
        }
        return normalized;
    }

    private static string RequiredText(
        string? value,
        int maxLength,
        bool allowEmpty,
        string name)
    {
        var text = value ?? "";
        if (!allowEmpty && string.IsNullOrWhiteSpace(text))
        {
            throw Error("invalid_params", $"{name} cannot be empty.");
        }
        if (text.Length > maxLength)
        {
            throw Error(
                "invalid_params",
                $"{name} cannot exceed {maxLength} characters.");
        }
        return text;
    }

    private static string? OptionalText(
        string? value,
        int maxLength,
        bool allowEmpty,
        string name) =>
        value == null
            ? null
            : RequiredText(value, maxLength, allowEmpty, name);

    private static void NormalizeOrders(PaperData paper) =>
        TodoRules.NormalizeOrders(paper.Items);

    private static PaperCommandException SaveFailed() =>
        Error(
            "save_failed",
            "PaperTodo could not save the change. The in-memory change was rolled back.");

    private static PaperCommandException Error(string code, string message) =>
        new(code, message);

    private sealed class TodoPaperSnapshot
    {
        private readonly List<PaperItemSnapshot> _items;

        private TodoPaperSnapshot(List<PaperItemSnapshot> items)
        {
            _items = items;
        }

        public static TodoPaperSnapshot Capture(PaperData paper) =>
            new(paper.Items.Select(PaperItemSnapshot.Capture).ToList());

        public void Restore(PaperData paper)
        {
            paper.Items.Clear();
            foreach (var snapshot in _items)
            {
                snapshot.Restore();
                paper.Items.Add(snapshot.Item);
            }
        }
    }

    private sealed record PaperItemSnapshot(
        PaperItem Item,
        string Text,
        bool Done,
        int Order,
        string? LinkedPaperId,
        string? LinkedPath,
        DateTimeOffset? ReminderAt,
        bool ReminderTriggered)
    {
        public static PaperItemSnapshot Capture(PaperItem item) =>
            new(
                item,
                item.Text,
                item.Done,
                item.Order,
                item.LinkedPaperId,
                item.LinkedPath,
                item.ReminderAt,
                item.ReminderTriggered);

        public void Restore()
        {
            Item.Text = Text;
            Item.Done = Done;
            Item.Order = Order;
            Item.RestoreQuickLaunch(LinkedPaperId, LinkedPath);
            Item.ReminderAt = ReminderAt;
            Item.ReminderTriggered = ReminderTriggered;
        }
    }
}
