using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PaperTodo;

internal static class MenuOpeningBoundaryChecks
{
    internal static void Run(AppController controller)
    {
        var saved = (controller.State.PaperSkin, controller.State.MatchAuxiliaryMaterialStrength);
        var failures = new List<string>();
        try
        {
            controller.State.PaperSkin = PaperSkins.Acrylic;
            controller.State.MatchAuxiliaryMaterialStrength = true;
            Theme.Invalidate();
            Program.Assert(MaterialMenuOpening.NeedsBackground, "menu boundary checks require material preparation");
            foreach (var kind in new[] { "hide-window", "collapse-anchor", "hide-show", "replace-anchor", "valid-anchor", "unanchored" })
            {
                try { CheckRequest(kind); Console.WriteLine("PASS pending menu boundary: " + kind); }
                catch (Exception ex) { failures.Add(kind + ": " + ex.Message); Console.WriteLine("FAIL pending menu boundary: " + kind + ": " + ex.Message); }
            }
        }
        finally
        {
            (controller.State.PaperSkin, controller.State.MatchAuxiliaryMaterialStrength) = saved;
            Theme.Invalidate();
        }
        Program.Assert(failures.Count == 0, string.Join("; ", failures));
    }

    private static void CheckRequest(string kind)
    {
        var anchor = new Border { Width = 80, Height = 40 };
        var replacement = new Border { Width = 80, Height = 40 };
        var panel = new StackPanel();
        panel.Children.Add(anchor); panel.Children.Add(replacement);
        var window = new Window { Width = 220, Height = 160, Left = 80, Top = 80,
            ShowInTaskbar = false, ShowActivated = false, Content = panel };
        var captures = new List<TaskCompletionSource<DesktopBackgroundCapture.Frame?>>();
        var probe = new Probe(kind == "unanchored" ? null : anchor, () =>
        {
            var capture = new TaskCompletionSource<DesktopBackgroundCapture.Frame?>(TaskCreationOptions.RunContinuationsAsynchronously);
            captures.Add(capture);
            return capture.Task; // A native readback already in progress may ignore cancellation.
        });
        try
        {
            window.Show(); Program.Pump();
            Program.Assert(anchor.IsLoaded && anchor.IsVisible, "real visible WPF target is ready");
            probe.Request(true);
            Until(() => captures.Count == 1, "pending native readback started");
            Program.Assert(!probe.Open && probe.Opening.IsPending, "opening really awaits capture");
            switch (kind)
            {
                case "hide-window": window.Hide(); break;
                case "collapse-anchor": anchor.Visibility = Visibility.Collapsed; break;
                case "hide-show": window.Hide(); window.Show(); break;
                case "replace-anchor": probe.Target = replacement; break;
            }
            captures[0].SetResult(Frame());
            Until(() => !probe.Opening.IsPending, "late capture delivery is finished or revoked");
            Program.Pump();
            var valid = kind is "valid-anchor" or "unanchored";
            Program.Assert(probe.Open == valid,
                valid ? "valid request must open" : "obsolete request must not open after its target lifetime changed");
            probe.Request(false);
            window.Show(); anchor.Visibility = Visibility.Visible;
            probe.Target = anchor;
            probe.Request(true);
            Until(() => captures.Count == 2, "next valid request can still acquire");
            captures[1].SetResult(Frame());
            Until(() => probe.Open, "next valid request opens normally");
        }
        finally
        {
            probe.Request(false);
            foreach (var capture in captures) capture.TrySetResult(null);
            probe.Surface.PrepareMenuBackground(null, false);
            window.Close(); Program.Pump();
        }
    }

    private static DesktopBackgroundCapture.Frame Frame() =>
        new(new BackgroundCaptureLayout.Scene(new Int32Rect(0, 0, 64, 32), 64, 32), new byte[64 * 32 * 4]);

    private sealed class Probe : FrameworkElement
    {
        private static readonly DependencyProperty OpenProperty = DependencyProperty.Register("Open", typeof(bool), typeof(Probe),
            new FrameworkPropertyMetadata(false, null, (d, value) => ((Probe)d).Opening.Coerce((bool)value)));
        internal readonly SkinBorder Surface = new() { IsMenu = true, Width = 240, Height = 160 };
        internal readonly MaterialMenuOpening Opening;
        internal UIElement? Target;
        internal bool Open => (bool)GetValue(OpenProperty);
        internal Probe(UIElement? target, Func<Task<DesktopBackgroundCapture.Frame?>> capture)
        {
            Target = target;
            Opening = new MaterialMenuOpening(this, OpenProperty, () => Surface, () => Target,
                () => PlacementMode.MousePoint, (_, _) => capture());
        }
        internal void Request(bool open) => SetCurrentValue(OpenProperty, open);
    }

    private static void Until(Func<bool> predicate, string reason)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate() && watch.Elapsed < TimeSpan.FromSeconds(3)) { Program.Pump(); Thread.Sleep(1); }
        Program.Assert(predicate(), reason);
    }
}
