using System.Media;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.FocusTimer;

public sealed class FocusTimerPlugin : IPaperBodyPlugin
{
    public string Id => "sample.focus-timer.native";
    public string DisplayName => "专注计时器";
    public string Description => "完整的 WPF 番茄钟示例，支持自动轮转、声音、每日目标和折叠后台计时。";
    public Version Version => new(1, 2, 0);
    public string ApiVersion => "1.2";
    public int StateVersion => 1;
    public PaperBodyCapabilities Capabilities => PaperBodyCapabilities.TextZoom;
    public PaperBodyRuntimeRequirements RuntimeRequirements =>
        PaperBodyRuntimeRequirements.BackgroundUpdates;

    public IPaperBodySession Create(PaperBodyContext context) => new Session(context);

    private enum TimerMode
    {
        Focus,
        Break
    }

    private sealed class State
    {
        public bool Initialized { get; set; }
        public TimerMode Mode { get; set; } = TimerMode.Focus;
        public int FocusMinutes { get; set; } = 25;
        public int BreakMinutes { get; set; } = 5;
        public int RemainingSeconds { get; set; } = 25 * 60;
        public bool IsRunning { get; set; }
        public DateTimeOffset? EndsAtUtc { get; set; }
        public int CompletedFocusSessions { get; set; }
        public int CompletedToday { get; set; }
        public string CompletionDate { get; set; } = "";
    }

    private sealed record Settings(
        int FocusMinutes,
        int BreakMinutes,
        int AdjustStep,
        int DailyGoal,
        bool AutoStartNext,
        bool ShowProgress,
        bool ShowCompleted,
        bool ConfirmReset,
        string Sound,
        string TitleStyle);

    private sealed class Session : IPaperBodySession
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new(JsonSerializerDefaults.Web);

        private readonly PaperBodyContext _context;
        private readonly State _state;
        private readonly Grid _root;
        private readonly Button _focusButton;
        private readonly Button _breakButton;
        private readonly TextBlock _completedText;
        private readonly TextBlock _timeText;
        private readonly TextBlock _statusText;
        private readonly ProgressBar _progress;
        private readonly Button _minusButton;
        private readonly TextBlock _durationText;
        private readonly Button _plusButton;
        private readonly Button _startButton;
        private readonly Button _skipButton;
        private readonly Button _resetButton;
        private readonly Button[] _buttons;
        private readonly DispatcherTimer _timer;

        private Settings _settings;
        private PaperBodyTheme _theme;
        private bool _runtimeVisible;
        private bool _disposed;
        private string _lastDisplayTitle = "";

        public Session(PaperBodyContext context)
        {
            _context = context;
            _theme = context.Theme;
            _settings = ReadSettings(context.SettingsJson);
            _state = ReadState(context.StateJson);
            var stateChanged = InitializeAndNormalizeState();

            _focusButton = MakeButton("专注");
            _breakButton = MakeButton("休息");
            _completedText = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(_focusButton, 0);
            Grid.SetColumn(_breakButton, 1);
            Grid.SetColumn(_completedText, 2);
            header.Children.Add(_focusButton);
            header.Children.Add(_breakButton);
            header.Children.Add(_completedText);

            _timeText = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _statusText = new TextBlock
            {
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _progress = new ProgressBar
            {
                Height = 5,
                Margin = new Thickness(10, 14, 10, 0),
                Minimum = 0,
                BorderThickness = new Thickness(0)
            };

            var center = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            };
            center.Children.Add(_timeText);
            center.Children.Add(_statusText);
            center.Children.Add(_progress);

            _minusButton = MakeButton("−");
            _durationText = new TextBlock
            {
                MinWidth = 100,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            _plusButton = MakeButton("+");

            var durationRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };
            durationRow.Children.Add(_minusButton);
            durationRow.Children.Add(_durationText);
            durationRow.Children.Add(_plusButton);

            _startButton = MakeButton("开始");
            _startButton.MinWidth = 102;
            _skipButton = MakeButton("跳过");
            _skipButton.MinWidth = 68;
            _resetButton = MakeButton("重置");
            _resetButton.MinWidth = 68;

            var actionRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            actionRow.Children.Add(_startButton);
            actionRow.Children.Add(_skipButton);
            actionRow.Children.Add(_resetButton);

            var footer = new StackPanel();
            footer.Children.Add(durationRow);
            footer.Children.Add(actionRow);

            _root = new Grid
            {
                Margin = new Thickness(18, 14, 18, 16),
                Background = Brushes.Transparent
            };
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition());
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(header, 0);
            Grid.SetRow(center, 1);
            Grid.SetRow(footer, 2);
            _root.Children.Add(header);
            _root.Children.Add(center);
            _root.Children.Add(footer);

            _buttons =
            [
                _focusButton, _breakButton, _minusButton, _plusButton,
                _startButton, _skipButton, _resetButton
            ];

            _focusButton.Click += (_, _) => SelectMode(TimerMode.Focus);
            _breakButton.Click += (_, _) => SelectMode(TimerMode.Break);
            _minusButton.Click += (_, _) => ChangeDuration(-_settings.AdjustStep);
            _plusButton.Click += (_, _) => ChangeDuration(_settings.AdjustStep);
            _startButton.Click += (_, _) => ToggleRunning();
            _skipButton.Click += (_, _) => CompletePhase(skipped: true);
            _resetButton.Click += (_, _) => ResetWithConfirmation();

            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _timer.Tick += OnTick;

            ApplyTheme(context.Theme);
            if (!CompleteExpiredPhase())
            {
                UpdateView();
                if (stateChanged)
                {
                    SaveState();
                }
            }
        }

        public FrameworkElement View => _root;

        private static Button MakeButton(string text) => new()
        {
            Content = text,
            Padding = new Thickness(11, 5, 11, 5),
            Margin = new Thickness(3, 0, 3, 0),
            MinHeight = 30,
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand
        };

        private static State ReadState(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<State>(json, JsonOptions) ?? new State();
            }
            catch (JsonException)
            {
                return new State();
            }
        }

        private bool InitializeAndNormalizeState()
        {
            var old = JsonSerializer.Serialize(_state, JsonOptions);
            if (!_state.Initialized)
            {
                _state.Initialized = true;
                _state.FocusMinutes = _settings.FocusMinutes;
                _state.BreakMinutes = _settings.BreakMinutes;
                _state.RemainingSeconds = _state.FocusMinutes * 60;
            }

            _state.FocusMinutes = Math.Clamp(_state.FocusMinutes, 1, 180);
            _state.BreakMinutes = Math.Clamp(_state.BreakMinutes, 1, 180);
            _state.CompletedFocusSessions = Math.Max(0, _state.CompletedFocusSessions);
            _state.CompletedToday = Math.Max(0, _state.CompletedToday);
            if (!Enum.IsDefined(_state.Mode))
            {
                _state.Mode = TimerMode.Focus;
            }

            RefreshDailyCounter();
            _state.RemainingSeconds =
                Math.Clamp(_state.RemainingSeconds, 0, DurationSeconds());
            if (!_state.IsRunning && _state.RemainingSeconds == 0)
            {
                _state.RemainingSeconds = DurationSeconds();
            }
            if (_state.IsRunning && _state.EndsAtUtc == null)
            {
                _state.IsRunning = false;
                _state.RemainingSeconds = Math.Max(1, _state.RemainingSeconds);
            }
            if (!_state.IsRunning)
            {
                _state.EndsAtUtc = null;
            }

            return !string.Equals(
                old,
                JsonSerializer.Serialize(_state, JsonOptions),
                StringComparison.Ordinal);
        }

        private void RefreshDailyCounter()
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (string.Equals(_state.CompletionDate, today, StringComparison.Ordinal))
            {
                return;
            }
            _state.CompletionDate = today;
            _state.CompletedToday = 0;
        }

        private int DurationMinutes() =>
            _state.Mode == TimerMode.Focus ? _state.FocusMinutes : _state.BreakMinutes;

        private int DurationSeconds() => DurationMinutes() * 60;

        private int RemainingSeconds()
        {
            if (!_state.IsRunning || _state.EndsAtUtc == null)
            {
                return Math.Clamp(_state.RemainingSeconds, 0, DurationSeconds());
            }

            return Math.Clamp(
                (int)Math.Ceiling(
                    (_state.EndsAtUtc.Value - DateTimeOffset.UtcNow).TotalSeconds),
                0,
                DurationSeconds());
        }

        private bool CompleteExpiredPhase()
        {
            if (!_state.IsRunning || RemainingSeconds() > 0)
            {
                return false;
            }
            CompletePhase(skipped: false);
            return true;
        }

        private void CompletePhase(bool skipped)
        {
            var completedFocus = _state.Mode == TimerMode.Focus && !skipped;
            if (completedFocus)
            {
                RefreshDailyCounter();
                _state.CompletedFocusSessions++;
                _state.CompletedToday++;
            }

            _state.Mode = _state.Mode == TimerMode.Focus
                ? TimerMode.Break
                : TimerMode.Focus;
            _state.RemainingSeconds = DurationSeconds();
            _state.EndsAtUtc = null;
            _state.IsRunning = _settings.AutoStartNext;
            if (_state.IsRunning)
            {
                _state.EndsAtUtc =
                    DateTimeOffset.UtcNow.AddSeconds(_state.RemainingSeconds);
                StartTimer();
            }
            else
            {
                _timer.Stop();
            }

            if (!skipped)
            {
                PlayCompletionSound();
            }
            SaveState();
            UpdateView();
        }

        private void PlayCompletionSound()
        {
            try
            {
                switch (_settings.Sound)
                {
                    case "asterisk":
                        SystemSounds.Asterisk.Play();
                        break;
                    case "exclamation":
                        SystemSounds.Exclamation.Play();
                        break;
                    case "beep":
                        SystemSounds.Beep.Play();
                        break;
                }
            }
            catch
            {
            }
        }

        private void SelectMode(TimerMode mode)
        {
            if (_state.Mode == mode)
            {
                return;
            }
            _state.Mode = mode;
            Reset(save: true);
        }

        private void ChangeDuration(int delta)
        {
            if (_state.IsRunning)
            {
                return;
            }

            if (_state.Mode == TimerMode.Focus)
            {
                _state.FocusMinutes = Math.Clamp(_state.FocusMinutes + delta, 1, 180);
            }
            else
            {
                _state.BreakMinutes = Math.Clamp(_state.BreakMinutes + delta, 1, 180);
            }

            _state.RemainingSeconds = DurationSeconds();
            SaveState();
            UpdateView();
        }

        private void ToggleRunning()
        {
            if (_state.IsRunning)
            {
                _state.RemainingSeconds = Math.Max(1, RemainingSeconds());
                _state.IsRunning = false;
                _state.EndsAtUtc = null;
                _timer.Stop();
            }
            else
            {
                _state.RemainingSeconds =
                    _state.RemainingSeconds <= 0
                        ? DurationSeconds()
                        : _state.RemainingSeconds;
                _state.IsRunning = true;
                _state.EndsAtUtc =
                    DateTimeOffset.UtcNow.AddSeconds(_state.RemainingSeconds);
                StartTimer();
            }

            SaveState();
            UpdateView();
        }

        private void ResetWithConfirmation()
        {
            if (_settings.ConfirmReset &&
                (_state.IsRunning || RemainingSeconds() < DurationSeconds()) &&
                MessageBox.Show(
                    "重置当前计时？",
                    "专注计时器",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }
            Reset(save: true);
        }

        private void Reset(bool save)
        {
            _state.IsRunning = false;
            _state.EndsAtUtc = null;
            _state.RemainingSeconds = DurationSeconds();
            _timer.Stop();
            if (save) SaveState();
            UpdateView();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (CompleteExpiredPhase())
            {
                return;
            }
            UpdateView();
        }

        private void UpdateView()
        {
            RefreshDailyCounter();
            var remaining = RemainingSeconds();
            var full = DurationSeconds();
            var minutes = remaining / 60;
            var seconds = remaining % 60;
            var modeName = _state.Mode == TimerMode.Focus ? "专注" : "休息";

            _timeText.Text = $"{minutes:00}:{seconds:00}";
            _statusText.Text = _state.Mode == TimerMode.Focus
                ? (_state.IsRunning ? "保持专注" : "准备开始")
                : (_state.IsRunning ? "放松一下" : "休息计时已暂停");
            _completedText.Text = _settings.DailyGoal > 0
                ? $"今日 {_state.CompletedToday}/{_settings.DailyGoal} · 总计 {_state.CompletedFocusSessions}"
                : $"今日 {_state.CompletedToday} · 总计 {_state.CompletedFocusSessions}";
            _completedText.Visibility = _settings.ShowCompleted
                ? Visibility.Visible
                : Visibility.Collapsed;
            _durationText.Text = $"{DurationMinutes()} 分钟 · ±{_settings.AdjustStep}";
            _progress.Visibility = _settings.ShowProgress
                ? Visibility.Visible
                : Visibility.Collapsed;
            _progress.Maximum = Math.Max(1, full);
            _progress.Value = Math.Clamp(full - remaining, 0, full);
            _startButton.Content = _state.IsRunning
                ? "暂停"
                : remaining < full ? "继续" : "开始";
            _minusButton.IsEnabled = !_state.IsRunning && DurationMinutes() > 1;
            _plusButton.IsEnabled = !_state.IsRunning && DurationMinutes() < 180;

            UpdateModeButtons();
            SetDisplayTitle(_settings.TitleStyle switch
            {
                "status" => _state.IsRunning ? $"{modeName}中" : $"{modeName}已暂停",
                "fixed" => "专注计时器",
                _ => $"{modeName} · {minutes:00}:{seconds:00}"
            });
        }

        private void ApplyTheme(PaperBodyTheme theme)
        {
            _theme = theme;
            var scale = Math.Clamp(theme.FontScale, 0.7, 2.5);
            var fontFamily = new FontFamily(theme.FontFamily);
            var text = ToBrush(theme.TextColor, "#202020");
            var weak = ToBrush(theme.WeakTextColor, "#707070");
            var border = ToBrush(theme.BorderColor, "#807050");
            var background = ToBrush(
                theme.IsDark ? "#18FFFFFF" : "#0C000000",
                "#0C000000");

            _timeText.FontFamily = fontFamily;
            _statusText.FontFamily = fontFamily;
            _completedText.FontFamily = fontFamily;
            _durationText.FontFamily = fontFamily;
            _timeText.FontSize = 46 * scale;
            _statusText.FontSize = 12 * scale;
            _completedText.FontSize = 10.5 * scale;
            _durationText.FontSize = 11.5 * scale;
            _timeText.Foreground = text;
            _statusText.Foreground = weak;
            _completedText.Foreground = weak;
            _durationText.Foreground = text;
            _progress.Foreground = ToBrush(theme.AccentColor, "#B07A31");
            _progress.Background = ToBrush("#30707070", "#30707070");

            foreach (var button in _buttons)
            {
                button.FontFamily = fontFamily;
                button.FontSize = 12 * scale;
                button.Foreground = text;
                button.Background = background;
                button.BorderBrush = border;
            }
            UpdateModeButtons();
        }

        private void UpdateModeButtons()
        {
            var active = ToBrush(
                AddAlpha(_theme.AccentColor, 52),
                "#34B07A31");
            var inactive = ToBrush(
                _theme.IsDark ? "#18FFFFFF" : "#0C000000",
                "#0C000000");
            var accent = ToBrush(_theme.AccentColor, "#B07A31");
            var border = ToBrush(_theme.BorderColor, "#807050");

            _focusButton.Background =
                _state.Mode == TimerMode.Focus ? active : inactive;
            _breakButton.Background =
                _state.Mode == TimerMode.Break ? active : inactive;
            _focusButton.BorderBrush =
                _state.Mode == TimerMode.Focus ? accent : border;
            _breakButton.BorderBrush =
                _state.Mode == TimerMode.Break ? accent : border;
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
                return "#34B07A31";
            }
        }

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

        private static Settings ReadSettings(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var root = document.RootElement;
                return new Settings(
                    Integer(root, "focusMinutes", 25, 1, 180),
                    Integer(root, "breakMinutes", 5, 1, 180),
                    Integer(root, "adjustStep", 5, 1, 30),
                    Integer(root, "dailyGoal", 4, 0, 24),
                    Boolean(root, "autoStartNext", false),
                    Boolean(root, "showProgress", true),
                    Boolean(root, "showCompleted", true),
                    Boolean(root, "confirmReset", true),
                    String(root, "sound", "asterisk"),
                    String(root, "titleStyle", "countdown"));
            }
            catch
            {
                return new Settings(
                    25, 5, 5, 4, false, true, true, true, "asterisk", "countdown");
            }
        }

        private static int Integer(
            JsonElement root,
            string name,
            int fallback,
            int min,
            int max) =>
            root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var number)
                ? Math.Clamp(number, min, max)
                : fallback;

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

        private void ApplySettings(Settings settings)
        {
            var previousDuration = DurationSeconds();
            var remaining = RemainingSeconds();
            var wasAtStart = !_state.IsRunning && remaining == previousDuration;

            _settings = settings;
            _state.FocusMinutes = settings.FocusMinutes;
            _state.BreakMinutes = settings.BreakMinutes;
            _state.RemainingSeconds = wasAtStart
                ? DurationSeconds()
                : Math.Clamp(remaining, 1, DurationSeconds());
            if (_state.IsRunning)
            {
                _state.EndsAtUtc =
                    DateTimeOffset.UtcNow.AddSeconds(_state.RemainingSeconds);
            }

            SaveState();
            UpdateView();
        }

        private void SetDisplayTitle(string title)
        {
            if (_lastDisplayTitle == title)
            {
                return;
            }
            _lastDisplayTitle = title;
            _context.SetDisplayTitle(title);
        }

        private void SaveState() =>
            _context.SaveStateJson(JsonSerializer.Serialize(_state, JsonOptions));

        private void StartTimer()
        {
            if (_runtimeVisible && _state.IsRunning && !_timer.IsEnabled)
            {
                _timer.Start();
            }
        }

        public void Commit()
        {
            if (_state.IsRunning)
            {
                _state.RemainingSeconds = RemainingSeconds();
            }
            CompleteExpiredPhase();
            SaveState();
        }

        public void OnSettingsChanged(string settingsJson) =>
            ApplySettings(ReadSettings(settingsJson));

        public void OnVisibilityChanged(bool visible)
        {
            _runtimeVisible = visible;
            if (!visible)
            {
                _timer.Stop();
                return;
            }

            if (CompleteExpiredPhase())
            {
                SaveState();
            }
            StartTimer();
            UpdateView();
        }

        public void OnPresentationChanged(bool visible) =>
            _root.IsHitTestVisible = visible;

        public void OnThemeChanged(PaperBodyTheme theme) => ApplyTheme(theme);
        public void OnTypographyChanged(PaperBodyTheme theme) => ApplyTheme(theme);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTick;
        }
    }
}
