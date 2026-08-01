using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.SampleClock;

public sealed class SampleClockPlugin : IPaperBodyPlugin
{
    public string Id => "sample.clock.native";
    public string DisplayName => "原生时钟";
    public string Description => "完整的 WPF 时钟示例：时区、日期格式、标题和日进度均可配置。";
    public Version Version => new(1, 3, 0);
    public string ApiVersion => "1.2";
    public int StateVersion => 1;
    public PaperBodyCapabilities Capabilities => PaperBodyCapabilities.TextZoom;
    public PaperBodyRuntimeRequirements RuntimeRequirements =>
        PaperBodyRuntimeRequirements.BackgroundUpdates;

    public IPaperBodySession Create(PaperBodyContext context) =>
        new ClockSession(context);

    private sealed record ClockSettings(
        bool ShowSeconds,
        bool ShowDate,
        bool ShowWeekday,
        bool ShowDayProgress,
        string HourCycle,
        string DateFormat,
        string TimeZone,
        string TitleMode,
        string CustomTitle,
        double ClockScale);

    private sealed class ClockSession : IPaperBodySession
    {
        private static readonly IReadOnlyDictionary<string, string> TimeZoneIds =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["local"] = "",
                ["utc"] = "UTC",
                ["beijing"] = "China Standard Time",
                ["tokyo"] = "Tokyo Standard Time",
                ["london"] = "GMT Standard Time",
                ["newYork"] = "Eastern Standard Time",
                ["losAngeles"] = "Pacific Standard Time"
            };

        private readonly PaperBodyContext _context;
        private readonly Grid _root;
        private readonly TextBlock _time;
        private readonly TextBlock _meridiem;
        private readonly TextBlock _date;
        private readonly TextBlock _zone;
        private readonly ProgressBar _dayProgress;
        private readonly DispatcherTimer _timer;
        private ClockSettings _settings;
        private PaperBodyTheme _theme;
        private bool _runtimeVisible;
        private string _lastDisplayTitle = "";

        public ClockSession(PaperBodyContext context)
        {
            _context = context;
            _theme = context.Theme;
            _settings = ReadSettings(context.SettingsJson);

            _time = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            _meridiem = new TextBlock
            {
                Margin = new Thickness(8, 0, 0, 6),
                VerticalAlignment = VerticalAlignment.Bottom
            };

            var timeRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            timeRow.Children.Add(_time);
            timeRow.Children.Add(_meridiem);

            _date = new TextBlock
            {
                Margin = new Thickness(0, 7, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            _zone = new TextBlock
            {
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            _dayProgress = new ProgressBar
            {
                Minimum = 0,
                Maximum = 1,
                Height = 4,
                Margin = new Thickness(18, 16, 18, 0),
                BorderThickness = new Thickness(0)
            };

            var content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            };
            content.Children.Add(timeRow);
            content.Children.Add(_date);
            content.Children.Add(_zone);
            content.Children.Add(_dayProgress);

            _root = new Grid
            {
                Background = Brushes.Transparent,
                Margin = new Thickness(16, 14, 16, 16),
                Children = { content }
            };

            _timer = new DispatcherTimer(DispatcherPriority.Background);
            _timer.Tick += OnTick;

            ApplyTheme(context.Theme);
            ApplySettings(_settings);
            Refresh();
        }

        public FrameworkElement View => _root;

        private void OnTick(object? sender, EventArgs e) => Refresh();

        private void ApplySettings(ClockSettings settings)
        {
            _settings = settings;
            _date.Visibility = settings.ShowDate || settings.ShowWeekday
                ? Visibility.Visible
                : Visibility.Collapsed;
            _dayProgress.Visibility = settings.ShowDayProgress
                ? Visibility.Visible
                : Visibility.Collapsed;
            _timer.Interval = settings.ShowSeconds
                ? TimeSpan.FromMilliseconds(250)
                : TimeSpan.FromSeconds(1);
            ApplyTheme(_theme);
            if (_runtimeVisible && !_timer.IsEnabled)
            {
                _timer.Start();
            }
        }

        private void Refresh()
        {
            var now = CurrentTime();
            var twelveHour = string.Equals(
                _settings.HourCycle,
                "12",
                StringComparison.Ordinal);
            var timeFormat = twelveHour
                ? (_settings.ShowSeconds ? "hh:mm:ss" : "hh:mm")
                : (_settings.ShowSeconds ? "HH:mm:ss" : "HH:mm");

            _time.Text = now.ToString(timeFormat);
            _meridiem.Text = twelveHour ? now.ToString("tt") : "";
            _meridiem.Visibility = twelveHour
                ? Visibility.Visible
                : Visibility.Collapsed;

            var dateParts = new List<string>();
            if (_settings.ShowDate)
            {
                dateParts.Add(FormatDate(now));
            }
            if (_settings.ShowWeekday)
            {
                dateParts.Add(now.ToString("dddd"));
            }
            _date.Text = string.Join(" · ", dateParts);
            _zone.Text = TimeZoneLabel();

            var seconds = now.TimeOfDay.TotalSeconds;
            _dayProgress.Value = Math.Clamp(
                seconds / TimeSpan.FromDays(1).TotalSeconds,
                0,
                1);

            SetDisplayTitle(DisplayTitle(now));
        }

        private DateTimeOffset CurrentTime()
        {
            if (string.Equals(_settings.TimeZone, "local", StringComparison.Ordinal))
            {
                return DateTimeOffset.Now;
            }

            try
            {
                var id = TimeZoneIds.TryGetValue(_settings.TimeZone, out var value)
                    ? value
                    : "";
                if (string.IsNullOrWhiteSpace(id))
                {
                    return DateTimeOffset.Now;
                }
                var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
                return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
            }
            catch
            {
                return DateTimeOffset.Now;
            }
        }

        private string TimeZoneLabel()
        {
            return _settings.TimeZone switch
            {
                "utc" => "UTC",
                "beijing" => "北京时间",
                "tokyo" => "东京时间",
                "london" => "伦敦时间",
                "newYork" => "纽约时间",
                "losAngeles" => "洛杉矶时间",
                _ => "本地时间"
            };
        }

        private string FormatDate(DateTimeOffset now) =>
            _settings.DateFormat switch
            {
                "short" => now.ToString("yyyy-MM-dd"),
                "slash" => now.ToString("yyyy/MM/dd"),
                "us" => now.ToString("MM/dd/yyyy"),
                "eu" => now.ToString("dd/MM/yyyy"),
                _ => now.ToString("yyyy年M月d日")
            };

        private string DisplayTitle(DateTimeOffset now)
        {
            var time = now.ToString(
                string.Equals(_settings.HourCycle, "12", StringComparison.Ordinal)
                    ? "hh:mm tt"
                    : "HH:mm");
            return _settings.TitleMode switch
            {
                "date" => FormatDate(now),
                "zone" => $"{TimeZoneLabel()} · {time}",
                "custom" when !string.IsNullOrWhiteSpace(_settings.CustomTitle) =>
                    _settings.CustomTitle.Trim(),
                "fixed" => "时钟",
                _ => time
            };
        }

        private void ApplyTheme(PaperBodyTheme theme)
        {
            _theme = theme;
            var scale = Math.Clamp(theme.FontScale * _settings.ClockScale, 0.7, 3);
            var font = new FontFamily(theme.FontFamily);
            var text = ToBrush(theme.TextColor, "#202020");
            var weak = ToBrush(theme.WeakTextColor, "#707070");

            _time.FontFamily = font;
            _meridiem.FontFamily = font;
            _date.FontFamily = font;
            _zone.FontFamily = font;

            _time.FontSize = 44 * scale;
            _meridiem.FontSize = 11 * scale;
            _date.FontSize = 12 * scale;
            _zone.FontSize = 10 * scale;

            _time.Foreground = text;
            _meridiem.Foreground = weak;
            _date.Foreground = text;
            _zone.Foreground = weak;
            _dayProgress.Foreground = ToBrush(theme.AccentColor, "#B07A31");
            _dayProgress.Background = ToBrush(
                theme.IsDark ? "#28FFFFFF" : "#22000000",
                "#22000000");
        }

        private static ClockSettings ReadSettings(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var root = document.RootElement;
                return new ClockSettings(
                    Boolean(root, "showSeconds", true),
                    Boolean(root, "showDate", true),
                    Boolean(root, "showWeekday", true),
                    Boolean(root, "showDayProgress", true),
                    String(root, "hourCycle", "24"),
                    String(root, "dateFormat", "long"),
                    String(root, "timeZone", "local"),
                    String(root, "titleMode", "time"),
                    String(root, "customTitle", ""),
                    Number(root, "clockScale", 1));
            }
            catch
            {
                return new ClockSettings(
                    true, true, true, true, "24", "long", "local", "time", "", 1);
            }
        }

        private static bool Boolean(JsonElement root, string name, bool fallback) =>
            root.TryGetProperty(name, out var value) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : fallback;

        private static string String(JsonElement root, string name, string fallback) =>
            root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : fallback;

        private static double Number(JsonElement root, string name, double fallback) =>
            root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var number)
                ? number
                : fallback;

        private static SolidColorBrush ToBrush(string value, string fallback)
        {
            try
            {
                return new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(value)!);
            }
            catch
            {
                return new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(fallback)!);
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

        public void OnSettingsChanged(string settingsJson)
        {
            ApplySettings(ReadSettings(settingsJson));
            Refresh();
        }

        public void OnThemeChanged(PaperBodyTheme theme) => ApplyTheme(theme);
        public void OnTypographyChanged(PaperBodyTheme theme) => ApplyTheme(theme);

        public void OnVisibilityChanged(bool visible)
        {
            _runtimeVisible = visible;
            if (visible)
            {
                if (!_timer.IsEnabled) _timer.Start();
                Refresh();
            }
            else
            {
                _timer.Stop();
            }
        }

        public void OnPresentationChanged(bool visible) =>
            _root.IsHitTestVisible = visible;

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
        }
    }
}
