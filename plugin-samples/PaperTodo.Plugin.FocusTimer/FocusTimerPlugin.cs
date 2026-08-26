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
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);


    public string MigrateState(string stateJson, int fromVersion)
    {
        try
        {
            var state = JsonSerializer.Deserialize<State>(
                string.IsNullOrWhiteSpace(stateJson) ? "{}" : stateJson,
                JsonOptions) ?? new State();
            state.LinkedPaperId ??= "";
            state.LinkedTodoId ??= "";
            return JsonSerializer.Serialize(state, JsonOptions);
        }
        catch
        {
            return JsonSerializer.Serialize(new State(), JsonOptions);
        }
    }

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
        public string LinkedPaperId { get; set; } = "";
        public string LinkedTodoId { get; set; } = "";
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
        bool ShowLinkedTodo,
        bool CompleteLinkedTodo,
        bool AutoSelectNextTodo,
        string Sound,
        string TitleStyle);

    private sealed record TodoOption(
        string PaperId,
        string TodoId,
        string Label,
        string Text,
        bool Done)
    {
        public bool IsNone =>
            string.IsNullOrWhiteSpace(PaperId) ||
            string.IsNullOrWhiteSpace(TodoId);
    }

    private sealed class Session : IPaperBodySession, IPaperMiniViewProvider
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new(JsonSerializerDefaults.Web);

        private readonly PaperBodyContext _context;
        private readonly State _state;
        private readonly Grid _root;
        private readonly Button _focusButton;
        private readonly Button _breakButton;
        private readonly TextBlock _completedText;
        private readonly Border _todoHost;
        private readonly ComboBox _todoBox;
        private readonly TextBlock _todoStatusText;
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
        private readonly DispatcherTimer _hostRefreshTimer;
        private readonly IDisposable? _subscription;

        private Settings _settings;
        private PaperBodyTheme _theme;
        private IReadOnlyList<TodoOption> _todoOptions = [];
        private bool _suppressTodoSelection;
        private bool _disposed;
        private bool _compactLayout;
        private string _lastDisplayTitle = "";
        private string _lastCapsuleSignature = "";
        private string _todoLoadError = "";
        private FocusTimerMiniView? _miniView;

        public Session(PaperBodyContext context)
        {
            _context = context;
            _theme = context.Body.Theme;
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

            _todoBox = new ComboBox
            {
                MinWidth = 170,
                MaxWidth = 460,
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                DisplayMemberPath = nameof(TodoOption.Label),
                ToolTip = "选择本轮专注对应的 PaperTodo 待办"
            };
            _todoBox.SelectionChanged += OnTodoSelectionChanged;
            _todoStatusText = new TextBlock
            {
                Margin = new Thickness(0, 5, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            var todoPanel = new StackPanel();
            todoPanel.Children.Add(_todoBox);
            todoPanel.Children.Add(_todoStatusText);
            _todoHost = new Border
            {
                Margin = new Thickness(3, 10, 3, 0),
                Padding = new Thickness(10, 8, 10, 8),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Child = todoPanel
            };

            _timeText = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _statusText = new TextBlock
            {
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
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

            var durationRow = new WrapPanel
            {
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

            var actionRow = new WrapPanel
            {
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
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition());
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(header, 0);
            Grid.SetRow(_todoHost, 1);
            Grid.SetRow(center, 2);
            Grid.SetRow(footer, 3);
            _root.Children.Add(header);
            _root.Children.Add(_todoHost);
            _root.Children.Add(center);
            _root.Children.Add(footer);
            _root.SizeChanged += (_, e) =>
                ApplyResponsiveLayout(e.NewSize.Width);

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

            _hostRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(80)
            };
            _hostRefreshTimer.Tick += (_, _) =>
            {
                _hostRefreshTimer.Stop();
                if (_disposed)
                {
                    return;
                }
                RefreshTodoOptions(saveIfMissing: true);
                UpdateView();
            };

            if (CanReadTodos())
            {
                RefreshTodoOptions(saveIfMissing: true);
                if (HasPermission(PaperTodoPermissionNames.TodosObserve))
                {
                    var kinds = new HashSet<PaperTodoEventKind>
                    {
                        PaperTodoEventKind.TodoCreated,
                        PaperTodoEventKind.TodoChanged,
                        PaperTodoEventKind.TodoDeleted
                    };
                    if (HasPermission(PaperTodoPermissionNames.PapersObserve))
                    {
                        kinds.Add(PaperTodoEventKind.PaperChanged);
                        kinds.Add(PaperTodoEventKind.PaperDeleted);
                    }
                    _subscription = context.Workspace.Subscribe(
                        new PaperTodoEventFilter { Kinds = kinds },
                        _ => QueueTodoRefresh());
                }
            }
            else
            {
                _todoLoadError = "插件未获得 todos.read 权限，无法关联待办。";
            }

            ApplyTheme(context.Body.Theme);
            if (!CompleteExpiredPhase())
            {
                UpdateView();
                if (stateChanged)
                {
                    SaveState();
                }
            }
            StartTimer();
        }

        public FrameworkElement View => _root;

        public PaperMiniViewSize PreferredMiniViewSize => new(300, 210);

        public FrameworkElement? CreateMiniView(PaperMiniViewContext context)
        {
            _miniView = new FocusTimerMiniView(context.Theme, ToggleRunning);
            UpdateView();
            return _miniView;
        }

        private sealed class FocusTimerMiniView : Grid
        {
            private readonly TextBlock _mode;
            private readonly TextBlock _time;
            private readonly TextBlock _task;
            private readonly TextBlock _status;
            private readonly ProgressBar _progress;
            private readonly Button _toggle;

            public FocusTimerMiniView(PaperBodyTheme theme, Action toggle)
            {
                Margin = new Thickness(14, 11, 14, 13);
                Background = Brushes.Transparent;
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                RowDefinitions.Add(new RowDefinition());
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                _mode = new TextBlock
                {
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                Children.Add(_mode);

                _time = new TextBlock
                {
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                };
                _task = new TextBlock
                {
                    Margin = new Thickness(4, 3, 4, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 240
                };
                var center = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Center
                };
                center.Children.Add(_time);
                center.Children.Add(_task);
                Grid.SetRow(center, 1);
                Children.Add(center);

                _progress = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 1,
                    Height = 5,
                    Margin = new Thickness(5, 0, 5, 8),
                    BorderThickness = new Thickness(0)
                };
                _status = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                _toggle = new Button
                {
                    MinWidth = 64,
                    MinHeight = 28,
                    Padding = new Thickness(10, 3, 10, 3),
                    Margin = new Thickness(10, 0, 0, 0),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                _toggle.Click += (_, _) => toggle();
                var action = new Grid();
                action.ColumnDefinitions.Add(new ColumnDefinition());
                action.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                action.Children.Add(_status);
                Grid.SetColumn(_toggle, 1);
                action.Children.Add(_toggle);
                var footer = new StackPanel();
                footer.Children.Add(_progress);
                footer.Children.Add(action);
                Grid.SetRow(footer, 2);
                Children.Add(footer);
                ApplyTheme(theme);
            }

            public void Update(
                string mode,
                string time,
                string task,
                string status,
                double progress,
                string action)
            {
                _mode.Text = mode;
                _time.Text = time;
                _task.Text = task;
                _task.Visibility = string.IsNullOrWhiteSpace(task)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                _status.Text = status;
                _progress.Value = Math.Clamp(progress, 0, 1);
                _toggle.Content = action;
            }

            public void ApplyTheme(PaperBodyTheme theme)
            {
                var scale = Math.Clamp(theme.FontScale, 0.85, 1.3);
                var font = new FontFamily(theme.FontFamily);
                var text = ToBrush(theme.TextColor, "#202020");
                var weak = ToBrush(theme.WeakTextColor, "#707070");
                var border = ToBrush(theme.BorderColor, "#807050");
                _mode.FontFamily = font;
                _time.FontFamily = font;
                _task.FontFamily = font;
                _status.FontFamily = font;
                _toggle.FontFamily = font;
                _mode.FontSize = 12 * scale;
                _time.FontSize = 42 * scale;
                _task.FontSize = 11 * scale;
                _status.FontSize = 10.5 * scale;
                _toggle.FontSize = 11 * scale;
                _mode.Foreground = weak;
                _time.Foreground = text;
                _task.Foreground = weak;
                _status.Foreground = weak;
                _progress.Foreground = ToBrush(theme.AccentColor, "#B07A31");
                _progress.Background = ToBrush("#30707070", "#30707070");
                _toggle.Foreground = text;
                _toggle.Background = ToBrush(
                    theme.IsDark ? "#18FFFFFF" : "#0C000000",
                    "#0C000000");
                _toggle.BorderBrush = border;
            }
        }

        private void ApplyResponsiveLayout(double width)
        {
            var compact = width < 300;
            if (_compactLayout == compact)
            {
                return;
            }

            _compactLayout = compact;
            _root.Margin = compact
                ? new Thickness(9, 9, 9, 11)
                : new Thickness(18, 14, 18, 16);
            _todoBox.MaxWidth = compact ? 260 : 460;
        }

        private static Button MakeButton(string text) => new()
        {
            Content = text,
            Padding = new Thickness(11, 5, 11, 5),
            Margin = new Thickness(3, 0, 3, 0),
            MinHeight = 30,
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand
        };

        private bool HasPermission(string permission) =>
            _context.GrantedPermissions.Contains(permission);

        private bool CanReadTodos() =>
            HasPermission(PaperTodoPermissionNames.TodosRead);

        private static State ReadState(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<State>(
                    string.IsNullOrWhiteSpace(json) ? "{}" : json,
                    JsonOptions) ?? new State();
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

            _state.LinkedPaperId ??= "";
            _state.LinkedTodoId ??= "";
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
                CompleteLinkedTodo();
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

        private void CompleteLinkedTodo()
        {
            if (!_settings.CompleteLinkedTodo ||
                !HasPermission(PaperTodoPermissionNames.TodosUpdate) ||
                string.IsNullOrWhiteSpace(_state.LinkedPaperId) ||
                string.IsNullOrWhiteSpace(_state.LinkedTodoId))
            {
                return;
            }

            try
            {
                var current = _context.Workspace
                    .ListTodos(_state.LinkedPaperId, includeBlank: false)
                    .FirstOrDefault(item => string.Equals(
                        item.Id,
                        _state.LinkedTodoId,
                        StringComparison.Ordinal));
                if (current == null)
                {
                    ClearLinkedTodo(save: false);
                    _todoLoadError = "关联待办已不存在。";
                    return;
                }
                if (!current.Done)
                {
                    _context.Workspace.UpdateTodo(new UpdateTodoRequest
                    {
                        PaperId = current.PaperId,
                        TodoId = current.Id,
                        Done = true
                    });
                }

                if (_settings.AutoSelectNextTodo)
                {
                    SelectNextOpenTodo(current.PaperId, current.Id);
                }
                else
                {
                    RefreshTodoOptions(saveIfMissing: true);
                }
            }
            catch (PaperTodoPluginException ex)
            {
                _todoLoadError = "完成关联待办失败：" + ex.Message;
            }
            catch (Exception ex)
            {
                _todoLoadError = "完成关联待办失败：" + ex.GetBaseException().Message;
            }
        }

        private void SelectNextOpenTodo(string previousPaperId, string previousTodoId)
        {
            try
            {
                var next = _context.Workspace.ListTodos(includeBlank: false)
                    .Where(item => !item.Done)
                    .OrderBy(item => item.PaperTitle, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(item => item.Order)
                    .FirstOrDefault(item =>
                        !string.Equals(item.PaperId, previousPaperId, StringComparison.Ordinal) ||
                        !string.Equals(item.Id, previousTodoId, StringComparison.Ordinal));
                if (next == null)
                {
                    ClearLinkedTodo(save: false);
                }
                else
                {
                    _state.LinkedPaperId = next.PaperId;
                    _state.LinkedTodoId = next.Id;
                }
                RefreshTodoOptions(saveIfMissing: true);
            }
            catch (Exception ex)
            {
                _todoLoadError = "选择下一项失败：" + ex.GetBaseException().Message;
            }
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
                return;
            }
            UpdateView();
        }

        private void OnTodoSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressTodoSelection)
            {
                return;
            }

            if (_todoBox.SelectedItem is not TodoOption option || option.IsNone)
            {
                ClearLinkedTodo(save: true);
            }
            else
            {
                _state.LinkedPaperId = option.PaperId;
                _state.LinkedTodoId = option.TodoId;
                _todoLoadError = "";
                SaveState();
            }
            UpdateView();
        }

        private void ClearLinkedTodo(bool save)
        {
            var changed =
                !string.IsNullOrWhiteSpace(_state.LinkedPaperId) ||
                !string.IsNullOrWhiteSpace(_state.LinkedTodoId);
            _state.LinkedPaperId = "";
            _state.LinkedTodoId = "";
            if (save && changed)
            {
                SaveState();
            }
        }

        private void QueueTodoRefresh()
        {
            if (_disposed)
            {
                return;
            }
            _hostRefreshTimer.Stop();
            _hostRefreshTimer.Start();
        }

        private void RefreshTodoOptions(bool saveIfMissing)
        {
            if (!CanReadTodos())
            {
                return;
            }

            try
            {
                var linkedPaperId = _state.LinkedPaperId;
                var linkedTodoId = _state.LinkedTodoId;
                var todos = _context.Workspace.ListTodos(includeBlank: false)
                    .Where(item => !item.Done ||
                        (string.Equals(item.PaperId, linkedPaperId, StringComparison.Ordinal) &&
                         string.Equals(item.Id, linkedTodoId, StringComparison.Ordinal)))
                    .OrderBy(item => item.PaperTitle, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(item => item.Order)
                    .Select(item => new TodoOption(
                        item.PaperId,
                        item.Id,
                        TodoLabel(item),
                        item.Text,
                        item.Done))
                    .ToList();
                todos.Insert(0, new TodoOption("", "", "不关联待办", "", false));
                _todoOptions = todos;

                var current = todos.FirstOrDefault(item =>
                    string.Equals(item.PaperId, linkedPaperId, StringComparison.Ordinal) &&
                    string.Equals(item.TodoId, linkedTodoId, StringComparison.Ordinal));
                var missing =
                    !string.IsNullOrWhiteSpace(linkedTodoId) &&
                    current == null;
                if (missing)
                {
                    ClearLinkedTodo(save: saveIfMissing);
                    _todoLoadError = "关联待办已删除或不可访问。";
                }

                _suppressTodoSelection = true;
                try
                {
                    _todoBox.ItemsSource = todos;
                    _todoBox.SelectedItem = current ?? todos[0];
                }
                finally
                {
                    _suppressTodoSelection = false;
                }
                if (!missing)
                {
                    _todoLoadError = "";
                }
            }
            catch (PaperTodoPluginException ex)
            {
                _todoLoadError = ex.Message;
            }
            catch (Exception ex)
            {
                _todoLoadError = ex.GetBaseException().Message;
            }
        }

        private static string TodoLabel(TodoSnapshot item)
        {
            var text = string.IsNullOrWhiteSpace(item.Text)
                ? "（空待办）"
                : Compact(item.Text, 46);
            var paper = string.IsNullOrWhiteSpace(item.PaperTitle)
                ? "待办纸"
                : Compact(item.PaperTitle, 24);
            return item.Done
                ? $"✓ {paper} · {text}"
                : $"{paper} · {text}";
        }

        private TodoOption? CurrentTodo() =>
            _todoOptions.FirstOrDefault(item =>
                !item.IsNone &&
                string.Equals(item.PaperId, _state.LinkedPaperId, StringComparison.Ordinal) &&
                string.Equals(item.TodoId, _state.LinkedTodoId, StringComparison.Ordinal));

        private void UpdateView()
        {
            RefreshDailyCounter();
            var remaining = RemainingSeconds();
            var full = DurationSeconds();
            var minutes = remaining / 60;
            var seconds = remaining % 60;
            var modeName = _state.Mode == TimerMode.Focus ? "专注" : "休息";
            var currentTodo = CurrentTodo();

            _todoHost.Visibility = _settings.ShowLinkedTodo
                ? Visibility.Visible
                : Visibility.Collapsed;
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

            if (!string.IsNullOrWhiteSpace(_todoLoadError))
            {
                _todoStatusText.Text = _todoLoadError;
            }
            else if (currentTodo == null)
            {
                _todoStatusText.Text = "本轮未关联待办；选择后可在专注结束时自动完成。";
            }
            else if (currentTodo.Done)
            {
                _todoStatusText.Text = "关联待办已完成。";
            }
            else
            {
                _todoStatusText.Text = _settings.CompleteLinkedTodo
                    ? "专注结束后将完成此待办。"
                    : "仅显示关联，不自动修改待办状态。";
            }

            UpdateModeButtons();
            var capsuleTitle = _settings.TitleStyle switch
            {
                "task" when currentTodo != null =>
                    $"{Compact(currentTodo.Text, 16)} · {minutes:00}:{seconds:00}",
                "status" => _state.IsRunning ? $"{modeName}中" : $"{modeName}已暂停",
                "fixed" => "专注计时器",
                _ => $"{modeName} · {minutes:00}:{seconds:00}"
            };
            var capsuleProgress = full <= 0
                ? 0
                : Math.Clamp((full - remaining) / (double)full, 0, 1);
            _miniView?.Update(
                modeName,
                $"{minutes:00}:{seconds:00}",
                currentTodo == null ? "" : Compact(currentTodo.Text, 30),
                _state.IsRunning ? "进行中" : "已暂停",
                capsuleProgress,
                _state.IsRunning ? "暂停" : remaining < full ? "继续" : "开始");
            SetPaperStatus(capsuleTitle, capsuleProgress, remaining, modeName);
        }

        private static string Compact(string text, int limit)
        {
            var value = (text ?? "").Trim();
            if (value.Length == 0)
            {
                return "待办";
            }
            return value.Length <= limit ? value : value[..Math.Max(1, limit - 1)] + "…";
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
            _todoStatusText.FontFamily = fontFamily;
            _timeText.FontSize = 46 * scale;
            _statusText.FontSize = 12 * scale;
            _completedText.FontSize = 10.5 * scale;
            _durationText.FontSize = 11.5 * scale;
            _todoStatusText.FontSize = 10.5 * scale;
            _timeText.Foreground = text;
            _statusText.Foreground = weak;
            _completedText.Foreground = weak;
            _durationText.Foreground = text;
            _todoStatusText.Foreground = weak;
            _todoHost.Background = background;
            _todoHost.BorderBrush = border;
            _context.Body.Controls.ApplySelectStyle(_todoBox, 11.5 * scale);
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
            _miniView?.ApplyTheme(theme);
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
                    Boolean(root, "showLinkedTodo", true),
                    Boolean(root, "completeLinkedTodo", false),
                    Boolean(root, "autoSelectNextTodo", false),
                    String(root, "sound", "asterisk"),
                    String(root, "titleStyle", "task"));
            }
            catch
            {
                return new Settings(
                    25, 5, 5, 4, false, true, true, true,
                    true, false, false, "asterisk", "task");
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

        private void SetPaperStatus(
            string title,
            double progress,
            int remaining,
            string modeName)
        {
            if (!string.Equals(_lastDisplayTitle, title, StringComparison.Ordinal))
            {
                _lastDisplayTitle = title;
                _context.Paper.SetHeaderText(title);
            }

            var tone = !_state.IsRunning
                ? PaperCapsuleTone.Muted
                : _state.Mode == TimerMode.Focus
                    ? PaperCapsuleTone.Accent
                    : PaperCapsuleTone.Default;
            var progressStep = (int)Math.Round(
                Math.Clamp(progress, 0, 1) * 1000,
                MidpointRounding.AwayFromZero);
            var signature =
                $"{title}\u001f{remaining}\u001f{progressStep}\u001f{tone}\u001f{_settings.ShowProgress}";
            if (string.Equals(_lastCapsuleSignature, signature, StringComparison.Ordinal))
            {
                return;
            }
            _lastCapsuleSignature = signature;

            var minutes = remaining / 60;
            var seconds = remaining % 60;
            var runningText = _state.IsRunning ? "进行中" : "已暂停";
            _context.Paper.SetCapsulePresentation(new PaperCapsulePresentation
            {
                PreferredWidth = PaperCapsulePresentation.AutomaticWidth,
                PlainText = title,
                ToolTip = $"{modeName} · {minutes:00}:{seconds:00} · {runningText}",
                Components = _settings.ShowProgress
                    ? new PaperCapsuleComponent[]
                    {
                        new PaperCapsuleComponent
                        {
                            Kind = PaperCapsuleComponentKind.StatusDot,
                            Tone = tone
                        },
                        new PaperCapsuleComponent
                        {
                            Kind = PaperCapsuleComponentKind.Text,
                            Text = title,
                            Fill = true
                        },
                        new PaperCapsuleComponent
                        {
                            Kind = PaperCapsuleComponentKind.ProgressBar,
                            Value = progress,
                            Width = 30,
                            Tone = tone
                        }
                    }
                    : new PaperCapsuleComponent[]
                    {
                        new PaperCapsuleComponent
                        {
                            Kind = PaperCapsuleComponentKind.StatusDot,
                            Tone = tone
                        },
                        new PaperCapsuleComponent
                        {
                            Kind = PaperCapsuleComponentKind.Text,
                            Text = title,
                            Fill = true
                        }
                    }
            });
        }

        private void SaveState() =>
            _context.SaveStateJson(JsonSerializer.Serialize(_state, JsonOptions));

        private void StartTimer()
        {
            if (_state.IsRunning && !_timer.IsEnabled)
            {
                _timer.Start();
            }
        }

        public void RefreshFromModel()
        {
            RefreshTodoOptions(saveIfMissing: true);
            UpdateView();
        }

        public void CancelInteractions()
        {
            _todoBox.IsDropDownOpen = false;
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
            if (!visible)
            {
                _hostRefreshTimer.Stop();
                StartTimer();
                return;
            }

            if (CompleteExpiredPhase())
            {
                SaveState();
            }
            RefreshTodoOptions(saveIfMissing: true);
            StartTimer();
            UpdateView();
        }

        public void OnPresentationChanged(bool visible)
        {
            _root.IsHitTestVisible = visible;
            if (!visible)
            {
                _todoBox.IsDropDownOpen = false;
            }
        }

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
            _hostRefreshTimer.Stop();
            _timer.Tick -= OnTick;
            _subscription?.Dispose();
            _miniView = null;
        }
    }
}
