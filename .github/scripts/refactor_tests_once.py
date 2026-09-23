from pathlib import Path
import os
import re
import subprocess

BASE = '3d76648c9beb35748ac06f34eadb2a5f922301bb'
assert os.environ.get('GITHUB_REF') == 'refs/heads/refactor/tests-behavior-and-tools'

def load(path):
    original = subprocess.check_output(['git', 'show', BASE + ':' + path]).decode('utf-8-sig').replace('\r\n', '\n')
    current = Path(path).read_text(encoding='utf-8-sig')
    if current != original:
        raise RuntimeError('Refusing to edit a changed input: ' + path)
    return current

def save(path, text):
    target = Path(path)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(text, encoding='utf-8', newline='\n')
    print('EDIT', path)

def replace(text, old, new=''):
    if text.count(old) != 1:
        raise RuntimeError('Expected one exact edit anchor: ' + repr(old[:140]))
    return text.replace(old, new, 1)

def cut(text, start, end):
    if text.count(start) != 1 or text.count(end) != 1:
        raise RuntimeError('Ambiguous cut: ' + start[:100])
    a, b = text.index(start), text.index(end)
    if b <= a: raise RuntimeError('Reversed cut')
    return text[:a] + text[b:]

def method_span(text, name):
    pattern = r'(?m)^    (?:private|internal|public) (?:static )?[^\n]*\b' + re.escape(name) + r'\('
    hits = list(re.finditer(pattern, text))
    if len(hits) != 1: raise RuntimeError('Method not unique: ' + name)
    a = hits[0].start()
    closing = re.search(r'(?m)^    }\n?', text[hits[0].end():])
    if not closing: raise RuntimeError('Method end not found: ' + name)
    b = hits[0].end() + closing.end()
    return a, b

def edit_method(text, name, operation):
    a, b = method_span(text, name)
    return text[:a] + operation(text[a:b]) + text[b:]

def remove_method(text, name, remove_calls=True):
    a, b = method_span(text, name)
    text = text[:a] + text[b:]
    if remove_calls:
        text = re.sub(r'(?m)^[ \t]*' + re.escape(name) + r'\([^\n]*\);\n', '', text)
    return text

def drop_assert(text, message):
    if text.count(message) != 1: raise RuntimeError('Assertion not unique: ' + message)
    location = text.index(message)
    starts = list(re.finditer(r'(?m)^[ \t]*(?:Assert|Require|Check)\(', text[:location]))
    if not starts: raise RuntimeError('Assertion start missing: ' + message)
    a = starts[-1].start()
    b = text.index(');', location) + 2
    if b < len(text) and text[b] == '\n': b += 1
    print('REMOVE ASSERT', message)
    return text[:a] + text[b:]

# Internal field/type/method layout is not a plugin contract. Keep real calls and public ABI checks.
p = 'tests/PaperTodo.ProtocolPolicyChecks/Program.cs'
s = load(p)
s = edit_method(s, 'CheckSingleHotkeyAuthority', lambda t: cut(t, '        var brokerType =', '        var tryApply ='))
for name in ['CheckRuntimeSlotAuthority', 'CheckProtocolBoundaries', 'CheckSharedWebInfrastructure', 'CheckManifestRuntimeAndMiniContracts']:
    s = remove_method(s, name)
s = drop_assert(s, 'Runtime transitions must not retain a descriptor-change hot-reload recovery path.')
s = edit_method(s, 'CheckSettingsLayoutManifest', lambda t: cut(t, '        var categoryType =', '        var validateApi ='))
s = edit_method(s, 'CheckSettingsLayoutManifest', lambda t: cut(t, '        var controller =', '    }'))
def runtime_api(t):
    t = cut(t, '        var controller =', '        var context =')
    t = cut(t, '        Assert(\n            controller.GetField', '        Assert(context.GetProperty')
    return drop_assert(t, 'The Web provider Runtime must use the same logical Paper/state contract as Native.')
s = edit_method(s, 'CheckUnifiedPluginRuntime', runtime_api)
s = edit_method(s, 'CheckWebBodyNavigationIdentity', lambda t: cut(t, '        Assert(', '        var canAccept ='))
s = drop_assert(s, 'Web body must have an explicit system-shell path for external top-level navigation.')
s = edit_method(s, 'CheckGlobalTopBarPriority', lambda t: cut(t, '        var controller =', '    }'))
s = edit_method(s, 'CheckPluginRuntimePersistenceGuards', lambda t: cut(t, '        var dataStore =', '        var controller ='))
s = edit_method(s, 'CheckPluginRuntimePersistenceGuards', lambda t: cut(t, '        var webRuntime =', '    }'))
s = edit_method(s, 'CheckPluginRuntimeSettings', lambda t: cut(t, '        var controller =', '    }'))
s = edit_method(s, 'CheckProtocol21Contributions', lambda t: cut(t, '        var controller =', '    }'))
s = s.replace('CheckSingleHotkeyAuthority', 'CheckShortcutReservations').replace('CheckUnifiedPluginRuntime', 'CheckRuntimeApiCompatibility').replace('CheckGlobalTopBarPriority', 'CheckTopBarApiCompatibility')
save(p, s)

# Thread safety and rendered dimensions remain; same-reference cache and frozen-shape assertions do not.
p = 'tests/PaperTodo.ThreadingChecks/ResourceChecks.cs'
s = load(p)
for message in ['same-thread cache not reused', 'global easing is not frozen', "existing menu still holds the old scale's style", 'new and existing menus do not share the current style']:
    s = drop_assert(s, message)
s = replace(s, '        for (var index = 0; index < first.Length; index++)\n            Assert(!ReferenceEquals(first[index], second![index]), "dispatcher-owned resource shared across UI threads");\n')
s = replace(s, '        var updated = (Style)menu.Resources[typeof(MenuItem)];\n')
save(p, s)
p = 'tests/PaperTodo.MarkdownEditingChecks/MarkerSlotEdgeChecks.cs'
save(p, remove_method(load(p), 'CheckMetricKeyTracksTextFormattingMode'))
p = 'tests/PaperTodo.EdgeTitleChecks/SharedFrameRenderingChecks.cs'
save(p, remove_method(load(p), 'PreviewClipReuse'))
p = 'tests/PaperTodo.EdgePreviewChecks/ArtifactRenderingChecks.cs'
s = load(p)
s = drop_assert(s, 'measure/repaint never recreate text blocks or layout')
s = replace(s, '            var before = surface.Artifact;\n')
save(p, s)
p = 'tests/PaperTodo.EdgePreviewChecks/ReviewBoundaryChecks.cs'
save(p, replace(load(p), 'ReferenceEquals(pieces[0].Text, text)', 'pieces[0].Text == text'))
p = 'tests/PaperTodo.EdgePreviewChecks/SharedSemanticChecks.cs'
s = load(p)
a = s.index('        var label = adjacent.Where')
b = s.index('        var parenthesis =', a)
s = s[:a] + '''        Require(string.Concat(adjacent.Select(p => p.Text)) == "a boldsecond" &&
            adjacent.All(p => p.Link?.Host == "example.com"),
            "styled and adjacent link labels retain their text and destination");
''' + s[b:]
s = cut(s, '        var cache = new Renderer.PreviewInlineCache();', '        Console.WriteLine(')
s = s.replace('link identity, caching and bounded publication', 'link destinations and bounded publication')
save(p, s)
p = 'tests/PaperTodo.EdgePreviewChecks/PreloadAuditChecks.cs'
s = load(p)
for message in ['shared half-second debounce precedes startup prelayout', 'eligibility, measurement and layout share one source read', 'unchanged excerpt reuses prepared semantics', "an already-running drain does not bypass the replacement's 500ms debounce"]:
    s = drop_assert(s, message)
s = replace(s, '            var requestedAt = Stopwatch.GetTimestamp();\n')
for line in ['            var sourceReads = 0;\n', '            var replacementAt = 0L;\n', '            var replacementReadAt = 0L;\n', '                replacementAt = Stopwatch.GetTimestamp();\n', '                    replacementReadAt = Stopwatch.GetTimestamp();\n']:
    s = replace(s, line)
s = s.replace('() => { sourceReads++; return new string(\'x\', 450); }', '() => new string(\'x\', 450)')
s = s.replace('semantic reuse and retirement', 'captured content and retirement').replace('replacement debounce and active Forget', 'replacement completion and active Forget')
save(p, s)

# Keep current content, input revocation, invalidation and unload checks without requiring surface reuse.
p = 'tests/PaperTodo.EdgePreviewChecks/CompletionChecks.cs'
s = load(p)
s = replace(s, '                descriptor.SetVisibility?.Invoke(false);\n', '                var textBeforeResume = PreviewText(viewport);\n                descriptor.SetVisibility?.Invoke(false);\n')
s = replace(s, 'Require(ReferenceEquals(body, PublishedBody(viewport)), "unchanged resume reuses its own surface");', 'Require(viewport.IsHitTestVisible && PreviewText(viewport) == textBeforeResume, "resume restores current visible content and input");')
s = replace(s, 'Require(ReferenceEquals(old, viewport.Children.OfType<MarkdownPreviewArtifactSurface>().Single()) && !viewport.IsHitTestVisible,', 'Require(!viewport.IsHitTestVisible && !PreviewText(viewport).Contains("obsolete"),')
s = edit_method(s, 'CheckReuseAndInvalidation', lambda _: '''    private static void CheckReuseAndInvalidation()
    {
        var viewport = new MarkdownEdgeCapsulePreviewViewport();
        void Set(string text) => viewport.SetContent(
            MarkdownEdgeCapsulePreviewRenderer.CaptureContent(text, MarkdownRenderModes.Full), _ => { });
        void Ready(string text) => UntilReview(() => viewport.IsHitTestVisible &&
            PreviewText(viewport).Contains(text), "current preview content is readable and interactive");
        Set("当前内容");
        var window = new Window { Content = viewport, Width = 320, Height = 180, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show(); Ready("当前内容");
            for (var i = 0; i < 5; i++)
            {
                viewport.SetPreviewActive(false); Pump();
                Require(!viewport.IsHitTestVisible, "inactive preview cannot accept input");
                viewport.SetPreviewActive(true); Ready("当前内容");
            }
            viewport.Visibility = Visibility.Hidden; Pump();
            viewport.Visibility = Visibility.Visible; Ready("当前内容");
            viewport.SetPreviewActive(false); Set("更新内容"); Pump();
            Require(!viewport.IsHitTestVisible, "inactive invalidation cannot enable stale input");
            viewport.SetPreviewActive(true); Ready("更新内容");
            window.Width += 70; Pump(); Ready("更新内容");
            typeof(MarkdownEdgeCapsulePreviewViewport).GetMethod("OnDpiChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(viewport, new object[] { new DpiScale(1, 1), new DpiScale(1.5, 1.5) });
            Pump(); Ready("更新内容");
            var body = PublishedBody(viewport);
            window.Content = null; Pump();
            Require(body.Parent == null && !viewport.Children.OfType<MarkdownPreviewArtifactSurface>().Any(), "unload releases the mounted surface");
            window.Content = viewport; Ready("更新内容");
            Console.WriteLine("PASS preview visibility, content/width/DPI invalidation and unload");
        }
        finally { window.Close(); Pump(); }
    }
''')
s = s.replace('CheckReuseAndInvalidation', 'CheckVisibilityAndInvalidation')
save(p, s)

# UI updates may reuse or replace controls: test visible text, draft, focus, notifications and late events.
p = 'tests/PaperTodo.SettingsApiChecks/SettingsUiChecks.cs'
s = load(p)
s = replace(s, 'Check(ReferenceEquals(window.Content, root) && ReferenceEquals(editor, ReadField<TextBox>(c, "_settingsExternalMarkdownTextBox")) &&\n                editor.IsKeyboardFocusWithin, "External extension update keeps the same focused editor and page.");', 'Check(editor.IsKeyboardFocusWithin, "External extension update preserves keyboard focus.");')
s = replace(s, 'Check(ReferenceEquals(window.Content, root) && editor.IsKeyboardFocusWithin && editor.Text == ".draft" &&', 'Check(editor.IsKeyboardFocusWithin && editor.Text == ".draft" &&')
s = replace(s, 'Check(refreshes == 1 && !ReferenceEquals(window.Content, root), "Theme rebuilds settings chrome exactly once.");', 'Check(refreshes == 1, "Theme changes notify existing consumers once.");')
s = replace(s, '            Check(c.State.ExternalMarkdownExtension == ".txt" && persistedExtension', '            editor = ReadField<TextBox>(c, "_settingsExternalMarkdownTextBox");\n            Check(c.State.ExternalMarkdownExtension == ".txt" && persistedExtension')
s = replace(s, '            Check(editor.IsKeyboardFocusWithin && editor.Text == ".draft" &&', '            editor = ReadField<TextBox>(c, "_settingsExternalMarkdownTextBox");\n            Check(editor.IsKeyboardFocusWithin && editor.Text == ".draft" &&')
a = s.index('            editor.Text = ".stale";')
b = s.index('\n        }\n        finally', a)
late = s[a:b]
s = s[:a] + '            if (!ReferenceEquals(editor, replacement))\n            {\n' + '\n'.join('    ' + line for line in late.splitlines()) + '\n            }' + s[b:]
save(p, s)
p = 'tests/PaperTodo.SettingsApiChecks/PresentationChecks.cs'
s = load(p)
s = drop_assert(s, 'Refreshing a linked title does not rebuild the todo editors.')
s = replace(s, '        var editors = ReadField<Dictionary<string, TodoTextBox>>(window, "_todoEditors");\n        var editor = editors[todo.Items[0].Id];\n')
s = replace(s, '        var linkedLabel = Labels(window).First(label => label.Text.Contains(note.Title, StringComparison.Ordinal));\n', '')
s = replace(s, '        Check(note.Title == "Link" && linkedLabel.Text.Contains("Link", StringComparison.Ordinal) &&\n            !linkedLabel.Text.Contains("LinkedABC", StringComparison.Ordinal),', '        var visibleLabels = Labels(window).Select(label => label.Text).ToArray();\n        Check(note.Title == "Link" && visibleLabels.Any(text => text.Contains("Link", StringComparison.Ordinal)) &&\n            visibleLabels.All(text => !text.Contains("LinkedABC", StringComparison.Ordinal)),')
s = replace(s, '        Check(note.Title == "Li" && !linkedLabel.Text.Contains("Link", StringComparison.Ordinal),\n            "UI title truncation refreshes the same linked label.");', '        Check(note.Title == "Li" && Labels(window).Any(label => label.Text.Contains("Li", StringComparison.Ordinal)) &&\n            Labels(window).All(label => !label.Text.Contains("LinkedABC", StringComparison.Ordinal)),\n            "UI title truncation refreshes the visible linked label.");')
save(p, s)

# Remove default-run profilers and the instrumented duplicate parser. Test through the actual edit owner.
root = 'tests/PaperTodo.MarkdownSemanticChecks/'
profile = load(root + 'PerformanceProfileChecks.cs')
a, b = method_span(profile, 'BuildLargeStressSource')
fixture = profile[a:b].replace('internal static string BuildLargeStressSource()', 'internal static string BuildDenseSource()')
helper = '''using ICSharpCode.AvalonEdit.Document;

namespace PaperTodo;

internal static class MarkdownEditBehavior
{
    internal static MarkdownSemanticSnapshot ReadAfterEdit(string before, string after)
    {
        var document = new TextDocument(before);
        using var semantics = new MarkdownSemanticDocument(document);
        document.Text = after;
        return semantics.TryGetCurrent(out var result)
            ? result
            : throw new InvalidOperationException("Edited document did not publish current semantics.");
    }

    internal static void AssertEquivalent(MarkdownSemanticSnapshot expected, MarkdownSemanticSnapshot actual, string name)
    {
        if (expected.LineCount != actual.LineCount || !expected.Spans.SequenceEqual(actual.Spans) ||
            !expected.Links.SequenceEqual(actual.Links))
            throw new InvalidOperationException($"FAIL {name}: edited semantics differ.");
        for (var line = 0; line < expected.LineCount; line++)
            if (!expected.GetLine(line).Equals(actual.GetLine(line)) ||
                !expected.SpansForLine(line).SequenceEqual(actual.SpansForLine(line)) ||
                !expected.LinksForLine(line).SequenceEqual(actual.LinksForLine(line)))
                throw new InvalidOperationException($"FAIL {name}: line {line} differs.");
    }

''' + fixture + '}\n'
save(root + 'MarkdownEditBehavior.cs', helper)
save(root + 'DenseMarkdownChecks.cs', '''using System.Runtime.CompilerServices;
namespace PaperTodo;
internal static class DenseMarkdownChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        var source = MarkdownEditBehavior.BuildDenseSource();
        var word = source.IndexOf("- [ ] item ", source.Length / 2, StringComparison.Ordinal);
        var fence = source.IndexOf("# Heading ", source.Length / 3, StringComparison.Ordinal);
        if (word < 0 || fence < 0) throw new InvalidOperationException("Dense Markdown fixture probe missing.");
        foreach (var (name, edited) in new[]
        {
            ("dense text edit", source.Insert(word + "- [ ] item ".Length, "Z")),
            ("dense fence edit", source.Insert(fence, "```text\\n"))
        })
        {
            MarkdownEditBehavior.AssertEquivalent(MarkdownSemanticSnapshot.Parse(edited),
                MarkdownEditBehavior.ReadAfterEdit(source, edited), name);
            Console.WriteLine("PASS " + name);
        }
    }
}
''')
for name in ['PerformanceProfileChecks.cs', 'IncrementalProfileChecks.cs', 'FenceDenseProfileChecks.cs']:
    load(root + name)
    Path(root + name).unlink()
    print('DELETE', root + name)
p = root + 'IncrementalLargeChecks.cs'
s = load(p)
s = remove_method(s, 'ProfileDenseMarkdownIncrementalEdit')
s = replace(s, '        var snapshot = MarkdownSemanticSnapshot.Parse(source);', '        var document = new ICSharpCode.AvalonEdit.Document.TextDocument(source);\n        using var semantics = new MarkdownSemanticDocument(document);')
a = s.index('            var windowLength =')
b = s.index('            ValidateRanges(', a)
s = s[:a] + '''            document.Text = next;
            if (!semantics.TryGetCurrent(out var incremental))
                throw new InvalidOperationException($"FAIL large edit step {step}: no current snapshot");
''' + s[b:]
s = replace(s, '            snapshot = incremental;\n')
s = s.replace('using System.Diagnostics;\n', '')
save(p, s)
p = root + 'FenceWindowChecks.cs'
s = load(p)
s = remove_method(s, 'ProfileFenceStateExpansion')
s = re.sub(r'(?m)^            minimumWindow: [^\n]*\n', '', s)
s = replace(s, '        var existingClosing = builder.Length;\n')
s = edit_method(s, 'CheckInlineBackticksStayLocal', lambda t: cut(t, '        var oldSnapshot =', '        AssertEquivalent('))
s = replace(s, '        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource), incremental, "inline backticks");', '        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource),\n            MarkdownEditBehavior.ReadAfterEdit(oldSource, newSource), "inline backticks");')
s = replace(s, '        Console.WriteLine($"PASS inline triple backticks stay local window={windowLength}");', '        Console.WriteLine("PASS inline triple backticks preserve literal text");')
s = edit_method(s, 'AssertExpandedEditMatchesFull', lambda _: '''    private static void AssertExpandedEditMatchesFull(string oldSource, string newSource, string name)
    {
        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource),
            MarkdownEditBehavior.ReadAfterEdit(oldSource, newSource), name);
        Console.WriteLine($"PASS {name}");
    }
''')
s = s[:s.index('    private static double Median(')] + '}\n'
s = s.replace('using System.Diagnostics;\n', '').replace('AssertExpandedEditMatchesFull', 'AssertEditMatchesFull').replace('CheckInlineBackticksStayLocal', 'CheckInlineBackticksRemainLiteral')
save(p, s)
p = root + 'IncrementalSnapshotChecks.cs'
s = load(p)
s = edit_method(s, 'CheckSmallDocumentsUseFullParse', lambda t: cut(t, '        if (MarkdownSemanticDocument.FullParseThresholdChars', '        var source ='))
s, count = re.subn(r'        var windowLength = MarkdownSemanticSnapshot.GetIncrementalWindowLengthForTests\([\s\S]*?\);\n\s*if \(windowLength[^\n]*\)\n        \{[\s\S]*?\n        \}\n', '', s)
if count != 3: raise RuntimeError('Expected three snapshot window-size constraints')
pattern = r'        if \(!MarkdownSemanticSnapshot.TryParseIncremental\(\s*oldSource,\s*oldSnapshot,\s*newSource,\s*out var (\w+)\)\)\s*\{\s*throw new InvalidOperationException\([\s\S]*?\);\s*\}\n'
s, count = re.subn(pattern, lambda m: '        var ' + m.group(1) + ' = MarkdownEditBehavior.ReadAfterEdit(oldSource, newSource);\n', s)
if count != 5: raise RuntimeError('Expected five forced incremental-path assertions')
s = edit_method(s, 'AssertFallsBack', lambda _: '''    private static void AssertEdit(string oldSource, string newSource, string name)
    {
        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource),
            MarkdownEditBehavior.ReadAfterEdit(oldSource, newSource), name);
        Console.WriteLine($"PASS {name} publishes current reference semantics");
    }
''')
s = s.replace('AssertFallsBack(', 'AssertEdit(').replace('        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);\n', '')
s = s.replace('Console.WriteLine($"PASS large plain edit local window={windowLength}");', 'Console.WriteLine("PASS large plain edit preserves semantics");')
s = s.replace('Console.WriteLine($"PASS existing long fence expands window={windowLength}");', 'Console.WriteLine("PASS existing long fence edit preserves semantics");')
s = s.replace('$"PASS new long fence expands by state scan window={windowLength} full={newSource.Length}"', '"PASS new long fence edit preserves semantics"')
s = s.replace('PASS small documents parse fully below 8K', 'PASS small document edit preserves full semantics').replace('PASS ordinary edit stays local and preserves distant reference link', 'PASS ordinary edit preserves distant reference link')
for old, new in [('CheckSmallDocumentsUseFullParse', 'CheckSmallDocumentEdit'), ('CheckLargePlainEditStaysLocal', 'CheckLargePlainEdit'), ('CheckExistingLongFenceExpandsFromSnapshot', 'CheckExistingLongFenceEdit'), ('CheckOrdinaryReferenceDocumentEditStaysLocal', 'CheckOrdinaryReferenceDocumentEdit'), ('CheckNewLongFenceExpandsByStateScan', 'CheckNewLongFenceEdit'), ('FallsBack', 'UpdatesReferences')]:
    s = s.replace(old, new)
save(p, s)
p = root + 'SynchronousDocumentChecks.cs'
s = load(p)
s = remove_method(s, 'ProfileActualDocumentEdit')
a = s.index('        var allocationBefore =')
b = s.index('        if (publications != 1', a)
s = s[:a] + '        document.Insert(editAt, "z");\n\n' + s[b:]
s = replace(s, '        Console.WriteLine(\n            $"PROFILE SyncDocument98k-full-fallback {elapsed:F3}ms alloc={allocated / 1024d:F1}KiB");\n')
s = s.replace('using System.Diagnostics;\n', '')
save(p, s)
# A single bounded large-input check remains a correctness smoke test, not a timing threshold.
p = root + 'Program.cs'
s = load(p)
s = s.replace('Large document performance smoke', 'Large document semantic smoke')
s = re.sub(r'    var stopwatch = System.Diagnostics.Stopwatch.StartNew\(\);\n', '', s)
s = s.replace('    stopwatch.Stop();\n', '')
s = re.sub(r'    True\(\s*stopwatch.Elapsed < TimeSpan.FromSeconds\(5\),\s*\$"[^\n]*"\);\n', '', s)
s = re.sub(r'    Console.WriteLine\(\s*\$"INFO Large document[^;]*;\n', '', s)
if 'stopwatch' in s: raise RuntimeError('Unreviewed stopwatch use in semantic Program')
save(p, s)
# Use the same source-link inputs as the semantic checks, never their test entry points or assembly.
save('tools/PaperTodo.MarkdownBenchmarks/PaperTodo.MarkdownBenchmarks.csproj', load(root + 'PaperTodo.MarkdownSemanticChecks.csproj'))

# The remaining Markdown render logger and its lock belong next to the other optional diagnostics.
p = 'src/PaperWindow.Note.cs'
s = load(p)
a, b = method_span(s, 'TraceNoteRender')
trace = s[a:b]
save(p, s[:a] + s[b:])
p = 'src/PaperWindow.cs'
s = load(p)
match = re.search(r'(?m)^    private static readonly object NoteRenderTraceLock[^\n]*\n', s)
if not match: raise RuntimeError('Note render trace lock declaration missing')
lock = match.group(0)
save(p, s[:match.start()] + s[match.end():])
save('tools/PaperTodo.EdgeDiagnostics/EntryPoints/PaperWindow.NoteDiagnostics.cs', 'namespace PaperTodo;\n\npublic sealed partial class PaperWindow\n{\n' + lock + '\n' + trace + '}\n')

# Extract executable sample checks; do not relocate the source-regex architecture assertions.
p = '.github/workflows/plugin-samples.yml'
s = load(p)
step_pattern = re.compile(r'(?m)^      - name: (.+)\n')
starts = list(step_pattern.finditer(s))
steps = {}
for i, match in enumerate(starts):
    end = starts[i + 1].start() if i + 1 < len(starts) else len(s)
    steps[match.group(1)] = s[match.start():end]
selected = ['Validate plugin manifests', 'Validate Web source and runtime copies', 'Check Web settings action behavior', 'Check Web plugin scripts', 'Rebuild and verify native runtime outputs']
blocks = []
for name in selected:
    step = steps[name]
    if '        run: |\n' in step:
        body = step.split('        run: |\n', 1)[1]
        body = '\n'.join(line[10:] if line.startswith('          ') else line for line in body.splitlines()).strip('\n')
    else:
        body = step.split('        run: ', 1)[1].strip()
        body += '\nif ($LASTEXITCODE -ne 0) { throw "Plugin sample behavior check failed: $LASTEXITCODE" }'
    body = body.replace('exit $LASTEXITCODE', 'throw "Plugin sample command failed: $LASTEXITCODE"').replace('Join-Path $env:RUNNER_TEMP', 'Join-Path $temporaryRoot')
    blocks.append("Write-Host '\n=== " + name + " ==='\n" + body)
script = '''#Requires -Version 7.2
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('PaperTodo-plugin-checks-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $temporaryRoot
Push-Location $repository
try {
''' + '\n\n'.join('\n'.join('    ' + line for line in block.splitlines()) for block in blocks) + '''
}
finally {
    Pop-Location
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}
'''
save('tests/plugin-samples/Check-Samples.ps1', script)
s = s[:s.index('      - name: Run protocol policy checks')] + '      - name: Plugin behavior and sample checks\n        shell: pwsh\n        run: ./tools/testing/Run-Checks.ps1 -Group plugins\n'
s = s.replace('      - "tests/PaperTodo.ProtocolPolicyChecks/**"', '      - "tools/testing/**"\n      - "tests/plugin-samples/**"\n      - "tests/PaperTodo.ProtocolPolicyChecks/**"')
save(p, s)
for path, anchor, group in [('.github/workflows/pull-request-build.yml', '      - name: Real paper and cross-process window stacking checks', 'regression'), ('.github/workflows/persistence-checks.yml', '      - name: Restore persistence checks', 'persistence'), ('.github/workflows/edge-diagnostics-checks.yml', '      - name: Journal Debug collection and exit checks', 'diagnostics')]:
    text = load(path)
    if text.count(anchor) != 1: raise RuntimeError('Workflow execution anchor missing: ' + path)
    text = text[:text.index(anchor)] + '      - name: Run ' + group + ' checks\n        run: ./tools/testing/Run-Checks.ps1 -Group ' + group + '\n'
    save(path, text)

# One-off verification only, not a new source-shape test in the repository.
for path in Path(root).glob('*.cs'):
    text = path.read_text(encoding='utf-8')
    for obsolete in ['PerformanceProfileChecks', 'GetIncrementalWindowLengthForTests', 'PROFILE ', 'windowLength']:
        if obsolete in text: raise RuntimeError(str(path) + ' retains removed profiler dependency: ' + obsolete)
subprocess.run(['git', 'diff', '--check'], check=True)
Path(__file__).unlink()
print('One-off edits complete; migration script removed.')
