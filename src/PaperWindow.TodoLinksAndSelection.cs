using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private sealed class TodoSweepSelectionState
    {
        public TodoSweepSelectionState(
            string anchorItemId,
            Border anchorRow,
            TodoTextBox? sourceTextBox)
        {
            AnchorItemId = anchorItemId;
            AnchorRow = anchorRow;
            SourceTextBox = sourceTextBox;
        }

        public string AnchorItemId { get; }
        public Border AnchorRow { get; }
        public TodoTextBox? SourceTextBox { get; }
        public bool IsPromoted { get; set; }
    }

    private readonly HashSet<string> _selectedTodoItemIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _todoGroupDragItemIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _todoGroupDragRestingOpacities = new(StringComparer.Ordinal);
    private TodoSweepSelectionState? _todoSweepSelection;
    private bool _todoSweepOwnsMouseCapture;
    private bool _todoSelectionInputHooksInstalled;
    private DispatcherTimer? _todoSweepTrackingTimer;

    private static Brush TodoSelectionBrush =>
        Theme.Tint((byte)(Theme.IsDark ? 62 : 42));

    private static Brush TodoReminderActiveBrush =>
        Theme.Danger((byte)(Theme.IsDark ? 52 : 38));

    private bool IsTodoGroupDrag => _todoGroupDragItemIds.Count > 1;

    private void EnsureTodoSelectionInputHooks()
    {
        if (_todoSelectionInputHooksInstalled)
        {
            return;
        }

        _todoSelectionInputHooksInstalled = true;
        PreviewMouseLeftButtonDown += OnTodoSelectionWindowPreviewMouseLeftButtonDown;
        PreviewKeyDown += OnTodoSelectionWindowPreviewKeyDown;
        PreviewMouseMove += OnTodoSweepPreviewMouseMove;
        PreviewMouseLeftButtonUp += OnTodoSweepPreviewMouseLeftButtonUp;
        LostMouseCapture += OnTodoSweepLostMouseCapture;
        Deactivated += OnTodoSweepWindowDeactivated;
    }

    private void OnTodoSelectionWindowPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_paper.Type != PaperTypes.Todo ||
            e.ChangedButton != MouseButton.Left ||
            _todoSweepSelection != null)
        {
            return;
        }

        if (FindTodoRowAncestor(e.OriginalSource as DependencyObject) == null)
        {
            ClearTodoSelection();
        }
    }

    private void OnTodoSelectionWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C &&
            Keyboard.Modifiers == ModifierKeys.Control &&
            TryCopySelectedTodoItems())
        {
            e.Handled = true;
        }
    }

    private void ConfigureTodoMultiSelection(
        Border row,
        PaperItem item,
        CheckBox check,
        TodoTextBox text)
    {
        row.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || _todoDrag != null)
            {
                return;
            }

            var source = e.OriginalSource as DependencyObject;
            if (IsDescendantOf(source, check) &&
                _selectedTodoItemIds.Count > 1 &&
                _selectedTodoItemIds.Contains(item.Id))
            {
                var selected = SelectedTodoItems();
                ApplyDoneToSelectedTodos(!selected.All(candidate => candidate.Done));
                e.Handled = true;
                return;
            }

            if (IsDescendantOf(source, check) ||
                IsDescendantOfCursor(source, Cursors.SizeAll) ||
                IsDescendantOfCursor(source, Cursors.Hand))
            {
                return;
            }

            // Do not consume the press. TodoTextBox keeps normal character selection until the
            // held pointer actually enters a different todo row; only then do we promote the
            // gesture to whole-item sweep selection.
            if (_selectedTodoItemIds.Count > 0)
            {
                ClearTodoSelection();
            }

            ArmTodoSweepSelection(
                item.Id,
                row,
                IsDescendantOf(source, text) ? text : null);
        };

        UpdateTodoRowBackground(row);
    }

    private static bool IsDescendantOfType<T>(DependencyObject? source)
        where T : DependencyObject
    {
        var current = source;
        while (current != null)
        {
            if (current is T)
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private static bool IsDescendantOfCursor(DependencyObject? source, Cursor cursor)
    {
        var current = source;
        while (current != null)
        {
            if (current is FrameworkElement element &&
                ReferenceEquals(element.Cursor, cursor))
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private Border? FindTodoRowAncestor(DependencyObject? source)
    {
        var current = source;
        while (current != null)
        {
            if (current is Border row && _todoRows.Contains(row))
            {
                return row;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void ArmTodoSweepSelection(
        string itemId,
        Border row,
        TodoTextBox? sourceTextBox)
    {
        _todoSweepSelection = new TodoSweepSelectionState(
            itemId,
            row,
            sourceTextBox);
        StartTodoSweepTracking();
    }

    private bool PromoteTodoSweepSelection(
        TodoSweepSelectionState state,
        string targetItemId)
    {
        // Transfer capture while the gesture is still only armed. The TextBox capture-loss event
        // bubbles through PaperWindow; marking the sweep promoted before this transfer could clear
        // the state while leaving the window itself captured.
        if (!CaptureMouse())
        {
            return false;
        }

        _todoSweepOwnsMouseCapture = true;
        state.IsPromoted = true;
        if (state.SourceTextBox != null)
        {
            state.SourceTextBox.Select(
                Math.Clamp(state.SourceTextBox.CaretIndex, 0, state.SourceTextBox.Text.Length),
                0);
        }
        FocusTodoSweepSelectionHost();
        _selectedTodoItemIds.Clear();
        _selectedTodoItemIds.Add(state.AnchorItemId);
        SelectTodoRange(state.AnchorItemId, targetItemId);
        return true;
    }

    private void FocusTodoSweepSelectionHost()
    {
        // Keep keyboard focus inside the paper without leaving any TodoTextBox active. Sweep
        // highlighting is drawn independently, so no editor retains a caret or real text range.
        _paperChrome.Focusable = true;
        _paperChrome.FocusVisualStyle = null;
        KeyboardNavigation.SetIsTabStop(_paperChrome, false);
        FocusManager.SetFocusedElement(this, _paperChrome);
        Keyboard.Focus(_paperChrome);
        if (!ReferenceEquals(Keyboard.FocusedElement, _paperChrome))
        {
            Keyboard.ClearFocus();
        }
    }

    private void OnTodoSweepPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var state = _todoSweepSelection;
        if (state == null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndTodoSweepSelection();
            return;
        }

        var point = e.GetPosition(this);
        if (!state.IsPromoted)
        {
            var firstTargetItemId = FindTodoSweepPromotionTargetItemId(state, point);
            if (firstTargetItemId == null)
            {
                // The original TextBox still owns this gesture and may select characters.
                return;
            }

            if (!PromoteTodoSweepSelection(state, firstTargetItemId))
            {
                CancelTodoSweepSelection(clearSelection: false);
                return;
            }
        }

        UpdatePromotedTodoSweepSelection(state, point, autoScroll: true);
        e.Handled = true;
    }

    private void UpdatePromotedTodoSweepSelection(
        TodoSweepSelectionState state,
        Point pointOnWindow,
        bool autoScroll)
    {
        if (autoScroll)
        {
            AutoScrollTodoSelection(pointOnWindow);
        }

        if (FindTodoRowForSweep(pointOnWindow)?.Tag is string targetItemId)
        {
            SelectTodoRange(state.AnchorItemId, targetItemId);
        }
    }

    private void StartTodoSweepTracking()
    {
        if (_todoSweepTrackingTimer == null)
        {
            _todoSweepTrackingTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(32)
            };
            _todoSweepTrackingTimer.Tick += OnTodoSweepTrackingTick;
        }

        _todoSweepTrackingTimer.Stop();
        _todoSweepTrackingTimer.Start();
    }

    private void StopTodoSweepTracking()
    {
        _todoSweepTrackingTimer?.Stop();
    }

    private void OnTodoSweepTrackingTick(object? sender, EventArgs e)
    {
        var state = _todoSweepSelection;
        if (state == null)
        {
            StopTodoSweepTracking();
            return;
        }

        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            EndTodoSweepSelection();
            return;
        }

        var point = Mouse.GetPosition(this);
        if (!state.IsPromoted)
        {
            var firstTargetItemId = FindTodoSweepPromotionTargetItemId(state, point);
            if (firstTargetItemId == null)
            {
                return;
            }

            if (!PromoteTodoSweepSelection(state, firstTargetItemId))
            {
                CancelTodoSweepSelection(clearSelection: false);
                return;
            }
        }

        UpdatePromotedTodoSweepSelection(state, point, autoScroll: true);
    }

    private void OnTodoSweepPreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_todoSweepSelection == null)
        {
            ReleaseTodoSweepMouseCapture();
            return;
        }

        var promoted = _todoSweepSelection.IsPromoted;
        var clearExistingSelection =
            !promoted && _selectedTodoItemIds.Count > 0;
        EndTodoSweepSelection();
        if (clearExistingSelection)
        {
            ClearTodoSelection();
        }
        e.Handled = promoted;
    }

    private void EndTodoSweepSelection()
        => CancelTodoSweepSelection(clearSelection: false);

    private void CancelTodoSweepSelection(bool clearSelection)
    {
        StopTodoSweepTracking();
        _todoSweepSelection = null;
        if (clearSelection)
        {
            _selectedTodoItemIds.Clear();
        }
        ReleaseTodoSweepMouseCapture();
        ApplyTodoSelectionVisuals();
    }

    private void ReleaseTodoSweepMouseCapture()
    {
        if (!_todoSweepOwnsMouseCapture)
        {
            return;
        }

        _todoSweepOwnsMouseCapture = false;
        if (IsMouseCaptured && _todoDrag == null)
        {
            ReleaseMouseCapture();
        }
    }

    private void OnTodoSweepLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_todoSweepOwnsMouseCapture || ReferenceEquals(Mouse.Captured, this))
        {
            // Ignore a descendant TextBox losing capture while ownership transfers to the window.
            return;
        }

        StopTodoSweepTracking();
        _todoSweepOwnsMouseCapture = false;
        _todoSweepSelection = null;
        ApplyTodoSelectionVisuals();
    }

    private void OnTodoSweepWindowDeactivated(object? sender, EventArgs e)
    {
        if (_todoSweepSelection != null ||
            _todoSweepOwnsMouseCapture ||
            _selectedTodoItemIds.Count > 0)
        {
            CancelTodoSweepSelection(clearSelection: true);
        }
    }

    private void AutoScrollTodoSelection(Point pointOnWindow)
    {
        var scrollViewer = FindVisualAncestor<ScrollViewer>(_todoPanel);
        if (scrollViewer == null || scrollViewer.ActualHeight <= 0)
        {
            return;
        }

        var point = TranslatePoint(pointOnWindow, scrollViewer);
        var edge = Math.Min(AppTypography.Scale(28), scrollViewer.ActualHeight / 4);
        var direction = 0;
        var overflow = 0d;
        if (point.Y < edge)
        {
            direction = -1;
            overflow = edge - point.Y;
        }
        else if (point.Y > scrollViewer.ActualHeight - edge)
        {
            direction = 1;
            overflow = point.Y - (scrollViewer.ActualHeight - edge);
        }

        if (direction == 0)
        {
            return;
        }

        var stepDistance = Math.Max(AppTypography.Scale(14), edge * 0.7);
        var steps = Math.Clamp(
            1 + (int)Math.Floor(Math.Max(0, overflow - 1) / stepDistance),
            1,
            6);
        for (var index = 0; index < steps; index++)
        {
            if (direction < 0)
            {
                scrollViewer.LineUp();
            }
            else
            {
                scrollViewer.LineDown();
            }
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject? start)
        where T : DependencyObject
    {
        var current = start;
        while (current != null)
        {
            if (current is T result)
            {
                return result;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private Border? FindTodoRowAtPoint(Point pointOnWindow)
    {
        foreach (var row in _todoRows)
        {
            if (!row.IsVisible || row.ActualWidth <= 0 || row.ActualHeight <= 0)
            {
                continue;
            }

            var origin = row.TranslatePoint(new Point(0, 0), this);
            if (pointOnWindow.X >= origin.X &&
                pointOnWindow.X <= origin.X + row.ActualWidth &&
                pointOnWindow.Y >= origin.Y &&
                pointOnWindow.Y <= origin.Y + row.ActualHeight)
            {
                return row;
            }
        }
        return null;
    }

    private Border? FindTodoRowForSweep(Point pointOnWindow)
    {
        var scrollViewer = FindVisualAncestor<ScrollViewer>(_todoPanel);
        var viewportTop = double.NegativeInfinity;
        var viewportBottom = double.PositiveInfinity;
        if (scrollViewer != null && scrollViewer.ActualHeight > 0)
        {
            var viewportOrigin = scrollViewer.TranslatePoint(new Point(0, 0), this);
            viewportTop = viewportOrigin.Y;
            viewportBottom = viewportOrigin.Y + scrollViewer.ActualHeight;
        }

        Border? nearest = null;
        var nearestDistance = double.PositiveInfinity;
        foreach (var row in _todoRows)
        {
            if (!row.IsVisible || row.ActualWidth <= 0 || row.ActualHeight <= 0)
            {
                continue;
            }

            var origin = row.TranslatePoint(new Point(0, 0), this);
            var top = origin.Y;
            var bottom = origin.Y + row.ActualHeight;
            if (bottom < viewportTop || top > viewportBottom)
            {
                continue;
            }

            if (pointOnWindow.Y >= top && pointOnWindow.Y <= bottom)
            {
                return row;
            }

            var distance = pointOnWindow.Y < top
                ? top - pointOnWindow.Y
                : pointOnWindow.Y - bottom;
            if (distance < nearestDistance)
            {
                nearest = row;
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    private string? FindTodoSweepPromotionTargetItemId(
        TodoSweepSelectionState state,
        Point pointOnWindow)
    {
        var row = FindTodoRowForSweep(pointOnWindow);
        if (row != null &&
            !ReferenceEquals(row, state.AnchorRow) &&
            row.Tag is string rowItemId)
        {
            return rowItemId;
        }

        var scrollViewer = FindVisualAncestor<ScrollViewer>(_todoPanel);
        if (scrollViewer == null || scrollViewer.ActualHeight <= 0)
        {
            return null;
        }

        var viewportOrigin = scrollViewer.TranslatePoint(new Point(0, 0), this);
        var direction = pointOnWindow.Y < viewportOrigin.Y
            ? -1
            : pointOnWindow.Y > viewportOrigin.Y + scrollViewer.ActualHeight
                ? 1
                : 0;
        if (direction == 0)
        {
            return null;
        }

        var orderedIds = OrderedItems().Select(item => item.Id).ToList();
        var anchorIndex = orderedIds.IndexOf(state.AnchorItemId);
        var targetIndex = anchorIndex + direction;
        return anchorIndex >= 0 && targetIndex >= 0 && targetIndex < orderedIds.Count
            ? orderedIds[targetIndex]
            : null;
    }

    private void SelectTodoRange(string anchorItemId, string targetItemId)
    {
        var orderedIds = OrderedItems().Select(item => item.Id).ToList();
        var anchorIndex = orderedIds.IndexOf(anchorItemId);
        var targetIndex = orderedIds.IndexOf(targetItemId);
        if (anchorIndex < 0 || targetIndex < 0)
        {
            return;
        }

        var start = Math.Min(anchorIndex, targetIndex);
        var end = Math.Max(anchorIndex, targetIndex);
        _selectedTodoItemIds.Clear();
        for (var index = start; index <= end; index++)
        {
            _selectedTodoItemIds.Add(orderedIds[index]);
        }
        ApplyTodoSelectionVisuals();
    }

    private void PruneTodoSelection()
    {
        var validIds = _paper.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        _selectedTodoItemIds.RemoveWhere(itemId => !validIds.Contains(itemId));
        _todoGroupDragItemIds.RemoveWhere(itemId => !validIds.Contains(itemId));
    }

    private void ClearTodoSelection()
    {
        if (_selectedTodoItemIds.Count == 0)
        {
            return;
        }

        _selectedTodoItemIds.Clear();
        ApplyTodoSelectionVisuals();
    }

    private void ApplyTodoSelectionVisuals()
    {
        foreach (var row in _todoRows)
        {
            if (!ReferenceEquals(row, _activeDropRow) &&
                !ReferenceEquals(row, _linkedPaperDropRow))
            {
                UpdateTodoRowBackground(row);
            }
        }
    }

    private void UpdateTodoRowBackground(Border row)
    {
        var itemId = row.Tag as string;
        var selected = itemId != null && _selectedTodoItemIds.Contains(itemId);
        var reminding = itemId != null &&
            _controller.State.ExperimentalTodoReminders &&
            _paper.Items.Any(item =>
                string.Equals(item.Id, itemId, StringComparison.Ordinal) &&
                !item.Done &&
                item.ReminderTriggered);
        row.Background = selected
            ? TodoSelectionBrush
            : reminding
                ? TodoReminderActiveBrush
                : row.IsMouseOver ? HoverBrush : Brushes.Transparent;

        if (itemId == null || !_todoEditors.TryGetValue(itemId, out var text))
        {
            return;
        }

        text.IsSweepSelected = selected;
        text.Foreground = text.IsDone ? BrightWeakTextBrush : TextBrush;
    }

    private List<PaperItem> SelectedTodoItems()
    {
        return OrderedItems()
            .Where(item => _selectedTodoItemIds.Contains(item.Id))
            .ToList();
    }

    private bool TryCopySelectedTodoItems()
    {
        if (_selectedTodoItemIds.Count == 0)
        {
            return false;
        }

        if (FocusManager.GetFocusedElement(this) is TodoTextBox box &&
            box.SelectionLength > 0)
        {
            return false;
        }

        var text = string.Join(
            Environment.NewLine,
            SelectedTodoItems().Select(item => item.Text));
        return ClipboardHelper.TrySetText(text);
    }

    private bool TryClearTodoSelectionFromEscape()
    {
        if (_todoSweepSelection == null && _selectedTodoItemIds.Count == 0)
        {
            return false;
        }

        CancelTodoSweepSelection(clearSelection: true);
        return true;
    }

    private bool MoveTodoItemsAfterDoneChange(
        IReadOnlyCollection<PaperItem> changedItems,
        bool done)
    {
        _ = changedItems;
        _ = done;
        return TodoRules.ApplyCompletedOrdering(
            _paper.Items,
            _controller.State.AutoMoveCompletedTodosToBottom);
    }

    private void ApplyDoneToSelectedTodos(bool done)
    {
        var selected = SelectedTodoItems();
        if (selected.Count == 0 || selected.All(item => item.Done == done))
        {
            return;
        }

        var previousItems = CloneItems(_paper.Items);
        PushUndoSnapshot();

        foreach (var item in selected)
        {
            item.Done = done;
            if (done)
            {
                item.ReminderAt = null;
                item.ReminderTriggered = false;
            }
        }

        if (done && _controller.State.AutoClearCompletedTodos)
        {
            var selectedIds = selected.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            _paper.Items.RemoveAll(item => selectedIds.Contains(item.Id));
            if (_paper.Items.Count == 0)
            {
                _paper.Items.Add(new PaperItem());
            }
            _selectedTodoItemIds.Clear();
        }
        else
        {
            // Move the whole changed block once so batch completion keeps its relative order.
            MoveTodoItemsAfterDoneChange(selected, done);
        }

        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();
        _controller.NotifyTodoReminderCollectionChanged();
        ReconcileTodoRows(
            done && _controller.State.AutoClearCompletedTodos
                ? null
                : selected.Select(item => item.Id));
        RefreshCapsuleEligibilityForLinkedPaperChanges(previousItems);
    }

    private void DeleteSelectedTodoItems()
    {
        var selected = SelectedTodoItems();
        if (selected.Count == 0)
        {
            return;
        }

        var previousItems = CloneItems(_paper.Items);
        var selectedIds = selected.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        PushUndoSnapshot();
        _paper.Items.RemoveAll(item => selectedIds.Contains(item.Id));
        if (_paper.Items.Count == 0)
        {
            _paper.Items.Add(new PaperItem());
        }

        _selectedTodoItemIds.Clear();
        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();
        _controller.NotifyTodoReminderCollectionChanged();
        ReconcileTodoRows();
        RefreshCapsuleEligibilityForLinkedPaperChanges(previousItems);
    }

    private bool TryCreateTodoSelectionContextMenu(
        PaperItem item,
        Border row,
        out ContextMenu menu)
    {
        menu = null!;
        if (_selectedTodoItemIds.Count <= 1 ||
            !_selectedTodoItemIds.Contains(item.Id))
        {
            return false;
        }

        var selected = SelectedTodoItems();
        menu = CreateContextMenu();
        menu.Items.Add(MenuHeader(Strings.Format(
            "MenuSelectedTodoCount",
            selected.Count)));
        menu.Items.Add(MenuItem(
            Strings.Get("MenuCopySelectedTodos"),
            (_, _) => TryCopySelectedTodoItems()));
        menu.Items.Add(MenuItem(
            selected.All(candidate => candidate.Done)
                ? Strings.Get("MenuUncompleteSelectedTodos")
                : Strings.Get("MenuCompleteSelectedTodos"),
            (_, _) => ApplyDoneToSelectedTodos(
                !selected.All(candidate => candidate.Done))));
        menu.Items.Add(MenuSeparator());
        menu.Items.Add(MenuItem(
            Strings.Format("MenuDeleteSelectedTodos", selected.Count),
            (_, _) => DeleteSelectedTodoItems()));
        menu.Opened += (_, _) => row.Background = TodoSelectionBrush;
        menu.Closed += (_, _) => UpdateTodoRowBackground(row);
        return true;
    }

    private void PrepareTodoSelectionForContextMenu(string itemId)
    {
        if (_selectedTodoItemIds.Count > 0 &&
            !_selectedTodoItemIds.Contains(itemId))
        {
            ClearTodoSelection();
        }
    }

    private void PrepareTodoDragSelection(string itemId)
    {
        _todoGroupDragItemIds.Clear();
        if (_selectedTodoItemIds.Count > 1 &&
            _selectedTodoItemIds.Contains(itemId))
        {
            foreach (var selectedId in _selectedTodoItemIds)
            {
                _todoGroupDragItemIds.Add(selectedId);
            }
            return;
        }

        ClearTodoSelection();
        _todoGroupDragItemIds.Add(itemId);
    }

    private void BeginTodoGroupDragVisuals(string sourceItemId)
    {
        _todoGroupDragRestingOpacities.Clear();
        if (!IsTodoGroupDrag)
        {
            return;
        }

        foreach (var row in _todoRows)
        {
            if (row.Tag is not string itemId ||
                !_todoGroupDragItemIds.Contains(itemId) ||
                string.Equals(itemId, sourceItemId, StringComparison.Ordinal))
            {
                continue;
            }

            var opacity = (double)row.GetAnimationBaseValue(OpacityProperty);
            _todoGroupDragRestingOpacities[itemId] = opacity;
            row.BeginAnimation(OpacityProperty, null);
            row.Opacity = 0.25;
        }
    }

    private void EndTodoGroupDragVisuals()
    {
        foreach (var (itemId, opacity) in _todoGroupDragRestingOpacities)
        {
            var row = _todoRows.FirstOrDefault(candidate =>
                candidate.Tag is string candidateId &&
                string.Equals(candidateId, itemId, StringComparison.Ordinal));
            if (row == null)
            {
                continue;
            }

            row.BeginAnimation(OpacityProperty, null);
            row.Opacity = opacity;
            UpdateTodoRowBackground(row);
        }
        _todoGroupDragRestingOpacities.Clear();
    }

    private bool RestrictTodoGroupDragToTrash()
    {
        if (!IsTodoGroupDrag)
        {
            return false;
        }

        if (_todoDrag != null)
        {
            _todoDrag.TargetId = null;
            _todoDrag.DropAtEnd = false;
        }
        return true;
    }

    private string TodoDragGhostText(string fallback)
    {
        return IsTodoGroupDrag
            ? Strings.Format("TodoDragSelectedCount", _todoGroupDragItemIds.Count)
            : fallback;
    }

    private bool DeleteTodoGroupDragItems()
    {
        if (!IsTodoGroupDrag)
        {
            return false;
        }

        DeleteSelectedTodoItems();
        ClearTodoDragGroupState();
        return true;
    }

    private void ClearTodoDragGroupState()
    {
        _todoGroupDragItemIds.Clear();
        _todoGroupDragRestingOpacities.Clear();
    }

    private void ConfigureTodoPathDrop(Border row, PaperItem item)
    {
        row.AddHandler(
            DragDrop.PreviewDragEnterEvent,
            new DragEventHandler((_, e) => UpdateTodoPathDropEffect(row, e)),
            handledEventsToo: true);
        row.AddHandler(
            DragDrop.PreviewDragOverEvent,
            new DragEventHandler((_, e) => UpdateTodoPathDropEffect(row, e)),
            handledEventsToo: true);
        row.AddHandler(
            DragDrop.PreviewDragLeaveEvent,
            new DragEventHandler((_, _) => ResetTodoPathDropVisual(row)),
            handledEventsToo: true);
        row.AddHandler(
            DragDrop.PreviewDropEvent,
            new DragEventHandler((_, e) =>
            {
                try
                {
                    var paths = GetTodoFileDropPaths(e.Data);
                    if (paths.Length != 1)
                    {
                        MessageBox.Show(
                            this,
                            Strings.Get("LinkedPathSingleDropMessage"),
                            Strings.Get("LinkedPathDropFailureTitle"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return;
                    }

                    var path = Path.GetFullPath(paths[0]);
                    var isFile = File.Exists(path);
                    var isDirectory = !isFile && Directory.Exists(path);
                    if (!isFile && !isDirectory)
                    {
                        MessageBox.Show(
                            this,
                            Strings.Format("LinkedPathMissingMessage", path),
                            Strings.Get("LinkedPathOpenFailureTitle"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }

                    LinkPathToTodo(item, path, isDirectory);
                    e.Effects = DragDropEffects.Link;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        this,
                        Strings.Format("LinkedPathDropFailureMessage", ex.Message),
                        Strings.Get("LinkedPathDropFailureTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                finally
                {
                    ResetTodoPathDropVisual(row);
                    e.Handled = true;
                }
            }),
            handledEventsToo: true);
    }

    private void UpdateTodoPathDropEffect(Border row, DragEventArgs e)
    {
        var paths = GetTodoFileDropPaths(e.Data);
        if (paths.Length == 1)
        {
            e.Effects = DragDropEffects.Link;
            row.Background = PaperLinkTargetBgBrush;
            row.BorderBrush = PaperLinkTargetBorderBrush;
            row.BorderThickness = new Thickness(1);
            row.Padding = new Thickness(1, 3, 1, 3);
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private static string[] GetTodoFileDropPaths(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) ||
            data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return [];
        }
        return paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
    }

    private void ResetTodoPathDropVisual(Border row)
    {
        if (ReferenceEquals(row, _linkedPaperDropRow) ||
            ReferenceEquals(row, _activeDropRow))
        {
            return;
        }

        row.BorderThickness = new Thickness(0, 2, 0, 2);
        row.BorderBrush = Brushes.Transparent;
        row.Padding = new Thickness(2);
        UpdateTodoRowBackground(row);
    }

    private void LinkPathToTodo(PaperItem item, string path, bool isDirectory)
    {
        if (string.Equals(item.LinkedPath, path, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(item.LinkedPaperId) &&
            item.LinkedPathIsDirectory == isDirectory)
        {
            return;
        }

        var focusedId = CurrentFocusedTodoItemId() ?? item.Id;
        var previousItems = CloneItems(_paper.Items);
        PushUndoSnapshot();
        item.LinkPath(path, isDirectory);
        _controller.MarkDirty();
        ReconcileTodoRows([item.Id], focusedId);
        RefreshCapsuleEligibilityForLinkedPaperChanges(previousItems);
    }

    private void UnlinkPathFromTodoItem(PaperItem item)
    {
        if (string.IsNullOrWhiteSpace(item.LinkedPath))
        {
            return;
        }

        var focusedId = CurrentFocusedTodoItemId() ?? item.Id;
        PushUndoSnapshot();
        item.ClearQuickLaunch();
        _controller.MarkDirty();
        ReconcileTodoRows([item.Id], focusedId);
    }

    private Border BuildTodoPathLinkButton(
        PaperItem item,
        TodoTextBox text,
        TodoVisualMetrics metrics)
    {
        var path = item.LinkedPath ?? "";
        var showName = _controller.State.ShowLinkedPaperName;
        var allowLongName =
            showName && _controller.State.AllowLongLinkedPaperTitles;
        var label = PathDisplayName(path);

        string LinkedPathButtonLabel(bool isTodoMultiline) =>
            TodoLinkedPathLabel(item, path, label, allowLongName, isTodoMultiline);

        double LegacyLinkedPathButtonWidth(bool isTodoMultiline) =>
            isTodoMultiline
                ? Math.Max(44, metrics.CheckColumnWidth * 2)
                : Math.Max(50, metrics.CheckColumnWidth * 2.2);

        double LinkedPathButtonWidth(bool isTodoMultiline, string value)
        {
            var legacyWidth = LegacyLinkedPathButtonWidth(isTodoMultiline);
            if (!allowLongName)
            {
                return legacyWidth;
            }

            var measuredWidth = MeasureCapsuleTextWidth(
                value,
                metrics.LinkedPaperNameFontSize,
                FontWeights.SemiBold,
                AppTypography.UiFontFamily) + 10;
            return Math.Max(legacyWidth, Math.Ceiling(measuredWidth));
        }

        var linkedPathButtonText = LinkedPathButtonLabel(isTodoMultiline: false);
        var multilineLinkedPathButtonText = LinkedPathButtonLabel(isTodoMultiline: true);
        var width = showName
            ? Math.Max(
                LinkedPathButtonWidth(false, linkedPathButtonText),
                LinkedPathButtonWidth(true, multilineLinkedPathButtonText))
            : Math.Max(23, metrics.CheckColumnWidth);
        var glyph = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };
        var button = new Border
        {
            Width = width,
            MinWidth = Math.Max(23, metrics.CheckColumnWidth),
            MinHeight = Math.Max(22, metrics.RowMinHeight - 2),
            Margin = new Thickness(1, 0, 0, 0),
            Padding = showName ? new Thickness(3, 1, 3, 1) : new Thickness(0),
            CornerRadius = new CornerRadius(RadiusControl),
            Cursor = Cursors.Hand,
            Child = glyph
        };

        void RefreshPresentation(bool hovered)
        {
            glyph.Text = showName
                ? (text.LineCount > 1
                    ? multilineLinkedPathButtonText
                    : linkedPathButtonText)
                : "\uE71B";
            glyph.FontFamily = showName
                ? AppTypography.UiFontFamily
                : new FontFamily("Segoe MDL2 Assets");
            glyph.FontSize = showName
                ? metrics.LinkedPaperNameFontSize
                : metrics.LinkedPaperIconFontSize;
            glyph.Foreground = hovered ? TextBrush : WeakTextBrush;
            glyph.Opacity = hovered ? 1.0 : 0.72;
            button.Background = hovered
                ? LinkedPaperLightBgBrush
                : LinkedPaperNormalBgBrush;
            button.ToolTip = Strings.Format("ToolTipOpenLinkedPath", path);
        }

        var linkedPathNameLayoutQueued = false;
        void QueueLinkedPathNameLayoutUpdate()
        {
            if (!showName || linkedPathNameLayoutQueued)
            {
                return;
            }

            linkedPathNameLayoutQueued = true;
            Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    linkedPathNameLayoutQueued = false;
                    RefreshPresentation(hovered: button.IsMouseOver);
                    glyph.TextWrapping = text.LineCount > 1
                        ? TextWrapping.Wrap
                        : TextWrapping.NoWrap;
                    glyph.MaxWidth = Math.Max(1, width - 6);
                }),
                System.Windows.Threading.DispatcherPriority.Render);
        }

        if (showName)
        {
            text.SizeChanged += (_, _) => QueueLinkedPathNameLayoutUpdate();
            text.TextChanged += (_, _) => QueueLinkedPathNameLayoutUpdate();
        }

        RefreshPresentation(hovered: false);
        QueueLinkedPathNameLayoutUpdate();
        button.MouseEnter += (_, _) => RefreshPresentation(hovered: true);
        button.MouseLeave += (_, _) =>
        {
            RefreshPresentation(hovered: false);
            button.Opacity = 1.0;
        };
        button.MouseLeftButtonDown += (_, e) =>
        {
            button.Opacity = 0.72;
            e.Handled = true;
        };
        button.MouseLeftButtonUp += (_, e) =>
        {
            button.Opacity = 1.0;
            OpenTodoLinkedPath(item);
            RefreshPresentation(hovered: button.IsMouseOver);
            e.Handled = true;
        };
        return button;
    }

    private string TodoLinkedPathLabel(
        PaperItem item,
        string path,
        string fileName,
        bool allowLongName,
        bool isTodoMultiline)
    {
        if (allowLongName)
        {
            var limit = isTodoMultiline ? 20 : 10;
            return CompactLinkedPaperTitleByDisplayWidth(
                fileName,
                limit,
                limit);
        }

        if (_controller.State.ShowLinkedPathExtensionOnly &&
            item.LinkedPathIsDirectory == false)
        {
            try
            {
                var extension = Path.GetExtension(fileName);
                if (!string.IsNullOrWhiteSpace(extension))
                {
                    return extension;
                }
            }
            catch
            {
            }
        }

        return isTodoMultiline
            ? CompactLinkedPaperTitle(fileName, 6, 5)
            : CompactLinkedPaperTitle(fileName, 3, 3);
    }

    private static string PathDisplayName(string path)
    {
        try
        {
            var trimmed = path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        catch
        {
            return path;
        }
    }

    private void OpenTodoLinkedPath(PaperItem item)
    {
        var path = item.LinkedPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            MessageBox.Show(
                this,
                Strings.Format("LinkedPathMissingMessage", path),
                Strings.Get("LinkedPathOpenFailureTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            ReconcileTodoRows(
                [item.Id],
                CurrentFocusedTodoItemId() ?? item.Id);
            return;
        }

        OpenShellPath(path);
    }

    private void OpenTodoLinkedPathLocation(PaperItem item)
    {
        var path = item.LinkedPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string? location;
        try
        {
            location = Directory.Exists(path)
                ? Directory.GetParent(path)?.FullName ?? path
                : Path.GetDirectoryName(path);
        }
        catch
        {
            location = null;
        }

        if (string.IsNullOrWhiteSpace(location) || !Directory.Exists(location))
        {
            MessageBox.Show(
                this,
                Strings.Format("LinkedPathMissingMessage", path),
                Strings.Get("LinkedPathOpenFailureTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        OpenShellPath(location);
    }

    private void OpenShellPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                Strings.Format("LinkedPathOpenFailureMessage", ex.Message),
                Strings.Get("LinkedPathOpenFailureTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
