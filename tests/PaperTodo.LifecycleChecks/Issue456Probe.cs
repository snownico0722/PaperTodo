using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static class Issue456Probe
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    internal static void Prepare(AppState state, string name)
    {
        var free = name == "issue456-free";
        state.UseCapsuleFreeQueues = free;
        state.UseCapsuleCollapseAll = true;
        for (int i = 0; i < state.Papers.Count; i++)
        {
            var left = i >= 3;
            var paper = state.Papers[i];
            paper.CapsuleSide = left ? DeepCapsuleSides.Left : DeepCapsuleSides.Right;
            if (free) paper.CapsuleQueueId = left ? "dest" : "source";
        }
        state.DeepCapsuleQueueStartTopMargins[free ? "|right#source" : "|right"] = 110;
        state.DeepCapsuleQueueStartTopMargins[free ? "|left#dest" : "|left"] = 190;
    }

    internal static async Task Run(AppController controller, string name)
    {
        var free = name == "issue456-free";
        var forced = name.EndsWith("-forced");
        var direct = name.StartsWith("issue456-direct");
        var right = free ? "|right#source" : "|right";
        var left = free ? "|left#dest" : "|left";
        var source = controller.MasterCapsuleForQueue(right) ?? throw new Exception("Missing source");
        var target = controller.MasterCapsuleForQueue(left) ?? throw new Exception("Missing target");
        var dispatcher = source.Dispatcher;
        await Task.Delay(500);
        await dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
        Point Center(MasterCapsuleWindow w)
        {
            var pill = (FrameworkElement)typeof(MasterCapsuleWindow).GetField("_pill", Instance)!.GetValue(w)!;
            return pill.PointToScreen(new Point(pill.ActualWidth / 2, pill.ActualHeight / 2));
        }
        var from = Center(source);
        var to = Center(target);
        Console.WriteLine($"BEGIN {name} OS={Environment.OSVersion.Version} CLR={Environment.Version} WPF={typeof(Window).Assembly.GetName().Version} tier={RenderCapability.Tier >> 16} screen={SystemParameters.PrimaryScreenWidth}x{SystemParameters.PrimaryScreenHeight} from={from} to={to} right=3 left=2");
        if (!source.IsVisible || !target.IsVisible || from.X <= to.X)
            throw new Exception("Two distinct visible queue masters required");

        bool pressed = false, flight = false, reveal = false, injected = false;
        var pool = typeof(EdgeCapsuleDragWindow).GetField("s_pooledHost", Static)!;
        var animation = typeof(EdgeCapsuleDragWindow).GetField("_dockingHandoffAnimation", Instance)!;
        void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) { pressed = true; Console.WriteLine("MASTER_RECEIVED_LEFT_DOWN"); }
        }
        source.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnDown), true);

        void OnRendering(object? sender, EventArgs e)
        {
            if (!EdgeCapsuleDragWindow.HasActiveLease || pool.GetValue(null) is not EdgeCapsuleDragWindow host ||
                animation.GetValue(host) is not object record) return;
            var phase = record.GetType().GetProperty("Phase")!.GetValue(record)?.ToString();
            if (phase == "Reveal") { reveal = true; return; }
            if (phase != "Flight" || flight) return;
            flight = true;
            Console.WriteLine($"FLIGHT_OBSERVED allLeft={controller.State.Papers.All(p => p.CapsuleSide == DeepCapsuleSides.Left)}");
            if (!forced) return;
            // Test-only positive control. Leave the real Rendering event, endpoint verification
            // and original completion delegate intact. Scope the one imposed condition precisely.
            var property = record.GetType().GetProperty("Completed")!;
            var original = (Action<bool>)property.GetValue(record)!;
            property.SetValue(record, (Action<bool>)(reached => {
                injected = true;
                Console.WriteLine($"INJECT_DISABLE_PROCESSING reached={reached}");
                using (dispatcher.DisableProcessing()) original(reached);
            }));
        }

        // Observe without handling. Record the exact first-chance stack even if the
        // surrounding render dispatch or lifecycle harness subsequently terminates.
        AppDomain.CurrentDomain.FirstChanceException += (_, eventArgs) =>
        {
            if (eventArgs.Exception is InvalidOperationException ex &&
                ex.Message.Contains("dispatcher processing", StringComparison.OrdinalIgnoreCase) &&
                ex.StackTrace?.Contains("PrepareQueueTransferHandoff") == true)
                Console.WriteLine("MATCHING_FIRST_CHANCE " + ex);
        };
        dispatcher.UnhandledException += (_, args) => Console.WriteLine("DISPATCHER_UNHANDLED " + args.Exception);
        CompositionTarget.Rendering += OnRendering;
        try
        {
            // The UI thread enters Windows' modal caption move loop. Drive the real
            // mouse from an independent thread and always release it on the test desktop.
            if (direct)
            {
                if (!controller.TryCreateMasterQueueFloatingDragHostOptions("", EdgeCapsuleEdge.Right, "▾", "3", out var options) ||
                    !controller.TryBeginMasterCapsuleQueueTransfer("", EdgeCapsuleEdge.Right,
                        controller.DeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Right), out var snapshot))
                    throw new Exception("Cannot prepare the two-queue controller transfer");
                var host = EdgeCapsuleDragWindow.Rent(options);
                typeof(MasterCapsuleWindow).GetField("_floatingDragHost", Instance)!.SetValue(source, host);
                typeof(MasterCapsuleWindow).GetField("_queueTransferSnapshot", Instance)!.SetValue(source, snapshot);
                var drop = new DeviceScreenPoint(to.X, to.Y);
                host.ShowWithEntrance(new DeviceScreenPoint(to.X + 110, to.Y + 60), false, 1, 0);
                source.Hide();
                if (!controller.CommitMasterCapsuleQueueTransfer(snapshot, drop, out var current) || current == null)
                    throw new Exception("Two-queue controller transfer failed");
                void Release()
                {
                    typeof(MasterCapsuleWindow).GetMethod("ReleaseFloatingDragHostHandlers", Instance)!.Invoke(source, null);
                    host.ReturnToPool();
                }
                typeof(MasterCapsuleWindow).GetMethod("BeginQueueTransferFloatingHandoff", Instance)!
                    .Invoke(source, new object[] { host, current, (Action)Release });
            }
            else await Task.Run(() => {
                LifecycleInput.MoveCursor(from);
                Thread.Sleep(140);
                LifecycleInput.LeftButton(true);
                try
                {
                    Thread.Sleep(140);
                    for (int i = 1; i <= 36; i++)
                    {
                        LifecycleInput.MoveCursor(new Point(from.X + (to.X - from.X) * i / 36,
                            from.Y + (to.Y - from.Y) * i / 36));
                        Thread.Sleep(22);
                    }
                    Thread.Sleep(140);
                }
                finally { LifecycleInput.LeftButton(false); }
            });
            var timer = Stopwatch.StartNew();
            bool Merged() => controller.State.Papers.Count == 5 && controller.State.Papers.All(p =>
                p.CapsuleSide == DeepCapsuleSides.Left && (!free || p.CapsuleQueueId == "dest"));
            while (timer.Elapsed.TotalSeconds < 5 &&
                !(Merged() && flight && !EdgeCapsuleDragWindow.HasActiveLease))
                await Task.Delay(20);
            var destination = controller.MasterCapsuleForQueue(left);
            var released = !EdgeCapsuleDragWindow.HasActiveLease;
            var visible = destination is { IsVisible: true } && destination.Opacity >= .999;
            var retired = controller.MasterCapsuleForQueue(right) == null;
            Console.WriteLine($"END {name} pressed={pressed} flight={flight} reveal={reveal} imposed={injected} merged={Merged()} visible={visible} retired={retired} released={released}");
            if (!((direct || pressed) && flight && reveal && Merged() && visible && retired && released))
                throw new Exception("Native drag did not satisfy all merge/flight completion checks");
        }
        finally { CompositionTarget.Rendering -= OnRendering; }
    }
}
