from pathlib import Path

root = Path(__file__).resolve().parents[1]
path = root / 'src/PaperCommandService.cs'
value = path.read_text(encoding='utf-8')
old = '''        TodoRules.ApplyCompletedOrdering(\n            paper.Items,\n            _controller.State.AutoMoveCompletedTodosToBottom);\n        NormalizeOrders(paper);\n        return added;'''
new = '''        if (_controller.State.AutoClearCompletedTodos)\n        {\n            var completedIds = added\n                .Where(item => item.Done)\n                .Select(item => item.Id)\n                .ToArray();\n            if (completedIds.Length > 0)\n            {\n                TodoRules.ApplyCompletionPolicy(\n                    paper.Items,\n                    completedIds,\n                    done: true,\n                    autoClearCompleted: true,\n                    autoMoveCompletedToBottom:\n                        _controller.State.AutoMoveCompletedTodosToBottom);\n                added.RemoveAll(item => item.Done);\n            }\n        }\n        TodoRules.ApplyCompletedOrdering(\n            paper.Items,\n            _controller.State.AutoMoveCompletedTodosToBottom);\n        NormalizeOrders(paper);\n        return added;'''
if value.count(old) != 1:
    raise RuntimeError(f'expected one AddTodoInputs policy block, got {value.count(old)}')
path.write_text(value.replace(old, new, 1), encoding='utf-8', newline='')
print('external Todo completion policy fixed')
