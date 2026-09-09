using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private readonly Dictionary<string, TextBlock> _todoReminderCountdowns =
        new(StringComparer.Ordinal);
    private string? _pendingTodoReminderRevealItemId;

    internal void UpdateTodoReminderFeature()
    {
        if (!_controller.State.ExperimentalTodoReminders)
        {
            _pendingTodoReminderRevealItemId = null;
        }
        if (_paper.Type == PaperTypes.Todo)
        {
            RebuildTodoRows(CurrentFocusedTodoItemId());
        }
    }

    internal void RefreshTodoReminderAfterTrigger(
        IEnumerable<string> itemIds)
    {
        if (_paper.Type == PaperTypes.Todo && _todoDrag == null)
        {
            ReconcileTodoRows(itemIds);
        }
    }

    internal void PulseTodoReminderSurface()
    {
        if (!_controller.State.EnableAnimations || !IsVisible && !IsDeepCapsuleSlotVisible)
        {
            return;
        }

        if (_paper.IsCollapsed && _edgeCapsuleHost?.IsVisible == true)
        {
            _edgeCapsuleHost.PulseReminder();
            return;
        }

        if (_paperChrome != null)
        {
            AnimationHelper.QuickBounce(
                _paperChrome,
                scale: _paper.IsCollapsed ? 1.05 : 1.025,
                duration: 95);
            if (Theme.DangerBrush is SolidColorBrush danger)
            {
                AnimationHelper.FlashHighlight(
                    _paperChrome,
                    danger.Color,
                    duration: 130);
            }
        }
    }

    private void AcknowledgeTriggeredTodoReminder(
        PaperItem item,
        Border? row = null)
    {
        if (!item.ReminderTriggered)
        {
            return;
        }

        item.ReminderTriggered = false;
        item.ReminderAt = null;
        if (_todoReminderCountdowns.TryGetValue(item.Id, out var countdown))
        {
            countdown.Visibility = Visibility.Collapsed;
        }
        _controller.NotifyTodoReminderChanged(saveImmediately: false);
        if (row != null)
        {
            UpdateTodoRowBackground(row);
        }
    }

    internal void OpenTodoReminderItem(string itemId)
    {
        if (_paper.Type != PaperTypes.Todo ||
            !_paper.Items.Any(item =>
                string.Equals(
                    item.Id,
                    itemId,
                    StringComparison.Ordinal)))
        {
            return;
        }

        _pendingFocusItemId = itemId;
        if (_paper.IsCollapsed)
        {
            OpenCapsuleForEditing();
            return;
        }

        FocusTodoItem(itemId);
        _controller.BringPaperToFront(_paper);
    }

    internal void RefreshTodoReminderCountdowns(DateTimeOffset now)
    {
        if (_paper.Type != PaperTypes.Todo ||
            !_controller.State.ExperimentalTodoReminders ||
            _todoReminderCountdowns.Count == 0)
        {
            return;
        }

        foreach (var (itemId, countdown) in _todoReminderCountdowns)
        {
            var item = _paper.Items.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
            if (item?.ReminderAt is not { } reminderAt || item.Done)
            {
                continue;
            }

            countdown.Text = TodoReminderCountdownText(reminderAt, now);
            countdown.ToolTip = TodoReminderToolTip(item);
        }
    }

    private Border BuildTodoReminderButton(
        PaperItem item,
        TodoVisualMetrics metrics)
    {
        var glyph = new VectorPrimitiveIconElement(VectorPrimitiveIconKind.Clock)
        {
            Foreground = WeakTextBrush,
            Opacity = 0.44,
            IconSize = Math.Max(
                AppTypography.Scale(10.5),
                metrics.TextFontSize - AppTypography.Scale(1.5)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var button = new Border
        {
            Width = Math.Max(
                AppTypography.Scale(17),
                metrics.CheckColumnWidth - AppTypography.Scale(6)),
            MinHeight = metrics.RowMinHeight,
            Margin = new Thickness(1, 0, 1, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(RadiusSmall),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = glyph,
            Visibility = item.Done ? Visibility.Hidden : Visibility.Visible,
            ToolTip = Strings.Get("TodoReminderSet")
        };

        button.MouseEnter += (_, _) =>
        {
            button.Background = HoverBrush;
            glyph.Opacity = 1.0;
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = Brushes.Transparent;
            glyph.Opacity = 0.44;
        };
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            glyph.Opacity = 0.66;
            e.Handled = true;
        };
        button.PreviewMouseLeftButtonUp += (_, e) =>
        {
            glyph.Opacity = 1.0;
            OpenTodoReminderMenu(button, item.Id);
            e.Handled = true;
        };
        return button;
    }

    private Border BuildTodoReminderCountdown(
        PaperItem item,
        TodoVisualMetrics metrics)
    {
        var countdown = new TextBlock
        {
            Text = TodoReminderCountdownText(
                item.ReminderAt!.Value,
                DateTimeOffset.Now),
            Foreground = Theme.ActiveBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = Math.Max(
                AppTypography.Scale(10),
                metrics.TextFontSize - AppTypography.Scale(2)),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = TodoReminderToolTip(item)
        };
        _todoReminderCountdowns[item.Id] = countdown;
        var host = new Border
        {
            MinWidth = Math.Max(
                AppTypography.Scale(24),
                metrics.CheckColumnWidth),
            MinHeight = metrics.RowMinHeight,
            Margin = new Thickness(1, 0, 1, 0),
            Padding = new Thickness(3, 0, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(RadiusSmall),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = countdown,
            Visibility = item.Done ? Visibility.Hidden : Visibility.Visible
        };
        host.MouseEnter += (_, _) =>
        {
            host.Background = HoverBrush;
            countdown.Foreground = TextBrush;
        };
        host.MouseLeave += (_, _) =>
        {
            host.Background = Brushes.Transparent;
            countdown.Foreground = Theme.ActiveBrush;
            countdown.Opacity = 1.0;
        };
        host.PreviewMouseLeftButtonDown += (_, e) =>
        {
            countdown.Opacity = 0.66;
            e.Handled = true;
        };
        host.PreviewMouseLeftButtonUp += (_, e) =>
        {
            countdown.Opacity = 1.0;
            OpenTodoReminderMenu(host, item.Id);
            e.Handled = true;
        };

        if (_controller.State.EnableAnimations &&
            string.Equals(
                _pendingTodoReminderRevealItemId,
                item.Id,
                StringComparison.Ordinal))
        {
            _pendingTodoReminderRevealItemId = null;
            host.Opacity = 0;
            host.Loaded += (_, _) =>
            {
                AnimationHelper.FadeIn(host, duration: 120);
                AnimationHelper.QuickBounce(
                    host,
                    scale: 1.06,
                    duration: 80);
            };
        }
        return host;
    }

    private static string TodoReminderCountdownText(
        DateTimeOffset reminderAt,
        DateTimeOffset now)
    {
        var remaining = reminderAt - now;
        if (remaining <= TimeSpan.Zero)
        {
            return Strings.Get("TodoReminderCountdownNow");
        }

        if (remaining.TotalMinutes < 60)
        {
            return Strings.Format(
                "TodoReminderCountdownMinutesFormat",
                Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)));
        }

        if (remaining.TotalHours < 24)
        {
            return Strings.Format(
                "TodoReminderCountdownHoursFormat",
                Math.Max(1, (int)Math.Ceiling(remaining.TotalHours)));
        }

        return Strings.Format(
            "TodoReminderCountdownDaysFormat",
            Math.Max(1, (int)Math.Ceiling(remaining.TotalDays)));
    }

    private string TodoReminderToolTip(PaperItem item)
    {
        return item.ReminderAt is { } reminderAt
            ? Strings.Format(
                "TodoReminderSetForFormat",
                reminderAt.ToLocalTime().ToString(
                    "g",
                    UiLanguages.EffectiveCulture))
            : Strings.Get("TodoReminderSet");
    }

    private void OpenTodoReminderMenu(
        FrameworkElement placementTarget,
        string itemId)
    {
        if (!_controller.State.ExperimentalTodoReminders)
        {
            return;
        }

        var item = _paper.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        if (item == null || item.Done)
        {
            return;
        }

        var menu = CreateContextMenu();
        if (!PopulateTodoReminderMenu(
                menu,
                itemId,
                includeHeader: true))
        {
            return;
        }

        var previousContextMenu = placementTarget.ContextMenu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(placementTarget.ContextMenu, menu))
            {
                placementTarget.ContextMenu = previousContextMenu;
            }
        };
        placementTarget.ContextMenu = menu;
        menu.PlacementTarget = placementTarget;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private MenuItem BuildTodoReminderContextMenuItem(string itemId)
    {
        var root = new MenuItem
        {
            Header = Strings.Get("TodoReminderSet"),
            Padding = new Thickness(8, 4, 10, 4),
            Background = Brushes.Transparent
        };
        root.SetResourceReference(
            Control.ForegroundProperty,
            "TextBrushKey");
        root.Items.Add(MenuHeader(Strings.Get("TodoReminderMenuHeader")));
        root.SubmenuOpened += (_, _) =>
        {
            root.Items.Clear();
            if (!PopulateTodoReminderMenu(
                    root,
                    itemId,
                    includeHeader: false))
            {
                root.Items.Add(MenuHeader(
                    Strings.Get("TodoReminderMenuUnavailable")));
            }
        };
        return root;
    }

    private bool PopulateTodoReminderMenu(
        ItemsControl menu,
        string itemId,
        bool includeHeader)
    {
        if (!_controller.State.ExperimentalTodoReminders)
        {
            return false;
        }

        var item = _paper.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        if (item == null || item.Done)
        {
            return false;
        }

        if (includeHeader)
        {
            menu.Items.Add(MenuHeader(
                Strings.Get("TodoReminderMenuHeader")));
        }

        var now = DateTimeOffset.Now;
        var defaultMinutes =
            ExperimentalTodoReminderOptions.NormalizeQuickMinutes(
                _controller.State.ExperimentalTodoReminderQuickMinutes);
        var minutePresets = new[]
        {
            defaultMinutes,
            10,
            30,
            60
        }.Distinct().ToArray();
        foreach (var minutes in minutePresets)
        {
            var label = Strings.Format("TodoReminderInMinutesFormat", minutes);
            if (minutes == defaultMinutes)
            {
                label += Strings.Get("TodoReminderDefaultSuffix");
            }

            var reminderAt = now.AddMinutes(minutes);
            menu.Items.Add(MenuItem(
                label,
                (_, _) => QueueTodoReminderChange(itemId, reminderAt)));
        }

        menu.Items.Add(MenuSeparator());
        var todayEvening = LocalReminderTime(now.LocalDateTime.Date, 18, 0);
        if (todayEvening is { } evening && evening > now)
        {
            menu.Items.Add(MenuItem(
                Strings.Format(
                    "TodoReminderPresetAtFormat",
                    evening.ToLocalTime().ToString(
                        "ddd HH:mm",
                        UiLanguages.EffectiveCulture)),
                (_, _) => QueueTodoReminderChange(itemId, evening)));
        }

        var tomorrowMorning = LocalReminderTime(
            now.LocalDateTime.Date.AddDays(1),
            9,
            0);
        if (tomorrowMorning is { } morning)
        {
            menu.Items.Add(MenuItem(
                Strings.Format(
                    "TodoReminderPresetAtFormat",
                    morning.ToLocalTime().ToString(
                        "ddd HH:mm",
                        UiLanguages.EffectiveCulture)),
                (_, _) => QueueTodoReminderChange(itemId, morning)));
        }

        var customInitial = item.ReminderAt is { } existing && existing > now
            ? existing
            : now.AddMinutes(defaultMinutes);
        menu.Items.Add(MenuItem(
            Strings.Get("TodoReminderCustom"),
            (_, _) => Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    if (IsClosed ||
                        !TodoReminderDialog.TryShow(
                            this,
                            customInitial,
                            _controller.State.EnableAnimations,
                            out var customReminder))
                    {
                        return;
                    }

                    SetTodoItemReminder(itemId, customReminder);
                }),
                DispatcherPriority.Background)));

        if (item.ReminderAt.HasValue)
        {
            menu.Items.Add(MenuSeparator());
            menu.Items.Add(MenuItem(
                Strings.Get("TodoReminderClear"),
                (_, _) => QueueTodoReminderChange(itemId, null)));
        }

        return true;
    }

    private void QueueTodoReminderChange(
        string itemId,
        DateTimeOffset? reminderAt)
    {
        _ = Dispatcher.BeginInvoke(
            (Action)(() => SetTodoItemReminder(itemId, reminderAt)),
            DispatcherPriority.Background);
    }

    private void SetTodoItemReminder(
        string itemId,
        DateTimeOffset? reminderAt)
    {
        if (!_controller.State.ExperimentalTodoReminders)
        {
            return;
        }

        var item = _paper.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        if (item == null ||
            item.Done ||
            (reminderAt.HasValue && reminderAt.Value <= DateTimeOffset.Now) ||
            item.ReminderAt == reminderAt)
        {
            return;
        }

        PushUndoSnapshot();
        item.ReminderAt = reminderAt;
        item.ReminderTriggered = false;
        _pendingTodoReminderRevealItemId =
            reminderAt.HasValue && _controller.State.EnableAnimations
                ? itemId
                : null;
        _controller.NotifyTodoReminderChanged(saveImmediately: true);
        ReconcileTodoRows([item.Id], item.Id);
    }

    private static DateTimeOffset? LocalReminderTime(
        DateTime date,
        int hour,
        int minute)
    {
        var local = DateTime.SpecifyKind(
            date.Date.AddHours(hour).AddMinutes(minute),
            DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(local))
        {
            return null;
        }

        return new DateTimeOffset(
            local,
            TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
