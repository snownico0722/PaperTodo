using System.Runtime.CompilerServices;
using System.Text;

namespace PaperTodo;

internal static class FenceWindowChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        CheckDeletingClosingFenceReachesEnd();
        CheckNewTildeFenceExpands();
        CheckFourBacktickChangePropagates();
        CheckRemovingLineBreakDestroysFence();
        CheckInsertingLineBreakCreatesFence();
        CheckNewFenceConvergesInsideExistingFence();
        CheckInlineBackticksRemainLiteral();
    }

    private static void CheckDeletingClosingFenceReachesEnd()
    {
        var builder = new StringBuilder();
        AppendPlain(builder, "prefix", 260);
        builder.Append("```text\n");
        AppendPlain(builder, "inside", 620);
        var closing = builder.Length;
        builder.Append("```\n");
        AppendPlain(builder, "tail", 420);

        var oldSource = builder.ToString();
        var newSource = oldSource.Remove(closing, 3);
        AssertEditMatchesFull(
            oldSource,
            newSource,
            "deleted closing fence");
    }

    private static void CheckNewTildeFenceExpands()
    {
        var builder = new StringBuilder();
        AppendPlain(builder, "prefix", 240);
        var insertAt = builder.Length;
        builder.Append("anchor\n");
        AppendPlain(builder, "body", 520);
        builder.Append("~~~\n");
        AppendPlain(builder, "tail", 260);

        var oldSource = builder.ToString();
        var newSource = oldSource.Insert(insertAt, "~~~text\n");
        AssertEditMatchesFull(
            oldSource,
            newSource,
            "new tilde fence");
    }

    private static void CheckFourBacktickChangePropagates()
    {
        var builder = new StringBuilder();
        AppendPlain(builder, "prefix", 220);
        var opening = builder.Length;
        builder.Append("````text\n");
        AppendPlain(builder, "inside-a", 180);
        builder.Append("```\n");
        AppendPlain(builder, "inside-b", 180);
        builder.Append("````\n");
        AppendPlain(builder, "tail", 260);

        var oldSource = builder.ToString();
        var newSource = oldSource.Remove(opening, 1);
        AssertEditMatchesFull(
            oldSource,
            newSource,
            "four-backtick opener shortened");
    }

    private static void CheckRemovingLineBreakDestroysFence()
    {
        var builder = new StringBuilder();
        AppendPlain(builder, "prefix", 220);
        builder.Append("lead");
        var lineBreak = builder.Length;
        builder.Append("\n```text\n");
        AppendPlain(builder, "body", 420);
        builder.Append("```\n");
        AppendPlain(builder, "tail", 320);

        var oldSource = builder.ToString();
        var newSource = oldSource.Remove(lineBreak, 1);
        AssertEditMatchesFull(
            oldSource,
            newSource,
            "removed line break destroys fence opener");
    }

    private static void CheckInsertingLineBreakCreatesFence()
    {
        var builder = new StringBuilder();
        AppendPlain(builder, "prefix", 220);
        builder.Append("lead");
        var insertAt = builder.Length;
        builder.Append("```text\n");
        AppendPlain(builder, "body", 420);
        builder.Append("```\n");
        AppendPlain(builder, "tail", 320);

        var oldSource = builder.ToString();
        var newSource = oldSource.Insert(insertAt, "\n");
        AssertEditMatchesFull(
            oldSource,
            newSource,
            "inserted line break creates fence opener");
    }

    private static void CheckNewFenceConvergesInsideExistingFence()
    {
        var builder = new StringBuilder();
        AppendPlain(builder, "prefix", 220);
        var insertAt = builder.Length;
        builder.Append("anchor\n");
        AppendPlain(builder, "between", 220);
        builder.Append("```csharp\n");
        AppendPlain(builder, "existing-fence", 180);
        builder.Append("```\n");
        AppendPlain(builder, "tail", 220);

        var oldSource = builder.ToString();
        var newSource = oldSource.Insert(insertAt, "```text\n");
        AssertEditMatchesFull(
            oldSource,
            newSource,
            "new fence convergence inside existing fence");
    }

    private static void CheckInlineBackticksRemainLiteral()
    {
        var builder = new StringBuilder();
        AppendPlain(builder, "plain", 700);
        builder.Append("paragraph `` inline marker remains ordinary text\n\n");
        AppendPlain(builder, "after", 700);

        var oldSource = builder.ToString();
        var marker = oldSource.IndexOf("`` inline", StringComparison.Ordinal);
        var newSource = oldSource.Insert(marker, "`");
        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource),
            MarkdownEditBehavior.ReadAfterEdit(oldSource, newSource), "inline backticks");
        Console.WriteLine("PASS inline triple backticks preserve literal text");
    }


    private static void AssertEditMatchesFull(string oldSource, string newSource, string name)
    {
        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource),
            MarkdownEditBehavior.ReadAfterEdit(oldSource, newSource), name);
        Console.WriteLine($"PASS {name}");
    }

    private static void AssertEquivalent(
        MarkdownSemanticSnapshot expected,
        MarkdownSemanticSnapshot actual,
        string name)
    {
        if (expected.LineCount != actual.LineCount ||
            !expected.Spans.SequenceEqual(actual.Spans) ||
            !expected.Links.SequenceEqual(actual.Links))
        {
            throw new InvalidOperationException($"FAIL {name}: snapshot mismatch");
        }
        for (var line = 0; line < expected.LineCount; line++)
        {
            if (!expected.GetLine(line).Equals(actual.GetLine(line)))
            {
                throw new InvalidOperationException($"FAIL {name}: line mismatch at {line}");
            }
        }
    }

    private static void AppendPlain(StringBuilder builder, string label, int count)
    {
        for (var index = 0; index < count; index++)
        {
            builder.Append(label).Append(" row ").Append(index)
                .Append(" ordinary words for local Markdown editing\n\n");
        }
    }

}
