using System.Text.Json;
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
                        Console.WriteLine("SMOKE_COLD " + JsonSerializer.Serialize(NamedPreviewMetrics(ProfileOne(text, MarkdownRenderModes.Full))));
                        Console.WriteLine("SMOKE_WARM " + JsonSerializer.Serialize(NamedPreviewMetrics(ProfileOne(text, MarkdownRenderModes.Full, "layout"))));
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
