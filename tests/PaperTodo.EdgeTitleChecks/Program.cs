using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using PaperTodo;

internal static partial class Program
{
    private static int assertions;

    [STAThread]
    private static int Main()
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            CycleAndUnicode();
            Console.WriteLine("PASS title-cycle-and-unicode");
            Persistence();
            Console.WriteLine("PASS legacy-and-zero-persistence");
            Geometry();
            Console.WriteLine("PASS title-presentation-and-transition-geometry");
            HostContentVisibility();
            Console.WriteLine("PASS host-title-plugin-and-icon-slot-layout");
            QueuedPreviewTransactions();
            Console.WriteLine("PASS queued-preview-transaction-ordering");
            Console.WriteLine($"Edge title checks: 5/5 groups, {assertions} assertions passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Application.Current.Shutdown(); }
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    private static void CycleAndUnicode()
    {
        var cycle = new[] { EdgeCapsuleTitleLimit.Unlimited, EdgeCapsuleTitleLimit.Hidden }
            .Concat(Enumerable.Range(1, PaperTitles.MaxConfigurableTitleLength)).ToArray();
        for (var i = 0; i < cycle.Length; i++)
        {
            Check(EdgeCapsuleTitleLimit.Step(cycle[i], true) == cycle[(i + 1) % cycle.Length], "Forward cycle");
            Check(EdgeCapsuleTitleLimit.Step(cycle[i], false) == cycle[(i + cycle.Length - 1) % cycle.Length], "Backward cycle");
        }
        const string text = "中👨‍👩‍👧‍👦e\u0301文";
        Check(EdgeCapsuleTitleLimit.TextForMeasure(text, 0) == text, "Legacy unlimited retains full title");
        Check(EdgeCapsuleTitleLimit.TextForMeasure(text, -1) == "", "Zero measures no title");
        Check(EdgeCapsuleTitleLimit.TextForMeasure(text, 2) == "中👨‍👩‍👧‍👦", "Do not split emoji clusters");
        Check(EdgeCapsuleTitleLimit.TextForMeasure(text, 3) == "中👨‍👩‍👧‍👦e\u0301", "Do not split combining characters");
        Check(EdgeCapsuleTitleLimit.TextForMeasure("", -1) == "", "Empty title");
        Check(EdgeCapsuleTitleLimit.Normalize(int.MinValue) == 0, "Invalid old negatives remain unlimited");
        Check(EdgeCapsuleTitleLimit.Normalize(int.MaxValue) == 20, "Upper bound");
    }

    private static void Persistence()
    {
        var dir = Path.Combine(Path.GetTempPath(), "papertodo-edge-title-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new StateStore(dir, DurableAtomicFileWriter.Shared);
            foreach (var value in new[] { 0, -1, 1, 20 })
            {
                File.WriteAllText(store.FilePath, $"{{\"papers\":[],\"deepCapsuleTitleMeasureCharacterLimit\":{value}}}");
                var state = store.Load();
                Check(state.DeepCapsuleTitleMeasureCharacterLimit == value, "Load/normalization retains semantics");
                File.WriteAllText(store.FilePath, store.SerializeState(state));
                Check(store.Load().DeepCapsuleTitleMeasureCharacterLimit == value, "Round-trip retains zero/unlimited");
            }
            File.WriteAllText(store.FilePath, "{\"papers\":[]}");
            Check(store.Load().DeepCapsuleTitleMeasureCharacterLimit == 0, "Missing legacy field remains unlimited");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static void HostContentVisibility()
    {
        using var host = EdgeCapsuleHost.Create(new EdgeCapsuleHostOptions(
            WindowChromeMargin: 4, ChromeCornerRadius: 16, InnerCornerRadius: 15,
            OutlineThickness: 2, OutlineOverlap: 1, BodyHeight: 32,
            LeftPadding: 6, IconGap: 4, IconText: "✓", IconFontSize: 13,
            LabelFontSize: 12, LabelFontWeight: FontWeights.Normal, CloseToolTip: "Close",
            PaperBrush: Brushes.White, PaperBorderBrush: Brushes.Gray, OutlineBrush: Brushes.Blue,
            HoverBrush: Brushes.LightGray, IconBrush: Brushes.Gray,
            StrongTextBrush: Brushes.Black, TextBrush: Brushes.Gray,
            UiFontFamily: new FontFamily("Segoe UI"), SymbolFontFamily: new FontFamily("Segoe UI Symbol"),
            Language: XmlLanguage.GetLanguage("en-US"), Topmost: false, DiagnosticId: "title-checks"));
        T HostPart<T>(string name) where T : class => (T)typeof(EdgeCapsuleHost)
            .GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        var label = HostPart<TextBlock>("Label");
        var icon = HostPart<TextBlock>("Icon");
        var contentGrid = HostPart<Grid>("ContentGrid");
        var window = HostPart<Window>("Window");
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), "Host test monitor");
        var layout = new EdgeCapsuleLayoutSnapshot(monitor, EdgeCapsuleEdge.Right, 40, 10,
            40, 28, 40, 178, 40, false, 1, null, ExpandedWidthDip: 150);
        var model = EdgeCapsuleModel.Initial with
        {
            State = new EdgeCapsuleState(EdgeCapsuleSlotState.CollapsedDocked,
                EdgeCapsuleVisualState.Resting, EdgeCapsuleGestureState.Idle, EdgeCapsuleOpenOrigin.Normal),
            Placement = new EdgeCapsulePlacement(0, 0, 1)
        };
        var pluginContent = new TextBlock { Text = "Plugin clock", Background = Brushes.Transparent };
        foreach (var hideTitle in new[] { false, true })
        foreach (var hovered in new[] { false, true })
        {
            var frame = EdgeCapsuleTargetPlanner.Calculate(
                model with { State = model.State with { Visual = hovered
                    ? EdgeCapsuleVisualState.Hovered : EdgeCapsuleVisualState.Resting } },
                layout with { HideRestingTitle = hideTitle }).Docked.ToFrame();
            var expectedTitle = !hideTitle || hovered ? Visibility.Visible : Visibility.Collapsed;
            host.SetLabel("Ordinary title", "Stored title");
            host.SetPluginContent(pluginContent, "Plugin tooltip");
            Check(host.Apply(frame), "Apply real host frame with plugin content");
            Check(label.Visibility == Visibility.Collapsed && icon.Visibility == Visibility.Collapsed,
                "Frame application must not reveal defaults beneath plugin content");
            Check(pluginContent.IsVisible, "Custom plugin content remains visible");

            host.SetPluginContent(null, null);
            Check(label.Visibility == expectedTitle && icon.Visibility == Visibility.Visible,
                "Removing plugin content restores defaults according to the applied title state");
            Check(host.Apply(frame), "Apply ordinary host frame");
            Check(label.Visibility == expectedTitle, "Ordinary frame honors title visibility");
            // Theme/title refresh uses this sequence. There is deliberately no Apply afterwards:
            // an unchanged target makes the presenter skip reapplying the frame.
            host.SetLabel("Refreshed title", "Refreshed tooltip");
            host.SetPluginContent(null, null);
            Check(label.Visibility == expectedTitle && label.Text == "Refreshed title" &&
                icon.Visibility == Visibility.Visible,
                "Refreshing ordinary content preserves hidden titles without a new frame");
        }

        // The fixed slot must affect the real WPF layout, not only the outer width calculation.
        // Measure the following label after switching between the two default glyphs.
        var visibleFrame = EdgeCapsuleTargetPlanner.Calculate(
            model with { State = model.State with { Visual = EdgeCapsuleVisualState.Hovered } },
            layout with { HideRestingTitle = false }).Docked.ToFrame();
        host.SetPluginContent(null, null);
        host.SetDefaultIconSlotWidth(40);
        Check(Math.Abs(host.DefaultIconSlotWidthForChecks - 40) < 0.01,
            "Host accepts a real default icon slot width");
        Check(host.Apply(visibleFrame), "Apply host frame for icon-slot alignment");
        host.SetLabel("Title", "Title");
        icon.Text = "✓";
        window.UpdateLayout();
        var todoLabelX = label.TranslatePoint(new Point(0, 0), contentGrid).X;
        icon.Text = "✎";
        window.UpdateLayout();
        var noteLabelX = label.TranslatePoint(new Point(0, 0), contentGrid).X;
        Check(Math.Abs(todoLabelX - noteLabelX) < 0.01,
            "Todo and note glyphs share the same real layout slot");
        host.SetDefaultIconSlotWidth(0);
        Check(host.DefaultIconSlotWidthForChecks < 0.01,
            "Script/natural icon layout can release the default slot");
        PreviewClipReuse(host);
    }

    private static void Geometry()
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        foreach (var edge in new[] { EdgeCapsuleEdge.Left, EdgeCapsuleEdge.Right })
        foreach (var hiddenClose in new[] { false, true })
        {
            var monitor = new MonitorGeometry("test", new DeviceScreenRect(-1920, -100, 0, 980), scale, scale);
            var layout = new EdgeCapsuleLayoutSnapshot(monitor, edge, 40, 10,
                40, 28, 40, 260, 180, hiddenClose, 1, null,
                ExpandedWidthDip: 150, HideRestingTitle: true);
            var model = EdgeCapsuleModel.Initial with
            {
                State = new EdgeCapsuleState(EdgeCapsuleSlotState.CollapsedDocked,
                    EdgeCapsuleVisualState.Resting, EdgeCapsuleGestureState.Idle, EdgeCapsuleOpenOrigin.Normal),
                Placement = new EdgeCapsulePlacement(0, 0, 1)
            };
            EdgeCapsuleTargetPresentation Plan(EdgeCapsuleModel m, EdgeCapsuleLayoutSnapshot l) =>
                EdgeCapsuleTargetPlanner.Calculate(m, l).Docked;
            var resting = Plan(model, layout);
            var hoverModel = model with { State = model.State with { Visual = EdgeCapsuleVisualState.Hovered } };
            var hovered = Plan(hoverModel, layout);
            Check(!resting.TitleVisible && hovered.TitleVisible, "Zero title appears only while expanded");
            Check(resting.HostBounds == hovered.HostBounds, "Reserve full title capacity before hover");
            var expectedCloseWidth = hiddenClose ? 0 : 28;
            var expected = EdgeCapsuleGeometry.Calculate(new(monitor, edge, 40, 150, expectedCloseWidth, 40));
            Check(hovered.Bounds == expected.Bounds && hovered.InteractiveBounds == expected.InteractiveBounds,
                "Visible width and hit region remove the hidden close strip");
            Check(!hovered.CloseSegmentActsAsContent,
                "Hidden close mode leaves no invisible content segment");
            var transition = new EdgeCapsuleTransition(resting.ToFrame(), hovered, 0, 100,
                EdgeCapsuleTransitionReason.Pointer);
            var previousWidth = resting.Bounds.Width;
            for (var tick = 0; tick <= 100; tick += 5)
            {
                var frame = EdgeCapsuleTransitionPolicy.Sample(transition, tick).Frame;
                Check(frame.IsUsable && frame.HostBounds == resting.HostBounds, "Every animation frame stays inside stable host");
                Check(frame.Bounds.Width >= previousWidth, "No width reversal during hover");
                previousWidth = frame.Bounds.Width;
            }
            var retract = new EdgeCapsuleTransition(hovered.ToFrame(), resting, 0, 100,
                EdgeCapsuleTransitionReason.Pointer);
            Check(!EdgeCapsuleTransitionPolicy.Sample(retract, 100).Frame.TitleVisible,
                "Mouse leave restores hidden title");
            Check(EdgeCapsuleTransitionPolicy.Create(resting.ToFrame(), hovered,
                EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.Pointer), false, 0, 1000) == null,
                "Animation disabled remains an immediate apply");
            var previewsEnabled = layout with { ExpandedWidthDip = layout.RestingWidthDip };
            var ordinaryHover = Plan(hoverModel, previewsEnabled);
            Check(!ordinaryHover.TitleVisible && ordinaryHover.BodyWindowWidthDevice == resting.BodyWindowWidthDevice,
                "Preview-enabled compact hover retains the configured title limit");
            var preview = Plan(hoverModel with { Preview = EdgeCapsulePreviewState.Open }, layout);
            Check(preview.Surface == EdgeCapsuleSurfaceKind.DockedPreview &&
                preview.Bounds.Width == (int)Math.Round(260 * scale),
                "Preview card keeps its independent close segment");
            var active = hoverModel with { State = hoverModel.State with { Visual = EdgeCapsuleVisualState.Active } };
            var handoff = Plan(active with { State = active.State with { Gesture = EdgeCapsuleGestureState.DockingHandoff } }, layout);
            var reveal = Plan(active with { State = active.State with { Gesture = EdgeCapsuleGestureState.DockingReveal } }, layout);
            Check(handoff.Bounds == reveal.Bounds && reveal.Bounds == Plan(active, layout).Bounds,
                "Docking handoff/reveal/active retain identical title geometry");
        }
    }
}
