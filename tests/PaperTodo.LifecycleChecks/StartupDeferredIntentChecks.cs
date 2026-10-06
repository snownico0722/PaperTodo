using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using PaperTodo;

internal static class StartupDeferredIntentChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task Run(string name, AppController controller, PaperWindow window)
    {
        // Use the real built-in body with a test startup declaration. This isolates the host's
        // startup ownership contract without relying on WebView loading or a throwing factory.
        await ((Task)Part(controller, "_startupShellPrewarmTask")).WaitAsync(TimeSpan.FromSeconds(10));
        var registry = controller.PaperBodyPlugins;
        var descriptors = (IDictionary)Part(registry, "_descriptors");
        var original = (PaperBodyPluginDescriptor)descriptors[PaperBodyProviderIds.Markdown]!;
        var paper = (PaperData)Part(window, "_paper");
        var owner = paper.StartupOwnerPluginId;
        var instance = paper.StartupInstanceKey;
        var shells = (Task)Part(controller, "_startupShellPrewarmTask");
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manifest = new PaperBodyPluginManifest
        {
            MaxPaperInstances = 0,
            StartupPaper = new PaperBodyPluginStartupManifest
            {
                EnabledSetting = "autoStart", InstanceKey = "audit", Presentation = "capsule"
            },
            Settings = [new PaperBodyPluginSettingManifest
            {
                Id = "autoStart", Type = "boolean", Default = JsonSerializer.SerializeToElement(true)
            }]
        };
        descriptors[original.Id] = original with { Manifest = manifest };
        paper.StartupOwnerPluginId = original.Id;
        paper.StartupInstanceKey = "audit";
        window.SetCollapsedState(name == "startup-intent-form", animate: false, saveGeometry: false);
        Set(controller, "_startupShellPrewarmTask", barrier.Task);
        var generation = (int)Part(controller, "_paperSurfaceRestoreGeneration");
        Task? pending = null;
        try
        {
            if (name == "startup-intent-before-schedule") controller.HideAllPapers();
            pending = (Task)typeof(AppController).GetMethod("SchedulePluginStartupPapers", Private)!
                .Invoke(controller, [StartupCommandKind.None, generation])!;
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
            Require(!pending.IsCompleted, "startup fixture never waited for its real shell task");
            switch (name)
            {
                case "startup-intent-hide-all": controller.HideAllPapers(); break;
                case "startup-intent-hide-one": controller.HidePaper(paper); break;
                case "startup-intent-form": window.SetCollapsedState(false, animate: false, saveGeometry: false); break;
                case "startup-intent-delete": controller.DeletePaper(paper); break;
                case "startup-intent-other-paper": controller.HidePaper(controller.State.Papers.First(p => p != paper)); break;
                case "startup-intent-unchanged":
                case "startup-intent-before-schedule": break;
                default: throw new ArgumentException(name);
            }
            barrier.SetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            switch (name)
            {
                case "startup-intent-hide-all":
                case "startup-intent-hide-one":
                case "startup-intent-before-schedule":
                    Require(!paper.IsVisible, "STARTUP_OVERRIDES_HIDE: delayed startup revived an explicitly hidden paper");
                    break;
                case "startup-intent-form":
                    Require(!paper.IsCollapsed, "STARTUP_OVERRIDES_FORM: delayed startup overwrote an explicit expansion");
                    break;
                case "startup-intent-delete":
                    Require(!controller.State.Papers.Any(p => p.StartupOwnerPluginId == original.Id && p.StartupInstanceKey == "audit"),
                        "STARTUP_RECREATES_DELETED: delayed startup recreated the just-deleted paper");
                    break;
                default:
                    Require(paper.IsVisible && paper.IsCollapsed,
                        "STARTUP_UNRELATED_CANCEL: untouched startup policy did not execute");
                    break;
            }
            Console.WriteLine("PASS " + name);
        }
        finally
        {
            barrier.TrySetResult();
            if (pending != null) await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Set(controller, "_startupShellPrewarmTask", shells);
            descriptors[original.Id] = original;
            paper.StartupOwnerPluginId = owner;
            paper.StartupInstanceKey = instance;
        }
    }

    private static object Part(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
