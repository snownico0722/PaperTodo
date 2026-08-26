from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{path}: expected exactly one match, found {count}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


# 1) Preserve the original completion-transition ordering semantics while keeping
# ApplyCompletedOrdering as the invariant repair used by insertion paths.
replace_once(
    "src/TodoRules.cs",
    '''    public static bool ApplyCompletionPolicy(\n        List<PaperItem> items,\n        IReadOnlyCollection<string> changedItemIds,\n        bool done,\n        bool autoClearCompleted,\n        bool autoMoveCompletedToBottom)\n    {\n        if (changedItemIds.Count == 0)\n        {\n            return false;\n        }\n\n        if (done && autoClearCompleted)\n        {\n            var changed = changedItemIds.ToHashSet(StringComparer.Ordinal);\n            items.RemoveAll(item => changed.Contains(item.Id) && item.Done);\n            if (items.Count == 0)\n            {\n                items.Add(new PaperItem());\n            }\n            NormalizeOrders(items);\n            return true;\n        }\n\n        return ApplyCompletedOrdering(items, autoMoveCompletedToBottom);\n    }\n''',
    '''    public static bool ApplyDoneTransitionOrdering(\n        List<PaperItem> items,\n        IReadOnlyCollection<string> changedItemIds,\n        bool done,\n        bool enabled)\n    {\n        if (!enabled || changedItemIds.Count == 0)\n        {\n            return false;\n        }\n\n        var changed = changedItemIds.ToHashSet(StringComparer.Ordinal);\n        var before = items.OrderBy(item => item.Order).ToList();\n        var moved = before\n            .Where(item => changed.Contains(item.Id))\n            .ToList();\n        if (moved.Count == 0)\n        {\n            return false;\n        }\n\n        var remaining = before\n            .Where(item => !changed.Contains(item.Id))\n            .ToList();\n        var insertIndex = done\n            ? remaining.Count\n            : remaining.FindIndex(item => item.Done);\n        if (insertIndex < 0)\n        {\n            insertIndex = remaining.Count;\n        }\n        remaining.InsertRange(insertIndex, moved);\n\n        if (before.Select(item => item.Id)\n            .SequenceEqual(remaining.Select(item => item.Id)))\n        {\n            return false;\n        }\n\n        items.Clear();\n        items.AddRange(remaining);\n        NormalizeOrders(items);\n        return true;\n    }\n\n    public static bool ApplyCompletionPolicy(\n        List<PaperItem> items,\n        IReadOnlyCollection<string> changedItemIds,\n        bool done,\n        bool autoClearCompleted,\n        bool autoMoveCompletedToBottom)\n    {\n        if (changedItemIds.Count == 0)\n        {\n            return false;\n        }\n\n        if (done && autoClearCompleted)\n        {\n            var changed = changedItemIds.ToHashSet(StringComparer.Ordinal);\n            items.RemoveAll(item => changed.Contains(item.Id) && item.Done);\n            if (items.Count == 0)\n            {\n                items.Add(new PaperItem());\n            }\n            NormalizeOrders(items);\n            return true;\n        }\n\n        return ApplyDoneTransitionOrdering(\n            items,\n            changedItemIds,\n            done,\n            autoMoveCompletedToBottom);\n    }\n''')

replace_once(
    "src/PaperWindow.TodoLinksAndSelection.cs",
    '''    private bool MoveTodoItemsAfterDoneChange(\n        IReadOnlyCollection<PaperItem> changedItems,\n        bool done)\n    {\n        _ = changedItems;\n        _ = done;\n        return TodoRules.ApplyCompletedOrdering(\n            _paper.Items,\n            _controller.State.AutoMoveCompletedTodosToBottom);\n    }\n''',
    '''    private bool MoveTodoItemsAfterDoneChange(\n        IReadOnlyCollection<PaperItem> changedItems,\n        bool done)\n    {\n        return TodoRules.ApplyDoneTransitionOrdering(\n            _paper.Items,\n            changedItems.Select(item => item.Id).ToArray(),\n            done,\n            _controller.State.AutoMoveCompletedTodosToBottom);\n    }\n''')

# 2) External todo updates must only auto-clear on an actual false -> true transition.
replace_once(
    "src/PaperCommandService.cs",
    '''        var item = RequireTodo(\n            paper,\n            RequiredId(request.TodoId, "todoId"));\n        var text = request.Text == null\n''',
    '''        var item = RequireTodo(\n            paper,\n            RequiredId(request.TodoId, "todoId"));\n        var wasDone = item.Done;\n        var text = request.Text == null\n''')

replace_once(
    "src/PaperCommandService.cs",
    '''            if (request.Done == true && _controller.State.AutoClearCompletedTodos)\n            {\n                TodoRules.ApplyCompletionPolicy(\n                    paper.Items,\n                    [item.Id],\n                    done: true,\n                    autoClearCompleted: true,\n                    autoMoveCompletedToBottom:\n                        _controller.State.AutoMoveCompletedTodosToBottom);\n            }\n            else\n            {\n                TodoRules.ApplyCompletedOrdering(\n                    paper.Items,\n                    _controller.State.AutoMoveCompletedTodosToBottom);\n            }\n''',
    '''            var doneChanged = request.Done.HasValue && item.Done != wasDone;\n            if (doneChanged && item.Done && _controller.State.AutoClearCompletedTodos)\n            {\n                TodoRules.ApplyCompletionPolicy(\n                    paper.Items,\n                    [item.Id],\n                    done: true,\n                    autoClearCompleted: true,\n                    autoMoveCompletedToBottom:\n                        _controller.State.AutoMoveCompletedTodosToBottom);\n            }\n            else if (doneChanged)\n            {\n                TodoRules.ApplyDoneTransitionOrdering(\n                    paper.Items,\n                    [item.Id],\n                    item.Done,\n                    _controller.State.AutoMoveCompletedTodosToBottom);\n            }\n            else\n            {\n                TodoRules.ApplyCompletedOrdering(\n                    paper.Items,\n                    _controller.State.AutoMoveCompletedTodosToBottom);\n            }\n''')

# 3) MCP must report a successful auto-clear as success instead of re-reading a deleted todo.
replace_once(
    "src/McpCommandService.cs",
    '''        _commands.UpdateTodo(\n            new UpdateTodoRequest\n            {\n                PaperId = paperId,\n                TodoId = todoId,\n                Text = hasText ? text : null,\n                Done = done,\n                Order = order,\n                UpdateLinkedPaper = hasLinkedPaper,\n                LinkedPaperId = linkedPaperId\n            },\n            PaperOperationContext.Mcp());\n        return TodoDetails(RequireTodo(paperId, todoId));\n''',
    '''        _commands.UpdateTodo(\n            new UpdateTodoRequest\n            {\n                PaperId = paperId,\n                TodoId = todoId,\n                Text = hasText ? text : null,\n                Done = done,\n                Order = order,\n                UpdateLinkedPaper = hasLinkedPaper,\n                LinkedPaperId = linkedPaperId\n            },\n            PaperOperationContext.Mcp());\n\n        if (done == true &&\n            !before.Done &&\n            _controller.State.AutoClearCompletedTodos)\n        {\n            return new\n            {\n                paper_id = paperId,\n                todo_id = todoId,\n                deleted = true\n            };\n        }\n\n        return TodoDetails(RequireTodo(paperId, todoId));\n''')

# Remove two mechanical empty blocks left by legacy-state deletion.
replace_once(
    "src/AppController.cs",
    '''        var changed = false;\n        foreach (var staleKey in State.CapsuleCollapseAllActiveQueues.Keys.Where(key => !live.Contains(key)).ToList())\n''',
    '''        foreach (var staleKey in State.CapsuleCollapseAllActiveQueues.Keys.Where(key => !live.Contains(key)).ToList())\n''')
replace_once(
    "src/AppController.cs",
    '''                State.CapsuleCollapseAllActiveQueues[fallbackKey] = true;\n                changed = true;\n                continue;\n''',
    '''                State.CapsuleCollapseAllActiveQueues[fallbackKey] = true;\n                continue;\n''')
replace_once(
    "src/AppController.cs",
    '''            State.CapsuleCollapseAllActiveQueues.Remove(staleKey);\n            changed = true;\n        }\n        if (changed)\n        {\n            }\n''',
    '''            State.CapsuleCollapseAllActiveQueues.Remove(staleKey);\n        }\n''')
replace_once(
    "src/AppController.cs",
    '''    // Reset ALL deep-capsule start heights to the default — both the legacy global scalar AND the\n    // per-queue dictionary. Callers use this when the feature is disabled/reset (and then\n    // persist them to data.json). Single chokepoint so no reset path forgets the dict again.\n''',
    '''    // Reset all per-queue deep-capsule start heights to the built-in default by removing\n    // overrides. Single chokepoint so no reset path forgets the dictionary.\n''')
replace_once(
    "src/StateStore.cs",
    '''        if (!state.UseCapsuleCollapseAll)\n        {\n        }\n        state.CapsuleCollapseAllActiveQueues ??= new Dictionary<string, bool>();\n''',
    '''        state.CapsuleCollapseAllActiveQueues ??= new Dictionary<string, bool>();\n''')

print("PR #153 review fixes applied")
