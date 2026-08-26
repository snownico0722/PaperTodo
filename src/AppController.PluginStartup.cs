using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private static readonly TimeSpan PluginStartupPaperPollInterval =
        TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PluginStartupPaperMaximumWait =
        TimeSpan.FromSeconds(5);
    private DispatcherTimer? _pluginStartupPaperTimer;
    private int _pluginStartupPaperGeneration;

    private void SchedulePluginStartupPapers(StartupCommandKind visibilityCommand)
    {
        _pluginStartupPaperTimer?.Stop();
        _pluginStartupPaperTimer = null;
        var generation = ++_pluginStartupPaperGeneration;
        if (IsExiting)
        {
            return;
        }

        // Per-paper Web runtimes are owned by real PaperData, including papers that start hidden.
        // They do not wait for visible shell construction or startupPaper presentation readiness.

        // Explicit --hide keeps the existing startup-paper behavior (do not create/show one), but
        // entity plugin papers already persisted in State still own their provider-level runtime.
        if (visibilityCommand == StartupCommandKind.Hide)
        {
            EnablePluginRuntimeReconciliation();
            return;
        }

        // Resolve the startup setting before deciding whether we need the deferred shell-ready
        // creation pass. If no startup paper is enabled, existing entity papers can own runtimes
        // immediately without waiting on the startup-paper timer.
        var candidates = PaperBodyPlugins.Descriptors
            .Where(descriptor =>
            {
                var startup = descriptor.Manifest?.StartupPaper;
                return startup != null && StartupSettingEnabled(descriptor, startup);
            })
            .ToArray();
        if (candidates.Length == 0)
        {
            EnablePluginRuntimeReconciliation();
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            EnablePluginRuntimeReconciliation();
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        var timer = new DispatcherTimer(
            DispatcherPriority.ApplicationIdle)
        {
            Interval = PluginStartupPaperPollInterval
        };
        timer.Tick += (_, _) =>
        {
            if (generation != _pluginStartupPaperGeneration ||
                IsExiting)
            {
                StopPluginStartupPaperTimer(timer);
                return;
            }

            var elapsed = TimeSpan.FromSeconds(
                Math.Max(0, Stopwatch.GetTimestamp() - startedAt) /
                (double)Stopwatch.Frequency);
            if (!StartupPaperCreationReady() &&
                elapsed < PluginStartupPaperMaximumWait)
            {
                return;
            }

            StopPluginStartupPaperTimer(timer);
            EnsurePluginStartupPapers(candidates);
            // startupPaper has now had first chance to create/restore its real plugin paper. Only
            // after that do we derive process-level plugin runtime ownership from final State.Papers.
            EnablePluginRuntimeReconciliation();
        };
        _pluginStartupPaperTimer = timer;
        timer.Start();
    }

    private bool StartupPaperCreationReady()
    {
        if (_isRestoringStartupPapers || _isPreparingStartupEdgeCapsules)
        {
            return false;
        }

        foreach (var paper in State.Papers.Where(item => item.IsVisible))
        {
            if (!_windows.TryGetValue(paper.Id, out var window) ||
                window.IsClosed ||
                !window.IsShellBuilt)
            {
                return false;
            }
        }
        return true;
    }

    private void StopPluginStartupPaperTimer(DispatcherTimer timer)
    {
        timer.Stop();
        if (ReferenceEquals(_pluginStartupPaperTimer, timer))
        {
            _pluginStartupPaperTimer = null;
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
