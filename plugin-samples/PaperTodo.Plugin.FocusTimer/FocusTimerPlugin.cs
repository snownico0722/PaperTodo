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
    public string Description => "完全使用 WPF 控件实现，支持折叠后台计时和状态恢复。";
    public Version Version => new(1, 0, 0);
    public string ApiVersion => "1.1";
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
        public State()
        {
        }

        public TimerMode Mode { get; set; } = TimerMode.Focus;
        public int FocusMinutes { get; set; } = 25;
        public int BreakMinutes { get; set; } = 5;
        public int RemainingSeconds { get; set; } = 25 * 60;
        public bool IsRunning { get; set; }
        public DateTimeOffset? EndsAtUtc { get; set; }
        public int CompletedFocusSessions { get; set; }
    }

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
        private readonly Button _resetButton;
        private readonly Button[] _buttons;
        private readonly DispatcherTimer _timer;

        private PaperBodyTheme _theme;
        private bool _runtimeVisible;
        private bool _disposed;
        private string _lastDisplayTitle = "";

        public Session(PaperBodyContext context)
        {
            _context = context;
            _theme = context.Theme;
            _state = ReadState(context.StateJson);
            var stateChanged = NormalizeState() | CompleteExpiredPhase();

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

            _minusButton = MakeButton("−5");
            _durationText = new TextBlock
            {
                MinWidth = 86,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            _plusButton = MakeButton("+5");

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
            _startButton.MinWidth = 112;
            _resetButton = MakeButton("重置");
            _resetButton.MinWidth = 78;

            var actionRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            actionRow.Children.Add(_startButton);
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
                _focusButton, _breakButton, _minusButton,
                _plusButton, _startButton, _resetButton
            ];

            _focusButton.Click += (_, _) => SelectMode(TimerMode.Focus);
            _breakButton.Click += (_, _) => SelectMode(TimerMode.Break);
            _minusButton.Click += (_, _) => ChangeDuration(-5);
            _plusButton.Click += (_, _) => ChangeDuration(5);
            _startButton.Click += (_, _) => ToggleRunning();
            _resetButton.Click += (_, _) => Reset();

            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += OnTick;

            ApplyTheme(context.Theme);
            UpdateView();
            if (stateChanged)
            {
                SaveState();
            }
        }

        public FrameworkElement View => _root;

        private static Button MakeButton(string text) => new()
        {
            Content = text,
            Padding = new Thickness(12, 5, 12, 5),
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

        private bool NormalizeState()
        {
            var old = JsonSerializer.Serialize(_state, JsonOptions);
            _state.FocusMinutes = Math.Clamp(_state.FocusMinutes, 1, 180);
            _state.BreakMinutes = Math.Clamp(_state.BreakMinutes, 1, 180);
            _state.CompletedFocusSessions = Math.Max(0, _state.CompletedFocusSessions);
            if (!Enum.IsDefined(_state.Mode))
            {
                _state.Mode = TimerMode.Focus;
            }

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

            if (_state.Mode == TimerMode.Focus)
            {
                _state.CompletedFocusSessions++;
                _state.Mode = TimerMode.Break;
            }
            else
            {
                _state.Mode = TimerMode.Focus;
            }

            _state.IsRunning = false;
            _state.EndsAtUtc = null;
            _state.RemainingSeconds = DurationSeconds();
            return true;
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

        private void Reset(bool save = true)
        {
            _state.IsRunning = false;
            _state.EndsAtUtc = null;
            _state.RemainingSeconds = DurationSeconds();
            _timer.Stop();
            if (save)
            {
                SaveState();
            }
            UpdateView();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (CompleteExpiredPhase())
            {
                _timer.Stop();
                SaveState();
            }
            UpdateView();
        }

        private void UpdateView()
        {
            var remaining = RemainingSeconds();
            var full = DurationSeconds();
            var minutes = remaining / 60;
            var seconds = remaining % 60;
            var modeName = _state.Mode == TimerMode.Focus ? "专注" : "休息";

            _timeText.Text = $"{minutes:00}:{seconds:00}";
            _statusText.Text = _state.Mode == TimerMode.Focus
                ? (_state.IsRunning ? "保持专注" : "准备开始")
                : (_state.IsRunning ? "放松一下" : "休息计时已暂停");
            _completedText.Text = $"完成 {_state.CompletedFocusSessions} 轮";
            _durationText.Text = $"{DurationMinutes()} 分钟";
            _progress.Maximum = Math.Max(1, full);
            _progress.Value = Math.Clamp(full - remaining, 0, full);
            _startButton.Content = _state.IsRunning
                ? "暂停"
                : remaining < full ? "继续" : "开始";
            _minusButton.IsEnabled = !_state.IsRunning && DurationMinutes() > 1;
            _plusButton.IsEnabled = !_state.IsRunning && DurationMinutes() < 180;

            UpdateModeButtons();
            SetDisplayTitle($"{modeName} · {minutes:00}:{seconds:00}");
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
            _completedText.FontSize = 11 * scale;
            _durationText.FontSize = 12 * scale;
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

        public void OnThemeChanged(PaperBodyTheme theme) =>
            ApplyTheme(theme);

        public void OnTypographyChanged(PaperBodyTheme theme) =>
            ApplyTheme(theme);

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
