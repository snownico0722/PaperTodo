using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    internal static void VerifyImages()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaperTodo.ReviewAssets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Identical pixel canvases with different (including non-square) source DPI must
            // produce identical WPF logical canvases, not change aspect ratio as frames advance.
            WritePng(Path.Combine(directory, "papertodomain1.png"), 96, 96);
            WritePng(Path.Combine(directory, "papertodomain2.png"), 300, 150);
            WritePng(Path.Combine(directory, "papertodomain3.png"), 72, 144);
            WritePng(Path.Combine(directory, "papertodomain.png"), 300, 150);
            // A native GDI+ format failure must never take the optional artwork fallback down.
            foreach (var malformed in new[]
            {
                Array.Empty<byte>(), "broken"u8.ToArray(), "GIF89a"u8.ToArray(),
                new byte[] { 0, 0, 1, 0, 0, 0 }
            })
            {
                File.WriteAllBytes(Path.Combine(directory, "papertodomain.gif"), malformed);
                Require(MasterCapsuleSkin.LoadClip(directory, "papertodomain")?.FrameCount == 3,
                    "Malformed GIF did not fall back to the complete numbered sequence");
            }
            var clip = MasterCapsuleSkin.LoadClip(directory, "papertodomain");
            Require(clip != null && clip.FrameCount == 3, "Sequence did not load");
            for (var i = 0; i < clip.FrameCount; i++)
            {
                var frame = clip.FrameAt(i);
                Require(frame.IsFrozen && frame.PixelWidth == 8 && frame.PixelHeight == 8 &&
                        Math.Abs(frame.Width - 8) < .01 && Math.Abs(frame.Height - 8) < .01,
                    "Review regression: asset DPI changed the common animation canvas");
            }
            for (var i = 1; i <= 3; i++) File.Delete(Path.Combine(directory, $"papertodomain{i}.png"));
            var still = MasterCapsuleSkin.LoadClip(directory, "papertodomain")!.FirstFrame;
            Require(Math.Abs(still.Width / still.Height - 1) < .001,
                "A static skin still used printing DPI instead of the pixel aspect ratio");
            Console.WriteLine("PASS review: corrupt GIF fallback and normalized pixel canvas");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void WritePng(string path, double dpiX, double dpiY)
    {
        var pixels = Enumerable.Range(0, 8 * 8).SelectMany(_ => new byte[] { 30, 80, 150, 255 }).ToArray();
        var image = BitmapSource.Create(8, 8, dpiX, dpiY, PixelFormats.Bgra32, null, pixels, 8 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    internal static void VerifySettings(AppController controller)
    {
        foreach (var (id, factory, property) in new[]
        {
            ("capsule.master_animation_fps", "CreateMasterCapsuleAnimationSpeedSlider", "masterCapsuleAnimationFps"),
            ("capsule.master_size", "CreateMasterCapsuleSizeSlider", "masterCapsuleSize")
        })
        {
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
            Require(saved.RootElement.GetProperty(property).GetInt32() == 18,
                "Stale slider handling diverged from StateStore");
        }
        Console.WriteLine("PASS review: stale slider callbacks, real edits and persistence");
    }

    internal static void VerifyDragOnly(AppController controller)
    {
        var master = controller.MasterCapsuleForQueue("", EdgeCapsuleEdge.Right)!;
        Require(master != null && MasterCapsuleSkin.Visuals.Normal == null && MasterCapsuleSkin.Visuals.Drag != null,
            "Drag-only review needs a default master and an independent drag clip");
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
                    Require(host.HasMasterSkin, "Real queue transfer did not bind the drag-only artwork");
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
                    Require(!Field<MasterCapsuleAnimationPlayer>(host, "_masterSkinPlayer").IsRunning,
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
                controller.MasterCapsuleQueueKey("", EdgeCapsuleEdge.Right), hadMargin, originalMargin)!;
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
