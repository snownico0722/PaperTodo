from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    target = Path(path)
    text = target.read_text(encoding='utf-8')
    if text.count(old) != 1:
        raise SystemExit(f'{path}: expected one match, got {text.count(old)}')
    target.write_text(text.replace(old, new, 1), encoding='utf-8')


replace_once(
    'src/WebPaperBodySession.cs',
    '    private readonly Action<JsonElement>? _postRuntimeMessage;\n',
    '    private readonly Action<JsonElement>? _postRuntimeMessage;\n'
    '    private readonly bool _paperRuntimeOwnsPresentation;\n')

replace_once(
    'src/WebPaperBodySession.cs',
    '''    public WebPaperBodySession(\n        PaperBodyContext context,\n        PaperBodyPluginManifest manifest,\n        Action<JsonElement>? postRuntimeMessage = null)\n    {\n        _context = context;\n        _manifest = manifest;\n        _postRuntimeMessage = postRuntimeMessage;\n''',
    '''    public WebPaperBodySession(\n        PaperBodyContext context,\n        PaperBodyPluginManifest manifest,\n        Action<JsonElement>? postRuntimeMessage = null,\n        bool paperRuntimeOwnsPresentation = false)\n    {\n        _context = context;\n        _manifest = manifest;\n        _postRuntimeMessage = postRuntimeMessage;\n        _paperRuntimeOwnsPresentation = paperRuntimeOwnsPresentation;\n''')

replace_once(
    'src/WebPaperBodySession.cs',
    '''        _webViewFailed = true;\n        UpdateWebViewPresentation();\n        _context.Paper.SetHeaderText("");\n        _context.Paper.SetCapsulePresentation(null);\n        _context.SetInputClaims(PaperBodyInputClaims.None);\n''',
    '''        _webViewFailed = true;\n        UpdateWebViewPresentation();\n        if (!_paperRuntimeOwnsPresentation)\n        {\n            _context.Paper.SetHeaderText("");\n            _context.Paper.SetCapsulePresentation(null);\n        }\n        _context.SetInputClaims(PaperBodyInputClaims.None);\n''')

replace_once(
    'src/PaperWindow.PluginBodies.cs',
    '''                return new WebPaperBodySession(\n                    context,\n                    descriptor.Manifest,\n                    payload => _controller.PostBodyMessageToWebPaperRuntime(\n                        _paper.Id,\n                        descriptor.Id,\n                        payload));\n''',
    '''                return new WebPaperBodySession(\n                    context,\n                    descriptor.Manifest,\n                    payload => _controller.PostBodyMessageToWebPaperRuntime(\n                        _paper.Id,\n                        descriptor.Id,\n                        payload),\n                    paperRuntimeOwnsPresentation:\n                        (descriptor.RuntimeRequirements &\n                         PaperBodyRuntimeRequirements.BackgroundUpdates) != 0 &&\n                        !string.IsNullOrWhiteSpace(\n                            descriptor.Manifest.PaperRuntimePath));\n''')

print('Web body failure presentation ownership fixed')
