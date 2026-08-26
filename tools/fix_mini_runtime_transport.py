from pathlib import Path

root = Path(__file__).resolve().parents[1]

def read(path):
    return (root / path).read_text(encoding='utf-8')

def write(path, value):
    (root / path).write_text(value, encoding='utf-8', newline='')

def replace_once(path, old, new):
    value = read(path)
    count = value.count(old)
    if count != 1:
        raise RuntimeError(f'{path}: expected one match, got {count}: {old[:100]!r}')
    write(path, value.replace(old, new, 1))

# Mini is a visual surface too. Once persistent state belongs to PaperRuntime it
# still needs the same thin command path as the full Body.
replace_once(
    'src/WebPaperBodySession.Mini.cs',
    '''                  const body = Object.freeze({\n                    markDirty() { post('markDirty'); },\n                    openExternal(url) { post('openExternal', String(url ?? '')); }\n                  });\n                  let miniReady = false;''',
    '''                  const body = Object.freeze({\n                    markDirty() { post('markDirty'); },\n                    openExternal(url) { post('openExternal', String(url ?? '')); }\n                  });\n                  const runtime = Object.freeze({\n                    post(message) { return request('runtime.post', { message: message ?? null }); }\n                  });\n                  let miniReady = false;''')
replace_once(
    'src/WebPaperBodySession.Mini.cs',
    '''                  window.papertodo = Object.freeze({\n                    surface: 'mini', paper, body, mini,\n                    workspace: Object.freeze({ request }),''',
    '''                  window.papertodo = Object.freeze({\n                    surface: 'mini', paper, body, mini, runtime,\n                    workspace: Object.freeze({ request }),''')

path = 'src/WebPaperBodySession.MiniRequests.cs'
write(path, '''using System.Text.Json;\nusing PaperTodo.Plugin;\n\nnamespace PaperTodo;\n\ninternal sealed partial class WebPaperBodySession\n{\n    private object? ExecuteMiniHostRequest(string method, JsonElement parameters)\n    {\n        if (string.Equals(method, "runtime.post", StringComparison.Ordinal))\n        {\n            var message = parameters.ValueKind == JsonValueKind.Object &&\n                          parameters.TryGetProperty("message", out var messageValue)\n                ? messageValue\n                : default;\n            if (_postRuntimeMessage == null ||\n                !_postRuntimeMessage(\n                    message.ValueKind == JsonValueKind.Undefined\n                        ? JsonSerializer.SerializeToElement<object?>(null)\n                        : message.Clone()))\n            {\n                throw new PaperTodoPluginException(\n                    "runtime_unavailable",\n                    "The paper runtime is not ready to accept this message.");\n            }\n            return null;\n        }\n\n        return WebPluginWorkspaceRequests.Execute(_context.Host, method, parameters);\n    }\n}\n''')

print('Mini runtime transport fixed')
