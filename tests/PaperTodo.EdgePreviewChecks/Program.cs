using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
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
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static IEnumerable<DependencyObject> Elements(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Elements(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static EdgeCapsuleHost NewHost(double chromeMargin = 4) => EdgeCapsuleHost.Create(new EdgeCapsuleHostOptions(
        chromeMargin, 16, 15, 2, 1, 32, 6, 4, "✓", 13, 12, FontWeights.Normal, "Close",
        Brushes.White, Brushes.Gray, Brushes.Blue, Brushes.LightGray, Brushes.Gray, Brushes.Black, Brushes.Gray,
        new FontFamily("Segoe UI"), new FontFamily("Segoe UI Symbol"), XmlLanguage.GetLanguage("en-US"), false, "edge-preview-check"));


}
