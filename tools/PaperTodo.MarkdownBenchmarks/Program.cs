using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ICSharpCode.AvalonEdit.Document;
using PaperTodo;

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("dotnet run --project tools/PaperTodo.MarkdownBenchmarks -c Release -- [--iterations 21]");
    return 0;
}
var iterations = 21;
if (args.Length != 0 &&
    (args.Length != 2 || args[0] != "--iterations" || !int.TryParse(args[1], out iterations) || iterations is < 1 or > 1000))
{
    Console.Error.WriteLine("Expected --iterations followed by a number from 1 to 1000. Use --help for usage.");
    return 2;
}
#if DEBUG
Console.WriteLine("WARNING: Debug build; use Release for performance comparisons.");
#else
Console.WriteLine("Configuration: Release");
#endif
Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"Samples: {iterations}; warmups: 2; allocations: current thread; no pass/fail time threshold");

try
{
    foreach (var (name, source) in new[]
    {
        ("Plain10k", BuildSource(10_000, false)),
        ("Plain60k", BuildSource(60_000, false)),
        ("Dense98k", BuildSource(98_000, true)),
        ("Escapes30k", string.Concat(Enumerable.Repeat("\\*", 15_000)))
    })
    {
        Measure(name + ".Parse", source.Length, () => MarkdownSemanticSnapshot.Parse(source));
        if (name == "Escapes30k") continue;
        var offset = source.IndexOf(name.StartsWith("Dense") ? "item " : "ordinary", source.Length / 2, StringComparison.Ordinal);
        if (offset < 0) throw new InvalidOperationException("Edit fixture has no probe.");
        using var edit = new EditSample(source, offset + 2, "Z");
        Measure(name + ".Edit", source.Length, edit.Apply);
    }
    var dense = BuildSource(98_000, true);
    var fenceAt = dense.IndexOf("# Heading ", dense.Length / 3, StringComparison.Ordinal);
    using var fence = new EditSample(dense, fenceAt, "```text\n");
    Measure("Dense98k.FenceEdit", dense.Length, fence.Apply);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

void Measure(string name, int chars, Func<MarkdownSemanticSnapshot> action)
{
    for (var i = 0; i < 2; i++) GC.KeepAlive(action());
    var elapsed = new double[iterations];
    var allocated = new long[iterations];
    for (var i = 0; i < iterations; i++)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var result = action();
        elapsed[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        allocated[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(result);
    }
    Array.Sort(elapsed);
    Array.Sort(allocated);
    var p95 = Math.Clamp((int)Math.Ceiling(iterations * 0.95) - 1, 0, iterations - 1);
    Console.WriteLine($"{name,-24} chars={chars} p50={elapsed[iterations / 2]:F3}ms p95={elapsed[p95]:F3}ms alloc-p50={allocated[iterations / 2] / 1024d:F1}KiB");
}

static string BuildSource(int chars, bool dense)
{
    var text = new StringBuilder(chars + 200);
    for (var i = 0; text.Length < chars; i++)
    {
        if (!dense)
        {
            text.Append("Paragraph ").Append(i).Append(" ordinary words for local Markdown editing.\n\n");
            continue;
        }
        text.Append("# Heading ").Append(i).Append('\n');
        text.Append("- [ ] item **bold** https://example.com/").Append(i).Append('\n');
        text.Append("> quote `code` ~~gone~~\n");
        if (i % 20 == 0) text.Append("```csharp\nvar x = 1;\n```\n");
    }
    return text.ToString();
}

// Alternate a real insertion/removal. Initial parsing and fixture construction are not timed.
// The product decides whether an edit is local or full; this tool does not clone that decision.
sealed class EditSample : IDisposable
{
    private readonly int _offset;
    private readonly string _insert;
    private readonly TextDocument _document;
    private readonly MarkdownSemanticDocument _semantics;
    private bool _edited;

    public EditSample(string source, int offset, string insert)
    {
        if (offset < 0 || offset > source.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        _offset = offset;
        _insert = insert;
        _document = new TextDocument(source);
        _semantics = new MarkdownSemanticDocument(_document);
    }

    public MarkdownSemanticSnapshot Apply()
    {
        if (_edited) _document.Remove(_offset, _insert.Length);
        else _document.Insert(_offset, _insert);
        _edited = !_edited;
        return _semantics.TryGetCurrent(out var snapshot)
            ? snapshot
            : throw new InvalidOperationException("The edited document did not publish a snapshot.");
    }

    public void Dispose() => _semantics.Dispose();
}
