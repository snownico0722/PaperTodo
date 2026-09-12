from pathlib import Path
import subprocess


def edit(name, old, new, count=1):
    p = Path(name)
    s = p.read_text(encoding='utf-8')
    assert s.count(old) == count, (name, s.count(old), old)
    p.write_bytes(s.replace(old, new).encode())
    subprocess.run(['git', 'add', '--', name], check=True)


edit('src/EdgeCapsulePreview.Preload.cs',
     'Content, double Zoom,\n        Action<string> OpenExternal)', 'Content, double Zoom)')
edit('src/EdgeCapsulePreview.Preload.cs', 'content, zoom, context.OpenExternal) : null;', 'content, zoom) : null;')
edit('tests/PaperTodo.EdgePreviewChecks/ArtifactRenderingChecks.cs', 'i:asset', 'i:123456', 2)
edit('tests/PaperTodo.EdgePreviewChecks/PreloadChecks.cs', 'i:asset', 'i:123456')
edit('tests/PaperTodo.EdgePreviewChecks/CompletionChecks.cs',
     'panel.Resources["HoverBrushKey"] = Brushes.LightGray;',
     'panel.Resources["HoverBrushKey"] = Brushes.LightGray;\n                panel.Resources["PaperBorderBrushKey"] = Brushes.Gray;')
edit('tests/PaperTodo.EdgePreviewChecks/Program.cs',
     '''if (element is MarkdownEdgeCapsulePreviewViewport old)
                return old.IsArrangeValid && old.Children.OfType<Panel>().Any(panel =>
                    panel.Opacity > 0 && (panel is MarkdownPreviewArtifactSurface || panel.Children.Count > 0) && panel.IsArrangeValid);''',
     '''if (element is MarkdownEdgeCapsulePreviewViewport viewport)
                return viewport.IsArrangeValid && viewport.Opacity > 0 && viewport.IsHitTestVisible &&
                    viewport.Children.OfType<MarkdownPreviewArtifactSurface>().Any(surface => surface.IsArrangeValid);''')
