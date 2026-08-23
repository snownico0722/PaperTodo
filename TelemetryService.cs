using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PaperTodo;

internal static class TelemetryService
{
    private const int SchemaVersion = 1;
    private const int WmHotkey = 0x0312;
    private const string Endpoint = "https://1251449999-60pjzyd4uu.ap-beijing.tencentscf.com";
    private const int MaxPendingReports = 31;
    private static readonly TimeSpan ActiveInputWindow = TimeSpan.FromSeconds(15);
    private static readonly object Gate = new();
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };
    private static readonly JsonSerializerOptions DiskJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions WireJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    private static readonly ConditionalWeakTable<MarkdownTextBox, PreviewFocusState> PreviewFocusStates = new();

    private static readonly string StatePath = Path.Combine(AppContext.BaseDirectory, "telemetry.json");
    private static readonly string CrashPath = Path.Combine(AppContext.BaseDirectory, "telemetry-crash.json");

    private static TelemetryPersistedState _persisted = LoadPersistedState();
    private static AppController? _controller;
    private static DispatcherTimer? _timer;
    private static UsageSnapshot? _previousSnapshot;
    private static DateTimeOffset _lastInputUtc = DateTimeOffset.MinValue;
    private static int _ticks;
    private static bool _attached;
    private static bool _runtimeHooksRegistered;
    private static bool _uploading;

    public static bool Enabled
    {
        get
        {
            lock (Gate)
            {
                return _persisted.Enabled;
            }
        }
    }

    public static void Attach(AppController controller)
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        _controller = controller;
        RegisterRuntimeHooksOnce();

        lock (Gate)
        {
            NormalizePersistedState(_persisted);
            if (!_persisted.Enabled)
            {
                ClearQueuedDataLocked();
            }
            else
            {
                // Merge crash markers before and after rollover so a crash around local midnight
                // lands on the correct local day before that day is finalized.
                MergeCrashMarkersLocked();
                EnsureCurrentDayLocked(controller.State, countLaunch: true);
                MergeCrashMarkersLocked();
            }
            SaveLocked();
        }

        _previousSnapshot = CaptureSnapshot(controller.State, previous: null);
        InputManager.Current.PreProcessInput += OnPreProcessInput;
        ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        // Only completed previous-day reports are queued. Send the whole backlog in one request.
        _ = UploadPendingBatchAsync();
    }

    public static void Detach()
    {
        if (!_attached)
        {
            return;
        }

        _timer?.Stop();
        if (_timer != null)
        {
            _timer.Tick -= OnTimerTick;
            _timer = null;
        }

        InputManager.Current.PreProcessInput -= OnPreProcessInput;
        ComponentDispatcher.ThreadPreprocessMessage -= OnThreadPreprocessMessage;

        try
        {
            CaptureTransitionsAndSnapshot();
        }
        catch
        {
            // Anonymous statistics must never affect shutdown.
        }

        lock (Gate)
        {
            SaveLocked();
        }

        _attached = false;
        _controller = null;
        _previousSnapshot = null;
        _lastInputUtc = DateTimeOffset.MinValue;
        _ticks = 0;
    }

    public static void SetEnabled(bool enabled)
    {
        lock (Gate)
        {
            if (_persisted.Enabled == enabled)
            {
                return;
            }

            _persisted.Enabled = enabled;
            if (!enabled)
            {
                ClearQueuedDataLocked();
            }
            else if (_controller != null)
            {
                EnsureCurrentDayLocked(_controller.State, countLaunch: true, resetCurrentDay: true);
            }
            SaveLocked();
        }

        if (_controller != null)
        {
            _previousSnapshot = CaptureSnapshot(_controller.State, previous: null);
        }
    }

    public static void RecordEmergencyCrash()
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                Dictionary<string, int> counts;
                try
                {
                    counts = File.Exists(CrashPath)
                        ? JsonSerializer.Deserialize<Dictionary<string, int>>(
                                File.ReadAllText(CrashPath),
                                DiskJsonOptions)
                            ?? new Dictionary<string, int>(StringComparer.Ordinal)
                        : new Dictionary<string, int>(StringComparer.Ordinal);
                }
                catch
                {
                    counts = new Dictionary<string, int>(StringComparer.Ordinal);
                }

                var date = LocalDateString();
                counts[date] = counts.TryGetValue(date, out var current)
                    ? AddBounded(current, 1, 1000)
                    : 1;
                WriteAtomic(CrashPath, JsonSerializer.Serialize(counts, DiskJsonOptions));
            }
        }
        catch
        {
            // Best effort only; never interfere with the real crash path.
        }
    }

    private static void ClearQueuedDataLocked()
    {
        _persisted.PendingReports.Clear();
        _persisted.CurrentDay = null;
        TryDeleteCrashMarker();
    }

    private static void RegisterRuntimeHooksOnce()
    {
        if (_runtimeHooksRegistered)
        {
            return;
        }

        _runtimeHooksRegistered = true;
        EventManager.RegisterClassHandler(
            typeof(MarkdownTextBox),
            Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnMarkdownGotKeyboardFocus),
            true);
        EventManager.RegisterClassHandler(
            typeof(MarkdownTextBox),
            Keyboard.LostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnMarkdownLostKeyboardFocus),
            true);
    }

    private static PreviewFocusState PreviewStateFor(MarkdownTextBox box)
    {
        return PreviewFocusStates.GetValue(box, static _ => new PreviewFocusState());
    }

    private static void OnMarkdownGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not MarkdownTextBox box)
        {
            return;
        }

        var state = PreviewStateFor(box);
        state.Generation++;
        state.WasEditing = true;
    }

    private static void OnMarkdownLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not MarkdownTextBox box || !Enabled || !_attached)
        {
            return;
        }

        var state = PreviewStateFor(box);
        if (!state.WasEditing)
        {
            return;
        }

        var generation = ++state.Generation;
        box.Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (!Enabled || !_attached || state.Generation != generation || !box.IsPreviewMode)
                {
                    return;
                }

                state.WasEditing = false;
                RecordCounter(day => day.MarkdownPreview = AddBounded(day.MarkdownPreview, 1, 100000));
            }),
            DispatcherPriority.ContextIdle);
    }

    private static void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (!Enabled)
        {
            return;
        }

        if (e.StagingItem.Input is MouseEventArgs or KeyboardEventArgs)
        {
            _lastInputUtc = DateTimeOffset.UtcNow;
        }
    }

    private static void OnThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        if (!Enabled || !_attached || msg.message != WmHotkey)
        {
            return;
        }

        _lastInputUtc = DateTimeOffset.UtcNow;
        RecordCounter(day => day.HotkeyTriggered = AddBounded(day.HotkeyTriggered, 1, 100000));
    }

    private static void RecordCounter(Action<TelemetryDayState> update)
    {
        var controller = _controller;
        if (controller == null || !Enabled)
        {
            return;
        }

        lock (Gate)
        {
            EnsureCurrentDayLocked(controller.State, countLaunch: false);
            if (_persisted.CurrentDay != null)
            {
                update(_persisted.CurrentDay);
            }
        }
    }

    private static void OnTimerTick(object? sender, EventArgs e)
    {
        var controller = _controller;
        if (controller == null || !_attached || !Enabled)
        {
            return;
        }

        try
        {
            var recentlyActive = DateTimeOffset.UtcNow - _lastInputUtc <= ActiveInputWindow;
            lock (Gate)
            {
                EnsureCurrentDayLocked(controller.State, countLaunch: false);
                if (_persisted.CurrentDay != null && recentlyActive)
                {
                    _persisted.CurrentDay.ActiveSeconds = AddBounded(
                        _persisted.CurrentDay.ActiveSeconds,
                        1,
                        86400);
                }
            }

            _ticks++;
            if ((_ticks % 2 == 0 && recentlyActive) || _ticks % 30 == 0)
            {
                CaptureTransitionsAndSnapshot();
            }
            if (_ticks % 30 == 0)
            {
                lock (Gate)
                {
                    SaveLocked();
                }
            }
        }
        catch
        {
            // Product behavior always wins over anonymous statistics.
        }
    }

    private static void CaptureTransitionsAndSnapshot()
    {
        var controller = _controller;
        if (controller == null || !Enabled)
        {
            return;
        }

        var previous = _previousSnapshot;
        var current = CaptureSnapshot(controller.State, previous);
        _previousSnapshot = current;

        lock (Gate)
        {
            EnsureCurrentDayLocked(controller.State, countLaunch: false);
            var day = _persisted.CurrentDay;
            if (day == null)
            {
                return;
            }

            ApplySnapshotMetrics(day, current);
            if (previous == null)
            {
                return;
            }

            foreach (var (paperId, currentPaper) in current.Papers)
            {
                if (!previous.Papers.TryGetValue(paperId, out var previousPaper))
                {
                    day.PaperCreated = AddBounded(day.PaperCreated, 1, 10000);
                    continue;
                }

                if (previousPaper.IsCollapsed != currentPaper.IsCollapsed)
                {
                    if (currentPaper.IsCollapsed)
                    {
                        day.PillCollapse = AddBounded(day.PillCollapse, 1, 100000);
                    }
                    else
                    {
                        day.PillExpand = AddBounded(day.PillExpand, 1, 100000);
                    }
                }

                if (currentPaper.Type == PaperTypes.Todo && previousPaper.Type == PaperTypes.Todo)
                {
                    foreach (var (itemId, currentItem) in currentPaper.TodoItems)
                    {
                        if (!previousPaper.TodoItems.TryGetValue(itemId, out var previousItem))
                        {
                            if (currentItem.HasText)
                            {
                                day.TodoCreated = AddBounded(day.TodoCreated, 1, 100000);
                            }
                            if (currentItem.Done && currentItem.HasText)
                            {
                                day.TodoCompleted = AddBounded(day.TodoCompleted, 1, 100000);
                            }
                            continue;
                        }

                        if (!previousItem.HasText && currentItem.HasText)
                        {
                            day.TodoCreated = AddBounded(day.TodoCreated, 1, 100000);
                        }
                        if (!previousItem.Done && currentItem.Done && currentItem.HasText)
                        {
                            day.TodoCompleted = AddBounded(day.TodoCompleted, 1, 100000);
                        }
                    }
                }

                if (currentPaper.Type == PaperTypes.Note && previousPaper.Type == PaperTypes.Note)
                {
                    var inserted = Math.Max(0, currentPaper.ImageCount - previousPaper.ImageCount);
                    if (inserted > 0)
                    {
                        day.ImageInserted = AddBounded(day.ImageInserted, inserted, 10000);
                    }
                }
            }

            var deleted = previous.Papers.Keys.Count(id => !current.Papers.ContainsKey(id));
            if (deleted > 0)
            {
                day.PaperDeleted = AddBounded(day.PaperDeleted, deleted, 10000);
            }
        }
    }

    private static UsageSnapshot CaptureSnapshot(AppState state, UsageSnapshot? previous)
    {
        var snapshot = new UsageSnapshot();
        foreach (var paper in state.Papers)
        {
            PaperUsageSnapshot? previousPaper = null;
            previous?.Papers.TryGetValue(paper.Id, out previousPaper);

            var itemStates = new Dictionary<string, TodoItemSnapshot>(StringComparer.Ordinal);
            if (paper.Type == PaperTypes.Todo)
            {
                foreach (var item in paper.Items)
                {
                    itemStates[item.Id] = new TodoItemSnapshot(
                        item.Done,
                        !string.IsNullOrWhiteSpace(item.Text));
                }
            }

            var content = paper.Content ?? "";
            var contentIdentity = RuntimeHelpers.GetHashCode(content);
            var imageCount = 0;
            if (paper.Type == PaperTypes.Note)
            {
                if (previousPaper != null &&
                    previousPaper.ContentIdentity == contentIdentity &&
                    previousPaper.ContentLength == content.Length)
                {
                    imageCount = previousPaper.ImageCount;
                }
                else if (content.Length > 0)
                {
                    try
                    {
                        imageCount = MarkdownImageReferences
                            .CollectImageIds(MarkdownImageReferences.StripRenderMarkers(content))
                            .Distinct(StringComparer.Ordinal)
                            .Count();
                    }
                    catch
                    {
                        imageCount = previousPaper?.ImageCount ?? 0;
                    }
                }
            }

            snapshot.Papers[paper.Id] = new PaperUsageSnapshot(
                paper.Type,
                paper.IsCollapsed,
                itemStates,
                contentIdentity,
                content.Length,
                imageCount);
        }

        snapshot.PaperCount = state.Papers.Count;
        snapshot.TodoPaperCount = state.Papers.Count(paper => paper.Type == PaperTypes.Todo);
        snapshot.NotePaperCount = state.Papers.Count(paper => paper.Type == PaperTypes.Note);
        snapshot.PillCount = state.Papers.Count(paper => paper.IsVisible && paper.IsCollapsed);
        snapshot.PillEnabled = state.UseCapsuleMode;
        return snapshot;
    }

    private static void ApplySnapshotMetrics(TelemetryDayState day, UsageSnapshot snapshot)
    {
        day.PaperCount = Math.Clamp(snapshot.PaperCount, 0, 10000);
        day.TodoPaperCount = Math.Clamp(snapshot.TodoPaperCount, 0, 10000);
        day.NotePaperCount = Math.Clamp(snapshot.NotePaperCount, 0, 10000);
        day.PillCount = Math.Clamp(snapshot.PillCount, 0, 10000);
        day.PillEnabled = snapshot.PillEnabled;
    }

    private static void ApplyStateMetrics(TelemetryDayState day, AppState state)
    {
        day.PaperCount = Math.Clamp(state.Papers.Count, 0, 10000);
        day.TodoPaperCount = Math.Clamp(state.Papers.Count(paper => paper.Type == PaperTypes.Todo), 0, 10000);
        day.NotePaperCount = Math.Clamp(state.Papers.Count(paper => paper.Type == PaperTypes.Note), 0, 10000);
        day.PillCount = Math.Clamp(state.Papers.Count(paper => paper.IsVisible && paper.IsCollapsed), 0, 10000);
        day.PillEnabled = state.UseCapsuleMode;
    }

    private static void EnsureCurrentDayLocked(
        AppState state,
        bool countLaunch,
        bool resetCurrentDay = false)
    {
        var today = LocalDateString();
        if (resetCurrentDay || _persisted.CurrentDay == null)
        {
            _persisted.CurrentDay = CreateDay(today, state);
        }
        else if (!string.Equals(_persisted.CurrentDay.Date, today, StringComparison.Ordinal))
        {
            FinalizeCurrentDayLocked();
            _persisted.CurrentDay = CreateDay(today, state);
        }
        else
        {
            RefreshEnvironment(_persisted.CurrentDay);
            ApplyStateMetrics(_persisted.CurrentDay, state);
        }

        if (countLaunch && _persisted.CurrentDay != null)
        {
            _persisted.CurrentDay.LaunchCount = AddBounded(
                _persisted.CurrentDay.LaunchCount,
                1,
                1000);
        }
    }

    private static TelemetryDayState CreateDay(string date, AppState state)
    {
        var day = new TelemetryDayState { Date = date };
        RefreshEnvironment(day);
        ApplyStateMetrics(day, state);
        return day;
    }

    private static void RefreshEnvironment(TelemetryDayState day)
    {
        day.AppVersion = AppVersion();
        day.Locale = UiLanguages.EffectiveUiCulture.Name;
        (day.CountryCode, day.Country) = Country();
        day.TimezoneOffset = (int)Math.Clamp(
            TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.Now).TotalMinutes,
            -14 * 60,
            14 * 60);
        try
        {
            day.MonitorCount = Math.Clamp(System.Windows.Forms.Screen.AllScreens.Length, 1, 32);
        }
        catch
        {
            day.MonitorCount = 1;
        }
    }

    private static void FinalizeCurrentDayLocked()
    {
        var day = _persisted.CurrentDay;
        if (day == null || !day.HasUsage())
        {
            return;
        }

        var report = CreateReport(day);
        _persisted.PendingReports.RemoveAll(existing =>
            string.Equals(existing.ReportId, report.ReportId, StringComparison.Ordinal));
        _persisted.PendingReports.Add(report);
        TrimPendingReportsLocked();
    }

    private static TelemetryReport CreateReport(TelemetryDayState day)
    {
        return new TelemetryReport
        {
            Kind = "daily_usage",
            SchemaVersion = SchemaVersion,
            ReportId = $"{_persisted.InstallId}_{day.Date}_v{SchemaVersion}",
            InstallId = _persisted.InstallId,
            Date = day.Date,
            TelemetryFirstSeenDate = _persisted.FirstSeenDate,
            AppVersion = day.AppVersion,
            Locale = day.Locale,
            CountryCode = day.CountryCode,
            Country = day.Country,
            TimezoneOffset = day.TimezoneOffset,
            MonitorCount = day.MonitorCount,
            LaunchCount = day.LaunchCount,
            ActiveSeconds = day.ActiveSeconds,
            PaperCount = day.PaperCount,
            TodoPaperCount = day.TodoPaperCount,
            NotePaperCount = day.NotePaperCount,
            PaperCreated = day.PaperCreated,
            PaperDeleted = day.PaperDeleted,
            TodoCreated = day.TodoCreated,
            TodoCompleted = day.TodoCompleted,
            PillEnabled = day.PillEnabled,
            PillCount = day.PillCount,
            PillExpand = day.PillExpand,
            PillCollapse = day.PillCollapse,
            MarkdownPreview = day.MarkdownPreview,
            ImageInserted = day.ImageInserted,
            HotkeyTriggered = day.HotkeyTriggered,
            CrashCount = day.CrashCount
        };
    }

    private static async Task UploadPendingBatchAsync()
    {
        List<TelemetryReport> batch;
        lock (Gate)
        {
            if (_uploading || !_persisted.Enabled || _persisted.PendingReports.Count == 0)
            {
                return;
            }

            _uploading = true;
            batch = _persisted.PendingReports.ToList();
        }

        try
        {
            var envelope = new TelemetryBatch
            {
                SchemaVersion = SchemaVersion,
                Reports = batch
            };
            var json = JsonSerializer.Serialize(envelope, WireJsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(Endpoint, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var sentIds = batch.Select(report => report.ReportId).ToHashSet(StringComparer.Ordinal);
            lock (Gate)
            {
                if (!_persisted.Enabled)
                {
                    return;
                }

                _persisted.PendingReports.RemoveAll(report => sentIds.Contains(report.ReportId));
                SaveLocked();
            }
        }
        catch
        {
            // Keep the whole batch for the next application launch.
        }
        finally
        {
            lock (Gate)
            {
                _uploading = false;
            }
        }
    }

    private static void MergeCrashMarkersLocked()
    {
        Dictionary<string, int>? counts;
        try
        {
            counts = File.Exists(CrashPath)
                ? JsonSerializer.Deserialize<Dictionary<string, int>>(
                    File.ReadAllText(CrashPath),
                    DiskJsonOptions)
                : null;
        }
        catch
        {
            return;
        }

        if (counts == null || counts.Count == 0)
        {
            TryDeleteCrashMarker();
            return;
        }

        var unmatched = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (date, count) in counts)
        {
            if (count <= 0)
            {
                continue;
            }

            if (_persisted.CurrentDay != null &&
                string.Equals(_persisted.CurrentDay.Date, date, StringComparison.Ordinal))
            {
                _persisted.CurrentDay.CrashCount = AddBounded(
                    _persisted.CurrentDay.CrashCount,
                    count,
                    1000);
                continue;
            }

            var report = _persisted.PendingReports.FirstOrDefault(item =>
                string.Equals(item.Date, date, StringComparison.Ordinal));
            if (report != null)
            {
                report.CrashCount = AddBounded(report.CrashCount, count, 1000);
                continue;
            }

            unmatched[date] = count;
        }

        try
        {
            if (unmatched.Count == 0)
            {
                TryDeleteCrashMarker();
            }
            else
            {
                WriteAtomic(CrashPath, JsonSerializer.Serialize(unmatched, DiskJsonOptions));
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private static void TryDeleteCrashMarker()
    {
        try
        {
            if (File.Exists(CrashPath))
            {
                File.Delete(CrashPath);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private static TelemetryPersistedState LoadPersistedState()
    {
        TelemetryPersistedState state;
        try
        {
            state = File.Exists(StatePath)
                ? JsonSerializer.Deserialize<TelemetryPersistedState>(
                        File.ReadAllText(StatePath),
                        DiskJsonOptions)
                    ?? new TelemetryPersistedState()
                : new TelemetryPersistedState();
        }
        catch
        {
            state = new TelemetryPersistedState();
        }

        NormalizePersistedState(state);
        return state;
    }

    private static void NormalizePersistedState(TelemetryPersistedState state)
    {
        if (string.IsNullOrWhiteSpace(state.InstallId) || state.InstallId.Length is < 16 or > 64)
        {
            state.InstallId = Guid.NewGuid().ToString("N");
        }
        if (string.IsNullOrWhiteSpace(state.FirstSeenDate))
        {
            state.FirstSeenDate = LocalDateString();
        }
        state.PendingReports ??= new List<TelemetryReport>();
        state.PendingReports.RemoveAll(report => report.Kind != "daily_usage");
    }

    private static void TrimPendingReportsLocked()
    {
        if (_persisted.PendingReports.Count <= MaxPendingReports)
        {
            return;
        }

        _persisted.PendingReports.RemoveRange(
            0,
            _persisted.PendingReports.Count - MaxPendingReports);
    }

    private static void SaveLocked()
    {
        try
        {
            NormalizePersistedState(_persisted);
            WriteAtomic(StatePath, JsonSerializer.Serialize(_persisted, DiskJsonOptions));
        }
        catch
        {
            // Local statistics persistence must never surface to the user.
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, path, overwrite: true);
    }

    private static int AddBounded(int current, int delta, int maximum)
    {
        return (int)Math.Clamp((long)current + Math.Max(0, delta), 0, maximum);
    }

    private static string LocalDateString()
    {
        return DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string AppVersion()
    {
        try
        {
            return typeof(App).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion?
                .Split('+', 2)[0] ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static (string Code, string Name) Country()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo");
            var code = key?.GetValue("Name") as string;
            if (!string.IsNullOrWhiteSpace(code))
            {
                var region = new RegionInfo(code);
                return (region.TwoLetterISORegionName, region.EnglishName);
            }
        }
        catch
        {
            // Fall back to the current Windows region below.
        }

        try
        {
            var region = RegionInfo.CurrentRegion;
            return (region.TwoLetterISORegionName, region.EnglishName);
        }
        catch
        {
            return ("", "");
        }
    }

    private sealed class TelemetryPersistedState
    {
        public bool Enabled { get; set; } = true;
        public string InstallId { get; set; } = Guid.NewGuid().ToString("N");
        public string FirstSeenDate { get; set; } = LocalDateString();
        public TelemetryDayState? CurrentDay { get; set; }
        public List<TelemetryReport> PendingReports { get; set; } = new();
    }

    private sealed class TelemetryDayState
    {
        public string Date { get; set; } = "";
        public string AppVersion { get; set; } = "";
        public string Locale { get; set; } = "";
        public string CountryCode { get; set; } = "";
        public string Country { get; set; } = "";
        public int TimezoneOffset { get; set; }
        public int MonitorCount { get; set; } = 1;
        public int LaunchCount { get; set; }
        public int ActiveSeconds { get; set; }
        public int PaperCount { get; set; }
        public int TodoPaperCount { get; set; }
        public int NotePaperCount { get; set; }
        public int PaperCreated { get; set; }
        public int PaperDeleted { get; set; }
        public int TodoCreated { get; set; }
        public int TodoCompleted { get; set; }
        public bool PillEnabled { get; set; }
        public int PillCount { get; set; }
        public int PillExpand { get; set; }
        public int PillCollapse { get; set; }
        public int MarkdownPreview { get; set; }
        public int ImageInserted { get; set; }
        public int HotkeyTriggered { get; set; }
        public int CrashCount { get; set; }

        public bool HasUsage()
        {
            return LaunchCount > 0 || ActiveSeconds > 0 || PaperCreated > 0 || PaperDeleted > 0 ||
                   TodoCreated > 0 || TodoCompleted > 0 || PillExpand > 0 || PillCollapse > 0 ||
                   MarkdownPreview > 0 || ImageInserted > 0 || HotkeyTriggered > 0 || CrashCount > 0;
        }
    }

    private sealed class TelemetryBatch
    {
        public int SchemaVersion { get; set; }
        public List<TelemetryReport> Reports { get; set; } = new();
    }

    private sealed class TelemetryReport
    {
        public string Kind { get; set; } = "daily_usage";
        public int SchemaVersion { get; set; }
        public string ReportId { get; set; } = "";
        public string InstallId { get; set; } = "";
        public string Date { get; set; } = "";
        public string TelemetryFirstSeenDate { get; set; } = "";
        public string AppVersion { get; set; } = "";
        public string Locale { get; set; } = "";
        public string CountryCode { get; set; } = "";
        public string Country { get; set; } = "";
        public int TimezoneOffset { get; set; }
        public int MonitorCount { get; set; }
        public int LaunchCount { get; set; }
        public int ActiveSeconds { get; set; }
        public int PaperCount { get; set; }
        public int TodoPaperCount { get; set; }
        public int NotePaperCount { get; set; }
        public int PaperCreated { get; set; }
        public int PaperDeleted { get; set; }
        public int TodoCreated { get; set; }
        public int TodoCompleted { get; set; }
        public bool PillEnabled { get; set; }
        public int PillCount { get; set; }
        public int PillExpand { get; set; }
        public int PillCollapse { get; set; }
        public int MarkdownPreview { get; set; }
        public int ImageInserted { get; set; }
        public int HotkeyTriggered { get; set; }
        public int CrashCount { get; set; }
    }

    private sealed class UsageSnapshot
    {
        public Dictionary<string, PaperUsageSnapshot> Papers { get; } = new(StringComparer.Ordinal);
        public int PaperCount { get; set; }
        public int TodoPaperCount { get; set; }
        public int NotePaperCount { get; set; }
        public int PillCount { get; set; }
        public bool PillEnabled { get; set; }
    }

    private sealed record PaperUsageSnapshot(
        string Type,
        bool IsCollapsed,
        Dictionary<string, TodoItemSnapshot> TodoItems,
        int ContentIdentity,
        int ContentLength,
        int ImageCount);

    private sealed record TodoItemSnapshot(bool Done, bool HasText);

    private sealed class PreviewFocusState
    {
        public int Generation { get; set; }
        public bool WasEditing { get; set; }
    }
}
