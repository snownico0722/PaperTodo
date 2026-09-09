using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private const double FindPopupBaseWidth = 276;

    private readonly record struct PaperFindMatch(string? TodoItemId, int Offset, int Length)
    {
        public bool IsNote => TodoItemId == null;
    }

    private Popup? _findPopup;
    private Border? _findHost;
    private TextBox? _findInput;
    private TextBlock? _findCountText;
    private Button? _findPreviousButton;
    private Button? _findNextButton;
    private readonly List<PaperFindMatch> _findMatches = [];
    private int _findMatchIndex = -1;
    private PaperFindMatch? _findAppliedMatch;
    private TodoTextBox? _findInactiveSelectionEditor;
    private bool _findInactiveSelectionOriginalValue;
    private bool _findSuppressInputTextChanged;
    private bool _findOwnerRefreshQueued;
    private bool _findBodyRefreshQueued;
    private double _findObservedTypographyScale = -1;
    private MarkdownTextBox? _findObservedNoteBox;
    private readonly List<TodoTextBox> _findObservedTodoEditors = [];
    private int _findObservedTodoRowsGeneration = -1;
    private EventHandler? _findNoteTextChangedHandler;
    private TextChangedEventHandler? _findTodoTextChangedHandler;

    private bool IsBuiltInFindOpen => _findPopup?.IsOpen == true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Built-in find is a fallback. Native/plugin content can consume Ctrl+F before this
        // bubbling Window handler and the host then leaves the gesture alone.
        if (e.Handled ||
            _advancedInteractionLocked ||
            _paper.IsCollapsed ||
            e.Key != Key.F ||
            Keyboard.Modifiers != ModifierKeys.Control ||
            !CanUseBuiltInFind())
        {
            return;
        }

        e.Handled = true;
        ShowBuiltInFind();
    }

    private bool CanUseBuiltInFind()
    {
        if (!_isShellBuilt)
        {
            return false;
        }

        return _paper.Type == PaperTypes.Todo ||
               (_paper.Type == PaperTypes.Note &&
                IsCurrentBodyProviderMarkdown &&
                _noteBox != null);
    }

    private void ShowBuiltInFind()
    {
        EnsureBuiltInFindPopup();
        if (_findPopup == null || _findInput == null)
        {
            return;
        }

        PaperFindMatch? preferredMatch = null;
        if (TryGetCurrentFindSeed(out var seed, out var seedMatch) &&
            seed.Length <= 200 &&
            seed.IndexOfAny(['\r', '\n']) < 0)
        {
            preferredMatch = seedMatch;
            SetBuiltInFindQuery(seed, preferredMatch);
        }
        else
        {
            RebuildFindMatches(preserveCurrent: true);
        }

        UpdateBuiltInFindVisuals();
        _findPopup.IsOpen = true;
        SynchronizeBuiltInFindOwnerState(refreshMatches: false);
        _findInput.Focus();
        _findInput.SelectAll();

        // Focusing the popup can make Markdown enter its normal preview state, which clears the
        // editor selection. Re-apply the selected match after that focus transition settles.
        _ = Dispatcher.BeginInvoke((Action)(() =>
        {
            if (!IsBuiltInFindOpen)
            {
                return;
            }
            ApplyCurrentFindMatch();
            RepositionBuiltInFindPopup();
        }), DispatcherPriority.Background);
    }

    private bool TryGetCurrentFindSeed(
        out string seed,
        out PaperFindMatch match)
    {
        if (_paper.Type == PaperTypes.Note &&
            _noteBox is { IsKeyboardFocusWithin: true, SelectionLength: > 0 } note)
        {
            seed = note.SelectedText ?? string.Empty;
            match = new PaperFindMatch(null, note.SelectionStart, note.SelectionLength);
            return seed.Length > 0;
        }

        if (_paper.Type == PaperTypes.Todo &&
            Keyboard.FocusedElement is TodoTextBox { SelectionLength: > 0 } todo)
        {
            foreach (var pair in _todoEditors)
            {
                if (!ReferenceEquals(pair.Value, todo))
                {
                    continue;
                }

                seed = todo.SelectedText;
                match = new PaperFindMatch(pair.Key, todo.SelectionStart, todo.SelectionLength);
                return seed.Length > 0;
            }
        }

        seed = string.Empty;
        match = default;
        return false;
    }

    private void SetBuiltInFindQuery(
        string query,
        PaperFindMatch? preferredMatch = null)
    {
        if (_findInput == null)
        {
            return;
        }

        if (string.Equals(_findInput.Text, query, StringComparison.Ordinal))
        {
            RebuildFindMatches(
                preserveCurrent: preferredMatch == null,
                preferredMatch: preferredMatch);
            return;
        }

        _findSuppressInputTextChanged = true;
        try
        {
            _findInput.Text = query;
        }
        finally
        {
            _findSuppressInputTextChanged = false;
        }

        RebuildFindMatches(
            preserveCurrent: false,
            preferredMatch: preferredMatch);
    }

    private void EnsureBuiltInFindPopup()
    {
        if (_findPopup != null || !_isShellBuilt)
        {
            return;
        }

        var host = new Border
        {
            Padding = new Thickness(4),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(RadiusControl),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Effect = CreatePaperChromeShadow(blurRadius: 10, opacity: 0.16, shadowDepth: 2)
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
            MinWidth = 72
        });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var input = new TextBox
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 2, 5, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
            FocusVisualStyle = null,
            ToolTip = "Ctrl+F"
        };
        input.TextChanged += (_, _) =>
        {
            if (!_findSuppressInputTextChanged)
            {
                RebuildFindMatches(preserveCurrent: false);
            }
        };
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                HideBuiltInFind(restoreFocus: true);
                return;
            }

            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                input.SelectAll();
                return;
            }

            if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                HideBuiltInFind(restoreFocus: false);
                CloseExpandedPaperFromWindowGesture();
                return;
            }

            if (e.Key != Key.Enter)
            {
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                MoveFindMatch(1);
            }
            else if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                e.Handled = true;
                MoveFindMatch(-1);
            }
        };
        Grid.SetColumn(input, 0);
        row.Children.Add(input);

        var count = new TextBlock
        {
            Text = "0 / 0",
            MinWidth = 42,
            Margin = new Thickness(5, 0, 3, 0),
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(count, 1);
        row.Children.Add(count);

        var previous = FindIconButton("↑", "Shift+Enter");
        previous.Click += (_, _) => MoveFindMatch(-1);
        Grid.SetColumn(previous, 2);
        row.Children.Add(previous);

        var next = FindIconButton("↓", "Enter");
        next.Click += (_, _) => MoveFindMatch(1);
        Grid.SetColumn(next, 3);
        row.Children.Add(next);

        var close = FindIconButton("×", "Esc");
        close.Click += (_, _) => HideBuiltInFind(restoreFocus: true);
        Grid.SetColumn(close, 4);
        row.Children.Add(close);

        host.Child = row;

        var popup = new Popup
        {
            PlacementTarget = _paperChrome,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = PlaceBuiltInFindPopup,
            AllowsTransparency = true,
            StaysOpen = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = host
        };
        popup.Opened += (_, _) =>
        {
            HookBuiltInFindContentChanges();
            CancelExperimentalAutoCollapse();
            RefreshExperimentalOpacity();
        };
        popup.Closed += (_, _) =>
        {
            UnhookBuiltInFindContentChanges();
            ReleaseTodoInactiveFindSelection();
            RefreshExperimentalOpacity();
        };

        _findPopup = popup;
        _findHost = host;
        _findInput = input;
        _findCountText = count;
        _findPreviousButton = previous;
        _findNextButton = next;

        LocationChanged += (_, _) => RepositionBuiltInFindPopup();
        SizeChanged += (_, _) =>
        {
            if (IsBuiltInFindOpen && _paper.IsCollapsed)
            {
                HideBuiltInFind(restoreFocus: false);
                return;
            }
            RepositionBuiltInFindPopup();
        };
        StateChanged += (_, _) =>
        {
            if (IsBuiltInFindOpen && WindowState == WindowState.Minimized)
            {
                HideBuiltInFind(restoreFocus: false);
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsBuiltInFindOpen && !IsVisible)
            {
                HideBuiltInFind(restoreFocus: false);
            }
        };
        Deactivated += OnBuiltInFindOwnerDeactivated;
        Closed += (_, _) => HideBuiltInFind(restoreFocus: false);
        LayoutUpdated += OnBuiltInFindOwnerLayoutUpdated;

        UpdateBuiltInFindVisuals();
        UpdateFindCount();
    }

    private static Button FindIconButton(string glyph, string tooltip)
    {
        var button = IconButton(glyph, tooltip);
        button.Padding = new Thickness(0);
        button.Margin = new Thickness(1, 0, 0, 0);
        button.Focusable = false;
        return button;
    }

    private CustomPopupPlacement[] PlaceBuiltInFindPopup(
        Size popupSize,
        Size targetSize,
        Point offset)
    {
        var y = TitleBarHeight + 4;
        var inside = new CustomPopupPlacement(
            new Point(Math.Max(6, targetSize.Width - popupSize.Width - 6), y),
            PopupPrimaryAxis.Horizontal);
        var right = new CustomPopupPlacement(
            new Point(targetSize.Width + 6, y),
            PopupPrimaryAxis.Horizontal);
        var left = new CustomPopupPlacement(
            new Point(-popupSize.Width - 6, y),
            PopupPrimaryAxis.Horizontal);

        return targetSize.Width >= popupSize.Width + 12
            ? [inside, right, left]
            : [right, left, inside];
    }

    private void UpdateBuiltInFindVisuals()
    {
        if (_findHost == null ||
            _findInput == null ||
            _findCountText == null)
        {
            return;
        }

        _findHost.Resources["PaperBrushKey"] = PaperBrush;
        _findHost.Resources["PaperBorderBrushKey"] = PaperBorderBrush;
        _findHost.Resources["TextBrushKey"] = TextBrush;
        _findHost.Resources["WeakTextBrushKey"] = WeakTextBrush;
        _findHost.Resources["HoverBrushKey"] = HoverBrush;

        _findHost.Background = PaperBrush;
        _findHost.BorderBrush = PaperBorderBrush;
        _findHost.Width = Math.Round(
            Math.Clamp(
                FindPopupBaseWidth * AppTypography.ScaleFactor,
                236,
                420));
        _findHost.Padding = new Thickness(AppTypography.Scale(4));

        _findInput.Foreground = TextBrush;
        _findInput.CaretBrush = TextBrush;
        _findInput.Background = PaperBrush;
        _findInput.BorderBrush = PaperBorderBrush;
        _findInput.FontFamily = AppTypography.FontFamilyFor(content: true, bold: false);
        _findInput.FontSize = AppTypography.Scale(11.5);
        _findInput.MinHeight = AppTypography.FitChrome(23);
        _findInput.Padding = new Thickness(
            AppTypography.Scale(5),
            AppTypography.Scale(2),
            AppTypography.Scale(5),
            AppTypography.Scale(2));

        _findCountText.Foreground = WeakTextBrush;
        _findCountText.FontFamily = AppTypography.FontFamilyFor(content: false, bold: false);
        _findCountText.FontSize = AppTypography.Scale(10.5);
        _findCountText.MinWidth = AppTypography.FitChrome(42);
        _findCountText.Margin = new Thickness(
            AppTypography.Scale(5),
            0,
            AppTypography.Scale(3),
            0);

        UpdateFindButtonMetrics(_findPreviousButton);
        UpdateFindButtonMetrics(_findNextButton);
        if (_findHost.Child is Grid grid && grid.Children.OfType<Button>().LastOrDefault() is { } close)
        {
            UpdateFindButtonMetrics(close);
        }

        _findObservedTypographyScale = AppTypography.ScaleFactor;
    }

    private static void UpdateFindButtonMetrics(Button? button)
    {
        if (button == null)
        {
            return;
        }

        var size = AppTypography.FitChrome(23);
        button.Width = size;
        button.Height = size;
        button.MinWidth = size;
        button.MinHeight = size;
        button.FontSize = AppTypography.Scale(11.5);
    }

    private bool BuiltInFindVisualsNeedRefresh()
    {
        if (_findHost == null)
        {
            return false;
        }

        return Math.Abs(_findObservedTypographyScale - AppTypography.ScaleFactor) > 0.001 ||
               !_findHost.Resources.Contains("PaperBrushKey") ||
               !ReferenceEquals(_findHost.Resources["PaperBrushKey"], PaperBrush) ||
               !ReferenceEquals(_findHost.Resources["PaperBorderBrushKey"], PaperBorderBrush) ||
               !ReferenceEquals(_findHost.Resources["TextBrushKey"], TextBrush) ||
               !ReferenceEquals(_findHost.Resources["WeakTextBrushKey"], WeakTextBrush);
    }

    private void RepositionBuiltInFindPopup()
    {
        if (!IsBuiltInFindOpen || _findPopup == null)
        {
            return;
        }

        // WPF Popup does not consistently re-run custom placement while its target HWND moves.
        // A no-op offset invalidation is enough to force placement without closing/reopening it.
        var offset = _findPopup.HorizontalOffset;
        _findPopup.HorizontalOffset = offset + 0.01;
        _findPopup.HorizontalOffset = offset;
    }

    private void OnBuiltInFindOwnerLayoutUpdated(object? sender, EventArgs e)
    {
        if (!IsBuiltInFindOpen || _findOwnerRefreshQueued)
        {
            return;
        }

        _findOwnerRefreshQueued = true;
        _ = Dispatcher.BeginInvoke((Action)(() =>
        {
            _findOwnerRefreshQueued = false;
            if (!IsBuiltInFindOpen)
            {
                return;
            }
            SynchronizeBuiltInFindOwnerState(refreshMatches: true);
        }), DispatcherPriority.Background);
    }

    private void SynchronizeBuiltInFindOwnerState(bool refreshMatches)
    {
        if (!IsBuiltInFindOpen)
        {
            return;
        }

        if (!IsVisible ||
            WindowState == WindowState.Minimized ||
            _paper.IsCollapsed ||
            !CanUseBuiltInFind())
        {
            HideBuiltInFind(restoreFocus: false);
            return;
        }

        var visualsChanged = BuiltInFindVisualsNeedRefresh();
        if (visualsChanged)
        {
            UpdateBuiltInFindVisuals();
            RepositionBuiltInFindPopup();
        }

        var bodyChanged =
            (_paper.Type == PaperTypes.Note &&
             !ReferenceEquals(_findObservedNoteBox, _noteBox)) ||
            (_paper.Type == PaperTypes.Todo &&
             _findObservedTodoRowsGeneration != _todoRowsGeneration);
        if (!bodyChanged)
        {
            return;
        }

        HookBuiltInFindContentChanges();
        if (refreshMatches)
        {
            RebuildFindMatches(
                preserveCurrent: true,
                applySelection: !IsBuiltInFindBodyKeyboardFocusWithin());
        }
    }

    private void OnBuiltInFindOwnerDeactivated(object? sender, EventArgs e)
    {
        if (!IsBuiltInFindOpen)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke((Action)(() =>
        {
            if (!IsBuiltInFindOpen ||
                _findHost?.IsKeyboardFocusWithin == true)
            {
                return;
            }

            HideBuiltInFind(restoreFocus: false);
            if (!IsActive)
            {
                ScheduleExperimentalAutoCollapse(blockedAtDeactivation: false);
            }
        }), DispatcherPriority.ContextIdle);
    }

    private void HookBuiltInFindContentChanges()
    {
        UnhookBuiltInFindContentChanges();

        _findNoteTextChangedHandler ??= (_, _) => QueueBuiltInFindBodyRefresh();
        _findTodoTextChangedHandler ??= (_, _) => QueueBuiltInFindBodyRefresh();

        if (_paper.Type == PaperTypes.Note && _noteBox != null)
        {
            _findObservedNoteBox = _noteBox;
            _findObservedNoteBox.TextChanged += _findNoteTextChangedHandler;
            _findObservedTodoRowsGeneration = -1;
            return;
        }

        _findObservedNoteBox = null;
        if (_paper.Type != PaperTypes.Todo)
        {
            _findObservedTodoRowsGeneration = -1;
            return;
        }

        _findObservedTodoRowsGeneration = _todoRowsGeneration;
        foreach (var editor in _todoEditors.Values.Distinct())
        {
            _findObservedTodoEditors.Add(editor);
            editor.TextChanged += _findTodoTextChangedHandler;
        }
    }

    private void UnhookBuiltInFindContentChanges()
    {
        if (_findObservedNoteBox != null && _findNoteTextChangedHandler != null)
        {
            _findObservedNoteBox.TextChanged -= _findNoteTextChangedHandler;
        }
        _findObservedNoteBox = null;

        if (_findTodoTextChangedHandler != null)
        {
            foreach (var editor in _findObservedTodoEditors)
            {
                editor.TextChanged -= _findTodoTextChangedHandler;
            }
        }
        _findObservedTodoEditors.Clear();
        _findObservedTodoRowsGeneration = -1;
    }

    private void QueueBuiltInFindBodyRefresh()
    {
        if (!IsBuiltInFindOpen || _findBodyRefreshQueued)
        {
            return;
        }

        _findBodyRefreshQueued = true;
        _ = Dispatcher.BeginInvoke((Action)(() =>
        {
            _findBodyRefreshQueued = false;
            if (!IsBuiltInFindOpen)
            {
                return;
            }

            RebuildFindMatches(
                preserveCurrent: true,
                applySelection: !IsBuiltInFindBodyKeyboardFocusWithin());
        }), DispatcherPriority.Background);
    }

    private bool IsBuiltInFindBodyKeyboardFocusWithin()
    {
        if (_paper.Type == PaperTypes.Note)
        {
            return _noteBox?.IsKeyboardFocusWithin == true;
        }

        return Keyboard.FocusedElement is TodoTextBox todo &&
               _todoEditors.Values.Any(editor => ReferenceEquals(editor, todo));
    }

    private void RebuildFindMatches(
        bool preserveCurrent,
        PaperFindMatch? preferredMatch = null,
        bool applySelection = true)
    {
        if (_findInput == null)
        {
            return;
        }

        var previous = preserveCurrent && TryGetCurrentFindMatch(out var current)
            ? current
            : (PaperFindMatch?)null;

        var query = _findInput.Text ?? string.Empty;
        _findMatches.Clear();
        _findMatches.AddRange(ScanFindMatches(query));

        if (_findMatches.Count == 0)
        {
            _findMatchIndex = -1;
            ClearAppliedFindSelection();
            SynchronizeGlobalNoteFindState(query);
            UpdateFindCount();
            return;
        }

        var preferredIndex = preferredMatch is PaperFindMatch preferred
            ? _findMatches.IndexOf(preferred)
            : -1;
        var previousIndex = previous is PaperFindMatch kept
            ? _findMatches.IndexOf(kept)
            : -1;
        _findMatchIndex = preferredIndex >= 0
            ? preferredIndex
            : previousIndex >= 0
                ? previousIndex
                : 0;

        if (applySelection)
        {
            ApplyCurrentFindMatch();
        }
        else
        {
            _findAppliedMatch = null;
            ReleaseTodoInactiveFindSelection();
        }
        SynchronizeGlobalNoteFindState(query);
        UpdateFindCount();
    }

    private List<PaperFindMatch> ScanFindMatches(string query)
    {
        var matches = new List<PaperFindMatch>();
        if (string.IsNullOrEmpty(query))
        {
            return matches;
        }

        if (_paper.Type == PaperTypes.Note && _noteBox != null)
        {
            AddFindMatches(matches, null, _noteBox.Text ?? string.Empty, query);
            return matches;
        }

        if (_paper.Type != PaperTypes.Todo)
        {
            return matches;
        }

        foreach (var item in OrderedItems())
        {
            if (_todoEditors.TryGetValue(item.Id, out var editor))
            {
                AddFindMatches(matches, item.Id, editor.Text ?? string.Empty, query);
            }
        }
        return matches;
    }

    private static void AddFindMatches(
        List<PaperFindMatch> destination,
        string? todoItemId,
        string text,
        string query)
    {
        var searchFrom = 0;
        while (searchFrom <= text.Length - query.Length)
        {
            var offset = text.IndexOf(
                query,
                searchFrom,
                StringComparison.OrdinalIgnoreCase);
            if (offset < 0)
            {
                break;
            }

            destination.Add(new PaperFindMatch(todoItemId, offset, query.Length));
            searchFrom = offset + query.Length;
        }
    }

    private void MoveFindMatch(int direction)
    {
        if (_findInput == null || string.IsNullOrEmpty(_findInput.Text))
        {
            return;
        }

        if (TryMoveGlobalNoteFindMatch(direction))
        {
            return;
        }

        var previous = TryGetCurrentFindMatch(out var current)
            ? current
            : (PaperFindMatch?)null;

        _findMatches.Clear();
        _findMatches.AddRange(ScanFindMatches(_findInput.Text));
        if (_findMatches.Count == 0)
        {
            _findMatchIndex = -1;
            ClearAppliedFindSelection();
            UpdateFindCount();
            return;
        }

        var currentIndex = previous is PaperFindMatch kept
            ? _findMatches.IndexOf(kept)
            : -1;
        if (currentIndex < 0)
        {
            _findMatchIndex = direction >= 0 ? 0 : _findMatches.Count - 1;
        }
        else
        {
            _findMatchIndex =
                (currentIndex + direction + _findMatches.Count) % _findMatches.Count;
        }

        ApplyCurrentFindMatch();
        UpdateFindCount();
    }

    private bool TryGetCurrentFindMatch(out PaperFindMatch match)
    {
        if (_findMatchIndex >= 0 && _findMatchIndex < _findMatches.Count)
        {
            match = _findMatches[_findMatchIndex];
            return true;
        }

        match = default;
        return false;
    }

    private void ApplyCurrentFindMatch()
    {
        if (!TryGetCurrentFindMatch(out var match))
        {
            ClearAppliedFindSelection();
            return;
        }

        if (_findAppliedMatch is PaperFindMatch previous && previous != match)
        {
            ClearFindSelection(previous);
        }

        if (match.IsNote)
        {
            ReleaseTodoInactiveFindSelection();
            if (_noteBox == null ||
                match.Offset < 0 ||
                match.Offset + match.Length > _noteBox.Text.Length)
            {
                _findAppliedMatch = null;
                return;
            }

            _noteBox.Select(match.Offset, match.Length);
            if (_noteBox.Document.TextLength > 0)
            {
                var lineOffset = Math.Min(match.Offset, _noteBox.Document.TextLength - 1);
                _noteBox.ScrollToLine(_noteBox.Document.GetLineByOffset(lineOffset).LineNumber);
            }
            _findAppliedMatch = match;
            return;
        }

        if (match.TodoItemId == null ||
            !_todoEditors.TryGetValue(match.TodoItemId, out var editor) ||
            match.Offset < 0 ||
            match.Offset + match.Length > editor.Text.Length)
        {
            _findAppliedMatch = null;
            return;
        }

        EnableTodoInactiveFindSelection(editor);
        editor.Select(match.Offset, match.Length);
        _todoRows.FirstOrDefault(row =>
            string.Equals(row.Tag as string, match.TodoItemId, StringComparison.Ordinal))
            ?.BringIntoView();
        _findAppliedMatch = match;
    }

    private void ClearAppliedFindSelection()
    {
        if (_findAppliedMatch is PaperFindMatch applied)
        {
            ClearFindSelection(applied);
        }
        _findAppliedMatch = null;
    }

    private void ClearFindSelection(PaperFindMatch match)
    {
        if (match.IsNote)
        {
            if (_noteBox != null &&
                _noteBox.SelectionStart == match.Offset &&
                _noteBox.SelectionLength == match.Length)
            {
                _noteBox.SelectionLength = 0;
            }
            return;
        }

        if (match.TodoItemId != null &&
            _todoEditors.TryGetValue(match.TodoItemId, out var editor) &&
            editor.SelectionStart == match.Offset &&
            editor.SelectionLength == match.Length)
        {
            editor.SelectionLength = 0;
        }
        ReleaseTodoInactiveFindSelection();
    }

    private void EnableTodoInactiveFindSelection(TodoTextBox editor)
    {
        if (ReferenceEquals(_findInactiveSelectionEditor, editor))
        {
            return;
        }

        ReleaseTodoInactiveFindSelection();
        _findInactiveSelectionEditor = editor;
        _findInactiveSelectionOriginalValue = editor.IsInactiveSelectionHighlightEnabled;
        editor.IsInactiveSelectionHighlightEnabled = true;
    }

    private void ReleaseTodoInactiveFindSelection()
    {
        if (_findInactiveSelectionEditor == null)
        {
            return;
        }

        _findInactiveSelectionEditor.IsInactiveSelectionHighlightEnabled =
            _findInactiveSelectionOriginalValue;
        _findInactiveSelectionEditor = null;
    }

    private void UpdateFindCount()
    {
        if (_findCountText == null)
        {
            return;
        }

        var current = _findMatchIndex >= 0 && _findMatches.Count > 0
            ? _findMatchIndex + 1
            : 0;
        _findCountText.Text = BuiltInFindCountText(current, _findMatches.Count);

        var enabled = HasBuiltInFindNavigationTarget();
        if (_findPreviousButton != null)
        {
            _findPreviousButton.IsEnabled = enabled;
        }
        if (_findNextButton != null)
        {
            _findNextButton.IsEnabled = enabled;
        }
    }

    private bool TryHandleBuiltInFindPreviewKeyDown(KeyEventArgs e)
    {
        if (!IsBuiltInFindOpen ||
            e.Key != Key.Escape ||
            Keyboard.Modifiers != ModifierKeys.None)
        {
            return false;
        }

        HideBuiltInFind(restoreFocus: true);
        e.Handled = true;
        return true;
    }

    private void HideBuiltInFind(bool restoreFocus)
    {
        if (_findPopup == null || !_findPopup.IsOpen)
        {
            return;
        }

        var hasMatch = TryGetCurrentFindMatch(out var match);
        _findPopup.IsOpen = false;

        if (!restoreFocus ||
            !IsVisible ||
            _paper.IsCollapsed ||
            WindowState == WindowState.Minimized)
        {
            return;
        }

        if (!IsActive)
        {
            Activate();
        }

        if (hasMatch && match.IsNote)
        {
            if (_noteBox is { IsPreviewMode: false } note)
            {
                note.Focus();
            }
            else
            {
                Focus();
            }
            return;
        }

        if (hasMatch &&
            match.TodoItemId != null &&
            _todoEditors.TryGetValue(match.TodoItemId, out var editor))
        {
            editor.Focus();
            return;
        }

        Focus();
    }
}
