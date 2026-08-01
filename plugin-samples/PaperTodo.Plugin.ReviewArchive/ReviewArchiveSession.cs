using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private readonly ComboBox _filterBox;
    private readonly TextBox _searchBox;
    private readonly StackPanel _listPanel;
    private readonly TextBlock _emptyText;
    private readonly Button _importButton;
    private readonly Button _exportButton;
    private readonly Button _clearButton;
    private readonly List<Button> _buttons;
    private readonly IDisposable _subscription;

    private ReviewArchiveSettings _settings;
    private ReviewArchiveViewState _viewState;
    private PaperBodyTheme _theme;
    private bool _disposed;
    private string _lastDisplayTitle = "";

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

        var heading = new StackPanel();
        heading.Children.Add(_summaryText);
        heading.Children.Add(_hintText);

        _filterBox = new ComboBox
        {
            Width = 112,
            Margin = new Thickness(0, 0, 8, 0),
            ItemsSource = new[]
            {
                new FilterOption("completed", "已完成"),
                new FilterOption("today", "今天完成"),
                new FilterOption("week", "近 7 天"),
                new FilterOption("month", "近 30 天"),
                new FilterOption("open", "进行中"),
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
            MaxWidth = 240,
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

        var filters = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 10)
        };
        filters.Children.Add(_filterBox);
        filters.Children.Add(_searchBox);

        _listPanel = new StackPanel();
        _emptyText = new TextBlock
        {
            Text = "当前筛选没有记录",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20),
            TextAlignment = TextAlignment.Center
        };
        var scroll = new ScrollViewer
        {
            Content = _listPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        _importButton = MakeButton("导入当前");
        _exportButton = MakeButton("导出 CSV");
        _clearButton = MakeButton("清空记录");
        _buttons = [_importButton, _exportButton, _clearButton];
        _importButton.Click += OnImport;
        _exportButton.Click += OnExport;
        _clearButton.Click += OnClear;

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
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
        Grid.SetRow(scroll, 2);
        Grid.SetRow(actions, 3);
        _root.Children.Add(heading);
        _root.Children.Add(filters);
        _root.Children.Add(scroll);
        _root.Children.Add(actions);

        _subscription = context.Host.Subscribe(
            new PaperTodoEventFilter
            {
                Kinds = new HashSet<PaperTodoEventKind>
                {
                    PaperTodoEventKind.PaperChanged,
                    PaperTodoEventKind.PaperDeleted,
                    PaperTodoEventKind.TodoCreated,
                    PaperTodoEventKind.TodoChanged,
                    PaperTodoEventKind.TodoDeleted
                }
            },
            value => ReviewArchiveStore.Apply(value, _settings));
        ReviewArchiveStore.Changed += OnArchiveChanged;

        _ = ReviewArchiveStore.ImportCurrent(context.Host, _settings, manual: false);
        ReviewArchiveStore.ApplyRetention(_settings);
        ApplyTheme(context.Theme);
        Refresh();
    }

    public FrameworkElement View => _root;

    private sealed record FilterOption(string Id, string Name);

    private static Button MakeButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(12, 5, 12, 5),
        Margin = new Thickness(6, 0, 0, 0),
        MinHeight = 30,
        BorderThickness = new Thickness(1),
        Cursor = System.Windows.Input.Cursors.Hand
    };

    private void OnArchiveChanged()
    {
        if (_disposed)
        {
            return;
        }
        Refresh();
    }

    private void Refresh()
    {
        var all = ReviewArchiveStore.Snapshot();
        var today = DateTimeOffset.Now.Date;
        var completed = all.Where(item => item.Done && item.CompletedAt.HasValue).ToArray();
        var todayCount = completed.Count(item => item.CompletedAt!.Value.ToLocalTime().Date == today);
        var weekCount = completed.Count(item => item.CompletedAt >= DateTimeOffset.Now.AddDays(-7));
        _summaryText.Text = $"已完成 {completed.Length} · 今日 {todayCount} · 近 7 天 {weekCount}";
        _hintText.Text = string.IsNullOrWhiteSpace(ReviewArchiveStore.LastSaveError)
            ? "记录池独立保存在插件 .runtime 中；删除原待办纸片后仍可复盘和导出。"
            : "记录池暂时无法写入：" + ReviewArchiveStore.LastSaveError;

        var filtered = Filter(all)
            .OrderByDescending(item => item.CompletedAt ?? item.LastChangedAt)
            .Take(300)
            .ToArray();
        _listPanel.Children.Clear();
        if (filtered.Length == 0)
        {
            _listPanel.Children.Add(_emptyText);
        }
        else
        {
            foreach (var record in filtered)
            {
                _listPanel.Children.Add(BuildRow(record));
            }
        }

        var title = _settings.TitleMode switch
        {
            "today" => $"今日完成 {todayCount}",
            "fixed" => string.IsNullOrWhiteSpace(_settings.FixedTitle)
                ? "复盘记录"
                : _settings.FixedTitle,
            _ => $"复盘 · {completed.Length} 条"
        };
        SetDisplayTitle(title);
        ApplyTheme(_theme);
    }

    private IEnumerable<ReviewArchiveRecord> Filter(IEnumerable<ReviewArchiveRecord> records)
    {
        var now = DateTimeOffset.Now;
        var filter = _viewState.Filter;
        var result = records.Where(item => filter switch
        {
            "today" => item.Done && item.CompletedAt?.ToLocalTime().Date == now.Date,
            "week" => item.Done && item.CompletedAt >= now.AddDays(-7),
            "month" => item.Done && item.CompletedAt >= now.AddDays(-30),
            "open" => !item.Done,
            "deleted" => item.SourceDeleted,
            "all" => true,
            _ => item.Done
        });

        if (!_settings.ShowOpenItems && filter == "all")
        {
            result = result.Where(item => item.Done || item.SourceDeleted);
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
        metadata.Add(record.Done
            ? "完成 " + FormatDate(record.CompletedAt ?? record.LastChangedAt)
            : "创建 " + FormatDate(record.CreatedAt));
        if (record.CreatedAtEstimated || record.CompletedAtEstimated)
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
        return new Border
        {
            Child = panel,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7)
        };
    }

    private string FormatDate(DateTimeOffset value) =>
        _settings.ExportDateFormat == "iso"
            ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var changed = ReviewArchiveStore.ImportCurrent(_context.Host, _settings, manual: true);
        if (!changed)
        {
            _hintText.Text = "当前待办已经全部存在于记录池中。";
        }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var records = Filter(ReviewArchiveStore.Snapshot())
            .OrderByDescending(item => item.CompletedAt ?? item.LastChangedAt)
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
        headers.AddRange(["创建时间", "完成时间", "源已删除", "创建时间精度", "完成时间精度", "来源"]);
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
            row.Add(FormatDate(record.CreatedAt));
            row.Add(record.CompletedAt.HasValue ? FormatDate(record.CompletedAt.Value) : "");
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
        _filterBox.Foreground = text;
        _filterBox.Background = surface;
        _filterBox.BorderBrush = border;
        foreach (var button in _buttons)
        {
            button.Foreground = text;
            button.Background = surface;
            button.BorderBrush = border;
            button.FontFamily = font;
            button.FontSize = 11.5 * scale;
        }

        foreach (var row in _listPanel.Children.OfType<Border>())
        {
            row.BorderBrush = border;
            row.Background = surface;
            foreach (var block in Descendants<TextBlock>(row))
            {
                block.Foreground = block.FontSize <= 10.5 ? weak : text;
                block.FontFamily = font;
            }
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

    private void SetDisplayTitle(string title)
    {
        if (string.Equals(_lastDisplayTitle, title, StringComparison.Ordinal))
        {
            return;
        }
        _lastDisplayTitle = title;
        _context.SetDisplayTitle(title);
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

    public void Commit() => SaveViewState();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        ReviewArchiveStore.Changed -= OnArchiveChanged;
        _subscription.Dispose();
    }
}
