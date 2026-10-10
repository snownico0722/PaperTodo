using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private readonly TodoArrowNavigation _todoArrowNavigation = new();
    private readonly HashSet<string> _foldedTodoItemIds = new(StringComparer.Ordinal);
    private static readonly Geometry TodoFoldUp = Geometry.Parse("M 2,8 L 6,4 L 10,8");
    private static readonly Geometry TodoFoldDown = Geometry.Parse("M 2,4 L 6,8 L 10,4");

    private const int TodoFoldThreshold = 4;
    private const int TodoFoldVisibleLines = 2;
    internal const int TodoTextMaxLength = 5000;
    internal const int MaxPastedTodoLines = 200;

    private UIElement BuildTodoBody()
    {
        if (_paper.Items.Count == 0)
        {
            _paper.Items.Add(new PaperItem { Order = 0 });
        }

        _todoPanel = new StackPanel
        {
            Margin = new Thickness(6.4, 3.2, 5.6, 3.2)
        };

        EnsureTodoSelectionInputHooks();
        RebuildTodoRows();

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _todoPanel,
            FocusVisualStyle = null
        };
        AttachTodoBackgroundHost(scrollViewer);
        return scrollViewer;
    }


    public void RefreshTodoRowsForExternalChange()
    {
        if (_paper.Type != PaperTypes.Todo)
        {
            return;
        }

        RebuildTodoRows(CurrentFocusedTodoItemId());
    }

    public void UpdateTodoVisualSize()
    {
        if (_paper.Type != PaperTypes.Todo)
        {
            return;
        }

        RebuildTodoRows(CurrentFocusedTodoItemId());
    }

    private void RebuildTodoRows(string? focusItemId = null, TodoFocusPlacement focusPlacement = TodoFocusPlacement.End)
    {
        InvalidateEdgeCapsulePreviewContent();
        if (_todoPanel == null)
        {
            return;
        }

        _todoRowsGeneration++;
        var targetFocus = focusItemId ?? _pendingFocusItemId;
        _pendingFocusItemId = null;

        NormalizeTodoItems();
        NormalizeOrders();
        PruneTodoSelection();
        _foldedTodoItemIds.IntersectWith(_paper.Items.Select(item => item.Id));

        // 记录现有行的ID，用于判断哪些是新增的
        var existingIds = new HashSet<string>(_todoRows.Select(r => (string)r.Tag));

        _todoPanel.Children.Clear();
        ResetCompletedTodoSectionHeader();
        _todoFoldFindControls.Clear();
        _todoEditors.Clear();
        _todoReminderCountdowns.Clear();
        _todoRows.Clear();
        _linkedPaperTitleRefreshers.Clear();
        _linkedPaperDropRow = null;

        foreach (var item in OrderedItems())
        {
            var row = BuildTodoRow(item, isNewItem: !existingIds.Contains(item.Id));
            _todoRows.Add(row);
            _todoPanel.Children.Add(row);
        }

        SyncCompletedTodoSection();
        SyncTodoAppendArea();
        RefreshTodoFindCues();

        if (!string.IsNullOrWhiteSpace(targetFocus))
        {
            FocusTodoItem(targetFocus, focusPlacement);
        }
    }

    private void ReconcileTodoRows(
        IEnumerable<string>? rebuildItemIds = null,
        string? focusItemId = null,
        TodoFocusPlacement focusPlacement = TodoFocusPlacement.End)
    {
        InvalidateEdgeCapsulePreviewContent();
        if (_todoPanel == null)
        {
            return;
        }

        _todoRowsGeneration++;
        var targetFocus = focusItemId ?? _pendingFocusItemId;
        _pendingFocusItemId = null;
        NormalizeTodoItems();
        NormalizeOrders();
        PruneTodoSelection();
        _foldedTodoItemIds.IntersectWith(_paper.Items.Select(item => item.Id));
        RemoveCompletedTodoSectionHeaderFromPanel();

        var rebuildIds = rebuildItemIds?.ToHashSet(StringComparer.Ordinal) ?? [];
        var orderedItems = OrderedItems().ToList();
        var itemIds = orderedItems
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var existingIds = _todoRows
            .Select(row => row.Tag as string)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        foreach (var row in _todoRows.ToList())
        {
            if (row.Tag is not string itemId ||
                !itemIds.Contains(itemId) ||
                rebuildIds.Contains(itemId))
            {
                RemoveTodoRowRegistration(row);
                _todoPanel.Children.Remove(row);
                _todoRows.Remove(row);
            }
        }

        var rowsById = _todoRows
            .Where(row => row.Tag is string)
            .ToDictionary(row => (string)row.Tag, StringComparer.Ordinal);
        var orderedRows = new List<Border>(orderedItems.Count);
        foreach (var item in orderedItems)
        {
            if (!rowsById.TryGetValue(item.Id, out var row))
            {
                row = BuildTodoRow(
                    item,
                    isNewItem: !existingIds.Contains(item.Id));
            }
            orderedRows.Add(row);
        }

        // Most visibility/content refreshes keep the same visual row order. Avoid allocating
        // the LIS index, predecessor arrays and retained set when nothing needs moving.
        if (!TodoRowsAreInPlace(orderedRows, _todoPanel.Children))
        {
            // Keep the largest set of rows whose relative order is already correct.
            // Moving the first item to the end should detach one row, not every other row.
            var retainedRows = TodoRowsToKeepInPlace(orderedRows, _todoPanel.Children);
            UIElement? nextRow = _appendArea != null &&
                _todoPanel.Children.Count > 0 &&
                ReferenceEquals(_todoPanel.Children[_todoPanel.Children.Count - 1], _appendArea)
                    ? _appendArea
                    : null;
            for (var index = orderedRows.Count - 1; index >= 0; index--)
            {
                var row = orderedRows[index];
                if (!retainedRows.Contains(row))
                {
                    var nextIndex = nextRow != null
                        ? _todoPanel.Children.IndexOf(nextRow)
                        : _todoPanel.Children.Count;
                    var currentIndex = _todoPanel.Children.IndexOf(row);
                    if (currentIndex < 0 || currentIndex != nextIndex - 1)
                    {
                        if (currentIndex >= 0)
                        {
                            _todoPanel.Children.RemoveAt(currentIndex);
                            if (currentIndex < nextIndex)
                            {
                                nextIndex--;
                            }
                        }
                        _todoPanel.Children.Insert(nextIndex, row);
                    }
                }
                nextRow = row;
            }
        }

        _todoRows.Clear();
        _todoRows.AddRange(orderedRows);
        SyncCompletedTodoSection();
        SyncTodoAppendArea();
        RefreshTodoFindCues();

        if (!string.IsNullOrWhiteSpace(targetFocus))
        {
            FocusTodoItem(targetFocus, focusPlacement);
        }
    }

    private static bool TodoRowsAreInPlace(
        IReadOnlyList<Border> orderedRows,
        UIElementCollection children)
    {
        if (children.Count < orderedRows.Count) return false;
        for (var index = 0; index < orderedRows.Count; index++)
        {
            if (!ReferenceEquals(children[index], orderedRows[index])) return false;
        }
        return true;
    }

    private static HashSet<Border> TodoRowsToKeepInPlace(
        IReadOnlyList<Border> orderedRows,
        UIElementCollection children)
    {
        var currentIndices = new Dictionary<UIElement, int>();
        for (var index = 0; index < children.Count; index++)
        {
            currentIndices.Add(children[index], index);
        }

        // Find a longest increasing subsequence of the rows' current visual indices.
        // New and rebuilt rows have no visual index and must be inserted separately.
        var tails = new int[orderedRows.Count];
        var predecessors = new int[orderedRows.Count];
        Array.Fill(predecessors, -1);
        var length = 0;
        for (var index = 0; index < orderedRows.Count; index++)
        {
            if (!currentIndices.TryGetValue(orderedRows[index], out var currentIndex))
            {
                continue;
            }
            var low = 0;
            var high = length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (currentIndices[orderedRows[tails[middle]]] < currentIndex)
                    low = middle + 1;
                else
                    high = middle;
            }
            if (low > 0) predecessors[index] = tails[low - 1];
            tails[low] = index;
            if (low == length) length++;
        }

        var retainedRows = new HashSet<Border>();
        for (var index = length == 0 ? -1 : tails[length - 1]; index >= 0; index = predecessors[index])
        {
            retainedRows.Add(orderedRows[index]);
        }
        return retainedRows;
    }

    private void RemoveTodoRowRegistration(Border row)
    {
        if (row.Tag is not string itemId)
        {
            return;
        }

        _todoEditors.Remove(itemId);
        _todoFoldFindControls.Remove(itemId);
        _todoReminderCountdowns.Remove(itemId);
        foreach (var paperId in _linkedPaperTitleRefreshers.Keys.ToList())
        {
            var refreshers = _linkedPaperTitleRefreshers[paperId];
            refreshers.Remove(itemId);
            if (refreshers.Count == 0)
            {
                _linkedPaperTitleRefreshers.Remove(paperId);
            }
        }
        if (ReferenceEquals(_linkedPaperDropRow, row))
        {
            _linkedPaperDropRow = null;
        }
        if (ReferenceEquals(_activeDropRow, row))
        {
            _activeDropRow = null;
        }
    }

    private void FocusTodoItem(string? itemId, TodoFocusPlacement placement = TodoFocusPlacement.End)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_todoEditors.TryGetValue(itemId, out var box))
            {
                box.Focus();
                box.CaretIndex = placement == TodoFocusPlacement.Start ? 0 : box.Text.Length;
            }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void SyncTodoAppendArea()
    {
        if (_todoPanel == null)
        {
            _appendArea = null;
            return;
        }

        if (!_controller.State.ShowTodoBottomBar)
        {
            if (_appendArea != null)
            {
                _todoPanel.Children.Remove(_appendArea);
            }
            _appendArea = null;
            return;
        }

        if (_appendArea == null || !_todoPanel.Children.Contains(_appendArea))
        {
            _todoPanel.Children.Add(BuildTodoAppendArea());
            return;
        }

        var appendIndex = _todoPanel.Children.IndexOf(_appendArea);
        if (appendIndex != _todoPanel.Children.Count - 1)
        {
            _todoPanel.Children.RemoveAt(appendIndex);
            _todoPanel.Children.Add(_appendArea);
        }
    }

    private Border BuildTodoAppendArea()
    {
        var metrics = TodoVisualSizes.Metrics(_controller.State.TodoVisualSize);
        var area = new Border
        {
            Margin = new Thickness(0, 6, 0, 2),
            Padding = new Thickness(
                0,
                Math.Max(AppTypography.Scale(3), metrics.TextVerticalPadding + AppTypography.Scale(1)),
                0,
                Math.Max(AppTypography.Scale(3), metrics.TextVerticalPadding + AppTypography.Scale(1))),
            CornerRadius = new CornerRadius(Theme.IsPixelSkin ? 0 : RadiusControl),
            BorderThickness = new Thickness(1),
            BorderBrush = AppendBorderBrush,
            Background = AppendBgBrush,
            MinHeight = metrics.AppendMinHeight,
            Cursor = Cursors.IBeam,
            ToolTip = Strings.Get("AppendAreaToolTip")
        };

        _appendArea = area;

        var plus = new TextBlock
        {
            Text = "＋",
            Foreground = WeakTextBrush,
            Opacity = 0.42,
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = metrics.AppendGlyphFontSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        area.Child = plus;

        area.MouseEnter += (_, _) =>
        {
            area.Background = AppendHoverBgBrush;
            plus.Opacity = 0.7;
        };

        area.MouseLeave += (_, _) =>
        {
            area.Background = AppendBgBrush;
            plus.Opacity = 0.42;
        };

        area.MouseLeftButtonDown += (_, e) =>
        {
            var newItem = AddItemAfter(OrderedItems().LastOrDefault(), "");
            _pendingFocusItemId = newItem.Id;
            ReconcileTodoRows(focusItemId: newItem.Id);
            e.Handled = true;
        };

        return area;
    }

    private static string CompactLinkedPaperTitle(string title, int fullTextElementLimit, int truncatedTextElementCount)
    {
        var text = title.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        int[] textElements;
        try
        {
            textElements = StringInfo.ParseCombiningCharacters(text);
        }
        catch
        {
            if (text.Length <= fullTextElementLimit)
            {
                return text;
            }

            return text[..Math.Min(Math.Max(1, truncatedTextElementCount), text.Length)] + "…";
        }

        if (textElements.Length <= fullTextElementLimit)
        {
            return text;
        }

        var keep = Math.Max(1, truncatedTextElementCount);
        var end = textElements.Length > keep ? textElements[keep] : Math.Min(keep, text.Length);
        return text[..end] + "…";
    }

    private static string CompactLinkedPaperTitleByDisplayWidth(string title, int fullDisplayWidthLimit, int truncatedDisplayWidth)
    {
        var text = title.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        if (EdgeCapsuleLayout.DisplayWidth(text) <= fullDisplayWidthLimit)
        {
            return text;
        }

        var keepWidth = Math.Max(1, truncatedDisplayWidth);
        var indexes = StringInfo.ParseCombiningCharacters(text);
        var width = 0;
        var end = 0;
        foreach (var index in indexes)
        {
            var nextIndex = NextTextElementIndex(indexes, index, text.Length);
            var element = text[index..nextIndex];
            var elementWidth = Math.Max(1, EdgeCapsuleLayout.DisplayWidth(element));
            if (width > 0 && width + elementWidth > keepWidth)
            {
                break;
            }

            width += elementWidth;
            end = nextIndex;
        }

        if (end <= 0)
        {
            end = indexes.Length > 0 ? NextTextElementIndex(indexes, indexes[0], text.Length) : Math.Min(1, text.Length);
        }

        return text[..end] + "…";
    }

    private static int NextTextElementIndex(int[] indexes, int currentIndex, int fallbackLength)
    {
        for (var i = 0; i < indexes.Length; i++)
        {
            if (indexes[i] == currentIndex)
            {
                return i + 1 < indexes.Length ? indexes[i + 1] : fallbackLength;
            }
        }

        return fallbackLength;
    }

    private Border BuildTodoRow(PaperItem item, bool isNewItem = false)
    {
        var metrics = TodoVisualSizes.Metrics(_controller.State.TodoVisualSize);
        var linkedPaperTitle = "";
        var hasLinkedPath = !string.IsNullOrWhiteSpace(item.LinkedPath);
        var hasLinkedPaper = _controller.State.EnableTodoPaperLinks &&
            _controller.TryGetLinkedPaperTitle(item.LinkedPaperId, out linkedPaperTitle);
        var runLinkedScriptOnClick = hasLinkedPaper &&
            _controller.ShouldRunLinkedScriptCapsule(item.LinkedPaperId);
        var todoRemindersEnabled =
            _controller.State.ExperimentalTodoReminders;
        var showTodoReminderButton = todoRemindersEnabled &&
            _controller.State.ExperimentalTodoReminderShowButton;
        var showTodoReminderControl = todoRemindersEnabled &&
            (showTodoReminderButton || item.ReminderAt.HasValue);

        var row = new Border
        {
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(Theme.IsPixelSkin ? 0 : RadiusControl),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0, 2, 0, 2),
            AllowDrop = true,
            Tag = item.Id,
            RenderTransform = new TransformGroup
            {
                Children = new TransformCollection
                {
                    new ScaleTransform(1, 1),
                    new TranslateTransform(0, 0)
                }
            },
            RenderTransformOrigin = new Point(0.5, 0.5)
        };

        row.MouseEnter += (_, _) =>
        {
            if (!Equals(_activeDropRow, row) && !Equals(_linkedPaperDropRow, row))
            {
                UpdateTodoRowBackground(row);
            }
        };

        row.MouseLeave += (_, _) =>
        {
            if (!Equals(_activeDropRow, row) && !Equals(_linkedPaperDropRow, row))
            {
                UpdateTodoRowBackground(row);
            }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(metrics.CheckColumnWidth)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (showTodoReminderControl)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(
                AppTypography.Scale(18),
                metrics.CheckColumnWidth - AppTypography.Scale(4)))
        });

        var check = new CheckBox
        {
            IsChecked = item.Done,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = Cursors.Hand,
            Focusable = false,
            FocusVisualStyle = null,
            Style = CurrentTodoCheckBoxStyle()
        };

        Grid.SetColumn(check, 0);
        grid.Children.Add(check);
        Border? reminderButton = null;
        Border? reminderCountdown = null;

        var text = new TodoTextBox
        {
            Text = item.Text,
            IsDone = item.Done,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = item.Done ? BrightWeakTextBrush : TextBrush,
            CaretBrush = TextBrush,
            FontFamily = AppTypography.FontFamilyFor(content: true, bold: _controller.State.TodoTextBold),
            FontSize = metrics.TextFontSize,
            FontWeight = AppTypography.FontWeightFor(_controller.State.TodoTextBold),
            Padding = new Thickness(
                AppTypography.Scale(2),
                metrics.TextVerticalPadding,
                AppTypography.Scale(2),
                metrics.TextVerticalPadding),
            VerticalContentAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            AcceptsReturn = false,
            MaxLength = TodoTextMaxLength
        };

        _todoEditors[item.Id] = text;

        text.TextChanged += (_, _) =>
        {
            AcknowledgeTriggeredTodoReminder(item, row);
            item.Text = text.Text;
            InvalidateEdgeCapsulePreviewContent();
            _controller.MarkDirty();
        };

        _todoArrowNavigation.Attach(text, backward =>
        {
            var neighbor = backward ? PreviousItem(item) : NextItem(item);
            return neighbor != null && _todoEditors.TryGetValue(neighbor.Id, out var editor)
                ? editor : null;
        });
        text.PreviewKeyDown += (_, e) => HandleTodoKeyDown(e, item, text);
        DataObject.AddPastingHandler(text, (sender, e) => HandleTodoPaste(e, item, text));

        text.GotFocus += (_, _) =>
        {
            _activeOriginalItemId = item.Id;
            _activeOriginalText = text.Text;
        };

        text.LostFocus += (_, _) =>
        {
            if (_activeOriginalItemId == item.Id && _activeOriginalText != null && text.Text != _activeOriginalText)
            {
                var oldText = item.Text;
                item.Text = _activeOriginalText;

                _undoStack.Add(CloneItems(_paper.Items));
                if (_undoStack.Count > MaxUndoDepth)
                {
                    _undoStack.RemoveAt(0);
                }
                _redoStack.Clear();

                item.Text = oldText;
                _activeOriginalText = oldText;
            }
        };

        check.Checked += (_, _) =>
        {
            PushUndoSnapshot();
            item.Done = true;
            InvalidateEdgeCapsulePreviewContent();
            if (item.ReminderAt.HasValue || item.ReminderTriggered)
            {
                item.ReminderAt = null;
                item.ReminderTriggered = false;
                _controller.NotifyTodoReminderChanged(saveImmediately: false);
            }
            if (reminderButton != null)
            {
                reminderButton.Visibility = Visibility.Hidden;
            }
            if (reminderCountdown != null)
            {
                reminderCountdown.Visibility = Visibility.Hidden;
            }
            text.IsDone = true;
            text.Foreground = BrightWeakTextBrush;
            _controller.MarkDirty();

            if (_controller.State.AutoClearCompletedTodos)
            {
                RemoveItem(item, pushUndo: false);
                return;
            }

            if (MoveTodoItemsAfterDoneChange([item], done: true))
            {
                ReconcileTodoRows([item.Id]);
                return;
            }
            SyncCompletedTodoSection();

            // 完成动画：只淡化，不缩小
            if (_controller.State.EnableAnimations)
            {
                AnimationHelper.FadeTo(row, 0.75, 200, AnimationHelper.QuickEase);
            }
        };

        check.Unchecked += (_, _) =>
        {
            PushUndoSnapshot();
            item.Done = false;
            InvalidateEdgeCapsulePreviewContent();
            if (reminderButton != null)
            {
                reminderButton.Visibility = Visibility.Visible;
            }
            if (reminderCountdown != null)
            {
                reminderCountdown.Visibility = item.ReminderAt.HasValue
                    ? Visibility.Visible
                    : Visibility.Hidden;
            }
            if (todoRemindersEnabled && item.ReminderAt.HasValue)
            {
                _controller.NotifyTodoReminderCollectionChanged();
            }
            text.IsDone = false;
            text.Foreground = TextBrush;
            _controller.MarkDirty();

            if (MoveTodoItemsAfterDoneChange([item], done: false))
            {
                ReconcileTodoRows([item.Id]);
                return;
            }
            SyncCompletedTodoSection();

            // 取消完成动画
            if (_controller.State.EnableAnimations)
            {
                AnimationHelper.FadeTo(row, 1.0, 150);
            }
            else
            {
                row.BeginAnimation(OpacityProperty, null);
                row.Opacity = 1.0;
            }
        };

        MenuItem? PopulateItemMenu(ContextMenu itemMenu)
        {
            if (TryPopulateTodoSelectionContextMenu(item, itemMenu))
            {
                return null;
            }

            MenuItem? reminderMenu = null;
            itemMenu.Items.Add(MenuHeader(Strings.Get("MenuTodoItem")));
            if (hasLinkedPaper)
            {
                var openMenuText = runLinkedScriptOnClick
                    ? Strings.Format("MenuEditLinkedScriptCapsule", linkedPaperTitle)
                    : Strings.Format("MenuOpenLinkedPaper", linkedPaperTitle);
                itemMenu.Items.Add(MenuItem(openMenuText, (_, _) => _controller.OpenLinkedPaper(item.LinkedPaperId, this)));
                itemMenu.Items.Add(MenuItem(Strings.Get("MenuUnlinkPaper"), (_, _) => UnlinkPaperFromTodoItem(item)));
                itemMenu.Items.Add(MenuSeparator());
            }
            else if (hasLinkedPath)
            {
                itemMenu.Items.Add(MenuItem(
                    Strings.Format("MenuOpenLinkedPath", PathDisplayName(item.LinkedPath!)),
                    (_, _) => OpenTodoLinkedPath(item)));
                itemMenu.Items.Add(MenuItem(
                    Strings.Get("MenuOpenLinkedPathLocation"),
                    (_, _) => OpenTodoLinkedPathLocation(item)));
                itemMenu.Items.Add(MenuItem(
                    Strings.Get("MenuUnlinkPath"),
                    (_, _) => UnlinkPathFromTodoItem(item)));
                itemMenu.Items.Add(MenuSeparator());
            }
            if (todoRemindersEnabled)
            {
                reminderMenu = BuildTodoReminderContextMenuItem(item.Id);
                reminderMenu.IsEnabled = !item.Done;
                itemMenu.Items.Add(reminderMenu);
                itemMenu.Items.Add(MenuSeparator());
            }
            itemMenu.Items.Add(MenuItem(Strings.Get("MenuDeleteItem"), (_, _) => RemoveItem(item)));
            itemMenu.Items.Add(MenuItem(Strings.Get("MenuClearDone"), (_, _) => ClearDoneItems()));

            return reminderMenu;
        }

        void AttachItemContextMenu(FrameworkElement element)
        {
            // WPF needs a non-null menu before ContextMenuOpening to open on the first
            // mouse or keyboard request. Populate the same instance: TextBox's class
            // handler attaches its Closed cleanup before our Opening handler runs.
            var menu = new MaterialContextMenu();
            var initialized = false;
            MenuItem? reminderMenu = null;
            element.ContextMenu = menu;
            element.ContextMenuOpening += (_, e) =>
            {
                // The event bubbles through the row as well as the clicked child. Only
                // the nearest owner fills its menu; the window can then append its actions.
                if (!ReferenceEquals(element.ContextMenu, menu) || !ReferenceEquals(
                        FindContextMenuOwner(e.OriginalSource as DependencyObject, this),
                        element))
                {
                    return;
                }

                if (!initialized)
                {
                    InitializeContextMenu(menu);
                    menu.Opened += (_, _) =>
                    {
                        row.Background = _selectedTodoItemIds.Count > 1 && _selectedTodoItemIds.Contains(item.Id)
                            ? TodoSelectionBrush
                            : HoverBrush;
                        if (reminderMenu != null)
                        {
                            reminderMenu.IsEnabled = !item.Done;
                        }
                    };
                    menu.Closed += (_, _) => UpdateTodoRowBackground(row);
                    initialized = true;
                }

                PrepareTodoSelectionForContextMenu(item.Id);
                menu.Items.Clear();
                reminderMenu = PopulateItemMenu(menu);
            };
            element.PreviewMouseRightButtonDown += (_, _) =>
            {
                PrepareTodoSelectionForContextMenu(item.Id);
                text.Focus();
            };
        }

        AttachItemContextMenu(row);
        AttachItemContextMenu(check);
        AttachItemContextMenu(text);

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (hasLinkedPath)
        {
            var pathLinkButton = BuildTodoPathLinkButton(item, text, metrics);
            AttachItemContextMenu(pathLinkButton);
            Grid.SetColumn(pathLinkButton, 2);
            grid.Children.Add(pathLinkButton);
        }
        else if (hasLinkedPaper)
        {
            var showLinkedPaperName = _controller.State.ShowLinkedPaperName;
            var allowLongLinkedPaperTitle = showLinkedPaperName && _controller.State.AllowLongLinkedPaperTitles;
            var linkedPaperActive = _controller.IsLinkedPaperShown(item.LinkedPaperId);

            string LinkedPaperButtonLabel(bool isTodoMultiline)
            {
                var title = allowLongLinkedPaperTitle
                    ? CompactLinkedPaperTitleByDisplayWidth(
                        linkedPaperTitle,
                        isTodoMultiline ? 20 : 10,
                        isTodoMultiline ? 20 : 10)
                    : isTodoMultiline
                        ? CompactLinkedPaperTitle(linkedPaperTitle, 6, 5)
                        : CompactLinkedPaperTitle(linkedPaperTitle, 3, 3);
                return runLinkedScriptOnClick ? "⚡ " + title : title;
            }

            double LegacyLinkedPaperButtonWidth(bool isTodoMultiline)
            {
                return isTodoMultiline
                    ? Math.Max(runLinkedScriptOnClick ? 52 : 44, metrics.CheckColumnWidth * (runLinkedScriptOnClick ? 2.35 : 2))
                    : Math.Max(runLinkedScriptOnClick ? 58 : 50, metrics.CheckColumnWidth * (runLinkedScriptOnClick ? 2.55 : 2.2));
            }

            double LegacyLinkedPaperTextMaxWidth(bool isTodoMultiline)
            {
                return metrics.CheckColumnWidth * (isTodoMultiline
                    ? runLinkedScriptOnClick ? 2.15 : 1.8
                    : runLinkedScriptOnClick ? 2.35 : 2);
            }

            double LinkedPaperButtonWidth(bool isTodoMultiline, string label)
            {
                if (!showLinkedPaperName)
                {
                    return Math.Max(23, metrics.CheckColumnWidth);
                }

                var legacyWidth = LegacyLinkedPaperButtonWidth(isTodoMultiline);
                if (!allowLongLinkedPaperTitle)
                {
                    return legacyWidth;
                }

                var measuredWidth = MeasureCapsuleTextWidth(label, metrics.LinkedPaperNameFontSize, FontWeights.SemiBold, AppTypography.UiFontFamily) + 10;
                return Math.Max(legacyWidth, Math.Ceiling(measuredWidth));
            }

            double LinkedPaperTextMaxWidth(bool isTodoMultiline, double buttonWidth)
            {
                if (allowLongLinkedPaperTitle)
                {
                    return Math.Max(1, buttonWidth - 6);
                }

                return LegacyLinkedPaperTextMaxWidth(isTodoMultiline);
            }

            var linkedPaperButtonText = showLinkedPaperName
                ? LinkedPaperButtonLabel(isTodoMultiline: false)
                : runLinkedScriptOnClick ? "⚡" : "\uE71B";
            var multilineLinkedPaperButtonText = showLinkedPaperName
                ? LinkedPaperButtonLabel(isTodoMultiline: true)
                : linkedPaperButtonText;
            var linkedPaperButtonWidth = showLinkedPaperName
                ? Math.Max(
                    LinkedPaperButtonWidth(isTodoMultiline: false, linkedPaperButtonText),
                    LinkedPaperButtonWidth(isTodoMultiline: true, multilineLinkedPaperButtonText))
                : Math.Max(23, metrics.CheckColumnWidth);
            var linkGlyph = new TextBlock
            {
                Text = linkedPaperButtonText,
                Foreground = linkedPaperActive ? LinkedPaperActiveTextBrush : WeakTextBrush,
                Opacity = linkedPaperActive ? 1.0 : 0.72,
                FontFamily = showLinkedPaperName
                    ? AppTypography.UiFontFamily
                    : runLinkedScriptOnClick ? new FontFamily("Segoe UI Symbol") : new FontFamily("Segoe MDL2 Assets"),
                FontSize = showLinkedPaperName
                    ? metrics.LinkedPaperNameFontSize
                    : runLinkedScriptOnClick ? metrics.LinkedPaperIconFontSize + 1 : metrics.LinkedPaperIconFontSize,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                LineHeight = showLinkedPaperName ? metrics.LinkedPaperNameFontSize + 1 : double.NaN,
                MaxWidth = showLinkedPaperName ? LinkedPaperTextMaxWidth(isTodoMultiline: false, linkedPaperButtonWidth) : double.PositiveInfinity
            };

            var linkButton = new Border
            {
                Width = showLinkedPaperName
                    ? linkedPaperButtonWidth
                    : Math.Max(23, metrics.CheckColumnWidth),
                MinWidth = Math.Max(23, metrics.CheckColumnWidth),
                MinHeight = Math.Max(22, metrics.RowMinHeight - 2),
                Margin = new Thickness(1, 0, 0, 0),
                Padding = showLinkedPaperName ? new Thickness(3, 1, 3, 1) : new Thickness(0),
                CornerRadius = new CornerRadius(Theme.IsPixelSkin ? 0 : RadiusControl),
                Background = linkedPaperActive ? LinkedPaperLightBgBrush : LinkedPaperNormalBgBrush,
                Cursor = Cursors.Hand,
                ToolTip = runLinkedScriptOnClick
                    ? Strings.Format("ToolTipRunLinkedScriptCapsule", linkedPaperTitle)
                    : Strings.Format("ToolTipOpenLinkedPaper", linkedPaperTitle),
                Child = linkGlyph
            };

            bool? lastLinkedPaperNameMultiline = null;
            var linkedPaperNameLayoutQueued = false;

            void UpdateLinkedPaperNameLayout()
            {
                linkedPaperNameLayoutQueued = false;
                if (!showLinkedPaperName)
                {
                    return;
                }

                var isTodoMultiline = text.LineCount > 1;
                if (lastLinkedPaperNameMultiline == isTodoMultiline)
                {
                    return;
                }

                lastLinkedPaperNameMultiline = isTodoMultiline;
                linkGlyph.Text = isTodoMultiline ? multilineLinkedPaperButtonText : linkedPaperButtonText;
                linkGlyph.TextWrapping = isTodoMultiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
                linkGlyph.MaxWidth = LinkedPaperTextMaxWidth(isTodoMultiline, linkedPaperButtonWidth);
            }

            void QueueLinkedPaperNameLayoutUpdate()
            {
                if (!showLinkedPaperName)
                {
                    return;
                }

                if (linkedPaperNameLayoutQueued)
                {
                    return;
                }

                linkedPaperNameLayoutQueued = true;
                Dispatcher.BeginInvoke((Action)UpdateLinkedPaperNameLayout, System.Windows.Threading.DispatcherPriority.Render);
            }

            if (showLinkedPaperName)
            {
                text.SizeChanged += (_, _) => QueueLinkedPaperNameLayoutUpdate();
                row.SizeChanged += (_, _) => QueueLinkedPaperNameLayoutUpdate();
                text.TextChanged += (_, _) => QueueLinkedPaperNameLayoutUpdate();
                QueueLinkedPaperNameLayoutUpdate();
            }

            void RefreshLinkedPaperPresentation()
            {
                if (!_controller.TryGetLinkedPaperTitle(
                        item.LinkedPaperId,
                        out var refreshedTitle))
                {
                    return;
                }

                // Visibility is button presentation, not a reason to replace its Todo editor.
                linkedPaperActive = _controller.IsLinkedPaperShown(item.LinkedPaperId);
                var hovered = linkButton.IsMouseOver;
                linkButton.Background = linkedPaperActive
                    ? hovered ? LinkedPaperMediumBgBrush : LinkedPaperLightBgBrush
                    : hovered ? LinkedPaperLightBgBrush : LinkedPaperNormalBgBrush;
                linkGlyph.Foreground = linkedPaperActive
                    ? LinkedPaperActiveTextBrush : hovered ? TextBrush : WeakTextBrush;
                linkGlyph.Opacity = linkedPaperActive || hovered ? 1.0 : 0.72;

                var runScript = _controller.ShouldRunLinkedScriptCapsule(item.LinkedPaperId);
                if (linkedPaperTitle == refreshedTitle && runLinkedScriptOnClick == runScript)
                {
                    return;
                }
                linkedPaperTitle = refreshedTitle;
                runLinkedScriptOnClick = runScript;
                linkedPaperButtonText = showLinkedPaperName
                    ? LinkedPaperButtonLabel(isTodoMultiline: false)
                    : runLinkedScriptOnClick ? "⚡" : "\uE71B";
                multilineLinkedPaperButtonText = showLinkedPaperName
                    ? LinkedPaperButtonLabel(isTodoMultiline: true)
                    : linkedPaperButtonText;
                linkedPaperButtonWidth = showLinkedPaperName
                    ? Math.Max(
                        LinkedPaperButtonWidth(false, linkedPaperButtonText),
                        LinkedPaperButtonWidth(true, multilineLinkedPaperButtonText))
                    : Math.Max(23, metrics.CheckColumnWidth);
                linkButton.Width = linkedPaperButtonWidth;
                linkButton.ToolTip = runLinkedScriptOnClick
                    ? Strings.Format("ToolTipRunLinkedScriptCapsule", linkedPaperTitle)
                    : Strings.Format("ToolTipOpenLinkedPaper", linkedPaperTitle);
                if (!showLinkedPaperName)
                {
                    linkGlyph.Text = linkedPaperButtonText;
                    linkGlyph.FontFamily = new FontFamily(runLinkedScriptOnClick
                        ? "Segoe UI Symbol" : "Segoe MDL2 Assets");
                    linkGlyph.FontSize = runLinkedScriptOnClick
                        ? metrics.LinkedPaperIconFontSize + 1 : metrics.LinkedPaperIconFontSize;
                }
                lastLinkedPaperNameMultiline = null;
                UpdateLinkedPaperNameLayout();
            }

            RegisterLinkedPaperTitleRefresher(
                item.Id,
                item.LinkedPaperId,
                RefreshLinkedPaperPresentation);

            linkButton.MouseEnter += (_, _) =>
            {
                linkButton.Background = linkedPaperActive ? LinkedPaperMediumBgBrush : LinkedPaperLightBgBrush;
                linkGlyph.Foreground = linkedPaperActive ? LinkedPaperActiveTextBrush : TextBrush;
                linkGlyph.Opacity = 1.0;
            };
            linkButton.MouseLeave += (_, _) =>
            {
                linkButton.Background = linkedPaperActive ? LinkedPaperLightBgBrush : LinkedPaperNormalBgBrush;
                linkGlyph.Foreground = linkedPaperActive ? LinkedPaperActiveTextBrush : WeakTextBrush;
                linkGlyph.Opacity = linkedPaperActive ? 1.0 : 0.7;
                linkButton.Opacity = 1.0;
            };
            linkButton.MouseLeftButtonDown += (_, e) =>
            {
                linkButton.Opacity = 0.72;
                e.Handled = true;
            };
            linkButton.MouseLeftButtonUp += (_, e) =>
            {
                linkButton.Opacity = 1.0;
                if (!_controller.ShouldRunLinkedScriptCapsule(item.LinkedPaperId) ||
                    !_controller.RunLinkedScriptCapsule(item.LinkedPaperId))
                {
                    _controller.OpenLinkedPaper(
                        item.LinkedPaperId,
                        this,
                        toggleIfExcludedFromCapsules: true);
                }
                e.Handled = true;
            };
            AttachItemContextMenu(linkButton);

            Grid.SetColumn(linkButton, 2);
            grid.Children.Add(linkButton);
        }

        if (showTodoReminderControl)
        {
            var reminderHost = new Grid();
            if (showTodoReminderButton)
            {
                reminderButton = BuildTodoReminderButton(item, metrics);
                reminderButton.Visibility =
                    !item.Done && !item.ReminderAt.HasValue
                        ? Visibility.Visible
                        : Visibility.Hidden;
                AttachItemContextMenu(reminderButton);
                reminderHost.Children.Add(reminderButton);
            }
            if (item.ReminderAt.HasValue)
            {
                reminderCountdown = BuildTodoReminderCountdown(item, metrics);
                AttachItemContextMenu(reminderCountdown);
                reminderHost.Children.Add(reminderCountdown);
            }

            Grid.SetColumn(reminderHost, 3);
            grid.Children.Add(reminderHost);
        }

        var handleGlyph = new TextBlock
        {
            Text = "≡",
            Foreground = WeakTextBrush,
            Opacity = 0.48,
            FontSize = Math.Max(AppTypography.Scale(11), metrics.TextFontSize - AppTypography.Scale(1)),
            FontFamily = AppTypography.SymbolFontFamily,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var handle = new Border
        {
            Width = Math.Max(
                AppTypography.Scale(14),
                metrics.CheckColumnWidth - AppTypography.Scale(8)),
            MinHeight = metrics.RowMinHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(RadiusSmall),
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            Child = handleGlyph,
            ToolTip = Strings.Get("DragSortToolTip")
        };

        handle.MouseEnter += (_, _) => handleGlyph.Opacity = 0.78;
        handle.MouseLeave += (_, _) =>
        {
            if (_todoDrag?.ItemId != item.Id)
            {
                handleGlyph.Opacity = 0.48;
            }
        };

        handle.PreviewMouseLeftButtonDown += (_, e) =>
        {
            PrepareTodoDragSelection(item.Id);
            _todoDrag = new TodoDragState(
                item.Id,
                row,
                handle,
                e.GetPosition(this),
                e.GetPosition(row));
            CaptureMouse();
            e.Handled = true;
        };
        AttachItemContextMenu(handle);

        // Reuse the trailing drag column; clipping the native editor never alters its text.
        var foldGlyph = new System.Windows.Shapes.Path
        {
            Data = TodoFoldUp,
            Stroke = WeakTextBrush,
            StrokeThickness = AppTypography.Scale(1.5),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
            Width = AppTypography.Scale(11),
            Height = AppTypography.Scale(9),
            Opacity = 0.58,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var foldButton = new Border
        {
            Width = Math.Max(AppTypography.Scale(18), metrics.CheckColumnWidth - AppTypography.Scale(4)),
            Height = AppTypography.Scale(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
            Child = foldGlyph
        };
        _todoFoldFindControls[item.Id] = (foldButton, foldGlyph);
        var trailingControls = new Grid();
        trailingControls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        trailingControls.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(foldButton, 0);
        trailingControls.Children.Add(foldButton);
        Grid.SetRow(handle, 1);
        trailingControls.Children.Add(handle);

        var foldRefreshQueued = false;

        bool ApplyFoldLineLimit(int maxLines)
        {
            if (text.MaxLines == maxLines) return false;

            // WPF caches MaxLines as a cap on the inner ScrollViewer. Restoring
            // int.MaxValue alone leaves that cap in place; an explicit unbounded
            // MaxHeight makes WPF clear it. Remove the override before folding.
            if (maxLines == int.MaxValue)
                text.MaxHeight = double.PositiveInfinity;
            else
                text.ClearValue(FrameworkElement.MaxHeightProperty);

            text.MaxLines = maxLines;
            return true;
        }

        void RefreshFoldPresentation()
        {
            foldRefreshQueued = false;
            if (!_todoEditors.TryGetValue(item.Id, out var live) || !ReferenceEquals(live, text))
                return;

            var lineCount = text.LineCount;
            if (lineCount < 0) return; // Layout/width change will retry when lines become available.
            var available = lineCount > TodoFoldThreshold;
            if (!available) _foldedTodoItemIds.Remove(item.Id);
            var folded = available && _foldedTodoItemIds.Contains(item.Id) && !text.IsKeyboardFocusWithin;

            foldButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            foldGlyph.Data = folded ? TodoFoldDown : TodoFoldUp;
            foldButton.ToolTip = Strings.Get(folded ? "TodoExpandFold" : "TodoCollapseFold");
            handle.MinHeight = available
                ? Math.Max(AppTypography.Scale(16), metrics.RowMinHeight - AppTypography.Scale(9))
                : metrics.RowMinHeight;
            text.VerticalContentAlignment = folded ? VerticalAlignment.Top : VerticalAlignment.Center;
            var maxLines = folded ? TodoFoldVisibleLines : int.MaxValue;
            if (ApplyFoldLineLimit(maxLines) && folded)
            {
                // After editing near the end, folding should show the first two lines.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_todoEditors.TryGetValue(item.Id, out var current) &&
                        ReferenceEquals(current, text) &&
                        text.MaxLines == TodoFoldVisibleLines && !text.IsKeyboardFocusWithin)
                    {
                        text.ScrollToLine(0);
                    }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            QueueTodoFindCueRefresh();
        }

        void QueueFoldRefresh()
        {
            if (foldRefreshQueued) return;
            foldRefreshQueued = true;
            Dispatcher.BeginInvoke(new Action(RefreshFoldPresentation),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        foldButton.MouseEnter += (_, _) => foldGlyph.Opacity = 0.9;
        foldButton.MouseLeave += (_, _) =>
            foldGlyph.Opacity = _findHiddenTodoItemIds.Contains(item.Id) ? 1.0 : 0.58;
        foldButton.MouseLeftButtonDown += (_, e) => e.Handled = true;
        foldButton.MouseLeftButtonUp += (_, e) =>
        {
            var alreadyFolded = _foldedTodoItemIds.Contains(item.Id) && !text.IsKeyboardFocusWithin;
            if (alreadyFolded)
            {
                _foldedTodoItemIds.Remove(item.Id);
            }
            else
            {
                _foldedTodoItemIds.Add(item.Id);
                if (text.IsKeyboardFocusWithin) Keyboard.ClearFocus();
            }
            RefreshFoldPresentation();
            if (IsBuiltInFindOpen)
            {
                // The new MaxLines layout settles after the click. Recompute hit
                // visibility before selecting the current match, without auto-opening.
                _ = Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (!IsBuiltInFindOpen) return;
                    RefreshTodoFindCues();
                    ApplyCurrentFindMatch();
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            e.Handled = true;
        };
        text.Loaded += (_, _) => QueueFoldRefresh();
        text.SizeChanged += (_, e) => { if (e.WidthChanged) QueueFoldRefresh(); };
        text.TextChanged += (_, _) => QueueFoldRefresh();
        text.GotKeyboardFocus += (_, _) =>
        {
            if (_foldedTodoItemIds.Contains(item.Id)) ApplyFoldLineLimit(int.MaxValue);
            QueueFoldRefresh();
        };
        text.LostKeyboardFocus += (_, _) => QueueFoldRefresh();

        Grid.SetColumn(trailingControls, showTodoReminderControl ? 4 : 3);
        grid.Children.Add(trailingControls);

        row.Child = grid;
        ConfigureTodoPathDrop(row, item);
        ConfigureTodoMultiSelection(row, item, check, text);

        // 新增动画：只对新建的项播放动画
        if (_controller.State.EnableAnimations && isNewItem)
        {
            row.Opacity = 0;
            AnimationHelper.GetTranslateTransform(row).Y = -20;

            Dispatcher.InvokeAsync(() =>
            {
                AnimationHelper.FadeIn(row, 250);
                AnimationHelper.TranslateTo(row, 0, 0, 250, AnimationHelper.SmoothEase);
            }, System.Windows.Threading.DispatcherPriority.Render);
        }

        return row;
    }

    private void HandleTodoKeyDown(KeyEventArgs e, PaperItem item, TodoTextBox box)
    {
        if (e.Key == Key.Back && _suppressTodoBackspaceUntilKeyUp)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            // Keep the native TextBox undo/redo path and honor the existing text limit.
            if (box.Text.Length - box.SelectionLength + Environment.NewLine.Length <= box.MaxLength)
            {
                var insertionStart = box.SelectionStart;
                box.SelectedText = Environment.NewLine;
                // SelectedText preserves the inserted range as a selection in WPF.
                // Collapse it so the next keystroke continues on the new line.
                box.Select(insertionStart + Environment.NewLine.Length, 0);
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            var newItem = AddItemAfter(item, "");
            _pendingFocusItemId = newItem.Id;
            ReconcileTodoRows(focusItemId: newItem.Id);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back &&
            string.IsNullOrEmpty(box.Text) &&
            !TodoRules.HasNonTextContent(item) &&
            _paper.Items.Count > 1)
        {
            var previous = PreviousItem(item);
            var next = NextItem(item);
            var focusTarget = previous?.Id ?? next?.Id;
            _suppressTodoBackspaceUntilKeyUp = true;
            var previousItems = CloneItems(_paper.Items);

            // 退格删除不播放动画，直接删除
            PushUndoSnapshot();
            _paper.Items.RemoveAll(i => i.Id == item.Id);

            if (_paper.Items.Count == 0)
            {
                var replacement = new PaperItem();
                _paper.Items.Add(replacement);
                focusTarget = replacement.Id;
            }

            NormalizeTodoItems();
            NormalizeOrders();
            _controller.MarkDirty();
            _controller.NotifyTodoReminderCollectionChanged();

            var focusPlacement = previous != null ? TodoFocusPlacement.End : TodoFocusPlacement.Start;
            ReconcileTodoRows(
                focusItemId: focusTarget,
                focusPlacement: focusPlacement);
            RefreshCapsuleEligibilityForLinkedPaperChanges(previousItems);
            e.Handled = true;
        }
    }

    private void HandleTodoPaste(DataObjectPastingEventArgs e, PaperItem item, TodoTextBox box)
    {
        if (!TryGetTodoPastingText(e.DataObject, out var raw))
        {
            return;
        }

        var cleanedLines = raw
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(CleanPastedTodoLine)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
        var tooManyItems = cleanedLines.Count > MaxPastedTodoLines;
        var textTruncated = cleanedLines.Any(line => line.Length > TodoTextMaxLength);
        var lines = cleanedLines
            .Take(MaxPastedTodoLines)
            .Select(LimitTodoText)
            .ToList();

        if (lines.Count <= 1)
        {
            var originalText = box.Text ?? "";
            var selectionStart = Math.Clamp(box.SelectionStart, 0, originalText.Length);
            var selectionLength = Math.Clamp(
                box.SelectionLength,
                0,
                originalText.Length - selectionStart);
            textTruncated |=
                (long)originalText.Length - selectionLength + raw.Length >
                TodoTextMaxLength;
            ShowTodoPasteLimitNotice(tooManyItems, textTruncated);
            return;
        }

        e.CancelCommand();

        var currentText = box.Text ?? "";
        var currentSelectionStart = Math.Clamp(box.SelectionStart, 0, currentText.Length);
        var currentSelectionLength = Math.Clamp(
            box.SelectionLength,
            0,
            currentText.Length - currentSelectionStart);
        var selectionEnd = currentSelectionStart + currentSelectionLength;
        var prefix = currentText[..currentSelectionStart];
        var suffix = currentText[selectionEnd..];
        textTruncated |=
            (long)prefix.Length + lines[0].Length > TodoTextMaxLength ||
            (long)lines[^1].Length + suffix.Length > TodoTextMaxLength;

        var pastedItemTexts = lines.ToList();
        pastedItemTexts[0] = LimitTodoText(prefix + pastedItemTexts[0]);
        pastedItemTexts[^1] = LimitTodoText(pastedItemTexts[^1] + suffix);

        PushUndoSnapshot();
        _activeOriginalItemId = null;
        _activeOriginalText = null;
        box.Text = pastedItemTexts[0];
        box.CaretIndex = Math.Min(box.Text.Length, prefix.Length + lines[0].Length);
        item.Text = box.Text;

        var ordered = OrderedItems().ToList();
        var itemIndex = ordered.FindIndex(i => string.Equals(i.Id, item.Id, StringComparison.Ordinal));
        var insertIndex = itemIndex >= 0 ? itemIndex + 1 : ordered.Count;
        var newItems = new List<PaperItem>();
        foreach (var line in pastedItemTexts.Skip(1))
        {
            newItems.Add(new PaperItem
            {
                Text = line,
                Done = false
            });
        }

        ordered.InsertRange(insertIndex, newItems);
        _paper.Items = ordered;
        NormalizeTodoItems();
        NormalizeOrders();
        TodoRules.ApplyCompletedOrdering(
            _paper.Items,
            _controller.State.AutoMoveCompletedTodosToBottom);
        _controller.MarkDirty();

        var focusItem = newItems.LastOrDefault() ?? item;
        _pendingFocusItemId = focusItem.Id;
        ReconcileTodoRows(focusItemId: focusItem.Id);

        // 粘贴多行时的错峰动画
        if (_controller.State.EnableAnimations && newItems.Count > 1)
        {
            var animationGeneration = _todoRowsGeneration;
            for (int i = 0; i < Math.Min(newItems.Count, 15); i++)
            {
                var animItem = newItems[i];
                var animRow = _todoRows.FirstOrDefault(r => (string)r.Tag == animItem.Id);
                if (animRow == null) continue;

                var delay = i * 40;
                animRow.Opacity = 0;
                AnimationHelper.GetTranslateTransform(animRow).Y = -15;

                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(delay),
                    Tag = animRow
                };
                timer.Tick += (s, _) =>
                {
                    timer.Stop();
                    var row = (Border)timer.Tag;
                    if (animationGeneration != _todoRowsGeneration || !_todoRows.Contains(row))
                    {
                        return;
                    }

                    if (_todoDrag?.IsDragging == true && ReferenceEquals(_todoDrag.SourceRow, row))
                    {
                        return;
                    }

                    AnimationHelper.FadeIn(row, 200);
                    AnimationHelper.TranslateTo(row, 0, 0, 220, AnimationHelper.QuickEase);
                };
                timer.Start();
            }
        }

        ShowTodoPasteLimitNotice(tooManyItems, textTruncated);
        _controller.MarkDirty();
    }

    private void ShowTodoPasteLimitNotice(bool tooManyItems, bool textTruncated)
    {
        if (!tooManyItems && !textTruncated)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                var messages = new List<string>(2);
                if (tooManyItems)
                {
                    messages.Add(Strings.Format(
                        "TodoPasteItemLimitMessage",
                        MaxPastedTodoLines));
                }
                if (textTruncated)
                {
                    messages.Add(Strings.Format(
                        "TodoPasteTextLimitMessage",
                        TodoTextMaxLength));
                }

                PaperNoticeDialog.Show(
                    this,
                    Strings.Get("TodoPasteTruncatedTitle"),
                    string.Join(Environment.NewLine, messages));
            }),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private static string CleanPastedTodoLine(string line)
    {
        var cleaned = line.Trim();

        cleaned = TodoCheckboxCleanRegex().Replace(cleaned, "");
        cleaned = TodoBulletCleanRegex().Replace(cleaned, "");
        cleaned = TodoNumberCleanRegex().Replace(cleaned, "");
        cleaned = TodoGlyphCleanRegex().Replace(cleaned, "");

        return cleaned.Trim();
    }

    private static string LimitTodoText(string text)
    {
        return text.Length > TodoTextMaxLength ? text[..TodoTextMaxLength] : text;
    }


    public void UpdateTodoLinkFeature()
    {
        RefreshWindowBindingButton();

        if (!_controller.State.EnableTodoPaperLinks)
        {
            _controller.EndPaperLinkDrag(_paper, commit: false);
            SetPaperLinkDropTarget(null);
        }

        if (!_controller.State.EnableTodoPaperLinks &&
            !_controller.State.ExperimentalWindowTethering)
        {
            EndTopBarDragGesture(
                commit: false,
                TopBarDragKind.WindowBinding);
        }

        RefreshTodoRowsForExternalChange();
    }


    private PaperItem AddItemAfter(PaperItem? after, string text, bool pushUndo = true)
    {
        if (pushUndo) PushUndoSnapshot();
        var ordered = OrderedItems().ToList();
        var index = after == null ? ordered.Count : ordered.FindIndex(i => i.Id == after.Id) + 1;
        if (index < 0) index = ordered.Count;

        var newItem = new PaperItem
        {
            Text = text,
            Done = false
        };

        ordered.Insert(index, newItem);
        _paper.Items = ordered;
        NormalizeTodoItems();
        NormalizeOrders();
        TodoRules.ApplyCompletedOrdering(
            _paper.Items,
            _controller.State.AutoMoveCompletedTodosToBottom);
        _controller.MarkDirty();

        return newItem;
    }

    private void RemoveItem(PaperItem item, bool rebuild = true, string? focusItemId = null, bool pushUndo = true)
    {
        if (pushUndo)
        {
            PushUndoSnapshot();
        }

        var fallbackFocus = focusItemId ?? PreviousItem(item)?.Id ?? NextItem(item)?.Id;
        var itemId = item.Id;
        var removedLinkedPaperIds = _paper.Items
            .Where(i => i.Id == itemId)
            .Select(i => i.LinkedPaperId)
            .ToList();

        // 删除动画
        if (_controller.State.EnableAnimations)
        {
            var row = _todoRows.FirstOrDefault(r => (string)r.Tag == itemId);
            if (row != null)
            {
                _paper.Items.RemoveAll(i => i.Id == itemId);

                if (_paper.Items.Count == 0)
                {
                    var replacement = new PaperItem();
                    _paper.Items.Add(replacement);
                    fallbackFocus = replacement.Id;
                }

                NormalizeTodoItems();
                NormalizeOrders();
                _controller.MarkDirty();
                _controller.NotifyTodoReminderCollectionChanged();
                RefreshCapsuleEligibilityForLinkedPapers(removedLinkedPaperIds);

                var animationGeneration = _todoRowsGeneration;
                row.IsHitTestVisible = false;
                AnimationHelper.EnsureTransform(row);
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(
                    1,
                    0,
                    TimeSpan.FromMilliseconds(AnimationTiming.ScaleMilliseconds(200)));
                var slideOut = new System.Windows.Media.Animation.DoubleAnimation(
                    0,
                    30,
                    TimeSpan.FromMilliseconds(AnimationTiming.ScaleMilliseconds(200)))
                {
                    EasingFunction = AnimationHelper.QuickEase
                };

                fadeOut.Completed += (s, e) =>
                {
                    if (rebuild && animationGeneration == _todoRowsGeneration)
                    {
                        ReconcileTodoRows(focusItemId: fallbackFocus);
                    }
                };

                row.BeginAnimation(OpacityProperty, fadeOut);
                AnimationHelper.GetTranslateTransform(row).BeginAnimation(TranslateTransform.XProperty, slideOut);
                return;
            }
        }

        // 无动画或找不到行时直接删除
        _paper.Items.RemoveAll(i => i.Id == itemId);

        if (_paper.Items.Count == 0)
        {
            var replacement = new PaperItem();
            _paper.Items.Add(replacement);
            fallbackFocus = replacement.Id;
        }

        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();
        _controller.NotifyTodoReminderCollectionChanged();
        RefreshCapsuleEligibilityForLinkedPapers(removedLinkedPaperIds);

        if (rebuild)
        {
            ReconcileTodoRows(focusItemId: fallbackFocus);
        }
    }

    private void ClearDoneItems()
    {
        if (_paper.Type != PaperTypes.Todo)
        {
            return;
        }

        var focusedId = CurrentFocusedTodoItemId();
        var completedItems = OrderedItems().Where(i => i.Done).ToList();
        if (completedItems.Count == 0)
        {
            return;
        }

        var completedItemIds = new HashSet<string>(completedItems.Select(i => i.Id), StringComparer.Ordinal);
        var removedLinkedPaperIds = completedItems
            .Select(i => i.LinkedPaperId)
            .ToList();
        var clearDoneGeneration = ++_clearDoneGeneration;

        PushUndoSnapshot();
        var remainingItems = OrderedItems()
            .Where(i => !completedItemIds.Contains(i.Id))
            .ToList();

        if (remainingItems.Count == 0)
        {
            remainingItems.Add(new PaperItem());
        }

        _paper.Items = remainingItems;
        NormalizeTodoItems();
        NormalizeOrders();

        var focus = remainingItems.FirstOrDefault(i => i.Id == focusedId)?.Id
            ?? remainingItems.FirstOrDefault(i => !TodoRules.IsPlaceholder(i))?.Id
            ?? remainingItems.FirstOrDefault()?.Id;

        _controller.MarkDirty();
        _controller.NotifyTodoReminderCollectionChanged();
        RefreshCapsuleEligibilityForLinkedPapers(removedLinkedPaperIds);

        // 批量消失动画
        if (_controller.State.EnableAnimations && completedItems.Count > 0)
        {
            var animatedRows = completedItems
                .Take(15)
                .Select(item => _todoRows.FirstOrDefault(r => (string)r.Tag == item.Id))
                .Where(row => row != null)
                .Cast<Border>()
                .ToList();

            if (animatedRows.Count > 0)
            {
                var rowGeneration = _todoRowsGeneration;
                var rowAnimationMilliseconds = AnimationTiming.ScaleMilliseconds(180);
                var rowStaggerMilliseconds = AnimationTiming.ScaleMilliseconds(30);
                for (int i = 0; i < animatedRows.Count; i++)
                {
                    var row = animatedRows[i];
                    row.IsHitTestVisible = false;
                    var delay = i * rowStaggerMilliseconds;
                    void StartRowAnimation()
                    {
                        if (clearDoneGeneration != _clearDoneGeneration ||
                            rowGeneration != _todoRowsGeneration ||
                            !_todoRows.Contains(row))
                        {
                            return;
                        }

                        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(
                            1,
                            0,
                            TimeSpan.FromMilliseconds(rowAnimationMilliseconds));
                        var slideOut = new System.Windows.Media.Animation.DoubleAnimation(
                            0,
                            20,
                            TimeSpan.FromMilliseconds(rowAnimationMilliseconds))
                        {
                            EasingFunction = AnimationHelper.QuickEase
                        };

                        row.BeginAnimation(OpacityProperty, fadeOut);
                        AnimationHelper.GetTranslateTransform(row).BeginAnimation(TranslateTransform.XProperty, slideOut);
                    }

                    if (delay == 0)
                    {
                        StartRowAnimation();
                        continue;
                    }

                    var timer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(delay)
                    };
                    timer.Tick += (s, _) =>
                    {
                        timer.Stop();
                        StartRowAnimation();
                    };
                    timer.Start();
                }

                var finalizeTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(
                        ((animatedRows.Count - 1) * rowStaggerMilliseconds) +
                        rowAnimationMilliseconds)
                };
                finalizeTimer.Tick += (_, _) =>
                {
                    finalizeTimer.Stop();
                    if (clearDoneGeneration == _clearDoneGeneration &&
                        rowGeneration == _todoRowsGeneration)
                    {
                        ReconcileTodoRows(focusItemId: focus);
                    }
                };
                finalizeTimer.Start();
                return;
            }
        }

        ReconcileTodoRows(focusItemId: focus);
    }

    private void RefreshCapsuleEligibilityForLinkedPapers(IEnumerable<string?> paperIds)
    {
        _controller.RefreshCapsuleEligibilityForLinkedPapers(paperIds);
    }

    private void RefreshCapsuleEligibilityForLinkedPaperChanges(IEnumerable<PaperItem> previousItems)
    {
        var changedPaperIds = previousItems
            .Select(item => item.LinkedPaperId)
            .Where(paperId => !string.IsNullOrWhiteSpace(paperId))
            .Select(paperId => paperId!)
            .ToHashSet(StringComparer.Ordinal);
        changedPaperIds.SymmetricExceptWith(_paper.Items
            .Select(item => item.LinkedPaperId)
            .Where(paperId => !string.IsNullOrWhiteSpace(paperId))
            .Select(paperId => paperId!));

        RefreshCapsuleEligibilityForLinkedPapers(changedPaperIds);
    }

    public bool TryHitTodoRow(Point screenPoint, out string? itemId)
    {
        itemId = null;
        if (!_controller.State.EnableTodoPaperLinks || _paper.Type != PaperTypes.Todo || _paper.IsCollapsed || !IsVisible)
        {
            return false;
        }

        foreach (var row in _todoRows)
        {
            if (row.Tag is not string rowItemId || !row.IsVisible || row.ActualWidth <= 0 || row.ActualHeight <= 0)
            {
                continue;
            }

            var point = row.PointFromScreen(screenPoint);
            if (point.X < 0 || point.X > row.ActualWidth || point.Y < 0 || point.Y > row.ActualHeight)
            {
                continue;
            }

            itemId = rowItemId;
            return true;
        }

        return false;
    }

    public void SetPaperLinkDropTarget(string? itemId)
    {
        if (_linkedPaperDropRow?.Tag is string currentId &&
            string.Equals(currentId, itemId, StringComparison.Ordinal))
        {
            return;
        }

        ClearPaperLinkDropTargetVisual();

        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        var row = _todoRows.FirstOrDefault(r =>
            r.Tag is string rowItemId &&
            string.Equals(rowItemId, itemId, StringComparison.Ordinal));
        if (row == null)
        {
            return;
        }

        _linkedPaperDropRow = row;
        row.Background = PaperLinkTargetBgBrush;
        row.BorderBrush = PaperLinkTargetBorderBrush;
        row.BorderThickness = new Thickness(1);
        row.Padding = new Thickness(1, 3, 1, 3);
    }

    public bool LinkPaperToTodo(string itemId, string paperId)
    {
        if (!_controller.State.EnableTodoPaperLinks || _paper.Type != PaperTypes.Todo ||
            !_controller.IsExistingPaper(paperId) || string.Equals(_paper.Id, paperId, StringComparison.Ordinal))
        {
            return false;
        }

        var item = _paper.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null)
        {
            return false;
        }

        if (string.Equals(item.LinkedPaperId, paperId, StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(item.LinkedPath))
        {
            return true;
        }

        var focusedId = CurrentFocusedTodoItemId();
        var previousItems = CloneItems(_paper.Items);
        PushUndoSnapshot();
        item.LinkPaper(paperId);
        _controller.MarkDirty();
        ReconcileTodoRows([item.Id], focusedId);
        RefreshCapsuleEligibilityForLinkedPaperChanges(previousItems);
        return true;
    }

    private void UnlinkPaperFromTodoItem(PaperItem item)
    {
        if (string.IsNullOrWhiteSpace(item.LinkedPaperId))
        {
            return;
        }

        var focusedId = CurrentFocusedTodoItemId() ?? item.Id;
        var previousItems = CloneItems(_paper.Items);
        PushUndoSnapshot();
        item.ClearQuickLaunch();
        _controller.MarkDirty();
        ReconcileTodoRows([item.Id], focusedId);
        RefreshCapsuleEligibilityForLinkedPaperChanges(previousItems);
    }

    private void ClearPaperLinkDropTargetVisual()
    {
        var row = _linkedPaperDropRow;
        if (row == null)
        {
            return;
        }

        _linkedPaperDropRow = null;
        row.BorderThickness = new Thickness(0, 2, 0, 2);
        row.BorderBrush = Brushes.Transparent;
        row.Padding = new Thickness(2);

        if (!Equals(_activeDropRow, row))
        {
            row.Background = row.IsMouseOver ? HoverBrush : Brushes.Transparent;
        }
    }

    private void RegisterLinkedPaperTitleRefresher(
        string itemId,
        string? paperId,
        Action refresher)
    {
        if (string.IsNullOrWhiteSpace(paperId))
        {
            return;
        }

        if (!_linkedPaperTitleRefreshers.TryGetValue(paperId, out var refreshers))
        {
            refreshers = new Dictionary<string, Action>(StringComparer.Ordinal);
            _linkedPaperTitleRefreshers[paperId] = refreshers;
        }
        refreshers[itemId] = refresher;
    }

    public void RefreshLinkedPaperTitle(string paperId)
    {
        if (!_linkedPaperTitleRefreshers.TryGetValue(paperId, out var refreshers))
        {
            return;
        }

        foreach (var refresher in refreshers.Values.ToArray())
        {
            refresher();
        }
    }







    private PaperItem? PreviousItem(PaperItem item)
    {
        var ordered = OrderedItems().ToList();
        var index = ordered.FindIndex(i => i.Id == item.Id);
        return index > 0 ? ordered[index - 1] : null;
    }

    private PaperItem? NextItem(PaperItem item)
    {
        var ordered = OrderedItems().ToList();
        var index = ordered.FindIndex(i => i.Id == item.Id);
        return index >= 0 && index < ordered.Count - 1 ? ordered[index + 1] : null;
    }

    private void BeginTodoMouseDrag()
    {
        if (_todoDrag == null)
        {
            return;
        }

        _todoDrag.IsDragging = true;

        var sourceRow = _todoDrag.SourceRow;
        _todoDrag.RestingOpacity = (double)sourceRow.GetAnimationBaseValue(OpacityProperty);
        sourceRow.BeginAnimation(OpacityProperty, null);
        sourceRow.Opacity = _todoDrag.RestingOpacity;

        var translate = AnimationHelper.GetTranslateTransform(sourceRow);
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        translate.X = 0;
        translate.Y = 0;

        sourceRow.Opacity = 0.25;
        sourceRow.Background = HoverBrush;
        BeginTodoGroupDragVisuals(_todoDrag.ItemId);
        _todoDrag.Handle.Opacity = 0.9;
        Mouse.OverrideCursor = Cursors.SizeAll;

        _todoDrag.Ghost = CreateTodoDragGhost(_todoDrag);
        _dragLayer?.Children.Add(_todoDrag.Ghost);
        UpdateTodoDragGhost(_todoDrag, _todoDrag.StartPoint);

        StartTodoDragEdgeScroll();
    }

    private void OnWindowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_todoDrag == null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // A released button observed from MouseMove means the owning MouseUp was lost.
            // Only the explicit MouseUp handler may commit a reorder.
            EndTodoMouseDrag(commit: false);
            e.Handled = true;
            return;
        }

        var current = e.GetPosition(this);

        if (!_todoDrag.IsDragging)
        {
            var movedEnough =
                Math.Abs(current.X - _todoDrag.StartPoint.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(current.Y - _todoDrag.StartPoint.Y) >= SystemParameters.MinimumVerticalDragDistance;

            if (!movedEnough)
            {
                return;
            }

            BeginTodoMouseDrag();
        }

        var panelPoint = _todoPanel != null ? e.GetPosition(_todoPanel) : current;
        UpdateTodoMouseDrag(panelPoint, current);
        e.Handled = true;
    }

    private void OnWindowPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_todoDrag == null)
        {
            return;
        }

        EndTodoMouseDrag(commit: _todoDrag.IsDragging);
        e.Handled = true;
    }

    private void UpdateTodoMouseDrag(Point pointOnPanel, Point pointOnWindow)
    {
        if (_todoDrag == null || _todoPanel == null)
        {
            return;
        }

        UpdateTodoDragGhost(_todoDrag, pointOnWindow);
        ClearActiveDropIndicator();

        var candidates = _todoRows
            .Where(row =>
                row.IsVisible &&
                row.Tag is string id &&
                !_todoGroupDragItemIds.Contains(id))
            .ToList();

        if (candidates.Count == 0)
        {
            _todoDrag.TargetId = null;
            return;
        }

        double bestDist = double.MaxValue;
        Border? bestRow = null;
        var bestPlacement = DropPlacement.After;

        foreach (var row in candidates)
        {
            double top = row.TranslatePoint(new Point(0, 0), _todoPanel).Y;
            ConsiderDropBoundary(row, DropPlacement.Before, top);
            ConsiderDropBoundary(row, DropPlacement.After, top + row.ActualHeight);
        }

        if (bestRow == null)
        {
            _todoDrag.TargetId = null;
            return;
        }

        ShowDropIndicator(bestRow, bestPlacement);
        _todoDrag.TargetId = bestRow.Tag as string;
        _todoDrag.TargetPlacement = bestPlacement;

        void ConsiderDropBoundary(Border row, DropPlacement placement, double y)
        {
            double dist = Math.Abs(pointOnPanel.Y - y);
            if (dist >= bestDist)
            {
                return;
            }

            bestDist = dist;
            bestRow = row;
            bestPlacement = placement;
        }
    }

    private Border CreateTodoDragGhost(TodoDragState state)
    {
        var metrics = TodoVisualSizes.Metrics(_controller.State.TodoVisualSize);
        var item = _paper.Items.FirstOrDefault(i => i.Id == state.ItemId);
        var text = TodoDragGhostText(item?.Text ?? "");
        var done = !IsTodoGroupDrag && item?.Done == true;

        var ghost = new Border
        {
            Width = Math.Max(state.SourceRow.ActualWidth, 160),
            MinHeight = Math.Max(state.SourceRow.ActualHeight, 30),
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(Theme.IsPixelSkin ? 0 : RadiusControl),
            Background = PaperBrush,
            BorderBrush = Theme.Tint(150),
            BorderThickness = new Thickness(1),
            Opacity = 0.65,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 3,
                Opacity = 0.24
            }
        };

        var grid = new Grid
        {
            IsHitTestVisible = false
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(metrics.CheckColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(
                AppTypography.Scale(18),
                metrics.CheckColumnWidth - AppTypography.Scale(4)))
        });

        var check = new TextBlock
        {
            Text = done ? "☑" : "☐",
            Foreground = done ? BrightWeakTextBrush : TextBrush,
            FontSize = metrics.GhostTextFontSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.78
        };
        Grid.SetColumn(check, 0);
        grid.Children.Add(check);

        var content = new TextBlock
        {
            Text = text,
            Foreground = done ? BrightWeakTextBrush : TextBrush,
            FontFamily = AppTypography.FontFamilyFor(content: true, bold: _controller.State.TodoTextBold),
            FontSize = metrics.GhostTextFontSize,
            FontWeight = AppTypography.FontWeightFor(_controller.State.TodoTextBold),
            Padding = new Thickness(
                AppTypography.Scale(2),
                metrics.TextVerticalPadding,
                AppTypography.Scale(2),
                metrics.TextVerticalPadding),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (done)
        {
            content.TextDecorations = TextDecorations.Strikethrough;
        }

        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        var handle = new TextBlock
        {
            Text = "≡",
            Foreground = WeakTextBrush,
            Opacity = 0.58,
            FontSize = Math.Max(AppTypography.Scale(12), metrics.GhostTextFontSize - AppTypography.Scale(1)),
            FontFamily = AppTypography.SymbolFontFamily,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(handle, 2);
        grid.Children.Add(handle);

        ghost.Child = grid;
        return ghost;
    }

    private void CloseTodoDragGhost(TodoDragState state)
    {
        if (state.Ghost == null)
        {
            return;
        }

        _dragLayer?.Children.Remove(state.Ghost);
        state.Ghost = null;
    }

    private static void UpdateTodoDragGhost(TodoDragState state, Point pointOnWindow)
    {
        if (state.Ghost == null)
        {
            return;
        }

        Canvas.SetLeft(state.Ghost, pointOnWindow.X - state.MouseOffsetInRow.X);
        Canvas.SetTop(state.Ghost, pointOnWindow.Y - state.MouseOffsetInRow.Y);
    }

    private void EndTodoMouseDrag(bool commit)
    {
        var state = _todoDrag;
        if (state == null)
        {
            return;
        }

        _todoDrag = null;
        StopTodoDragEdgeScroll();

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        Mouse.OverrideCursor = null;

        CloseTodoDragGhost(state);
        EndTodoGroupDragVisuals();

        state.SourceRow.BeginAnimation(OpacityProperty, null);
        state.SourceRow.Opacity = state.RestingOpacity;
        UpdateTodoRowBackground(state.SourceRow);
        state.Handle.Opacity = 1.0;

        ClearActiveDropIndicator();

        if (!commit)
        {
            ClearTodoDragGroupState();
            ReconcileTodoRows(focusItemId: state.ItemId);
            return;
        }

        if (!string.IsNullOrWhiteSpace(state.TargetId))
        {
            var draggedIds = _todoGroupDragItemIds.Count > 0
                ? _todoGroupDragItemIds.ToArray()
                : new[] { state.ItemId };
            ClearTodoDragGroupState();
            MoveItems(
                draggedIds,
                state.TargetId,
                state.TargetPlacement,
                focusItemId: state.ItemId);
            return;
        }

        ClearTodoDragGroupState();
        ReconcileTodoRows(focusItemId: state.ItemId);
    }

    private void MoveItems(
        IReadOnlyCollection<string> draggedIds,
        string targetId,
        DropPlacement placement,
        string focusItemId)
    {
        if (!TodoRules.TryCreateMovedOrder(
                _paper.Items,
                draggedIds,
                targetId,
                placement == DropPlacement.After,
                out var reordered))
        {
            return;
        }

        PushUndoSnapshot();
        _paper.Items = reordered;
        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();

        ReconcileTodoRows(focusItemId: focusItemId);
    }

    private IEnumerable<PaperItem> OrderedItems()
    {
        return _paper.Items.OrderBy(i => i.Order).ToList();
    }

    private void NormalizeTodoItems()
    {
        if (_paper.Type != PaperTypes.Todo)
        {
            return;
        }

        var ordered = _paper.Items.ToList();
        if (ordered.Count == 0)
        {
            ordered.Add(new PaperItem());
        }

        _paper.Items = ordered;
    }

    private string? CurrentFocusedTodoItemId()
    {
        var focused = FocusManager.GetFocusedElement(this);

        if (focused is TodoTextBox box)
        {
            foreach (var pair in _todoEditors)
            {
                if (ReferenceEquals(pair.Value, box))
                {
                    return pair.Key;
                }
            }
        }

        return null;
    }

    private void NormalizeOrders()
    {
        // Preserve the current list order. Sorting here would undo freshly inserted
        // or dragged rows because new items start with Order = 0 until we renumber them.
        TodoRules.NormalizeOrders(_paper.Items);
    }

    private void ShowDropIndicator(Border row, DropPlacement placement)
    {
        if (!Equals(_activeDropRow, row))
        {
            ClearActiveDropIndicator();
            _activeDropRow = row;
        }

        if (_dragLayer == null)
        {
            return;
        }

        if (_dropIndicatorLine == null)
        {
            _dropIndicatorLine = new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = DropIndicatorBrush,
                IsHitTestVisible = false
            };
            Panel.SetZIndex(_dropIndicatorLine, 1001);
            _dragLayer.Children.Add(_dropIndicatorLine);
        }

        _dropIndicatorLine.Background = DropIndicatorBrush;
        var rowOrigin = row.TranslatePoint(new Point(0, 0), _dragLayer);
        var y = placement == DropPlacement.Before
            ? rowOrigin.Y
            : rowOrigin.Y + row.ActualHeight;
        var width = Math.Max(24, row.ActualWidth - 8);

        _dropIndicatorLine.Width = width;
        Canvas.SetLeft(_dropIndicatorLine, rowOrigin.X + 4);
        Canvas.SetTop(_dropIndicatorLine, y - (_dropIndicatorLine.Height / 2));
    }

    private void ClearDropIndicator(Border row)
    {
        if (Equals(_activeDropRow, row))
        {
            _activeDropRow = null;
        }

        row.BorderThickness = new Thickness(0, 2, 0, 2);
        row.BorderBrush = Brushes.Transparent;
        row.Padding = new Thickness(2);

        if (_dropIndicatorLine != null)
        {
            _dragLayer?.Children.Remove(_dropIndicatorLine);
            _dropIndicatorLine = null;
        }
    }

    private void ClearActiveDropIndicator()
    {
        if (_activeDropRow != null)
        {
            ClearDropIndicator(_activeDropRow);
            _activeDropRow = null;
        }
    }

    private static List<PaperItem> CloneItems(List<PaperItem> items)
    {
        return TodoRules.CloneAll(items);
    }

    private void PushUndoSnapshot()
    {
        CommitFocusedTextIfNeeded();

        _undoStack.Add(CloneItems(_paper.Items));
        if (_undoStack.Count > MaxUndoDepth)
        {
            _undoStack.RemoveAt(0);
        }
        _redoStack.Clear();
    }

    private void CommitFocusedTextIfNeeded()
    {
        var focusedId = CurrentFocusedTodoItemId();
        if (focusedId != null && _todoEditors.TryGetValue(focusedId, out var box))
        {
            if (_activeOriginalItemId == focusedId && _activeOriginalText != null && box.Text != _activeOriginalText)
            {
                var item = _paper.Items.FirstOrDefault(i => i.Id == focusedId);
                if (item != null)
                {
                    var oldText = item.Text;
                    item.Text = _activeOriginalText;

                    var oldSnapshot = CloneItems(_paper.Items);
                    _undoStack.Add(oldSnapshot);
                    if (_undoStack.Count > MaxUndoDepth)
                    {
                        _undoStack.RemoveAt(0);
                    }

                    item.Text = oldText;
                    _activeOriginalText = oldText;
                }
            }
        }
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        var focusedId = CurrentFocusedTodoItemId();

        var currentItems = CloneItems(_paper.Items);
        _redoStack.Add(currentItems);

        var previousItems = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        RestoreTodoHistorySnapshot(previousItems, focusedId);
        RefreshCapsuleEligibilityForLinkedPaperChanges(currentItems);
    }

    private void Redo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }

        var focusedId = CurrentFocusedTodoItemId();

        var currentItems = CloneItems(_paper.Items);
        _undoStack.Add(currentItems);

        var nextItems = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);

        RestoreTodoHistorySnapshot(nextItems, focusedId);
        RefreshCapsuleEligibilityForLinkedPaperChanges(currentItems);
    }

    private void RestoreTodoHistorySnapshot(List<PaperItem> snapshot, string? focusedId)
    {
        var currentById = _paper.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var rebuildIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < snapshot.Count; index++)
        {
            var restored = snapshot[index];
            if (currentById.TryGetValue(restored.Id, out var current) &&
                TodoHistoryRowContentEquals(current, restored))
            {
                // Retained row handlers close over this exact model instance. Replacing it
                // with an equal snapshot would leave subsequent edits writing to a stale item.
                snapshot[index] = current;
            }
            else
            {
                rebuildIds.Add(restored.Id);
            }
        }

        var retainedFocus = focusedId != null &&
            !rebuildIds.Contains(focusedId) &&
            snapshot.Any(item => item.Id == focusedId) &&
            _todoEditors.ContainsKey(focusedId);

        // Applying history is not a new human edit. Row removal/reordering may raise LostFocus;
        // an unchanged focused editor, on the other hand, will not raise GotFocus again.
        _activeOriginalItemId = null;
        _activeOriginalText = null;
        _pendingFocusItemId = null;
        _paper.Items = snapshot;
        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();
        _controller.NotifyTodoReminderCollectionChanged();
        ReconcileTodoRows(rebuildIds, retainedFocus ? null : focusedId);

        // The former full rebuild reset native TextBox history. Keep that paper-history
        // boundary so a retained editor cannot replay stale local edits ahead of Ctrl+Y/Z.
        foreach (var editor in _todoEditors.Values)
        {
            if (editor.IsUndoEnabled)
            {
                editor.SetCurrentValue(TextBoxBase.IsUndoEnabledProperty, false);
                editor.SetCurrentValue(TextBoxBase.IsUndoEnabledProperty, true);
            }
        }
        if (retainedFocus && _todoEditors.TryGetValue(focusedId!, out var focusedEditor))
        {
            // A reorder can detach and reinsert the focused row. Restore its keyboard focus
            // without moving the caret or replacing its selection as FocusTodoItem would.
            if (!focusedEditor.IsKeyboardFocused)
            {
                focusedEditor.Focus();
            }
            _activeOriginalItemId = focusedId;
            _activeOriginalText = focusedEditor.Text;
        }
    }

    private static bool TodoHistoryRowContentEquals(PaperItem current, PaperItem restored) =>
        current.Text == restored.Text &&
        current.Done == restored.Done &&
        current.LinkedPaperId == restored.LinkedPaperId &&
        current.LinkedPath == restored.LinkedPath &&
        current.LinkedPathIsDirectory == restored.LinkedPathIsDirectory &&
        current.ReminderAt == restored.ReminderAt &&
        current.ReminderAt?.Offset == restored.ReminderAt?.Offset &&
        current.ReminderTriggered == restored.ReminderTriggered;

    private bool TryCollapseExpandedPaperFromEscape()
    {
        if (_todoDrag != null ||
            _topBarDrag != null ||
            IsDeepCapsuleReordering ||
            IsDeepCapsuleSlotPendingClick ||
            _titleBarDragSession != null)
        {
            // Escape first cancels the active gesture. It must not change form while a drag can
            // still receive a later MouseUp and commit against a hidden/collapsed visual tree.
            AbortAllInteractions(InteractionAbortReason.FormChanging);
            return true;
        }

        if (_paper.IsCollapsed ||
            !_controller.State.UseCapsuleMode ||
            !CanDisplayAsCapsule())
        {
            return false;
        }

        if (_isEditingTitle)
        {
            CommitTitleEdit();
        }

        if (_paper.Type == PaperTypes.Note)
        {
            CommitPendingNoteContentForSave();
            ExitNoteEditor();
        }
        else
        {
            Keyboard.ClearFocus();
        }

        SetCollapsedState(true);
        return true;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape &&
            Keyboard.Modifiers == ModifierKeys.None &&
            TryClearTodoSelectionFromEscape())
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape &&
            Keyboard.Modifiers == ModifierKeys.None &&
            !BodyClaimsInput(PaperBodyInputClaims.EscapeKey) &&
            TryCollapseExpandedPaperFromEscape())
        {
            e.Handled = true;
            return;
        }

        if (_paper.Type == PaperTypes.Note)
        {
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.C && TryCopySelectedTodoItems())
            {
                e.Handled = true;
            }
            else if (e.Key == Key.Z)
            {
                var focusedId = CurrentFocusedTodoItemId();
                if (focusedId != null && _todoEditors.TryGetValue(focusedId, out var box))
                {
                    if (box.CanUndo)
                    {
                        return;
                    }
                }

                Undo();
                e.Handled = true;
            }
            else if (e.Key == Key.Y)
            {
                var focusedId = CurrentFocusedTodoItemId();
                if (focusedId != null && _todoEditors.TryGetValue(focusedId, out var box))
                {
                    if (box.CanRedo)
                    {
                        return;
                    }
                }

                Redo();
                e.Handled = true;
            }
        }
    }

    private void OnWindowPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back)
        {
            _suppressTodoBackspaceUntilKeyUp = false;
        }
    }

private static bool TryGetTodoPastingText(
        IDataObject dataObject,
        out string raw)
    {
        raw = "";
        try
        {
            raw = dataObject.GetDataPresent(DataFormats.UnicodeText)
                ? dataObject.GetData(DataFormats.UnicodeText) as string ?? ""
                : dataObject.GetDataPresent(DataFormats.Text)
                    ? dataObject.GetData(DataFormats.Text) as string ?? ""
                    : "";
        }
        catch
        {
            raw = "";
            return false;
        }

        return raw.Length > 0;
    }
}
