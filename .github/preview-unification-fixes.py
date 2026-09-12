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
edit('src/EdgeCapsulePreview.Markdown.Artifact.cs',
     'copy.Pen = new Pen(foreground, 1);',
     '''// Syntax fades the child glyph, not the enclosing underline.
                            copy.Pen = new Pen(link ? _link : spec.Foreground, 1);''')
# WPF can already have snapshotted an event invocation while a nested dispatcher
# completion detaches the handler. Close the gate on the UI before disposing its CTS.
edit('src/EdgeCapsulePreview.Preload.cs',
     'void CancelLifetime() => lifetime.Cancel();',
     '''var listening = true;
        void CancelLifetime() { if (listening) lifetime.Cancel(); }''')
edit('src/EdgeCapsulePreview.Preload.cs',
     '''void Detach()
            {
                target.Context.InvalidationSource.Invalidated -= invalidated;''',
     '''void Detach()
            {
                listening = false;
                target.Context.InvalidationSource.Invalidated -= invalidated;''')
edit('tests/PaperTodo.EdgePreviewChecks/ArtifactRenderingChecks.cs',
     '        IndependentDrawingReferences();',
     '''        // Underline belongs to its enclosing Span/Hyperlink, not faded syntax Runs.
        root.Resources["TextBrushKey"] = Brushes.Black;
        root.Resources["LinkBrushKey"] = Brushes.Blue;
        foreach (var (text, color) in new[] {
            ("<u>under ~~strike~~</u>", Colors.Black),
            ("[a **bold**](https://example.com)", Colors.Blue) })
        {
            var syntaxPlan = Renderer.CaptureArtifactPlan(root,
                Renderer.CaptureContent(text, MarkdownRenderModes.Enhanced), 400, 1);
            var decoratedSyntax = syntaxPlan.Styles.Where(s =>
                s.Foreground is SolidColorBrush b && b.Color == ((SolidColorBrush)Theme.SyntaxFadeBrush).Color &&
                s.Decorations?.Any(d => d.Location == TextDecorationLocation.Underline) == true).ToArray();
            Require(decoratedSyntax.Length > 0 && decoratedSyntax.All(s => s.Decorations!
                .Where(d => d.Location == TextDecorationLocation.Underline)
                .All(d => d.Pen?.Brush is SolidColorBrush b && b.Color == color)),
                "nested faded syntax retains the enclosing decoration color");
        }
        IndependentDrawingReferences();''')
# Keep exact inspected source beside results; temporary transport files are removed before delivery.
tree = subprocess.check_output(['git', 'write-tree'], text=True).strip()
subprocess.run(['git', 'archive', '--format=zip', '--output=evidence/unified-source.zip', tree], check=True)
