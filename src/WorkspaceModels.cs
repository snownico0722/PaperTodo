using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace PaperTodo;

/// <summary>Only data.json owns this catalog; numbered documents never own a second copy.</summary>
public sealed class WorkspaceConfiguration
{
    public bool Enabled { get; set; }
    public int Count { get; set; } = 2;
    public int Active { get; set; } = 1;
    public Dictionary<int, bool> FollowMainSettings { get; set; } = new();

    public bool FollowsMain(int number) => number > 1 &&
        (FollowMainSettings == null || !FollowMainSettings.TryGetValue(number, out var follow) || follow);

    internal WorkspaceConfiguration Normalized() => new()
    {
        Enabled = Enabled,
        Count = Math.Clamp(Count, 2, 5),
        Active = Enabled ? Math.Clamp(Active, 1, Math.Clamp(Count, 2, 5)) : 1,
        FollowMainSettings = Enumerable.Range(2, 4).ToDictionary(n => n, FollowsMain)
    };
}

internal sealed class WorkspacePaths
{
    internal WorkspacePaths(string directory, int number)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (number is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(number));
        Directory = Path.GetFullPath(directory);
        Number = number;
        Suffix = number == 1 ? "" : number.ToString(CultureInfo.InvariantCulture);
    }

    internal string Directory { get; }
    internal int Number { get; }
    internal string Suffix { get; }
    internal string Data => Path.Combine(Directory, "data" + Suffix + ".json");
    internal string Backup => Path.Combine(Directory, "data" + Suffix + ".backup.json");
    internal string Images => Path.Combine(Directory, "note-assets" + Suffix + ".lmdb");
    internal string PluginData => Path.Combine(Directory, "plugins", "data" + Suffix);
}

/// <summary>
/// A small settings-only snapshot. Papers, identities and desktop geometry are never inherited.
/// Snapshot values are detached JSON values, not shared mutable dictionaries from another workspace.
/// </summary>

internal enum WorkspaceStateFieldScope
{
    FollowMain,      // Software settings; a secondary slot can adopt the main value.
    LocalContent,    // Papers and their data always belong to the selected slot.
    LocalLayout,     // Queue placement and collapse memory never cross slots.
    MainCatalog,     // Only main data.json owns the workspace configuration.
    LegacyMigration  // Obsolete top-bar inputs; retain their previous projection semantics.
}

/// <summary>
/// Explicit AppState ownership contract. An unclassified future property is LOCAL by
/// default, never automatically inherited; WorkspaceFieldScopeChecks requires an
/// exact match between this registry and every public writable AppState property.
/// </summary>
internal static class WorkspaceFieldScopes
{
    internal static IReadOnlyDictionary<string, WorkspaceStateFieldScope> Declared { get; } = Build();

    internal static bool Inherits(string name) =>
        Declared.TryGetValue(name, out var scope) &&
        (scope is WorkspaceStateFieldScope.FollowMain or WorkspaceStateFieldScope.LegacyMigration);

    private static IReadOnlyDictionary<string, WorkspaceStateFieldScope> Build()
    {
        var result = new Dictionary<string, WorkspaceStateFieldScope>(StringComparer.Ordinal);
        void Add(WorkspaceStateFieldScope scope, params string[] names)
        {
            foreach (var name in names)
                if (!result.TryAdd(name, scope))
                    throw new InvalidOperationException("Duplicate workspace field ownership: " + name);
        }

        // Preserve the established settings-follow behavior, including MCP permissions,
        // global hotkeys and plugin enablement. Do not expand this list implicitly.
        Add(WorkspaceStateFieldScope.FollowMain,
            nameof(AppState.UiLanguage), nameof(AppState.DataMergeConflictPolicy), nameof(AppState.Theme), nameof(AppState.ColorScheme),
            nameof(AppState.PaperSkin), nameof(AppState.MaterialTransparency), nameof(AppState.HideSurfaceOutline),
            nameof(AppState.MatchAuxiliaryMaterialStrength), nameof(AppState.MicaBackdropType), nameof(AppState.MicaAlwaysActive),
            nameof(AppState.MarkdownRenderMode), nameof(AppState.ImageReferenceTextMode), nameof(AppState.MarkdownEditAnimationEnabled),
            nameof(AppState.TodoVisualSize), nameof(AppState.ShowTodoBottomBar), nameof(AppState.AutoClearCompletedTodos),
            nameof(AppState.AutoMoveCompletedTodosToBottom), nameof(AppState.AutoCompressLargeImages), nameof(AppState.UiFontPreset),
            nameof(AppState.TextRenderingProfile), nameof(AppState.AdvancedSettingsMode), nameof(AppState.TelemetryEnabled),
            nameof(AppState.CustomFontEnhancedBold), nameof(AppState.ExternalMarkdownExtension), nameof(AppState.Zoom),
            nameof(AppState.NoteTextSize), nameof(AppState.NoteTextBold), nameof(AppState.TodoTextBold),
            nameof(AppState.TitleTextSize), nameof(AppState.TitleTextBold), nameof(AppState.CapsuleTextSize),
            nameof(AppState.CapsuleTextBold), nameof(AppState.UseCapsuleMode), nameof(AppState.UseDeepCapsuleMode),
            nameof(AppState.DeepCapsuleGapSize), nameof(AppState.ShowTopBarNewTodoButton), nameof(AppState.ShowTopBarNewNoteButton),
            nameof(AppState.ShowTopBarExternalOpenButton), nameof(AppState.TopBarShortcutOrder), nameof(AppState.TopBarPinnedCount),
            nameof(AppState.HidePapersFromTaskbar), nameof(AppState.HidePapersFromWindowSwitcher), nameof(AppState.EnableTodoPaperLinks),
            nameof(AppState.ShowLinkedPaperName), nameof(AppState.AllowLongLinkedPaperTitles), nameof(AppState.ShowLinkedPathExtensionOnly),
            nameof(AppState.HideLinkedPapersFromCapsules), nameof(AppState.RunLinkedScriptCapsulesOnClick), nameof(AppState.MaxTitleLength),
            nameof(AppState.UseCapsuleCollapseAll), nameof(AppState.CompactMasterCapsule), nameof(AppState.ShowDeepCapsuleWhileExpanded),
            nameof(AppState.HideEdgeCapsuleCloseButtonOnHover), nameof(AppState.CollapseExpandedDeepCapsuleOnClick), nameof(AppState.EnableAnimations),
            nameof(AppState.FastAnimations), nameof(AppState.EnableToolTips), nameof(AppState.ExperimentalInactivePaperOpacity),
            nameof(AppState.ExperimentalInactivePaperOpacityLevel), nameof(AppState.ExperimentalRestingCapsuleOpacity), nameof(AppState.ExperimentalRestingCapsuleOpacityLevel),
            nameof(AppState.ExperimentalRestingCapsuleOpacityIncludesMaster), nameof(AppState.ExperimentalRestingCapsuleOpacityAlways), nameof(AppState.ExperimentalCollapsePaperOnDeactivate),
            nameof(AppState.ExperimentalHideInactiveTopBarButtons), nameof(AppState.ExperimentalHideInactiveTitleBar), nameof(AppState.ExperimentalDockedCapsulesNonTopmost),
            nameof(AppState.ExperimentalEdgeCapsuleHoverPreview), nameof(AppState.EdgeCapsulePreviewPreferDownward), nameof(AppState.ExperimentalEdgeCapsuleHoverIntent),
            nameof(AppState.ExperimentalEdgeCapsuleHoverIntentSensitivity), nameof(AppState.ExperimentalAllowLockIconUnlock), nameof(AppState.ExperimentalShortcutOpacityLevel),
            nameof(AppState.ExperimentalTodoReminders), nameof(AppState.ExperimentalTodoReminderShowButton), nameof(AppState.ExperimentalTodoReminderQuickMinutes),
            nameof(AppState.ExperimentalTodoReminderSoundEnabled), nameof(AppState.ExperimentalTodoReminderSound), nameof(AppState.McpEnabled),
            nameof(AppState.McpAllowBlankWrites), nameof(AppState.McpAllowFullWrites), nameof(AppState.McpAllowDeletes),
            nameof(AppState.ExperimentalCapsuleMagnetism), nameof(AppState.ExperimentalCapsuleMagnetScreenEdges), nameof(AppState.ExperimentalCapsuleMagnetWindowEdges),
            nameof(AppState.ExperimentalCapsuleMagnetDistance), nameof(AppState.ExperimentalWindowTethering), nameof(AppState.ExperimentalWindowTetherPreferredEdge),
            nameof(AppState.ExperimentalWindowTetherGap), nameof(AppState.ExperimentalTetherVisibilityLink), nameof(AppState.ExperimentalTetherMinimizedBehavior),
            nameof(AppState.ResizeGripMode), nameof(AppState.FullscreenTopmostMode), nameof(AppState.UsePersistentPowerShellProcess),
            nameof(AppState.PreferPowerShell7), nameof(AppState.HideScriptRunWindow), nameof(AppState.DeepCapsuleTitleMeasureCharacterLimit),
            nameof(AppState.GlobalHotkeys), nameof(AppState.GlobalHotkeyEnabled), nameof(AppState.DisabledPluginIds),
            nameof(AppState.DistinguishNumpadShortcutDigits), nameof(AppState.PreserveLinkedPaperHiddenStateInVisibilityShortcuts), nameof(AppState.OpenEdgeCapsuleShortcutAtCursor),
            nameof(AppState.RememberDeepCapsuleExpandedPosition));
        Add(WorkspaceStateFieldScope.LocalContent,
            nameof(AppState.Papers));
        Add(WorkspaceStateFieldScope.LocalLayout,
            nameof(AppState.CapsuleCollapseAllActiveQueues), nameof(AppState.DeepCapsuleQueueStartTopMargins), nameof(AppState.DeepCapsuleSide),
            nameof(AppState.DeepCapsuleMonitorDeviceName));
        Add(WorkspaceStateFieldScope.MainCatalog,
            nameof(AppState.Workspaces));
        // These inputs are normalized away after migration, but the old snapshot included
        // them. Keep that behavior rather than changing persistence during this hardening.
        Add(WorkspaceStateFieldScope.LegacyMigration,
            nameof(AppState.TodoTopBarShortcuts), nameof(AppState.NoteTopBarShortcuts));
        return result;
    }
}

internal sealed class WorkspaceSettingsSnapshot
{
    private static readonly PropertyInfo[] AllProperties = typeof(AppState)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetMethod != null && p.SetMethod?.IsPublic == true).ToArray();
    private static readonly PropertyInfo[] SettingsProperties = AllProperties
        .Where(p => WorkspaceFieldScopes.Inherits(p.Name)).ToArray();
    private readonly JsonElement[] _values;

    private WorkspaceSettingsSnapshot(AppState state) => _values = SettingsProperties
        .Select(p => JsonSerializer.SerializeToElement(p.GetValue(state), p.PropertyType, StateStore.JsonOptions))
        .ToArray();

    internal static WorkspaceSettingsSnapshot Capture(AppState state) => new(state);

    internal void ApplyTo(AppState state)
    {
        for (var i = 0; i < SettingsProperties.Length; i++)
            SettingsProperties[i].SetValue(state,
                _values[i].Deserialize(SettingsProperties[i].PropertyType, StateStore.JsonOptions));
    }

    internal AppState ProjectForSave(AppState effective)
    {
        // Shallow-copy the root only. Serialization still visits Papers once, on the UI thread;
        // no full document round-trip or historical snapshot is added to ordinary saving.
        var document = new AppState();
        foreach (var property in AllProperties) property.SetValue(document, property.GetValue(effective));
        ApplyTo(document);
        document.Workspaces = null;
        return document;
    }
}

internal sealed class WorkspaceSession
{
    internal WorkspaceSession(WorkspacePaths paths, StateStore store, AppState state,
        bool followsMain, WorkspaceSettingsSnapshot? independentSettings)
    {
        Paths = paths;
        Store = store;
        State = state;
        FollowsMain = followsMain;
        IndependentSettings = independentSettings;
        InheritedSettings = store.InheritedSettings;
    }

    internal WorkspacePaths Paths { get; }
    internal StateStore Store { get; }
    internal AppState State { get; }
    internal bool FollowsMain { get; }
    internal WorkspaceSettingsSnapshot? IndependentSettings { get; set; }
    internal WorkspaceSettingsSnapshot? InheritedSettings { get; }

    internal void BindPersistence()
    {
        Store.InheritedSettings = InheritedSettings;
        // Cached writers must retain only a small settings snapshot, not this session's
        // entire inactive AppState. AcceptReload rebinds when independent settings change.
        Store.PersistenceProjection = FollowsMain ? IndependentSettings!.ProjectForSave : null;
    }
}
