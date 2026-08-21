using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace PaperTodo;

internal enum GlobalShortcutGroup
{
    General,
    Labs,
    EdgeLeft,
    EdgeRight
}

internal enum ExperimentalShortcutKind
{
    None,
    CurrentPaperPassive,
    AllSurfacesPassive,
    LockAllPapers,
    AllPapersTransparent,
    AllCapsulesTransparent,
    CurrentPaperTransparent
}

internal sealed record GlobalShortcutDefinition(
    string Id,
    string LabelKey,
    string DefaultGesture,
    GlobalShortcutGroup Group,
    StartupCommandKind StartupCommandKind = StartupCommandKind.None,
    string PreferredCapsuleSide = "",
    int EdgeOrdinal = 0,
    bool DefaultEnabled = false,
    ExperimentalShortcutKind ExperimentalKind = ExperimentalShortcutKind.None)
{
    public bool IsEdgeCapsule =>
        EdgeOrdinal is >= 1 and <= 9 &&
        PreferredCapsuleSide is DeepCapsuleSides.Left or DeepCapsuleSides.Right;

    public bool IsExecutable =>
        StartupCommandKind != StartupCommandKind.None ||
        IsEdgeCapsule ||
        ExperimentalKind != ExperimentalShortcutKind.None;
}

internal static class GlobalShortcutCatalog
{
    public const string Show = "startup.show";
    public const string Hide = "startup.hide";
    public const string Toggle = "startup.toggle";
    public const string NewTodo = "startup.newTodo";
    public const string NewNote = "startup.newNote";
    public const string Exit = "startup.exit";
    public const string CurrentPaperPassive = "labs.passiveCurrent";
    public const string AllSurfacesPassive = "labs.passiveAll";
    public const string LockAllPapers = "labs.lockAllPapers";
    public const string AllPapersTransparent = "labs.transparentAllPapers";
    public const string AllCapsulesTransparent = "labs.transparentAllCapsules";
    public const string CurrentPaperTransparent = "labs.transparentCurrentPaper";

    public static IReadOnlyList<GlobalShortcutDefinition> Definitions { get; } = BuildDefinitions();

    private static readonly Dictionary<string, GlobalShortcutDefinition> ById =
        Definitions.ToDictionary(definition => definition.Id, StringComparer.Ordinal);

    public static GlobalShortcutDefinition? Find(string id)
    {
        return ById.GetValueOrDefault(id);
    }

    public static Dictionary<string, string> NormalizeBindings(Dictionary<string, string>? source)
    {
        source ??= new Dictionary<string, string>();
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in Definitions.Where(item => !item.IsEdgeCapsule))
        {
            if (!source.TryGetValue(definition.Id, out var configured))
            {
                configured = definition.DefaultGesture;
            }

            normalized[definition.Id] = ShortcutGesture.TryParse(configured, out var gesture)
                ? gesture.ToStorageString()
                : "";
        }

        foreach (var group in new[] { GlobalShortcutGroup.EdgeLeft, GlobalShortcutGroup.EdgeRight })
        {
            var groupDefinitions = Definitions.Where(item => item.Group == group).ToArray();
            ShortcutGesture.TryParse(groupDefinitions[0].DefaultGesture, out var defaultGesture);
            var modifiers = defaultGesture.Modifiers;
            foreach (var definition in groupDefinitions)
            {
                if (source.TryGetValue(definition.Id, out var configured) &&
                    ShortcutGesture.TryParse(configured, out var configuredGesture) &&
                    ShortcutGesture.HasEdgePrefixModifiers(configuredGesture.Modifiers))
                {
                    modifiers = configuredGesture.Modifiers;
                    break;
                }
            }

            foreach (var definition in groupDefinitions)
            {
                normalized[definition.Id] = ShortcutGesture.ForEdgeOrdinal(
                    modifiers,
                    definition.EdgeOrdinal).ToStorageString();
            }
        }

        return normalized;
    }

    public static Dictionary<string, bool> NormalizeEnabled(Dictionary<string, bool>? source)
    {
        source ??= new Dictionary<string, bool>();
        var normalized = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var definition in Definitions.Where(item => !item.IsEdgeCapsule))
        {
            normalized[definition.Id] = source.TryGetValue(definition.Id, out var enabled)
                ? enabled
                : definition.DefaultEnabled;
        }

        foreach (var group in new[] { GlobalShortcutGroup.EdgeLeft, GlobalShortcutGroup.EdgeRight })
        {
            var groupDefinitions = DefinitionsInGroup(group);
            var groupEnabled = false;
            var hasConfigured = false;
            foreach (var definition in groupDefinitions)
            {
                if (!source.TryGetValue(definition.Id, out var enabled))
                {
                    continue;
                }

                hasConfigured = true;
                groupEnabled |= enabled;
            }

            if (!hasConfigured)
            {
                groupEnabled = groupDefinitions[0].DefaultEnabled;
            }

            foreach (var definition in groupDefinitions)
            {
                normalized[definition.Id] = groupEnabled;
            }
        }

        return normalized;
    }

    public static IReadOnlyList<GlobalShortcutDefinition> DefinitionsInGroup(GlobalShortcutGroup group)
    {
        return Definitions.Where(item => item.Group == group).ToArray();
    }

    public static GlobalShortcutDefinition EdgeSequenceUiDefinition(GlobalShortcutGroup group)
    {
        if (group is not (GlobalShortcutGroup.EdgeLeft or GlobalShortcutGroup.EdgeRight))
        {
            throw new ArgumentOutOfRangeException(nameof(group));
        }

        return DefinitionsInGroup(group)[0];
    }

    public static GlobalShortcutGroup OppositeEdgeGroup(GlobalShortcutGroup group)
    {
        return group switch
        {
            GlobalShortcutGroup.EdgeLeft => GlobalShortcutGroup.EdgeRight,
            GlobalShortcutGroup.EdgeRight => GlobalShortcutGroup.EdgeLeft,
            _ => throw new ArgumentOutOfRangeException(nameof(group))
        };
    }

    public static bool TryGetEdgePrefixModifiers(
        IReadOnlyDictionary<string, string> bindings,
        GlobalShortcutGroup group,
        out ModifierKeys modifiers)
    {
        modifiers = ModifierKeys.None;
        foreach (var definition in DefinitionsInGroup(group))
        {
            if (bindings.TryGetValue(definition.Id, out var configured) &&
                ShortcutGesture.TryParse(configured, out var gesture) &&
                ShortcutGesture.HasEdgePrefixModifiers(gesture.Modifiers))
            {
                modifiers = gesture.Modifiers;
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyCollection<string> ExecutableIds { get; } =
        Definitions.Where(definition => definition.IsExecutable)
            .Select(definition => definition.Id)
            .ToArray();

    private static IReadOnlyList<GlobalShortcutDefinition> BuildDefinitions()
    {
        var definitions = new List<GlobalShortcutDefinition>
        {
            new(Show, "ShortcutShowAll", "", GlobalShortcutGroup.General, StartupCommandKind.Show),
            new(Hide, "ShortcutHideAll", "", GlobalShortcutGroup.General, StartupCommandKind.Hide),
            new(Toggle, "ShortcutToggleVisibility", "", GlobalShortcutGroup.General, StartupCommandKind.Toggle),
            new(NewTodo, "ShortcutNewTodo", "", GlobalShortcutGroup.General, StartupCommandKind.NewTodo),
            new(NewNote, "ShortcutNewNote", "", GlobalShortcutGroup.General, StartupCommandKind.NewNote),
            new(Exit, "ShortcutExit", "", GlobalShortcutGroup.General, StartupCommandKind.Exit),
            new(
                CurrentPaperPassive,
                "LabsCurrentPaperPassive",
                "Ctrl+Alt+Shift+P",
                GlobalShortcutGroup.Labs,
                ExperimentalKind: ExperimentalShortcutKind.CurrentPaperPassive),
            new(
                AllSurfacesPassive,
                "LabsAllSurfacesPassive",
                "Ctrl+Alt+Shift+A",
                GlobalShortcutGroup.Labs,
                ExperimentalKind: ExperimentalShortcutKind.AllSurfacesPassive),
            new(
                LockAllPapers,
                "LabsLockAllPapers",
                "Ctrl+Alt+Shift+L",
                GlobalShortcutGroup.Labs,
                ExperimentalKind: ExperimentalShortcutKind.LockAllPapers),
            new(
                AllPapersTransparent,
                "LabsAllPapersTransparent",
                "Ctrl+Alt+Shift+O",
                GlobalShortcutGroup.Labs,
                ExperimentalKind: ExperimentalShortcutKind.AllPapersTransparent),
            new(
                AllCapsulesTransparent,
                "LabsAllCapsulesTransparent",
                "Ctrl+Alt+Shift+C",
                GlobalShortcutGroup.Labs,
                ExperimentalKind: ExperimentalShortcutKind.AllCapsulesTransparent),
            new(
                CurrentPaperTransparent,
                "LabsCurrentPaperTransparent",
                "Ctrl+Alt+Shift+T",
                GlobalShortcutGroup.Labs,
                ExperimentalKind: ExperimentalShortcutKind.CurrentPaperTransparent)
        };

        for (var ordinal = 1; ordinal <= 9; ordinal++)
        {
            definitions.Add(new GlobalShortcutDefinition(
                $"edge.left.{ordinal}",
                "ShortcutEdgeLeftSequence",
                $"Ctrl+Shift+{ordinal}",
                GlobalShortcutGroup.EdgeLeft,
                PreferredCapsuleSide: DeepCapsuleSides.Left,
                EdgeOrdinal: ordinal));
        }

        for (var ordinal = 1; ordinal <= 9; ordinal++)
        {
            definitions.Add(new GlobalShortcutDefinition(
                $"edge.right.{ordinal}",
                "ShortcutEdgeRightSequence",
                $"Ctrl+Alt+{ordinal}",
                GlobalShortcutGroup.EdgeRight,
                PreferredCapsuleSide: DeepCapsuleSides.Right,
                EdgeOrdinal: ordinal));
        }

        return definitions;
    }
}

internal readonly record struct ShortcutGesture(Key Key, ModifierKeys Modifiers)
{
    public static ShortcutGesture ForEdgeOrdinal(ModifierKeys modifiers, int ordinal)
    {
        if (ordinal is < 1 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        return new ShortcutGesture(Key.D0 + ordinal, modifiers);
    }

    public static bool HasExactlyTwoModifiers(ModifierKeys modifiers)
    {
        return CountSupportedModifiers(modifiers) == 2;
    }

    public static bool HasEdgePrefixModifiers(ModifierKeys modifiers)
    {
        var count = CountSupportedModifiers(modifiers);
        return count is >= 2 and <= 3;
    }

    public static int CountSupportedModifiers(ModifierKeys modifiers)
    {
        const ModifierKeys supported = ModifierKeys.Control |
            ModifierKeys.Alt |
            ModifierKeys.Shift |
            ModifierKeys.Windows;
        if ((modifiers & ~supported) != ModifierKeys.None)
        {
            return 0;
        }

        var count = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) count++;
        if (modifiers.HasFlag(ModifierKeys.Alt)) count++;
        if (modifiers.HasFlag(ModifierKeys.Shift)) count++;
        if (modifiers.HasFlag(ModifierKeys.Windows)) count++;
        return count;
    }

    public static bool IsEdgeOrdinalKey(Key key, int ordinal)
    {
        return ordinal is >= 1 and <= 9 &&
            (key == Key.D0 + ordinal || key == Key.NumPad0 + ordinal);
    }

    public static bool IsAnyEdgeOrdinalKey(Key key)
    {
        return key is (>= Key.D1 and <= Key.D9) or (>= Key.NumPad1 and <= Key.NumPad9);
    }

    public bool IsDigitKey =>
        Key is (>= Key.D0 and <= Key.D9) or (>= Key.NumPad0 and <= Key.NumPad9);

    public ShortcutGesture NormalizeNumpadDigit()
    {
        if (Key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            var ordinal = (int)Key - (int)Key.NumPad0;
            return new ShortcutGesture((Key)((int)Key.D0 + ordinal), Modifiers);
        }
        return this;
    }

    public IEnumerable<ShortcutGesture> RegistrationGestures(bool includeDigitAlias)
    {
        yield return this;
        if (!includeDigitAlias)
        {
            yield break;
        }

        if (Key is >= Key.D0 and <= Key.D9)
        {
            var ordinal = (int)Key - (int)Key.D0;
            yield return new ShortcutGesture((Key)((int)Key.NumPad0 + ordinal), Modifiers);
        }
        else if (Key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            var ordinal = (int)Key - (int)Key.NumPad0;
            yield return new ShortcutGesture((Key)((int)Key.D0 + ordinal), Modifiers);
        }
    }

    public string ToEdgePrefixDisplayString()
    {
        if (!HasEdgePrefixModifiers(Modifiers))
        {
            return "";
        }

        return string.Join('+', ModifierParts());
    }

    public string ToEdgeSequenceDisplayString()
    {
        var prefix = ToEdgePrefixDisplayString();
        return string.IsNullOrEmpty(prefix) ? "" : $"{prefix}+1–9";
    }

    public static bool TryParse(string? text, out ShortcutGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var modifiers = ModifierKeys.None;
        Key key = Key.None;
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModifierKeys.Control;
                    continue;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    continue;
                case "shift":
                    modifiers |= ModifierKeys.Shift;
                    continue;
                case "win":
                case "windows":
                    modifiers |= ModifierKeys.Windows;
                    continue;
            }

            if (key != Key.None || !TryParseKey(part, out key))
            {
                return false;
            }
        }

        if (modifiers == ModifierKeys.None || IsModifierKey(key) || key == Key.None)
        {
            return false;
        }

        gesture = new ShortcutGesture(key, modifiers);
        return true;
    }

    public string ToStorageString()
    {
        if (Key == Key.None)
        {
            return "";
        }

        var parts = ModifierParts();
        parts.Add(StorageKeyName(Key));
        return string.Join('+', parts);
    }

    public string ToDisplayString()
    {
        if (Key == Key.None)
        {
            return "";
        }

        var parts = ModifierParts();
        parts.Add(DisplayKeyName(Key));
        return string.Join('+', parts);
    }

    private List<string> ModifierParts()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return parts;
    }

    private static bool TryParseKey(string text, out Key key)
    {
        if (text.Length == 1 && text[0] is >= '0' and <= '9')
        {
            key = Key.D0 + (text[0] - '0');
            return true;
        }

        if (text.Length == 1 && text[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
        {
            key = Key.A + (char.ToUpperInvariant(text[0]) - 'A');
            return true;
        }

        return Enum.TryParse(text, ignoreCase: true, out key);
    }

    private static string StorageKeyName(Key key)
    {
        return key is >= Key.D0 and <= Key.D9
            ? ((int)(key - Key.D0)).ToString()
            : key.ToString();
    }

    private static string DisplayKeyName(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)(key - Key.D0)).ToString();
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return $"Num {(int)(key - Key.NumPad0)}";
        }

        return key switch
        {
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            _ => key.ToString()
        };
    }

    public static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
    }
}

internal enum GlobalShortcutRegistrationFailure
{
    None,
    Conflict,
    SystemOccupied,
    RegistrationFailed,
    UnregistrationFailed
}

internal sealed class GlobalHotkeyManager : IDisposable
{
    private readonly Guid _ownerId = Guid.NewGuid();
    private bool _disposed;

    public GlobalHotkeyManager()
    {
        GlobalHotkeyBroker.AddOwner(_ownerId, commandId =>
        {
            if (!_disposed)
            {
                Invoked?.Invoke(commandId);
            }
        });
    }

    public event Action<string>? Invoked;

    public IReadOnlyDictionary<string, string> ActiveBindings =>
        GlobalHotkeyBroker.ActiveBindings(_ownerId);

    public bool TryApply(
        IReadOnlyDictionary<string, string> desiredBindings,
        IReadOnlyCollection<string> activeCommandIds,
        bool distinguishNumpadDigits,
        out string? failedCommandId,
        out GlobalShortcutRegistrationFailure failure) =>
        TryApply(
            desiredBindings,
            activeCommandIds,
            activeCommandIds,
            distinguishNumpadDigits,
            out failedCommandId,
            out failure);

    public bool TryApply(
        IReadOnlyDictionary<string, string> desiredBindings,
        IReadOnlyCollection<string> activeCommandIds,
        IReadOnlyCollection<string> reservedCommandIds,
        bool distinguishNumpadDigits,
        out string? failedCommandId,
        out GlobalShortcutRegistrationFailure failure)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GlobalHotkeyBroker.TryApply(
            _ownerId,
            desiredBindings,
            activeCommandIds,
            reservedCommandIds,
            distinguishNumpadDigits,
            out failedCommandId,
            out failure);
    }

    public void Suspend()
    {
        if (_disposed)
        {
            return;
        }
        GlobalHotkeyBroker.SuspendOwner(_ownerId);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        GlobalHotkeyBroker.RemoveOwner(_ownerId);
    }
}

internal static class GlobalHotkeyBroker
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private sealed class OwnerState
    {
        public required Action<string> Dispatch { get; init; }
        public Dictionary<string, string> Bindings { get; set; } = new(StringComparer.Ordinal);
        public HashSet<string> ActiveCommandIds { get; set; } = new(StringComparer.Ordinal);
        public HashSet<string> ReservedCommandIds { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed record NativeBinding(Guid OwnerId, string CommandId, int NativeId);

    private static readonly HwndSource Source;
    private static readonly Dictionary<Guid, OwnerState> Owners = new();
    private static readonly Dictionary<ShortcutGesture, NativeBinding> NativeByGesture = new();
    private static readonly Dictionary<int, NativeBinding> NativeById = new();
    private static int _nextNativeId = 1;
    private static bool _distinguishNumpadDigits;

    static GlobalHotkeyBroker()
    {
        var parameters = new HwndSourceParameters("PaperTodo.GlobalHotkeyBroker")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x00000080
        };
        Source = new HwndSource(parameters);
        Source.AddHook(WindowHook);
    }

    public static void AddOwner(Guid ownerId, Action<string> dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (!Owners.TryAdd(ownerId, new OwnerState { Dispatch = dispatch }))
        {
            throw new InvalidOperationException("A global hotkey owner with the same id already exists.");
        }
    }

    public static IReadOnlyDictionary<string, string> ActiveBindings(Guid ownerId)
    {
        if (!Owners.TryGetValue(ownerId, out var owner))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var active = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var commandId in owner.ActiveCommandIds)
        {
            if (owner.Bindings.TryGetValue(commandId, out var binding) &&
                NativeByGesture.Values.Any(item =>
                    item.OwnerId == ownerId &&
                    string.Equals(item.CommandId, commandId, StringComparison.Ordinal)))
            {
                active[commandId] = binding;
            }
        }
        return active;
    }

    public static bool TryApply(
        Guid ownerId,
        IReadOnlyDictionary<string, string> desiredBindings,
        IReadOnlyCollection<string> activeCommandIds,
        IReadOnlyCollection<string> reservedCommandIds,
        bool distinguishNumpadDigits,
        out string? failedCommandId,
        out GlobalShortcutRegistrationFailure failure)
    {
        failedCommandId = null;
        failure = GlobalShortcutRegistrationFailure.None;
        if (!Owners.ContainsKey(ownerId))
        {
            failure = GlobalShortcutRegistrationFailure.RegistrationFailed;
            return false;
        }

        var candidateBindings = new Dictionary<string, string>(desiredBindings, StringComparer.Ordinal);
        var candidateActive = activeCommandIds.ToHashSet(StringComparer.Ordinal);
        var candidateReserved = reservedCommandIds.ToHashSet(StringComparer.Ordinal);
        candidateReserved.UnionWith(candidateActive);

        if (!TryBuildCombinedPlan(
                ownerId,
                candidateBindings,
                candidateActive,
                candidateReserved,
                distinguishNumpadDigits,
                out var desiredNative,
                out failedCommandId))
        {
            failure = GlobalShortcutRegistrationFailure.Conflict;
            return false;
        }

        var newlyRegistered = new List<ShortcutGesture>();
        foreach (var pair in desiredNative)
        {
            if (NativeByGesture.ContainsKey(pair.Key))
            {
                continue;
            }

            if (!TryRegisterGesture(pair.Key, out var nativeId, out failure))
            {
                failedCommandId = pair.Value.CommandId;
                RollbackNewRegistrations(newlyRegistered);
                return false;
            }

            var native = new NativeBinding(pair.Value.OwnerId, pair.Value.CommandId, nativeId);
            NativeByGesture[pair.Key] = native;
            NativeById[nativeId] = native;
            newlyRegistered.Add(pair.Key);
        }

        var removedRegistrations = new List<(ShortcutGesture Gesture, NativeBinding Binding)>();
        foreach (var pair in NativeByGesture.ToArray())
        {
            if (desiredNative.ContainsKey(pair.Key))
            {
                continue;
            }

            if (!TryUnregisterGesture(pair.Key))
            {
                failure = GlobalShortcutRegistrationFailure.UnregistrationFailed;
                failedCommandId = pair.Value.OwnerId == ownerId
                    ? pair.Value.CommandId
                    : candidateActive.FirstOrDefault() ?? candidateReserved.FirstOrDefault();
                RollbackNewRegistrations(newlyRegistered);
                RestoreRemovedRegistrations(removedRegistrations);
                return false;
            }

            removedRegistrations.Add((pair.Key, pair.Value));
        }

        foreach (var pair in desiredNative)
        {
            if (!NativeByGesture.TryGetValue(pair.Key, out var current))
            {
                continue;
            }
            if (current.OwnerId == pair.Value.OwnerId &&
                string.Equals(current.CommandId, pair.Value.CommandId, StringComparison.Ordinal))
            {
                continue;
            }

            var updated = new NativeBinding(
                pair.Value.OwnerId,
                pair.Value.CommandId,
                current.NativeId);
            NativeByGesture[pair.Key] = updated;
            NativeById[current.NativeId] = updated;
        }

        var owner = Owners[ownerId];
        owner.Bindings = candidateBindings;
        owner.ActiveCommandIds = candidateActive;
        owner.ReservedCommandIds = candidateReserved;
        _distinguishNumpadDigits = distinguishNumpadDigits;
        return true;
    }

    public static void SuspendOwner(Guid ownerId)
    {
        if (!Owners.TryGetValue(ownerId, out var owner))
        {
            return;
        }

        _ = TryApply(
            ownerId,
            owner.Bindings,
            Array.Empty<string>(),
            owner.ReservedCommandIds,
            _distinguishNumpadDigits,
            out _,
            out _);
    }

    public static void RemoveOwner(Guid ownerId)
    {
        if (!Owners.TryGetValue(ownerId, out var owner))
        {
            return;
        }

        if (!TryApply(
                ownerId,
                owner.Bindings,
                Array.Empty<string>(),
                Array.Empty<string>(),
                _distinguishNumpadDigits,
                out _,
                out _))
        {
            // Keep the broker owner record if the OS refused to release one of its native hotkeys.
            // The manager has already marked itself disposed, so the retained dispatch is inert and
            // cannot execute a stale command. A later process teardown still releases the HWND.
            return;
        }
        Owners.Remove(ownerId);
    }

    private static void RollbackNewRegistrations(IEnumerable<ShortcutGesture> gestures)
    {
        foreach (var gesture in gestures.Reverse())
        {
            _ = TryUnregisterGesture(gesture);
        }
    }

    private static void RestoreRemovedRegistrations(
        IEnumerable<(ShortcutGesture Gesture, NativeBinding Binding)> registrations)
    {
        foreach (var (gesture, binding) in registrations.Reverse())
        {
            _ = TryRestoreGesture(gesture, binding);
        }
    }

    private static bool TryRestoreGesture(ShortcutGesture gesture, NativeBinding binding)
    {
        if (!RegisterHotKey(
                Source.Handle,
                binding.NativeId,
                NativeModifiers(gesture.Modifiers) | ModNoRepeat,
                (uint)KeyInterop.VirtualKeyFromKey(gesture.Key)))
        {
            return false;
        }

        NativeByGesture[gesture] = binding;
        NativeById[binding.NativeId] = binding;
        return true;
    }

    private static bool TryBuildCombinedPlan(
        Guid candidateOwnerId,
        IReadOnlyDictionary<string, string> candidateBindings,
        IReadOnlySet<string> candidateActive,
        IReadOnlySet<string> candidateReserved,
        bool distinguishNumpadDigits,
        out Dictionary<ShortcutGesture, (Guid OwnerId, string CommandId)> desiredNative,
        out string? failedCommandId)
    {
        desiredNative = new Dictionary<ShortcutGesture, (Guid OwnerId, string CommandId)>();
        failedCommandId = null;
        var reservations = new Dictionary<ShortcutGesture, (Guid OwnerId, string CommandId)>();

        foreach (var ownerPair in Owners)
        {
            var ownerId = ownerPair.Key;
            var owner = ownerPair.Value;
            var bindings = ownerId == candidateOwnerId ? candidateBindings : owner.Bindings;
            var reserved = ownerId == candidateOwnerId ? candidateReserved : owner.ReservedCommandIds;

            foreach (var commandId in reserved)
            {
                if (!bindings.TryGetValue(commandId, out var text) ||
                    string.IsNullOrWhiteSpace(text) ||
                    !ShortcutGesture.TryParse(text, out var gesture) ||
                    gesture.Key == Key.None)
                {
                    continue;
                }

                foreach (var registrationGesture in RegistrationGesturesFor(
                             commandId,
                             gesture,
                             distinguishNumpadDigits))
                {
                    if (reservations.TryGetValue(registrationGesture, out var existing))
                    {
                        if (ownerId == candidateOwnerId)
                        {
                            failedCommandId = commandId;
                        }
                        else if (existing.OwnerId == candidateOwnerId)
                        {
                            failedCommandId = existing.CommandId;
                        }
                        return false;
                    }
                    reservations[registrationGesture] = (ownerId, commandId);
                }
            }
        }

        foreach (var ownerPair in Owners)
        {
            var ownerId = ownerPair.Key;
            var owner = ownerPair.Value;
            var bindings = ownerId == candidateOwnerId ? candidateBindings : owner.Bindings;
            var active = ownerId == candidateOwnerId ? candidateActive : owner.ActiveCommandIds;
            foreach (var commandId in active)
            {
                if (!bindings.TryGetValue(commandId, out var text) ||
                    string.IsNullOrWhiteSpace(text) ||
                    !ShortcutGesture.TryParse(text, out var gesture) ||
                    gesture.Key == Key.None)
                {
                    continue;
                }

                foreach (var registrationGesture in RegistrationGesturesFor(
                             commandId,
                             gesture,
                             distinguishNumpadDigits))
                {
                    desiredNative[registrationGesture] = (ownerId, commandId);
                }
            }
        }

        return true;
    }

    private static IEnumerable<ShortcutGesture> RegistrationGesturesFor(
        string commandId,
        ShortcutGesture gesture,
        bool distinguishNumpadDigits)
    {
        var definition = GlobalShortcutCatalog.Find(commandId);
        var includeDigitAlias =
            !distinguishNumpadDigits &&
            definition?.IsEdgeCapsule != true &&
            gesture.IsDigitKey;
        return gesture.RegistrationGestures(includeDigitAlias);
    }

    private static bool TryRegisterGesture(
        ShortcutGesture gesture,
        out int nativeId,
        out GlobalShortcutRegistrationFailure failure)
    {
        nativeId = _nextNativeId++;
        failure = GlobalShortcutRegistrationFailure.None;
        if (RegisterHotKey(
                Source.Handle,
                nativeId,
                NativeModifiers(gesture.Modifiers) | ModNoRepeat,
                (uint)KeyInterop.VirtualKeyFromKey(gesture.Key)))
        {
            return true;
        }

        failure = Marshal.GetLastWin32Error() == ErrorHotkeyAlreadyRegistered
            ? GlobalShortcutRegistrationFailure.SystemOccupied
            : GlobalShortcutRegistrationFailure.RegistrationFailed;
        return false;
    }

    private static bool TryUnregisterGesture(ShortcutGesture gesture)
    {
        if (!NativeByGesture.TryGetValue(gesture, out var native))
        {
            return true;
        }

        if (!UnregisterHotKey(Source.Handle, native.NativeId))
        {
            return false;
        }

        NativeByGesture.Remove(gesture);
        NativeById.Remove(native.NativeId);
        return true;
    }

    private static IntPtr WindowHook(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg == WmHotkey && NativeById.TryGetValue(wParam.ToInt32(), out var native))
        {
            if (Owners.TryGetValue(native.OwnerId, out var owner))
            {
                owner.Dispatch(native.CommandId);
            }
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static uint NativeModifiers(ModifierKeys modifiers)
    {
        var result = 0u;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= ModControl;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= ModShift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= ModWin;
        return result;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
