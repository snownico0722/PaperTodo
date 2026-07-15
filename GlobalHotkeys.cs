using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace PaperTodo;

internal enum GlobalShortcutGroup
{
    General,
    EdgeLeft,
    EdgeRight
}

internal sealed record GlobalShortcutDefinition(
    string Id,
    string LabelKey,
    string DefaultGesture,
    GlobalShortcutGroup Group,
    StartupCommandKind StartupCommandKind = StartupCommandKind.None,
    string PreferredCapsuleSide = "",
    int EdgeOrdinal = 0,
    bool DefaultEnabled = false)
{
    public bool IsEdgeCapsule =>
        EdgeOrdinal is >= 1 and <= 9 &&
        PreferredCapsuleSide is DeepCapsuleSides.Left or DeepCapsuleSides.Right;

    public bool IsExecutable =>
        StartupCommandKind != StartupCommandKind.None || IsEdgeCapsule;
}

internal static class GlobalShortcutCatalog
{
    public const string Show = "startup.show";
    public const string Hide = "startup.hide";
    public const string Toggle = "startup.toggle";
    public const string NewTodo = "startup.newTodo";
    public const string NewNote = "startup.newNote";
    public const string Exit = "startup.exit";

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
        foreach (var definition in Definitions.Where(item => item.Group == GlobalShortcutGroup.General))
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
                    ShortcutGesture.HasExactlyTwoModifiers(configuredGesture.Modifiers))
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
        foreach (var definition in Definitions)
        {
            normalized[definition.Id] = source.TryGetValue(definition.Id, out var enabled)
                ? enabled
                : definition.DefaultEnabled;
        }

        return normalized;
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
            new(Exit, "ShortcutExit", "", GlobalShortcutGroup.General, StartupCommandKind.Exit)
        };

        for (var ordinal = 1; ordinal <= 9; ordinal++)
        {
            definitions.Add(new GlobalShortcutDefinition(
                $"edge.left.{ordinal}",
                "ShortcutEdgeLeftSequenceFormat",
                $"Ctrl+Shift+{ordinal}",
                GlobalShortcutGroup.EdgeLeft,
                PreferredCapsuleSide: DeepCapsuleSides.Left,
                EdgeOrdinal: ordinal));
        }

        for (var ordinal = 1; ordinal <= 9; ordinal++)
        {
            definitions.Add(new GlobalShortcutDefinition(
                $"edge.right.{ordinal}",
                "ShortcutEdgeRightSequenceFormat",
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
        const ModifierKeys supported = ModifierKeys.Control |
            ModifierKeys.Alt |
            ModifierKeys.Shift |
            ModifierKeys.Windows;
        if ((modifiers & ~supported) != ModifierKeys.None)
        {
            return false;
        }

        var count = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) count++;
        if (modifiers.HasFlag(ModifierKeys.Alt)) count++;
        if (modifiers.HasFlag(ModifierKeys.Shift)) count++;
        if (modifiers.HasFlag(ModifierKeys.Windows)) count++;
        return count == 2;
    }

    public static bool IsEdgeOrdinalKey(Key key, int ordinal)
    {
        return ordinal is >= 1 and <= 9 &&
            (key == Key.D0 + ordinal || key == Key.NumPad0 + ordinal);
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
    SystemOccupied,
    RegistrationFailed
}

internal sealed class GlobalHotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private readonly HwndSource _source;
    private readonly Dictionary<int, string> _commandByNativeId = new();
    private readonly Dictionary<ShortcutGesture, int> _nativeIdByGesture = new();
    private Dictionary<string, string> _activeBindings = new(StringComparer.Ordinal);
    private int _nextNativeId = 1;

    public GlobalHotkeyManager()
    {
        var parameters = new HwndSourceParameters("PaperTodo.GlobalHotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x00000080
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WindowHook);
    }

    public event Action<string>? Invoked;

    public IReadOnlyDictionary<string, string> ActiveBindings => _activeBindings;

    public bool TryApply(
        IReadOnlyDictionary<string, string> desiredBindings,
        IReadOnlyCollection<string> activeCommandIds,
        out string? failedCommandId,
        out GlobalShortcutRegistrationFailure failure)
    {
        failedCommandId = null;
        failure = GlobalShortcutRegistrationFailure.None;
        var activeIds = activeCommandIds.ToHashSet(StringComparer.Ordinal);
        var desired = new List<(string CommandId, string Text, ShortcutGesture Gesture)>();
        var commandByGesture = new Dictionary<ShortcutGesture, string>();
        foreach (var pair in desiredBindings)
        {
            if (!activeIds.Contains(pair.Key) ||
                string.IsNullOrWhiteSpace(pair.Value) ||
                !ShortcutGesture.TryParse(pair.Value, out var gesture) ||
                gesture.Key == Key.None)
            {
                continue;
            }

            if (!commandByGesture.TryAdd(gesture, pair.Key))
            {
                failedCommandId = pair.Key;
                failure = GlobalShortcutRegistrationFailure.RegistrationFailed;
                return false;
            }

            desired.Add((pair.Key, pair.Value, gesture));
        }

        var newlyRegistered = new List<ShortcutGesture>();
        foreach (var binding in desired)
        {
            if (_nativeIdByGesture.ContainsKey(binding.Gesture))
            {
                continue;
            }

            if (!TryRegisterGesture(binding.Gesture, out var nativeId, out failure))
            {
                failedCommandId = binding.CommandId;
                foreach (var registeredGesture in newlyRegistered)
                {
                    TryUnregisterGesture(registeredGesture);
                }
                return false;
            }

            _nativeIdByGesture[binding.Gesture] = nativeId;
            _commandByNativeId[nativeId] = "";
            newlyRegistered.Add(binding.Gesture);
        }

        var activeByGesture = desired
            .ToDictionary(binding => binding.Gesture, binding => binding.CommandId);

        foreach (var pair in _nativeIdByGesture.ToArray())
        {
            if (activeByGesture.TryGetValue(pair.Key, out var commandId))
            {
                _commandByNativeId[pair.Value] = commandId;
                continue;
            }

            TryUnregisterGesture(pair.Key);
        }

        _activeBindings = desired
            .ToDictionary(binding => binding.CommandId, binding => binding.Text, StringComparer.Ordinal);
        return true;
    }

    private bool TryRegisterGesture(
        ShortcutGesture gesture,
        out int nativeId,
        out GlobalShortcutRegistrationFailure failure)
    {
        nativeId = _nextNativeId++;
        failure = GlobalShortcutRegistrationFailure.None;
        if (RegisterHotKey(
                _source.Handle,
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

    private bool TryUnregisterGesture(ShortcutGesture gesture)
    {
        if (!_nativeIdByGesture.TryGetValue(gesture, out var nativeId))
        {
            return true;
        }

        _commandByNativeId[nativeId] = "";
        if (!UnregisterHotKey(_source.Handle, nativeId))
        {
            return false;
        }

        _nativeIdByGesture.Remove(gesture);
        _commandByNativeId.Remove(nativeId);
        return true;
    }

    private void UnregisterAll()
    {
        foreach (var gesture in _nativeIdByGesture.Keys.ToArray())
        {
            TryUnregisterGesture(gesture);
        }
    }
    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _commandByNativeId.TryGetValue(wParam.ToInt32(), out var commandId))
        {
            if (!string.IsNullOrEmpty(commandId))
            {
                Invoked?.Invoke(commandId);
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

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WindowHook);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
