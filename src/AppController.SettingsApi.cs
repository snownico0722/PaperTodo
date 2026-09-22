using System.IO;
using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    private PaperSettingsService? _publicSettings;
    internal PaperSettingsService PublicSettings => _publicSettings ??= new(
        CreatePublicSettingsCatalog(), () => IsRunning,
        TryCommitExternalMutation, RunExternalPostCommitUi);

    private enum SettingEffects
    {
        None, Advanced, Telemetry, ToolTips, Animations, Theme, Typography, Markdown,
        ImageReferences, MarkdownAnimations, ExternalExtension, Compress, TodoOrder,
        TodoLinks, TodoRows, LinkedCapsules, TopBar, SystemVisibility, Fullscreen, Resize,
        CapsuleMode, Arrange, CapsuleClose, Titles, Preview, HoverIntent, EdgeTopmost,
        Opacity, Focus, Passive, Reminders, Magnet, Tether, TetherOptions, TetherVisibility,
        Scripts, VisibilitySnapshot, Shortcuts, Mcp
    }

    private PaperSettingDefinition DefineSetting<T>(string id, Func<T> get, Action<T> set,
        SettingEffects effects = SettingEffects.None, string? title = null,
        string[]? options = null, double? minimum = null, double? maximum = null,
        double? step = null, bool requiresRestart = false,
        string description = "")
    {
        var kind = typeof(T) == typeof(bool) ? "boolean" : typeof(T) == typeof(int) ? "integer" :
            typeof(T) == typeof(double) ? "number" : "string";
        var allowed = options?.ToHashSet(StringComparer.Ordinal);
        return new PaperSettingDefinition
        {
            Metadata = new PaperSettingSnapshot
            {
                Id = id, Category = id.Split('.')[0], Title = title ?? id, Type = kind,
                Value = JsonSerializer.SerializeToElement(get()), Writable = true,
                RequiresRestart = requiresRestart, Description = description,
                Min = minimum, Max = maximum, Step = step,
                MaxLength = kind == "string" ? (id == "note.external_extension" ? 32 : 128) : null,
                Options = Array.AsReadOnly(options ?? [])
            },
            Read = () => JsonSerializer.SerializeToElement(get()),
            Validate = value => ValidatePublicSetting(value, kind, allowed, minimum, maximum, step, id),
            Begin = value => BeginSettingChange(id, get, set, value.Deserialize<T>()!, effects)
        };
    }

    private static JsonElement ValidatePublicSetting(JsonElement value, string kind,
        IReadOnlySet<string>? options, double? minimum, double? maximum, double? step, string id)
    {
        static PaperSettingsException Invalid() => PaperSettingsService.Error(
            "invalid_setting_value", "Use the type, range and options returned by list_settings/get_setting.");
        switch (kind)
        {
            case "boolean":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
                break;
            case "integer":
            case "number":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) ||
                    !double.IsFinite(number) || (minimum.HasValue && number < minimum.Value) ||
                    (maximum.HasValue && number > maximum.Value) ||
                    (kind == "integer" && !value.TryGetInt32(out _))) throw Invalid();
                if (step is > 0)
                {
                    var index = (number - (minimum ?? 0)) / step.Value;
                    if (Math.Abs(index - Math.Round(index)) > 0.000001) throw Invalid();
                }
                break;
            case "string":
                if (value.ValueKind != JsonValueKind.String) throw Invalid();
                var text = value.GetString()!;
                if (text.Length > 128 || (options != null && !options.Contains(text))) throw Invalid();
                if (id == "note.external_extension")
                {
                    var extension = text.Trim();
                    if (extension.Length == 0) return JsonSerializer.SerializeToElement(ExternalMarkdownFileExtensions.Default);
                    if (extension.StartsWith("*.", StringComparison.Ordinal)) extension = extension[1..];
                    if (!extension.StartsWith('.')) extension = "." + extension;
                    if (extension.Length is < 2 or > 32 || extension.Contains("..", StringComparison.Ordinal) ||
                        extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw Invalid();
                    return JsonSerializer.SerializeToElement(ExternalMarkdownFileExtensions.Normalize(extension));
                }
                break;
            default: throw Invalid();
        }
        return value.Clone();
    }

    private PaperSettingChange BeginSettingChange<T>(string id, Func<T> get,
        Action<T> set, T value, SettingEffects effects)
    {
        var previous = get();
        if (id == "window.hide_from_taskbar" && value is false && State.HidePapersFromWindowSwitcher)
            throw PaperSettingsService.Error("setting_dependency", "Disable window.hide_from_switcher first.");
        if (id == "shortcuts.distinguish_numpad")
            return BeginPublicShortcutChange(null, null, (bool)(object)value!);

        var oldCapsule = State.UseCapsuleMode;
        var oldDeep = State.UseDeepCapsuleMode;
        var oldTaskbar = State.HidePapersFromTaskbar;
        var oldQueues = effects == SettingEffects.CapsuleMode
            ? new Dictionary<string, bool>(State.CapsuleCollapseAllActiveQueues) : null;
        var titles = id == "title.max_length"
            ? State.Papers.Select(p => (Paper: p, Title: p.Title)).ToArray() : null;
        var todoOrder = id == "todo.move_completed_to_bottom"
            ? State.Papers.Where(p => p.Type == PaperTypes.Todo)
                .Select(p => (Paper: p, Items: p.Items.ToList(), Orders: p.Items.Select(i => (Item: i, i.Order)).ToArray())).ToArray()
            : null;
        var handoffs = new List<(PaperWindow Window, PaperWindow.DeepCapsuleModeHandoff Handoff)>();
        if (effects == SettingEffects.CapsuleMode)
        {
            // Settle the existing visual owner before changing flags; never fabricate a second
            // presentation state. The same preparation/handoff is used by the interactive UI.
            foreach (var window in _windows.Values.ToArray())
            {
                window.PrepareForCapsulePresentationModeChange();
                if (id == "capsule.edge_enabled" && value is false && oldDeep &&
                    window.TryCaptureDeepCapsuleModeHandoff(out var handoff)) handoffs.Add((window, handoff));
            }
        }
        return new PaperSettingChange(
            Apply: () =>
            {
                set(value);
                if (id == "window.hide_from_switcher" && value is true) State.HidePapersFromTaskbar = true;
                if (effects == SettingEffects.CapsuleMode)
                {
                    if (id == "capsule.enabled" && value is false) State.UseDeepCapsuleMode = false;
                    if (id == "capsule.edge_enabled" && value is true) State.UseCapsuleMode = true;
                    if (id == "capsule.master_enabled" && value is true)
                        State.UseCapsuleMode = State.UseDeepCapsuleMode = true;
                    if (!State.UseDeepCapsuleMode || !State.UseCapsuleCollapseAll)
                    {
                        State.CapsuleCollapseAllActiveQueues.Clear();
                    }
                }
                if (titles != null) ClampPaperTitlesToMaxLength(State.MaxTitleLength);
                if (todoOrder != null && State.AutoMoveCompletedTodosToBottom)
                {
                    foreach (var row in todoOrder)
                    {
                        row.Paper.Items = row.Items.OrderBy(i => i.Done).ThenBy(i => i.Order).ToList();
                        TodoRules.NormalizeOrders(row.Paper.Items);
                    }
                }
            },
            Rollback: () =>
            {
                set(previous);
                State.HidePapersFromTaskbar = oldTaskbar;
                if (oldQueues != null)
                {
                    State.UseCapsuleMode = oldCapsule;
                    State.UseDeepCapsuleMode = oldDeep;
                    State.CapsuleCollapseAllActiveQueues = oldQueues;
                }
                if (titles != null) foreach (var row in titles) row.Paper.Title = row.Title;
                if (todoOrder != null) foreach (var row in todoOrder)
                {
                    row.Paper.Items = row.Items;
                    foreach (var item in row.Orders) item.Item.Order = item.Order;
                }
            },
            Publish: () =>
            {
                if (titles != null)
                    foreach (var row in titles)
                        if (!string.Equals(row.Title, row.Paper.Title, StringComparison.Ordinal))
                            NotifyPaperDisplayTitleChanged(row.Paper.Id);
                if (effects == SettingEffects.CapsuleMode)
                {
                    foreach (var window in _windows.Values.ToArray())
                    {
                        if (oldCapsule != State.UseCapsuleMode) window.UpdateCapsuleMode();
                        if (oldDeep != State.UseDeepCapsuleMode) window.UpdateDeepCapsuleMode();
                        if (id == "capsule.show_while_expanded") window.UpdateDeepCapsuleExpandedSlotMode();
                    }
                    if (!State.UseCapsuleMode)
                        foreach (var paper in State.Papers)
                            SetPaperCollapsedRuntime(paper, false, animate: false, saveGeometry: false);
                    ArrangeDeepCapsules(animate: State.EnableAnimations);
                    foreach (var (window, handoff) in handoffs)
                        if (!window.IsClosed) window.RestoreCollapsedSurfaceAfterDeepCapsuleModeDisabled(handoff);
                    RestoreMissingVisiblePaperSurfaces();
                    // Presentation reconciliation may update collapsed/geometry state, as in the UI.
                    MarkDirty();
                    RebuildTrayMenu();
                }
                else PublishSettingEffects(effects);
                // Theme already refreshes the settings chrome. Typography and mode changes
                // also affect the whole tree; all other changes update only their own region.
                if (effects is SettingEffects.Advanced or SettingEffects.Typography)
                    RefreshSettingsWindowContent();
                else if (effects != SettingEffects.Theme)
                    RefreshSettingsForChange(id);
            });
    }

    private void PublishSettingEffects(SettingEffects effects)
    {
        switch (effects)
        {
            case SettingEffects.Advanced:
                if (!State.AdvancedSettingsMode && _settingsPage == SettingsPage.Labs) _settingsPage = SettingsPage.General;
                _shortcutRecordingCommandId = null;
                ClearShortcutApplyFailure();
                break;
            case SettingEffects.Telemetry: TelemetryService.SetEnabled(State.TelemetryEnabled); break;
            case SettingEffects.Theme: RefreshThemeSurfaces(); break;
            case SettingEffects.Typography: RefreshPublicSettingsTypography(); break;
            case SettingEffects.ToolTips: RefreshToolTipSetting(); break;
            case SettingEffects.Animations:
                if (!State.EnableAnimations)
                {
                    foreach (var w in _windows.Values.ToArray()) w.SettleAnimationsForDisabledSetting();
                    ArrangeDeepCapsules(animate: false);
                }
                break;
            case SettingEffects.Markdown:
                foreach (var w in _windows.Values.ToArray()) w.UpdateMarkdownRenderMode();
                RebuildTrayMenu(); break;
            case SettingEffects.MarkdownAnimations:
                foreach (var w in _windows.Values.ToArray()) w.UpdateMarkdownEditAnimation(); break;
            case SettingEffects.ImageReferences:
                foreach (var w in _windows.Values.ToArray()) w.UpdateImageReferenceTextMode(); break;
            case SettingEffects.ExternalExtension:
                // Update the existing editor before any later focus-loss commit can read it.
                if (_settingsExternalMarkdownTextBox is { } editor)
                {
                    var caret = editor.CaretIndex;
                    editor.Text = ExternalMarkdownFileExtensions.Normalize(State.ExternalMarkdownExtension);
                    editor.CaretIndex = Math.Min(caret, editor.Text.Length);
                }
                foreach (var w in _windows.Values.ToArray()) w.UpdateExternalMarkdownExtension(); break;
            case SettingEffects.Compress: _imageStore.AutoCompressLargeImages = State.AutoCompressLargeImages; break;
            case SettingEffects.TodoRows:
            case SettingEffects.TodoOrder:
                foreach (var w in _windows.Values.ToArray()) w.RefreshTodoRowsForExternalChange(); break;
            case SettingEffects.TodoLinks:
                ClearPaperLinkDropTarget();
                foreach (var w in _windows.Values.ToArray()) w.UpdateTodoLinkFeature();
                RefreshCapsuleEligibilityForLinkedPapers(); break;
            case SettingEffects.LinkedCapsules: RefreshCapsuleEligibilityForLinkedPapers(); break;
            case SettingEffects.TopBar:
                foreach (var w in _windows.Values.ToArray()) w.UpdateTopBarNewPaperButtons(); break;
            case SettingEffects.SystemVisibility: RefreshPaperSystemVisibility(reapplyTaskbarShellState: true); break;
            case SettingEffects.Fullscreen: RefreshFullscreenAvoidanceRuntime(); break;
            case SettingEffects.Resize: RefreshApplicationThemeResources(); break;
            case SettingEffects.Arrange: ArrangeDeepCapsules(animate: State.EnableAnimations); break;
            case SettingEffects.CapsuleClose:
                foreach (var w in _windows.Values.ToArray()) w.UpdateEdgeCapsuleCloseButtonMode(); break;
            case SettingEffects.Titles:
                foreach (var w in _windows.Values.ToArray()) w.RefreshPaperTitle();
                ArrangeDeepCapsules(animate: State.EnableAnimations); RebuildTrayMenu(); break;
            case SettingEffects.Preview:
                if (!State.ExperimentalEdgeCapsuleHoverPreview) CloseEdgeCapsulePreview(animate: false, arrange: false);
                ArrangeDeepCapsules(animate: false);
                RefreshEdgeCapsuleHoverIntentRuntime(); break;
            case SettingEffects.HoverIntent: RefreshEdgeCapsuleHoverIntentRuntime(); break;
            case SettingEffects.EdgeTopmost:
                foreach (var w in _windows.Values.ToArray()) w.RefreshDeepCapsuleSlotTopmost();
                foreach (var w in _masterCapsules.Values.ToArray()) w.RefreshEffectiveTopmost(); break;
            case SettingEffects.Opacity: RefreshExperimentalOpacitySurfaces(); break;
            case SettingEffects.Focus: RefreshExperimentalFocusPresentationSurfaces(); break;
            case SettingEffects.Passive: RefreshAdvancedShortcutSurfaces(); break;
            case SettingEffects.Reminders: RefreshTodoReminderFeature(); break;
            case SettingEffects.Magnet:
                foreach (var w in _windows.Values.ToArray()) w.DisableExperimentalCapsuleMagnet();
                RefreshExperimentalWindowRuntime(); break;
            case SettingEffects.Tether:
                if (!State.ExperimentalWindowTethering)
                    foreach (var w in _windows.Values.ToArray()) w.DisableExperimentalWindowTether();
                RefreshExperimentalWindowRuntime(); RefreshExperimentalAttachmentMenus(); break;
            case SettingEffects.TetherOptions:
                foreach (var w in _windows.Values.ToArray()) w.RefreshExperimentalWindowTetherOptions(); break;
            case SettingEffects.TetherVisibility:
                foreach (var w in _windows.Values.ToArray())
                {
                    if (State.ExperimentalTetherVisibilityLink) w.RefreshExperimentalTetherVisibilityOptions();
                    else w.DisableExperimentalTetherVisibilityLink();
                }
                break;
            case SettingEffects.Scripts:
                if (State.UsePersistentPowerShellProcess)
                    PaperWindow.EnsurePersistentScriptProcessForSettings(State);
                else
                    PaperWindow.StopPersistentScriptProcesses();
                break;
            case SettingEffects.VisibilitySnapshot: ClearVisibilityShortcutRestoreSnapshot(); break;
            case SettingEffects.Mcp: RefreshMcpRuntime(); break;
        }
    }

    private void RefreshPublicSettingsTypography()
    {
        AppTypography.Configure(State.UiFontPreset, State.Zoom, State.CustomFontEnhancedBold, State.TextRenderingProfile);
        NoteTypography.Configure(State.NoteTextSize, State.NoteTextBold);
        RefreshTypography();
    }

    private IEnumerable<PaperSettingDefinition> CreateExternalSettingsCatalog()
    {
        // These existing owners persist atomically before publishing their in-memory preference.
        // They are not copied into AppState or saved again through a different store.
        var startup = DefineSetting("general.startup", SystemSettingsHelper.IsStartupEnabled,
            _ => { }, title: Strings.Get("TrayStartup"));
        startup.Begin = value => new PaperSettingChange(
            () =>
            {
                if (!SystemSettingsHelper.ToggleStartup(value.GetBoolean()))
                    throw PaperSettingsService.Error("setting_apply_failed", "Could not update the Windows startup registration.");
            }, () => { }, () => { RebuildTrayMenu(); RefreshSettingsForChange("general.startup"); }, () => true);
        yield return startup;
        foreach (var definition in new[]
        {
            BackgroundSetting("appearance.background_blend", () => PaperBackground.BlendWithTheme, PaperBackground.SetBlendWithTheme),
            BackgroundSetting("appearance.background_stretch", () => PaperBackground.StretchImage, PaperBackground.SetStretch),
            BackgroundSetting("appearance.background_layout", () => PaperBackground.Layout, PaperBackground.SetLayout,
                ["center", "bottomLeft", "bottomCenter", "bottomRight"])
        }) yield return definition;
    }

    private PaperSettingDefinition BackgroundSetting<T>(string id, Func<T> read, Action<T> write, string[]? options = null)
    {
        var title = id switch
        {
            "appearance.background_blend" => SettingsSidebarLocalized("启用和配色混合", "Blend with paper colors", "紙面カラーと混合する", "종이 색상과 혼합"),
            "appearance.background_stretch" => SettingsSidebarLocalized("拉伸", "Stretch", "ストレッチ", "늘이기"),
            _ => SettingsSidebarLocalized("位置", "Position", "位置", "위치")
        };
        var setting = DefineSetting(id, read, write, title: title, options: options);
        setting.Begin = value => new PaperSettingChange(
            () =>
            {
                try { write(value.Deserialize<T>()!); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { throw PaperSettingsService.Error("save_failed", "Could not save paper-background preferences."); }
            }, () => { }, () => { RefreshPaperBackgroundSurfaces(); RefreshSettingsForChange(id); }, () => true);
        return setting;
    }
}
