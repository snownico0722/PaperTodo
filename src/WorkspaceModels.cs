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
internal sealed class WorkspaceSettingsSnapshot
{
    private static readonly HashSet<string> WorkspaceProperties = new(StringComparer.Ordinal)
    {
        nameof(AppState.Papers), nameof(AppState.Workspaces),
        nameof(AppState.CapsuleCollapseAllActiveQueues),
        nameof(AppState.DeepCapsuleQueueStartTopMargins),
        nameof(AppState.DeepCapsuleSide), nameof(AppState.DeepCapsuleMonitorDeviceName)
    };
    private static readonly PropertyInfo[] AllProperties = typeof(AppState)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetMethod != null && p.SetMethod?.IsPublic == true).ToArray();
    private static readonly PropertyInfo[] SettingsProperties = AllProperties
        .Where(p => !WorkspaceProperties.Contains(p.Name)).ToArray();
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
