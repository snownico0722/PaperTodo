from pathlib import Path


def read(path):
    return Path(path).read_text(encoding='utf-8')


def write(path, text):
    Path(path).write_text(text, encoding='utf-8')


def replace(path, old, new, count=1):
    text = read(path)
    actual = text.count(old)
    if actual != count:
        raise SystemExit(f'{path}: expected {count}, found {actual}: {old[:100]!r}')
    write(path, text.replace(old, new, count))


# Old per-Paper runtime presentation cache is gone; shell state already comes from PaperData and
# future provider Runtime publications use the generic runtime presentation route.
replace(
    'src/PaperWindow.cs',
    '        _controller.ApplyWebPaperRuntimePresentationToWindow(this);\n',
    '')

# Mini state is frontend state and remains writable. Long-lived title/header/capsule presentation
# is provider-Runtime-owned when appRuntime exists, matching Body semantics.
replace(
    'src/WebPaperBodySession.Mini.cs',
'''                    case "saveState":
                        if (!_owner._paperRuntimeOwnsState)
                        {
                            _owner.UpdateStateFromWebSurface(payload, this);
                        }
                        break;
                    case "setTitle":
                        _owner._context.SetTitle(ReadPayloadString(payload));
                        break;
                    case "setHeaderText":
                        _owner._context.Paper.SetHeaderText(ReadPayloadString(payload));
                        break;
                    case "setCapsulePresentation":
                        _owner._context.Paper.SetCapsulePresentation(
                            payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                                ? null
                                : JsonSerializer.Deserialize<PaperCapsulePresentation>(
                                    payload.GetRawText(),
                                    BridgeJsonOptions));
                        break;''',
'''                    case "saveState":
                        _owner.UpdateStateFromWebSurface(payload, this);
                        break;
                    case "setTitle":
                        if (!_owner._runtimeOwnsPresentation)
                        {
                            _owner._context.SetTitle(ReadPayloadString(payload));
                        }
                        break;
                    case "setHeaderText":
                        if (!_owner._runtimeOwnsPresentation)
                        {
                            _owner._context.Paper.SetHeaderText(ReadPayloadString(payload));
                        }
                        break;
                    case "setCapsulePresentation":
                        if (!_owner._runtimeOwnsPresentation)
                        {
                            _owner._context.Paper.SetCapsulePresentation(
                                payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                                    ? null
                                    : JsonSerializer.Deserialize<PaperCapsulePresentation>(
                                        payload.GetRawText(),
                                        BridgeJsonOptions));
                        }
                        break;''')

# Discovery fingerprint follows the one Web Runtime entry; remove the retired extra argument and
# parameter/body completely.
replace(
    'src/PaperBodyPluginRegistry.cs',
'''            manifest.MiniEntryPath,
            manifest.RuntimePath,
            manifest.PaperRuntimePath);''',
'''            manifest.MiniEntryPath,
            manifest.RuntimePath);''')
replace(
    'src/PaperBodyPluginRegistry.cs',
'''        string? miniEntryPath = null,
        string? runtimePath = null,
        string? paperRuntimePath = null)''',
'''        string? miniEntryPath = null,
        string? runtimePath = null)''')
replace(
    'src/PaperBodyPluginRegistry.cs',
'''        if (!string.IsNullOrWhiteSpace(paperRuntimePath))
        {
            var paperRuntime = new FileInfo(paperRuntimePath);
            value += $":{paperRuntime.Length}:{paperRuntime.LastWriteTimeUtc.Ticks}";
        }
''',
'')

replace(
    'src/AppController.cs',
    '        // and emit Body -> PaperRuntime messages during restore.\n',
    '        // and emit Body -> provider Runtime messages during restore.\n')

print('phase2 residual cleanup complete')
