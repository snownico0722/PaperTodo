using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PaperTodo;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
    private const string FixtureMarker = ".papertodo-lifecycle-fixture";
    private static readonly string[] Cases = ["startup", "missing-monitor", "master-queue-transfer", "master-queue-cancel", "master-queue-drag-preparation", "master-queue-membership", "master-queue-mutations", "master-queue-hide", "master-queue-disconnect", "master-queue-merge", "master-queue-merge-collapsed", "real-exit", "early-expand", "cancel-prewarm", "real-exit-scripts", "early-exit"];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--sleep-child"])
        {
            Console.WriteLine("ready");
            Thread.Sleep(60_000); // test process ignores EOF; must be killed after the shared grace period
            return 0;
        }
        if (args is ["--fixture", var fixtureName] && Cases.Contains(fixtureName))
        {
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, FixtureMarker)))
                throw new InvalidOperationException("Refusing to use a non-fixture data directory.");
            // Explicit shutdown can stop the Dispatcher before the awaiting caller resumes.
            // Failures set the exit code directly; success does not depend on that continuation.
            var result = 0;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Dispatcher.InvokeAsync(async () =>
            {
                try { await RunFixture(fixtureName); result = 0; }
                catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
                finally { app.Shutdown(); }
            });
            app.Exit += (_, _) => Console.WriteLine("WPF_EXIT_COMPLETED");
            app.Run();
            return result;
        }
        try
        {
            var cases = args is ["--case", var caseName] && Cases.Contains(caseName)
                ? [caseName]
                : args.Length == 0 ? Cases
                : throw new ArgumentException("Use --case with a lifecycle fixture name; measurements moved to tools/PaperTodo.DesktopBenchmarks.");
            foreach (var name in cases) RunIsolated(name);
            Console.WriteLine($"PASS {cases.Length} lifecycle fixtures (isolated data)");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void RunIsolated(string name)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "PaperTodo.LifecycleChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            // Never point the real controller at a user's data directory. Each process owns a
            // fresh copy of just these test binaries, no installed plugins or existing state.
            CopyBinaries(AppContext.BaseDirectory, fixture);
            File.WriteAllText(Path.Combine(fixture, FixtureMarker), "owned test data");
            var start = ChildStart(fixture);
            start.ArgumentList.Add("--fixture"); start.ArgumentList.Add(name);
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(30_000))
            {
                child.Kill(entireProcessTree: true); child.WaitForExit();
                throw new TimeoutException("Lifecycle fixture timed out: " + name);
            }
            var text = output.GetAwaiter().GetResult();
            Console.Write(text); Console.Error.Write(error.GetAwaiter().GetResult());
            Require(child.ExitCode == 0, name + " failed");
            if (name.StartsWith("real-exit") || name == "early-exit")
            {
                using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "data.json")));
                var paper = saved.RootElement.GetProperty("papers")[0];
                Require(paper.GetProperty("content").GetString() == (name == "early-exit" ? "short note 0" : "pending editor text at exit"), "last editor change was lost");
                Require(paper.GetProperty("isVisible").GetBoolean(), "hiding for exit persisted as a user hide");
                Require(text.Contains("WPF_EXIT_COMPLETED"), "normal WPF exit event was skipped");
                foreach (var childLine in text.Split('\n').Where(value => value.StartsWith("SCRIPT_CHILD ")))
                {
                    var id = int.Parse(childLine["SCRIPT_CHILD ".Length..]);
                    Process? script;
                    try { script = Process.GetProcessById(id); }
                    catch (ArgumentException) { continue; }
                    using (script) Require(script.HasExited, "script survived real application exit");
                }
            }
            Console.WriteLine("PASS lifecycle " + name);
        }
        finally { try { Directory.Delete(fixture, recursive: true); } catch { } }
    }

    private static ProcessStartInfo ChildStart(string directory)
    {
        var executable = Path.Combine(directory, "PaperTodo.LifecycleChecks.exe");
        return new ProcessStartInfo(executable)
        {
            UseShellExecute = false, WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, CreateNoWindow = true
        };
    }

    private static void CopyBinaries(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var extension = Path.GetExtension(file);
            if (extension is ".exe" or ".dll" or ".pdb" || file.EndsWith(".deps.json") || file.EndsWith(".runtimeconfig.json"))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        foreach (var locale in new[] { "en", "ja", "ko", "runtimes" })
        {
            var directory = Path.Combine(source, locale);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
        }
    }

    private static async Task RunFixture(string name)
    {
        const int count = 5;
        var state = new AppState
        {
            TelemetryEnabled = false, EnableAnimations = true,
            UseCapsuleMode = true, UseDeepCapsuleMode = true,
            ExperimentalEdgeCapsuleHoverPreview = true,
            UsePersistentPowerShellProcess = false, McpEnabled = false
        };
        var area = SystemParameters.WorkArea;
        for (var i = 0; i < count; i++)
            state.Papers.Add(new PaperData
            {
                Id = "fixture-" + i, Type = PaperTypes.Note, Content = "short note " + i,
                IsVisible = true, IsCollapsed = true, X = area.Left + 60, Y = area.Top + 60,
                Width = 300, Height = 240, CapsuleSide = DeepCapsuleSides.Right
            });
        if (name.StartsWith("master-queue-merge"))
        {
            state.Papers[2].CapsuleSide = DeepCapsuleSides.Left;
            state.Papers[3].CapsuleSide = DeepCapsuleSides.Left;
            if (name == "master-queue-merge-collapsed")
                state.CapsuleCollapseAllActiveQueues["|" + DeepCapsuleSides.Left] = true;
        }
        if (name == "missing-monitor")
            state.Papers.Add(new PaperData
            {
                Id = "missing-screen", Type = PaperTypes.Note, Content = "keep my coordinates",
                IsVisible = true, IsCollapsed = false, X = 1_000_000, Y = 100, Width = 300, Height = 240
            });
        if (name == "master-queue-membership")
            MasterQueueMembershipChecks.Prepare(state);
        var store = new StateStore();
        store.SaveJsonSync(store.SerializeState(state), 1);
        var controller = new AppController();
        var windows = (Dictionary<string, PaperWindow>)Field(controller, "_windows");
        var cache = MarkdownEdgePreviewPreload.For(Dispatcher.CurrentDispatcher);
        var children = new List<Process>();
        try
        {
            await controller.StartAsync(createDefaultPaper: false);
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
            var visible = windows.Values.Count(window => window.HasVisibleSurface);
            if (name == "missing-monitor")
            {
                Require(!windows.ContainsKey("missing-screen"),
                    "ambiguous off-screen paper was restored before the one-shot monitor grace");
                Require(visible == count,
                    "known-monitor capsules waited for an unrelated missing display");
                Require(controller.State.Papers.Single(paper => paper.Id == "missing-screen").X == 1_000_000,
                    "startup overwrote ambiguous coordinates before the grace period");

                await Until(
                    () => windows.TryGetValue("missing-screen", out var missing) &&
                          missing.HasVisibleSurface,
                    "one-shot missing-monitor recovery");
                Require(controller.State.Papers.Single(paper => paper.Id == "missing-screen").X != 1_000_000,
                    "off-screen paper was not rescued after the bounded grace period");
                Require(windows.Values.Count(window => window.HasVisibleSurface) == count + 1,
                    "bounded off-screen rescue hid or duplicated an already-restored paper");
            }
            if (name == "master-queue-drag-preparation")
            {
                MasterQueueDragPreparationChecks.Run(controller);
                await Until(() => windows.Values.All(window => !window.IsCollapseAllRetracted),
                    "master drag cancelled during layout restored presentation");
                return;
            }
            if (name == "master-queue-hide")
            {
                var margin = controller.DeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Right);
                Require(controller.TryBeginMasterCapsuleQueueTransfer(
                    "", EdgeCapsuleEdge.Right, margin, out var transfer),
                    "hide fixture transfer did not start");
                controller.HideAllPapers();
                Require(controller.State.Papers.All(paper => !paper.IsVisible),
                    "hide during master transfer did not hide the papers");
                Require(Application.Current.Windows.Cast<Window>().All(window => !window.IsVisible),
                    "hide during master transfer left a master or paper surface visible");
                Require(controller.State.CapsuleCollapseAllActiveQueues.Count == 0,
                    "hide during master transfer persisted temporary collapse");
                Require(!controller.CommitMasterCapsuleQueueTransfer(transfer, new DeviceScreenPoint(100, 100)),
                    "hidden master transfer could still commit after lifecycle cancellation");
                return;
            }
            if (name == "master-queue-disconnect")
            {
                MasterQueueMonitorChecks.Run(controller);
                MasterQueueMonitorChecks.RunFloatingZOrder(controller);
                return;
            }
            if (name == "master-queue-membership")
            {
                await MasterQueueMembershipChecks.RunMembership(controller, windows, store);
                return;
            }
            if (name == "master-queue-mutations")
            {
                await MasterQueueMembershipChecks.RunMutations(controller, windows, store);
                return;
            }
            if (name == "master-queue-cancel")
            {
                controller.SetDeepCapsuleStartTopMargin("", EdgeCapsuleEdge.Right, 80);
                var originalMargin = controller.DeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Right);
                controller.SaveNow(sync: true);
                controller.SetDeepCapsuleStartTopMargin("", EdgeCapsuleEdge.Right, originalMargin + 20);
                Require(controller.TryBeginMasterCapsuleQueueTransfer(
                    "", EdgeCapsuleEdge.Right, originalMargin, out var cancelled),
                    "cancellable master transfer did not start");
                Require(windows.Values.All(window => window.IsCollapseAllRetracted),
                    "temporary source retraction did not reach the presenters");
                controller.SaveNow(sync: true); // native modal dragging permits unrelated saves
                var duringDrag = store.Load();
                Require(duringDrag.CapsuleCollapseAllActiveQueues.Count == 0,
                    "saving during master drag persisted temporary retraction");
                Require(Math.Abs(duringDrag.DeepCapsuleQueueStartTopMargins["|" + DeepCapsuleSides.Right] - originalMargin) < 0.01,
                    "saving during master drag persisted the preceding vertical preview");
                controller.CancelMasterCapsuleQueueTransfer(cancelled);
                Require(controller.State.Papers.Select(paper => paper.Id).SequenceEqual(
                    Enumerable.Range(0, count).Select(i => "fixture-" + i)),
                    "cancelling master transfer changed paper order");
                Require(controller.State.Papers.All(paper => paper.CapsuleSide == DeepCapsuleSides.Right),
                    "cancelling master transfer changed queue membership");
                await Until(() => windows.Values.All(window => !window.IsCollapseAllRetracted),
                    "cancelled master queue restored presentation");
                Require(controller.TryBeginMasterCapsuleQueueTransfer(
                    "", EdgeCapsuleEdge.Right, originalMargin, out var repeated),
                    "cancelled master drag stranded the transfer gate");
                controller.CancelMasterCapsuleQueueTransfer(cancelled); // late completion from the old native loop
                Require(windows.Values.All(window => window.IsCollapseAllRetracted),
                    "old transfer cancellation canceled the newer transfer");
                Require(!controller.CommitMasterCapsuleQueueTransfer(cancelled, new DeviceScreenPoint(100, 100)),
                    "old transfer committed over a newer transfer");
                controller.CancelMasterCapsuleQueueTransfer(repeated);
                await Until(() => windows.Values.All(window => !window.IsCollapseAllRetracted),
                    "repeated master queue restored presentation");
                Require(controller.State.DeepCapsuleQueueStartTopMargins.TryGetValue(
                        "|" + DeepCapsuleSides.Right, out var restoredMargin) &&
                        Math.Abs(restoredMargin - originalMargin) < 0.01,
                    "cancelling master transfer lost the existing source anchor");
                MasterQueueMembershipChecks.RunAbsentMarginCancellation(controller, store);
                return;
            }
            if (name.StartsWith("master-queue-merge"))
            {
                var targetCollapsed = name == "master-queue-merge-collapsed";
                // The source is expanded in both cases; an existing target owns its collapse state.
                var sourceMargin = controller.DeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Right);
                Require(controller.TryBeginMasterCapsuleQueueTransfer(
                    "", EdgeCapsuleEdge.Right, sourceMargin, out var transfer),
                    "merge transfer did not start");
                Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor),
                    "merge monitor geometry");
                var drop = new DeviceScreenPoint(monitor.WorkArea.Left + 10, monitor.WorkArea.Top + 100);
                Require(controller.CommitMasterCapsuleQueueTransfer(transfer, drop),
                    "master transfer into existing queue did not commit");
                var expectedOrder = new[] { "fixture-2", "fixture-3", "fixture-0", "fixture-1", "fixture-4" };
                Require(controller.State.Papers.Select(paper => paper.Id).SequenceEqual(expectedOrder),
                    "merge did not append source members in their original order");
                Require(controller.State.Papers.All(paper => paper.CapsuleSide == DeepCapsuleSides.Left),
                    "merge left source members behind");
                await Until(() => windows.Values.All(window => window.IsCollapseAllRetracted == targetCollapsed),
                    "merged queue retained target collapse state");
                controller.SaveNow(sync: true);
                var saved = store.Load();
                Require(saved.Papers.Select(paper => paper.Id).SequenceEqual(expectedOrder),
                    "merged member order did not survive persistence");
                Require(saved.CapsuleCollapseAllActiveQueues.Values.Any(active => active) == targetCollapsed,
                    "merged collapse state did not survive persistence");
                return;
            }
            if (name == "master-queue-transfer")
            {
                var sourceMargin = controller.DeepCapsuleStartTopMarginForQueue(
                    "",
                    EdgeCapsuleEdge.Right);
                Require(
                    controller.TryCreateMasterQueueFloatingDragHostOptions(
                        "",
                        EdgeCapsuleEdge.Right,
                        "▾",
                        count.ToString(),
                        out var floatingOptions),
                    "master queue did not create a floating drag presentation");
                Require(
                    floatingOptions.Shape.Kind == EdgeCapsuleSurfaceKind.FloatingFree &&
                    Math.Abs(
                        floatingOptions.Shape.WindowWidthDip -
                        PaperLayoutDefaults.CapsuleWidth) < 0.01 &&
                    floatingOptions.Icon == "▾" &&
                    floatingOptions.Label == count.ToString(),
                    "master queue drag does not reuse the ordinary FloatingFree capsule shape");
                Require(
                    controller.TryBeginMasterCapsuleQueueTransfer(
                        "",
                        EdgeCapsuleEdge.Right,
                        sourceMargin,
                        out var transfer),
                    "master queue transfer did not start");
                Require(
                    windows.Values.All(window => window.IsCollapseAllRetracted),
                    "master drag did not temporarily retract the source queue");
                Require(
                    controller.State.CapsuleCollapseAllActiveQueues.Count == 0,
                    "temporary drag retraction changed persistent collapse state");

                Require(
                    WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                        null,
                        out var monitor),
                    "master transfer test monitor");
                var drop = new DeviceScreenPoint(
                    monitor.WorkArea.Left + Math.Max(1, monitor.WorkArea.Width / 4),
                    monitor.WorkArea.Top + Math.Max(1, monitor.WorkArea.Height / 3));
                var rightDrop = new DeviceScreenPoint(
                    monitor.WorkArea.Left + Math.Max(1, monitor.WorkArea.Width * 3 / 4),
                    drop.Y);
                Require(
                    MasterCapsuleQueueTransferPolicy.TryResolveTarget(
                        rightDrop,
                        "",
                        EdgeCapsuleEdge.Left,
                        count + 1,
                        controller.DeepCapsuleGap,
                        out var rightTarget) &&
                    rightTarget.Edge == EdgeCapsuleEdge.Right,
                    "master transfer did not resolve the target monitor's right half to the right queue");
                Require(
                    MasterCapsuleQueueTransferPolicy.TryResolveTarget(
                        drop,
                        "",
                        EdgeCapsuleEdge.Right,
                        count + 1,
                        controller.DeepCapsuleGap,
                        out var leftTarget) &&
                    leftTarget.Edge == EdgeCapsuleEdge.Left,
                    "master transfer did not resolve the target monitor's left half to the left queue");
                Require(
                    controller.CommitMasterCapsuleQueueTransfer(
                        transfer,
                        drop),
                    "master queue transfer did not commit");

                var expectedMonitor =
                    WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(
                        monitor.DeviceName);
                Require(
                    controller.State.Papers.Take(count).All(paper =>
                        paper.CapsuleSide == DeepCapsuleSides.Left &&
                        string.Equals(
                            paper.CapsuleMonitorDeviceName,
                            expectedMonitor,
                            StringComparison.Ordinal)),
                    "master transfer did not move every queue member to the target monitor/edge");
                Require(
                    controller.State.CapsuleCollapseAllActiveQueues.Count == 0,
                    "temporary master-drag retraction leaked into the committed queue");

                Require(
                    MasterCapsuleQueueTransferPolicy.TryResolveTarget(
                        drop,
                        "",
                        EdgeCapsuleEdge.Right,
                        count + 1,
                        controller.DeepCapsuleGap,
                        out var target),
                    "master transfer target policy did not resolve");
                Require(
                    Math.Abs(
                        controller.DeepCapsuleStartTopMarginForQueue(
                            expectedMonitor,
                            EdgeCapsuleEdge.Left) -
                        target.StartTopMargin) < 0.01,
                    "master transfer did not preserve the drop height as the new queue anchor");
                await Until(
                    () => windows.Values.All(window => window.IsDeepCapsuleSlotVisible),
                    "master-transferred queue presentation");
                return;
            }
            if (name == "early-exit")
            {
                controller.Exit();
                return;
            }
            if (name == "cancel-prewarm")
            {
                controller.HideAllPapers();
                await (Task)Field(controller, "_startupShellPrewarmTask");
                await Until(() => cache.PendingCount == 0, "cancelled preview drain");
                Require(windows.Values.All(window => !window.HasVisibleSurface) && cache.ArtifactCount == 0,
                    "deferred startup resurrected a hidden surface or its cache");
                return;
            }
            if (name == "early-expand")
            {
                windows["fixture-0"].ActivateFromEdgeShortcut();
                Require(windows["fixture-0"].IsShellBuilt && windows["fixture-0"].HasExpandedPaperSurface,
                    "early demand did not construct and show the selected paper");
            }
            await Until(() => windows.Values.All(window => window.IsShellBuilt), "shell drain");
            // A hidden edge anchor legitimately defers optional preload while its paper is
            // expanded. Startup correctness is not defined by the cache's pending count.
            if (name == "early-expand")
            {
                var first = windows["fixture-0"];
                var editor = Field(first, "_noteBox");
                editor.GetType().GetProperty("Text")!.SetValue(editor, "edited during startup");
                first.CommitPendingNoteContentForSave();
                await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                Require(first.HasExpandedPaperSurface &&
                    controller.State.Papers.Single(paper => paper.Id == "fixture-0").Content == "edited during startup",
                    "startup completion withdrew the requested paper or lost its edit");
            }
            if (name == "startup")
                Require(windows.Count == count && windows.Values.All(window => window.HasVisibleSurface),
                    "startup did not restore the requested visible papers");
            if (name == "real-exit-scripts")
            {
                var registry = (IDictionary)typeof(PaperWindow).GetField("PersistentScriptProcesses", Private)!.GetValue(null)!;
                for (var i = 0; i < 3; i++)
                {
                    var start = ChildStart(AppContext.BaseDirectory);
                    start.ArgumentList.Add("--sleep-child");
                    var process = Process.Start(start)!;
                    children.Add(process);
                    Console.WriteLine("SCRIPT_CHILD " + process.Id);
                    Require(await process.StandardOutput.ReadLineAsync() == "ready", "script fixture did not start");
                    registry.Add("fixture-script-" + i, process);
                }
            }
            if (name.StartsWith("real-exit"))
            {
                var editor = Field(windows["fixture-0"], "_noteBox");
                editor.GetType().GetProperty("Text")!.SetValue(editor, "pending editor text at exit");
                controller.Exit();
                return;
            }
            var surfaces = Application.Current.Windows.Cast<Window>().ToArray();
            controller.Dispose();
            Require(surfaces.All(window => !window.IsVisible), "visible surfaces remained after dispose");
        }
        finally
        {
            controller.Dispose();
            foreach (var process in children)
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); } catch { }
        }
    }

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, Private)?.GetValue(target) ??
        target.GetType().GetProperty(name, Private)?.GetValue(target) ??
        throw new MissingMemberException(target.GetType().FullName, name);
    private static async Task Until(Func<bool> ready, string name)
    {
        var started = Stopwatch.GetTimestamp();
        while (!ready())
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 12) throw new TimeoutException(name);
            await Task.Delay(10);
        }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

