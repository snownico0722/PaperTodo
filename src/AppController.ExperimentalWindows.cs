using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class AppController
{
    // Stability requires both real observations and elapsed time. This avoids treating the first
    // frame after a render stall as stable, without making lifetime depend on refresh rate.
    private const int ExperimentalFollowMinimumStableSamples = 3;
    private static readonly TimeSpan ExperimentalFollowStableDuration =
        TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ExperimentalFollowMovingIdleDuration =
        TimeSpan.FromSeconds(2);
    private ExternalWindowTracker? _externalWindowTracker;
    private readonly HashSet<IntPtr> _externalMoveSizeWindows = new();
    private bool _experimentalFollowRendering;
    private long _experimentalFollowLastChangeTimestamp;
    private int _experimentalFollowStableSamples;

    private bool NeedsExternalWindowTracker =>
        _windows.Values.Any(window =>
            window.HasExperimentalExternalWindowAttachment);

    internal void NotifyExperimentalWindowAttachmentChanged()
    {
        if (!IsExiting)
        {
            RefreshExperimentalWindowRuntime();
        }
    }

    private void RefreshExperimentalWindowRuntime()
    {
        if (IsExiting)
        {
            DisposeExperimentalWindowRuntime();
            return;
        }

        if (NeedsExternalWindowTracker)
        {
            if (_externalWindowTracker == null)
            {
                var tracker = new ExternalWindowTracker(
                    Application.Current.Dispatcher);
                tracker.Changed += OnExternalWindowChanged;
                _externalWindowTracker = tracker;
            }
            return;
        }

        DisposeExternalWindowTracker();
    }

    private void OnExternalWindowChanged(ExternalWindowEvent windowEvent)
    {
        if (IsExiting || !NeedsExternalWindowTracker)
        {
            return;
        }

        if ((windowEvent.Kind &
             ExternalWindowEventKind.MoveSizeStarted) != 0)
        {
            _externalMoveSizeWindows.Add(windowEvent.Handle);
        }
        if ((windowEvent.Kind &
             (ExternalWindowEventKind.MoveSizeEnded |
              ExternalWindowEventKind.Destroyed)) != 0)
        {
            _externalMoveSizeWindows.Remove(windowEvent.Handle);
        }

        var followsEventWindow = false;
        foreach (var window in _windows.Values.ToList())
        {
            followsEventWindow |=
                window.TracksExperimentalExternalWindow(
                    windowEvent.Handle);
            window.HandleExternalWindowEvent(windowEvent);
        }

        if (followsEventWindow &&
            (windowEvent.Kind &
             (ExternalWindowEventKind.Location |
              ExternalWindowEventKind.MoveSizeStarted |
              ExternalWindowEventKind.MoveSizeEnded |
              ExternalWindowEventKind.MinimizeEnded |
              ExternalWindowEventKind.Uncloaked)) != 0)
        {
            BeginExperimentalFollowFrames();
        }
    }

    private void BeginExperimentalFollowFrames()
    {
        _experimentalFollowLastChangeTimestamp = Stopwatch.GetTimestamp();
        _experimentalFollowStableSamples = 0;
        if (_experimentalFollowRendering ||
            IsExiting ||
            !NeedsExternalWindowTracker)
        {
            return;
        }

        CompositionTarget.Rendering +=
            OnExperimentalFollowRendering;
        _experimentalFollowRendering = true;
    }

    internal void RequestExperimentalWindowFrames()
    {
        if (NeedsExternalWindowTracker)
        {
            BeginExperimentalFollowFrames();
        }
    }

    private void OnExperimentalFollowRendering(
        object? sender,
        EventArgs e)
    {
        if (IsExiting || !NeedsExternalWindowTracker)
        {
            StopExperimentalFollowFrames();
            return;
        }

        var hasAttachment = false;
        var targetMoving = false;
        var changed = false;
        foreach (var window in _windows.Values.ToList())
        {
            if (!window.RefreshExperimentalAttachmentFrame(
                    out var targetHandle,
                    out var windowChanged))
            {
                continue;
            }

            hasAttachment = true;
            changed |= windowChanged;
            targetMoving |=
                _externalMoveSizeWindows.Contains(targetHandle);
        }

        if (!hasAttachment)
        {
            StopExperimentalFollowFrames();
            return;
        }

        if (changed)
        {
            _experimentalFollowLastChangeTimestamp =
                Stopwatch.GetTimestamp();
            _experimentalFollowStableSamples = 0;
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (_experimentalFollowLastChangeTimestamp == 0)
        {
            _experimentalFollowLastChangeTimestamp = now;
            return;
        }

        if (_experimentalFollowStableSamples < int.MaxValue)
        {
            _experimentalFollowStableSamples++;
        }
        var idleDuration = targetMoving
            ? ExperimentalFollowMovingIdleDuration
            : ExperimentalFollowStableDuration;
        var idleElapsed = TimeSpan.FromSeconds(
            Math.Max(
                0,
                now - _experimentalFollowLastChangeTimestamp) /
            (double)Stopwatch.Frequency);
        if (_experimentalFollowStableSamples >=
                ExperimentalFollowMinimumStableSamples &&
            idleElapsed >= idleDuration)
        {
            // A missed MOVESIZEEND must not leave a render-rate Win32 sampler alive forever.
            // A later location event starts it again if the target resumes moving.
            if (targetMoving)
            {
                _externalMoveSizeWindows.Clear();
            }
            StopExperimentalFollowFrames();
        }
    }

    private void StopExperimentalFollowFrames()
    {
        if (_experimentalFollowRendering)
        {
            CompositionTarget.Rendering -=
                OnExperimentalFollowRendering;
            _experimentalFollowRendering = false;
        }
        _experimentalFollowLastChangeTimestamp = 0;
        _experimentalFollowStableSamples = 0;
    }

    private void ToggleExperimentalCapsuleMagnetism() =>
        SetSettingFromUi("window.magnet_enabled", !State.ExperimentalCapsuleMagnetism);

    private void ToggleExperimentalCapsuleMagnetScreenEdges() =>
        SetSettingFromUi("window.magnet_screen_edges", !State.ExperimentalCapsuleMagnetScreenEdges);

    private void ToggleExperimentalCapsuleMagnetWindowEdges() =>
        SetSettingFromUi("window.magnet_window_edges", !State.ExperimentalCapsuleMagnetWindowEdges);

    private void SetExperimentalCapsuleMagnetDistance(int distance) =>
        SetSettingFromUi("window.magnet_distance", ExperimentalWindowAttachmentOptions.NormalizeSnapDistance( distance));

    private void ToggleExperimentalWindowTethering() =>
        SetSettingFromUi("window.tether_enabled", !State.ExperimentalWindowTethering);

    private void SetExperimentalWindowTetherPreferredEdge(string edge) =>
        SetSettingFromUi("window.tether_edge", ExperimentalWindowTetherOptions.NormalizeEdge(edge));

    private void SetExperimentalWindowTetherGap(int gap) =>
        SetSettingFromUi("window.tether_gap", ExperimentalWindowTetherOptions.NormalizeGap(gap));

    private void ToggleExperimentalTetherVisibilityLink() =>
        SetSettingFromUi("window.tether_visibility_link", !State.ExperimentalTetherVisibilityLink);

    private void SetExperimentalTetherMinimizedBehavior(string behavior) =>
        SetSettingFromUi("window.tether_minimized", ExperimentalTetherVisibilityModes.Normalize(behavior));

    private void RefreshExperimentalAttachmentMenus()
    {
        foreach (var window in _windows.Values.ToList())
        {
            window.RefreshExperimentalAttachmentMenu();
        }
    }

    private void RefreshExperimentalAttachmentsAfterDisplayMetrics()
    {
        foreach (var window in _windows.Values.ToList())
        {
            window.RefreshExperimentalAttachmentForDisplayMetrics();
        }
    }

    private void DisposeExternalWindowTracker()
    {
        StopExperimentalFollowFrames();
        _externalMoveSizeWindows.Clear();
        if (_externalWindowTracker == null)
        {
            return;
        }

        _externalWindowTracker.Changed -= OnExternalWindowChanged;
        _externalWindowTracker.Dispose();
        _externalWindowTracker = null;
    }

    private void DisposeExperimentalWindowRuntime()
    {
        DisposeExternalWindowTracker();
        foreach (var window in _windows.Values.ToList())
        {
            window.DisposeExperimentalWindowAttachment();
        }
    }
}
