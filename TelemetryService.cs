using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PaperTodo;

internal static class TelemetryService
{
    private const int SchemaVersion = 1;
    private const int WmHotkey = 0x0312;
    private const string Endpoint = "https://1251449999-60pjzyd4uu.ap-beijing.tencentscf.com";
    private const int MaxPendingReports = 31;

    private static readonly TimeSpan TimerInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ActiveInputWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ActiveSnapshotInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleSnapshotInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PersistInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MouseTransitionThrottle = TimeSpan.FromMilliseconds(250);

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

    private static readonly ConditionalWeakTable<TodoTextBox, TodoInputState> TodoInputStates = new();
    private static readonly ConditionalWeakTable<MarkdownTextBox, PreviewFocusState> PreviewFocusStates = new();
    private static readonly HashSet<string> DirectTodoCreatedItemIds = new(StringComparer.Ordinal);
    private static readonly HashSet<string> DirectTodoCompletedItemIds = new(StringComparer.Ordinal);
    private static readonly string StatePath = Path.Combine(AppContext.BaseDirectory, "telemetry.json");
    private static readonly string CrashPath = Path.Combine(AppContext.BaseDirectory, "telemetry-crash.json");

    private static TelemetryPersistedState _persisted = LoadPersistedState();
    private static AppController? _controller;
    private static DispatcherTimer? _timer;
    private static UsageSnapshot? _previousSnapshot;
    private static readonly Dictionary<string, bool> PaperCollapsedStates = new(StringComparer.Ordinal);

    private static DateTimeOffset _lastInputUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset _lastTickUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset _lastSnapshotUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset _lastSaveUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset _lastMouseTransitionQueueUtc = DateTimeOffset.MinValue;

    private static WeakReference<TodoTextBox>? _pendingTodoBox;
    private static WeakReference<CheckBox>? _pendingCheckBox;
    private static bool _pendingCheckWasChecked;
    private static bool _pendingCheckHasText;
    private static string? _pendingCheckItemId;
    private static bool _pendingCheckReleaseObserved;
    private static WeakReference<MarkdownTextBox>? _pendingMarkdownEditor;
    private static bool _captureOnPostProcess;

    private static bool _attached;
    private static bool _runtimeActive;
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

        var shouldStartRuntime = false;
        lock (Gate)
        {
            NormalizePersistedState(_persisted);
            if (!_persisted.Enabled)
            {
                ClearQueuedDataLocked();
            }
            else
            {
                MergeCrashMarkersLocked();
                EnsureCurrentDayLocked(controller.State, countLaunch: true);
                MergeCrashMarkersLocked();
                QueueInitialReportIfNeededLocked();
                shouldStartRuntime = true;
            }
            SaveLocked();
        }

        if (!shouldStartRuntime)
        {
            return;
        }

        StartRuntime(controller);
        _ = UploadPendingBatchAsync();
    }

    public static void Detach()
    {
        if (!_attached)
        {
            return;
        }

        StopRuntime(captureFinalSnapshot: true);

        lock (Gate)
        {
            SaveLocked();
        }

        _attached = false;
        _controller = null;
    }

    public static void SetEnabled(bool enabled)
    {
        AppController? controller;
        lock (Gate)
        {
            if (_persisted.Enabled == enabled)
            {
                return;
            }

            _persisted.Enabled = enabled;
            controller = _controller;

            if (!enabled)
            {
                ClearQueuedDataLocked();
            }
            else if (controller != null)
            {
                EnsureCurrentDayLocked(controller.State, countLaunch: false, resetCurrentDay: true);
                QueueInitialReportIfNeededLocked();
            }

            SaveLocked();
        }

        if (!enabled)
        {
            StopRuntime(captureFinalSnapshot: false);
            return;
        }

        if (controller != null && _attached)
        {
            StartRuntime(controller);
            _ = UploadPendingBatchAsync();
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

    private static void StartRuntime(AppController controller)
    {
        if (_runtimeActive || !Enabled)
        {
            return;
        }

        _runtimeActive = true;
        _previousSnapshot = CaptureSnapshot(controller.State, previous: null);
        ResetPaperTransitionBaseline(controller.State);

        var now = DateTimeOffset.UtcNow;
        _lastInputUtc = DateTimeOffset.MinValue;
        _lastTickUtc = now;
        _lastSnapshotUtc = now;
        _lastSaveUtc = now;
        _lastMouseTransitionQueueUtc = DateTimeOffset.MinValue;

        InputManager.Current.PreProcessInput += OnPreProcessInput;
        InputManager.Current.PostProcessInput += OnPostProcessInput;
        ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimerInterval
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    private static void StopRuntime(bool captureFinalSnapshot)
    {
        if (!_runtimeActive)
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
        InputManager.Current.PostProcessInput -= OnPostProcessInput;
        ComponentDispatcher.ThreadPreprocessMessage -= OnThreadPreprocessMessage;

        if (captureFinalSnapshot && Enabled)
        {
            try
            {
                CaptureLightweightPaperTransitions();
                CaptureUsageTransitionsAndSnapshot();
            }
            catch
            {
                // Anonymous statistics must never affect shutdown.
            }
        }

        _runtimeActive = false;
        _previousSnapshot = null;
        PaperCollapsedStates.Clear();
        DirectTodoCreatedItemIds.Clear();
        DirectTodoCompletedItemIds.Clear();
        _lastInputUtc = DateTimeOffset.MinValue;
        _lastTickUtc = DateTimeOffset.MinValue;
        _lastSnapshotUtc = DateTimeOffset.MinValue;
        _lastSaveUtc = DateTimeOffset.MinValue;
        _lastMouseTransitionQueueUtc = DateTimeOffset.MinValue;
        _pendingTodoBox = null;
        _pendingCheckBox = null;
        _pendingCheckWasChecked = false;
        _pendingCheckHasText = false;
        _pendingCheckItemId = null;
        _pendingCheckReleaseObserved = false;
        _pendingMarkdownEditor = null;
        _captureOnPostProcess = false;
    }

    private static void ClearQueuedDataLocked()
    {
        _persisted.PendingReports.Clear();
        _persisted.CurrentDay = null;
        TryDeleteCrashMarker();
    }

    private static void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (!_runtimeActive)
        {
            return;
        }

        var input = e.StagingItem.Input;
        if (input is not (MouseEventArgs or KeyboardEventArgs))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _lastInputUtc = now;

        var forcePostCapture = input is KeyboardEventArgs or MouseButtonEventArgs;
        if (forcePostCapture)
        {
            if (Keyboard.FocusedElement is TodoTextBox todoBox)
            {
                var todoState = TodoInputStates.GetValue(todoBox, static _ => new TodoInputState());
                if (!todoState.Initialized)
                {
                    todoState.Initialized = true;
                    todoState.HasText = !string.IsNullOrWhiteSpace(todoBox.Text);
                }
                _pendingTodoBox = new WeakReference<TodoTextBox>(todoBox);
            }

            var markdownBox = FindAncestor<MarkdownTextBox>(Keyboard.FocusedElement as DependencyObject);
            if (markdownBox != null)
            {
                var previewState = PreviewFocusStates.GetValue(markdownBox, static _ => new PreviewFocusState());
                previewState.WasEditing = true;
                _pendingMarkdownEditor = new WeakReference<MarkdownTextBox>(markdownBox);
            }

            if (input is MouseButtonEventArgs mouseButton &&
                mouseButton.ChangedButton == MouseButton.Left)
            {
                var inputSource =
                    mouseButton.OriginalSource as DependencyObject ??
                    Mouse.DirectlyOver as DependencyObject;
                var check = FindAncestor<CheckBox>(inputSource);
                if (mouseButton.ButtonState == MouseButtonState.Pressed &&
                    check != null &&
                    check.IsLoaded &&
                    Window.GetWindow(check) is PaperWindow)
                {
                    var editor = TodoEditorForCheckBox(check);
                    _pendingCheckBox = new WeakReference<CheckBox>(check);
                    _pendingCheckWasChecked = check.IsChecked == true;
                    _pendingCheckHasText = editor != null && !string.IsNullOrWhiteSpace(editor.Text);
                    _pendingCheckItemId = TodoItemIdForElement(check);
                    _pendingCheckReleaseObserved = false;
                }
                else if (mouseButton.ButtonState == MouseButtonState.Released &&
                    _pendingCheckBox != null)
                {
                    _pendingCheckReleaseObserved = true;
                }
            }
        }

        if (forcePostCapture || now - _lastMouseTransitionQueueUtc >= MouseTransitionThrottle)
        {
            _lastMouseTransitionQueueUtc = now;
            _captureOnPostProcess = true;
        }
    }

    private static void OnPostProcessInput(object sender, ProcessInputEventArgs e)
    {
        if (!_captureOnPostProcess)
        {
            return;
        }

        _captureOnPostProcess = false;
        if (!_runtimeActive || _controller == null)
        {
            return;
        }

        CapturePendingTodoTextTransition();
        CapturePendingTodoCompletion();
        CapturePendingMarkdownPreview();
        CaptureLightweightPaperTransitions();
    }

    private static void CapturePendingTodoTextTransition()
    {
        var pending = _pendingTodoBox;
        _pendingTodoBox = null;
        if (pending == null || !pending.TryGetTarget(out var todoBox))
        {
            return;
        }

        var state = TodoInputStates.GetValue(todoBox, static _ => new TodoInputState());
        var hasText = !string.IsNullOrWhiteSpace(todoBox.Text);
        if (!state.Initialized)
        {
            state.Initialized = true;
            state.HasText = hasText;
            return;
        }

        if (!state.HasText && hasText)
        {
            RecordCounter(day => day.TodoCreated = AddBounded(day.TodoCreated, 1, 100000));
            var itemId = TodoItemIdForElement(todoBox);
            if (!string.IsNullOrWhiteSpace(itemId))
            {
                DirectTodoCreatedItemIds.Add(itemId);
            }
        }
        state.HasText = hasText;
    }

    private static void CapturePendingTodoCompletion()
    {
        var pending = _pendingCheckBox;
        if (pending == null || !pending.TryGetTarget(out var check))
        {
            ClearPendingTodoCompletion();
            return;
        }

        var changedToChecked =
            !_pendingCheckWasChecked &&
            check.IsChecked == true;

        if (changedToChecked && _pendingCheckHasText)
        {
            RecordCounter(day => day.TodoCompleted = AddBounded(day.TodoCompleted, 1, 100000));
            if (!string.IsNullOrWhiteSpace(_pendingCheckItemId))
            {
                DirectTodoCompletedItemIds.Add(_pendingCheckItemId);
            }
        }

        if (changedToChecked || _pendingCheckReleaseObserved)
        {
            ClearPendingTodoCompletion();
        }
    }

    private static void ClearPendingTodoCompletion()
    {
        _pendingCheckBox = null;
        _pendingCheckWasChecked = false;
        _pendingCheckHasText = false;
        _pendingCheckItemId = null;
        _pendingCheckReleaseObserved = false;
    }

    private static void CapturePendingMarkdownPreview()
    {
        var pending = _pendingMarkdownEditor;
        _pendingMarkdownEditor = null;
        if (pending == null || !pending.TryGetTarget(out var box))
        {
            return;
        }

        var state = PreviewFocusStates.GetValue(box, static _ => new PreviewFocusState());
        if (state.WasEditing && !box.IsKeyboardFocusWithin && box.IsPreviewMode)
        {
            state.WasEditing = false;
            RecordCounter(day => day.MarkdownPreview = AddBounded(day.MarkdownPreview, 1, 100000));
        }
    }

    private static string? TodoItemIdForElement(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current != null)
        {
            if (current is FrameworkElement { Tag: string itemId } &&
                !string.IsNullOrWhiteSpace(itemId))
            {
                return itemId;
            }

            try
            {
                current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
            }
            catch
            {
                current = LogicalTreeHelper.GetParent(current);
            }
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        var current = source;
        while (current != null)
        {
            if (current is T match)
            {
                return match;
            }

            try
            {
                current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
            }
            catch
            {
                current = LogicalTreeHelper.GetParent(current);
            }
        }

        return null;
    }

    private static TodoTextBox? TodoEditorForCheckBox(CheckBox check)
    {
        DependencyObject? parent;
        try
        {
            parent = VisualTreeHelper.GetParent(check);
        }
        catch
        {
            parent = null;
        }

        if (parent is not Grid grid)
        {
            return null;
        }

        foreach (UIElement child in grid.Children)
        {
            if (child is TodoTextBox editor)
            {
                return editor;
            }
        }

        return null;
    }

    private static void OnThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        if (!_runtimeActive || msg.message != WmHotkey)
        {
            return;
        }

        _lastInputUtc = DateTimeOffset.UtcNow;
        RecordCounter(day => day.HotkeyTriggered = AddBounded(day.HotkeyTriggered, 1, 100000));
    }

    private static void RecordCounter(Action<TelemetryDayState> update)
    {
        var controller = _controller;
        if (controller == null || !_runtimeActive)
        {
            return;
        }

        var rolledOver = false;
        lock (Gate)
        {
            rolledOver = EnsureCurrentDayLocked(controller.State, countLaunch: false);
            if (_persisted.CurrentDay != null)
            {
                update(_persisted.CurrentDay);
            }
            if (rolledOver)
            {
                SaveLocked();
            }
        }

        if (rolledOver)
        {
            _previousSnapshot = CaptureSnapshot(controller.State, previous: null);
            ResetPaperTransitionBaseline(controller.State);
            _lastSnapshotUtc = DateTimeOffset.UtcNow;
            _ = UploadPendingBatchAsync();
        }
    }

    private static void OnTimerTick(object? sender, EventArgs e)
    {
        var controller = _controller;
        if (controller == null || !_runtimeActive)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var elapsedSeconds = _lastTickUtc == DateTimeOffset.MinValue
                ? (int)TimerInterval.TotalSeconds
                : (int)Math.Clamp(
                    Math.Round((now - _lastTickUtc).TotalSeconds),
                    1,
                    30);
            _lastTickUtc = now;

            var recentlyActive = now - _lastInputUtc <= ActiveInputWindow;
            var rolledOver = false;
            lock (Gate)
            {
                rolledOver = EnsureCurrentDayLocked(controller.State, countLaunch: false);
                if (_persisted.CurrentDay != null && recentlyActive)
                {
                    _persisted.CurrentDay.ActiveSeconds = AddBounded(
                        _persisted.CurrentDay.ActiveSeconds,
                        elapsedSeconds,
                        86400);
                }
            }

            if (rolledOver)
            {
                _previousSnapshot = CaptureSnapshot(controller.State, previous: null);
                ResetPaperTransitionBaseline(controller.State);
                _lastSnapshotUtc = now;
            }
            else
            {
                rolledOver = CaptureLightweightPaperTransitions();
            }

            var snapshotInterval = recentlyActive
                ? ActiveSnapshotInterval
                : IdleSnapshotInterval;
            if (now - _lastSnapshotUtc >= snapshotInterval)
            {
                rolledOver |= CaptureUsageTransitionsAndSnapshot();
                _lastSnapshotUtc = now;
            }

            if (rolledOver || now - _lastSaveUtc >= PersistInterval)
            {
                lock (Gate)
                {
                    SaveLocked();
                }
                _lastSaveUtc = now;
            }

            if (rolledOver)
            {
                _ = UploadPendingBatchAsync();
            }
        }
        catch
        {
            // Product behavior always wins over anonymous statistics.
        }
    }

    private static void ResetPaperTransitionBaseline(AppState state)
    {
        PaperCollapsedStates.Clear();
        foreach (var paper in state.Papers)
        {
            PaperCollapsedStates[paper.Id] = paper.IsCollapsed;
        }
    }

    private static bool CaptureLightweightPaperTransitions()
    {
        var controller = _controller;
        if (controller == null || !_runtimeActive)
        {
            return false;
        }

        var currentIds = new HashSet<string>(StringComparer.Ordinal);
        var rolledOver = false;

        lock (Gate)
        {
            rolledOver = EnsureCurrentDayLocked(controller.State, countLaunch: false);
            var day = _persisted.CurrentDay;
            if (day == null)
            {
                return rolledOver;
            }

            foreach (var paper in controller.State.Papers)
            {
                currentIds.Add(paper.Id);
                if (!PaperCollapsedStates.TryGetValue(paper.Id, out var wasCollapsed))
                {
                    PaperCollapsedStates[paper.Id] = paper.IsCollapsed;
                    day.PaperCreated = AddBounded(day.PaperCreated, 1, 10000);
                    continue;
                }

                if (wasCollapsed == paper.IsCollapsed)
                {
                    continue;
                }

                PaperCollapsedStates[paper.Id] = paper.IsCollapsed;
                if (paper.IsCollapsed)
                {
                    day.PillCollapse = AddBounded(day.PillCollapse, 1, 100000);
                }
                else
                {
                    day.PillExpand = AddBounded(day.PillExpand, 1, 100000);
                }
            }

            var removedIds = PaperCollapsedStates.Keys
                .Where(id => !currentIds.Contains(id))
                .ToList();
            if (removedIds.Count > 0)
            {
                day.PaperDeleted = AddBounded(day.PaperDeleted, removedIds.Count, 10000);
                foreach (var id in removedIds)
                {
                    PaperCollapsedStates.Remove(id);
                }
            }
        }

        return rolledOver;
    }

    private static bool CaptureUsageTransitionsAndSnapshot()
    {
        var controller = _controller;
        if (controller == null || !_runtimeActive)
        {
            return false;
        }

        var previous = _previousSnapshot;
        var current = CaptureSnapshot(controller.State, previous);
        _previousSnapshot = current;

        var rolledOver = false;
        lock (Gate)
        {
            rolledOver = EnsureCurrentDayLocked(controller.State, countLaunch: false);
            var day = _persisted.CurrentDay;
            if (day == null)
            {
                DirectTodoCreatedItemIds.Clear();
                DirectTodoCompletedItemIds.Clear();
                return rolledOver;
            }

            ApplySnapshotMetrics(day, current);
            if (rolledOver || previous == null)
            {
                DirectTodoCreatedItemIds.Clear();
                DirectTodoCompletedItemIds.Clear();
                return rolledOver;
            }

            foreach (var (paperId, currentPaper) in current.Papers)
            {
                if (!previous.Papers.TryGetValue(paperId, out var previousPaper))
                {
                    CountInitialPaperContent(day, currentPaper);
                    continue;
                }

                if (currentPaper.Type == PaperTypes.Todo && previousPaper.Type == PaperTypes.Todo)
                {
                    foreach (var (itemId, currentItem) in currentPaper.TodoItems)
                    {
                        if (!previousPaper.TodoItems.TryGetValue(itemId, out var previousItem))
                        {
                            if (currentItem.HasText &&
                                !DirectTodoCreatedItemIds.Remove(itemId))
                            {
                                day.TodoCreated = AddBounded(day.TodoCreated, 1, 100000);
                            }
                            if (currentItem.Done &&
                                currentItem.HasText &&
                                !DirectTodoCompletedItemIds.Remove(itemId))
                            {
                                day.TodoCompleted = AddBounded(day.TodoCompleted, 1, 100000);
                            }
                            continue;
                        }

                        // The focused empty -> non-empty transition is counted directly from input.
                        // Snapshot fallback catches paste/import/programmatic changes that bypass it.
                        if (!previousItem.HasText &&
                            currentItem.HasText &&
                            !DirectTodoCreatedItemIds.Remove(itemId))
                        {
                            day.TodoCreated = AddBounded(day.TodoCreated, 1, 100000);
                        }
                        if (!previousItem.Done &&
                            currentItem.Done &&
                            currentItem.HasText &&
                            !DirectTodoCompletedItemIds.Remove(itemId))
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
        }

        DirectTodoCreatedItemIds.Clear();
        DirectTodoCompletedItemIds.Clear();
        return rolledOver;
    }

    private static void CountInitialPaperContent(TelemetryDayState day, PaperUsageSnapshot paper)
    {
        if (paper.Type == PaperTypes.Todo)
        {
            foreach (var (itemId, item) in paper.TodoItems)
            {
                if (item.HasText && !DirectTodoCreatedItemIds.Remove(itemId))
                {
                    day.TodoCreated = AddBounded(day.TodoCreated, 1, 100000);
                }
                if (item.Done &&
                    item.HasText &&
                    !DirectTodoCompletedItemIds.Remove(itemId))
                {
                    day.TodoCompleted = AddBounded(day.TodoCompleted, 1, 100000);
                }
            }
        }
        else if (paper.Type == PaperTypes.Note && paper.ImageCount > 0)
        {
            day.ImageInserted = AddBounded(day.ImageInserted, paper.ImageCount, 10000);
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

    private static bool EnsureCurrentDayLocked(
        AppState state,
        bool countLaunch,
        bool resetCurrentDay = false)
    {
        var today = LocalDateString();
        var rolledOver = false;

        if (resetCurrentDay || _persisted.CurrentDay == null)
        {
            _persisted.CurrentDay = CreateDay(today, state);
        }
        else if (!string.Equals(_persisted.CurrentDay.Date, today, StringComparison.Ordinal))
        {
            FinalizeCurrentDayLocked();
            _persisted.CurrentDay = CreateDay(today, state);
            rolledOver = true;
        }

        if (countLaunch && _persisted.CurrentDay != null)
        {
            _persisted.CurrentDay.LaunchCount = AddBounded(
                _persisted.CurrentDay.LaunchCount,
                1,
                1000);
        }

        return rolledOver;
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

    private static void QueueInitialReportIfNeededLocked()
    {
        if (_persisted.FirstReportCreated ||
            _persisted.CurrentDay == null ||
            _persisted.PendingReports.Count > 0 ||
            !string.Equals(_persisted.FirstSeenDate, _persisted.CurrentDay.Date, StringComparison.Ordinal))
        {
            return;
        }

        // A brand-new install has no yesterday report yet. Send one provisional current-day row
        // immediately using the same report_id as the eventual completed-day row. If the user
        // never returns, the install still exists in new-user/DAU views. If they do return, the
        // completed row naturally wins when analytics deduplicates by report_id + received_at_ms.
        _persisted.PendingReports.Add(CreateReport(_persisted.CurrentDay));
        _persisted.FirstReportCreated = true;
        TrimPendingReportsLocked();
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
            // Keep the whole batch for a later launch or the next day rollover.
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
        state.PendingReports.RemoveAll(report =>
            report.Kind != "daily_usage" ||
            string.IsNullOrWhiteSpace(report.Date) ||
            !string.Equals(
                report.ReportId,
                $"{state.InstallId}_{report.Date}_v{SchemaVersion}",
                StringComparison.Ordinal));

        if (!state.FirstReportCreated &&
            (!string.Equals(state.FirstSeenDate, LocalDateString(), StringComparison.Ordinal) ||
             state.PendingReports.Count > 0))
        {
            state.FirstReportCreated = true;
        }
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
        public bool FirstReportCreated { get; set; }
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
        Dictionary<string, TodoItemSnapshot> TodoItems,
        int ContentIdentity,
        int ContentLength,
        int ImageCount);

    private sealed record TodoItemSnapshot(bool Done, bool HasText);

    private sealed class TodoInputState
    {
        public bool Initialized { get; set; }
        public bool HasText { get; set; }
    }

    private sealed class PreviewFocusState
    {
        public bool WasEditing { get; set; }
    }
}
