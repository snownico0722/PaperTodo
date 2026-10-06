using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PaperTodo;
using PaperTodo.Plugin;

internal static class InteractionLifetimeChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task Run(string name, PaperWindow window)
    {
        if (name == "lifetime-reminder-flash")
        {
            await ReminderFlash();
            return;
        }
        window.EnsureShellBuilt();
        var paper = (PaperData)Part(window, "_paper");
        var host = (PaperBodyHost)Part(window, "_paperBodyHost");
        var original = host.Current;
        var providerId = paper.BodyProviderId;
        var visible = paper.IsVisible;
        var runtimeVisible = (bool)Part(window, "_bodyRuntimeVisible");
        paper.BodyProviderId = "tests.reentrant-body";
        paper.IsVisible = true;
        var probe = new BodyProbe();
        Set(host, "<Current>k__BackingField", probe);
        try
        {
            if (name == "lifetime-body-visibility")
            {
                probe.Presentation = _ => host.CommitCancelDispose(false);
                window.NotifyCurrentPaperBodyVisibility(true);
                Require(probe.Disposals == 1 && probe.Visibility.Count == 0,
                    "RETIRED_VISIBILITY: a disposed body received the second visibility notification");

                probe = new BodyProbe();
                host.Attach(probe);
                probe.Presentation = shown =>
                {
                    if (!shown) return;
                    paper.IsVisible = false;
                    window.NotifyCurrentPaperBodyVisibility(false);
                };
                window.NotifyCurrentPaperBodyVisibility(true);
                Require(probe.Visibility.SequenceEqual(new[] { false }),
                    "REENTRANT_VISIBILITY: an older true notification overwrote the nested hide");
            }
            else if (name == "lifetime-body-failure")
            {
                var replacement = new BodyProbe();
                probe.CommitAction = () =>
                {
                    probe.CommitAction = null;
                    host.CommitCancelDispose(false);
                    host.Attach(replacement);
                    throw new InvalidOperationException("Failure from the retired body");
                };
                window.CommitCurrentPaperBody();
                Require(ReferenceEquals(host.Current, replacement) && replacement.Disposals == 0,
                    "STALE_BODY_FAILURE: a retired body failure replaced the new session");
            }
            else if (name == "lifetime-mini-reset")
            {
                var replacement = new BodyProbe();
                var replacementView = new Border();
                Set(window, "_pluginMiniViewProvider", probe);
                Set(window, "_pluginMiniViewVisible", true);
                Set(window, "_pluginMiniViewActive", true);
                var retired = 0;
                probe.MiniVisibility = _ =>
                {
                    // Bound faulty recursion so the baseline produces an assertion, not stack overflow.
                    if (++retired != 1) return;
                    ResetMini(window);
                    Set(window, "_pluginMiniViewProvider", replacement);
                    Set(window, "_pluginMiniView", replacementView);
                    Set(window, "_pluginMiniViewVisible", true);
                    Set(window, "_pluginMiniViewActive", true);
                };
                ResetMini(window);
                Require(retired == 1 && ReferenceEquals(Part(window, "_pluginMiniViewProvider"), replacement) &&
                    ReferenceEquals(Part(window, "_pluginMiniView"), replacementView),
                    "REENTRANT_MINI_RESET: cleanup notified the same owner twice or erased the replacement cache");
                ResetMini(window);
            }
            else throw new ArgumentException(name);
            Console.WriteLine("PASS " + name);
        }
        finally
        {
            probe.CommitAction = null;
            probe.Presentation = null;
            probe.MiniVisibility = null;
            ResetMini(window);
            Set(host, "<Current>k__BackingField", original);
            Set(window, "_bodyRuntimeVisible", runtimeVisible);
            paper.BodyProviderId = providerId;
            paper.IsVisible = visible;
        }
    }

    private static async Task ReminderFlash()
    {
        var theme = new Border { Background = Brushes.Blue };
        var border = new Border();
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Border.Background)) { Source = theme });
        var surface = new Window { Width = 160, Height = 100, ShowInTaskbar = false, ShowActivated = false, Content = border };
        try
        {
            surface.Show();
            AnimationHelper.FlashHighlight(border, Colors.Red, 50);
            Require(BindingOperations.IsDataBound(border, Border.BackgroundProperty) &&
                ReferenceEquals(border.GetAnimationBaseValue(Border.BackgroundProperty), Brushes.Blue),
                "FLASH_BASE_OWNER: reminder animation replaced the base background or removed its binding");
            AnimationHelper.FlashHighlight(border, Colors.Yellow, 80);
            theme.Background = Brushes.Lime;
            var watch = Stopwatch.StartNew();
            while (!ReferenceEquals(border.Background, Brushes.Lime))
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(3))
                    throw new InvalidOperationException("FLASH_STALE_RESTORE: completed flash restored an obsolete brush");
                await Task.Delay(10);
            }
            Require(BindingOperations.IsDataBound(border, Border.BackgroundProperty),
                "FLASH_BINDING_LOST: background binding did not survive the reminder");
            Console.WriteLine("PASS lifetime-reminder-flash: repeated real WPF clocks and intervening bound theme");
        }
        finally { surface.Close(); }
    }

    private sealed class BodyProbe : IPaperBodySession, IPaperMiniViewProvider
    {
        public FrameworkElement View { get; } = new Border();
        public Action? CommitAction;
        public Action<bool>? Presentation, MiniVisibility;
        public readonly List<bool> Visibility = new();
        public int Disposals;
        public void Commit() => CommitAction?.Invoke();
        public void Dispose() => Disposals++;
        public void OnPresentationChanged(bool visible) => Presentation?.Invoke(visible);
        public void OnVisibilityChanged(bool visible) => Visibility.Add(visible);
        public FrameworkElement CreateMiniView(PaperMiniViewContext context) => new Border();
        public void OnMiniViewVisibilityChanged(bool visible) => MiniVisibility?.Invoke(visible);
    }
    private static object Part(object value, string name) => value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static void Set(object value, string name, object? data) => value.GetType().GetField(name, Private)!.SetValue(value, data);
    private static void ResetMini(PaperWindow window) => typeof(PaperWindow).GetMethod("ResetPluginMiniViewCache", Private)!.Invoke(window, null);
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
