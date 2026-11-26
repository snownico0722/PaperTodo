using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PaperTodo;
using PaperTodo.Plugin;

internal static class PluginCapsulePreviewChecks
{
    public static async Task Run(AppController controller, PaperWindow window)
    {
        window.EnsureShellBuilt();
        var paper = window.EdgeCapsulePreviewPaper;
        // Exercise the compatibility preview itself without installing any third-party plugin.
        paper.BodyProviderId = "test.no-dedicated-mini";
        var context = (EdgeCapsulePreviewContext)typeof(PaperWindow)
            .GetMethod("CreateEdgeCapsulePreviewContext", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null)!;
        var owner = Guid.NewGuid();
        var baseline = window.DescribePluginEdgeCapsulePreview(context);
        var view = baseline.CreateContent(baseline.Size);
        var host = new Window { Content = view, ShowActivated = false, ShowInTaskbar = false,
            Width = baseline.Size.WidthDip, Height = baseline.Size.HeightDip };
        host.Show();
        try
        {
            await Flush();
            Require(HasText(context.Title), "missing initial default title");
            controller.SetWorkspaceCapsuleOverride(owner, paper.Id, Capsule("overridden", 320));
            await Flush();
            var wide = window.DescribePluginEdgeCapsulePreview(context).Size;
            Require(wide.WidthDip == 360 && HasText("overridden"),
                "default preview did not switch to the wide workspace capsule");

            controller.SetWorkspaceCapsuleOverride(owner, paper.Id, Capsule("template", 40),
                _ => new TextBlock { Text = "native" });
            await Flush();
            Require(window.DescribePluginEdgeCapsulePreview(context).Size.WidthDip == 220 &&
                Descendants(view).OfType<Rectangle>().Any(shape =>
                    shape.Fill is VisualBrush { Visual: TextBlock { Text: "native" } }),
                "native mirror and preview width did not follow the same override");

            controller.SetWorkspaceCapsuleOverride(owner, paper.Id, null);
            await Flush();
            Require(window.DescribePluginEdgeCapsulePreview(context).Size == baseline.Size &&
                HasText(context.Title), "clearing a live override did not restore the default card");

            // A body presentation beneath the override must survive publication and removal.
            typeof(PaperWindow).GetMethod("SetPluginCapsulePresentation",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [Capsule("body", 40)]);
            controller.SetWorkspaceCapsuleOverride(owner, paper.Id, Capsule("replacement", 320));
            await Flush();
            Require(HasText("replacement") && window.DescribePluginEdgeCapsulePreview(context).Size == wide,
                "body presentation masked the workspace override");
            controller.ClearWorkspaceCapsuleOverrides(owner);
            await Flush();
            Require(HasText("body") && window.DescribePluginEdgeCapsulePreview(context).Size.WidthDip == 220,
                "releasing the override did not restore the body presentation and its width");

            Console.WriteLine("PASS plugin fallback Mini follows the effective capsule and restores its content");
        }
        finally { host.Close(); }

        bool HasText(string text) => Descendants(view).OfType<TextBlock>().Any(label => label.Text == text);
    }

    private static PaperCapsulePresentation Capsule(string text, double width) => new()
    {
        PreferredWidth = width,
        Components = [new PaperCapsuleComponent { Kind = PaperCapsuleComponentKind.Text, Text = text }]
    };

    private static async Task Flush() =>
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject element)
    {
        yield return element;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        foreach (var child in Descendants(VisualTreeHelper.GetChild(element, i))) yield return child;
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
