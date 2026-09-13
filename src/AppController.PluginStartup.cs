using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private int _pluginStartupPaperGeneration;

    private async void SchedulePluginStartupPapers(StartupCommandKind visibilityCommand)
    {
        var generation = ++_pluginStartupPaperGeneration;
        if (IsExiting) return;
        if (visibilityCommand == StartupCommandKind.Hide)
        {
            EnablePluginRuntimeReconciliation();
            return;
        }
        var candidates = PaperBodyPlugins.Descriptors.Where(descriptor =>
            descriptor.Manifest?.StartupPaper is { } startup && StartupSettingEnabled(descriptor, startup)).ToArray();
        if (candidates.Length == 0)
        {
            EnablePluginRuntimeReconciliation();
            return;
        }
        try
        {
            // Even an already-complete shell queue must not initialize a plugin inline in
            // StartAsync. Let the existing surfaces present and return startup command control.
            await Application.Current.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
            // The shell queue knows when it finished. Do not poll every 50ms (or wait on hidden
            // papers belonging to a monitor which has not appeared). A replacement queue wins.
            Task shells;
            do
            {
                shells = _startupShellPrewarmTask;
                await shells;
                if (IsExiting || generation != _pluginStartupPaperGeneration) return;
            } while (!ReferenceEquals(shells, _startupShellPrewarmTask));
            EnsurePluginStartupPapers(candidates);
            EnablePluginRuntimeReconciliation();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Plugin startup paper creation failed: {0}", ex);
        }
    }

    private void EnsurePluginStartupPapers(
        IReadOnlyList<PaperBodyPluginDescriptor> descriptors)
    {
        var changed = false;
        foreach (var descriptor in descriptors)
        {
            var startup = descriptor.Manifest?.StartupPaper;
            if (startup == null || !StartupSettingEnabled(descriptor, startup))
            {
                continue;
            }

            var paper = State.Papers.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.StartupOwnerPluginId,
                    descriptor.Id,
                    StringComparison.Ordinal) &&
                string.Equals(
                    candidate.StartupInstanceKey,
                    startup.InstanceKey,
                    StringComparison.Ordinal));
            if (paper != null &&
                (!string.Equals(
                     paper.BodyProviderId,
                     descriptor.Id,
                     StringComparison.Ordinal) ||
                 paper.Type != PaperTypes.Note))
            {
                // The user repurposed the previously generated paper. Do not take it over or
                // create a duplicate behind their back.
                continue;
            }

            if (paper == null)
            {
                if (!CanCreatePluginPaper(descriptor))
                {
                    continue;
                }
                paper = CreatePaper(PaperTypes.Note, show: false);
                if (paper == null)
                {
                    continue;
                }
                paper.BodyProviderId = descriptor.Id;
                paper.StartupOwnerPluginId = descriptor.Id;
                paper.StartupInstanceKey = startup.InstanceKey;
                if (!string.IsNullOrWhiteSpace(startup.Title))
                {
                    paper.Title = PaperTitles.CleanCustomTitle(
                        startup.Title,
                        State.MaxTitleLength);
                }
                changed = true;
            }

            var collapsed = startup.Presentation == "capsule";
            if (!paper.IsVisible || paper.IsCollapsed != collapsed)
            {
                paper.IsVisible = true;
                paper.IsCollapsed = collapsed;
                changed = true;
            }
            // A startup Paper becomes a Runtime owner before its visible Body is attached.
            EnablePluginRuntimeReconciliation();
            ShowPaper(paper, activate: false);
        }

        if (!changed)
        {
            return;
        }
        ArrangeDeepCapsules(
            animate: State.EnableAnimations,
            flushInitialPresentations: true);
        RefreshTrayMenu();
        MarkDirty();
    }

    private bool StartupSettingEnabled(
        PaperBodyPluginDescriptor descriptor,
        PaperBodyPluginStartupManifest startup)
    {
        try
        {
            using var document = JsonDocument.Parse(
                PaperBodyPlugins.DataStore.GetSettingsJson(descriptor));
            return document.RootElement.TryGetProperty(
                    startup.EnabledSetting,
                    out var value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                value.GetBoolean();
        }
        catch
        {
            return false;
        }
    }
}
