using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PaperTodo;

internal static class MasterCapsuleReviewChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static object? Call(object owner, string name, params object?[] args) =>
        owner.GetType().GetMethod(name, Private)!.Invoke(owner, args);

    internal static void VerifySettings(AppController controller)
    {
        // Both image sliders share the same commit lifecycle; exercise it once.
        const string id = "capsule.master_animation_fps";
        const string factory = "CreateMasterCapsuleAnimationSpeedSlider";
        void Set(int value) => controller.PublicSettings.Set(id, JsonSerializer.SerializeToElement(value));
        int Read() => controller.PublicSettings.Get(id).Value.GetInt32();
        Slider NewSlider() => ((Grid)Call(controller, factory)!).Children.OfType<Slider>().Single();
        static void LoseFocus(Slider slider) => slider.RaiseEvent(new KeyboardFocusChangedEventArgs(
            Keyboard.PrimaryDevice, Environment.TickCount, slider, null)
        { RoutedEvent = Keyboard.LostKeyboardFocusEvent });

        Set(12);
        var stale = NewSlider();
        Set(20); // API / restore-defaults supersedes the slider instance being removed.
        LoseFocus(stale);
        Require(Read() == 20, "Review regression: stale slider overwrote the newer setting");
        stale.Value = 25;
        LoseFocus(stale); // An obsolete pending drag must not win either.
        Require(Read() == 20, "A retired slider draft overwrote the newer setting");
        var current = NewSlider();
        current.Value = 24;
        LoseFocus(current);
        Require(Read() == 24, "A current user edit was not committed");
        Set(18);
        LoseFocus(current); // Repeated completion from a previously committed control.
        Require(Read() == 18, "An old completion replayed after a later API write");
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data.json")));
        Require(saved.RootElement.GetProperty("masterCapsuleAnimationFps").GetInt32() == 18,
            "Stale slider handling diverged from StateStore");
        Console.WriteLine("PASS review: stale slider callbacks, real edits and persistence");
    }

    internal static void VerifyDragOnly(AppController controller)
    {
        var master = controller.MasterCapsuleForQueue("", EdgeCapsuleEdge.Right)!;
        Require(master != null && MasterCapsuleSkin.Visuals.Normal == null && MasterCapsuleSkin.Visuals.Drag != null,
            "Drag-only review needs a default master and an independent drag clip");
        var player = Field<MasterCapsuleAnimationPlayer>(master, "_skinPlayer");
        Require(player.Playback == null && !player.IsRunning &&
                Field<Image>(master, "_skinImage").Visibility == Visibility.Collapsed &&
                Field<SkinBorder>(master, "_fallbackSkinChrome").Visibility == Visibility.Visible,
            "A drag-only skin replaced or animated the default resting pill");
        Require(controller.TryCreateMasterQueueFloatingDragHostOptions("", EdgeCapsuleEdge.Right, "▾", "5", out var standard),
            "Default floating options unavailable");
        Require(EdgeCapsuleDragWindow.TryPrewarmInfrastructure(standard), "Floating HWND prewarm failed");
        var host = EdgeCapsuleDragWindow.Rent(standard);
        host.ReturnToPool();
        var originalMargin = controller.DeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Right);
        var hadMargin = controller.HasDeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Right);
        var originalSides = controller.State.Papers.Select(paper => paper.CapsuleSide).ToArray();
        Exception? failure = null;
        var observed = false;
        DispatcherOperation? inspection = null;
        void OnVisible(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!host.IsVisible || inspection != null) return;
            inspection = master.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                try
                {
                    observed = true;
                    var actual = Field<EdgeCapsuleDragWindowOptions>(host, "_configuredOptions");
                    var floatingPlayer = Field<MasterCapsuleAnimationPlayer>(host, "_masterSkinPlayer");
                    Require(host.HasMasterSkin && floatingPlayer.IsRunning &&
                            Field<Image>(host, "_masterSkinImage").Source != null,
                        "Real queue transfer did not start the drag-only artwork");
                    // Observe the options produced by TryRunQueueTransfer, rather than copying its
                    // construction into the test (which previously missed this regression).
                    Require(actual.Shape.Equals(standard.Shape),
                        "Review regression: drag-only transfer replaced the default floating geometry");
                    host.SetMasterSkinPlayback(null, EdgeCapsuleEdge.Right);
                    host.UpdateLayout();
                    var shell = Field<Grid>(host, "_shell");
                    var surface = Field<Grid>(host, "_surface");
                    Require(shell.Visibility == Visibility.Visible &&
                            shell.Height + shell.Margin.Top + shell.Margin.Bottom <= surface.ActualHeight + .1,
                        "Default shell was clipped when the drag-only image returned to normal");
                    Require(!floatingPlayer.IsRunning,
                        "Returning to the default shell left the animation running");
                }
                catch (Exception ex) { failure = ex; }
                finally { master.CancelQueueTransfer(); }
            }));
        }
        host.IsVisibleChanged += OnVisible;
        try
        {
            Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), "Monitor unavailable");
            var pointer = new DeviceScreenPoint(monitor.WorkArea.Right - 100, monitor.WorkArea.Top + 100);
            var sessionType = typeof(MasterCapsuleWindow).GetNestedType("MasterDragSession", Private)!;
            var session = Activator.CreateInstance(sessionType, pointer,
                controller.MasterCapsuleQueueKey("", EdgeCapsuleEdge.Right), hadMargin, originalMargin, null)!;
            Require((bool)Call(master, "TryRunQueueTransfer", pointer, session)!, "Transfer did not start");
            master.Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
            if (failure != null) throw failure;
            Require(observed, "The real transfer geometry was never inspected");
            Require(master.IsVisible && !host.IsVisible && !EdgeCapsuleDragWindow.HasActiveLease,
                "Cancelled review transfer stranded its visual authority or pool lease");
            Require(controller.State.Papers.Select(paper => paper.CapsuleSide).SequenceEqual(originalSides) &&
                    controller.HasDeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Right) == hadMargin &&
                    Math.Abs(controller.DeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Right) - originalMargin) < .01,
                "Review transfer mutated queue membership or anchor");
        }
        finally
        {
            inspection?.Abort();
            host.IsVisibleChanged -= OnVisible;
        }
        Console.WriteLine("PASS review: real drag-only transfer, default-shell bounds and cancellation");
    }
}
