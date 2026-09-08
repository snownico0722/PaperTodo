using System.Runtime.CompilerServices;
using PaperTodo;

internal static class LinkEscapeOwnershipChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        ExpectOwned("[b](https://example.com/a\\*b)", "destination escape");
        ExpectOwned("[b](https://example.com \"a\\*b\")", "title escape");
        ExpectNotOwned("[a\\*b](https://example.com)", "label escape remains independent");
        Console.WriteLine("PASS link escape ownership");
    }

    private static void ExpectOwned(string source, string message)
    {
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        var escape = FindEscape(snapshot);
        if (!MarkdownLinkEscapeOwnership.TryGetOwningLink(escape, snapshot, source, out _))
        {
            throw new InvalidOperationException($"FAIL link escape ownership: {message}");
        }
    }

    private static void ExpectNotOwned(string source, string message)
    {
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        var escape = FindEscape(snapshot);
        if (MarkdownLinkEscapeOwnership.TryGetOwningLink(escape, snapshot, source, out _))
        {
            throw new InvalidOperationException($"FAIL link escape ownership: {message}");
        }
    }

    private static MarkdownSemanticSpan FindEscape(MarkdownSemanticSnapshot snapshot)
    {
        foreach (var span in snapshot.Spans)
        {
            if (span.Kind == MarkdownSemanticSpanKind.EscapeMarker)
            {
                return span;
            }
        }

        throw new InvalidOperationException("FAIL link escape ownership: no escape marker");
    }
}
