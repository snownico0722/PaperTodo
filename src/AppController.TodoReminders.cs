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
    private static readonly TimeSpan ReminderAdvanceNotice = TimeSpan.FromSeconds(5);

    // One short-lived token per pending Todo occurrence, invalidated by edits/rescheduling.
    // Tokens do not persist: overdue reminders after startup use the usual host notification.
    private sealed class AdvanceReminderNotice(PaperData paper, PaperItem item, DateTimeOffset dueAt)
    {
        public PaperData Paper { get; } = paper;
        public PaperItem Item { get; } = item;
        public DateTimeOffset DueAt { get; } = dueAt;
        public Guid NotificationId { get; } = Guid.NewGuid();
        public bool SuppressDefault { get; set; }
    }

    private readonly Dictionary<PaperItem, AdvanceReminderNotice> _advanceReminderNotices =
        new(ReferenceEqualityComparer.Instance);
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

    private void PruneAdvanceReminderNotices()
    {
        foreach (var notice in _advanceReminderNotices.Values.ToArray())
        {
            if (!State.ExperimentalTodoReminders || !TodoReminderPolicy.IsPending(notice.Item) ||
                notice.Item.ReminderAt != notice.DueAt ||
                !State.Papers.Contains(notice.Paper) || !notice.Paper.Items.Contains(notice.Item))
                _advanceReminderNotices.Remove(notice.Item);
        }
    }

    // Once an advance notice was sent, schedule the actual due time, not the already
    // elapsed five-second lead time. During retry, skip overdue items to avoid a hot loop.
    private DateTimeOffset NextReminderWake(DateTimeOffset now, bool includeOverdue)
    {
        var next = DateTimeOffset.MaxValue;
        foreach (var paper in State.Papers.Where(p => p.Type == PaperTypes.Todo))
        {
            foreach (var item in paper.Items.Where(TodoReminderPolicy.IsPending))
            {
                var dueAt = item.ReminderAt!.Value;
                if (dueAt <= now)
                {
                    if (includeOverdue) return now;
                    continue;
                }
                var wake = _advanceReminderNotices.ContainsKey(item)
                    ? dueAt
                    : dueAt - ReminderAdvanceNotice;
                if (wake < next) next = wake;
            }
        }
        return next;
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
        PruneAdvanceReminderNotices();
        if (IsExiting || !State.ExperimentalTodoReminders)
            return;

        var now = DateTimeOffset.Now;
        var nextWake = NextReminderWake(now, includeOverdue: true);
        if (nextWake == DateTimeOffset.MaxValue)
            return;

        var delay = nextWake - now;
        var interval = delay <= TodoReminderMinimumTimerInterval
            ? TodoReminderMinimumTimerInterval
            : delay >= TodoReminderMaximumTimerInterval
                ? TodoReminderMaximumTimerInterval
                : delay;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        timer.Tick += OnTodoReminderTimerTick;
        _todoReminderTimer = timer;
        timer.Start();
    }

    // A plugin receives this event five seconds before due (or as soon as possible if a
    // reminder is scheduled with fewer than five seconds left). It can call the explicit
    // suppression API before due; the normal notification is never blocked awaiting JS.
    private void PublishUpcomingTodoReminders(DateTimeOffset now)
    {
        PruneAdvanceReminderNotices();
        if (IsExiting || !State.ExperimentalTodoReminders)
            return;

        var operationId = Guid.NewGuid();
        var events = new List<PaperTodo.Plugin.TodoReminderUpcomingEvent>();
        foreach (var paper in State.Papers.Where(p => p.Type == PaperTypes.Todo))
        {
            foreach (var item in paper.Items.Where(TodoReminderPolicy.IsPending))
            {
                var dueAt = item.ReminderAt!.Value;
                if (dueAt <= now || dueAt > now + ReminderAdvanceNotice ||
                    _advanceReminderNotices.ContainsKey(item))
                    continue;

                var notice = new AdvanceReminderNotice(paper, item, dueAt);
                _advanceReminderNotices.Add(item, notice);
                events.Add(new PaperTodo.Plugin.TodoReminderUpcomingEvent(
                    CaptureTodoSnapshot(paper, item), notice.NotificationId,
                    new(Guid.NewGuid(), operationId, now, PaperTodo.Plugin.PaperTodoEventOrigin.System, null)));
            }
        }
        _paperBodyPluginEvents?.PublishReminderUpcoming(events);
    }

    // Suppress only this default presentation; the Todo and reminder time are untouched.
    // The host treats the occurrence as triggered when it reaches its due time.
    internal bool SuppressDefaultReminderNotification(Guid notificationId)
    {
        if (IsExiting || !State.ExperimentalTodoReminders) return false;
        PruneAdvanceReminderNotices();
        var notice = _advanceReminderNotices.Values.FirstOrDefault(
            value => value.NotificationId == notificationId);
        if (notice == null || notice.DueAt <= DateTimeOffset.Now) return false;
        notice.SuppressDefault = true;
        return true;
    }

    private bool IsDefaultReminderSuppressed(PaperItem item) =>
        _advanceReminderNotices.TryGetValue(item, out var notice) &&
        notice.SuppressDefault && notice.DueAt == item.ReminderAt;

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
        if (IsExiting || !State.ExperimentalTodoReminders)
        {
            return;
        }

        TimeZoneInfo.ClearCachedData();
        RequestImmediateTodoReminderCheck();
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
        PublishUpcomingTodoReminders(now);
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
                .Where(item => TodoReminderPolicy.IsDue(item, now))
                .Select(item => (Paper: paper, Item: item)))
            .ToList();
        if (due.Count == 0)
        {
            RefreshTodoReminderSchedule();
            return;
        }

        // Record pending user changes without executing plugin callbacks. They are
        // delivered after host presentation, ahead of the system reminder transitions.
        var publishPending = _paperBodyPluginEvents?.CapturePendingChangesForReminder();
        Action? publishPresentation = null;
        try
        {
            ProcessDueTodoReminderPresentations(now, due, ref publishPending, ref publishPresentation);
        }
        finally
        {
            // Even on a delivery failure, pending user changes must not disappear.
            publishPending?.Invoke();
            publishPresentation?.Invoke();
        }
    }

    private void ProcessDueTodoReminderPresentations(
        DateTimeOffset now, List<(PaperData Paper, PaperItem Item)> due,
        ref Action? publishPending, ref Action? publishPresentation)
    {
        var suppressed = due.Where(entry => IsDefaultReminderSuppressed(entry.Item)).ToList();
        var defaultDue = due.Where(entry => !IsDefaultReminderSuppressed(entry.Item)).ToList();

        var openedFirst = false;
        try
        {
            if (defaultDue.Count > 0)
            {
                OpenTodoReminderTarget(defaultDue[0].Paper, defaultDue[0].Item);
                openedFirst = true;
            }
        }
        catch
        {
            // A stale paper surface may still allow the tray notification to surface.
        }

        var balloonSurfaced = false;
        try
        {
            if (defaultDue.Count > 0)
                balloonSurfaced = ShowTodoReminderBalloon(defaultDue);
        }
        catch
        {
            // Keep reminders pending when their delivery path was not available.
        }

        // Showing the first default reminder can open a previously hidden Paper.
        // Attribute that visibility/collapse transition to the system, not a later user scan.
        publishPresentation = defaultDue.Count > 0
            ? _paperBodyPluginEvents?.CaptureReminderPresentationChanges(defaultDue[0].Paper, now)
            : null;

        var deliveredCount = TodoReminderPolicy.DeliveredCount(defaultDue.Count, openedFirst, balloonSurfaced);
        if (deliveredCount == 0 && suppressed.Count == 0)
        {
            ScheduleTodoReminderRetry();
            return;
        }

        // Plugin-suppressed occurrences are acknowledged without host UI or sound.
        // Other due reminders retain the existing partial-delivery and retry rules.
        var delivered = suppressed.Concat(defaultDue.Take(deliveredCount)).ToList();
        if (deliveredCount > 0)
            PlayTodoReminderSound();

        var beforeTriggered = delivered.Select(entry => CaptureTodoSnapshot(entry.Paper, entry.Item)).ToArray();
        foreach (var (paper, item) in delivered)
        {
            item.ReminderTriggered = true;
            if (item.ReminderAt is { } reminderAt &&
                _windows.TryGetValue(paper.Id, out var window))
            {
                window.PreserveTriggeredTodoReminderInHistory(item.Id, reminderAt);
            }
        }

        foreach (var paperGroup in delivered.GroupBy(
                     entry => entry.Paper.Id,
                     StringComparer.Ordinal))
        {
            if (_windows.TryGetValue(paperGroup.Key, out var window))
            {
                window.RefreshTodoReminderAfterTrigger(
                    paperGroup.Select(entry => entry.Item.Id));
            }
        }

        // Freeze the whole batch before invoking plugins. A listener may synchronously edit or
        // delete another item; its callback must not change which reminders this batch surfaced.
        var triggered = delivered.Select(entry => CaptureTodoSnapshot(entry.Paper, entry.Item)).ToArray();
        var publishChanges = _paperBodyPluginEvents?.CaptureTriggeredReminderChanges(beforeTriggered, triggered, now);
        SaveNow();
        if (delivered.Count < due.Count)
        {
            ScheduleTodoReminderRetry();
        }
        else
        {
            RefreshTodoReminderSchedule();
        }

        // Ordinary TodoChanged callbacks run only after the host's presentation
        // decision is final. Preserve user-before-system ordering without a hidden hook.
        publishPending?.Invoke();
        publishPending = null;
        publishPresentation?.Invoke();
        publishPresentation = null;
        publishChanges?.Invoke();
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
        PruneAdvanceReminderNotices();
        if (IsExiting || !State.ExperimentalTodoReminders)
            return;

        // Retry failed/partial delivery within thirty seconds, but honor a different
        // reminder's earlier advance-notice or due-time wake.
        var interval = TimeSpan.FromSeconds(30);
        var now = DateTimeOffset.Now;
        var nextFutureWake = NextReminderWake(now, includeOverdue: false);
        if (nextFutureWake != DateTimeOffset.MaxValue)
        {
            var untilNext = nextFutureWake - now;
            if (untilNext < interval) interval = untilNext;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = interval < TodoReminderMinimumTimerInterval
                ? TodoReminderMinimumTimerInterval
                : interval
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

internal static class TodoReminderPolicy
{
    public static bool IsPending(PaperItem item) =>
        !item.Done && !item.ReminderTriggered && item.ReminderAt.HasValue;

    public static bool IsDue(PaperItem item, DateTimeOffset now) =>
        IsPending(item) && item.ReminderAt <= now;

    // A batch balloon represents every due item; opening one paper represents only its first item.
    public static int DeliveredCount(int dueCount, bool openedFirst, bool balloonSurfaced) =>
        balloonSurfaced ? dueCount : openedFirst ? Math.Min(1, dueCount) : 0;
}
