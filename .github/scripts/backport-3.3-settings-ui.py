from pathlib import Path
import re
import subprocess

ROOT = Path('.')

def read(path):
    return (ROOT / path).read_text(encoding='utf-8')

def write(path, text):
    (ROOT / path).write_text(text, encoding='utf-8', newline='')

def replace_once(path, old, new):
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{path}: expected one match, got {count}: {old[:80]!r}')
    write(path, text.replace(old, new, 1))

def replace_all_checked(path, old, new, expected):
    text = read(path)
    count = text.count(old)
    if count != expected:
        raise RuntimeError(f'{path}: expected {expected} matches, got {count}: {old[:80]!r}')
    write(path, text.replace(old, new))

def insert_after(path, anchor, addition):
    replace_once(path, anchor, anchor + addition)

def insert_before(path, anchor, addition):
    replace_once(path, anchor, addition + anchor)

def extract_braced_member(text, marker):
    idx = text.index(marker)
    start = text.rfind('\n', 0, idx) + 1
    brace = text.index('{', idx)
    depth = 0
    in_string = False
    verbatim = False
    escape = False
    i = brace
    while i < len(text):
        ch = text[i]
        if in_string:
            if verbatim:
                if ch == '"':
                    if i + 1 < len(text) and text[i + 1] == '"':
                        i += 2
                        continue
                    in_string = False
                    verbatim = False
            else:
                if escape:
                    escape = False
                elif ch == '\\':
                    escape = True
                elif ch == '"':
                    in_string = False
        else:
            if ch == '"':
                in_string = True
                verbatim = i > 0 and text[i - 1] == '@'
            elif ch == '{':
                depth += 1
            elif ch == '}':
                depth -= 1
                if depth == 0:
                    end = i + 1
                    if end < len(text) and text[end] == '\r': end += 1
                    if end < len(text) and text[end] == '\n': end += 1
                    return start, end, text[start:end]
        i += 1
    raise RuntimeError(f'unclosed member {marker}')

def replace_member(path, marker, replacement):
    text = read(path)
    start, end, _ = extract_braced_member(text, marker)
    write(path, text[:start] + replacement.rstrip() + '\n\n' + text[end:])

def git_show(spec):
    return subprocess.check_output(['git', 'show', spec], text=True, encoding='utf-8')

# ---------- startup language ----------
write('StateJsonReadPolicy.cs', '''using System.Text.Json;\n\nnamespace PaperTodo;\n\ninternal static class StateJsonReadPolicy\n{\n    public const JsonCommentHandling CommentHandling = JsonCommentHandling.Skip;\n    public const bool AllowTrailingCommas = true;\n\n    public static JsonDocumentOptions DocumentOptions => new()\n    {\n        CommentHandling = CommentHandling,\n        AllowTrailingCommas = AllowTrailingCommas\n    };\n}\n''')

write('UiLanguages.cs', '''using System.Globalization;\nusing System.IO;\nusing System.Text.Json;\n\nnamespace PaperTodo;\n\npublic static class UiLanguages\n{\n    public const string System = "system";\n    public const string ChineseSimplified = "zh-CN";\n    public const string English = "en-US";\n    public const string Japanese = "ja-JP";\n    public const string Korean = "ko-KR";\n\n#if PAPERTODO_DEFAULT_ENGLISH\n    public const string Default = English;\n#else\n    public const string Default = System;\n#endif\n\n    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentCulture;\n    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;\n\n    public static CultureInfo EffectiveCulture { get; private set; } = SystemCulture;\n    public static CultureInfo EffectiveUiCulture { get; private set; } = SystemUiCulture;\n\n    public static string Normalize(string? language)\n        => language is ChineseSimplified or English or Japanese or Korean ? language : System;\n\n    public static string LoadPersistedPreference()\n    {\n        foreach (var fileName in new[] { "data.json", "data.backup.json" })\n        {\n            var path = Path.Combine(AppContext.BaseDirectory, fileName);\n            if (!File.Exists(path)) continue;\n            try\n            {\n                using var document = JsonDocument.Parse(\n                    File.ReadAllText(path),\n                    StateJsonReadPolicy.DocumentOptions);\n                if (document.RootElement.TryGetProperty("uiLanguage", out var value) &&\n                    value.ValueKind == JsonValueKind.String)\n                {\n                    return Normalize(value.GetString());\n                }\n                return Default;\n            }\n            catch\n            {\n                // Normal state loading owns corruption reporting; localization is best-effort.\n            }\n        }\n        return Default;\n    }\n\n    public static void ConfigureStartupLanguage(string? commandLineLanguage)\n    {\n        if (TryResolveCommandLineCulture(commandLineLanguage, out var commandCulture))\n        {\n            EffectiveCulture = commandCulture;\n            EffectiveUiCulture = commandCulture;\n            return;\n        }\n\n        var preference = LoadPersistedPreference();\n        EffectiveCulture = ResolveCulture(preference, SystemCulture);\n        EffectiveUiCulture = ResolveCulture(preference, SystemUiCulture);\n    }\n\n    public static bool TryGetCulture(string? language, out CultureInfo culture)\n    {\n        var normalized = Normalize(language);\n        if (normalized == System)\n        {\n            culture = null!;\n            return false;\n        }\n        culture = CultureInfo.GetCultureInfo(normalized);\n        return true;\n    }\n\n    private static CultureInfo ResolveCulture(string? language, CultureInfo systemCulture)\n    {\n        var normalized = Normalize(language);\n        return normalized == System ? systemCulture : CultureInfo.GetCultureInfo(normalized);\n    }\n\n    private static bool TryResolveCommandLineCulture(string? language, out CultureInfo culture)\n    {\n        culture = null!;\n        var value = (language ?? "").Trim().Replace('_', '-');\n        if (string.IsNullOrWhiteSpace(value)) return false;\n        try\n        {\n            var requested = CultureInfo.GetCultureInfo(value);\n            if (requested.TwoLetterISOLanguageName is not ("zh" or "en" or "ja" or "ko"))\n                return false;\n            culture = requested.IsNeutralCulture\n                ? CultureInfo.GetCultureInfo(requested.TwoLetterISOLanguageName switch\n                {\n                    "zh" => ChineseSimplified,\n                    "ja" => Japanese,\n                    "ko" => Korean,\n                    _ => English\n                })\n                : requested;\n            return true;\n        }\n        catch (CultureNotFoundException)\n        {\n            return false;\n        }\n    }\n}\n''')

replace_member('App.xaml.cs', 'private static void ApplyStartupCultureOverride', '''    private static void ApplyStartupCultureOverride(string? defaultLanguage)\n    {\n        // Explicit --language/--lang remains highest priority. Without it, use the\n        // persisted Settings choice; "system" preserves the real process culture.\n        UiLanguages.ConfigureStartupLanguage(defaultLanguage);\n        ApplyCulture(UiLanguages.EffectiveCulture);\n    }''')

write('Strings.cs', '''using System.Resources;\n\nnamespace PaperTodo;\n\npublic static class Strings\n{\n    private static readonly ResourceManager Manager = new("PaperTodo.Resources.Strings", typeof(Strings).Assembly);\n\n    public static string Get(string key)\n    {\n        return Manager.GetString(key, UiLanguages.EffectiveUiCulture) ?? key;\n    }\n\n    public static string Format(string key, params object[] args)\n    {\n        return string.Format(UiLanguages.EffectiveCulture, Get(key), args);\n    }\n}\n''')

# Keep WPF shaping/measurement pinned to the configured UI culture even across Dispatcher context flow.
for path in ['AppTypography.cs', 'MasterCapsuleWindow.cs', 'PaperWindow.cs', 'AppController.Shortcuts.cs']:
    text = read(path)
    text = text.replace('CultureInfo.CurrentUICulture', 'UiLanguages.EffectiveUiCulture')
    write(path, text)

# App-only themed select copied from the final main implementation, with plugin-specific entry removed.
select = git_show('origin/main:src/PaperSelectControl.cs')
select = select.replace('using PaperTodo.Plugin;\n', '')
for marker in ['public static void ApplyPluginTheme', 'private static Brush Brush(', 'private static string AddAlpha(']:
    start, end, _ = extract_braced_member(select, marker)
    select = select[:start] + select[end:]
select = select.replace('PaperSelectControl', 'SettingsSelectControl')
select = select.replace('Host-owned ComboBox visual exposed to trusted native plugins. Plugins keep data and selection\n/// semantics while PaperTodo owns chrome, popup, theme and DPI behavior.', 'PaperTodo-owned ComboBox chrome for settings. The popup, selection rows and hover state\n/// follow the active app theme instead of falling back to the system ComboBox theme.')
write('SettingsSelectControl.cs', select)

write('AppController.SettingsSelects.cs', '''using System;\nusing System.Windows;\nusing System.Windows.Controls;\n\nnamespace PaperTodo;\n\npublic sealed partial class AppController\n{\n    private void SetUiLanguage(string language)\n    {\n        var normalized = UiLanguages.Normalize(language);\n        if (string.Equals(State.UiLanguage, normalized, StringComparison.Ordinal)) return;\n        State.UiLanguage = normalized;\n        SaveNow();\n        RefreshSettingsWindowContent();\n    }\n\n    private UIElement CreateUiLanguageSettingsRow()\n    {\n        var panel = new StackPanel();\n        panel.Children.Add(WrapWithHint(\n            SettingsFieldLabel(Strings.Get("SettingsUiLanguage"), topMargin: 4),\n            "TipSettingsUiLanguage"));\n        panel.Children.Add(CreateUiLanguageSelector());\n        return panel;\n    }\n\n    private UIElement CreateUiLanguageSelector()\n    {\n        return CreateSettingsSelect(\n            [\n                (UiLanguages.System, Strings.Get("UiLanguageSystem")),\n                (UiLanguages.ChineseSimplified, Strings.Get("UiLanguageZhHans")),\n                (UiLanguages.English, Strings.Get("UiLanguageEnglish")),\n                (UiLanguages.Japanese, Strings.Get("UiLanguageJapanese")),\n                (UiLanguages.Korean, Strings.Get("UiLanguageKorean"))\n            ],\n            UiLanguages.Normalize(State.UiLanguage),\n            SetUiLanguage);\n    }\n\n    private void SetDeepCapsuleGapSize(string size)\n    {\n        var normalized = DeepCapsuleGapSizes.Normalize(size);\n        if (State.DeepCapsuleGapSize == normalized) return;\n        State.DeepCapsuleGapSize = normalized;\n        SaveNow();\n        ArrangeDeepCapsules(animate: State.EnableAnimations);\n        RefreshSettingsWindowContent();\n    }\n\n    private UIElement CreateDeepCapsuleGapSelector()\n    {\n        return CreateSettingsSelect(\n            [\n                (DeepCapsuleGapSizes.Narrow, Strings.Get("DeepCapsuleGapNarrow")),\n                (DeepCapsuleGapSizes.Standard, Strings.Get("DeepCapsuleGapStandard")),\n                (DeepCapsuleGapSizes.Wide, Strings.Get("DeepCapsuleGapWide"))\n            ],\n            DeepCapsuleGapSizes.Normalize(State.DeepCapsuleGapSize),\n            SetDeepCapsuleGapSize);\n    }\n\n    private UIElement CreateSettingsSelect(\n        (string Key, string Label)[] choices,\n        string selectedKey,\n        Action<string> onSelect)\n    {\n        var combo = new ComboBox\n        {\n            Height = AppTypography.FitChrome(28),\n            VerticalContentAlignment = VerticalAlignment.Center,\n            HorizontalContentAlignment = HorizontalAlignment.Stretch,\n            Focusable = false,\n            Margin = new Thickness(0, 4, 0, 10)\n        };\n        SettingsSelectControl.ApplyAppTheme(combo, AppTypography.Scale(12));\n        ComboBoxItem? selected = null;\n        foreach (var (key, label) in choices)\n        {\n            var item = new ComboBoxItem { Tag = key, Content = label };\n            combo.Items.Add(item);\n            if (string.Equals(key, selectedKey, StringComparison.Ordinal)) selected = item;\n        }\n        if (combo.Items.Count > 0) combo.SelectedItem = selected ?? combo.Items[0];\n        combo.SelectionChanged += (_, _) =>\n        {\n            if (combo.SelectedItem is ComboBoxItem { Tag: string key }) onSelect(key);\n        };\n        return combo;\n    }\n}\n''')

# ---------- state fields / capsule gap ----------
models = read('Models.cs')
anchor = 'public static class TodoVisualSizes\n'
if 'public static class DeepCapsuleGapSizes' not in models:
    gap_class = '''public static class DeepCapsuleGapSizes\n{\n    public const string Narrow = "narrow";\n    public const string Standard = "standard";\n    public const string Wide = "wide";\n    public const double StandardGap = 4;\n    public const double VariantDelta = 4;\n\n    public static string Normalize(string? size) =>\n        size is Narrow or Wide ? size : Standard;\n\n    public static double Value(string? size) => Normalize(size) switch\n    {\n        Narrow => StandardGap - VariantDelta,\n        Wide => StandardGap + VariantDelta,\n        _ => StandardGap\n    };\n}\n\n'''
    models = models.replace(anchor, gap_class + anchor, 1)
models = models.replace('    [JsonRequired]\n    public List<PaperData> Papers { get; set; } = new();\n', '    [JsonRequired]\n    public List<PaperData> Papers { get; set; } = new();\n    public string UiLanguage { get; set; } = UiLanguages.Default;\n', 1)
models = models.replace('    public bool UseDeepCapsuleMode { get; set; } = true;\n', '    public bool UseDeepCapsuleMode { get; set; } = true;\n    public string DeepCapsuleGapSize { get; set; } = DeepCapsuleGapSizes.Standard;\n', 1)
models = models.replace('    public Dictionary<string, bool> GlobalHotkeyEnabled { get; set; } = new();\n', '    public Dictionary<string, bool> GlobalHotkeyEnabled { get; set; } = new();\n    // Preserve 3.x behavior on upgrade: number-row and numpad keys remain distinct unless the user opts into mixed response.\n    public bool DistinguishNumpadShortcutDigits { get; set; } = true;\n', 1)
write('Models.cs', models)

replace_once('EdgeCapsuleLayout.cs', '    // Vertical gap between stacked capsules.\n    public const double Gap = 4;\n', '''    // Vertical gap between stacked capsules. Standard preserves the 3.2 layout.\n    public static double Gap =>\n        AppController.Current is { State: { } state }\n            ? DeepCapsuleGapSizes.Value(state.DeepCapsuleGapSize)\n            : DeepCapsuleGapSizes.StandardGap;\n''')

# Settings rows + preserve master-capsule preference when the dependent mode is temporarily disabled.
replace_once('AppController.Settings.cs', '        leftColumn.Children.Add(SettingsSectionLabel(Strings.Get("SettingsGeneral")));\n', '        leftColumn.Children.Add(SettingsSectionLabel(Strings.Get("SettingsGeneral")));\n        leftColumn.Children.Add(CreateUiLanguageSettingsRow());\n')
replace_once('AppController.Settings.cs', '        leftColumn.Children.Add(CreateResizeGripModeSegmentSelector());\n', '        leftColumn.Children.Add(CreateResizeGripModeSegmentSelector());\n        leftColumn.Children.Add(WrapWithHint(\n            SettingsFieldLabel(Strings.Get("SettingsDeepCapsuleGap")),\n            "TipSettingsDeepCapsuleGap"));\n        leftColumn.Children.Add(CreateDeepCapsuleGapSelector());\n')
replace_all_checked('AppController.Settings.cs', '            State.UseCapsuleCollapseAll = false;\n', '', 2)

# Restore-default behavior for new settings.
settings = read('AppController.Settings.cs')
settings = settings.replace('        State.HidePapersFromTaskbar = true;\n', '        State.UiLanguage = UiLanguages.Default;\n        State.HidePapersFromTaskbar = true;\n', 1)
# Visual page defaults own the visual gap; if the exact method changes later, insert before its SaveNow/refresh block via theme anchor.
if 'State.DeepCapsuleGapSize = DeepCapsuleGapSizes.Standard;' not in settings:
    marker = '        State.ResizeGripMode = ResizeGripModes.Soft;\n'
    if marker in settings:
        settings = settings.replace(marker, marker + '        State.DeepCapsuleGapSize = DeepCapsuleGapSizes.Standard;\n', 1)
write('AppController.Settings.cs', settings)

# ---------- distinct numpad shortcuts ----------
gh = read('GlobalHotkeys.cs')
if 'public bool IsDigitKey =>' not in gh:
    marker = '    public string ToEdgePrefixDisplayString()\n'
    addition = '''    public bool IsDigitKey =>\n        Key is (>= Key.D0 and <= Key.D9) or (>= Key.NumPad0 and <= Key.NumPad9);\n\n    public ShortcutGesture NormalizeNumpadDigit()\n    {\n        if (Key is >= Key.NumPad0 and <= Key.NumPad9)\n        {\n            var ordinal = (int)Key - (int)Key.NumPad0;\n            return new ShortcutGesture((Key)((int)Key.D0 + ordinal), Modifiers);\n        }\n        return this;\n    }\n\n    public IEnumerable<ShortcutGesture> RegistrationGestures(bool includeDigitAlias)\n    {\n        yield return this;\n        if (!includeDigitAlias) yield break;\n        if (Key is >= Key.D0 and <= Key.D9)\n        {\n            var ordinal = (int)Key - (int)Key.D0;\n            yield return new ShortcutGesture((Key)((int)Key.NumPad0 + ordinal), Modifiers);\n        }\n        else if (Key is >= Key.NumPad0 and <= Key.NumPad9)\n        {\n            var ordinal = (int)Key - (int)Key.NumPad0;\n            yield return new ShortcutGesture((Key)((int)Key.D0 + ordinal), Modifiers);\n        }\n    }\n\n'''
    if marker not in gh: raise RuntimeError('GlobalHotkeys: edge display marker not found')
    gh = gh.replace(marker, addition + marker, 1)
write('GlobalHotkeys.cs', gh)

main_gh = git_show('origin/main:src/GlobalHotkeys.cs')
_, _, main_try_apply = extract_braced_member(main_gh, 'public bool TryApply(')
replace_member('GlobalHotkeys.cs', 'public bool TryApply(', main_try_apply)

shortcuts = read('AppController.Shortcuts.cs')
# Three stable TryApply call sites.
patterns = [
    ('                State.GlobalHotkeys,\n                enabledCommandIds,\n                out _shortcutApplyFailureId,', '                State.GlobalHotkeys,\n                enabledCommandIds,\n                State.DistinguishNumpadShortcutDigits,\n                out _shortcutApplyFailureId,'),
    ('                desiredBindings,\n                enabledCommandIds,\n                out var failedCommandId,', '                desiredBindings,\n                enabledCommandIds,\n                State.DistinguishNumpadShortcutDigits,\n                out var failedCommandId,'),
    ('                desired,\n                enabledCommandIds,\n                out var failedCommandId,', '                desired,\n                enabledCommandIds,\n                State.DistinguishNumpadShortcutDigits,\n                out var failedCommandId,')
]
for old, new in patterns:
    if old not in shortcuts: raise RuntimeError(f'Shortcuts TryApply anchor missing: {old[:40]}')
    shortcuts = shortcuts.replace(old, new, 1)

ui_anchor = '''        foreach (var definition in GlobalShortcutCatalog.DefinitionsInGroup(GlobalShortcutGroup.General))\n        {\n            rows.Children.Add(BuildShortcutRow(definition));\n        }\n\n'''
ui_add = '''        rows.Children.Add(WrapWithHint(\n            SettingsToggle(\n                Strings.Get("SettingsDistinguishNumpadShortcutDigits"),\n                State.DistinguishNumpadShortcutDigits,\n                ToggleDistinguishNumpadShortcutDigits),\n            "TipSettingsDistinguishNumpadShortcutDigits"));\n\n'''
if ui_anchor not in shortcuts: raise RuntimeError('shortcut UI anchor missing')
shortcuts = shortcuts.replace(ui_anchor, ui_anchor + ui_add, 1)

method_anchor = '    private void FocusShortcutRecorder()\n'
methods = '''    private void ToggleDistinguishNumpadShortcutDigits()\n    {\n        var desiredMode = !State.DistinguishNumpadShortcutDigits;\n        var desiredBindings = GlobalShortcutCatalog.NormalizeBindings(State.GlobalHotkeys);\n        var desiredEnabled = GlobalShortcutCatalog.NormalizeEnabled(State.GlobalHotkeyEnabled);\n        if (!desiredMode && NumpadEquivalentConflictIds(desiredBindings, desiredEnabled).Count > 0)\n        {\n            ShowNumpadShortcutModeConflict();\n            RefreshSettingsWindowContent();\n            return;\n        }\n\n        var enabledCommandIds = GlobalShortcutCatalog.ExecutableIds\n            .Where(id => desiredEnabled.GetValueOrDefault(id))\n            .ToArray();\n        var manager = EnsureGlobalHotkeyManager();\n        if (!manager.TryApply(\n                desiredBindings,\n                enabledCommandIds,\n                desiredMode,\n                out _,\n                out _))\n        {\n            ShowNumpadShortcutModeConflict();\n            RefreshSettingsWindowContent();\n            return;\n        }\n\n        State.DistinguishNumpadShortcutDigits = desiredMode;\n        ClearShortcutApplyFailure();\n        SaveNow();\n        RefreshSettingsWindowContent();\n    }\n\n    private void ShowNumpadShortcutModeConflict()\n    {\n        if (_settingsWindow != null)\n        {\n            PaperNoticeDialog.Show(\n                _settingsWindow,\n                Strings.Get("ShortcutNumpadModeConflictTitle"),\n                Strings.Get("ShortcutNumpadModeConflictMessage"));\n        }\n    }\n\n'''
if method_anchor not in shortcuts: raise RuntimeError('shortcut method anchor missing')
shortcuts = shortcuts.replace(method_anchor, methods + method_anchor, 1)

dup_anchor = '        // Same modifier prefix on left and right collides for every digit 1–9.\n        MarkEdgePrefixConflictsAsDuplicates();\n'
dup_add = '''        if (!State.DistinguishNumpadShortcutDigits && _shortcutEnabledDraft != null)\n        {\n            _shortcutDuplicateIds.UnionWith(\n                NumpadEquivalentConflictIds(_shortcutDraft, _shortcutEnabledDraft));\n        }\n'''
if dup_anchor not in shortcuts: raise RuntimeError('shortcut duplicate anchor missing')
shortcuts = shortcuts.replace(dup_anchor, dup_anchor + dup_add, 1)

mark_anchor = '    private void MarkEdgePrefixConflictsAsDuplicates()\n'
conflict_method = '''    private static HashSet<string> NumpadEquivalentConflictIds(\n        IReadOnlyDictionary<string, string> bindings,\n        IReadOnlyDictionary<string, bool> enabled)\n    {\n        var conflicts = new HashSet<string>(StringComparer.Ordinal);\n        var firstCommandByGesture = new Dictionary<ShortcutGesture, string>();\n        foreach (var pair in bindings)\n        {\n            if (!enabled.GetValueOrDefault(pair.Key) ||\n                GlobalShortcutCatalog.Find(pair.Key)?.IsEdgeCapsule == true ||\n                !ShortcutGesture.TryParse(pair.Value, out var gesture) ||\n                !gesture.IsDigitKey)\n            {\n                continue;\n            }\n            var normalized = gesture.NormalizeNumpadDigit();\n            if (firstCommandByGesture.TryGetValue(normalized, out var firstCommandId))\n            {\n                conflicts.Add(firstCommandId);\n                conflicts.Add(pair.Key);\n            }\n            else\n            {\n                firstCommandByGesture[normalized] = pair.Key;\n            }\n        }\n        return conflicts;\n    }\n\n'''
if mark_anchor not in shortcuts: raise RuntimeError('shortcut mark anchor missing')
shortcuts = shortcuts.replace(mark_anchor, conflict_method + mark_anchor, 1)
shortcuts = shortcuts.replace('            State.OpenEdgeCapsuleShortcutAtCursor = true;\n', '            State.OpenEdgeCapsuleShortcutAtCursor = true;\n            State.DistinguishNumpadShortcutDigits = true;\n', 1)
write('AppController.Shortcuts.cs', shortcuts)

# ---------- visual depth ----------
def ensure_using(path, using_line, after='using System.Windows.Media;\n'):
    text = read(path)
    if using_line not in text:
        if after not in text: raise RuntimeError(f'{path}: using anchor missing')
        text = text.replace(after, after + using_line, 1)
        write(path, text)

ensure_using('EdgeCapsuleHost.cs', 'using System.Windows.Media.Effects;\n')
ensure_using('MasterCapsuleWindow.cs', 'using System.Windows.Media.Effects;\n')
ensure_using('EdgeCapsuleDragWindow.cs', 'using System.Windows.Media.Effects;\n')

replace_once('EdgeCapsuleHost.cs', '''            BorderThickness = new Thickness(1),\n            Background = options.PaperBrush,\n            BorderBrush = options.PaperBorderBrush,\n            SnapsToDevicePixels = true\n        };''', '''            BorderThickness = new Thickness(1),\n            Background = options.PaperBrush,\n            BorderBrush = options.PaperBorderBrush,\n            SnapsToDevicePixels = true,\n            Effect = new DropShadowEffect\n            {\n                BlurRadius = 4,\n                ShadowDepth = 0,\n                Opacity = 0.10\n            }\n        };''')
replace_once('MasterCapsuleWindow.cs', '''            BorderBrush = Theme.PaperBorderBrush,\n            SnapsToDevicePixels = true,\n            Cursor = System.Windows.Input.Cursors.Hand\n        };''', '''            BorderBrush = Theme.PaperBorderBrush,\n            SnapsToDevicePixels = true,\n            Cursor = System.Windows.Input.Cursors.Hand,\n            Effect = new DropShadowEffect\n            {\n                BlurRadius = 4,\n                ShadowDepth = 0,\n                Opacity = 0.10\n            }\n        };''')
replace_once('EdgeCapsuleDragWindow.cs', '''            BorderThickness = new Thickness(1),\n            CornerRadius = new CornerRadius(options.Shape.CornerRadiusDip),\n            SnapsToDevicePixels = true\n        });''', '''            BorderThickness = new Thickness(1),\n            CornerRadius = new CornerRadius(options.Shape.CornerRadiusDip),\n            SnapsToDevicePixels = true,\n            Effect = new DropShadowEffect\n            {\n                BlurRadius = 8,\n                ShadowDepth = 1,\n                Opacity = 0.12\n            }\n        });''')

paper = read('PaperWindow.cs')
paper = paper.replace('? CreatePaperChromeShadow(blurRadius: 8, opacity: 0.08)\n            : CreatePaperChromeShadow();', '? CreatePaperChromeShadow(blurRadius: 8, opacity: 0.12, shadowDepth: 1)\n            : CreatePaperChromeShadow();')
paper = paper.replace('private static DropShadowEffect CreatePaperChromeShadow(double blurRadius = 14, double opacity = 0.18)\n    {\n        return new DropShadowEffect\n        {\n            BlurRadius = blurRadius,\n            ShadowDepth = 2,\n            Opacity = opacity\n        };\n    }', 'private static DropShadowEffect CreatePaperChromeShadow(\n        double blurRadius = 14,\n        double opacity = 0.22,\n        double shadowDepth = 2)\n    {\n        return new DropShadowEffect\n        {\n            BlurRadius = blurRadius,\n            ShadowDepth = shadowDepth,\n            Opacity = opacity\n        };\n    }')
write('PaperWindow.cs', paper)

# ---------- resources ----------
translations = {
    'Resources/Strings.resx': {
        'SettingsUiLanguage': '界面语言',
        'TipSettingsUiLanguage': '选择 PaperTodo 的界面语言；保存后重启程序生效。显式启动语言参数仍优先。',
        'UiLanguageSystem': '跟随系统', 'UiLanguageZhHans': '简体中文', 'UiLanguageEnglish': 'English', 'UiLanguageJapanese': '日本語', 'UiLanguageKorean': '한국어',
        'SettingsDeepCapsuleGap': '边缘胶囊间距', 'TipSettingsDeepCapsuleGap': '调整贴边胶囊队列的纵向间距；标准保持旧版 4 DIP。',
        'DeepCapsuleGapNarrow': '较窄', 'DeepCapsuleGapStandard': '标准', 'DeepCapsuleGapWide': '较宽',
        'SettingsDistinguishNumpadShortcutDigits': '区分小键盘数字键',
        'TipSettingsDistinguishNumpadShortcutDigits': '开启后数字键与小键盘数字键可分别注册；关闭后两者混合响应，但不会修改已保存的快捷键。快速启动侧边胶囊不受影响。',
        'ShortcutNumpadModeConflictTitle': '小键盘快捷键冲突',
        'ShortcutNumpadModeConflictMessage': '无法切换小键盘模式：现有快捷键存在数字键/小键盘冲突，或混合响应所需的组合已被其他程序占用。现有快捷键不会被修改。'
    },
    'Resources/Strings.en.resx': {
        'SettingsUiLanguage': 'Interface language',
        'TipSettingsUiLanguage': 'Choose the PaperTodo interface language. The saved choice takes effect after restart; an explicit startup language still wins.',
        'UiLanguageSystem': 'Follow system', 'UiLanguageZhHans': '简体中文', 'UiLanguageEnglish': 'English', 'UiLanguageJapanese': '日本語', 'UiLanguageKorean': '한국어',
        'SettingsDeepCapsuleGap': 'Edge capsule spacing', 'TipSettingsDeepCapsuleGap': 'Adjust vertical spacing in docked capsule queues. Standard preserves the previous 4 DIP spacing.',
        'DeepCapsuleGapNarrow': 'Narrow', 'DeepCapsuleGapStandard': 'Standard', 'DeepCapsuleGapWide': 'Wide',
        'SettingsDistinguishNumpadShortcutDigits': 'Distinguish numpad digits',
        'TipSettingsDistinguishNumpadShortcutDigits': 'When enabled, number-row and numpad digits can be registered separately. When disabled, either key triggers the stored binding without rewriting it. Edge quick-launch sequences are unchanged.',
        'ShortcutNumpadModeConflictTitle': 'Numpad shortcut conflict',
        'ShortcutNumpadModeConflictMessage': 'The numpad mode could not be changed because existing bindings conflict across number-row/numpad digits, or a required mixed-mode combination is already owned by another app. Existing bindings were not changed.'
    },
    'Resources/Strings.ja.resx': {
        'SettingsUiLanguage': '表示言語', 'TipSettingsUiLanguage': 'PaperTodo の表示言語を選択します。保存後、再起動すると反映されます。明示的な起動言語指定が優先されます。',
        'UiLanguageSystem': 'システムに従う', 'UiLanguageZhHans': '简体中文', 'UiLanguageEnglish': 'English', 'UiLanguageJapanese': '日本語', 'UiLanguageKorean': '한국어',
        'SettingsDeepCapsuleGap': '端カプセルの間隔', 'TipSettingsDeepCapsuleGap': '端に固定したカプセル列の縦間隔を調整します。標準は従来の 4 DIP を維持します。',
        'DeepCapsuleGapNarrow': '狭い', 'DeepCapsuleGapStandard': '標準', 'DeepCapsuleGapWide': '広い',
        'SettingsDistinguishNumpadShortcutDigits': 'テンキー数字を区別',
        'TipSettingsDistinguishNumpadShortcutDigits': 'オンでは数字列とテンキーを別々に登録できます。オフでは保存値を書き換えず両方で反応します。端のクイック起動シーケンスには影響しません。',
        'ShortcutNumpadModeConflictTitle': 'テンキーショートカットの競合',
        'ShortcutNumpadModeConflictMessage': '既存の数字列/テンキー割り当てが競合しているか、混合応答に必要な組み合わせを他のアプリが使用しているため切り替えできません。既存の割り当ては変更されません。'
    },
    'Resources/Strings.ko.resx': {
        'SettingsUiLanguage': '인터페이스 언어', 'TipSettingsUiLanguage': 'PaperTodo 인터페이스 언어를 선택합니다. 저장 후 다시 시작하면 적용되며 명시적인 시작 언어가 우선합니다.',
        'UiLanguageSystem': '시스템 설정 따르기', 'UiLanguageZhHans': '简体中文', 'UiLanguageEnglish': 'English', 'UiLanguageJapanese': '日本語', 'UiLanguageKorean': '한국어',
        'SettingsDeepCapsuleGap': '가장자리 캡슐 간격', 'TipSettingsDeepCapsuleGap': '도킹된 캡슐 열의 세로 간격을 조정합니다. 표준은 기존 4 DIP를 유지합니다.',
        'DeepCapsuleGapNarrow': '좁게', 'DeepCapsuleGapStandard': '표준', 'DeepCapsuleGapWide': '넓게',
        'SettingsDistinguishNumpadShortcutDigits': '숫자 키패드 숫자 구분',
        'TipSettingsDistinguishNumpadShortcutDigits': '켜면 숫자열과 숫자 키패드를 따로 등록할 수 있습니다. 끄면 저장된 값을 바꾸지 않고 둘 다 반응합니다. 가장자리 빠른 실행 시퀀스에는 영향을 주지 않습니다.',
        'ShortcutNumpadModeConflictTitle': '숫자 키패드 단축키 충돌',
        'ShortcutNumpadModeConflictMessage': '기존 숫자열/숫자 키패드 바인딩이 충돌하거나 혼합 응답에 필요한 조합을 다른 앱이 사용 중이라 모드를 변경할 수 없습니다. 기존 바인딩은 변경되지 않습니다.'
    }
}

def xml_escape(value):
    return value.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;')

for path, entries in translations.items():
    text = read(path)
    missing = []
    for key, value in entries.items():
        if f'name="{key}"' not in text:
            missing.append(f'  <data name="{key}" xml:space="preserve">\n    <value>{xml_escape(value)}</value>\n  </data>\n')
    if missing:
        if '</root>' not in text: raise RuntimeError(f'{path}: no root terminator')
        text = text.replace('</root>', ''.join(missing) + '</root>', 1)
        write(path, text)

# ---------- changelog ----------
changelog = read('CHANGELOG.md')
anchor = '### v3.3\n\n**优化和修复**\n\n'
addition = '''- 设置新增界面语言选择，可选择跟随系统、简体中文、English、日本语或韩语；保存后重启生效，并修复异步界面刷新时语言可能退回系统语言的问题。\n- 全局快捷键可选择是否区分主键盘数字键与小键盘数字键；默认保持 3.x 原有的独立响应。\n- 视觉设置新增贴边胶囊间距三档：0 / 4 / 8 DIP，默认仍为原来的 4 DIP。\n- 关闭胶囊或贴边胶囊模式时不再清除“显示主胶囊”的用户偏好，重新启用后继续沿用。\n- 调整展开纸片、普通胶囊、拖出的贴边胶囊和主/贴边胶囊的阴影层次，使不同形态更易区分。\n'''
if anchor not in changelog: raise RuntimeError('CHANGELOG v3.3 anchor missing')
changelog = changelog.replace(anchor, anchor + addition, 1)
write('CHANGELOG.md', changelog)

# Basic invariants before compiling.
assert 'State.UseCapsuleCollapseAll = false;' not in read('AppController.Settings.cs')
assert 'public bool DistinguishNumpadShortcutDigits { get; set; } = true;' in read('Models.cs')
assert 'CreateUiLanguageSettingsRow' in read('AppController.Settings.cs')
assert 'CreateDeepCapsuleGapSelector' in read('AppController.Settings.cs')
print('Backport source transformations completed.')
