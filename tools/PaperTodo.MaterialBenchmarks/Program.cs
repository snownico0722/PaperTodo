using System.IO;
using System.Windows;
using PaperTodo;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--help"] or ["-h"] or [])
        {
            Console.WriteLine("Material measurements:");
            Console.WriteLine("  --benchmark <json>");
            Console.WriteLine("  --drag-benchmark <json>");
            Console.WriteLine("  --drag-snapshot-timing <json>");
            Console.WriteLine("  --resize-shadow-benchmark <json>");
            Console.WriteLine("Run in Release on Windows. Drag/resize modes use real mouse input and an isolated fixture.");
            return 0;
        }

        var app = CreateApplication();
        try
        {
            if (args is ["--benchmark", var output])
            {
                using var controller = new AppController();
                MaterialBenchmarks.Run(controller, output);
                return 0;
            }
            if (args is ["--drag-benchmark", var dragOutput])
                return MaterialDragBenchmarks.RunIsolated(dragOutput);
            if (args is ["--drag-snapshot-timing", var timingOutput])
                return MaterialDragBenchmarks.RunSnapshotTimingIsolated(timingOutput);
            if (args is ["--resize-shadow-benchmark", var resizeOutput])
                return MaterialResizeBenchmarks.RunIsolated(resizeOutput);
            if (args is ["--drag-fixture", var fixtureOutput])
            {
                RequireFixture();
                using var controller = new AppController();
                MaterialDragBenchmarks.Run(controller, fixtureOutput);
                return 0;
            }
            if (args is ["--drag-snapshot-fixture", var snapshotOutput])
            {
                RequireFixture();
                using var controller = new AppController();
                MaterialDragBenchmarks.RunSnapshotTiming(controller, snapshotOutput);
                return 0;
            }
            if (args is ["--resize-shadow-fixture", var resizeFixtureOutput])
            {
                RequireFixture();
                using var controller = new AppController();
                MaterialResizeBenchmarks.Run(controller, resizeFixtureOutput);
                return 0;
            }

            Console.Error.WriteLine("Unknown arguments. Use --help.");
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            app.Shutdown();
        }
    }

    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireFixture()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, MaterialDragBenchmarks.FixtureMarker)))
            throw new InvalidOperationException("Refusing to use non-fixture data for dragging.");
    }

    private static Application CreateApplication()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var source = typeof(Program).Assembly.GetManifestResourceStream("PaperTodo.App.xaml")
            ?? throw new InvalidOperationException("Missing PaperTodo.App.xaml benchmark resource.");
        var xaml = System.Xml.Linq.XDocument.Load(source);
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new System.Xml.Linq.XElement(
            ns + "ResourceDictionary",
            xaml.Root!.Attributes().Where(a => a.IsNamespaceDeclaration),
            xaml.Root.Element(ns + "Application.Resources")!.Nodes());
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString());
        return app;
    }
}
