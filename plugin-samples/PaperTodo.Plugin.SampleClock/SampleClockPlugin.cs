using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.SampleClock;

public sealed class SampleClockPlugin : IPaperBodyPlugin
{

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

    private sealed class ClockSession :
        IPaperBodySession,
        IPaperCapsuleViewProvider,
        IPaperMiniViewProvider
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
        private string _lastDisplayTitle = "";
        private string _lastCapsuleSignature = "";
        private string _capsuleTitle = "时钟";
        private double _capsuleProgress;
        private bool _capsuleShowProgress = true;
        private ClockCapsuleView? _regularCapsuleView;
        private ClockCapsuleView? _dockedCapsuleView;
        private ClockMiniView? _miniView;

        public ClockSession(PaperBodyContext context)
        {
            _context = context;
            _theme = context.Body.Theme;
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

            ApplyTheme(context.Body.Theme);
            ApplySettings(_settings);
            Refresh();
        }

        public FrameworkElement View => _root;

        public FrameworkElement? CreateCapsuleView(PaperCapsuleViewContext context)
        {
            var view = new ClockCapsuleView(context);
            view.Update(_capsuleTitle, _capsuleProgress, _capsuleShowProgress);
            if (context.Surface == PaperCapsuleSurfaceKind.Docked)
            {
                _dockedCapsuleView = view;
            }
            else
            {
                _regularCapsuleView = view;
            }
            return view;
        }

        public PaperMiniViewSize PreferredMiniViewSize => new(300, 190);

        public FrameworkElement? CreateMiniView(PaperMiniViewContext context)
        {
            _miniView = new ClockMiniView(context);
            _miniView.Update(
                _time.Text,
                _meridiem.Text,
                _date.Text,
                _zone.Text,
                _dayProgress.Value,
                _settings.ShowDayProgress);
            return _miniView;
        }

        private sealed class ClockMiniView : Grid
        {
            private readonly TextBlock _time;
            private readonly TextBlock _meridiem;
            private readonly TextBlock _date;
            private readonly TextBlock _zone;
            private readonly ProgressBar _progress;

            public ClockMiniView(PaperMiniViewContext context)
            {
                Margin = new Thickness(14, 11, 14, 13);
                Background = Brushes.Transparent;
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                RowDefinitions.Add(new RowDefinition());
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                _zone = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                };
                Children.Add(_zone);

                _time = new TextBlock
                {
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                };
                _meridiem = new TextBlock
                {
                    Margin = new Thickness(7, 0, 0, 5),
                    VerticalAlignment = VerticalAlignment.Bottom
                };
                var timeRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                timeRow.Children.Add(_time);
                timeRow.Children.Add(_meridiem);
                Grid.SetRow(timeRow, 1);
                Children.Add(timeRow);

                _date = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                _progress = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 1,
                    Height = 5,
                    Margin = new Thickness(8, 9, 8, 0),
                    BorderThickness = new Thickness(0)
                };
                var footer = new StackPanel();
                footer.Children.Add(_date);
                footer.Children.Add(_progress);
                Grid.SetRow(footer, 2);
                Children.Add(footer);
                ApplyTheme(context.Theme);
            }

            public void Update(
                string time,
                string meridiem,
                string date,
                string zone,
                double progress,
                bool showProgress)
            {
                _time.Text = time;
                _meridiem.Text = meridiem;
                _meridiem.Visibility = string.IsNullOrWhiteSpace(meridiem)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                _date.Text = date;
                _date.Visibility = string.IsNullOrWhiteSpace(date)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                _zone.Text = zone;
                _progress.Value = Math.Clamp(progress, 0, 1);
                _progress.Visibility = showProgress
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            public void ApplyTheme(PaperBodyTheme theme)
            {
                var scale = Math.Clamp(theme.FontScale, 0.85, 1.3);
                var font = new FontFamily(theme.FontFamily);
                var text = ToBrush(theme.TextColor, "#202020");
                var weak = ToBrush(theme.WeakTextColor, "#707070");
                _time.FontFamily = font;
                _meridiem.FontFamily = font;
                _date.FontFamily = font;
                _zone.FontFamily = font;
                _time.FontSize = 42 * scale;
                _meridiem.FontSize = 10.5 * scale;
                _date.FontSize = 11.5 * scale;
                _zone.FontSize = 10 * scale;
                _time.Foreground = text;
                _meridiem.Foreground = weak;
                _date.Foreground = weak;
                _zone.Foreground = weak;
                _progress.Foreground = ToBrush(theme.AccentColor, "#B07A31");
                _progress.Background = ToBrush(
                    theme.IsDark ? "#28FFFFFF" : "#22000000",
                    "#22000000");
            }
        }

        private sealed class ClockCapsuleView : Grid
        {
            private readonly PaperCapsuleSurfaceKind _surface;
            private readonly TextBlock _label;
            private readonly Grid _progressHost;
            private readonly Border _progressTrack;
            private readonly Border _progressFill;
            private double _progressValue;

            public ClockCapsuleView(PaperCapsuleViewContext context)
            {
                _surface = context.Surface;
                Background = Brushes.Transparent;
                ClipToBounds = true;
                _label = new TextBlock
                {
                    Margin = new Thickness(4, 0, 4, 2),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontWeight = FontWeights.SemiBold
                };

                var inset = context.Surface == PaperCapsuleSurfaceKind.Docked ? 4 : 6;
                _progressHost = new Grid
                {
                    Height = 3,
                    Margin = new Thickness(inset, 0, inset, 3),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    ClipToBounds = true
                };
                _progressTrack = new Border
                {
                    CornerRadius = new CornerRadius(1.5),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };
                _progressFill = new Border
                {
                    CornerRadius = new CornerRadius(1.5),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Stretch
                };
                _progressHost.Children.Add(_progressTrack);
                _progressHost.Children.Add(_progressFill);
                _progressHost.SizeChanged += (_, _) => UpdateProgressWidth();

                Children.Add(_label);
                Children.Add(_progressHost);
                ApplyTheme(context.Theme);
            }

            public void Update(string title, double progress, bool showProgress)
            {
                _label.Text = title;
                _label.Margin = showProgress
                    ? new Thickness(4, 0, 4, 2)
                    : new Thickness(4, 0, 4, 0);
                _progressValue = Math.Clamp(progress, 0, 1);
                _progressHost.Visibility = showProgress
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                UpdateProgressWidth();
            }

            public void ApplyTheme(PaperBodyTheme theme)
            {
                var scale = Math.Clamp(theme.FontScale, 0.85, 1.2);
                _label.FontFamily = new FontFamily(theme.FontFamily);
                _label.FontSize =
                    (_surface == PaperCapsuleSurfaceKind.Docked ? 11.5 : 12) * scale;
                _label.Foreground = ToBrush(theme.TextColor, "#202020");
                var track = ToBrush(theme.WeakTextColor, "#707070");
                track.Opacity = 0.22;
                _progressTrack.Background = track;
                _progressFill.Background = ToBrush(theme.AccentColor, "#B07A31");
            }

            private void UpdateProgressWidth()
            {
                _progressFill.Width = Math.Max(0, _progressHost.ActualWidth * _progressValue);
            }
        }

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
            if (!_timer.IsEnabled)
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
            var dayProgress = Math.Clamp(
                seconds / TimeSpan.FromDays(1).TotalSeconds,
                0,
                1);
            _dayProgress.Value = dayProgress;
            _miniView?.Update(
                _time.Text,
                _meridiem.Text,
                _date.Text,
                _zone.Text,
                dayProgress,
                _settings.ShowDayProgress);

            SetPaperStatus(DisplayTitle(now), dayProgress);
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
            _regularCapsuleView?.ApplyTheme(theme);
            _dockedCapsuleView?.ApplyTheme(theme);
            _miniView?.ApplyTheme(theme);
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

        private void SetPaperStatus(string title, double dayProgress)
        {
            if (!string.Equals(_lastDisplayTitle, title, StringComparison.Ordinal))
            {
                _lastDisplayTitle = title;
                _context.Paper.SetHeaderText(title);
            }

            // The body can refresh at 4 Hz, but a day-progress capsule has no useful visual
            // change at that cadence. Share one 0.1% quantization boundary between the 1.7
            // live views and the 1.6 fallback so neither path churns layout unnecessarily.
            var progressStep = (int)Math.Round(
                Math.Clamp(dayProgress, 0, 1) * 1000,
                MidpointRounding.AwayFromZero);
            var signature =
                $"{title}\u001f{progressStep}\u001f{_settings.ShowDayProgress}";
            if (string.Equals(_lastCapsuleSignature, signature, StringComparison.Ordinal))
            {
                return;
            }
            _lastCapsuleSignature = signature;
            _capsuleTitle = title;
            _capsuleProgress = progressStep / 1000.0;
            _capsuleShowProgress = _settings.ShowDayProgress;
            _regularCapsuleView?.Update(
                _capsuleTitle,
                _capsuleProgress,
                _capsuleShowProgress);
            _dockedCapsuleView?.Update(
                _capsuleTitle,
                _capsuleProgress,
                _capsuleShowProgress);
            _context.Paper.SetCapsulePresentation(new PaperCapsulePresentation
            {
                PreferredWidth = PaperCapsulePresentation.AutomaticWidth,
                PlainText = title,
                ToolTip = title,
                Components = _settings.ShowDayProgress
                    ? new PaperCapsuleComponent[]
                    {
                        new PaperCapsuleComponent
                        {
                            Kind = PaperCapsuleComponentKind.ProgressRing,
                            Value = _capsuleProgress,
                            Width = 18,
                            Tone = PaperCapsuleTone.Accent
                        },
                        new PaperCapsuleComponent
                        {
                            Kind = PaperCapsuleComponentKind.Text,
                            Text = title,
                            Fill = true
                        }
                    }
                    : new PaperCapsuleComponent[]
                    {
                        new PaperCapsuleComponent
                        {
                            Kind = PaperCapsuleComponentKind.Text,
                            Text = title,
                            Fill = true
                        }
                    }
            });
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
            if (!_timer.IsEnabled) _timer.Start();
            if (visible) Refresh();
        }

        public void OnPresentationChanged(bool visible) =>
            _root.IsHitTestVisible = visible;

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _regularCapsuleView = null;
            _dockedCapsuleView = null;
            _miniView = null;
        }
    }
}
