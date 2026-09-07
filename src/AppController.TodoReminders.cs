using System.IO;
using System.Media;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Application = System.Windows.Application;

namespace PaperTodo;

public sealed partial class AppController
{
    private const long TodoReminderMaximumCustomSoundBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan TodoReminderMaximumTimerInterval =
        TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TodoReminderMinimumTimerInterval =
        TimeSpan.FromMilliseconds(250);
    private SoundPlayer? _todoReminderCustomSoundPlayer;
    private long _todoReminderCustomSoundLength = -1;
    private DateTime _todoReminderCustomSoundWriteTimeUtc;

    internal void NotifyTodoReminderChanged(bool saveImmediately)
    {
        if (saveImmediately)
        {
            SaveNow();
        }
        else
        {
            MarkDirty();
        }

        RefreshTodoReminderSchedule();
    }

    internal void NotifyTodoReminderCollectionChanged()
    {
        if (State.ExperimentalTodoReminders)
        {
            RefreshTodoReminderSchedule();
        }
    }

    internal void RefreshTodoReminderFeature()
    {
        foreach (var window in _windows.Values)
        {
            window.UpdateTodoReminderFeature();
        }

        RefreshTodoReminderSchedule();
    }

    private void RefreshTodoReminderSchedule()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(
                (Action)RefreshTodoReminderSchedule,
                DispatcherPriority.Background);
            return;
        }

        StopTodoReminderTimer();
        if (IsExiting || !State.ExperimentalTodoReminders)
        {
            return;
        }

        var nextReminder = State.Papers
            .Where(paper => paper.Type == PaperTypes.Todo)
            .SelectMany(paper => paper.Items)
            .Where(item =>
                !item.Done &&
                !item.ReminderTriggered &&
                item.ReminderAt.HasValue)
            .Select(item => item.ReminderAt!.Value)
            .DefaultIfEmpty(DateTimeOffset.MaxValue)
            .Min();
        if (nextReminder == DateTimeOffset.MaxValue)
        {
            return;
        }

        var delay = nextReminder - DateTimeOffset.Now;
        var interval = delay <= TodoReminderMinimumTimerInterval
            ? TodoReminderMinimumTimerInterval
            : delay >= TodoReminderMaximumTimerInterval
                ? TodoReminderMaximumTimerInterval
                : delay;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = interval
        };
        timer.Tick += OnTodoReminderTimerTick;
        _todoReminderTimer = timer;
        timer.Start();
    }

    private void OnTodoReminderTimerTick(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _todoReminderTimer))
        {
            return;
        }

        CheckTodoRemindersNow();
    }

    private void RequestImmediateTodoReminderCheck()
    {
        if (IsExiting || !State.ExperimentalTodoReminders)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(
            (Action)CheckTodoRemindersNow,
            DispatcherPriority.Background);
    }

    private void OnSystemTimeChanged(
        object? sender,
        EventArgs e)
    {
        if (IsExiting)
        {
            return;
        }

        TimeZoneInfo.ClearCachedData();
        RequestImmediateTodoReminderCheck();
        RequestTodoRetentionCheck();
    }

    private void CheckTodoRemindersNow()
    {
        StopTodoReminderTimer();
        if (IsExiting || !State.ExperimentalTodoReminders)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        foreach (var window in _windows.Values)
        {
            window.RefreshTodoReminderCountdowns(now);
        }
        ProcessDueTodoReminders(now);
    }

    private void ProcessDueTodoReminders(DateTimeOffset now)
    {
        if (IsExiting || !State.ExperimentalTodoReminders)
        {
            return;
        }

        var due = State.Papers
            .Where(paper => paper.Type == PaperTypes.Todo)
            .SelectMany(paper => paper.Items
                .Where(item =>
                    !item.Done &&
                    !item.ReminderTriggered &&
                    item.ReminderAt is { } reminderAt &&
                    reminderAt <= now)
                .Select(item => (Paper: paper, Item: item)))
            .ToList();
        if (due.Count == 0)
        {
            RefreshTodoReminderSchedule();
            return;
        }

        var surfaced = false;
        try
        {
            OpenTodoReminderTarget(due[0].Paper, due[0].Item);
            surfaced = true;
        }
        catch
        {
            // A stale paper surface may still allow the tray notification to surface.
        }
        try
        {
            surfaced |= ShowTodoReminderBalloon(due);
        }
        catch
        {
            // Keep the reminder pending when neither delivery path is available.
        }

        if (!surfaced)
        {
            ScheduleTodoReminderRetry();
            return;
        }

        // Sound is an optional presentation channel. A bad custom file or unavailable system
        // sound must never keep a successfully surfaced reminder pending.
        PlayTodoReminderSound();

        foreach (var (paper, item) in due)
        {
            item.ReminderTriggered = true;
            if (item.ReminderAt is { } reminderAt &&
                _windows.TryGetValue(paper.Id, out var window))
            {
                window.PreserveTriggeredTodoReminderInHistory(item.Id, reminderAt);
            }
        }

        foreach (var paperGroup in due.GroupBy(
                     entry => entry.Paper.Id,
                     StringComparer.Ordinal))
        {
            if (_windows.TryGetValue(paperGroup.Key, out var window))
            {
                window.RefreshTodoReminderAfterTrigger(
                    paperGroup.Select(entry => entry.Item.Id));
            }
        }

        SaveNow();
        RefreshTodoReminderSchedule();
    }

    private void OpenTodoReminderTarget(
        PaperData paper,
        PaperItem item)
    {
        ShowPaper(paper, activate: false);
        if (_windows.TryGetValue(paper.Id, out var window))
        {
            window.PulseTodoReminderSurface();
            window.OpenTodoReminderItem(item.Id);
        }
    }

    private bool ShowTodoReminderBalloon(
        IReadOnlyList<(PaperData Paper, PaperItem Item)> due)
    {
        if (_trayIcon == null || due.Count == 0)
        {
            return false;
        }

        var first = due[0];
        var firstText = CompactTodoReminderText(first.Item.Text);
        var message = due.Count == 1
            ? Strings.Format(
                "TodoReminderBalloonSingleFormat",
                PaperTitleText(first.Paper),
                firstText)
            : Strings.Format(
                "TodoReminderBalloonMultipleFormat",
                due.Count,
                firstText);
        _trayIcon.ShowBalloonTip(
            Strings.Get("TodoReminderBalloonTitle"),
            message,
            BalloonIcon.Info);
        return true;
    }

    private void PlayTodoReminderSound()
    {
        if (!State.ExperimentalTodoReminderSoundEnabled)
        {
            return;
        }

        if (TryPlayCustomTodoReminderSound())
        {
            return;
        }

        try
        {
            TodoReminderSystemSound().Play();
        }
        catch
        {
            // Reminder delivery has already succeeded. Sound is best effort only.
        }
    }

    private bool TryPlayCustomTodoReminderSound()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "papertodo.wav");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists ||
                info.Length < 12 ||
                info.Length > TodoReminderMaximumCustomSoundBytes)
            {
                ResetTodoReminderCustomSoundCache();
                return false;
            }

            if (_todoReminderCustomSoundPlayer != null &&
                _todoReminderCustomSoundLength == info.Length &&
                _todoReminderCustomSoundWriteTimeUtc == info.LastWriteTimeUtc)
            {
                _todoReminderCustomSoundPlayer.Play();
                return true;
            }

            ResetTodoReminderCustomSoundCache();
            if (!HasWaveHeader(path))
            {
                return false;
            }

            var player = new SoundPlayer(path);
            player.Load();
            _todoReminderCustomSoundPlayer = player;
            _todoReminderCustomSoundLength = info.Length;
            _todoReminderCustomSoundWriteTimeUtc = info.LastWriteTimeUtc;
            player.Play();
            return true;
        }
        catch
        {
            ResetTodoReminderCustomSoundCache();
            return false;
        }
    }

    private static bool HasWaveHeader(string path)
    {
        Span<byte> header = stackalloc byte[12];
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Read(header) != header.Length)
        {
            return false;
        }

        return header[0] == (byte)'R' &&
            header[1] == (byte)'I' &&
            header[2] == (byte)'F' &&
            header[3] == (byte)'F' &&
            header[8] == (byte)'W' &&
            header[9] == (byte)'A' &&
            header[10] == (byte)'V' &&
            header[11] == (byte)'E';
    }

    private SystemSound TodoReminderSystemSound()
    {
        return TodoReminderSoundOptions.Normalize(
            State.ExperimentalTodoReminderSound) switch
        {
            TodoReminderSoundOptions.Beep => SystemSounds.Beep,
            TodoReminderSoundOptions.Exclamation => SystemSounds.Exclamation,
            TodoReminderSoundOptions.Hand => SystemSounds.Hand,
            TodoReminderSoundOptions.Question => SystemSounds.Question,
            _ => SystemSounds.Asterisk
        };
    }

    private void ResetTodoReminderCustomSoundCache()
    {
        _todoReminderCustomSoundPlayer?.Dispose();
        _todoReminderCustomSoundPlayer = null;
        _todoReminderCustomSoundLength = -1;
        _todoReminderCustomSoundWriteTimeUtc = default;
    }

    private void ScheduleTodoReminderRetry()
    {
        StopTodoReminderTimer();
        if (IsExiting || !State.ExperimentalTodoReminders)
        {
            return;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        timer.Tick += OnTodoReminderTimerTick;
        _todoReminderTimer = timer;
        timer.Start();
    }

    private static string CompactTodoReminderText(string? text)
    {
        var compact = string.Join(
            " ",
            (text ?? "")
                .Split(
                    new[] { '\r', '\n', '\t' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0));
        if (string.IsNullOrWhiteSpace(compact))
        {
            return Strings.Get("TodoReminderUnnamed");
        }

        const int maximumLength = 90;
        return compact.Length <= maximumLength
            ? compact
            : compact[..(maximumLength - 1)] + "…";
    }

    private void StopTodoReminderTimer()
    {
        if (_todoReminderTimer == null)
        {
            return;
        }

        _todoReminderTimer.Stop();
        _todoReminderTimer.Tick -= OnTodoReminderTimerTick;
        _todoReminderTimer = null;
    }
}
