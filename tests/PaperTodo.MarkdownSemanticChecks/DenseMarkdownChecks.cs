using System.Runtime.CompilerServices;
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
            ("dense fence edit", source.Insert(fence, "```text\n"))
        })
        {
            MarkdownEditBehavior.AssertEquivalent(MarkdownSemanticSnapshot.Parse(edited),
                MarkdownEditBehavior.ReadAfterEdit(source, edited), name);
            Console.WriteLine("PASS " + name);
        }
    }
}
