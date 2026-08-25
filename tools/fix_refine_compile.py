from pathlib import Path

path = Path('src/PaperWindow.WebPaperRuntimePresentation.cs')
text = path.read_text(encoding='utf-8')
old = '    private void ClearWebPaperRuntimePresentation(string providerId)\n'
new = '    internal void ClearWebPaperRuntimePresentation(string providerId)\n'
if text.count(old) != 1:
    raise SystemExit(f'expected one ClearWebPaperRuntimePresentation declaration, got {text.count(old)}')
path.write_text(text.replace(old, new, 1), encoding='utf-8')
