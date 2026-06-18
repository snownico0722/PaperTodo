using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace PaperTodo;

public sealed partial class AppController : IDisposable
{
    public static AppController Current { get; private set; } = null!;

    private readonly StateStore _store = new();
    private readonly Dictionary<string, PaperWindow> _windows = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _topmostRefreshTimer;

    private TaskbarIcon? _trayIcon;
    private ContextMenu? _trayMenu;
    private Window? _settingsWindow;
    private TextBox? _settingsExternalMarkdownTextBox;
    private CheckBox? _settingsCapsuleModeCheckBox;
    private CheckBox? _settingsDeepCapsuleModeCheckBox;
    private CheckBox? _settingsDeepCapsuleExpandedSlotCheckBox;
    private CheckBox? _settingsCapsuleCollapseAllCheckBox;
    private bool _isExiting;
    private bool _suppressDirty;
    private bool _hasShownSaveFailure;
    private bool _ignoreSaveFailures;
    private int _trayRefreshSuppressionDepth;
    private long _saveVersion;
    private readonly Dictionary<string, int> _visibilityAnimationVersions = new();
    private bool _suppressTopmostForFullscreenForeground;
    private IntPtr _fullscreenAvoidanceWindow;
    private DateTimeOffset _lastFullscreenGlobalScanAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFullscreenDebugLogAt = DateTimeOffset.MinValue;
    private bool? _lastFullscreenDebugSuppressState;
    private PaperWindow? _noteLinkTargetWindow;
    private string? _noteLinkTargetItemId;
    // One master pill per docked-capsule queue, keyed by QueueKey(monitorDevice, edge).
    private readonly Dictionary<string, MasterCapsuleWindow> _masterCapsules = new();

    private static Brush TrayPaperBrush => Theme.PaperBrush;
    private static Brush TrayBorderBrush => Theme.PaperBorderBrush;
    private static Brush TrayTextBrush => Theme.TextBrush;
    private static Brush TrayWeakTextBrush => Theme.WeakTextBrush;
    private static Brush TrayHoverBrush => Theme.HoverBrush;
    private static readonly bool EnableFullscreenDebugLog = false;
    private static string FullscreenDebugLogPath => Path.Combine(AppContext.BaseDirectory, "fullscreen-debug.log");

    public AppState State { get; private set; }
    public bool SuppressTopmostForFullscreenForeground => _suppressTopmostForFullscreenForeground;
    public IntPtr FullscreenAvoidanceWindow => _fullscreenAvoidanceWindow;
    private bool ShouldAvoidFullscreenTopmost => FullscreenTopmostModes.Normalize(State.FullscreenTopmostMode) == FullscreenTopmostModes.Avoid;

    public AppController()
    {
        Current = this;
        State = _store.Load();
        ToolTipPreferences.Register(() => State.EnableToolTips);

        _saveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };

        _topmostRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _topmostRefreshTimer.Tick += (_, _) => RefreshTopmostForForegroundWindow();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void Start(bool createDefaultPaper = true)
    {
        CreateTrayIcon();
        RefreshTopmostForForegroundWindow();
        _topmostRefreshTimer.Start();

        if (State.Papers.Count == 0)
        {
            if (createDefaultPaper)
            {
                CreatePaper(PaperTypes.Todo, show: true);
                SaveNow();
            }
            return;
        }

        var rescuedPapers = EnsurePapersOnScreen();

        // Closing a paper only hides it for the current session; a fresh app start
        // restores every non-deleted paper so a paper never feels "lost".
        _suppressDirty = true;
        try
        {
            foreach (var paper in State.Papers.ToList())
            {
                ShowPaper(paper);
            }
        }
        finally
        {
            _suppressDirty = false;
        }

        if (rescuedPapers)
        {
            SaveNow();
        }
    }

    public PaperData? CreatePaper(string type, bool show = true, PaperData? sourcePaper = null)
    {
        if (State.Papers.Count >= 100)
        {
            ShowPaperLimitDialog();
            return null;
        }

        var offset = State.Papers.Count * 24;

        double newX = 140 + offset;
        double newY = 140 + offset;

        if (sourcePaper != null)
        {
            newX = sourcePaper.X + 30;
            newY = sourcePaper.Y + 30;
        }

        while (State.Papers.Any(p => Math.Abs(p.X - newX) < 5 && Math.Abs(p.Y - newY) < 5))
        {
            newX += 30;
            newY += 30;
        }

        var paperType = type == PaperTypes.Note ? PaperTypes.Note : PaperTypes.Todo;
        var paper = new PaperData
        {
            Type = paperType,
            Title = PaperTitles.DefaultTitle(paperType, NextTitleNumber(paperType)),
            X = newX,
            Y = newY,
            Width = type == PaperTypes.Note ? PaperLayoutDefaults.NoteDefaultWidth : PaperLayoutDefaults.TodoDefaultWidth,
            Height = type == PaperTypes.Note ? PaperLayoutDefaults.NoteDefaultHeight : PaperLayoutDefaults.TodoDefaultHeight,
            IsVisible = show,
            AlwaysOnTop = sourcePaper?.AlwaysOnTop ?? false
        };

        RescuePaperIfOffScreen(paper, State.Papers.Count);

        if (paper.Type == PaperTypes.Todo)
        {
            paper.Items.Add(new PaperItem
            {
                Text = "",
                Done = false,
                Order = 0
            });
        }

        State.Papers.Add(paper);

        if (show)
        {
            _trayRefreshSuppressionDepth++;
            try
            {
                ShowPaper(paper);
                if (sourcePaper != null && _windows.TryGetValue(paper.Id, out var window))
                {
                    ForceWindowToFront(window);
                }
            }
            finally
            {
                _trayRefreshSuppressionDepth--;
            }
        }

        RefreshTrayMenu();
        MarkDirty();
        return paper;
    }

    private int NextTitleNumber(string paperType)
    {
        var normalizedType = paperType == PaperTypes.Note ? PaperTypes.Note : PaperTypes.Todo;
        var prefix = normalizedType == PaperTypes.Note ? "笔记" : "待办";
        var usedNumbers = new HashSet<int>();

        foreach (var paper in State.Papers.Where(p => p.Type == normalizedType))
        {
            var title = PaperTitles.CleanCustomTitle(paper.Title);
            if (title.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(title[prefix.Length..], out var number) &&
                number > 0)
            {
                usedNumbers.Add(number);
            }
        }

        var next = 1;
        while (usedNumbers.Contains(next))
        {
            next++;
        }

        return next;
    }

    public int TitleNumberFor(PaperData paper)
    {
        var normalizedType = paper.Type == PaperTypes.Note ? PaperTypes.Note : PaperTypes.Todo;
        var number = 1;
        foreach (var existing in State.Papers)
        {
            if (existing.Type != normalizedType)
            {
                continue;
            }

            if (existing.Id == paper.Id)
            {
                return number;
            }

            number++;
        }

        return Math.Max(1, number);
    }

    public string PaperTitleText(PaperData paper)
    {
        return PaperTitles.EffectiveTitle(paper, TitleNumberFor(paper));
    }

    public string PaperCapsuleTitle(PaperData paper)
    {
        return PaperTitles.CapsuleText(paper, TitleNumberFor(paper));
    }

    public void UpdatePaperTitle(PaperData paper, string title)
    {
        var cleaned = PaperTitles.CleanCustomTitle(title, State.MaxTitleLength);
        if (paper.Title == cleaned)
        {
            return;
        }

        paper.Title = cleaned;
        if (_windows.TryGetValue(paper.Id, out var window))
        {
            window.RefreshPaperTitle();
        }
        RefreshTrayMenu();
        MarkDirty();
    }

    public void SetPaperTextZoom(PaperData paper, double zoom)
    {
        if (paper.Type != PaperTypes.Note)
        {
            return;
        }

        var normalized = Math.Round(Math.Clamp(zoom, 0.5, 1.5), 1);
        if (Math.Abs(paper.TextZoom - normalized) < 0.001)
        {
            return;
        }

        paper.TextZoom = normalized;
        if (_windows.TryGetValue(paper.Id, out var window))
        {
            window.UpdateTextZoom();
        }
        MarkDirty();
    }

    public void TogglePaperVisibility(PaperData paper)
    {
        if (IsPaperShown(paper))
        {
            HidePaper(paper);
        }
        else
        {
            ShowPaper(paper);
        }
    }

    public bool IsExistingNote(string? noteId)
    {
        return FindNote(noteId) != null;
    }

    public bool TryGetLinkedNoteTitle(string? noteId, out string title)
    {
        var note = FindNote(noteId);
        if (note == null)
        {
            title = "";
            return false;
        }

        title = PaperTitleText(note);
        return true;
    }

    public bool IsNoteLinkedToAnyTodo(PaperData paper)
    {
        if (paper.Type != PaperTypes.Note)
        {
            return false;
        }

        var noteId = paper.Id;
        foreach (var sourcePaper in State.Papers)
        {
            if (sourcePaper.Type != PaperTypes.Todo)
            {
                continue;
            }

            foreach (var item in sourcePaper.Items)
            {
                if (string.Equals(item.LinkedNoteId, noteId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool CanPaperDisplayAsCapsule(PaperData paper)
    {
        if (!State.UseCapsuleMode)
        {
            return false;
        }

        return !(State.EnableTodoNoteLinks &&
            State.HideLinkedNotesFromCapsules &&
            IsNoteLinkedToAnyTodo(paper));
    }

    public void OpenLinkedNote(string? noteId, Window? anchorWindow = null)
    {
        var note = FindNote(noteId);
        if (note == null)
        {
            return;
        }

        if (_windows.TryGetValue(note.Id, out var window))
        {
            note.IsVisible = true;
            RescuePaperIfOffScreen(note, State.Papers.IndexOf(note));
            window.CancelPendingVisibilityTransitions();

            if (note.IsCollapsed)
            {
                window.ExpandForProgrammaticOpen();
            }
            else if (!window.HasVisibleSurface)
            {
                RestoreExistingPaperWindowSurface(note, window);
            }

            PlaceLinkedNoteBesideAnchor(note, window, anchorWindow);
            ForceWindowToFront(window);
            RefreshTrayMenu();
            MarkDirty();
            return;
        }

        note.IsCollapsed = false;
        PlaceLinkedNoteBesideAnchor(note, null, anchorWindow);
        ShowPaper(note);
        if (_windows.TryGetValue(note.Id, out window))
        {
            ForceWindowToFront(window);
        }
    }

    public void BeginNoteLinkDrag(PaperData sourceNote)
    {
        if (!State.EnableTodoNoteLinks || sourceNote.Type != PaperTypes.Note)
        {
            return;
        }

        ClearNoteLinkDropTarget();
    }

    public void UpdateNoteLinkDrag(PaperData sourceNote, Point screenPoint)
    {
        if (!State.EnableTodoNoteLinks || sourceNote.Type != PaperTypes.Note)
        {
            ClearNoteLinkDropTarget();
            return;
        }

        PaperWindow? targetWindow = null;
        string? targetItemId = null;

        foreach (var window in _windows.Values)
        {
            if (window.TryHitTodoRow(screenPoint, out var itemId))
            {
                targetWindow = window;
                targetItemId = itemId;
            }
        }

        if (ReferenceEquals(_noteLinkTargetWindow, targetWindow) &&
            string.Equals(_noteLinkTargetItemId, targetItemId, StringComparison.Ordinal))
        {
            return;
        }

        ClearNoteLinkDropTarget();

        if (targetWindow != null && !string.IsNullOrWhiteSpace(targetItemId))
        {
            _noteLinkTargetWindow = targetWindow;
            _noteLinkTargetItemId = targetItemId;
            targetWindow.SetNoteLinkDropTarget(targetItemId);
        }
    }

    public void EndNoteLinkDrag(PaperData sourceNote, bool commit)
    {
        if (State.EnableTodoNoteLinks &&
            sourceNote.Type == PaperTypes.Note &&
            commit &&
            _noteLinkTargetWindow != null &&
            !string.IsNullOrWhiteSpace(_noteLinkTargetItemId))
        {
            _noteLinkTargetWindow.LinkNoteToTodo(_noteLinkTargetItemId, sourceNote.Id);
        }

        ClearNoteLinkDropTarget();
    }

    private static void PlaceLinkedNoteBesideAnchor(PaperData note, Window? noteWindow, Window? anchorWindow)
    {
        if (anchorWindow == null || double.IsNaN(anchorWindow.Left) || double.IsNaN(anchorWindow.Top))
        {
            return;
        }

        const double gap = 10;
        const double margin = 8;
        var area = WindowWorkAreaHelper.WorkAreaFor(anchorWindow);

        var noteWidth = Math.Max(
            Math.Max(noteWindow is { ActualWidth: > 1 } ? noteWindow.ActualWidth : 0, note.Width),
            PaperLayoutDefaults.MinWidth);
        var noteHeight = Math.Max(
            Math.Max(noteWindow is { ActualHeight: > 1 } ? noteWindow.ActualHeight : 0, note.Height),
            PaperLayoutDefaults.MinHeight);

        noteWidth = Math.Min(noteWidth, Math.Max(PaperLayoutDefaults.MinWidth, area.Width - (margin * 2)));
        noteHeight = Math.Min(noteHeight, Math.Max(PaperLayoutDefaults.MinHeight, area.Height - (margin * 2)));

        var anchorWidth = anchorWindow.ActualWidth > 1 ? anchorWindow.ActualWidth : anchorWindow.Width;
        if (double.IsNaN(anchorWidth) || double.IsInfinity(anchorWidth) || anchorWidth <= 1)
        {
            anchorWidth = PaperLayoutDefaults.TodoDefaultWidth;
        }

        var rightX = anchorWindow.Left + anchorWidth + gap;
        var leftX = anchorWindow.Left - noteWidth - gap;
        var minX = area.Left + margin;
        var maxX = Math.Max(minX, area.Right - noteWidth - margin);

        var targetX = rightX <= maxX
            ? rightX
            : leftX >= minX
                ? leftX
                : Math.Clamp(rightX, minX, maxX);

        var minY = area.Top + margin;
        var maxY = Math.Max(minY, area.Bottom - noteHeight - margin);
        var targetY = Math.Clamp(anchorWindow.Top, minY, maxY);

        note.X = Math.Round(targetX);
        note.Y = Math.Round(targetY);

        if (noteWindow != null)
        {
            noteWindow.Left = note.X;
            noteWindow.Top = note.Y;
        }
    }

    private void ClearNoteLinkDropTarget()
    {
        _noteLinkTargetWindow?.SetNoteLinkDropTarget(null);
        _noteLinkTargetWindow = null;
        _noteLinkTargetItemId = null;
    }

    private PaperData? FindNote(string? noteId)
    {
        if (string.IsNullOrWhiteSpace(noteId))
        {
            return null;
        }

        return State.Papers.FirstOrDefault(p => p.Id == noteId && p.Type == PaperTypes.Note);
    }

    public void ExecuteStartupCommand(StartupCommand command)
    {
        switch (command.Kind)
        {
            case StartupCommandKind.Show:
                ShowAllPapers();
                break;
            case StartupCommandKind.Hide:
                HideAllPapers();
                break;
            case StartupCommandKind.Toggle:
                if (State.Papers.Any(IsPaperShown))
                {
                    HideAllPapers();
                }
                else
                {
                    ShowAllPapers();
                }
                break;
            case StartupCommandKind.NewTodo:
                CreatePaper(PaperTypes.Todo, show: true);
                break;
            case StartupCommandKind.NewNote:
                CreatePaper(PaperTypes.Note, show: true);
                break;
            case StartupCommandKind.Exit:
                Exit();
                break;
        }
    }

    private int NextVisibilityAnimationVersion(string paperId)
    {
        _visibilityAnimationVersions.TryGetValue(paperId, out var current);
        var next = current + 1;
        _visibilityAnimationVersions[paperId] = next;
        return next;
    }

    private bool IsVisibilityAnimationCurrent(string paperId, int version)
    {
        return _visibilityAnimationVersions.TryGetValue(paperId, out var current) && current == version;
    }

    public void ShowPaper(PaperData paper)
    {
        RefreshTopmostForForegroundWindow();
        if (paper.IsCollapsed && !CanPaperDisplayAsCapsule(paper))
        {
            paper.IsCollapsed = false;
        }
        paper.IsVisible = true;
        var visibilityVersion = NextVisibilityAnimationVersion(paper.Id);
        RescuePaperIfOffScreen(paper, State.Papers.IndexOf(paper));

        if (!_windows.TryGetValue(paper.Id, out var window))
        {
            window = new PaperWindow(paper, this);
            _windows[paper.Id] = window;
        }
        window.CancelPendingVisibilityTransitions();

        var showAsDeepCapsuleOnly = State.UseCapsuleMode && State.UseDeepCapsuleMode && paper.IsCollapsed;

        if (!showAsDeepCapsuleOnly && !window.IsVisible)
        {
            window.Left = paper.X;
            window.Top = paper.Y;
            if (paper.IsCollapsed && State.UseCapsuleMode)
            {
                window.Width = window.DesiredCapsuleWindowWidth;
                window.Height = PaperLayoutDefaults.CapsuleHeight;
            }
            else
            {
                window.Width = paper.Width;
                window.Height = paper.Height;
            }
            // To prevent a 1-frame DWM cache flash when a window's size changes while hidden,
            // we show it fully transparent first, then restore opacity after layout is complete.
            double originalOpacity = window.Opacity;
            window.Opacity = 0;
            window.Show();

            window.Dispatcher.InvokeAsync(() =>
            {
                if (!paper.IsVisible || !IsVisibilityAnimationCurrent(paper.Id, visibilityVersion))
                {
                    return;
                }

                // A retracted collapse-all capsule must stay at Opacity 0; restoring it here
                // would un-hide it behind the master pill on restart.
                if (window.IsCollapseAllRetracted)
                {
                    return;
                }

                // 显示动画：淡入
                if (State.EnableAnimations && originalOpacity > 0)
                {
                    var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, originalOpacity, TimeSpan.FromMilliseconds(200))
                    {
                        EasingFunction = AnimationHelper.QuickEase
                    };
                    window.BeginAnimation(Window.OpacityProperty, fadeIn);
                }
                else
                {
                    window.Opacity = originalOpacity;
                }
            }, System.Windows.Threading.DispatcherPriority.Render);
        }
        else if (showAsDeepCapsuleOnly && window.IsVisible)
        {
            window.HideMainWindowForDeepCapsuleMode();
        }

        if (!_suppressTopmostForFullscreenForeground && window.IsVisible)
        {
            window.Activate();
        }
        if (State.UseCapsuleMode && State.UseDeepCapsuleMode && ShouldPaperOccupyDeepCapsuleSlot(paper, window))
        {
            ArrangeDeepCapsules(animate: State.EnableAnimations);
        }
        RefreshTrayMenu();
        MarkDirty();
    }

    private static void ForceWindowToFront(Window window)
    {
        if (Current.ShouldAvoidFullscreenTopmost && FullscreenForegroundWindowDetector.IsForegroundFullscreen())
        {
            return;
        }

        var restoreTopmost = window.Topmost;
        window.Topmost = true;
        window.Activate();
        window.Focus();
        window.Dispatcher.BeginInvoke(
            () => window.Topmost = restoreTopmost,
            DispatcherPriority.ApplicationIdle);
    }

    private static void ForceWindowToFrontWithEmphasis(Window window, AppState state)
    {
        ForceWindowToFront(window);

        // 强调动画：轻微弹跳
        if (state.EnableAnimations && window.IsVisible)
        {
            window.Dispatcher.InvokeAsync(() =>
            {
                AnimationHelper.QuickBounce(window, 1.03, 100);
            }, DispatcherPriority.Render);
        }
    }

    public void BringPaperToFront(PaperData paper)
    {
        if (!_windows.TryGetValue(paper.Id, out var window) || !window.IsVisible)
        {
            if (paper.IsVisible && window?.IsDeepCapsuleSlotVisible == true)
            {
                ShowPaper(paper);
            }
            return;
        }

        if (!paper.IsCollapsed)
        {
            window.EnsureExpandedSurfaceGeometry();
        }
        ForceWindowToFrontWithEmphasis(window, State);
    }

    private void RefreshTopmostForForegroundWindow()
    {
        var shouldSuppress = false;
        var avoidanceWindow = IntPtr.Zero;
        if (ShouldAvoidFullscreenTopmost)
        {
            var now = DateTimeOffset.UtcNow;
            var allowGlobalScan = now - _lastFullscreenGlobalScanAt >= TimeSpan.FromSeconds(1);
            if (allowGlobalScan)
            {
                _lastFullscreenGlobalScanAt = now;
            }

            if (FullscreenForegroundWindowDetector.TryGetFullscreenWindow(out var fullscreenWindow, allowGlobalScan))
            {
                shouldSuppress = true;
                avoidanceWindow = fullscreenWindow;
            }
        }

        var avoidanceWindowChanged = avoidanceWindow != _fullscreenAvoidanceWindow;
        if (shouldSuppress == _suppressTopmostForFullscreenForeground && !avoidanceWindowChanged)
        {
            if (!shouldSuppress)
            {
                RefreshFloatingSurfaceZOrder();
            }

            if (ShouldAvoidFullscreenTopmost)
            {
                WriteFullscreenDebugSnapshot(shouldSuppress);
            }

            return;
        }

        _suppressTopmostForFullscreenForeground = shouldSuppress;
        _fullscreenAvoidanceWindow = avoidanceWindow;
        foreach (var window in _windows.Values)
        {
            window.RefreshEffectiveTopmost();
        }
        foreach (var m in _masterCapsules.Values) m.RefreshEffectiveTopmost();

        if (ShouldAvoidFullscreenTopmost)
        {
            WriteFullscreenDebugSnapshot(shouldSuppress);
        }
    }

    private void WriteFullscreenDebugSnapshot(bool shouldSuppress)
    {
        if (!EnableFullscreenDebugLog)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var stateChanged = _lastFullscreenDebugSuppressState != shouldSuppress;
        if (!stateChanged && now - _lastFullscreenDebugLogAt < TimeSpan.FromSeconds(3))
        {
            return;
        }

        _lastFullscreenDebugLogAt = now;
        _lastFullscreenDebugSuppressState = shouldSuppress;
        try
        {
            TrimFullscreenDebugLogIfNeeded();
            File.AppendAllText(
                FullscreenDebugLogPath,
                $"==== PaperTodo fullscreen debug shouldSuppress={shouldSuppress} ====" + Environment.NewLine +
                FullscreenForegroundWindowDetector.BuildDebugSnapshot() +
                Environment.NewLine,
                Encoding.UTF8);
        }
        catch
        {
            // Debug logging must never affect normal window behavior.
        }
    }

    private static void TrimFullscreenDebugLogIfNeeded()
    {
        var file = new FileInfo(FullscreenDebugLogPath);
        if (!file.Exists || file.Length < 512 * 1024)
        {
            return;
        }

        File.WriteAllText(FullscreenDebugLogPath, string.Empty, Encoding.UTF8);
    }

    public void RefreshFloatingSurfaceZOrder()
    {
        if (_suppressTopmostForFullscreenForeground)
        {
            return;
        }

        foreach (var window in _windows.Values)
        {
            window.RefreshDeepCapsuleSlotTopmost();
        }
        foreach (var m in _masterCapsules.Values) m.RefreshEffectiveTopmost();
    }

    public void HidePaper(PaperData paper)
    {
        paper.IsVisible = false;
        var visibilityVersion = NextVisibilityAnimationVersion(paper.Id);

        if (_windows.TryGetValue(paper.Id, out var window))
        {
            var saveGeometry = !window.IsDeepCapsulePlaced;
            window.DetachFromDeepCapsuleStack(animate: State.EnableAnimations);

            // 隐藏动画：淡出
            if (State.EnableAnimations && window.IsVisible)
            {
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(window.Opacity, 0, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = AnimationHelper.QuickEase
                };
                fadeOut.Completed += (s, e) =>
                {
                    window.BeginAnimation(Window.OpacityProperty, null);
                    window.Opacity = 1;
                    if (paper.IsVisible || !IsVisibilityAnimationCurrent(paper.Id, visibilityVersion))
                    {
                        return;
                    }

                    window.Hide();
                    if (paper.IsCollapsed)
                    {
                        window.SetCollapsedState(false, animate: false, saveGeometry: saveGeometry);
                    }
                };
                window.BeginAnimation(Window.OpacityProperty, fadeOut);
            }
            else
            {
                window.BeginAnimation(Window.OpacityProperty, null);
                window.Opacity = 1;
                window.Hide();
                if (paper.IsCollapsed)
                {
                    window.SetCollapsedState(false, animate: false, saveGeometry: saveGeometry);
                }
            }
        }
        else
        {
            paper.IsCollapsed = false;
        }

        ArrangeDeepCapsules();
        RefreshTrayMenu();
        MarkDirty();
    }

    public void ShowAllPapers()
    {
        EnsurePapersOnScreen();

        var wasSuppressingDirty = _suppressDirty;
        _suppressDirty = true;
        _trayRefreshSuppressionDepth++;
        try
        {
            foreach (var paper in State.Papers)
            {
                ShowPaper(paper);
            }
        }
        finally
        {
            _trayRefreshSuppressionDepth--;
            _suppressDirty = wasSuppressingDirty;
        }

        RefreshTrayMenu();
        MarkDirty();
    }

    public void HideAllPapers()
    {
        foreach (var paper in State.Papers)
        {
            paper.IsVisible = false;
        }

        foreach (var window in _windows.Values)
        {
            // Fully detach from the stack, not just the expanded reservation: a docked collapsed
            // capsule shows its own slot-host window that a reservation-only clear leaves on screen.
            var saveGeometry = !window.IsDeepCapsulePlaced;
            window.DetachFromDeepCapsuleStack();
            window.Hide();
            window.SetCollapsedState(false, animate: false, saveGeometry: saveGeometry);
        }

        foreach (var paper in State.Papers)
        {
            paper.IsCollapsed = false;
        }

        ArrangeDeepCapsules();
        RefreshTrayMenu();
        MarkDirty();
    }

    public void DeletePaper(PaperData paper)
    {
        var deletedNoteId = paper.Type == PaperTypes.Note ? paper.Id : null;

        if (_windows.TryGetValue(paper.Id, out var window))
        {
            window.CloseForReal();
            _windows.Remove(paper.Id);
        }

        State.Papers.RemoveAll(p => p.Id == paper.Id);

        if (!string.IsNullOrWhiteSpace(deletedNoteId))
        {
            ClearTodoLinksToNote(deletedNoteId);
        }

        if (State.Papers.Count == 0)
        {
            _trayRefreshSuppressionDepth++;
            try
            {
                CreatePaper(PaperTypes.Todo, show: true);
            }
            finally
            {
                _trayRefreshSuppressionDepth--;
            }
        }

        ArrangeDeepCapsules();
        RefreshTrayMenu();
        MarkDirty();
    }

    private void ClearTodoLinksToNote(string noteId)
    {
        var affectedPaperIds = new HashSet<string>();

        foreach (var paper in State.Papers.Where(p => p.Type == PaperTypes.Todo))
        {
            foreach (var item in paper.Items)
            {
                if (!string.Equals(item.LinkedNoteId, noteId, StringComparison.Ordinal))
                {
                    continue;
                }

                item.LinkedNoteId = null;
                affectedPaperIds.Add(paper.Id);
            }
        }

        foreach (var paperId in affectedPaperIds)
        {
            if (_windows.TryGetValue(paperId, out var todoWindow))
            {
                todoWindow.RefreshTodoRowsForExternalChange();
            }
        }

        RefreshCapsuleEligibilityForLinkedNotes();
    }

    public void RefreshCapsuleEligibilityForLinkedNotes()
    {
        foreach (var window in _windows.Values)
        {
            window.RefreshCapsuleEligibility();
        }

        ArrangeDeepCapsules(animate: State.EnableAnimations);
        RefreshTrayMenu();
    }

    public bool IsPaperEmpty(PaperData paper)
    {
        if (paper.Type == PaperTypes.Note)
        {
            return string.IsNullOrWhiteSpace(paper.Content);
        }

        return !paper.Items.Any(item =>
            !string.IsNullOrWhiteSpace(item.Text) ||
            IsExistingNote(item.LinkedNoteId));
    }

    public int VisibleDeepCapsuleCount()
    {
        return DeepCapsulePapersInOrder().Count;
    }

    public double VisibleDeepCapsuleRestingWidth()
    {
        if (!State.UseCapsuleMode || !State.UseDeepCapsuleMode)
        {
            return 0;
        }

        double width = 0;
        foreach (var paper in DeepCapsulePapersInOrder())
        {
            if (_windows.TryGetValue(paper.Id, out var window))
            {
                width = Math.Max(width, window.DeepCapsuleRestingVisibleWidth);
            }
        }

        return width;
    }

    public void ReorderDeepCapsule(PaperData paper, int targetIndex)
    {
        if (!State.UseCapsuleMode || !State.UseDeepCapsuleMode)
        {
            return;
        }

        var capsulePapers = DeepCapsulePapersInOrder();
        var currentIndex = capsulePapers.FindIndex(p => p.Id == paper.Id);
        if (currentIndex < 0 || capsulePapers.Count == 0)
        {
            return;
        }

        targetIndex = Math.Clamp(targetIndex, 0, capsulePapers.Count - 1);
        if (targetIndex == currentIndex)
        {
            ArrangeDeepCapsules(animate: true);
            return;
        }

        var draggedPaper = capsulePapers[currentIndex];
        capsulePapers.RemoveAt(currentIndex);
        targetIndex = Math.Clamp(targetIndex, 0, capsulePapers.Count);
        capsulePapers.Insert(targetIndex, draggedPaper);

        var reorderedCapsuleIds = new HashSet<string>(capsulePapers.Select(p => p.Id));
        var reorderedPapers = new List<PaperData>(State.Papers.Count);
        var capsuleCursor = 0;

        foreach (var existing in State.Papers)
        {
            if (reorderedCapsuleIds.Contains(existing.Id))
            {
                reorderedPapers.Add(capsulePapers[capsuleCursor]);
                capsuleCursor++;
            }
            else
            {
                reorderedPapers.Add(existing);
            }
        }

        State.Papers = reorderedPapers;
        ArrangeDeepCapsules(animate: true);
        RefreshTrayMenu();
        MarkDirty();
    }

    public void UpdateGeometry(PaperData paper, Window window)
    {
        if (window is PaperWindow paperWindow && paperWindow.SuppressGeometrySave)
        {
            return;
        }

        if (double.IsNaN(window.Left) || double.IsNaN(window.Top))
        {
            return;
        }

        paper.X = Math.Round(window.Left);
        paper.Y = Math.Round(window.Top);
        if (!paper.IsCollapsed)
        {
            paper.Width = Math.Round(Math.Max(window.ActualWidth > 0 ? window.ActualWidth : window.Width, PaperLayoutDefaults.MinWidth));
            paper.Height = Math.Round(Math.Max(window.ActualHeight > 0 ? window.ActualHeight : window.Height, PaperLayoutDefaults.MinHeight));
        }

        MarkDirty();
    }

    // A queue is identified by (monitor device, edge). All docked capsules sharing the same
    // pair form one vertical stack with its own master pill.
    private static string QueueKey(string monitorDeviceName, string side)
        => $"{monitorDeviceName ?? ""}|{(side == DeepCapsuleSides.Left ? DeepCapsuleSides.Left : DeepCapsuleSides.Right)}";

    private static string QueueKey(PaperData paper) => QueueKey(paper.CapsuleMonitorDeviceName, paper.CapsuleSide);

    public void ArrangeDeepCapsules(bool animate = false)
    {
        SyncDeepCapsuleAnchor();
        if (!State.UseCapsuleMode || !State.UseDeepCapsuleMode)
        {
            foreach (var window in _windows.Values)
            {
                window.DetachFromDeepCapsuleStack();
            }
            DestroyAllMasterCapsules();
            return;
        }

        var capsulePapers = DeepCapsulePapersInOrder();

        // Group capsule papers into per-(monitor,edge) queues, preserving State.Papers order
        // within each queue. Each queue lays out independently with its own slot indices + master.
        var queues = new Dictionary<string, List<PaperData>>(StringComparer.Ordinal);
        var queueOrder = new List<string>();
        foreach (var paper in capsulePapers)
        {
            var key = QueueKey(paper);
            if (!queues.TryGetValue(key, out var list))
            {
                list = new List<PaperData>();
                queues[key] = list;
                queueOrder.Add(key);
            }
            list.Add(paper);
        }

        var showMasterGlobally = State.UseCapsuleCollapseAll;
        var perQueueIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var paper in State.Papers)
        {
            if (!_windows.TryGetValue(paper.Id, out var window))
            {
                continue;
            }

            if (ShouldPaperOccupyDeepCapsuleSlot(paper, window))
            {
                var key = QueueKey(paper);
                var queueShowMaster = showMasterGlobally && queues.TryGetValue(key, out var q) && q.Count > 0;
                var visualOffset = queueShowMaster ? 1 : 0;
                var slotCount = (queues.TryGetValue(key, out var ql) ? ql.Count : 0) + visualOffset;
                var area = DeepCapsuleLayout.WorkAreaForQueue(paper.CapsuleMonitorDeviceName);
                var startTop = DeepCapsuleLayout.NormalizeStartTopMargin(DeepCapsuleStartTopMarginFor(paper), area, slotCount);
                var retracted = queueShowMaster && State.CapsuleCollapseAllActive;

                var idx = perQueueIndex.TryGetValue(key, out var v) ? v : 0;
                if (retracted)
                {
                    window.RetractIntoMaster(DeepCapsuleLayout.TopForIndex(0, startTop, area), animate);
                }
                else if (paper.IsCollapsed)
                {
                    window.ApplyDeepCapsulePlacement(idx, animate, visualOffset);
                }
                else
                {
                    window.ApplyExpandedDeepCapsuleSlotPlacement(idx, animate, visualOffset);
                }
                perQueueIndex[key] = idx + 1;
            }
            else
            {
                if (!paper.IsVisible && window.HasExpandedDeepCapsuleSlotReservation)
                {
                    continue;
                }

                window.DetachFromDeepCapsuleStack();
            }
        }

        SyncMasterCapsules(queues, queueOrder, animate);
    }

    // Reconcile one master pill per non-empty queue (when collapse-all is on). Creates/updates the
    // masters for live queues and closes masters whose queue disappeared.
    private void SyncMasterCapsules(Dictionary<string, List<PaperData>> queues, List<string> queueOrder, bool animate)
    {
        if (!State.UseCapsuleCollapseAll)
        {
            DestroyAllMasterCapsules();
            return;
        }

        var retracted = State.CapsuleCollapseAllActive;
        var liveKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var key in queueOrder)
        {
            var papers = queues[key];
            if (papers.Count == 0)
            {
                continue;
            }

            liveKeys.Add(key);
            var sample = papers[0];
            var edge = sample.CapsuleSide == DeepCapsuleSides.Left ? DeepCapsuleEdge.Left : DeepCapsuleEdge.Right;
            var monitor = sample.CapsuleMonitorDeviceName;

            if (!_masterCapsules.TryGetValue(key, out var master))
            {
                master = new MasterCapsuleWindow(this, edge, monitor);
                _masterCapsules[key] = master;
                master.ShowPlaced(papers.Count, retracted);
            }
            else
            {
                master.SetQueue(edge, monitor);
                master.UpdateState(papers.Count, retracted, animate);
            }
        }

        // Close masters for queues that no longer exist.
        foreach (var staleKey in _masterCapsules.Keys.Where(k => !liveKeys.Contains(k)).ToList())
        {
            _masterCapsules[staleKey].CloseForReal();
            _masterCapsules.Remove(staleKey);
        }
    }

    private void DestroyAllMasterCapsules()
    {
        if (_masterCapsules.Count == 0)
        {
            return;
        }

        // Collapsing the masters must never strand retracted capsules off-screen at Opacity 0.
        if (State.CapsuleCollapseAllActive)
        {
            State.CapsuleCollapseAllActive = false;
        }

        foreach (var master in _masterCapsules.Values)
        {
            master.CloseForReal();
        }
        _masterCapsules.Clear();
    }

    // Toggle whether the real capsules are retracted behind the master pill.
    public void ToggleCapsuleCollapseAllActive()
    {
        if (!State.UseCapsuleMode || !State.UseDeepCapsuleMode || !State.UseCapsuleCollapseAll)
        {
            return;
        }

        State.CapsuleCollapseAllActive = !State.CapsuleCollapseAllActive;
        ArrangeDeepCapsules(animate: true);
        SaveNow();
    }

    private void ToggleCapsuleCollapseAll()
    {
        State.UseCapsuleCollapseAll = !State.UseCapsuleCollapseAll;

        if (!State.UseCapsuleCollapseAll)
        {
            State.CapsuleCollapseAllActive = false;
            State.DeepCapsuleStartTopMargin = DeepCapsuleLayout.StartTopMargin;
        }

        // Collapse-all rides on top of edge-aligned capsules; enabling it implies both prerequisites.
        if (State.UseCapsuleCollapseAll && (!State.UseCapsuleMode || !State.UseDeepCapsuleMode))
        {
            State.UseCapsuleMode = true;
            State.UseDeepCapsuleMode = true;
            foreach (var window in _windows.Values)
            {
                window.UpdateCapsuleMode();
                window.UpdateDeepCapsuleMode();
            }
        }

        ArrangeDeepCapsules(animate: true);
        SaveNow();
        RebuildTrayMenu();
        RefreshSettingsCapsuleToggleStates();
    }

    private List<PaperData> DeepCapsulePapersInOrder()
    {
        var papers = new List<PaperData>();
        if (!State.UseCapsuleMode || !State.UseDeepCapsuleMode)
        {
            return papers;
        }

        foreach (var paper in State.Papers)
        {
            if (!paper.IsVisible)
            {
                continue;
            }

            if (!_windows.TryGetValue(paper.Id, out var window))
            {
                continue;
            }

            if (ShouldPaperOccupyDeepCapsuleSlot(paper, window))
            {
                papers.Add(paper);
            }
        }

        return papers;
    }

    private bool ShouldPaperOccupyDeepCapsuleSlot(PaperData paper, PaperWindow window)
    {
        if (!paper.IsVisible || !CanPaperDisplayAsCapsule(paper))
        {
            return false;
        }

        if (paper.IsCollapsed)
        {
            return true;
        }

        return State.UseDeepCapsuleMode &&
            State.ShowDeepCapsuleWhileExpanded &&
            window.HasVisibleSurface;
    }

    public void MarkDirty()
    {
        if (_isExiting || _suppressDirty)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow(bool sync = false)
    {
        try
        {
            _saveTimer.Stop();
            var version = Interlocked.Increment(ref _saveVersion);
            var json = _store.SerializeState(State);
            if (sync)
            {
                _store.SaveJsonSync(json, version);
                _hasShownSaveFailure = false;
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _store.SaveJsonAsync(json, version);
                        Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                        {
                            _hasShownSaveFailure = false;
                        }));
                    }
                    catch (Exception ex)
                    {
                        Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                        {
                            HandleSaveFailure(ex);
                        }));
                    }
                });
            }
        }
        catch (Exception ex)
        {
            HandleSaveFailure(ex);
        }
    }

    private static Style BuildDialogButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 4, 10, 4)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Theme.TextBrush));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        var mouseOver = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        mouseOver.Setters.Add(new Setter(Control.BackgroundProperty, Theme.HoverBrush));

        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.7));

        template.Triggers.Add(mouseOver);
        template.Triggers.Add(pressed);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));

        return style;
    }

    private void HandleSaveFailure(Exception ex)
    {
        if (!_hasShownSaveFailure && !_ignoreSaveFailures)
        {
            _hasShownSaveFailure = true;
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                var dlg = new Window
                {
                    Title = Strings.Get("SaveFailureTitle"),
                    Width = 360,
                    Height = 180,
                    WindowStyle = WindowStyle.None,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    Topmost = true
                };
                var border = new Border
                {
                    Background = Theme.PaperBrush,
                    BorderBrush = Theme.PaperBorderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(20)
                };
                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var txt = new TextBlock
                {
                    Text = Strings.Format("SaveFailureMessage", ex.Message),
                    Foreground = Theme.TextBrush,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14
                };
                grid.Children.Add(txt);
                var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetRow(btnPanel, 1);
                var btnOk = new Button { Content = Strings.Get("CommonOk"), Width = 80, Margin = new Thickness(0, 0, 10, 0), Style = BuildDialogButtonStyle() };
                btnOk.Click += (s, e) => { _hasShownSaveFailure = false; dlg.Close(); };
                var btnIgnore = new Button { Content = Strings.Get("SaveFailureIgnore"), Width = 110, Style = BuildDialogButtonStyle() };
                btnIgnore.Click += (s, e) => { _ignoreSaveFailures = true; dlg.Close(); };
                btnPanel.Children.Add(btnOk);
                btnPanel.Children.Add(btnIgnore);
                grid.Children.Add(btnPanel);
                border.Child = grid;
                dlg.Content = border;
                dlg.ShowDialog();
            });
        }
    }

    private static void ShowPaperLimitDialog()
    {
        var dialog = new Window
        {
            Title = Strings.Get("PaperLimitTitle"),
            Width = 340,
            Height = 176,
            MinWidth = 340,
            MinHeight = 176,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true
        };

        var root = new Border
        {
            Background = Theme.PaperBrush,
            BorderBrush = Theme.PaperBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18),
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 2,
                Opacity = 0.22
            }
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = Strings.Get("PaperLimitTitle"),
            Foreground = Theme.TextBrush,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var message = new TextBlock
        {
            Text = Strings.Get("PaperLimitMessage"),
            Foreground = Theme.WeakTextBrush,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var ok = new Button
        {
            Content = Strings.Get("CommonOk"),
            MinWidth = 72,
            Style = BuildDialogButtonStyle()
        };
        ok.Click += (_, _) => dialog.Close();
        buttons.Children.Add(ok);

        Grid.SetRow(title, 0);
        Grid.SetRow(message, 1);
        Grid.SetRow(buttons, 2);

        layout.Children.Add(title);
        layout.Children.Add(message);
        layout.Children.Add(buttons);

        root.Child = layout;
        dialog.Content = root;
        dialog.ShowDialog();
    }

    private bool IsPaperShown(PaperData paper)
    {
        return paper.IsVisible && _windows.TryGetValue(paper.Id, out var window) && window.HasVisibleSurface;
    }

    private bool EnsurePapersOnScreen()
    {
        var changed = false;
        for (var i = 0; i < State.Papers.Count; i++)
        {
            changed |= RescuePaperIfOffScreen(State.Papers[i], i);
        }

        return changed;
    }

    private static bool RescuePaperIfOffScreen(PaperData paper, int offsetIndex)
    {
        var area = WorkAreaForPaper(paper);
        var originalWidth = paper.Width;
        var originalHeight = paper.Height;
        paper.Width = ClampPaperDimension(
            paper.Width,
            paper.Type == PaperTypes.Note ? PaperLayoutDefaults.NoteDefaultWidth : PaperLayoutDefaults.TodoDefaultWidth,
            PaperLayoutDefaults.MinWidth,
            Math.Max(PaperLayoutDefaults.MinWidth, area.Width - 80));
        paper.Height = ClampPaperDimension(
            paper.Height,
            paper.Type == PaperTypes.Note ? PaperLayoutDefaults.NoteDefaultHeight : PaperLayoutDefaults.TodoDefaultHeight,
            PaperLayoutDefaults.MinHeight,
            Math.Max(PaperLayoutDefaults.MinHeight, area.Height - 80));
        var resized = DimensionChanged(originalWidth, paper.Width) ||
            DimensionChanged(originalHeight, paper.Height);

        var clamped = ClampPaperToWorkArea(paper, area);

        if (IsPaperUsablyInsideWorkArea(paper, area))
        {
            return resized || clamped;
        }

        PlacePaperInWorkArea(paper, area, offsetIndex);
        return true;
    }

    private static double ClampPaperDimension(double value, double fallback, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            value = fallback;
        }

        return Math.Clamp(value, min, max);
    }

    private static bool DimensionChanged(double oldValue, double newValue)
    {
        if (double.IsNaN(oldValue) || double.IsInfinity(oldValue))
        {
            return true;
        }

        return Math.Abs(newValue - oldValue) > 0.001;
    }

    private static bool ClampPaperToWorkArea(PaperData paper, Rect area)
    {
        if (!IsFinite(paper.X) || !IsFinite(paper.Y) || area.Width <= 0 || area.Height <= 0)
        {
            return false;
        }

        const double margin = 8;
        var minX = area.Left + margin;
        var maxX = Math.Max(minX, area.Right - paper.Width - margin);
        var minY = area.Top + margin;
        var maxY = Math.Max(minY, area.Bottom - paper.Height - margin);

        var newX = Math.Clamp(paper.X, minX, maxX);
        var newY = Math.Clamp(paper.Y, minY, maxY);
        var changed = Math.Abs(newX - paper.X) > 0.001 || Math.Abs(newY - paper.Y) > 0.001;

        paper.X = newX;
        paper.Y = newY;
        return changed;
    }

    private static bool IsPaperUsablyInsideWorkArea(PaperData paper, Rect area)
    {
        if (!IsFinite(paper.X) || !IsFinite(paper.Y) || !IsFinite(paper.Width) || !IsFinite(paper.Height))
        {
            return false;
        }

        var paperRect = new Rect(
            paper.X,
            paper.Y,
            Math.Max(paper.Width, 80),
            Math.Max(paper.Height, 80));

        return area.Contains(paperRect.TopLeft) &&
               paperRect.Right <= area.Right + 0.001 &&
               paperRect.Bottom <= area.Bottom + 0.001;
    }

    private static void PlacePaperInWorkArea(PaperData paper, Rect area, int offsetIndex)
    {
        const double margin = 40;
        var offset = Math.Min(Math.Max(offsetIndex, 0), 8) * 22;

        var minX = area.Left + margin;
        var maxX = Math.Max(minX, area.Right - paper.Width - margin);
        var minY = area.Top + margin;
        var maxY = Math.Max(minY, area.Bottom - paper.Height - margin);

        paper.X = Math.Round(Math.Clamp(area.Left + margin + offset, minX, maxX));
        paper.Y = Math.Round(Math.Clamp(area.Top + margin + offset, minY, maxY));
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static Rect WorkAreaForPaper(PaperData paper)
    {
        if (IsFinite(paper.X) &&
            IsFinite(paper.Y) &&
            IsFinite(paper.Width) &&
            IsFinite(paper.Height) &&
            paper.Width > 0 &&
            paper.Height > 0)
        {
            return WindowWorkAreaHelper.WorkAreaFor(new Rect(paper.X, paper.Y, paper.Width, paper.Height));
        }

        return SystemParameters.WorkArea;
    }

    // Push the persisted dock anchor (edge + monitor) into the shared layout statics so every
    // capsule and the master pill resolve geometry against the same screen. Cheap and idempotent.
    private void SyncDeepCapsuleAnchor()
    {
        var edge = State.DeepCapsuleSide == DeepCapsuleSides.Left
            ? DeepCapsuleEdge.Left
            : DeepCapsuleEdge.Right;
        DeepCapsuleLayout.SetAnchor(edge, State.DeepCapsuleMonitorDeviceName);
    }

    // Per-queue vertical rest position. For now all queues share the legacy global margin;
    // Stage 4 will key this by (monitor, edge). Centralized here so callers don't read State.
    public double DeepCapsuleStartTopMarginFor(PaperData paper)
    {
        return State.DeepCapsuleStartTopMargin;
    }

    public double DeepCapsuleStartTopMarginForQueue(string monitorDeviceName, DeepCapsuleEdge edge)
    {
        return State.DeepCapsuleStartTopMargin;
    }

    // Commit a new dock anchor (monitor + edge) chosen by dragging the master pill, then relayout.
    // startTopMargin is the vertical rest position resolved against the NEW monitor's work area.
    public void SetDeepCapsuleAnchor(string monitorDeviceName, string side, double startTopMargin)
    {
        if (!State.UseCapsuleMode || !State.UseDeepCapsuleMode)
        {
            return;
        }

        State.DeepCapsuleMonitorDeviceName = (monitorDeviceName ?? "").Trim();
        State.DeepCapsuleSide = DeepCapsuleSides.Normalize(side);
        SyncDeepCapsuleAnchor();

        var slotCount = VisibleDeepCapsuleCount() + (State.UseCapsuleCollapseAll && VisibleDeepCapsuleCount() > 0 ? 1 : 0);
        State.DeepCapsuleStartTopMargin = DeepCapsuleLayout.NormalizeStartTopMargin(startTopMargin, slotCount);

        ArrangeDeepCapsules(animate: true);
        SaveNow();
    }

    // Live-adjust the stack's vertical rest position while the master pill is dragged within its
    // magnetic X band. Relayouts every capsule immediately (cheap for a small stack); persists
    // only when commit is set (drag release), so mid-drag frames don't thrash the save path.
    public void SetDeepCapsuleStartTopMargin(double startTopMargin, bool commit = false)
    {
        if (!State.UseCapsuleMode || !State.UseDeepCapsuleMode)
        {
            return;
        }

        var slotCount = VisibleDeepCapsuleCount() + (State.UseCapsuleCollapseAll && VisibleDeepCapsuleCount() > 0 ? 1 : 0);
        var normalized = DeepCapsuleLayout.NormalizeStartTopMargin(startTopMargin, slotCount);

        if (Math.Abs(State.DeepCapsuleStartTopMargin - normalized) < 0.01)
        {
            if (commit)
            {
                SaveNow();
            }
            return;
        }

        State.DeepCapsuleStartTopMargin = normalized;
        ArrangeDeepCapsules(animate: false);

        if (commit)
        {
            SaveNow();
        }
        else
        {
            MarkDirty();
        }
    }

    public void Exit()
    {
        _isExiting = true;
        _saveTimer.Stop();
        if (_trayMenu != null)
        {
            _trayMenu.IsOpen = false;
        }
        _settingsWindow?.Close();
        _settingsWindow = null;
        SaveNow(sync: true);

        _trayIcon?.Dispose();
        _trayIcon = null;
        _trayMenu = null;

        foreach (var window in _windows.Values.ToList())
        {
            window.CloseForReal();
        }

        Application.Current.Shutdown();
        Environment.Exit(0);
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _saveTimer.Stop();
        _topmostRefreshTimer.Stop();
        if (_trayMenu != null)
        {
            _trayMenu.IsOpen = false;
        }
        _settingsWindow?.Close();
        _settingsWindow = null;
        foreach (var window in _windows.Values)
        {
            window.ClearDeepCapsuleSlotReservation();
        }
        foreach (var m in _masterCapsules.Values)
        {
            m.CloseForReal();
        }
        _masterCapsules.Clear();
        _trayIcon?.Dispose();
        _trayIcon = null;
        _trayMenu = null;
    }
}
