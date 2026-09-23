using System.Runtime.CompilerServices;
using System.Text;

namespace PaperTodo;

internal static class IncrementalLargeChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        CheckLargeLocalSmoke();
    }

    private static void CheckLargeLocalSmoke()
    {
        var builder = new StringBuilder(70_000);
        var index = 0;
        while (builder.Length < 60_000)
        {
            builder.Append("Paragraph ").Append(index)
                .Append(" ordinary words for large best-effort local editing.\n\n");
            if ((index % 31) == 0)
            {
                builder.Append("```text\nfenced row\nsecond fenced row\n```\n\n");
            }
            index++;
        }

        var source = builder.ToString();
        var document = new ICSharpCode.AvalonEdit.Document.TextDocument(source);
        using var semantics = new MarkdownSemanticDocument(document);
        for (var step = 0; step < 24; step++)
        {
            var marker = $"Paragraph {50 + (step * 7)} ordinary";
            var offset = source.IndexOf(marker, StringComparison.Ordinal);
            if (offset < 0)
            {
                throw new InvalidOperationException($"FAIL large local smoke: marker '{marker}' missing");
            }
            offset += marker.Length;
            var next = source.Insert(offset, "Z");
            document.Text = next;
            if (!semantics.TryGetCurrent(out var incremental))
                throw new InvalidOperationException($"FAIL large edit step {step}: no current snapshot");
            ValidateRanges(incremental, next.Length, $"large local smoke step {step}");
            source = next;
        }

        Console.WriteLine("PASS large best-effort local edit smoke");
    }


    private static void ValidateRanges(
        MarkdownSemanticSnapshot snapshot,
        int sourceLength,
        string name)
    {
        foreach (var span in snapshot.Spans)
        {
            if (span.Start < 0 || span.End < span.Start || span.End > sourceLength)
            {
                throw new InvalidOperationException(
                    $"FAIL {name}: invalid span {span.Start}..{span.End} / {sourceLength}");
            }
        }
        foreach (var link in snapshot.Links)
        {
            if (link.Start < 0 || link.End < link.Start || link.End > sourceLength)
            {
                throw new InvalidOperationException(
                    $"FAIL {name}: invalid link {link.Start}..{link.End} / {sourceLength}");
            }
        }
    }
}
