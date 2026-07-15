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
        Visual,
        Shortcuts
    }

    private enum ShortcutUiStatus
    {
        Disabled,
        Unassigned,
        Registered,
        Duplicate,
        SystemOccupied,
        RegistrationFailed,
        PendingSave
    }

    private SettingsPage _settingsPage;
    private GlobalHotkeyManager? _globalHotkeys;
    private Dictionary<string, string>? _shortcutDraft;
    private Dictionary<string, bool>? _shortcutEnabledDraft;
    private string? _shortcutRecordingCommandId;
    private readonly HashSet<string> _shortcutDuplicateIds = new(StringComparer.Ordinal);
    private string? _shortcutApplyFailureId;
    private GlobalShortcutRegistrationFailure _shortcutApplyFailure;

    private void InitializeGlobalHotkeys()
    {
        DisposeGlobalHotkeys();
        State.GlobalHotkeys = GlobalShortcutCatalog.NormalizeBindings(State.GlobalHotkeys);
        State.GlobalHotkeyEnabled = GlobalShortcutCatalog.NormalizeEnabled(State.GlobalHotkeyEnabled);

        var manager = new GlobalHotkeyManager();
        manager.Invoked += OnGlobalHotkeyInvoked;
        _globalHotkeys = manager;

        var enabledCommandIds = GlobalShortcutCatalog.ExecutableIds
            .Where(id => State.GlobalHotkeyEnabled.GetValueOrDefault(id))
            .ToArray();
        if (!manager.TryApply(
                State.GlobalHotkeys,
                enabledCommandIds,
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
        if (definition.IsEdgeCapsule && HasDeepCapsuleReorderDragInProgress())
        {
            return;
        }

        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (definition.IsEdgeCapsule)
            {
                ActivateEdgeCapsuleShortcut(
                    definition.PreferredCapsuleSide,
                    definition.EdgeOrdinal);
                return;
            }

            ExecuteStartupCommand(new StartupCommand(definition.StartupCommandKind));
        }, DispatcherPriority.Input);
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

        if (!ShortcutGesture.TryParse(binding, out var gesture) ||
            !TrySetRecordedShortcutDraft(recordingCommandId, gesture))
        {
            return true;
        }

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
        _shortcutEnabledDraft ??= new Dictionary<string, bool>(
            GlobalShortcutCatalog.NormalizeEnabled(State.GlobalHotkeyEnabled),
            StringComparer.Ordinal);
        RefreshShortcutDuplicateIds();
    }

    private bool TrySetRecordedShortcutDraft(string commandId, ShortcutGesture gesture)
    {
        var definition = GlobalShortcutCatalog.Find(commandId);
        if (definition == null)
        {
            return false;
        }

        EnsureShortcutDraft();
        if (definition.IsEdgeCapsule)
        {
            if (!ShortcutGesture.HasExactlyTwoModifiers(gesture.Modifiers) ||
                !ShortcutGesture.IsEdgeOrdinalKey(gesture.Key, definition.EdgeOrdinal))
            {
                return false;
            }

            SetEdgeShortcutModifiersDraft(definition.Group, gesture.Modifiers);
        }
        else
        {
            _shortcutDraft![commandId] = gesture.ToStorageString();
        }

        _shortcutEnabledDraft![commandId] = true;
        return true;
    }

    private void SetEdgeShortcutModifiersDraft(GlobalShortcutGroup group, ModifierKeys modifiers)
    {
        foreach (var definition in GlobalShortcutCatalog.Definitions.Where(item => item.Group == group))
        {
            _shortcutDraft![definition.Id] = ShortcutGesture.ForEdgeOrdinal(
                modifiers,
                definition.EdgeOrdinal).ToStorageString();
        }
    }

    private void DiscardShortcutDraft()
    {
        _shortcutDraft = null;
        _shortcutEnabledDraft = null;
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
            if (GlobalShortcutCatalog.Find(commandId)?.IsEdgeCapsule == true)
            {
                return;
            }

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
        var definition = GlobalShortcutCatalog.Find(commandId);
        if (definition?.IsEdgeCapsule == true &&
            (!ShortcutGesture.HasExactlyTwoModifiers(modifiers) ||
             !ShortcutGesture.IsEdgeOrdinalKey(key, definition.EdgeOrdinal)))
        {
            return;
        }
        if (!TrySetRecordedShortcutDraft(commandId, gesture))
        {
            return;
        }

        _shortcutRecordingCommandId = null;
        _shortcutApplyFailureId = null;
        _shortcutApplyFailure = GlobalShortcutRegistrationFailure.None;
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
                _shortcutEnabledDraft![definition.Id] = definition.DefaultEnabled;
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
        foreach (var group in new[]
                 {
                     GlobalShortcutGroup.General,
                     GlobalShortcutGroup.EdgeLeft,
                     GlobalShortcutGroup.EdgeRight
                 })
        {
            rows.Children.Add(BuildShortcutGroupLabel(group));
            foreach (var definition in GlobalShortcutCatalog.Definitions.Where(item => item.Group == group))
            {
                rows.Children.Add(BuildShortcutRow(definition));
            }
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
        AddShortcutHeader(grid, Strings.Get("ShortcutEnabledHeader"), 0);
        AddShortcutHeader(grid, Strings.Get("ShortcutCommandHeader"), 1);
        AddShortcutHeader(grid, Strings.Get("ShortcutKeyHeader"), 2);
        AddShortcutHeader(grid, Strings.Get("ShortcutStatusHeader"), 3);
        return grid;
    }

    private static UIElement BuildShortcutGroupLabel(GlobalShortcutGroup group)
    {
        var key = group switch
        {
            GlobalShortcutGroup.EdgeLeft => "ShortcutGroupLeftPreferred",
            GlobalShortcutGroup.EdgeRight => "ShortcutGroupRightPreferred",
            _ => "ShortcutGroupGeneral"
        };
        return new TextBlock
        {
            Text = Strings.Get(key),
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(11.5),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 9, 0, 2)
        };
    }

    private UIElement BuildShortcutRow(GlobalShortcutDefinition definition)
    {
        var grid = ShortcutRowGrid();
        grid.MinHeight = 34;

        var enabledToggle = new CheckBox
        {
            IsChecked = _shortcutEnabledDraft![definition.Id],
            Foreground = TrayTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = Strings.Get("ShortcutEnableTip"),
            Style = BuildSettingsCheckBoxStyle()
        };
        enabledToggle.Click += (_, _) =>
        {
            SetShortcutEnabledImmediately(definition, enabledToggle.IsChecked == true);
        };
        Grid.SetColumn(enabledToggle, 0);
        grid.Children.Add(enabledToggle);

        var label = new TextBlock
        {
            Text = definition.IsEdgeCapsule
                ? Strings.Format(definition.LabelKey, definition.EdgeOrdinal)
                : Strings.Get(definition.LabelKey),
            Foreground = TrayTextBrush,
            FontSize = AppTypography.Scale(12.5),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 1);
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
        if (definition.IsEdgeCapsule)
        {
            keyButton.ToolTip = Strings.Get("ShortcutEdgeKeyTip");
        }
        keyButton.Click += (_, _) =>
        {
            _shortcutRecordingCommandId = definition.Id;
            _shortcutApplyFailureId = null;
            RefreshSettingsWindowContent();
            FocusShortcutRecorder();
        };
        Grid.SetColumn(keyButton, 2);
        grid.Children.Add(keyButton);

        var status = ShortcutStatusFor(definition);
        var statusText = new TextBlock
        {
            Text = Strings.Get(StatusResourceKey(status)),
            Foreground = StatusBrush(status),
            FontSize = AppTypography.Scale(11.5),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 4, 0)
        };
        Grid.SetColumn(statusText, 3);
        grid.Children.Add(statusText);

        if (!definition.IsEdgeCapsule)
        {
            var clear = SettingsIconButton("×", Strings.Get("ShortcutClear"));
            clear.Click += (_, _) =>
            {
                _shortcutDraft![definition.Id] = "";
                _shortcutRecordingCommandId = null;
                _shortcutApplyFailureId = null;
                _shortcutApplyFailure = GlobalShortcutRegistrationFailure.None;
                RefreshSettingsWindowContent();
            };
            Grid.SetColumn(clear, 4);
            grid.Children.Add(clear);
        }

        var restore = SettingsIconButton("↺", Strings.Get("ShortcutRestoreDefault"));
        restore.Click += (_, _) =>
        {
            if (definition.IsEdgeCapsule &&
                ShortcutGesture.TryParse(definition.DefaultGesture, out var defaultGesture))
            {
                SetEdgeShortcutModifiersDraft(definition.Group, defaultGesture.Modifiers);
            }
            else
            {
                _shortcutDraft![definition.Id] = definition.DefaultGesture;
            }
            _shortcutEnabledDraft![definition.Id] =
                definition.DefaultEnabled || !string.IsNullOrWhiteSpace(definition.DefaultGesture);
            _shortcutRecordingCommandId = null;
            _shortcutApplyFailureId = null;
            _shortcutApplyFailure = GlobalShortcutRegistrationFailure.None;
            RefreshSettingsWindowContent();
        };
        Grid.SetColumn(restore, 5);
        grid.Children.Add(restore);

        return grid;
    }

    private static Grid ShortcutRowGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(142) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
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
            FontSize = AppTypography.Scale(11),
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
            FontSize = AppTypography.Scale(12),
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
            FontSize = AppTypography.Scale(14),
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
                     .Where(pair =>
                         _shortcutEnabledDraft?.GetValueOrDefault(pair.Key) == true &&
                         !string.IsNullOrWhiteSpace(pair.Value))
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
        var enabled = _shortcutEnabledDraft![definition.Id];
        if (_shortcutApplyFailureId == definition.Id)
        {
            return _shortcutApplyFailure == GlobalShortcutRegistrationFailure.SystemOccupied
                ? ShortcutUiStatus.SystemOccupied
                : ShortcutUiStatus.RegistrationFailed;
        }
        if (enabled && _shortcutDuplicateIds.Contains(definition.Id))
        {
            return ShortcutUiStatus.Duplicate;
        }
        if (!State.GlobalHotkeys.TryGetValue(definition.Id, out var saved) ||
            !string.Equals(saved, binding, StringComparison.Ordinal) ||
            State.GlobalHotkeyEnabled.GetValueOrDefault(definition.Id) != enabled)
        {
            return ShortcutUiStatus.PendingSave;
        }
        if (!enabled)
        {
            return ShortcutUiStatus.Disabled;
        }
        if (string.IsNullOrWhiteSpace(binding))
        {
            return ShortcutUiStatus.Unassigned;
        }
        return _globalHotkeys?.ActiveBindings.ContainsKey(definition.Id) == true
            ? ShortcutUiStatus.Registered
            : ShortcutUiStatus.RegistrationFailed;
    }

    private static string StatusResourceKey(ShortcutUiStatus status)
    {
        return status switch
        {
            ShortcutUiStatus.Disabled => "ShortcutStatusDisabled",
            ShortcutUiStatus.Registered => "ShortcutStatusRegistered",
            ShortcutUiStatus.Duplicate => "ShortcutStatusDuplicate",
            ShortcutUiStatus.SystemOccupied => "ShortcutStatusSystemOccupied",
            ShortcutUiStatus.RegistrationFailed => "ShortcutStatusRegistrationFailed",
            ShortcutUiStatus.PendingSave => "ShortcutStatusPendingSave",
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

    private void SetShortcutEnabledImmediately(GlobalShortcutDefinition definition, bool enabled)
    {
        EnsureShortcutDraft();
        var desiredBindings = new Dictionary<string, string>(State.GlobalHotkeys, StringComparer.Ordinal);
        if (enabled)
        {
            if (definition.IsEdgeCapsule)
            {
                foreach (var groupDefinition in GlobalShortcutCatalog.Definitions.Where(
                             item => item.Group == definition.Group))
                {
                    desiredBindings[groupDefinition.Id] = _shortcutDraft![groupDefinition.Id];
                }
            }
            else
            {
                desiredBindings[definition.Id] = _shortcutDraft![definition.Id];
            }
            desiredBindings = GlobalShortcutCatalog.NormalizeBindings(desiredBindings);
        }

        var desiredEnabled = GlobalShortcutCatalog.NormalizeEnabled(State.GlobalHotkeyEnabled);
        desiredEnabled[definition.Id] = enabled;
        var enabledCommandIds = GlobalShortcutCatalog.ExecutableIds
            .Where(id => desiredEnabled.GetValueOrDefault(id))
            .ToArray();
        var manager = EnsureGlobalHotkeyManager();
        if (!manager.TryApply(
                desiredBindings,
                enabledCommandIds,
                out _shortcutApplyFailureId,
                out _shortcutApplyFailure))
        {
            _shortcutEnabledDraft![definition.Id] =
                State.GlobalHotkeyEnabled.GetValueOrDefault(definition.Id);
            RefreshSettingsWindowContent();
            return;
        }

        State.GlobalHotkeys = desiredBindings;
        State.GlobalHotkeyEnabled = desiredEnabled;
        _shortcutEnabledDraft![definition.Id] = enabled;
        _shortcutApplyFailureId = null;
        _shortcutApplyFailure = GlobalShortcutRegistrationFailure.None;
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private GlobalHotkeyManager EnsureGlobalHotkeyManager()
    {
        _globalHotkeys ??= new GlobalHotkeyManager();
        _globalHotkeys.Invoked -= OnGlobalHotkeyInvoked;
        _globalHotkeys.Invoked += OnGlobalHotkeyInvoked;
        return _globalHotkeys;
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
        var desiredEnabled = GlobalShortcutCatalog.NormalizeEnabled(_shortcutEnabledDraft);
        var enabledCommandIds = GlobalShortcutCatalog.ExecutableIds
            .Where(id => desiredEnabled.GetValueOrDefault(id))
            .ToArray();
        var manager = EnsureGlobalHotkeyManager();

        if (!manager.TryApply(
                desired,
                enabledCommandIds,
                out _shortcutApplyFailureId,
                out _shortcutApplyFailure))
        {
            RefreshSettingsWindowContent();
            return;
        }

        State.GlobalHotkeys = desired;
        State.GlobalHotkeyEnabled = desiredEnabled;
        _shortcutDraft = new Dictionary<string, string>(desired, StringComparer.Ordinal);
        _shortcutEnabledDraft = new Dictionary<string, bool>(desiredEnabled, StringComparer.Ordinal);
        _shortcutApplyFailureId = null;
        _shortcutApplyFailure = GlobalShortcutRegistrationFailure.None;
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ActivateEdgeCapsuleShortcut(string preferredSide, int ordinal)
    {
        if (HasDeepCapsuleReorderDragInProgress() ||
            ordinal is < 1 or > 9 ||
            !State.UseCapsuleMode ||
            !State.UseDeepCapsuleMode)
        {
            return;
        }

        var papers = DeepCapsulePapersInOrder();
        if (papers.Count == 0)
        {
            return;
        }

        var normalizedPreferredSide = DeepCapsuleSides.Normalize(preferredSide);
        var oppositeSide = normalizedPreferredSide == DeepCapsuleSides.Left
            ? DeepCapsuleSides.Right
            : DeepCapsuleSides.Left;
        var queuePlan = EdgeCapsuleQueueCoordinator.Build(
            papers.Select(paper => new EdgeCapsuleQueueMember(paper, QueueKey(paper))),
            State.UseCapsuleCollapseAll);
        var queuesByKey = queuePlan.Queues.ToDictionary(queue => queue.Key, StringComparer.Ordinal);

        foreach (var monitorName in EdgeShortcutMonitorSearchOrder())
        {
            foreach (var side in new[] { normalizedPreferredSide, oppositeSide })
            {
                var queueKey = QueueKey(monitorName, side);
                if (!queuesByKey.TryGetValue(queueKey, out var queue) || queue.Papers.Count < ordinal)
                {
                    continue;
                }

                var target = queue.Papers[ordinal - 1];
                if (_windows.TryGetValue(target.Id, out var window))
                {
                    window.ActivateFromEdgeShortcut();
                }
                return;
            }
        }
    }

    private static IReadOnlyList<string> EdgeShortcutMonitorSearchOrder()
    {
        var currentMonitor = "";
        if (WindowNative.TryGetCursorScreenPosition(out var cursor) &&
            WindowWorkAreaHelper.MonitorAtDeviceScreenPoint(cursor) is { } cursorMonitor)
        {
            currentMonitor = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(cursorMonitor.DeviceName);
        }

        var connectedMonitors = WindowWorkAreaHelper.ConnectedMonitorGeometries();
        var candidateNames = connectedMonitors
            .Select(monitor => WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(monitor.DeviceName))
            .Append(currentMonitor)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(currentMonitor, out var currentGeometry))
        {
            return candidateNames;
        }

        var currentCenterX = (currentGeometry.WorkArea.Left + currentGeometry.WorkArea.Right) / 2.0;
        var currentCenterY = (currentGeometry.WorkArea.Top + currentGeometry.WorkArea.Bottom) / 2.0;
        var candidates = connectedMonitors
            .Select(geometry => (
                Name: WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(geometry.DeviceName),
                Geometry: geometry))
            .Where(candidate => !string.Equals(candidate.Name, currentMonitor, StringComparison.Ordinal))
            .ToList();

        static double CenterX(MonitorGeometry geometry) =>
            (geometry.WorkArea.Left + geometry.WorkArea.Right) / 2.0;
        static double CenterY(MonitorGeometry geometry) =>
            (geometry.WorkArea.Top + geometry.WorkArea.Bottom) / 2.0;

        var left = candidates
            .Where(candidate => CenterX(candidate.Geometry) < currentCenterX - 0.5)
            .OrderBy(candidate => currentCenterX - CenterX(candidate.Geometry))
            .ThenBy(candidate => Math.Abs(currentCenterY - CenterY(candidate.Geometry)))
            .ToList();
        var right = candidates
            .Where(candidate => CenterX(candidate.Geometry) > currentCenterX + 0.5)
            .OrderBy(candidate => CenterX(candidate.Geometry) - currentCenterX)
            .ThenBy(candidate => Math.Abs(currentCenterY - CenterY(candidate.Geometry)))
            .ToList();
        var vertical = candidates
            .Where(candidate => Math.Abs(CenterX(candidate.Geometry) - currentCenterX) <= 0.5)
            .OrderBy(candidate => Math.Abs(currentCenterY - CenterY(candidate.Geometry)))
            .ToList();

        var ordered = new List<string>(candidateNames.Count) { currentMonitor };
        var horizontalDepth = Math.Max(left.Count, right.Count);
        for (var index = 0; index < horizontalDepth; index++)
        {
            if (index < left.Count)
            {
                ordered.Add(left[index].Name);
            }
            if (index < right.Count)
            {
                ordered.Add(right[index].Name);
            }
        }
        ordered.AddRange(vertical.Select(candidate => candidate.Name));
        return ordered;
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
            FontSize = AppTypography.Scale(13),
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
                FontSize = AppTypography.Scale(15),
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
