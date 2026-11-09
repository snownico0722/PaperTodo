using System.Runtime.CompilerServices;
using PaperTodo;

internal static class ImageReferenceChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        Check("Image after contained unclosed fence remains semantic image", () =>
        {
            const string source = "- item\n  ```\n![image|100%](i:123)";
            var references = MarkdownImageReferences.Enumerate(source).ToArray();
            Equal(1, references.Length, "top-level image reference count");
            Equal("123", references[0].ImageId, "top-level image id");
        });

        Check("Indented-code image-looking line is not semantic image", () =>
        {
            const string source = "    ![image|100%](i:123)";
            Equal(0, MarkdownImageReferences.Enumerate(source).Count(), "indented code semantic image count");
        });

        Check("Destructive GC protection stays conservative", () =>
        {
            const string source = "    ![image|100%](i:123)";
            var protectedIds = MarkdownImageReferences.CollectImageIds(source);
            True(protectedIds.Contains("123"), "code-looking reference still protects stored image bytes");
        });

        Check("Replacing an image URL preserves its list and line formatting", () =>
        {
            const string source = "- step\r\n\r\n  ![image|65%]( i:123 ) {width=50%}  \r\n\r\n  details";
            var reference = MarkdownImageReferences.Enumerate(source).Single();
            Equal("  ![image|65%]( i:456 ) {width=50%}  ", reference.WithUrl("i:456"), "workspace remap");
            Equal(source.Replace("i:123", "./123.png"),
                MarkdownImageReferences.ReplaceForExternalMarkdown(source, _ => "./123.png"), "portable URL");
        });

        Check("External replacement does not rewrite code image text", () =>
        {
            const string source = "    ![image|100%](i:123)";
            var replaced = MarkdownImageReferences.ReplaceForExternalMarkdown(source, _ => "./123.png");
            Equal(source, replaced, "indented code export text");
        });
    }

    private static void Check(string name, Action check)
    {
        try
        {
            check();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"FAIL {name}: {ex.Message}", ex);
        }
    }

    private static void True(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
        }
    }
}
