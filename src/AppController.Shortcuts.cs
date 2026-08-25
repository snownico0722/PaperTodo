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
    private enum ShortcutUiStatus
    {
        Disabled,
        Unassigned,
        Registered,
        Duplicate,
        SystemOccupied,
        RegistrationFailed
    }

    private GlobalHotkeyManager? _globalHotkeys;
    private Dictionary<string, string>? _shortcutDraft;
    private Dictionary<string, bool>? _shortcutEnabledDraft;
    private string? _shortcutRecordingCommandId;
    private readonly HashSet<string> _shortcutDuplicateIds = new(StringComparer.Ordinal);
    private string? _shortcutApplyFailureId;
    private GlobalShortcutRegistrationFailure _shortcutApplyFailure;
    private ShortcutUiStatus? _shortcutApplyFailureStatus;

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
        ClearShortcutApplyFailure();
        if (!manager.TryApply(
                State.GlobalHotkeys,
                enabledCommandIds,
                State.DistinguishNumpadShortcutDigits,
                out _shortcutApplyFailureId,
                out _shortcutApplyFailure))
        {
            _shortcutApplyFailureStatus = FailureStatus(_shortcutApplyFailure);
            return;
        }

        _shortcutApplyFailureId = null;
        _shortcutApplyFailure = GlobalShortcutRegistrationFailure.None;
        _shortcutApplyFailureStatus = null;
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
            if (definition.ExperimentalKind != ExperimentalShortcutKind.None)
            {
                ExecuteExperimentalShortcut(definition);
                return;
            }

            if (definition.IsEdgeCapsule)
            {
                ActivateEdgeCapsuleShortcut(
                    definition.PreferredCapsuleSide,
                    definition.EdgeOrdinal);
                return;
            }

            ExecuteGlobalShortcutCommand(definition);
        }, DispatcherPriority.Input);
    }

    private bool TryRecordRegisteredGlobalHotkey(string commandId)
    {
        if (!SupportsShortcutRecording(_settingsPage) ||
            _shortcutRecordingCommandId is not { Length: > 0 } recordingCommandId)
        {
            return false;
        }

        if (_globalHotkeys is not { } hotkeys ||
            !hotkeys.ActiveBindings.TryGetValue(commandId, out var binding) ||
            string.IsNullOrEmpty(binding))
        {
            return true;
        }

        if (!ShortcutGesture.TryParse(binding, out var gesture) ||
            !TrySetRecordedShortcutDraft(recordingCommandId, gesture))
        {
            return true;
        }

        _shortcutRecordingCommandId = null;
        ApplyShortcutDraft(recordingCommandId);
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
            if (!ShortcutGesture.HasEdgePrefixModifiers(gesture.Modifiers) ||
                ShortcutGesture.IsModifierKey(gesture.Key) ||
                gesture.Key == Key.None)
            {
                return false;
            }

            if (GlobalShortcutCatalog.TryGetEdgePrefixModifiers(
                    _shortcutDraft!,
                    GlobalShortcutCatalog.OppositeEdgeGroup(definition.Group),
                    out var oppositeModifiers) &&
                oppositeModifiers == gesture.Modifiers)
            {
                return false;
            }

            SetEdgeShortcutModifiersDraft(definition.Group, gesture.Modifiers);
            SetEdgeShortcutEnabledDraft(definition.Group, enabled: true);
        }
        else
        {
            _shortcutDraft![commandId] = gesture.ToStorageString();
            if (definition.Group != GlobalShortcutGroup.Labs)
            {
                _shortcutEnabledDraft![commandId] = true;
            }
        }

        return true;
    }

    private void SetEdgeShortcutModifiersDraft(GlobalShortcutGroup group, ModifierKeys modifiers)
    {
        foreach (var definition in GlobalShortcutCatalog.DefinitionsInGroup(group))
        {
            _shortcutDraft![definition.Id] = ShortcutGesture.ForEdgeOrdinal(
                modifiers,
                definition.EdgeOrdinal).ToStorageString();
        }
    }

    private void SetEdgeShortcutEnabledDraft(GlobalShortcutGroup group, bool enabled)
    {
        foreach (var definition in GlobalShortcutCatalog.DefinitionsInGroup(group))
        {
            _shortcutEnabledDraft![definition.Id] = enabled;
        }
    }

    private void ResetShortcutDraftToSavedState()
    {
        _shortcutDraft = new Dictionary<string, string>(
            GlobalShortcutCatalog.NormalizeBindings(State.GlobalHotkeys),
            StringComparer.Ordinal);
        _shortcutEnabledDraft = new Dictionary<string, bool>(
            GlobalShortcutCatalog.NormalizeEnabled(State.GlobalHotkeyEnabled),
            StringComparer.Ordinal);
        RefreshShortcutDuplicateIds();
    }

    private void ClearShortcutApplyFailure()
    {
        _shortcutApplyFailureId = null;
        _shortcutApplyFailure = GlobalShortcutRegistrationFailure.None;
        _shortcutApplyFailureStatus = null;
    }

    private void RefreshShortcutSettingsUi()
    {
        if (_settingsPage == SettingsPage.Labs)
        {
            RefreshSettingsRegions("labs.passive");
            return;
        }

        RefreshSettingsWindowContent();
    }

    private static ShortcutUiStatus? FailureStatus(
        GlobalShortcutRegistrationFailure failure) => failure switch
    {
        GlobalShortcutRegistrationFailure.Conflict => ShortcutUiStatus.Duplicate,
        GlobalShortcutRegistrationFailure.SystemOccupied => ShortcutUiStatus.SystemOccupied,
        _ => null
    };

    private void RollbackShortcutDraftAfterFailure(
        string? failedCommandId,
        GlobalShortcutRegistrationFailure registrationFailure,
        ShortcutUiStatus? status = null)
    {
        ResetShortcutDraftToSavedState();
        _shortcutApplyFailureId = failedCommandId;
        _shortcutApplyFailure = registrationFailure;
        _shortcutApplyFailureStatus = status ?? FailureStatus(registrationFailure);
        RefreshShortcutSettingsUi();
    }

    private void DiscardShortcutDraft()
    {
        _shortcutDraft = null;
        _shortcutEnabledDraft = null;
        _shortcutRecordingCommandId = null;
        _shortcutDuplicateIds.Clear();
        ClearShortcutApplyFailure();
    }

    private static Key ResolveShortcutEventKey(KeyEventArgs e)
    {
        return e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key
        };
    }

    private void OnSettingsWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!SupportsShortcutRecording(_settingsPage) ||
            string.IsNullOrEmpty(_shortcutRecordingCommandId))
        {
            return;
        }

        var commandId = _shortcutRecordingCommandId;
        var key = ResolveShortcutEventKey(e);
        e.Handled = true;

        if (key == Key.Escape)
        {
            _shortcutRecordingCommandId = null;
            RefreshShortcutSettingsUi();
            return;
        }

        if ((key == Key.Back || key == Key.Delete) && Keyboard.Modifiers == ModifierKeys.None)
        {
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
        if (!TrySetRecordedShortcutDraft(commandId, gesture))
        {
            return;
        }

        _shortcutRecordingCommandId = null;
        ApplyShortcutDraft(commandId);
    }

    private void OnSettingsWindowPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!SupportsShortcutRecording(_settingsPage) ||
            _shortcutRecordingCommandId is not { Length: > 0 } commandId ||
            GlobalShortcutCatalog.Find(commandId) is not { IsEdgeCapsule: true } definition)
        {
            return;
        }

        var key = ResolveShortcutEventKey(e);
        var releasedModifier = key switch
        {
            Key.LeftCtrl or Key.RightCtrl => ModifierKeys.Control,
            Key.LeftAlt or Key.RightAlt => ModifierKeys.Alt,
            Key.LeftShift or Key.RightShift => ModifierKeys.Shift,
            Key.LWin or Key.RWin => ModifierKeys.Windows,
            _ => ModifierKeys.None
        };
        if (releasedModifier == ModifierKeys.None)
        {
            return;
        }

        e.Handled = true;
        var modifiers = Keyboard.Modifiers | releasedModifier;
        var gesture = ShortcutGesture.ForEdgeOrdinal(modifiers, definition.EdgeOrdinal);
        if (!TrySetRecordedShortcutDraft(commandId, gesture))
        {
            return;
        }

        _shortcutRecordingCommandId = null;
        ApplyShortcutDraft(commandId);
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
            Margin = new Thickness(0, 14, 2, 4)
        };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var resetAll = SettingsTextButton(Strings.Get("SettingsRestorePageDefaults"));
        resetAll.MinWidth = 108;
        resetAll.Padding = new Thickness(18, 0, 18, 0);
        resetAll.Click += (_, _) =>
        {
            EnsureShortcutDraft();
            foreach (var definition in GlobalShortcutCatalog.Definitions
                         .Where(item => item.Group != GlobalShortcutGroup.Labs))
            {
                _shortcutDraft![definition.Id] = definition.DefaultGesture;
                _shortcutEnabledDraft![definition.Id] = definition.DefaultEnabled;
            }

            State.OpenEdgeCapsuleShortcutAtCursor = true;
            State.PreserveLinkedPaperHiddenStateInVisibilityShortcuts = true;
            ClearVisibilityShortcutRestoreSnapshot();
            _shortcutRecordingCommandId = null;
            ApplyShortcutDraft();
        };
        Grid.SetColumn(resetAll, 1);
        actions.Children.Add(resetAll);

        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);

        var rows = new StackPanel();
        rows.Children.Add(BuildShortcutHeaderRow());

        rows.Children.Add(BuildShortcutGroupLabel(GlobalShortcutGroup.General));
        foreach (var definition in GlobalShortcutCatalog.DefinitionsInGroup(GlobalShortcutGroup.General))
        {
            rows.Children.Add(BuildShortcutRow(definition));
            if (definition.Id == GlobalShortcutCatalog.Hide &&
                State.AdvancedSettingsMode)
            {
                rows.Children.Add(SettingsToggle(
                    Strings.Get("ShortcutPreserveLinkedPaperHiddenState"),
                    State.PreserveLinkedPaperHiddenStateInVisibilityShortcuts,
                    TogglePreserveLinkedPaperHiddenStateInVisibilityShortcuts));
            }
        }
        var distinguishNumpadToggle = SettingsToggle(
            Strings.Get("SettingsDistinguishNumpadShortcutDigits"),
            State.DistinguishNumpadShortcutDigits,
            ToggleDistinguishNumpadShortcutDigits);
        distinguishNumpadToggle.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = Strings.Get("SettingsDistinguishNumpadShortcutDigits"),
                    Foreground = TrayTextBrush,
                    FontSize = AppTypography.Scale(13),
                    VerticalAlignment = VerticalAlignment.Center
                },
                CreateSettingsHintGlyph(
                    "TipSettingsDistinguishNumpadShortcutDigits",
                    new Thickness(4, 0, 0, 0))
            }
        };
        rows.Children.Add(distinguishNumpadToggle);

        var edgeQueueTipLabelWidth = MeasureShortcutTipLabelMinWidth(
            (Strings.Get("ShortcutEdgeLeftSequence"), AppTypography.Scale(12.5), FontWeights.Normal),
            (Strings.Get("ShortcutEdgeRightSequence"), AppTypography.Scale(12.5), FontWeights.Normal));

        rows.Children.Add(BuildShortcutInlineHintLabel(
            Strings.Get("ShortcutGroupEdgeSequences"),
            "ShortcutGroupEdgeSequencesTip",
            isGroupHeader: true));
        foreach (var group in new[] { GlobalShortcutGroup.EdgeLeft, GlobalShortcutGroup.EdgeRight })
        {
            rows.Children.Add(BuildShortcutRow(
                GlobalShortcutCatalog.EdgeSequenceUiDefinition(group),
                edgeTipLabelMinWidth: edgeQueueTipLabelWidth));
        }

        var openAtCursorToggle = SettingsToggle(
            Strings.Get("SettingsOpenEdgeCapsuleShortcutAtCursor"),
            State.OpenEdgeCapsuleShortcutAtCursor,
            ToggleOpenEdgeCapsuleShortcutAtCursor);
        openAtCursorToggle.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = Strings.Get("SettingsOpenEdgeCapsuleShortcutAtCursor"),
                    Foreground = TrayTextBrush,
                    FontSize = AppTypography.Scale(13),
                    VerticalAlignment = VerticalAlignment.Center
                },
                CreateSettingsHintGlyph("TipOpenEdgeCapsuleShortcutAtCursor", new Thickness(4, 0, 0, 0))
            }
        };
        rows.Children.Add(openAtCursorToggle);

        root.Children.Add(rows);

        return root;
    }

    private static bool SupportsShortcutRecording(SettingsPage page)
    {
        return page is SettingsPage.Shortcuts or SettingsPage.Labs;
    }

    private UIElement BuildLabsShortcutSetting(
        GlobalShortcutDefinition definition,
        string tipKey)
    {
        EnsureShortcutDraft();

        var card = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 3, 0, 5),
            Margin = new Thickness(0, 1, 0, 3)
        };
        var content = new StackPanel();
        var enabled = _shortcutEnabledDraft!.GetValueOrDefault(definition.Id);
        content.Children.Add(WrapWithHint(
            SettingsToggle(
                Strings.Get(definition.LabelKey),
                enabled,
                () => SetShortcutEnabledImmediately(definition, !enabled)),
            tipKey));

        var shortcutRow = new Grid
        {
            Margin = new Thickness(0, 7, 0, 0)
        };
        shortcutRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shortcutRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shortcutRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = Strings.Get("LabsShortcut"),
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(11.5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 10, 0)
        };
        Grid.SetColumn(label, 0);
        shortcutRow.Children.Add(label);

        var isRecording = _shortcutRecordingCommandId == definition.Id;
        var keyButton = SettingsTextButton(isRecording
            ? Strings.Get("ShortcutRecording")
            : DisplayShortcut(_shortcutDraft![definition.Id]));
        keyButton.MinWidth = 150;
        keyButton.HorizontalAlignment = HorizontalAlignment.Left;
        keyButton.Focusable = true;
        keyButton.Click += (_, _) =>
        {
            _shortcutRecordingCommandId = definition.Id;
            ClearShortcutApplyFailure();
            RefreshShortcutSettingsUi();
            FocusShortcutRecorder();
        };
        Grid.SetColumn(keyButton, 1);
        shortcutRow.Children.Add(keyButton);

        var status = ShortcutStatusFor(definition);
        var statusText = new TextBlock
        {
            Text = Strings.Get(StatusResourceKey(status)),
            Foreground = StatusBrush(status),
            FontSize = AppTypography.Scale(11.5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 2, 0)
        };
        Grid.SetColumn(statusText, 2);
        shortcutRow.Children.Add(statusText);

        content.Children.Add(shortcutRow);
        card.Child = content;
        return card;
    }

    private void ToggleOpenEdgeCapsuleShortcutAtCursor()
    {
        State.OpenEdgeCapsuleShortcutAtCursor = !State.OpenEdgeCapsuleShortcutAtCursor;
        MarkDirty();
    }

    private void ToggleDistinguishNumpadShortcutDigits()
    {
        var desiredMode = !State.DistinguishNumpadShortcutDigits;
        var desiredBindings = GlobalShortcutCatalog.NormalizeBindings(State.GlobalHotkeys);
        var desiredEnabled = GlobalShortcutCatalog.NormalizeEnabled(State.GlobalHotkeyEnabled);
        if (!desiredMode &&
            NumpadEquivalentConflictIds(desiredBindings, desiredEnabled).Count > 0)
        {
            ShowNumpadShortcutModeConflict();
            RefreshSettingsWindowContent();
            return;
        }

        var enabledCommandIds = GlobalShortcutCatalog.ExecutableIds
            .Where(id => desiredEnabled.GetValueOrDefault(id))
            .ToArray();
        var manager = EnsureGlobalHotkeyManager();

        SuspendPluginShortcutRegistrations();
        if (!manager.TryApply(
                desiredBindings,
                enabledCommandIds,
                desiredMode,
                out _,
                out _))
        {
            RefreshPluginShortcuts();
            ShowNumpadShortcutModeConflict();
            RefreshSettingsWindowContent();
            return;
        }

        State.DistinguishNumpadShortcutDigits = desiredMode;
        RefreshPluginShortcuts();
        ClearShortcutApplyFailure();
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ShowNumpadShortcutModeConflict()
    {
        if (_settingsWindow != null)
        {
            PaperNoticeDialog.Show(
                _settingsWindow,
                Strings.Get("ShortcutNumpadModeConflictTitle"),
                Strings.Get("ShortcutNumpadModeConflictMessage"));
        }
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
            GlobalShortcutGroup.EdgeLeft or GlobalShortcutGroup.EdgeRight => "ShortcutGroupEdgeSequences",
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

    private UIElement BuildShortcutRow(
        GlobalShortcutDefinition definition,
        double edgeTipLabelMinWidth = 0)
    {
        var grid = ShortcutRowGrid();
        grid.MinHeight = 34;
        var isEdgeSequence = definition.IsEdgeCapsule;

        var enabledToggle = new CheckBox
        {
            IsChecked = _shortcutEnabledDraft![definition.Id],
            Foreground = TrayTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = Strings.Get(isEdgeSequence ? "ShortcutEdgeEnableTip" : "ShortcutEnableTip"),
            Style = BuildSettingsCheckBoxStyle()
        };
        enabledToggle.Click += (_, _) =>
        {
            SetShortcutEnabledImmediately(definition, enabledToggle.IsChecked == true);
        };
        Grid.SetColumn(enabledToggle, 0);
        grid.Children.Add(enabledToggle);

        if (isEdgeSequence)
        {
            var labelWithTip = BuildShortcutInlineHintLabel(
                Strings.Get(definition.LabelKey),
                definition.Group == GlobalShortcutGroup.EdgeLeft
                    ? "ShortcutEdgeLeftTip"
                    : "ShortcutEdgeRightTip",
                isGroupHeader: false,
                edgeTipLabelMinWidth);
            Grid.SetColumn(labelWithTip, 1);
            grid.Children.Add(labelWithTip);
        }
        else
        {
            var label = new TextBlock
            {
                Text = Strings.Get(definition.LabelKey),
                Foreground = TrayTextBrush,
                FontSize = AppTypography.Scale(12.5),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);
        }

        var isRecording = _shortcutRecordingCommandId == definition.Id;
        var binding = _shortcutDraft![definition.Id];

        if (isEdgeSequence)
        {
            var keyCell = new Grid();
            keyCell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            keyCell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var keyText = isRecording
                ? Strings.Get("ShortcutRecording")
                : DisplayEdgePrefixShortcut(binding);
            var keyButton = SettingsTextButton(keyText);
            keyButton.MinWidth = 96;
            keyButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            keyButton.Focusable = true;
            keyButton.FontFamily = AppTypography.UiFontFamily;
            keyButton.ToolTip = Strings.Get("ShortcutEdgeKeyTip");
            keyButton.Click += (_, _) =>
            {
                _shortcutRecordingCommandId = definition.Id;
                ClearShortcutApplyFailure();
                RefreshSettingsWindowContent();
                FocusShortcutRecorder();
            };
            Grid.SetColumn(keyButton, 0);
            keyCell.Children.Add(keyButton);

            var fixedTail = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0),
                ToolTip = Strings.Get("ShortcutEdgeDigitsFixedTip")
            };
            fixedTail.Children.Add(new TextBlock
            {
                Text = "+",
                Foreground = TrayWeakTextBrush,
                Opacity = 0.45,
                FontSize = AppTypography.Scale(11),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0),
                IsHitTestVisible = false
            });
            fixedTail.Children.Add(new Border
            {
                Background = TrayHoverBrush,
                BorderBrush = TrayBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                Opacity = 0.92,
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = Strings.Get("ShortcutEdgeDigitsFixed"),
                    Foreground = TrayWeakTextBrush,
                    FontSize = AppTypography.Scale(11.5),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            });
            Grid.SetColumn(fixedTail, 1);
            keyCell.Children.Add(fixedTail);

            Grid.SetColumn(keyCell, 2);
            grid.Children.Add(keyCell);
        }
        else
        {
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
                ClearShortcutApplyFailure();
                RefreshSettingsWindowContent();
                FocusShortcutRecorder();
            };
            Grid.SetColumn(keyButton, 2);
            grid.Children.Add(keyButton);
        }

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

        var restore = SettingsIconButton("↺", Strings.Get("ShortcutRestoreDefault"));
        restore.Click += (_, _) =>
        {
            EnsureShortcutDraft();
            if (isEdgeSequence &&
                ShortcutGesture.TryParse(definition.DefaultGesture, out var defaultGesture))
            {
                SetEdgeShortcutModifiersDraft(definition.Group, defaultGesture.Modifiers);
                SetEdgeShortcutEnabledDraft(definition.Group, definition.DefaultEnabled);
            }
            else
            {
                _shortcutDraft![definition.Id] = definition.DefaultGesture;
                _shortcutEnabledDraft![definition.Id] = definition.DefaultEnabled;
            }

            _shortcutRecordingCommandId = null;
            ApplyShortcutDraft(definition.Id);
        };
        Grid.SetColumn(restore, 4);
        grid.Children.Add(restore);

        return grid;
    }

    private UIElement BuildShortcutInlineHintLabel(
        string text,
        string tipKey,
        bool isGroupHeader,
        double labelMinWidth = 0)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = isGroupHeader ? new Thickness(0, 9, 0, 2) : new Thickness(0)
        };

        row.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = isGroupHeader ? TrayWeakTextBrush : TrayTextBrush,
            FontSize = AppTypography.Scale(isGroupHeader ? 11.5 : 12.5),
            FontWeight = isGroupHeader ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MinWidth = labelMinWidth > 0 ? labelMinWidth : 0
        });
        row.Children.Add(CreateSettingsHintGlyph(tipKey, new Thickness(4, 0, 0, 0)));
        return row;
    }

    private double MeasureShortcutTipLabelMinWidth(
        params (string Text, double FontSize, FontWeight Weight)[] labels)
    {
        var pixelsPerDip = _settingsWindow != null
            ? VisualTreeHelper.GetDpi(_settingsWindow).PixelsPerDip
            : 1.0;
        var maxWidth = 0.0;
        foreach (var (text, fontSize, weight) in labels)
        {
            var formatted = new FormattedText(
                text,
                UiLanguages.EffectiveUiCulture,
                FlowDirection.LeftToRight,
                new Typeface(AppTypography.UiFontFamily, FontStyles.Normal, weight, FontStretches.Normal),
                fontSize,
                TrayTextBrush,
                pixelsPerDip);
            maxWidth = Math.Max(maxWidth, formatted.WidthIncludingTrailingWhitespace);
        }

        return Math.Ceiling(maxWidth);
    }

    private static Grid ShortcutRowGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.95, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(172) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
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

    private static string DisplayEdgePrefixShortcut(string binding)
    {
        return ShortcutGesture.TryParse(binding, out var gesture) &&
               ShortcutGesture.HasEdgePrefixModifiers(gesture.Modifiers)
            ? gesture.ToEdgePrefixDisplayString()
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

        MarkEdgePrefixConflictsAsDuplicates();
        if (!State.DistinguishNumpadShortcutDigits && _shortcutEnabledDraft != null)
        {
            _shortcutDuplicateIds.UnionWith(
                NumpadEquivalentConflictIds(_shortcutDraft, _shortcutEnabledDraft));
        }
    }

    private static HashSet<string> NumpadEquivalentConflictIds(
        IReadOnlyDictionary<string, string> bindings,
        IReadOnlyDictionary<string, bool> enabled)
    {
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        var firstCommandByGesture = new Dictionary<ShortcutGesture, string>();
        foreach (var pair in bindings)
        {
            if (!enabled.GetValueOrDefault(pair.Key) ||
                GlobalShortcutCatalog.Find(pair.Key)?.IsEdgeCapsule == true ||
                !ShortcutGesture.TryParse(pair.Value, out var gesture) ||
                !gesture.IsDigitKey)
            {
                continue;
            }

            var normalized = gesture.NormalizeNumpadDigit();
            if (firstCommandByGesture.TryGetValue(normalized, out var firstCommandId))
            {
                conflicts.Add(firstCommandId);
                conflicts.Add(pair.Key);
            }
            else
            {
                firstCommandByGesture[normalized] = pair.Key;
            }
        }
        return conflicts;
    }

    private void MarkEdgePrefixConflictsAsDuplicates()
    {
        if (_shortcutDraft == null || _shortcutEnabledDraft == null)
        {
            return;
        }

        if (!GlobalShortcutCatalog.TryGetEdgePrefixModifiers(
                _shortcutDraft,
                GlobalShortcutGroup.EdgeLeft,
                out var leftModifiers) ||
            !GlobalShortcutCatalog.TryGetEdgePrefixModifiers(
                _shortcutDraft,
                GlobalShortcutGroup.EdgeRight,
                out var rightModifiers) ||
            leftModifiers != rightModifiers)
        {
            return;
        }

        var leftEnabled = GlobalShortcutCatalog.DefinitionsInGroup(GlobalShortcutGroup.EdgeLeft)
            .Any(item => _shortcutEnabledDraft.GetValueOrDefault(item.Id));
        var rightEnabled = GlobalShortcutCatalog.DefinitionsInGroup(GlobalShortcutGroup.EdgeRight)
            .Any(item => _shortcutEnabledDraft.GetValueOrDefault(item.Id));
        if (!leftEnabled || !rightEnabled)
        {
            return;
        }

        foreach (var definition in GlobalShortcutCatalog.DefinitionsInGroup(GlobalShortcutGroup.EdgeLeft)
                     .Concat(GlobalShortcutCatalog.DefinitionsInGroup(GlobalShortcutGroup.EdgeRight)))
        {
            _shortcutDuplicateIds.Add(definition.Id);
        }
    }

    private ShortcutUiStatus ShortcutStatusFor(GlobalShortcutDefinition definition)
    {
        RefreshShortcutDuplicateIds();
        if (definition.IsEdgeCapsule)
        {
            return EdgeSequenceStatusFor(definition.Group);
        }

        var binding = _shortcutDraft![definition.Id];
        var enabled = _shortcutEnabledDraft![definition.Id];
        if (_shortcutApplyFailureId == definition.Id)
        {
            if (_shortcutApplyFailureStatus is { } status)
            {
                return status;
            }
            return _shortcutApplyFailure == GlobalShortcutRegistrationFailure.SystemOccupied
                ? ShortcutUiStatus.SystemOccupied
                : ShortcutUiStatus.RegistrationFailed;
        }
        if (enabled && _shortcutDuplicateIds.Contains(definition.Id))
        {
            return ShortcutUiStatus.Duplicate;
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

    private ShortcutUiStatus EdgeSequenceStatusFor(GlobalShortcutGroup group)
    {
        var definitions = GlobalShortcutCatalog.DefinitionsInGroup(group);
        var representative = definitions[0];
        var enabled = _shortcutEnabledDraft![representative.Id];

        if (_shortcutApplyFailureId is { } failedId &&
            definitions.Any(item => string.Equals(item.Id, failedId, StringComparison.Ordinal)))
        {
            if (_shortcutApplyFailureStatus is { } status)
            {
                return status;
            }
            return _shortcutApplyFailure == GlobalShortcutRegistrationFailure.SystemOccupied
                ? ShortcutUiStatus.SystemOccupied
                : ShortcutUiStatus.RegistrationFailed;
        }

        if (enabled && definitions.Any(item => _shortcutDuplicateIds.Contains(item.Id)))
        {
            return ShortcutUiStatus.Duplicate;
        }

        if (!enabled)
        {
            return ShortcutUiStatus.Disabled;
        }

        if (definitions.Any(item =>
                !_shortcutDraft!.TryGetValue(item.Id, out var binding) ||
                string.IsNullOrWhiteSpace(binding)))
        {
            return ShortcutUiStatus.Unassigned;
        }

        return definitions.All(item => _globalHotkeys?.ActiveBindings.ContainsKey(item.Id) == true)
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
        ClearShortcutApplyFailure();
        var desiredBindings = new Dictionary<string, string>(State.GlobalHotkeys, StringComparer.Ordinal);
        if (enabled)
        {
            if (definition.IsEdgeCapsule)
            {
                foreach (var groupDefinition in GlobalShortcutCatalog.DefinitionsInGroup(definition.Group))
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
        if (definition.IsEdgeCapsule)
        {
            foreach (var groupDefinition in GlobalShortcutCatalog.DefinitionsInGroup(definition.Group))
            {
                desiredEnabled[groupDefinition.Id] = enabled;
            }
        }
        else
        {
            desiredEnabled[definition.Id] = enabled;
        }

        var enabledCommandIds = GlobalShortcutCatalog.ExecutableIds
            .Where(id => desiredEnabled.GetValueOrDefault(id))
            .ToArray();
        var manager = EnsureGlobalHotkeyManager();
        if (!manager.TryApply(
                desiredBindings,
                enabledCommandIds,
                State.DistinguishNumpadShortcutDigits,
                out var failedCommandId,
                out var registrationFailure))
        {
            RollbackShortcutDraftAfterFailure(
                failedCommandId ?? definition.Id,
                registrationFailure);
            return;
        }

        State.GlobalHotkeys = desiredBindings;
        State.GlobalHotkeyEnabled = desiredEnabled;
        if (definition.IsEdgeCapsule)
        {
            SetEdgeShortcutEnabledDraft(definition.Group, enabled);
        }
        else
        {
            _shortcutEnabledDraft![definition.Id] = enabled;
        }

        HandleExperimentalShortcutFeatureChanged(definition, enabled);
        ClearShortcutApplyFailure();
        SaveNow();
        RefreshShortcutSettingsUi();
    }

    private GlobalHotkeyManager EnsureGlobalHotkeyManager()
    {
        _globalHotkeys ??= new GlobalHotkeyManager();
        _globalHotkeys.Invoked -= OnGlobalHotkeyInvoked;
        _globalHotkeys.Invoked += OnGlobalHotkeyInvoked;
        return _globalHotkeys;
    }

    private void ApplyShortcutDraft(string? changedCommandId = null)
    {
        EnsureShortcutDraft();
        ClearShortcutApplyFailure();
        RefreshShortcutDuplicateIds();
        if (_shortcutDuplicateIds.Count > 0)
        {
            RollbackShortcutDraftAfterFailure(
                changedCommandId ?? _shortcutDuplicateIds.FirstOrDefault(),
                GlobalShortcutRegistrationFailure.Conflict,
                ShortcutUiStatus.Duplicate);
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
                State.DistinguishNumpadShortcutDigits,
                out var failedCommandId,
                out var registrationFailure))
        {
            RollbackShortcutDraftAfterFailure(
                failedCommandId ?? changedCommandId,
                registrationFailure);
            return;
        }

        State.GlobalHotkeys = desired;
        State.GlobalHotkeyEnabled = desiredEnabled;
        _shortcutDraft = new Dictionary<string, string>(desired, StringComparer.Ordinal);
        _shortcutEnabledDraft = new Dictionary<string, bool>(desiredEnabled, StringComparer.Ordinal);
        ClearShortcutApplyFailure();
        SaveNow();
        RefreshShortcutSettingsUi();
    }

    private void RestoreLabsShortcutDefaults()
    {
        EnsureShortcutDraft();
        var labDefinitions = GlobalShortcutCatalog.DefinitionsInGroup(GlobalShortcutGroup.Labs);
        var desiredBindings = GlobalShortcutCatalog.NormalizeBindings(State.GlobalHotkeys);
        var desiredEnabled = GlobalShortcutCatalog.NormalizeEnabled(State.GlobalHotkeyEnabled);
        foreach (var definition in labDefinitions)
        {
            desiredBindings[definition.Id] = definition.DefaultGesture;
            desiredEnabled[definition.Id] = definition.DefaultEnabled;
        }

        var enabledCommandIds = GlobalShortcutCatalog.ExecutableIds
            .Where(id => desiredEnabled.GetValueOrDefault(id))
            .ToArray();
        var manager = EnsureGlobalHotkeyManager();
        if (!manager.TryApply(
                desiredBindings,
                enabledCommandIds,
                State.DistinguishNumpadShortcutDigits,
                out var failedCommandId,
                out var registrationFailure))
        {
            RollbackShortcutDraftAfterFailure(failedCommandId, registrationFailure);
            return;
        }

        State.GlobalHotkeys = desiredBindings;
        State.GlobalHotkeyEnabled = desiredEnabled;
        _shortcutDraft = new Dictionary<string, string>(desiredBindings, StringComparer.Ordinal);
        _shortcutEnabledDraft = new Dictionary<string, bool>(desiredEnabled, StringComparer.Ordinal);
        _shortcutRecordingCommandId = null;
        foreach (var definition in labDefinitions)
        {
            HandleExperimentalShortcutFeatureChanged(definition, enabled: false);
        }
        ClearShortcutApplyFailure();
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
