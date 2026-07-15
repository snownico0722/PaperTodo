using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace PaperTodo;

public sealed partial class AppController
{
    private enum SettingsPage
    {
        General,
        Shortcuts
    }

    private enum ShortcutUiStatus
    {
        Unassigned,
        Registered,
        Duplicate,
        SystemOccupied,
        RegistrationFailed,
        PendingSave,
        PendingBehavior
    }

    private SettingsPage _settingsPage;
    private GlobalHotkeyManager? _globalHotkeys;
    private Dictionary<string, string>? _shortcutDraft;
    private string? _shortcutRecordingCommandId;
    private readonly HashSet<string> _shortcutDuplicateIds = new(StringComparer.Ordinal);
    private string? _shortcutApplyFailureId;
    private GlobalShortcutRegistrationFailure _shortcutApplyFailure;

    private void InitializeGlobalHotkeys()
    {
        DisposeGlobalHotkeys();
        State.GlobalHotkeys = GlobalShortcutCatalog.NormalizeBindings(State.GlobalHotkeys);

        var manager = new GlobalHotkeyManager();
        manager.Invoked += OnGlobalHotkeyInvoked;
        _globalHotkeys = manager;

        var executableBindings = State.GlobalHotkeys
            .Where(pair => GlobalShortcutCatalog.Find(pair.Key)?.IsExecutable == true)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (!manager.TryApply(
                executableBindings,
                GlobalShortcutCatalog.ExecutableIds,
                out _shortcutApplyFailureId,
                out _shortcutApplyFailure))
        {
            return;
        }

        _shortcutApplyFailureId = null;
        _shortcutApplyFailure = GlobalShortcutRegistrationFailure.None;
    }

    private void DisposeGlobalHotkeys()
    {
        if (_globalHotkeys == null)
        {
            return;
        }

        _globalHotkeys.Invoked -= OnGlobalHotkeyInvoked;
        _globalHotkeys.Dispose();
        _globalHotkeys = null;
    }

    private void OnGlobalHotkeyInvoked(string commandId)
    {
        var definition = GlobalShortcutCatalog.Find(commandId);
        if (definition?.IsExecutable != true || IsExiting)
        {
            return;
        }

        if (TryRecordRegisteredGlobalHotkey(commandId))
        {
            return;
        }

        _ = Application.Current.Dispatcher.InvokeAsync(
            () => ExecuteStartupCommand(new StartupCommand(definition.StartupCommandKind)),
            DispatcherPriority.Input);
    }

    private bool TryRecordRegisteredGlobalHotkey(string commandId)
    {
        if (_settingsPage != SettingsPage.Shortcuts ||
            _shortcutRecordingCommandId is not { Length: > 0 } recordingCommandId)
        {
            return false;
        }

        if (_globalHotkeys is not { } hotkeys ||
            !hotkeys.ActiveBindings.TryGetValue(commandId, out var binding) ||
            string.IsNullOrEmpty(binding))
        {
            // A WM_HOTKEY received during recording must never fall through to its old action.
            return true;
        }

        EnsureShortcutDraft();
        _shortcutDraft![recordingCommandId] = binding;
        _shortcutRecordingCommandId = null;
        _shortcutApplyFailureId = null;
        _shortcutApplyFailure = GlobalShortcutRegistrationFailure.None;
        RefreshSettingsWindowContent();
        return true;
    }

    private void EnsureShortcutDraft()
    {
        _shortcutDraft ??= new Dictionary<string, string>(
            GlobalShortcutCatalog.NormalizeBindings(State.GlobalHotkeys),
            StringComparer.Ordinal);
        RefreshShortcutDuplicateIds();
    }

    private void DiscardShortcutDraft()
    {
        _shortcutDraft = null;
        _shortcutRecordingCommandId = null;
        _shortcutDuplicateIds.Clear();
        _shortcutApplyFailureId = null;
        _shortcutApplyFailure = GlobalShortcutRegistrationFailure.None;
    }

    private void OnSettingsWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_settingsPage != SettingsPage.Shortcuts ||
            string.IsNullOrEmpty(_shortcutRecordingCommandId))
        {
            return;
        }

        var commandId = _shortcutRecordingCommandId;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;

        if (key == Key.Escape)
        {
            _shortcutRecordingCommandId = null;
            RefreshSettingsWindowContent();
            return;
        }

        if ((key == Key.Back || key == Key.Delete) && Keyboard.Modifiers == ModifierKeys.None)
        {
            EnsureShortcutDraft();
            _shortcutDraft![commandId] = "";
            _shortcutRecordingCommandId = null;
            _shortcutApplyFailureId = null;
            RefreshSettingsWindowContent();
            return;
        }

        if (ShortcutGesture.IsModifierKey(key))
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None || key == Key.None)
        {
            return;
        }

        var gesture = new ShortcutGesture(key, modifiers);
        EnsureShortcutDraft();
        _shortcutDraft![commandId] = gesture.ToStorageString();
        _shortcutRecordingCommandId = null;
        _shortcutApplyFailureId = null;
        RefreshSettingsWindowContent();
    }

    private UIElement BuildShortcutSettingsPage()
    {
        EnsureShortcutDraft();

        var root = new DockPanel
        {
            LastChildFill = true
        };

        var actions = new Grid
        {
            Margin = new Thickness(0, 10, 2, 0)
        };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var resetAll = SettingsTextButton(Strings.Get("ShortcutRestoreAll"));
        resetAll.Click += (_, _) =>
        {
            EnsureShortcutDraft();
            foreach (var definition in GlobalShortcutCatalog.Definitions)
            {
                _shortcutDraft![definition.Id] = definition.DefaultGesture;
            }
            _shortcutApplyFailureId = null;
            RefreshSettingsWindowContent();
        };
        Grid.SetColumn(resetAll, 1);
        actions.Children.Add(resetAll);

        var save = SettingsTextButton(Strings.Get("ShortcutSave"));
        save.Margin = new Thickness(8, 0, 0, 0);
        save.Background = Theme.ActiveBrush;
        save.Foreground = TrayPaperBrush;
        save.Click += (_, _) => ApplyShortcutDraft();
        Grid.SetColumn(save, 2);
        actions.Children.Add(save);

        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);

        var rows = new StackPanel();
        rows.Children.Add(BuildShortcutHeaderRow());
        foreach (var definition in GlobalShortcutCatalog.Definitions)
        {
            rows.Children.Add(BuildShortcutRow(definition));
        }

        root.Children.Add(new ScrollViewer
        {
            Content = rows,
            MaxHeight = SettingsOptionsMaxHeight() - 8,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            PanningMode = PanningMode.VerticalOnly
        });

        return root;
    }

    private void FocusShortcutRecorder()
    {
        if (_settingsWindow == null)
        {
            return;
        }

        _ = _settingsWindow.Dispatcher.InvokeAsync(() =>
        {
            _settingsWindow.Activate();
            _settingsWindow.Focus();
            Keyboard.Focus(_settingsWindow);
        }, DispatcherPriority.Input);
    }

    private UIElement BuildShortcutHeaderRow()
    {
        var grid = ShortcutRowGrid();
        grid.Margin = new Thickness(0, 0, 0, 4);
        AddShortcutHeader(grid, Strings.Get("ShortcutCommandHeader"), 0);
        AddShortcutHeader(grid, Strings.Get("ShortcutKeyHeader"), 1);
        AddShortcutHeader(grid, Strings.Get("ShortcutStatusHeader"), 2);
        return grid;
    }

    private UIElement BuildShortcutRow(GlobalShortcutDefinition definition)
    {
        var grid = ShortcutRowGrid();
        grid.MinHeight = 34;

        var label = new TextBlock
        {
            Text = Strings.Get(definition.LabelKey),
            Foreground = TrayTextBrush,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var isRecording = _shortcutRecordingCommandId == definition.Id;
        var binding = _shortcutDraft![definition.Id];
        var keyText = isRecording
            ? Strings.Get("ShortcutRecording")
            : DisplayShortcut(binding);

        var keyButton = SettingsTextButton(keyText);
        keyButton.MinWidth = 132;
        keyButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        keyButton.Focusable = true;
        keyButton.FontFamily = AppTypography.UiFontFamily;
        keyButton.Click += (_, _) =>
        {
            _shortcutRecordingCommandId = definition.Id;
            _shortcutApplyFailureId = null;
            RefreshSettingsWindowContent();
            FocusShortcutRecorder();
        };
        Grid.SetColumn(keyButton, 1);
        grid.Children.Add(keyButton);

        var status = ShortcutStatusFor(definition);
        var statusText = new TextBlock
        {
            Text = Strings.Get(StatusResourceKey(status)),
            Foreground = StatusBrush(status),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 4, 0)
        };
        Grid.SetColumn(statusText, 2);
        grid.Children.Add(statusText);

        var clear = SettingsIconButton("×", Strings.Get("ShortcutClear"));
        clear.Click += (_, _) =>
        {
            _shortcutDraft![definition.Id] = "";
            _shortcutRecordingCommandId = null;
            _shortcutApplyFailureId = null;
            RefreshSettingsWindowContent();
        };
        Grid.SetColumn(clear, 3);
        grid.Children.Add(clear);

        var restore = SettingsIconButton("↺", Strings.Get("ShortcutRestoreDefault"));
        restore.Click += (_, _) =>
        {
            _shortcutDraft![definition.Id] = definition.DefaultGesture;
            _shortcutRecordingCommandId = null;
            _shortcutApplyFailureId = null;
            RefreshSettingsWindowContent();
        };
        Grid.SetColumn(restore, 4);
        grid.Children.Add(restore);

        return grid;
    }

    private static Grid ShortcutRowGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(154) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        return grid;
    }

    private static void AddShortcutHeader(Grid grid, string text, int column)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = TrayWeakTextBrush,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = column == 0 ? new Thickness(0) : new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(label, column);
        grid.Children.Add(label);
    }

    private Button SettingsTextButton(string text)
    {
        return new Button
        {
            Content = text,
            Height = 27,
            Padding = new Thickness(10, 0, 10, 0),
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            Foreground = TrayTextBrush,
            FontSize = 12,
            Cursor = Cursors.Hand,
            Focusable = false,
            Style = BuildSettingsCloseButtonStyle()
        };
    }

    private Button SettingsIconButton(string glyph, string toolTip)
    {
        return new Button
        {
            Content = glyph,
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = TrayWeakTextBrush,
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = 14,
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = toolTip,
            Style = BuildSettingsCloseButtonStyle()
        };
    }

    private static string DisplayShortcut(string binding)
    {
        return ShortcutGesture.TryParse(binding, out var gesture) && gesture.Key != Key.None
            ? gesture.ToDisplayString()
            : Strings.Get("ShortcutUnassigned");
    }

    private void RefreshShortcutDuplicateIds()
    {
        _shortcutDuplicateIds.Clear();
        if (_shortcutDraft == null)
        {
            return;
        }

        foreach (var group in _shortcutDraft
                     .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                     .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            foreach (var pair in group)
            {
                _shortcutDuplicateIds.Add(pair.Key);
            }
        }
    }

    private ShortcutUiStatus ShortcutStatusFor(GlobalShortcutDefinition definition)
    {
        RefreshShortcutDuplicateIds();
        var binding = _shortcutDraft![definition.Id];
        if (string.IsNullOrWhiteSpace(binding))
        {
            return ShortcutUiStatus.Unassigned;
        }
        if (_shortcutDuplicateIds.Contains(definition.Id))
        {
            return ShortcutUiStatus.Duplicate;
        }
        if (_shortcutApplyFailureId == definition.Id)
        {
            return _shortcutApplyFailure == GlobalShortcutRegistrationFailure.SystemOccupied
                ? ShortcutUiStatus.SystemOccupied
                : ShortcutUiStatus.RegistrationFailed;
        }
        if (!definition.IsExecutable)
        {
            return ShortcutUiStatus.PendingBehavior;
        }
        if (!State.GlobalHotkeys.TryGetValue(definition.Id, out var saved) ||
            !string.Equals(saved, binding, StringComparison.Ordinal))
        {
            return ShortcutUiStatus.PendingSave;
        }
        return _globalHotkeys?.ActiveBindings.ContainsKey(definition.Id) == true
            ? ShortcutUiStatus.Registered
            : ShortcutUiStatus.RegistrationFailed;
    }

    private static string StatusResourceKey(ShortcutUiStatus status)
    {
        return status switch
        {
            ShortcutUiStatus.Registered => "ShortcutStatusRegistered",
            ShortcutUiStatus.Duplicate => "ShortcutStatusDuplicate",
            ShortcutUiStatus.SystemOccupied => "ShortcutStatusSystemOccupied",
            ShortcutUiStatus.RegistrationFailed => "ShortcutStatusRegistrationFailed",
            ShortcutUiStatus.PendingSave => "ShortcutStatusPendingSave",
            ShortcutUiStatus.PendingBehavior => "ShortcutStatusPendingBehavior",
            _ => "ShortcutStatusUnassigned"
        };
    }

    private static Brush StatusBrush(ShortcutUiStatus status)
    {
        return status switch
        {
            ShortcutUiStatus.Duplicate or ShortcutUiStatus.SystemOccupied or ShortcutUiStatus.RegistrationFailed
                => Brushes.IndianRed,
            ShortcutUiStatus.Registered => Theme.ActiveBrush,
            _ => TrayWeakTextBrush
        };
    }

    private void ApplyShortcutDraft()
    {
        EnsureShortcutDraft();
        RefreshShortcutDuplicateIds();
        if (_shortcutDuplicateIds.Count > 0)
        {
            RefreshSettingsWindowContent();
            return;
        }

        var desired = GlobalShortcutCatalog.NormalizeBindings(_shortcutDraft);
        _globalHotkeys ??= new GlobalHotkeyManager();
        _globalHotkeys.Invoked -= OnGlobalHotkeyInvoked;
        _globalHotkeys.Invoked += OnGlobalHotkeyInvoked;

        if (!_globalHotkeys.TryApply(
                desired,
                GlobalShortcutCatalog.ExecutableIds,
                out _shortcutApplyFailureId,
                out _shortcutApplyFailure))
        {
            RefreshSettingsWindowContent();
            return;
        }

        State.GlobalHotkeys = desired;
        _shortcutDraft = new Dictionary<string, string>(desired, StringComparer.Ordinal);
        _shortcutApplyFailureId = null;
        _shortcutApplyFailure = GlobalShortcutRegistrationFailure.None;
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private UIElement CreateDeepCapsuleTitleMeasureLimitStepper()
    {
        var container = new Border
        {
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 4, 0, 10),
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var valueText = new TextBlock
        {
            Text = DeepCapsuleTitleMeasureLimitText(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TrayTextBrush
        };
        Grid.SetColumn(valueText, 1);

        Border StepButton(string glyph, int column, Action onClick)
        {
            var glyphText = new TextBlock
            {
                Text = glyph,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = AppTypography.SymbolFontFamily,
                FontSize = 15,
                Foreground = TrayTextBrush
            };
            var button = new Border
            {
                Width = 34,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = glyphText
            };
            button.MouseEnter += (_, _) => button.Background = TrayHoverBrush;
            button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
            button.MouseLeftButtonDown += (_, e) =>
            {
                onClick();
                valueText.Text = DeepCapsuleTitleMeasureLimitText();
                e.Handled = true;
            };
            Grid.SetColumn(button, column);
            return button;
        }

        grid.Children.Add(StepButton("−", 0, () =>
        {
            var current = State.DeepCapsuleTitleMeasureCharacterLimit;
            SetDeepCapsuleTitleMeasureCharacterLimit(current == 0 ? PaperTitles.MaxConfigurableTitleLength : current - 1);
        }));
        grid.Children.Add(valueText);
        grid.Children.Add(StepButton("＋", 2, () =>
        {
            var current = State.DeepCapsuleTitleMeasureCharacterLimit;
            SetDeepCapsuleTitleMeasureCharacterLimit(current == 0 ? 0 : current >= PaperTitles.MaxConfigurableTitleLength ? 0 : current + 1);
        }));

        container.Child = grid;
        return container;
    }

    private string DeepCapsuleTitleMeasureLimitText()
    {
        return State.DeepCapsuleTitleMeasureCharacterLimit == 0
            ? Strings.Get("SettingsAllCharacters")
            : State.DeepCapsuleTitleMeasureCharacterLimit.ToString(CultureInfo.InvariantCulture);
    }

    private void SetDeepCapsuleTitleMeasureCharacterLimit(int value)
    {
        var normalized = Math.Clamp(value, 0, PaperTitles.MaxConfigurableTitleLength);
        if (State.DeepCapsuleTitleMeasureCharacterLimit == normalized)
        {
            return;
        }

        State.DeepCapsuleTitleMeasureCharacterLimit = normalized;
        foreach (var window in _windows.Values)
        {
            window.RefreshPaperTitle();
        }
        ArrangeDeepCapsules(animate: true);
        SaveNow();
    }
}
