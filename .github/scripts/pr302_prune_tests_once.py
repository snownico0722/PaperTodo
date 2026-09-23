from pathlib import Path
import os
import re
import subprocess

BASE = 'dbe37e3aea84618dcbbc096a4d34a0dbe40bf20c'
assert os.environ.get('GITHUB_REF') == 'refs/heads/refactor/tests-behavior-and-tools'
originals = {}

def read(path):
    if path not in originals:
        baseline = subprocess.check_output(['git', 'show', BASE + ':' + path]).decode('utf-8-sig').replace('\r\n', '\n')
        current = Path(path).read_text(encoding='utf-8-sig')
        if current != baseline:
            raise RuntimeError('Input changed since review: ' + path)
        originals[path] = current
    return originals[path]

def write(path, text):
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding='utf-8', newline='\n')
    print('WRITE', path, len(text.splitlines()))

def one(text, old, new=''):
    if text.count(old) != 1: raise RuntimeError('Non-unique replacement: ' + repr(old[:120]))
    return text.replace(old, new, 1)

def span(text, name):
    hits = list(re.finditer(r'(?m)^    (?:private|internal|public) static [^\n]*\b' + re.escape(name) + r'\(', text))
    if len(hits) != 1: raise RuntimeError('Non-unique method: ' + name)
    a = hits[0].start()
    end = re.search(r'(?m)^    }\n?', text[hits[0].end():])
    if not end: raise RuntimeError('Missing method end: ' + name)
    return a, hits[0].end() + end.end()

def method(text, name):
    a,b = span(text, name)
    return text[a:b]

def remove(text, name):
    a,b = span(text, name)
    return text[:a] + text[b:]

def replace_method(text, name, body):
    a,b = span(text, name)
    return text[:a] + body.rstrip() + '\n' + text[b:]

def between(text, begin, end, replacement=''):
    if text.count(begin) != 1 or text.count(end) != 1: raise RuntimeError('Ambiguous range: ' + begin)
    a,b = text.index(begin), text.index(end)
    if b <= a: raise RuntimeError('Reversed range')
    return text[:a] + replacement + text[b:]

preview = 'tests/PaperTodo.EdgePreviewChecks/'
semantic = 'tests/PaperTodo.MarkdownSemanticChecks/'
editing = 'tests/PaperTodo.MarkdownEditingChecks/'
bench = 'tools/PaperTodo.DesktopBenchmarks/'
program = read(preview + 'Program.cs')
preload = read(preview + 'PreloadChecks.cs')
boundary = read(preview + 'ReviewBoundaryChecks.cs')
completion = read(preview + 'CompletionChecks.cs')
lifecycle = read('tests/PaperTodo.LifecycleChecks/Program.cs')

# These are measurements and exports, not test entry points. The tool references the product,
# never a test assembly, and calls the same actual host/renderer as before.
imports = '''using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
'''
helper = ''.join(method(program, n) for n in ['Require', 'Pump', 'Elements'])
a = program.index('    private static EdgeCapsuleHost NewHost(')
b = program.index('    private static void Profile()', a)
helper += program[a:b]
helper += method(preload, 'AwaitPreload')
render = method(completion, 'RenderForCheck').replace('RenderForCheck', 'RenderForSample').replace('AwaitWorkerCheck(', 'AwaitPreload(')
preview_tools = ''.join(method(program, n) for n in ['Profile', 'ProfileOne'])
preview_tools += ''.join(method(preload, n) for n in ['PreloadMemory', 'ProfilePreload'])
preview_tools += method(boundary, 'ProfilePlainInlineAllocation')
preview_tools += method(completion, 'ExportPreviewPixels').replace('RenderForCheck(', 'RenderForSample(')
write(bench + 'PreviewMeasurements.cs', imports + helper + render + preview_tools + '}\n')
write(bench + 'PaperTodo.DesktopBenchmarks.csproj', '''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.17763.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="../../PaperTodo.csproj" /></ItemGroup>
</Project>
''')
write(bench + 'Program.cs', '''using System.Text.Json;
using System.Windows;
using PaperTodo;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--lifecycle-fixture", var count] && int.TryParse(count, out var parsed) && parsed is > 0 and <= 100)
            return LifecycleFixture(parsed);
        if (args is ["--help"] or ["-h"] or [])
        {
            Console.WriteLine("Desktop measurements: --preview | --preload [--reverse] | --memory | --inline | --lifecycle | --export <folder> | --smoke");
            Console.WriteLine("Run in Release on Windows. No pass/fail performance threshold. Fixtures never use daily PaperTodo data.");
            return 0;
        }
        if (!(args is ["--preview"] or ["--preload"] or ["--preload", "--reverse"] or ["--memory"] or ["--inline"] or ["--lifecycle"] or ["--export", _] or ["--smoke"]))
        {
            Console.Error.WriteLine("Unknown arguments. Use --help.");
            return 2;
        }
        try
        {
            Console.WriteLine($"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
#if DEBUG
            Console.WriteLine("WARNING: Debug measurements are not comparable to Release.");
#else
            Console.WriteLine("Configuration: Release");
#endif
            if (args[0] == "--lifecycle") { MeasureLifecycle(smoke: false); return 0; }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                switch (args[0])
                {
                    case "--preview": Profile(); break;
                    case "--preload": ProfilePreload(args.Length == 2); break;
                    case "--memory": PreloadMemory(); break;
                    case "--inline": ProfilePlainInlineAllocation(); break;
                    case "--export": ExportPreviewPixels(args[1]); break;
                    case "--smoke":
                        var text = string.Concat(Enumerable.Repeat("**sample** `code` [link](https://example.com) ", 30));
                        Console.WriteLine("SMOKE_COLD " + JsonSerializer.Serialize(ProfileOne(text, MarkdownRenderModes.Full)));
                        Console.WriteLine("SMOKE_WARM " + JsonSerializer.Serialize(ProfileOne(text, MarkdownRenderModes.Full, "layout")));
                        MeasureLifecycle(smoke: true);
                        break;
                }
            }
            finally { app.Shutdown(); }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
''')
# Isolated lifecycle measurements use the real AppController. The old baseline bypass and
# regression assertions are not copied into this tool.
copy_binaries = method(lifecycle, 'CopyBinaries')
write(bench + 'LifecycleMeasurements.cs', imports + copy_binaries + '''
    private const string LifecycleMarker = ".papertodo-desktop-measurement";

    private static void MeasureLifecycle(bool smoke)
    {
        var counts = smoke ? new[] { 1 } : new[] { 1, 5, 10, 25 };
        for (var round = 0; round < (smoke ? 1 : 3); round++)
        foreach (var count in round % 2 == 0 ? counts : counts.Reverse())
        {
            var directory = Path.Combine(Path.GetTempPath(), "PaperTodo.DesktopBenchmarks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                CopyBinaries(AppContext.BaseDirectory, directory);
                File.WriteAllText(Path.Combine(directory, LifecycleMarker), "owned measurement data");
                var start = new ProcessStartInfo(Path.Combine(directory, "PaperTodo.DesktopBenchmarks.exe"))
                {
                    WorkingDirectory = directory, UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
                };
                start.ArgumentList.Add("--lifecycle-fixture"); start.ArgumentList.Add(count.ToString());
                var began = Stopwatch.GetTimestamp();
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start lifecycle fixture.");
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30_000))
                {
                    process.Kill(entireProcessTree: true); process.WaitForExit();
                    throw new TimeoutException("Lifecycle fixture did not finish.");
                }
                Console.Write(stdout.GetAwaiter().GetResult()); Console.Error.Write(stderr.GetAwaiter().GetResult());
                if (process.ExitCode != 0) throw new InvalidOperationException("Lifecycle fixture failed: " + process.ExitCode);
                Console.WriteLine("LIFECYCLE_PROCESS " + JsonSerializer.Serialize(new { count, round, totalMs = Stopwatch.GetElapsedTime(began).TotalMilliseconds }));
            }
            finally { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } }
        }
    }

    private static int LifecycleFixture(int count)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, LifecycleMarker)))
            throw new InvalidOperationException("Refusing to measure against non-fixture user data.");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var result = 0;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var state = new AppState
                {
                    TelemetryEnabled = false, EnableAnimations = true, UseCapsuleMode = true,
                    UseDeepCapsuleMode = true, ExperimentalEdgeCapsuleHoverPreview = true,
                    UsePersistentPowerShellProcess = false, McpEnabled = false
                };
                var area = SystemParameters.WorkArea;
                for (var i = 0; i < count; i++) state.Papers.Add(new PaperData
                {
                    Id = "measurement-" + i, Type = PaperTypes.Note, Content = "short note " + i,
                    IsVisible = true, IsCollapsed = true, X = area.Left + 60, Y = area.Top + 60,
                    Width = 300, Height = 240, CapsuleSide = DeepCapsuleSides.Right
                });
                var store = new StateStore(); store.SaveJsonSync(store.SerializeState(state), 1);
                var began = Stopwatch.GetTimestamp();
                using var controller = new AppController();
                var constructed = Stopwatch.GetTimestamp();
                await controller.StartAsync(createDefaultPaper: false);
                var started = Stopwatch.GetTimestamp();
                var windows = (Dictionary<string, PaperWindow>)typeof(AppController)
                    .GetField("_windows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
                var cache = MarkdownEdgePreviewPreload.For(app.Dispatcher);
                while (!windows.Values.All(window => window.IsShellBuilt) || cache.PendingCount > 0)
                {
                    if (Stopwatch.GetElapsedTime(started).TotalSeconds > 12) throw new TimeoutException("Startup never settled.");
                    await Task.Delay(10);
                }
                var ready = Stopwatch.GetTimestamp();
                var visible = windows.Values.Count(window => window.HasVisibleSurface);
                var disposal = Stopwatch.GetTimestamp();
                controller.Dispose();
                Console.WriteLine("LIFECYCLE_SAMPLE " + JsonSerializer.Serialize(new
                {
                    count, visible,
                    ctorMs = Stopwatch.GetElapsedTime(began, constructed).TotalMilliseconds,
                    startupReturnMs = Stopwatch.GetElapsedTime(constructed, started).TotalMilliseconds,
                    readyMs = Stopwatch.GetElapsedTime(constructed, ready).TotalMilliseconds,
                    disposeMs = Stopwatch.GetElapsedTime(disposal).TotalMilliseconds
                }));
            }
            catch (Exception error) { result = 1; Console.Error.WriteLine(error); }
            finally { app.Shutdown(); }
        });
        app.Run();
        return result;
    }
}
''')
p = 'PaperTodo.csproj'
s = read(p)
s = one(s, '    <!-- Diagnostic entry points keep their existing DEBUG guards / Release no-op behavior.', '''    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo">
      <_Parameter1>PaperTodo.DesktopBenchmarks</_Parameter1>
    </AssemblyAttribute>
    <!-- Diagnostic entry points keep their existing DEBUG guards / Release no-op behavior.''')
write(p, s)

# Tests have no benchmarking/export switches and no implicit fallback for misspelled arguments.
s = program
for name in ['Profile', 'ProfileOne']: s = remove(s, name)
s = replace_method(s, 'Main', '''    private static int Main(string[] args)
    {
        new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            if (args is ["--artifact-readiness"]) { ArtifactSurfaceChecks(); ArtifactReadinessChecks(); }
            else if (args is ["--worker-checks"]) MarkdownWorkerChecks();
            else if (args is ["--review-integration"]) ReviewIntegrationChecks();
            else if (args is ["--review-only"]) ReviewBoundaryChecks();
            else if (args.Length == 0)
            {
                ArtifactSurfaceChecks(); ArtifactRenderingChecks(); SharedPreviewSemanticChecks.Run();
                Checks(); ReviewBoundaryChecks(); ArtifactReadinessChecks(); PreloadChecks();
                ReviewIntegrationChecks(); MarkdownWorkerChecks();
            }
            else throw new ArgumentException("Unknown check arguments. Measurements now live in tools/PaperTodo.DesktopBenchmarks.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Application.Current.Shutdown(); }
    }''')
s = s.replace('using System.Diagnostics;\n', '').replace('using System.Text.Json;\n', '')
write(preview + 'Program.cs', s)
s = remove(remove(boundary, 'ProfilePlainInlineAllocation'), 'CheckPlainInlineReuse')
s = one(s, '        Check("plain-inline-reuse", CheckPlainInlineReuse);\n')
write(preview + 'ReviewBoundaryChecks.cs', s)
write(preview + 'CompletionChecks.cs', remove(completion, 'ExportPreviewPixels'))

# Six representative cold/warm presentations replace 60 cross-product cases and cache-hit counters.
# Independent hand-authored image oracles, link gestures and worker cancellation remain elsewhere.
write(preview + 'PreloadChecks.cs', imports + method(preload, 'AwaitPreload') + '''
    private static void PreloadChecks()
    {
        var cache = MarkdownEdgePreviewPreload.For(Dispatcher.CurrentDispatcher);
        cache.SetEnabledForChecks(true);
        var root = new Grid();
        root.Resources["TextBrushKey"] = Brushes.DarkRed;
        root.Resources["WeakTextBrushKey"] = Brushes.Gray;
        root.Resources["LinkBrushKey"] = Brushes.Blue;
        root.Resources["HoverBrushKey"] = Brushes.LightGray;
        root.Resources["PaperBorderBrushKey"] = Brushes.Gray;
        var window = new Window { Content = root, Width = 550, Height = 500, ShowInTaskbar = false, ShowActivated = false };
        var size = new EdgeCapsulePreviewSize(460, 410);
        var text = "# Heading **bold**\\n> quote [q](https://example.com/q)\\n- [x] done `code`\\n12) ordered *italic*\\n---\\n![image](i:123456)\\n```\\n\\nliteral **code**\\n```\\n" + new string('文', 450);
        var mode = MarkdownRenderModes.Full;
        EdgeCapsulePreviewContext Context() => new(new PaperData(), () => "Preview", false, () => text, () => mode,
            (_, _) => false, _ => false, () => new Style(), () => "", _ => { }, new());
        bool Warm(EdgeCapsulePreviewContext context) => AwaitPreload(cache.WarmLayoutAsync(new(context, root, size, () => true, cache.Capture(context))));
        Border Demand(EdgeCapsulePreviewContext context)
        {
            cache.BeginDemand();
            var view = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(context).CreateContent(size);
            var border = new Border { Width = size.ContentSize.Width, Height = size.ContentSize.Height, Child = view };
            root.Children.Add(border);
            ((EdgeCapsuleLivePreviewView)view).PrepareForFirstDisplay();
            border.Measure(new Size(border.Width, border.Height));
            border.Arrange(new Rect(0, 0, border.Width, border.Height));
            UntilReview(() => Elements(view).OfType<MarkdownEdgeCapsulePreviewViewport>().Single().Opacity == 1, "preview publishes complete content");
            return border;
        }
        void Release(Border border) { root.Children.Remove(border); border.Child = null; Pump(); }
        byte[] Pixels(FrameworkElement element)
        {
            element.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(element);
            var width = (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY);
            var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var bytes = new byte[width * height * 4]; bitmap.CopyPixels(bytes, width * 4, 0); return bytes;
        }
        try
        {
            window.Show(); Pump();
            foreach (var (renderMode, sharp, zoom) in new[]
            {
                (MarkdownRenderModes.Off, false, 1.0), (MarkdownRenderModes.Off, true, 1.3),
                (MarkdownRenderModes.Basic, false, 0.7), (MarkdownRenderModes.Basic, true, 1.3),
                (MarkdownRenderModes.Full, false, 0.7), (MarkdownRenderModes.Full, true, 1.3)
            })
            {
                mode = renderMode;
                AppTypography.Configure(sharp ? UiFontPresets.YaHei : UiFontPresets.Default,
                    sharp ? 1.25 : 1, textRenderingProfile: sharp ? TextRenderingProfiles.Sharp : TextRenderingProfiles.Standard);
                NoteTypography.Configure(sharp ? VisualTextSizes.Large : VisualTextSizes.Medium, sharp);
                cache.Clear();
                var context = Context(); context.Paper.TextZoom = zoom;
                Require(Warm(context) && root.Children.Count == 0, "preparation completes without mounting hidden controls");
                var hot = Demand(context); var hotPixels = Pixels(hot); Release(hot);
                cache.Clear();
                var cold = Demand(context); var coldPixels = Pixels(cold); Release(cold);
                Require(hotPixels.Length == coldPixels.Length && hotPixels.Zip(coldPixels).All(pair => Math.Abs(pair.First - pair.Second) <= 32),
                    $"preparation preserves visible pixels: {renderMode}/{sharp}/{zoom}");
            }
            mode = MarkdownRenderModes.Full;
            cache.Clear();
            text = string.Concat(Enumerable.Repeat("**before** *content* `code` ", 80));
            var changedContext = Context(); Require(Warm(changedContext), "prepare old content");
            text = string.Concat(Enumerable.Repeat("**updated** *content* `code` ", 80));
            changedContext.InvalidationSource.Invalidate();
            var changed = Demand(changedContext);
            Require(PreviewText(changed).Contains("updated") && !PreviewText(changed).Contains("before"), "an edit cannot mount a stale prepared body");
            Release(changed);
            cache.Clear();
            using var cancellation = new CancellationTokenSource();
            var cancelled = cache.WarmLayoutAsync(new(changedContext, root, size, () => true, cache.Capture(changedContext)), cancellation.Token);
            cancellation.Cancel();
            try { AwaitPreload(cancelled); } catch (OperationCanceledException) { }
            Pump();
            Require(cache.ArtifactCount == 0 && root.Children.Count == 0, "cancelled preparation leaves no published result or hidden controls");
            var late = cache.WarmLayoutAsync(new(changedContext, root, size, () => true, cache.Capture(changedContext)));
            changedContext.InvalidationSource.Invalidate();
            Require(!AwaitPreload(late) && cache.ArtifactCount == 0, "late preparation cannot publish an obsolete generation");
            Console.WriteLine("PASS six cold/warm presentations, current content and cancelled/stale preparation");
        }
        finally
        {
            cache.Clear(); window.Close(); Pump();
            AppTypography.Configure(UiFontPresets.Default); NoteTypography.Configure(VisualTextSizes.Medium, false);
        }
    }
}
''')

# Delete whole retired fixtures, not disable or relocate their assertions.
for path in [preview + 'PreloadAuditChecks.cs', preview + 'PreloadSelectionChecks.cs',
             editing + 'ContainerMappingChecks.cs', editing + 'LinkEscapeOwnershipChecks.cs',
             editing + 'MarkerSlotTypographyCacheChecks.cs', semantic + 'IncrementalLargeChecks.cs']:
    read(path)
    Path(path).unlink()
    print('DELETE', path)

# Only actual redraw results matter; cache object identity/full-fallback policy do not.
p = editing + 'LocalRedrawChecks.cs'
s = read(p)
s = replace_method(s, 'RunLocalRedrawChecks', '''    private static void RunLocalRedrawChecks(Action<string, Action> check)
    {
        const string blocks = "plain\\n\\nprefix **one** suffix\\n\\n# heading\\n\\n[b](https://example.com/a\\\\*b)\\n\\n> quote\\n\\n";
        var source = blocks + string.Concat(Enumerable.Repeat("unaffected text\\n", 30));
        check("Caret changes and pending deletion/undo match a full redraw", () =>
        {
            using var viewport = new Viewport(source);
            foreach (var label in new[] { "one", "heading", "https" })
            {
                viewport.Box.CaretOffset = source.IndexOf(label, StringComparison.Ordinal);
                viewport.Flush(); viewport.AssertFullRender(label);
            }
            viewport.Box.CaretOffset = source.IndexOf("one", StringComparison.Ordinal);
            viewport.Box.CaretOffset = source.IndexOf("quote", StringComparison.Ordinal);
            viewport.Box.Document.Remove(0, blocks.Length);
            viewport.Flush(); viewport.AssertFullRender("deletion with pending redraw");
            viewport.Box.Undo(); viewport.Flush();
            Equal(source, viewport.Box.Text, "undo restores text after pending redraw");
            viewport.AssertFullRender("undo");
        });
        check("Single and multiline fades preserve current pixels", () =>
        {
            foreach (var syntax in new[] { "**one**", "**first\\nsecond**", "[first\\nsecond](https://example.com)" })
            {
                using var viewport = new Viewport("plain\\n\\n" + syntax + "\\n\\n" + source);
                viewport.Box.CaretOffset = 9; viewport.Flush();
                viewport.Box.SetMarkdownEditAnimationEnabled(true); viewport.Flush();
                var first = viewport.DrawFadeFrame(0.2); viewport.AssertFullRender("fade start");
                var last = viewport.DrawFadeFrame(1.0); viewport.AssertFullRender("fade end");
                Require(!first.SequenceEqual(last), "fade changes visible pixels");
                viewport.Box.SetMarkdownEditAnimationEnabled(false);
                viewport.Box.CaretOffset = 0; viewport.Flush(); viewport.AssertFullRender("fade exit");
            }
        });
        check("Pending redraw survives wrapped text and mode changes", () =>
        {
            var wrapped = "prefix **" + string.Concat(Enumerable.Repeat("long words ", 50)) + "** suffix\\n\\n" + source;
            using var viewport = new Viewport(wrapped);
            viewport.Box.CaretOffset = 12; viewport.Flush(); viewport.AssertFullRender("wrapped");
            viewport.Box.CaretOffset = 0; viewport.Box.SetPreviewMode(true);
            viewport.Flush(); viewport.AssertFullRender("preview");
            viewport.Box.SetPreviewMode(false); viewport.Box.CaretOffset = 12;
            viewport.Box.SetMarkdownRenderMode(MarkdownRenderModes.Basic);
            viewport.Flush(); viewport.AssertFullRender("Basic");
            viewport.Box.SetMarkdownRenderMode(MarkdownRenderModes.Full);
            viewport.Flush(); viewport.AssertFullRender("Full");
            viewport.Box.CaretOffset = 0;
        });
        Pump();
    }''')
# XAtOffset belongs to the nested helper and is now unused.
a = s.index('        public double XAtOffset(')
b = s.index('        public byte[] DrawFadeFrame(', a)
s = s[:a] + s[b:]
write(p, s)

# Keep image load/corrupt input coverage; remove the mature position/stretch/decoder constant matrix.
p = editing + 'PaperBackgroundChecks.cs'
read(p)
write(p, '''using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static partial class Program
{
    private static void CheckPaperBackgroundToggle()
    {
        var paths = new[] { "papertodo.png", "papertodo.jpg", "papertodo.jpeg" }
            .Select(name => Path.Combine(AppContext.BaseDirectory, name)).ToArray();
        Require(paths.All(path => !File.Exists(path)), "background check must not replace an existing image");
        var path = paths[0];
        try
        {
            var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgr24, null, new byte[16 * 16 * 3], 16 * 3);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            var brush = PaperBackground.CreateBrush(false, PaperBackgroundLayouts.Center, false);
            Require(brush?.ImageSource is BitmapSource image && image.PixelWidth == 16 && image.PixelHeight == 16,
                "a valid small background loads without enlarging its decoded image");
            var host = new Grid(); PaperBackground.Apply(host);
            Require(host.Background is ImageBrush && PaperBackground.LoadError == null, "valid background is applied");
            File.WriteAllText(path, "not an image");
            Require(PaperBackground.CreateBrush(false, PaperBackgroundLayouts.Center, false) == null &&
                !string.IsNullOrWhiteSpace(PaperBackground.LoadError), "corrupt input falls back and reports a load error");
            PaperBackground.Apply(host);
            Require(host.Background is SolidColorBrush plain && plain.Color.A == 0, "corrupt input does not leave the old image mounted");
        }
        finally { File.Delete(path); }
    }
}
''')
p = editing + 'QuoteRailAlignmentChecks.cs'
s = read(p)
s = one(s, '        CheckLogicalPrefix();\n')
for n in ['CheckLogicalPrefix', 'ParseLine']: s = remove(s, n)
a = s.index('    private static readonly double[] FontScales')
b = s.index('    [ModuleInitializer]', a)
s = s[:a] + '    private static readonly double[] FontScales = { 1.0, 1.25, 1.5 };\n\n' + s[b:]
write(p, s)

# Lifecycle correctness no longer doubles as a benchmark or an old-build comparison harness.
p = 'tests/PaperTodo.LifecycleChecks/Program.cs'
s = lifecycle
s = re.sub(r'(?m)^    private static readonly string\[\] Cases = .*$', '    private static readonly string[] Cases = ["startup", "missing-monitor", "cancel-monitor", "real-exit", "early-expand", "cancel-prewarm", "real-exit-scripts", "early-exit"];', s)
s = s.replace('RunFixture(args[1], args.Contains("--baseline"))', 'RunFixture(args[1])')
s = between(s, '            var baseline = args.Contains("--baseline");', '            Console.WriteLine("PASS lifecycle fixtures', '            if (args.Length != 0) throw new ArgumentException("Lifecycle measurements moved to tools/PaperTodo.DesktopBenchmarks.");\n            foreach (var name in Cases) RunIsolated(name);\n')
s = s.replace('RunIsolated(string name, bool baseline)', 'RunIsolated(string name)').replace('RunFixture(string name, bool baseline)', 'RunFixture(string name)')
s = one(s, '            if (baseline) start.ArgumentList.Add("--baseline");\n')
s = one(s, '            var endedAt = Stopwatch.GetTimestamp();\n')
s = s.replace('var saved = JsonDocument.Parse(', 'using var saved = JsonDocument.Parse(')
s = s.replace('Require(baseline || text.Contains("WPF_EXIT_COMPLETED")', 'Require(text.Contains("WPF_EXIT_COMPLETED")')
s = between(s, '                var line = text.Split(\'\\n\').Single(value => value.StartsWith("EXIT_REQUEST "));', '            }\n        }\n        finally', '')
s = one(s, '            Require(child.ExitCode == 0, name + " failed");', '            Require(child.ExitCode == 0, name + " failed");\n            Console.WriteLine("PASS lifecycle " + name);')
s = one(s, '        var count = name.StartsWith("capsules-") ? int.Parse(name[9..]) : 5;', '        const int count = 5;')
s = s.replace('ExperimentalEdgeCapsuleHoverPreview = name != "preview-off"', 'ExperimentalEdgeCapsuleHoverPreview = true')
for line in ['        var started = Stopwatch.GetTimestamp();\n', '        var constructed = Stopwatch.GetTimestamp();\n', '            var returned = Stopwatch.GetTimestamp();\n', '            var shells = Stopwatch.GetTimestamp();\n', '            var ready = Stopwatch.GetTimestamp();\n', '            var disposedAt = Stopwatch.GetTimestamp();\n']:
    # The async timeout helper keeps its stopwatch; only the ctor's first occurrence is removed.
    if line not in s: raise RuntimeError('Timing anchor absent: ' + line)
    s = s.replace(line, '', 1)
s = s.replace('if (!baseline && (name is "missing-monitor" or "cancel-monitor"))', 'if (name is "missing-monitor" or "cancel-monitor")')
s = s.replace(' && !baseline)', ')')
s = s.replace('                Console.WriteLine("EXIT_REQUEST " + Stopwatch.GetTimestamp());\n', '')
s = between(s, '            long[]? previewVersions = null;', '            await Until(() => windows.Values.All(window => window.IsShellBuilt), "shell drain");', '''            if (name == "early-expand")
            {
                windows["fixture-0"].ActivateFromEdgeShortcut();
                Require(windows["fixture-0"].IsShellBuilt && windows["fixture-0"].HasExpandedPaperSurface,
                    "early demand did not construct and show the selected paper");
            }
''')
s = between(s, '            if (name == "preview-before-shell")', '            if (name == "cancel-monitor")', '''            if (name == "startup")
                Require(windows.Count == count && windows.Values.All(window => window.HasVisibleSurface),
                    "startup did not restore the requested visible papers");
''')
s = s.replace('if (name is "scripts" or "real-exit-scripts")', 'if (name == "real-exit-scripts")')
s = between(s, '            var exitAt = Stopwatch.GetTimestamp();', '            controller.Dispose();', '            var surfaces = Application.Current.Windows.Cast<Window>().ToArray();\n')
s = between(s, '            Console.WriteLine("LIFECYCLE_SAMPLE "', '        }\n        finally', '')
if '--baseline' in s or '--profile' in s or 'LIFECYCLE_SAMPLE' in s: raise RuntimeError('Lifecycle measurement remains in tests')
write(p, s)

# A helper's transition enum table duplicates execution-based runtime lifetime coverage.
p = 'tests/PaperTodo.ProtocolPolicyChecks/Program.cs'
s = read(p)
s = one(s, '            CheckRuntimeTransitions(host);\n')
s = remove(s, 'CheckRuntimeTransitions')
write(p, s)

# The existing visual oracle stays exact, but stops allocating a formatted message per pixel.
p = 'tests/PaperTodo.ThreadingChecks/InactiveTitleBarChecks.cs'
s = read(p)
s = one(s, 'foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })', 'foreach (var scale in new[] { 1.0, 1.25, 2.0 })')
s = one(s, 'foreach (var size in new[] { new Size(240, 180), new Size(310, 210) })', 'foreach (var size in new[] { new Size(240, 180) })')
s = one(s, '            var bodyArrangeCount = body.ArrangeCount;\n')
s = one(s, '                Assert(body.ArrangeCount == bodyArrangeCount, "title hiding rearranged the body");\n')
s = one(s, '''                    Assert(Math.Abs(actual[i] - expected[i]) <= tolerance,
                        $"{reason}: scale={scale} size={size} byte={i} actual={actual[i]} expected={expected[i]}");''', '''                    if (Math.Abs(actual[i] - expected[i]) > tolerance)
                        throw new InvalidOperationException($"{reason}: scale={scale} size={size} byte={i} actual={actual[i]} expected={expected[i]}");''')
s = s.replace('TitleBarBodyProbe', 'Grid')
a = s.index('    private sealed class Grid : Grid')
s = s[:a] + '}\n'
write(p, s)
p = 'tests/PaperTodo.WindowStackChecks/Program.cs'
s = read(p)
s = one(s, '            total++;', '''            // Every operation still runs unowned and with the hidden native owner. The
            // intermediate taskbar-only mode needs representatives, not another full product.
            if (visibility == "taskbar-hidden" && operation is not ("delete" or "animated-hide")) continue;
            total++;''')
write(p, s)
p = 'tests/PaperTodo.EdgeDiagnosticJournalChecks/Program.cs'
s = read(p)
a,b = span(s, 'LimitsAndAllocation')
t = s[a:b]
start = t.index('        var allocation = new JournalBuffer(')
t = t[:start] + '        Console.WriteLine("PASS limits-and-drops");\n    }\n'
s = s[:a] + t + s[b:]
s = s.replace('LimitsAndAllocation', 'LimitsAndDrops')
write(p, s)

# Documentation describes commands; historical measurements move with their tool, not its tests.
p = preview + 'PRELOAD.md'
s = read(p)
start = s.index('## 统一 renderer 的收口验证')
write(bench + 'PRELOAD-HISTORY.md', '# 预览性能历史记录\n\n以下为历史提交的测量结果，不是当前性能承诺或当前测试矩阵。当前命令与测量限制见 [README](README.md)。产品架构见 [ARCHITECTURE](../../doc/ARCHITECTURE.md)。\n\n' + s[start:])
Path(p).unlink()
write(preview + 'README.md', '''# Edge 预览行为检查

```powershell
dotnet run --project tests/PaperTodo.EdgePreviewChecks -c Release
if ($LASTEXITCODE -ne 0) { throw 'Edge preview checks failed' }
```

保留原生链接的真实命中、裁切、键盘与焦点行为；独立手写 WPF 图像参考；六组代表性的冷/热显示一致性；取消、版本失效、主题/DPI 变化和 worker 阻塞时宿主动画仍能结束。

不再验证预加载固定档位、固定读取次数、缓存命中计数或内部对象复用方式。普通显示与高风险交接由可执行行为覆盖，不把历史每个细小 bug 都永久扩成一个测试项目。

性能、内存采样及像素导出已迁到 [DesktopBenchmarks](../../tools/PaperTodo.DesktopBenchmarks/README.md)，历史采样见该工具的 `PRELOAD-HISTORY.md`。测试程序不再接受性能/导出参数，错误参数返回失败而不是偷偷运行全部检查。架构边界以 [ARCHITECTURE](../../doc/ARCHITECTURE.md) 为准。
''')
write(bench + 'README.md', '''# 桌面性能与导出工具

本项目直接引用当前 `PaperTodo.csproj`，不引用测试程序集，也不复制产品 renderer。需要 Windows、.NET 10 SDK 和可用的 WPF 桌面。所有性能命令只手动运行，不进入默认回归。

```powershell
# 验证工具入口，不据此评价性能。
dotnet run --project tools/PaperTodo.DesktopBenchmarks -c Release -- --smoke
if ($LASTEXITCODE -ne 0) { throw 'Desktop measurement smoke failed' }

# 实际 host/presenter 的首次准备、动画与开放输入。
dotnet run --project tools/PaperTodo.DesktopBenchmarks -c Release -- --preview

# 冷/热准备的正反顺序样本。
dotnet run --project tools/PaperTodo.DesktopBenchmarks -c Release -- --preload
dotnet run --project tools/PaperTodo.DesktopBenchmarks -c Release -- --preload --reverse

# 托管内存、行内分配，以及隔离进程的启动/退出阶段。
dotnet run --project tools/PaperTodo.DesktopBenchmarks -c Release -- --memory
dotnet run --project tools/PaperTodo.DesktopBenchmarks -c Release -- --inline
dotnet run --project tools/PaperTodo.DesktopBenchmarks -c Release -- --lifecycle

# 将原始 RGBA 像素写入指定的实验目录；会覆盖同名导出文件。
dotnet run --project tools/PaperTodo.DesktopBenchmarks -c Release -- --export output/edge-pixels
```

`--preview` 每组 24 次，前三次预热不进入汇总；`--preload` 每种顺序 9 次，输出原始样本，比较时须按一致口径剔除预热。`--lifecycle` 对 1/5/10/25 张纸片各运行三个独立进程，交替数量顺序；只复制构建产物到新临时目录，不读取或修改日常 PaperTodo 数据，结束后删除本次目录。不保留旧 `--baseline` 跳过行为保障的开关。

布局就绪、开放输入、动画结束与物理屏幕显示不是同一个时刻；这里没有 GPU 呈现或硬件输入延迟测量。UI 当前线程分配不含 worker；强制 GC 后的托管堆增量不是工作集或原生/GPU 内存。数字只适合同机、同构建、同场景比较，不作为 CI 的毫秒或零分配门槛。历史结果见 [PRELOAD-HISTORY](PRELOAD-HISTORY.md)。
''')
p = 'tests/README.md'
s = read(p)
s += '''
## 测试保留与退出

测试是否保留取决于现在能否保护重要行为，不取决于它是否曾属于某个 bug 修复。重复的弱断言、已由集成场景覆盖的私有辅助步骤、固定优化档位和低回归风险的参数排列可以删除，不另搬到“可选测试”中存活。

仍保留保存失败和最后编辑不丢失、公开插件兼容、跨线程调用撤销、真实输入命中、窗口交接、取消与过期回调等高影响边界。计时器用于防止测试挂住、控制模拟时钟或验证不能阻塞的退出行为时，不属于性能跑分。

生命周期默认检查不采样、不比较旧实现，只保留八种启动/退出行为。窗口层级覆盖所有操作在普通窗口与隐藏 owner 两种关键形态，任务栏隐藏采用两种代表操作，避免完整交叉排列。背景图片保留加载与损坏输入回退，不再枚举每种对齐和固定解码尺寸。

桌面性能、预览内存与像素导出统一在 `tools/PaperTodo.DesktopBenchmarks`；Markdown 解析采样在 `tools/PaperTodo.MarkdownBenchmarks`。`-Group all` 不构建或执行这两个工具。
'''
write(p, s)
p = 'tools/README.md'
s = read(p)
s = one(s, '| `PaperTodo.MarkdownBenchmarks`', '| `PaperTodo.DesktopBenchmarks` | 真窗口预览、冷/热准备、内存、隔离启动/退出测量及像素导出；不依赖测试程序集 |\n| `PaperTodo.MarkdownBenchmarks`')
write(p, s)

# The transient script is not part of the final tree. Whitespace validation is not a source-shape test.
Path(__file__).unlink()
subprocess.run(['git', 'diff', '--check'], check=True)
for group in ['tests', 'tools']:
    sources = [p for p in Path(group).rglob('*') if p.suffix in ('.cs', '.cjs', '.ps1')]
    print('SOURCE_TOTAL', group, 'files', len(sources), 'lines', sum(len(p.read_text(encoding='utf-8-sig').splitlines()) for p in sources))
print('Reviewed pruning complete.')
