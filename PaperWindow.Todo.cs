using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PaperTodo;

public sealed partial class PaperWindow
{
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

        RebuildTodoRows();

        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _todoPanel,
            FocusVisualStyle = null
        };
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
        if (_todoPanel == null)
        {
            return;
        }

        _todoRowsGeneration++;
        var targetFocus = focusItemId ?? _pendingFocusItemId;
        _pendingFocusItemId = null;

        NormalizeTodoItems();
        NormalizeOrders();

        // 记录现有行的ID，用于判断哪些是新增的
        var existingIds = new HashSet<string>(_todoRows.Select(r => (string)r.Tag));

        _todoPanel.Children.Clear();
        _todoEditors.Clear();
        _todoRows.Clear();
        _linkedNoteDropRow = null;

        foreach (var item in OrderedItems())
        {
            var row = BuildTodoRow(item, isNewItem: !existingIds.Contains(item.Id));
            _todoPanel.Children.Add(row);
        }

        _todoPanel.Children.Add(BuildTodoAppendArea());

        if (!string.IsNullOrWhiteSpace(targetFocus))
        {
            FocusTodoItem(targetFocus, focusPlacement);
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

    private UIElement BuildTodoAppendArea()
    {
        var metrics = TodoVisualSizes.Metrics(_controller.State.TodoVisualSize);
        var area = new Border
        {
            Margin = new Thickness(0, 6, 0, 2),
            Padding = new Thickness(0, Math.Max(3, metrics.TextVerticalPadding + 1), 0, Math.Max(3, metrics.TextVerticalPadding + 1)),
            CornerRadius = new CornerRadius(RadiusControl),
            BorderThickness = new Thickness(1),
            BorderBrush = AppendBorderBrush,
            Background = AppendBgBrush,
            MinHeight = metrics.AppendMinHeight,
            Cursor = Cursors.IBeam,
            AllowDrop = true,
            ToolTip = Strings.Get("AppendAreaToolTip")
        };

        _appendArea = area;

        var plus = new TextBlock
        {
            Text = "＋",
            Foreground = WeakTextBrush,
            Opacity = 0.42,
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
            ResetAppendAreaDropState();
        };

        area.MouseLeftButtonDown += (_, e) =>
        {
            var newItem = AddItemAfter(OrderedItems().LastOrDefault(), "");
            _pendingFocusItemId = newItem.Id;
            RebuildTodoRows(newItem.Id);
            e.Handled = true;
        };

        return area;
    }

    private void ShowAppendAreaAsTrashBin(bool active, bool hovered = false)
    {
        if (_appendArea == null)
        {
            return;
        }

        if (active)
        {
            if (hovered)
            {
                _appendArea.Background = TrashHoverBgBrush;
                _appendArea.BorderBrush = TrashHoverBorderBrush;
                _appendArea.BorderThickness = new Thickness(1.5);
            }
            else
            {
                _appendArea.Background = TrashBgBrush;
                _appendArea.BorderBrush = TrashBorderBrush;
                _appendArea.BorderThickness = new Thickness(1);
            }

            if (_appendArea.Child is TextBlock text)
            {
                var metrics = TodoVisualSizes.Metrics(_controller.State.TodoVisualSize);
                text.Text = "🗑";
                text.Foreground = TrashTextBrush;
                text.Opacity = hovered ? 1.0 : 0.65;
                text.FontSize = metrics.TrashGlyphFontSize;
            }
        }
        else
        {
            _appendArea.Background = AppendBgBrush;
            _appendArea.BorderBrush = AppendBorderBrush;
            _appendArea.BorderThickness = new Thickness(1);

            if (_appendArea.Child is TextBlock text)
            {
                var metrics = TodoVisualSizes.Metrics(_controller.State.TodoVisualSize);
                text.Text = "＋";
                text.Foreground = WeakTextBrush;
                text.Opacity = 0.42;
                text.FontSize = metrics.AppendGlyphFontSize;
            }
        }
    }

    private void ResetAppendAreaDropState()
    {
        ShowAppendAreaAsTrashBin(active: false);
    }

    private static string CompactLinkedNoteTitle(string title, int fullTextElementLimit, int truncatedTextElementCount)
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

    private UIElement BuildTodoRow(PaperItem item, bool isNewItem = false)
    {
        var metrics = TodoVisualSizes.Metrics(_controller.State.TodoVisualSize);
        var linkedNoteTitle = "";
        var hasLinkedNote = _controller.State.EnableTodoNoteLinks &&
            _controller.TryGetLinkedNoteTitle(item.LinkedNoteId, out linkedNoteTitle);
        var runLinkedScriptOnClick = hasLinkedNote &&
            _controller.ShouldRunLinkedScriptCapsule(item.LinkedNoteId);

        var row = new Border
        {
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(RadiusControl),
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
            if (!Equals(_activeDropRow, row) && !Equals(_linkedNoteDropRow, row))
            {
                row.Background = HoverBrush;
            }
        };

        row.MouseLeave += (_, _) =>
        {
            if (!Equals(_activeDropRow, row) && !Equals(_linkedNoteDropRow, row))
            {
                row.Background = Brushes.Transparent;
            }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(metrics.CheckColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(18, metrics.CheckColumnWidth - 4)) });

        var check = new CheckBox
        {
            IsChecked = item.Done,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = Cursors.Hand,
            Focusable = false,
            FocusVisualStyle = null,
            Style = SharedCheckBoxStyle
        };

        Grid.SetColumn(check, 0);
        grid.Children.Add(check);

        var text = new TodoTextBox
        {
            Text = item.Text,
            IsDone = item.Done,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = item.Done ? BrightWeakTextBrush : TextBrush,
            CaretBrush = TextBrush,
            FontSize = metrics.TextFontSize,
            Padding = new Thickness(2, metrics.TextVerticalPadding, 2, metrics.TextVerticalPadding),
            VerticalContentAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            MaxLength = 5000
        };

        _todoEditors[item.Id] = text;

        text.TextChanged += (_, _) =>
        {
            item.Text = text.Text;
            _controller.MarkDirty();
        };

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
            text.IsDone = true;
            text.Foreground = BrightWeakTextBrush;
            _controller.MarkDirty();

            // 完成动画：只淡化，不缩小
            if (_controller.State.EnableAnimations)
            {
                var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.75, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = AnimationHelper.QuickEase
                };
                row.BeginAnimation(OpacityProperty, fadeAnim);
            }
        };

        check.Unchecked += (_, _) =>
        {
            PushUndoSnapshot();
            item.Done = false;
            text.IsDone = false;
            text.Foreground = TextBrush;
            _controller.MarkDirty();

            // 取消完成动画
            if (_controller.State.EnableAnimations)
            {
                var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(row.Opacity, 1.0, TimeSpan.FromMilliseconds(150));
                row.BeginAnimation(OpacityProperty, fadeAnim);
            }
        };

        ContextMenu CreateItemMenu()
        {
            var itemMenu = CreateContextMenu();
            itemMenu.Items.Add(MenuHeader(Strings.Get("MenuTodoItem")));
            if (hasLinkedNote)
            {
                var openMenuText = runLinkedScriptOnClick
                    ? Strings.Format("MenuEditLinkedScriptCapsule", linkedNoteTitle)
                    : Strings.Format("MenuOpenLinkedNote", linkedNoteTitle);
                itemMenu.Items.Add(MenuItem(openMenuText, (_, _) => _controller.OpenLinkedNote(item.LinkedNoteId, this)));
                itemMenu.Items.Add(MenuItem(Strings.Get("MenuUnlinkNote"), (_, _) => UnlinkNoteFromTodoItem(item)));
                itemMenu.Items.Add(MenuSeparator());
            }
            itemMenu.Items.Add(MenuItem(Strings.Get("MenuDeleteItem"), (_, _) => RemoveItem(item)));
            itemMenu.Items.Add(MenuItem(Strings.Get("MenuClearDone"), (_, _) => ClearDoneItems()));

            itemMenu.Opened += (_, _) => row.Background = HoverBrush;
            itemMenu.Closed += (_, _) =>
            {
                if (!row.IsMouseOver)
                {
                    row.Background = Brushes.Transparent;
                }
            };

            return itemMenu;
        }

        void AttachItemContextMenu(FrameworkElement element)
        {
            element.ContextMenu = CreateItemMenu();
            element.PreviewMouseRightButtonDown += (_, _) => text.Focus();
        }

        AttachItemContextMenu(row);
        AttachItemContextMenu(check);
        AttachItemContextMenu(text);

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (hasLinkedNote)
        {
            var showLinkedNoteName = _controller.State.ShowLinkedNoteName;
            string LinkedNoteButtonLabel(int fullTextElementLimit, int truncatedTextElementCount)
            {
                var title = CompactLinkedNoteTitle(linkedNoteTitle, fullTextElementLimit, truncatedTextElementCount);
                return runLinkedScriptOnClick ? "⚡ " + title : title;
            }

            var linkedNoteButtonText = showLinkedNoteName
                ? LinkedNoteButtonLabel(3, 3)
                : runLinkedScriptOnClick ? "⚡" : "\uE71B";
            var linkGlyph = new TextBlock
            {
                Text = linkedNoteButtonText,
                Foreground = WeakTextBrush,
                Opacity = 0.72,
                FontFamily = showLinkedNoteName
                    ? new FontFamily("Segoe UI")
                    : runLinkedScriptOnClick ? new FontFamily("Segoe UI Symbol") : new FontFamily("Segoe MDL2 Assets"),
                FontSize = showLinkedNoteName
                    ? metrics.LinkedNoteNameFontSize
                    : runLinkedScriptOnClick ? metrics.LinkedNoteIconFontSize + 1 : metrics.LinkedNoteIconFontSize,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                LineHeight = showLinkedNoteName ? metrics.LinkedNoteNameFontSize + 1 : double.NaN,
                MaxWidth = showLinkedNoteName ? metrics.CheckColumnWidth * (runLinkedScriptOnClick ? 2.35 : 2) : double.PositiveInfinity
            };

            var linkButton = new Border
            {
                Width = showLinkedNoteName
                    ? Math.Max(runLinkedScriptOnClick ? 58 : 50, metrics.CheckColumnWidth * (runLinkedScriptOnClick ? 2.55 : 2.2))
                    : Math.Max(23, metrics.CheckColumnWidth),
                MinWidth = Math.Max(23, metrics.CheckColumnWidth),
                MinHeight = Math.Max(22, metrics.RowMinHeight - 2),
                Margin = new Thickness(1, 0, 0, 0),
                Padding = showLinkedNoteName ? new Thickness(3, 1, 3, 1) : new Thickness(0),
                CornerRadius = new CornerRadius(RadiusControl),
                Background = LinkedNoteBgBrush,
                Cursor = Cursors.Hand,
                ToolTip = runLinkedScriptOnClick
                    ? Strings.Format("ToolTipRunLinkedScriptCapsule", linkedNoteTitle)
                    : Strings.Format("ToolTipOpenLinkedNote", linkedNoteTitle),
                Child = linkGlyph
            };

            void UpdateLinkedNoteNameLayout()
            {
                if (!showLinkedNoteName)
                {
                    return;
                }

                var isTodoMultiline = text.LineCount > 1;
                linkGlyph.Text = isTodoMultiline
                    ? LinkedNoteButtonLabel(6, 5)
                    : LinkedNoteButtonLabel(3, 3);
                linkGlyph.TextWrapping = isTodoMultiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
                linkGlyph.MaxWidth = isTodoMultiline
                    ? metrics.CheckColumnWidth * (runLinkedScriptOnClick ? 2.15 : 1.8)
                    : metrics.CheckColumnWidth * (runLinkedScriptOnClick ? 2.35 : 2);
                linkButton.Width = isTodoMultiline
                    ? Math.Max(runLinkedScriptOnClick ? 52 : 44, metrics.CheckColumnWidth * (runLinkedScriptOnClick ? 2.35 : 2))
                    : Math.Max(runLinkedScriptOnClick ? 58 : 50, metrics.CheckColumnWidth * (runLinkedScriptOnClick ? 2.55 : 2.2));
            }

            void QueueLinkedNoteNameLayoutUpdate()
            {
                if (!showLinkedNoteName)
                {
                    return;
                }

                Dispatcher.BeginInvoke((Action)UpdateLinkedNoteNameLayout, System.Windows.Threading.DispatcherPriority.Render);
            }

            if (showLinkedNoteName)
            {
                text.SizeChanged += (_, _) => QueueLinkedNoteNameLayoutUpdate();
                row.SizeChanged += (_, _) => QueueLinkedNoteNameLayoutUpdate();
                text.TextChanged += (_, _) => QueueLinkedNoteNameLayoutUpdate();
                QueueLinkedNoteNameLayoutUpdate();
            }

            linkButton.MouseEnter += (_, _) =>
            {
                linkButton.Background = LinkedNoteHoverBgBrush;
                linkGlyph.Foreground = TextBrush;
                linkGlyph.Opacity = 1.0;
            };
            linkButton.MouseLeave += (_, _) =>
            {
                linkButton.Background = LinkedNoteBgBrush;
                linkGlyph.Foreground = WeakTextBrush;
                linkGlyph.Opacity = 0.7;
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
                if (!_controller.ShouldRunLinkedScriptCapsule(item.LinkedNoteId) ||
                    !_controller.RunLinkedScriptCapsule(item.LinkedNoteId))
                {
                    _controller.OpenLinkedNote(item.LinkedNoteId, this);
                }
                e.Handled = true;
            };
            AttachItemContextMenu(linkButton);

            Grid.SetColumn(linkButton, 2);
            grid.Children.Add(linkButton);
        }

        var handleGlyph = new TextBlock
        {
            Text = "≡",
            Foreground = WeakTextBrush,
            Opacity = 0.48,
            FontSize = Math.Max(11, metrics.TextFontSize - 1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var handle = new Border
        {
            Width = Math.Max(14, metrics.CheckColumnWidth - 8),
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
            _todoDrag = new TodoDragState(item.Id, row, handle, e.GetPosition(this));
            CaptureMouse();
            e.Handled = true;
        };
        AttachItemContextMenu(handle);

        Grid.SetColumn(handle, 3);
        grid.Children.Add(handle);

        row.Child = grid;
        _todoRows.Add(row);

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

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            var newItem = AddItemAfter(item, "");
            _pendingFocusItemId = newItem.Id;
            RebuildTodoRows(newItem.Id);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back && string.IsNullOrEmpty(box.Text) && _paper.Items.Count > 1)
        {
            var previous = PreviousItem(item);
            var next = NextItem(item);
            var focusTarget = previous?.Id ?? next?.Id;
            _suppressTodoBackspaceUntilKeyUp = true;

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

            var focusPlacement = previous != null ? TodoFocusPlacement.End : TodoFocusPlacement.Start;
            RebuildTodoRows(focusTarget, focusPlacement);
            e.Handled = true;
        }
    }

    private void HandleTodoPaste(DataObjectPastingEventArgs e, PaperItem item, TodoTextBox box)
    {
        if (!ClipboardHelper.TryGetText(out var raw) || string.IsNullOrEmpty(raw))
        {
            return;
        }

        var lines = raw
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(CleanPastedTodoLine)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Length > 5000 ? line[..5000] : line)
            .ToList();

        if (lines.Count > 200)
        {
            lines = lines.Take(200).ToList();
        }

        if (lines.Count <= 1)
        {
            return;
        }

        e.CancelCommand();

        PushUndoSnapshot();
        ReplaceSelection(box, lines[0]);
        item.Text = box.Text;

        var last = item;
        var newItems = new List<PaperItem>();
        foreach (var line in lines.Skip(1))
        {
            last = AddItemAfter(last, line, pushUndo: false);
            newItems.Add(last);
        }

        _pendingFocusItemId = last.Id;
        RebuildTodoRows(last.Id);

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

                    AnimationHelper.FadeIn(row, 200);
                    AnimationHelper.TranslateTo(row, 0, 0, 220, AnimationHelper.QuickEase);
                };
                timer.Start();
            }
        }

        _controller.MarkDirty();
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


    public void UpdateTodoLinkFeature()
    {
        if (_linkNoteButton != null)
        {
            _linkNoteButton.Visibility = _controller.State.EnableTodoNoteLinks ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!_controller.State.EnableTodoNoteLinks)
        {
            EndNoteLinkMouseGesture(commit: false);
            SetNoteLinkDropTarget(null);
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
        _controller.MarkDirty();

        return newItem;
    }

    private void RemoveItem(PaperItem item, bool rebuild = true, string? focusItemId = null)
    {
        PushUndoSnapshot();
        var fallbackFocus = focusItemId ?? PreviousItem(item)?.Id ?? NextItem(item)?.Id;
        var itemId = item.Id;

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

                var animationGeneration = _todoRowsGeneration;
                row.IsHitTestVisible = false;
                AnimationHelper.EnsureTransform(row);
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
                var slideOut = new System.Windows.Media.Animation.DoubleAnimation(0, 30, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = AnimationHelper.QuickEase
                };

                fadeOut.Completed += (s, e) =>
                {
                    if (rebuild && animationGeneration == _todoRowsGeneration)
                    {
                        RebuildTodoRows(fallbackFocus);
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

        if (rebuild)
        {
            RebuildTodoRows(fallbackFocus);
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
            ?? remainingItems.FirstOrDefault(i => !IsBlank(i))?.Id
            ?? remainingItems.FirstOrDefault()?.Id;

        _controller.MarkDirty();

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
                for (int i = 0; i < animatedRows.Count; i++)
                {
                    var row = animatedRows[i];
                    row.IsHitTestVisible = false;
                    var delay = i * 30;
                    void StartRowAnimation()
                    {
                        if (clearDoneGeneration != _clearDoneGeneration ||
                            rowGeneration != _todoRowsGeneration ||
                            !_todoRows.Contains(row))
                        {
                            return;
                        }

                        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
                        var slideOut = new System.Windows.Media.Animation.DoubleAnimation(0, 20, TimeSpan.FromMilliseconds(180))
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
                    Interval = TimeSpan.FromMilliseconds(((animatedRows.Count - 1) * 30) + 180)
                };
                finalizeTimer.Tick += (_, _) =>
                {
                    finalizeTimer.Stop();
                    if (clearDoneGeneration == _clearDoneGeneration &&
                        rowGeneration == _todoRowsGeneration)
                    {
                        RebuildTodoRows(focus);
                    }
                };
                finalizeTimer.Start();
                return;
            }
        }

        RebuildTodoRows(focus);
    }

    public bool TryHitTodoRow(Point screenPoint, out string? itemId)
    {
        itemId = null;
        if (!_controller.State.EnableTodoNoteLinks || _paper.Type != PaperTypes.Todo || _paper.IsCollapsed || !IsVisible)
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

    public void SetNoteLinkDropTarget(string? itemId)
    {
        if (_linkedNoteDropRow?.Tag is string currentId &&
            string.Equals(currentId, itemId, StringComparison.Ordinal))
        {
            return;
        }

        ClearNoteLinkDropTargetVisual();

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

        _linkedNoteDropRow = row;
        row.Background = NoteLinkTargetBgBrush;
        row.BorderBrush = NoteLinkTargetBorderBrush;
        row.BorderThickness = new Thickness(1);
        row.Padding = new Thickness(1, 3, 1, 3);
    }

    public bool LinkNoteToTodo(string itemId, string noteId)
    {
        if (!_controller.State.EnableTodoNoteLinks || _paper.Type != PaperTypes.Todo || !_controller.IsExistingNote(noteId))
        {
            return false;
        }

        var item = _paper.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null)
        {
            return false;
        }

        if (string.Equals(item.LinkedNoteId, noteId, StringComparison.Ordinal))
        {
            return true;
        }

        var focusedId = CurrentFocusedTodoItemId();
        PushUndoSnapshot();
        item.LinkedNoteId = noteId;
        _controller.MarkDirty();
        RebuildTodoRows(focusedId);
        _controller.RefreshCapsuleEligibilityForLinkedNotes();
        return true;
    }

    private void UnlinkNoteFromTodoItem(PaperItem item)
    {
        if (string.IsNullOrWhiteSpace(item.LinkedNoteId))
        {
            return;
        }

        var focusedId = CurrentFocusedTodoItemId() ?? item.Id;
        PushUndoSnapshot();
        item.LinkedNoteId = null;
        _controller.MarkDirty();
        RebuildTodoRows(focusedId);
        _controller.RefreshCapsuleEligibilityForLinkedNotes();
    }

    private void ClearNoteLinkDropTargetVisual()
    {
        var row = _linkedNoteDropRow;
        if (row == null)
        {
            return;
        }

        _linkedNoteDropRow = null;
        row.BorderThickness = new Thickness(0, 2, 0, 2);
        row.BorderBrush = Brushes.Transparent;
        row.Padding = new Thickness(2);

        if (!Equals(_activeDropRow, row))
        {
            row.Background = row.IsMouseOver ? HoverBrush : Brushes.Transparent;
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

        var rowOrigin = _todoDrag.SourceRow.TranslatePoint(new Point(0, 0), this);
        _todoDrag.MouseOffsetInRow = new Point(
            Math.Max(0, _todoDrag.StartPoint.X - rowOrigin.X),
            Math.Max(0, _todoDrag.StartPoint.Y - rowOrigin.Y));

        _todoDrag.SourceRow.Opacity = 0.25;
        _todoDrag.SourceRow.Background = HoverBrush;
        _todoDrag.Handle.Opacity = 0.9;
        Mouse.OverrideCursor = Cursors.SizeAll;

        _todoDrag.Ghost = CreateTodoDragGhost(_todoDrag);
        _dragLayer?.Children.Add(_todoDrag.Ghost);
        UpdateTodoDragGhost(_todoDrag, _todoDrag.StartPoint);

        ShowAppendAreaAsTrashBin(active: true);
    }

    private void OnWindowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_todoDrag == null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndTodoMouseDrag(commit: _todoDrag.IsDragging);
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

        bool overTrash = false;
        if (_appendArea != null && _appendArea.IsVisible)
        {
            try
            {
                var transform = this.TransformToVisual(_appendArea);
                Point posInAppend = transform.Transform(pointOnWindow);
                if (posInAppend.X >= 0 && posInAppend.X <= _appendArea.ActualWidth &&
                    posInAppend.Y >= 0 && posInAppend.Y <= _appendArea.ActualHeight)
                {
                    overTrash = true;
                }
            }
            catch
            {
                // Fallback in case layout is not fully updated
            }
        }

        if (overTrash)
        {
            _todoDrag.TargetId = null;
            _todoDrag.DropAtEnd = true;
            ShowAppendAreaAsTrashBin(active: true, hovered: true);
            return;
        }

        ShowAppendAreaAsTrashBin(active: true, hovered: false);

        var candidates = _todoRows
            .Where(row => row.Tag is string id && id != _todoDrag.ItemId)
            .ToList();

        if (candidates.Count == 0)
        {
            _todoDrag.TargetId = null;
            _todoDrag.DropAtEnd = false;
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
            _todoDrag.DropAtEnd = false;
            return;
        }

        ShowDropIndicator(bestRow, bestPlacement);
        _todoDrag.TargetId = bestRow.Tag as string;
        _todoDrag.TargetPlacement = bestPlacement;
        _todoDrag.DropAtEnd = false;

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
        var text = item?.Text ?? "";
        var done = item?.Done == true;

        var ghost = new Border
        {
            Width = Math.Max(state.SourceRow.ActualWidth, 160),
            MinHeight = Math.Max(state.SourceRow.ActualHeight, 30),
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(RadiusControl),
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(18, metrics.CheckColumnWidth - 4)) });

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
            FontSize = metrics.GhostTextFontSize,
            Padding = new Thickness(2, metrics.TextVerticalPadding, 2, metrics.TextVerticalPadding),
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
            FontSize = Math.Max(12, metrics.GhostTextFontSize - 1),
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

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        Mouse.OverrideCursor = null;

        CloseTodoDragGhost(state);

        state.SourceRow.Opacity = 1.0;
        state.SourceRow.Background = Brushes.Transparent;
        state.Handle.Opacity = 1.0;

        ClearActiveDropIndicator();
        ShowAppendAreaAsTrashBin(active: false);

        if (!commit)
        {
            RebuildTodoRows(state.ItemId);
            return;
        }

        if (state.DropAtEnd)
        {
            var item = _paper.Items.FirstOrDefault(i => i.Id == state.ItemId);
            if (item != null)
            {
                RemoveItem(item, rebuild: true);
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(state.TargetId))
        {
            MoveItem(state.ItemId, state.TargetId, state.TargetPlacement, focusDragged: true);
            return;
        }

        RebuildTodoRows(state.ItemId);
    }

    private void MoveItemBefore(string draggedId, string targetId, bool focusDragged = true)
    {
        MoveItem(draggedId, targetId, DropPlacement.Before, focusDragged);
    }

    private void MoveItemAfter(string draggedId, string targetId, bool focusDragged = true)
    {
        MoveItem(draggedId, targetId, DropPlacement.After, focusDragged);
    }

    private void MoveItem(string draggedId, string targetId, DropPlacement placement, bool focusDragged)
    {
        if (draggedId == targetId)
        {
            return;
        }

        var ordered = OrderedItems().ToList();
        var originalOrder = ordered.Select(i => i.Id).ToList();

        var dragged = ordered.FirstOrDefault(i => i.Id == draggedId);
        var target = ordered.FirstOrDefault(i => i.Id == targetId);

        if (dragged == null || target == null)
        {
            return;
        }

        ordered.Remove(dragged);

        var targetIndex = ordered.IndexOf(target);
        if (targetIndex < 0)
        {
            return;
        }

        if (placement == DropPlacement.After)
        {
            targetIndex++;
        }

        targetIndex = Math.Clamp(targetIndex, 0, ordered.Count);
        ordered.Insert(targetIndex, dragged);

        if (originalOrder.SequenceEqual(ordered.Select(i => i.Id)))
        {
            return;
        }

        PushUndoSnapshot();
        _paper.Items = ordered;
        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();

        RebuildTodoRows(focusDragged ? dragged.Id : null);
    }

    private void MoveItemToEnd(string draggedId, bool focusDragged = true)
    {
        var ordered = OrderedItems().ToList();
        var dragged = ordered.FirstOrDefault(i => i.Id == draggedId);
        if (dragged == null)
        {
            return;
        }

        var oldIndex = ordered.IndexOf(dragged);
        if (oldIndex == ordered.Count - 1)
        {
            return;
        }

        PushUndoSnapshot();
        ordered.Remove(dragged);
        ordered.Add(dragged);

        _paper.Items = ordered;
        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();
        RebuildTodoRows(focusDragged ? dragged.Id : null);
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

    private static bool IsBlank(PaperItem item)
    {
        return string.IsNullOrWhiteSpace(item.Text);
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
        for (var i = 0; i < _paper.Items.Count; i++)
        {
            _paper.Items[i].Order = i;
        }
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

    private static void ReplaceSelection(TextBox box, string replacement)
    {
        box.SelectedText = replacement;
        box.SelectionStart = box.SelectionStart + replacement.Length;
    }


    private static List<PaperItem> CloneItems(List<PaperItem> items)
    {
        return items.Select(i => new PaperItem
        {
            Id = i.Id,
            Text = i.Text,
            Done = i.Done,
            Order = i.Order,
            LinkedNoteId = i.LinkedNoteId
        }).ToList();
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

        _paper.Items = previousItems;
        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();

        RebuildTodoRows(focusedId);
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

        _paper.Items = nextItems;
        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();

        RebuildTodoRows(focusedId);
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_paper.Type == PaperTypes.Note)
        {
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.Z)
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


}
