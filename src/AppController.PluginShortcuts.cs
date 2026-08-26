using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    private sealed record PluginShortcutRegistration(
        string CommandId,
        string ProviderId,
        string SettingId,
        string ActionId);

    private sealed record PluginGlobalShortcutRuntime(
        Guid RuntimeId,
        string ProviderId,
        Func<bool> IsActive,
        Action<PaperShortcutActionInvocation> Dispatch);

    private GlobalHotkeyManager? _pluginHotkeys;
    private readonly Dictionary<string, PluginShortcutRegistration> _pluginShortcutRegistrations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShortcutUiStatus> _pluginShortcutStatuses =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginGlobalShortcutRuntime> _pluginShortcutRuntimes =
        new(StringComparer.Ordinal);
    private readonly HashSet<PaperWindow> _pluginShortcutTrackedWindows = [];
    private readonly Dictionary<string, long> _pluginShortcutPaperRecency =
        new(StringComparer.Ordinal);
    private long _pluginShortcutPaperRecencySequence;
    private string? _pluginShortcutRecordingCommandId;

    internal void SetPluginGlobalShortcutRuntime(
        Guid runtimeId,
        string providerId,
        Func<bool> isActive,
        Action<PaperShortcutActionInvocation> dispatch)
    {
        ArgumentNullException.ThrowIfNull(isActive);
        ArgumentNullException.ThrowIfNull(dispatch);
        if (IsExiting)
        {
            return;
        }

        _pluginShortcutRuntimes[providerId] = new PluginGlobalShortcutRuntime(
            runtimeId,
            providerId,
            isActive,
            dispatch);
        RefreshPluginShortcutsAfterRuntimeChange();
    }

    internal void RemovePluginGlobalShortcutRuntime(Guid runtimeId, string providerId)
    {
        if (_pluginShortcutRuntimes.TryGetValue(providerId, out var current) &&
            current.RuntimeId == runtimeId)
        {
            _pluginShortcutRuntimes.Remove(providerId);
            RefreshPluginShortcutsAfterRuntimeChange();
        }
    }

    private void RefreshPluginShortcutsAfterRuntimeChange()
    {
        if (_pluginShortcutRecordingCommandId == null && _shortcutRecordingCommandId == null)
        {
            RefreshPluginShortcuts();
        }
    }

    internal void RefreshPluginShortcuts(
        IReadOnlyDictionary<string, string>? bindingOverrides = null,
        string? excludedCommandId = null)
    {
        if (IsExiting)
        {
            return;
        }

        EnsurePluginShortcutWindowTracking();
        _pluginShortcutRegistrations.Clear();
        _pluginShortcutStatuses.Clear();

        var desiredBindings = new Dictionary<string, string>(StringComparer.Ordinal);
        var reservedCommandIds = new HashSet<string>(StringComparer.Ordinal);
        var activeCommandIds = new HashSet<string>(StringComparer.Ordinal);
        var registrationsByCommand = new Dictionary<string, ShortcutGesture[]>(StringComparer.Ordinal);

        foreach (var descriptor in _paperBodyPlugins.Descriptors)
        {
            if (descriptor.Kind == PaperBodyPluginKind.BuiltIn || descriptor.Manifest == null)
            {
                continue;
            }

            foreach (var setting in descriptor.Manifest.Settings.Where(item => item.Type == "shortcut"))
            {
                var commandId = PluginShortcutCommandId(descriptor.Id, setting.Id);
                var registration = new PluginShortcutRegistration(
                    commandId,
                    descriptor.Id,
                    setting.Id,
                    setting.ShortcutAction);
                _pluginShortcutRegistrations[commandId] = registration;

                var binding = bindingOverrides != null &&
                              bindingOverrides.TryGetValue(commandId, out var overridden)
                    ? overridden
                    : ReadPluginShortcutBinding(descriptor, setting);
                desiredBindings[commandId] = binding;

                if (string.Equals(commandId, excludedCommandId, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(binding))
                {
                    _pluginShortcutStatuses[commandId] = ShortcutUiStatus.Unassigned;
                    continue;
                }

                if (!ShortcutGesture.TryParse(binding, out var gesture) || gesture.Key == Key.None)
                {
                    _pluginShortcutStatuses[commandId] = ShortcutUiStatus.RegistrationFailed;
                    continue;
                }

                reservedCommandIds.Add(commandId);
                registrationsByCommand[commandId] = RegistrationGesturesForPlugin(gesture).ToArray();

                var canExecute = PluginShortcutActions.IsCustomAction(setting.ShortcutAction)
                    ? HasActivePluginShortcutRuntime(descriptor.Id)
                    : HasEntityPluginPaper(descriptor.Id);
                if (canExecute)
                {
                    activeCommandIds.Add(commandId);
                }
                else
                {
                    _pluginShortcutStatuses[commandId] = ShortcutUiStatus.Disabled;
                }
            }
        }

        var builtInGestures = ConfiguredBuiltInRegistrationGestures();
        foreach (var pair in registrationsByCommand)
        {
            if (pair.Value.Any(builtInGestures.Contains))
            {
                _pluginShortcutStatuses[pair.Key] = ShortcutUiStatus.Duplicate;
                activeCommandIds.Remove(pair.Key);
                reservedCommandIds.Remove(pair.Key);
            }
        }

        var commandsByGesture = new Dictionary<ShortcutGesture, HashSet<string>>();
        foreach (var pair in registrationsByCommand)
        {
            foreach (var gesture in pair.Value)
            {
                if (!commandsByGesture.TryGetValue(gesture, out var commands))
                {
                    commands = new HashSet<string>(StringComparer.Ordinal);
                    commandsByGesture.Add(gesture, commands);
                }
                commands.Add(pair.Key);
            }
        }
        foreach (var commands in commandsByGesture.Values.Where(items => items.Count > 1))
        {
            foreach (var commandId in commands)
            {
                _pluginShortcutStatuses[commandId] = ShortcutUiStatus.Duplicate;
                activeCommandIds.Remove(commandId);
                reservedCommandIds.Remove(commandId);
            }
        }

        var manager = EnsurePluginHotkeyManager();
        while (true)
        {
            if (manager.TryApply(
                    desiredBindings,
                    activeCommandIds,
                    reservedCommandIds,
                    State.DistinguishNumpadShortcutDigits,
                    out var failedCommandId,
                    out var failure))
            {
                break;
            }

            if (string.IsNullOrEmpty(failedCommandId) ||
                !activeCommandIds.Remove(failedCommandId))
            {
                foreach (var commandId in activeCommandIds)
                {
                    _pluginShortcutStatuses.TryAdd(
                        commandId,
                        failure == GlobalShortcutRegistrationFailure.Conflict
                            ? ShortcutUiStatus.Duplicate
                            : ShortcutUiStatus.RegistrationFailed);
                }
                activeCommandIds.Clear();
                _ = manager.TryApply(
                    desiredBindings,
                    activeCommandIds,
                    reservedCommandIds,
                    State.DistinguishNumpadShortcutDigits,
                    out _,
                    out _);
                break;
            }

            _pluginShortcutStatuses[failedCommandId] = failure switch
            {
                GlobalShortcutRegistrationFailure.Conflict => ShortcutUiStatus.Duplicate,
                GlobalShortcutRegistrationFailure.SystemOccupied => ShortcutUiStatus.SystemOccupied,
                _ => ShortcutUiStatus.RegistrationFailed
            };
        }

        foreach (var registration in _pluginShortcutRegistrations.Values)
        {
            if (_pluginShortcutStatuses.ContainsKey(registration.CommandId))
            {
                continue;
            }

            var binding = desiredBindings.GetValueOrDefault(registration.CommandId) ?? "";
            _pluginShortcutStatuses[registration.CommandId] = string.IsNullOrWhiteSpace(binding)
                ? ShortcutUiStatus.Unassigned
                : manager.ActiveBindings.ContainsKey(registration.CommandId)
                    ? ShortcutUiStatus.Registered
                    : ShortcutUiStatus.Disabled;
        }
    }

    private GlobalHotkeyManager EnsurePluginHotkeyManager()
    {
        if (_pluginHotkeys != null)
        {
            return _pluginHotkeys;
        }

        var manager = new GlobalHotkeyManager();
        manager.Invoked += OnPluginHotkeyInvoked;
        _pluginHotkeys = manager;
        return manager;
    }

    private void SuspendAllHotkeysForPluginRecording()
    {
        DisposeGlobalHotkeys();
        SuspendPluginShortcutRegistrations();
    }

    private void RestoreAllHotkeysAfterPluginRecording()
    {
        InitializeGlobalHotkeys();
        RefreshPluginShortcuts();
    }

    private HashSet<ShortcutGesture> ConfiguredBuiltInRegistrationGestures()
    {
        var result = new HashSet<ShortcutGesture>();
        var bindings = GlobalShortcutCatalog.NormalizeBindings(State.GlobalHotkeys);
        var enabled = GlobalShortcutCatalog.NormalizeEnabled(State.GlobalHotkeyEnabled);
        foreach (var commandId in GlobalShortcutCatalog.ExecutableIds)
        {
            if (!enabled.GetValueOrDefault(commandId) ||
                !bindings.TryGetValue(commandId, out var binding) ||
                !ShortcutGesture.TryParse(binding, out var gesture) ||
                gesture.Key == Key.None)
            {
                continue;
            }

            var definition = GlobalShortcutCatalog.Find(commandId);
            var includeDigitAlias =
                !State.DistinguishNumpadShortcutDigits &&
                definition?.IsEdgeCapsule != true &&
                gesture.IsDigitKey;
            foreach (var registrationGesture in gesture.RegistrationGestures(includeDigitAlias))
            {
                result.Add(registrationGesture);
            }
        }
        return result;
    }

    private IEnumerable<ShortcutGesture> RegistrationGesturesForPlugin(ShortcutGesture gesture)
    {
        var includeDigitAlias = !State.DistinguishNumpadShortcutDigits && gesture.IsDigitKey;
        return gesture.RegistrationGestures(includeDigitAlias);
    }

    private bool HasActivePluginShortcutRuntime(string providerId)
    {
        if (!_pluginShortcutRuntimes.TryGetValue(providerId, out var runtime))
        {
            return false;
        }
        if (runtime.IsActive())
        {
            return true;
        }

        _pluginShortcutRuntimes.Remove(providerId);
        return false;
    }

    private static string PluginShortcutCommandId(string providerId, string settingId) =>
        $"plugin::{providerId}::{settingId}";

    private string ReadPluginShortcutBinding(
        PaperBodyPluginDescriptor descriptor,
        PaperBodyPluginSettingManifest setting)
    {
        var stored = _paperBodyPlugins.DataStore.GetSettingValue(descriptor, setting);
        return stored.ValueKind == JsonValueKind.String
            ? stored.GetString() ?? ""
            : "";
    }

    private ShortcutUiStatus PluginShortcutStatusFor(string commandId) =>
        _pluginShortcutStatuses.GetValueOrDefault(
            commandId,
            ShortcutUiStatus.Unassigned);

    private FrameworkElement BuildPluginShortcutSetting(
        PaperBodyPluginDescriptor descriptor,
        PaperBodyPluginSettingManifest setting)
    {
        var commandId = PluginShortcutCommandId(descriptor.Id, setting.Id);
        var root = new StackPanel
        {
            Width = 168
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var keyButton = SettingsTextButton("");
        keyButton.MinWidth = 0;
        keyButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        keyButton.Focusable = true;
        Grid.SetColumn(keyButton, 0);
        row.Children.Add(keyButton);

        var restore = SettingsIconButton("↺", Strings.Get("ShortcutRestoreDefault"));
        restore.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(restore, 1);
        row.Children.Add(restore);
        root.Children.Add(row);

        var statusText = new TextBlock
        {
            FontSize = AppTypography.Scale(10.5),
            Margin = new Thickness(2, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        root.Children.Add(statusText);

        void UpdateVisual(ShortcutUiStatus? temporaryStatus = null)
        {
            var isRecording = string.Equals(
                _pluginShortcutRecordingCommandId,
                commandId,
                StringComparison.Ordinal);
            keyButton.Content = isRecording
                ? Strings.Get("ShortcutRecording")
                : DisplayShortcut(ReadPluginShortcutBinding(descriptor, setting));
            var status = temporaryStatus ?? PluginShortcutStatusFor(commandId);
            statusText.Text = Strings.Get(StatusResourceKey(status));
            statusText.Foreground = StatusBrush(status);
        }

        void StopRecording(bool restoreRuntime)
        {
            if (!string.Equals(
                    _pluginShortcutRecordingCommandId,
                    commandId,
                    StringComparison.Ordinal))
            {
                return;
            }

            _pluginShortcutRecordingCommandId = null;
            if (restoreRuntime)
            {
                RestoreAllHotkeysAfterPluginRecording();
            }
            UpdateVisual();
        }

        keyButton.Click += (_, _) =>
        {
            if (_pluginShortcutRecordingCommandId is { Length: > 0 } previous &&
                !string.Equals(previous, commandId, StringComparison.Ordinal))
            {
                _pluginShortcutRecordingCommandId = null;
                RestoreAllHotkeysAfterPluginRecording();
            }

            _pluginShortcutRecordingCommandId = commandId;
            SuspendAllHotkeysForPluginRecording();
            UpdateVisual();
            _ = keyButton.Dispatcher.InvokeAsync(() =>
            {
                keyButton.Focus();
                Keyboard.Focus(keyButton);
            }, DispatcherPriority.Input);
        };

        keyButton.PreviewKeyDown += (_, e) =>
        {
            if (!string.Equals(
                    _pluginShortcutRecordingCommandId,
                    commandId,
                    StringComparison.Ordinal))
            {
                return;
            }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            e.Handled = true;
            if (key == Key.Escape)
            {
                StopRecording(restoreRuntime: true);
                return;
            }
            if ((key == Key.Back || key == Key.Delete) &&
                Keyboard.Modifiers == ModifierKeys.None)
            {
                _pluginShortcutRecordingCommandId = null;
                RestoreAllHotkeysAfterPluginRecording();
                if (!TryCommitPluginShortcutBinding(
                        descriptor,
                        setting,
                        "",
                        out var clearStatus))
                {
                    UpdateVisual(clearStatus);
                    return;
                }
                UpdateVisual();
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
            _pluginShortcutRecordingCommandId = null;
            RestoreAllHotkeysAfterPluginRecording();
            if (!TryCommitPluginShortcutBinding(
                    descriptor,
                    setting,
                    gesture.ToStorageString(),
                    out var status))
            {
                UpdateVisual(status);
                return;
            }
            UpdateVisual();
        };

        keyButton.LostKeyboardFocus += (_, _) =>
        {
            StopRecording(restoreRuntime: true);
        };

        restore.Click += (_, _) =>
        {
            var wasRecording = _pluginShortcutRecordingCommandId != null;
            _pluginShortcutRecordingCommandId = null;
            if (wasRecording)
            {
                RestoreAllHotkeysAfterPluginRecording();
            }
            var defaultValue = PaperBodyPluginRegistry.DefaultSettingValue(setting);
            var binding = defaultValue.ValueKind == JsonValueKind.String
                ? defaultValue.GetString() ?? ""
                : "";
            if (!TryCommitPluginShortcutBinding(
                    descriptor,
                    setting,
                    binding,
                    out var status))
            {
                UpdateVisual(status);
                return;
            }
            UpdateVisual();
        };

        UpdateVisual();
        return root;
    }

    private bool TryCommitPluginShortcutBinding(
        PaperBodyPluginDescriptor descriptor,
        PaperBodyPluginSettingManifest setting,
        string binding,
        out ShortcutUiStatus status)
    {
        var commandId = PluginShortcutCommandId(descriptor.Id, setting.Id);
        var normalizedElement = PaperBodyPluginRegistry.NormalizeSettingValue(
            setting,
            JsonSerializer.SerializeToElement(binding));
        var normalized = normalizedElement.ValueKind == JsonValueKind.String
            ? normalizedElement.GetString() ?? ""
            : "";

        RefreshPluginShortcuts(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [commandId] = normalized
            });
        status = PluginShortcutStatusFor(commandId);
        if (status is not (
                ShortcutUiStatus.Registered or
                ShortcutUiStatus.Unassigned or
                ShortcutUiStatus.Disabled))
        {
            RefreshPluginShortcuts();
            return false;
        }

        _paperBodyPlugins.DataStore.SetSettingValue(
            descriptor,
            setting,
            JsonSerializer.SerializeToElement(normalized));
        NotifyPluginSettingsChanged(descriptor);
        RefreshPluginShortcuts();
        status = PluginShortcutStatusFor(commandId);
        return true;
    }

    private void NotifyPluginSettingsChanged(PaperBodyPluginDescriptor descriptor)
    {
        var settingsJson = _paperBodyPlugins.DataStore.GetSettingsJson(descriptor);
        RetryFailedPluginRuntimeAfterSettingsChanged(descriptor.Id);
        foreach (var window in _windows.Values.ToList())
        {
            window.NotifyPaperBodyPluginSettingsChanged(descriptor.Id, settingsJson);
        }
    }

    private void OnPluginHotkeyInvoked(string commandId)
    {
        if (IsExiting)
        {
            return;
        }
        if (TryRecordRegisteredPluginHotkey(commandId))
        {
            return;
        }
        if (!_pluginShortcutRegistrations.TryGetValue(commandId, out var registration))
        {
            return;
        }

        _ = Application.Current.Dispatcher.InvokeAsync(
            () => ExecutePluginShortcut(registration),
            DispatcherPriority.Input);
    }

    private bool TryRecordRegisteredPluginHotkey(string commandId)
    {
        if (!SupportsShortcutRecording(_settingsPage) ||
            _shortcutRecordingCommandId is not { Length: > 0 } recordingCommandId)
        {
            return false;
        }

        if (_pluginHotkeys is not { } hotkeys ||
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
        SuspendPluginShortcutRegistrations();
        try
        {
            ApplyShortcutDraft(recordingCommandId);
        }
        finally
        {
            if (!IsExiting)
            {
                RefreshPluginShortcuts();
            }
        }
        return true;
    }

    private void ExecutePluginShortcut(PluginShortcutRegistration registration)
    {
        if (IsExiting)
        {
            return;
        }

        if (!PluginShortcutActions.TryParsePaperAction(
                registration.ActionId,
                out var paperAction))
        {
            DispatchPluginShortcutAction(registration);
            return;
        }

        var paper = ResolvePluginShortcutPaper(registration.ProviderId);
        if (paper == null)
        {
            return;
        }

        switch (paperAction)
        {
            case PluginShortcutPaperAction.Show:
                TryShowPluginHostPaper(paper.Id, registration.ProviderId, activate: true);
                break;
            case PluginShortcutPaperAction.Hide:
                TryHidePluginHostPaper(paper.Id, registration.ProviderId);
                break;
            case PluginShortcutPaperAction.Toggle:
                TryTogglePluginHostPaperVisibility(
                    paper.Id,
                    registration.ProviderId,
                    activate: true);
                break;
            case PluginShortcutPaperAction.Expand:
                TryExpandPluginHostPaper(paper.Id, registration.ProviderId, activate: true);
                break;
            case PluginShortcutPaperAction.Collapse:
                TryCollapsePluginHostPaper(paper.Id, registration.ProviderId);
                break;
            case PluginShortcutPaperAction.Activate:
                TryActivatePluginHostPaper(paper.Id, registration.ProviderId);
                break;
        }
    }

    private void DispatchPluginShortcutAction(PluginShortcutRegistration registration)
    {
        if (!_pluginShortcutRuntimes.TryGetValue(registration.ProviderId, out var runtime))
        {
            return;
        }
        if (!runtime.IsActive())
        {
            RemovePluginGlobalShortcutRuntime(runtime.RuntimeId, registration.ProviderId);
            return;
        }

        try
        {
            runtime.Dispatch(new PaperShortcutActionInvocation(
                registration.SettingId,
                registration.ActionId));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "Plugin shortcut action failed. Provider={0}; Action={1}; Exception={2}",
                registration.ProviderId,
                registration.ActionId,
                ex.GetBaseException());
        }
    }

    private void EnsurePluginShortcutWindowTracking()
    {
        foreach (var pair in _windows.ToArray())
        {
            var paperId = pair.Key;
            var window = pair.Value;
            if (window.IsClosed || !_pluginShortcutTrackedWindows.Add(window))
            {
                continue;
            }

            window.Activated += (_, _) =>
            {
                if (!IsExiting && !window.IsClosed)
                {
                    RecordPluginShortcutPaperActivation(paperId);
                }
            };
            window.Closed += (_, _) =>
            {
                _pluginShortcutTrackedWindows.Remove(window);
                _pluginShortcutPaperRecency.Remove(paperId);
            };

            if (window.IsActive)
            {
                RecordPluginShortcutPaperActivation(paperId);
            }
        }
    }

    private void RecordPluginShortcutPaperActivation(string paperId)
    {
        _pluginShortcutPaperRecency[paperId] = ++_pluginShortcutPaperRecencySequence;
    }

    private PaperData? ResolvePluginShortcutPaper(string providerId)
    {
        EnsurePluginShortcutWindowTracking();
        var candidates = State.Papers
            .Where(paper =>
                paper.Type == PaperTypes.Note &&
                string.Equals(
                    paper.BodyProviderId,
                    providerId,
                    StringComparison.Ordinal))
            .ToArray();

        var active = candidates.FirstOrDefault(paper =>
            _windows.TryGetValue(paper.Id, out var window) &&
            !window.IsClosed &&
            window.IsActive);
        if (active != null)
        {
            RecordPluginShortcutPaperActivation(active.Id);
            return active;
        }

        var recent = candidates
            .Where(paper => _pluginShortcutPaperRecency.ContainsKey(paper.Id))
            .OrderByDescending(paper => _pluginShortcutPaperRecency[paper.Id])
            .FirstOrDefault();
        return recent
               ?? candidates.FirstOrDefault(paper => paper.IsVisible)
               ?? candidates.FirstOrDefault(paper =>
                   string.Equals(
                       paper.StartupOwnerPluginId,
                       providerId,
                       StringComparison.Ordinal))
               ?? candidates.FirstOrDefault();
    }

    private void DisposePluginShortcuts()
    {
        _pluginShortcutRecordingCommandId = null;
        if (_pluginHotkeys != null)
        {
            _pluginHotkeys.Invoked -= OnPluginHotkeyInvoked;
            _pluginHotkeys.Dispose();
            _pluginHotkeys = null;
        }
        _pluginShortcutRegistrations.Clear();
        _pluginShortcutStatuses.Clear();
        _pluginShortcutRuntimes.Clear();
        _pluginShortcutTrackedWindows.Clear();
        _pluginShortcutPaperRecency.Clear();
    }
}
