from pathlib import Path
import subprocess

BASE = '13a2224c549fcc7d6fbbd5c58a9b666d9f75086f'
TEXT = 'b88322feb212c4ca5be1da2d900e35794666dc37'
OLD = '07b7f3ab50fb8461e2d89ec112d1f1572ed256fc'

def source(ref, path):
    return subprocess.check_output(['git', 'show', f'{ref}:{path}']).decode('utf-8-sig').replace('\r\n', '\n')

def read(path):
    return Path(path).read_text(encoding='utf-8-sig')

def write(path, value):
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_text(value, encoding='utf-8')

def once(text, old, new):
    assert text.count(old) == 1, (old, text.count(old))
    return text.replace(old, new)

def method(text, signature):
    start = text.index(signature)
    brace = text.index('{', start)
    depth = 1
    end = brace + 1
    while depth:
        if text[end] == '{': depth += 1
        elif text[end] == '}': depth -= 1
        end += 1
    return text[start:end]

for name in ['EdgeCapsulePreview.Markdown.cs','EdgeCapsulePreview.Markdown.Inlines.cs','EdgeCapsulePreview.Markdown.TextLayout.cs']:
    write('src/' + name, source(TEXT, 'src/' + name))

path = 'src/EdgeCapsulePreview.Markdown.cs'
s = read(path)
base = source(BASE, path)
# Keep the existing main sizing contract, not PR243's synchronous WPF row measurement.
signature = '    public EdgeCapsulePreviewDescriptor Describe(EdgeCapsulePreviewContext context)'
s = once(s, method(s, signature), method(base, signature))
estimate = method(base, '    public static int EstimateVisualLines(')
estimate = once(estimate, 'PrepareInlineTextForMeasurement(StripBlockPrefix(trimmed))',
                'PrepareInlineTextForMeasurement(StripBlockPrefix(trimmed), content.Inlines)')
s = once(s, '    public static bool RenderInto(', estimate + '\n\n    public static bool RenderInto(')
# Retain PR244's body publication and ordered-marker fixes.
s = once(s, '        ClipToBounds = true;\n        _body = body;',
         '        ClipToBounds = true;\n        Opacity = 0;\n        IsHitTestVisible = false;\n        _body = body;')
s = once(s, '            _sourceTruncated = truncated;\n',
         '            _sourceTruncated = truncated;\n            Opacity = 1;\n            IsHitTestVisible = true;\n')
s = once(s, '            return ordered.Groups[2].Value;',
         '            return $"{ordered.Groups[1].Value}. {ordered.Groups[2].Value}";')
# Inline recognition is now owned by the existing Markdig semantic snapshot.
start = s.index('    private static readonly Regex InlinePattern')
end = s.index('    private static readonly Regex HeadingPattern', start)
s = s[:start] + s[end:]
write(path, s)

path = 'src/EdgeCapsulePreview.Markdown.Inlines.cs'
s = read(path)
old = method(s, '    internal static IEnumerable<InlinePiece> InlinePieces(')
new = '''    internal static IEnumerable<InlinePiece> InlinePieces(
        string text, string mode, InlineStyle style = InlineStyle.None, Uri? link = null, int depth = 0)
    {
        foreach (var piece in SemanticInlinePieces(text, mode))
            yield return piece with { Style = piece.Style | style, Link = piece.Link ?? link };
    }'''
s = once(s, old, new)
s = once(s, 'Weak = 16, Syntax = 32', 'Weak = 16, Syntax = 32, Underline = 64')
s = once(s, '            (activeLink?.Inlines ?? target).Add(inline);',
         '            if (Has(InlineStyle.Underline)) inline = new Span(inline) { TextDecorations = TextDecorations.Underline };\n            (activeLink?.Inlines ?? target).Add(inline);')
s = s.replace('The single bounded inline grammar shared by measurement, TextBlock publication and',
              "The note's shared semantic recognizer feeds measurement, TextBlock publication and")
write(path, s)
path = 'src/EdgeCapsulePreview.Markdown.TextLayout.cs'
s = once(read(path), '            if (link) foreach (var item in System.Windows.TextDecorations.Underline)',
         '            if (link || Has(MarkdownEdgeCapsulePreviewRenderer.InlineStyle.Underline)) foreach (var item in System.Windows.TextDecorations.Underline)')
write(path, s)
write('src/EdgeCapsulePreview.Markdown.SemanticInlines.cs', read('.github/preview-experiment/SemanticInlines.cs.txt'))
path = 'src/MarkdownImageReferences.cs'
write(path, once(read(path), '    private static bool TrySplitMarkdownImage(', '    internal static bool TrySplitMarkdownImage('))
path = 'src/PaperWindow.EdgeCapsulePreview.cs'
write(path, once(read(path), 'return AvalonEditEdgeCapsulePreviewProvider.Instance;', 'return MarkdownEdgeCapsulePreviewProvider.Instance;'))

# Reuse the prepared-renderer behavioral regressions. The one exact-Describe-height test belongs
# to the rejected synchronous sizing change, so the existing main sizing checks remain its oracle.
folder = 'tests/PaperTodo.MarkdownEditingChecks/'
for name in ['EdgePreviewChecks.cs', 'EdgePreviewInlinePreparationChecks.cs', 'EdgePreviewTextLayoutChecks.cs']:
    write(folder + name, source(TEXT, folder + name))
path = folder + 'EdgePreviewTextLayoutChecks.cs'
s = read(path)
start = s.index('        check("Sixteen admitted rows reserve their actual height')
end = s.index('        check("Value inline runs retain', start)
s = s[:start] + '        // Describe retains main\'s bounded lightweight estimate; renderer checks follow.\n' + s[end:]
write(path, s)
path = folder + 'EdgePreviewChecks.cs'
s = read(path)
# Restore the stronger main assertion: budget-external text does not change requested geometry.
start = s.index('                Equal(Describe(prefix).Size.WidthDip, Describe(extra).Size.WidthDip,')
end = s.index('                Require(MarkdownEdgeCapsulePreviewRenderer.RenderInto(panel, extra', start)
s = s[:start] + '                Equal(Describe(prefix).Size, Describe(extra).Size, "budget-external text cannot change card geometry");\n' + s[end:]
write(path, s)
path = 'tests/PaperTodo.EdgePreviewExperimentChecks/Program.cs'
s = once(read(path), '            else Checks();', '            else { SharedPreviewSemanticChecks.Run(); Checks(); }')
write(path, s)
write('tests/PaperTodo.EdgePreviewExperimentChecks/SharedSemanticChecks.cs', read('.github/preview-experiment/SharedSemanticChecks.cs.txt'))

# Identical pinned profile harness across controls; no new parser instantiated in old controls.
for folder in ['../main-control', '../pr243-control', '../avalon-control']:
    write(folder + '/src/EdgeCapsulePreview.AvalonEdit.cs', source(OLD, 'src/EdgeCapsulePreview.AvalonEdit.cs'))
    for name in ['Program.cs', 'PaperTodo.EdgePreviewExperimentChecks.csproj']:
        p = 'tests/PaperTodo.EdgePreviewExperimentChecks/' + name
        write(folder + '/' + p, source(OLD, p))

paths = [
    'src/EdgeCapsulePreview.Markdown.cs', 'src/EdgeCapsulePreview.Markdown.Inlines.cs',
    'src/EdgeCapsulePreview.Markdown.TextLayout.cs', 'src/EdgeCapsulePreview.Markdown.SemanticInlines.cs',
    'src/MarkdownImageReferences.cs', 'src/PaperWindow.EdgeCapsulePreview.cs',
    folder + 'EdgePreviewChecks.cs' if False else 'tests/PaperTodo.MarkdownEditingChecks/EdgePreviewChecks.cs',
    'tests/PaperTodo.MarkdownEditingChecks/EdgePreviewInlinePreparationChecks.cs',
    'tests/PaperTodo.MarkdownEditingChecks/EdgePreviewTextLayoutChecks.cs',
    'tests/PaperTodo.EdgePreviewExperimentChecks/Program.cs',
    'tests/PaperTodo.EdgePreviewExperimentChecks/SharedSemanticChecks.cs']
write('../results/changed-paths.txt', '\n'.join(paths) + '\n')
subprocess.run(['git', 'add', '--', *paths], check=True)
subprocess.run(['git', 'diff', '--cached', '--check'], check=True)
write('../results/candidate.diff', subprocess.check_output(['git', 'diff', '--cached']).decode('utf-8'))
