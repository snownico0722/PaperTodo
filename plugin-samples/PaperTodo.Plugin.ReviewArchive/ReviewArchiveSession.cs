using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PaperTodo.Plugin;
using static PaperTodo.Plugin.ReviewArchive.ReviewArchiveSettingsReader;

namespace PaperTodo.Plugin.ReviewArchive;

internal sealed class ReviewArchiveSession : IPaperBodySession
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly PaperBodyContext _context;
    private readonly Grid _root;
    private readonly TextBlock _summaryText;
    private readonly TextBlock _hintText;
    private readonly WrapPanel _insightsPanel;
    private readonly TextBlock _todayValue;
    private readonly TextBlock _weekValue;
    private readonly TextBlock _streakValue;
    private readonly TextBlock _openValue;
    private readonly ComboBox _filterBox;
    private readonly TextBox _searchBox;
    private readonly ListBox _list;
    private readonly TextBlock _emptyText;
    private readonly Button _importButton;
    private readonly Button _exportButton;
    private readonly Button _clearButton;
    private readonly List<Button> _buttons;
    private readonly DispatcherTimer _refreshTimer;

    private ReviewArchiveSettings _settings;
    private ReviewArchiveViewState _viewState;
    private PaperBodyTheme _theme;
    private bool _disposed;
    private bool _compactLayout;

    public ReviewArchiveSession(PaperBodyContext context)
    {
        _context = context;
        _theme = context.Theme;
        _settings = ReadSettings(context.SettingsJson);
        _viewState = ReadViewState(context.StateJson, _settings.DefaultFilter);
        ReviewArchiveStore.EnsureLoaded();

        _summaryText = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = 18
        };
        _hintText = new TextBlock
        {
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };

        _insightsPanel = new WrapPanel
        {
            Margin = new Thickness(0, 10, 0, 0)
        };
        var todayCard = CreateInsightCard("今日");
        var weekCard = CreateInsightCard("近 7 天");
        var streakCard = CreateInsightCard("连续");
        var openCard = CreateInsightCard("进行中");
        _todayValue = todayCard.Value;
        _weekValue = weekCard.Value;
        _streakValue = streakCard.Value;
        _openValue = openCard.Value;
        _insightsPanel.Children.Add(todayCard.Card);
        _insightsPanel.Children.Add(weekCard.Card);
        _insightsPanel.Children.Add(streakCard.Card);
        _insightsPanel.Children.Add(openCard.Card);

        var heading = new StackPanel();
        heading.Children.Add(_summaryText);
        heading.Children.Add(_hintText);
        heading.Children.Add(_insightsPanel);

        _filterBox = new ComboBox
        {
            Width = 122,
            Margin = new Thickness(0, 0, 8, 0),
            ItemsSource = new[]
            {
                new FilterOption("completed", "已完成"),
                new FilterOption("today", "今天完成"),
                new FilterOption("week", "近 7 天"),
                new FilterOption("month", "近 30 天"),
                new FilterOption("open", "进行中"),
                new FilterOption("reopened", "重新打开"),
                new FilterOption("reminders", "有提醒"),
                new FilterOption("deleted", "源已删除"),
                new FilterOption("all", "全部记录")
            },
            DisplayMemberPath = nameof(FilterOption.Name),
            SelectedValuePath = nameof(FilterOption.Id),
            SelectedValue = _viewState.Filter
        };
        _filterBox.SelectionChanged += (_, _) =>
        {
            _viewState.Filter = _filterBox.SelectedValue as string ?? "completed";
            SaveViewState();
            Refresh();
        };

        _searchBox = new TextBox
        {
            MinWidth = 120,
            MaxWidth = 260,
            Height = 30,
            Padding = new Thickness(8, 4, 8, 4),
            Text = _viewState.Search,
            ToolTip = "搜索待办正文或所属纸片"
        };
        _searchBox.TextChanged += (_, _) =>
        {
            _viewState.Search = _searchBox.Text.Trim();
            SaveViewState();
            Refresh();
        };

        var filters = new WrapPanel
        {
            Margin = new Thickness(0, 12, 0, 10)
        };
        filters.Children.Add(_filterBox);
        filters.Children.Add(_searchBox);

        _emptyText = new TextBlock
        {
            Text = "当前筛选没有记录",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20),
            TextAlignment = TextAlignment.Center
        };
        _list = CreateVirtualizedList();

        _importButton = MakeButton("导入当前");
        _exportButton = MakeButton("导出 CSV");
        _clearButton = MakeButton("清空记录");
        _buttons = [_importButton, _exportButton, _clearButton];
        _importButton.Click += OnImport;
        _exportButton.Click += OnExport;
        _clearButton.Click += OnClear;

        var actions = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        actions.Children.Add(_importButton);
        actions.Children.Add(_exportButton);
        actions.Children.Add(_clearButton);

        _root = new Grid
        {
            Margin = new Thickness(16, 13, 16, 14),
            Background = Brushes.Transparent
        };
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition());
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(heading, 0);
        Grid.SetRow(filters, 1);
        Grid.SetRow(_list, 2);
        Grid.SetRow(actions, 3);
        _root.Children.Add(heading);
        _root.Children.Add(filters);
        _root.Children.Add(_list);
        _root.Children.Add(actions);
        _root.SizeChanged += (_, e) =>
            ApplyResponsiveLayout(e.NewSize.Width);

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            if (!_disposed)
            {
                Refresh();
            }
        };

        ReviewArchiveStore.Changed += OnArchiveChanged;

        ReviewArchiveStore.ApplyRetention(_settings);
        ApplyTheme(context.Body.Theme);
        Refresh();
    }

    public FrameworkElement View => _root;

    private static ListBox CreateVirtualizedList()
    {
        var list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetCanContentScroll(list, true);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);

        var itemRoot = new FrameworkElementFactory(typeof(Border));
        itemRoot.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "Content");
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        itemRoot.AppendChild(presenter);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.FocusableProperty, false));
        itemStyle.Setters.Add(new Setter(Control.IsTabStopProperty, false));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(
            Control.HorizontalContentAlignmentProperty,
            HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(
            Control.TemplateProperty,
            new ControlTemplate(typeof(ListBoxItem)) { VisualTree = itemRoot }));
        list.ItemContainerStyle = itemStyle;
        list.ItemsPanel = new ItemsPanelTemplate(
            new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        return list;
    }

    private void ApplyResponsiveLayout(double width)
    {
        var compact = width < 310;
        if (_compactLayout == compact)
        {
            return;
        }

        _compactLayout = compact;
        _root.Margin = compact
            ? new Thickness(8, 8, 8, 10)
            : new Thickness(16, 13, 16, 14);
        _filterBox.Width = compact ? 106 : 122;
        _searchBox.MaxWidth = compact ? 180 : 260;
    }

    private sealed record FilterOption(string Id, string Name);

    private static (Border Card, TextBlock Value) CreateInsightCard(string label)
    {
        var value = new TextBlock
        {
            Text = "0",
            FontWeight = FontWeights.SemiBold,
            FontSize = 15
        };
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 9.5,
            Margin = new Thickness(0, 2, 0, 0)
        };
        var panel = new StackPanel();
        panel.Children.Add(value);
        panel.Children.Add(labelText);
        return (new Border
        {
            MinWidth = 68,
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(0, 0, 6, 6),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = panel
        }, value);
    }

    private static Button MakeButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(12, 5, 12, 5),
        Margin = new Thickness(6, 0, 0, 0),
        MinHeight = 30,
        BorderThickness = new Thickness(1),
        Cursor = System.Windows.Input.Cursors.Hand
    };

    private static DateTimeOffset LastActivityAt(ReviewArchiveRecord record) =>
        record.Events.Count > 0
            ? record.Events.Max(value => value.At)
            : record.CompletedAt ?? record.DeletedAt ?? record.ReminderChangedAt ?? record.LastChangedAt;

    private static bool HasCompletionSince(ReviewArchiveRecord record, DateTimeOffset since) =>
        record.Events.Any(value => value.Kind == "completed" && value.At >= since);

    private static DateTimeOffset? LastEventAt(ReviewArchiveRecord record, string kind) =>
        record.Events
            .Where(value => value.Kind == kind)
            .Select(value => (DateTimeOffset?)value.At)
            .Max();

    private static int EventCount(ReviewArchiveRecord record, string kind) =>
        record.Events.Count(value => value.Kind == kind);

    private static bool IsActiveReminder(ReviewArchiveRecord record, DateTimeOffset now) =>
        !record.Done &&
        !record.SourceDeleted &&
        record.ReminderAt.HasValue &&
        record.ReminderAt.Value > now;

    private static bool IsUpcomingReminder(ReviewArchiveRecord record, DateTimeOffset now) =>
        IsActiveReminder(record, now) &&
        record.ReminderAt!.Value <= now.AddHours(24);

    private static int CompletionStreakDays(IEnumerable<ReviewArchiveEvent> completionEvents)
    {
        var days = completionEvents
            .Select(value => value.At.ToLocalTime().Date)
            .ToHashSet();
        if (days.Count == 0)
        {
            return 0;
        }

        var cursor = DateTime.Now.Date;
        if (!days.Contains(cursor))
        {
            cursor = cursor.AddDays(-1);
        }

        var streak = 0;
        while (days.Contains(cursor))
        {
            streak++;
            cursor = cursor.AddDays(-1);
        }
        return streak;
    }

    private void OnArchiveChanged()
    {
        if (_disposed)
        {
            return;
        }
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void Refresh()
    {
        var all = ReviewArchiveStore.Snapshot();
        var now = DateTimeOffset.Now;
        var completionEvents = all
            .SelectMany(item => item.Events.Where(value => value.Kind == "completed"))
            .ToArray();
        var completedRecords = all.Count(item =>
            item.Events.Any(value => value.Kind == "completed"));
        var todayCount = completionEvents.Count(value =>
            value.At.ToLocalTime().Date == now.Date);
        var weekCount = completionEvents.Count(value =>
            value.At >= now.AddDays(-7));
        var openCount = all.Count(item => !item.Done && !item.SourceDeleted);
        var reopenedCount = all.Sum(item => EventCount(item, "reopened"));
        var streak = CompletionStreakDays(completionEvents);
        var upcomingCount = all.Count(item => IsUpcomingReminder(item, now));

        _summaryText.Text =
            $"完成 {completedRecords} 项 / {completionEvents.Length} 次 · 重新打开 {reopenedCount} 次";
        _todayValue.Text = todayCount.ToString();
        _weekValue.Text = weekCount.ToString();
        _streakValue.Text = streak > 0 ? streak + " 天" : "0";
        _openValue.Text = openCount.ToString();
        _insightsPanel.Visibility = _settings.ShowInsights
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(ReviewArchiveStore.LastSaveError))
        {
            _hintText.Text = "记录池暂时无法写入：" + ReviewArchiveStore.LastSaveError;
        }
        else if (upcomingCount > 0)
        {
            _hintText.Text = $"未来 24 小时有 {upcomingCount} 个待办提醒；记录池独立保存在插件 .runtime 中。";
        }
        else
        {
            _hintText.Text = "记录池独立保存在插件 .runtime 中；删除原待办纸片后仍可复盘和导出。";
        }

        var filtered = Filter(all)
            .OrderByDescending(LastActivityAt)
            .Take(300)
            .ToArray();
        _list.ItemsSource = filtered.Length == 0
            ? new object[] { _emptyText }
            : filtered.Select(record => (object)BuildRow(record)).ToArray();

        ApplyTheme(_theme);
    }

    private IEnumerable<ReviewArchiveRecord> Filter(IEnumerable<ReviewArchiveRecord> records)
    {
        var now = DateTimeOffset.Now;
        var filter = _viewState.Filter;
        var result = records.Where(item => filter switch
        {
            "today" => item.Events.Any(value =>
                value.Kind == "completed" &&
                value.At.ToLocalTime().Date == now.Date),
            "week" => HasCompletionSince(item, now.AddDays(-7)),
            "month" => HasCompletionSince(item, now.AddDays(-30)),
            "open" => !item.Done && !item.SourceDeleted,
            "reopened" => item.Events.Any(value => value.Kind == "reopened"),
            "reminders" => !item.Done && !item.SourceDeleted && item.ReminderAt.HasValue,
            "deleted" => item.SourceDeleted,
            "all" => true,
            _ => item.Events.Any(value => value.Kind == "completed")
        });

        if (!_settings.ShowOpenItems && filter == "all")
        {
            result = result.Where(item =>
                item.Events.Any(value => value.Kind == "completed") ||
                item.SourceDeleted ||
                item.Events.Any(value => value.Kind == "reopened") ||
                item.ReminderAt.HasValue);
        }

        var search = _viewState.Search;
        if (search.Length > 0)
        {
            result = result.Where(item =>
                item.Text.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                item.PaperTitle.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        }
        return result;
    }

    private Border BuildRow(ReviewArchiveRecord record)
    {
        var now = DateTimeOffset.Now;
        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(record.Text) ? "（空待办）" : record.Text,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = record.Done ? FontWeights.Normal : FontWeights.SemiBold
        };

        var metadata = new List<string>();
        if (_settings.IncludePaperTitle && !string.IsNullOrWhiteSpace(record.PaperTitle))
        {
            metadata.Add(record.PaperTitle);
        }
        var lastCompleted = LastEventAt(record, "completed");
        var lastReopened = LastEventAt(record, "reopened");
        metadata.Add(record.Done && lastCompleted.HasValue
            ? "完成 " + FormatDate(lastCompleted.Value)
            : lastReopened.HasValue
                ? "重新打开 " + FormatDate(lastReopened.Value)
                : "创建 " + FormatDate(record.CreatedAt));

        var completionCount = EventCount(record, "completed");
        if (completionCount > 1)
        {
            metadata.Add($"完成 {completionCount} 次");
        }
        var reopenedCount = EventCount(record, "reopened");
        if (reopenedCount > 1)
        {
            metadata.Add($"重开 {reopenedCount} 次");
        }

        if (record.ReminderAt.HasValue && !record.Done && !record.SourceDeleted)
        {
            var reminder = record.ReminderAt.Value;
            metadata.Add(reminder <= now
                ? "提醒已到期 " + FormatDate(reminder)
                : "提醒 " + FormatDate(reminder));
        }
        if (_settings.ShowReminderChanges)
        {
            var reminderChanges =
                EventCount(record, "reminder-set") +
                EventCount(record, "reminder-cleared");
            if (reminderChanges > 1)
            {
                metadata.Add($"提醒调整 {reminderChanges} 次");
            }
        }
        if (record.Events.Any(value => value.Estimated))
        {
            metadata.Add("时间为首次观察值");
        }
        if (_settings.ShowDeletedBadge && record.SourceDeleted)
        {
            metadata.Add("源已删除");
        }

        var detail = new TextBlock
        {
            Text = string.Join(" · ", metadata),
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap
        };

        var panel = new StackPanel();
        panel.Children.Add(title);
        panel.Children.Add(detail);
        var row = new Border
        {
            Child = panel,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7)
        };
        if (record.ReminderAt.HasValue && !record.Done && !record.SourceDeleted)
        {
            row.Tag = record.ReminderAt.Value <= now
                ? "reminder-overdue"
                : IsUpcomingReminder(record, now)
                    ? "reminder-upcoming"
                    : null;
        }
        return row;
    }

    private string FormatDate(DateTimeOffset value) =>
        _settings.ExportDateFormat == "iso"
            ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var changed = ReviewArchiveStore.ImportCurrent(_context.Workspace, _settings, manual: true);
        if (!changed)
        {
            _hintText.Text = "当前待办已经全部存在于记录池中。";
        }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var records = Filter(ReviewArchiveStore.Snapshot())
            .OrderByDescending(LastActivityAt)
            .ToArray();
        var dialog = new SaveFileDialog
        {
            Title = "导出 PaperTodo 复盘记录",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"PaperTodo-复盘-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            AddExtension = true,
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var csv = BuildCsv(records);
            var encoding = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: _settings.ExportEncoding == "utf8bom");
            File.WriteAllText(dialog.FileName, csv, encoding);
            _hintText.Text = $"已导出 {records.Length} 条：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.GetBaseException().Message,
                "导出失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private string BuildCsv(IReadOnlyList<ReviewArchiveRecord> records)
    {
        var builder = new StringBuilder();
        var headers = new List<string> { "状态", "待办" };
        if (_settings.IncludePaperTitle)
        {
            headers.Add("所属纸片");
        }
        headers.AddRange([
            "创建时间",
            "最后完成时间",
            "完成次数",
            "最后重新打开",
            "提醒时间",
            "提醒变更次数",
            "源已删除",
            "创建时间精度",
            "完成时间精度",
            "来源"]);
        builder.AppendLine(string.Join(',', headers.Select(Csv)));

        foreach (var record in records)
        {
            var row = new List<string>
            {
                record.Done ? "已完成" : "进行中",
                record.Text
            };
            if (_settings.IncludePaperTitle)
            {
                row.Add(record.PaperTitle);
            }
            var lastCompleted = LastEventAt(record, "completed");
            var lastReopened = LastEventAt(record, "reopened");
            var reminderChanges =
                EventCount(record, "reminder-set") +
                EventCount(record, "reminder-cleared");
            row.Add(FormatDate(record.CreatedAt));
            row.Add(lastCompleted.HasValue ? FormatDate(lastCompleted.Value) : "");
            row.Add(EventCount(record, "completed").ToString());
            row.Add(lastReopened.HasValue ? FormatDate(lastReopened.Value) : "");
            row.Add(record.ReminderAt.HasValue ? FormatDate(record.ReminderAt.Value) : "");
            row.Add(reminderChanges.ToString());
            row.Add(record.SourceDeleted ? "是" : "否");
            row.Add(record.CreatedAtEstimated ? "首次观察" : "精确");
            row.Add(record.CompletedAtEstimated ? "首次观察" : record.CompletedAt.HasValue ? "精确" : "");
            row.Add(record.Origin);
            builder.AppendLine(string.Join(',', row.Select(Csv)));
        }
        return builder.ToString();
    }

    private static string Csv(string value) =>
        '"' + (value ?? "").Replace("\"", "\"\"") + '"';

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (_settings.ConfirmClear &&
            MessageBox.Show(
                "确定清空全部复盘记录吗？此操作不会删除 PaperTodo 中的待办。",
                "清空复盘记录",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        ReviewArchiveStore.Clear(_settings);
    }

    private void ApplyTheme(PaperBodyTheme theme)
    {
        _theme = theme;
        var scale = Math.Clamp(theme.FontScale, 0.7, 2.5);
        var text = Brush(theme.TextColor, "#202020");
        var weak = Brush(theme.WeakTextColor, "#707070");
        var border = Brush(theme.BorderColor, "#807050");
        var surface = Brush(theme.IsDark ? "#18FFFFFF" : "#0C000000", "#0C000000");
        var accent = Brush(theme.AccentColor, "#B07A31");
        var accentSurface = Brush(AddAlpha(theme.AccentColor, theme.IsDark ? (byte)46 : (byte)26), "#1AB07A31");
        var overdueSurface = Brush(theme.IsDark ? "#28D06A5F" : "#18C44B42", "#18C44B42");
        var overdueBorder = Brush(theme.IsDark ? "#FFD97B72" : "#FFC44B42", "#FFC44B42");
        var font = new FontFamily(theme.FontFamily);

        _summaryText.Foreground = text;
        _summaryText.FontFamily = font;
        _summaryText.FontSize = 18 * scale;
        _hintText.Foreground = weak;
        _hintText.FontFamily = font;
        _hintText.FontSize = 11 * scale;
        _emptyText.Foreground = weak;
        _emptyText.FontFamily = font;
        _searchBox.Foreground = text;
        _searchBox.Background = surface;
        _searchBox.BorderBrush = border;
        _context.Body.Controls.ApplySelectStyle(_filterBox, 11.5 * scale);

        foreach (var card in _insightsPanel.Children.OfType<Border>())
        {
            card.BorderBrush = border;
            card.Background = surface;
            foreach (var block in Descendants<TextBlock>(card))
            {
                var isValue = block.FontWeight == FontWeights.SemiBold;
                block.Foreground = isValue ? text : weak;
                block.FontFamily = font;
                block.FontSize = (isValue ? 15 : 9.5) * scale;
            }
        }

        foreach (var button in _buttons)
        {
            button.Foreground = text;
            button.Background = surface;
            button.BorderBrush = border;
            button.FontFamily = font;
            button.FontSize = 11.5 * scale;
        }

        foreach (var row in _list.Items.OfType<Border>())
        {
            var reminderTag = row.Tag as string;
            if (_settings.HighlightUpcomingReminders && reminderTag == "reminder-overdue")
            {
                row.BorderBrush = overdueBorder;
                row.Background = overdueSurface;
            }
            else if (_settings.HighlightUpcomingReminders && reminderTag == "reminder-upcoming")
            {
                row.BorderBrush = accent;
                row.Background = accentSurface;
            }
            else
            {
                row.BorderBrush = border;
                row.Background = surface;
            }
            foreach (var block in Descendants<TextBlock>(row))
            {
                block.Foreground = block.FontSize <= 10.5 ? weak : text;
                block.FontFamily = font;
            }
        }
    }

    private static string AddAlpha(string value, byte alpha)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(value)!;
            return $"#{alpha:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        catch
        {
            return "#1AB07A31";
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static SolidColorBrush Brush(string value, string fallback)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)!);
        }
        catch
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback)!);
        }
    }

    private void SaveViewState() =>
        _context.SaveStateJson(JsonSerializer.Serialize(_viewState, JsonOptions));

    public void OnSettingsChanged(string settingsJson)
    {
        _settings = ReadSettings(settingsJson);
        ReviewArchiveStore.ApplyRetention(_settings);
        Refresh();
    }

    public void OnThemeChanged(PaperBodyTheme theme) => ApplyTheme(theme);

    public void OnTypographyChanged(PaperBodyTheme theme) => OnThemeChanged(theme);

    public void OnPresentationChanged(bool visible) => _root.IsHitTestVisible = visible;

    public void CancelInteractions()
    {
        _filterBox.IsDropDownOpen = false;
    }

    public void Commit()
    {
        SaveViewState();
        ReviewArchiveStore.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _refreshTimer.Stop();
        ReviewArchiveStore.Changed -= OnArchiveChanged;
        ReviewArchiveStore.Flush();
    }
}
