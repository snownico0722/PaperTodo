from pathlib import Path


def read(path):
    return Path(path).read_text(encoding="utf-8")


def write(path, text):
    Path(path).write_text(text, encoding="utf-8", newline="")


def replace_once(path, old, new):
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, got {count}: {old[:100]!r}")
    write(path, text.replace(old, new, 1))


# 1) 'Follow system' must preserve Windows' separate format/UI cultures.
replace_once(
    "App.xaml.cs",
    "        UiLanguages.ConfigureStartupLanguage(defaultLanguage);\n        ApplyCulture(UiLanguages.EffectiveCulture);\n",
    "        UiLanguages.ConfigureStartupLanguage(defaultLanguage);\n        if (UiLanguages.ShouldApplyThreadCulture)\n        {\n            ApplyCulture(UiLanguages.EffectiveCulture);\n        }\n")

replace_once(
    "UiLanguages.cs",
    "    public static CultureInfo EffectiveCulture { get; private set; } = SystemCulture;\n    public static CultureInfo EffectiveUiCulture { get; private set; } = SystemUiCulture;\n",
    "    public static CultureInfo EffectiveCulture { get; private set; } = SystemCulture;\n    public static CultureInfo EffectiveUiCulture { get; private set; } = SystemUiCulture;\n    public static bool ShouldApplyThreadCulture { get; private set; }\n")

replace_once(
    "UiLanguages.cs",
    "        if (TryResolveCommandLineCulture(commandLineLanguage, out var commandCulture))\n        {\n            EffectiveCulture = commandCulture;\n            EffectiveUiCulture = commandCulture;\n            return;\n        }\n\n        var preference = LoadPersistedPreference();\n        EffectiveCulture = ResolveCulture(preference, SystemCulture);\n        EffectiveUiCulture = ResolveCulture(preference, SystemUiCulture);\n",
    "        if (TryResolveCommandLineCulture(commandLineLanguage, out var commandCulture))\n        {\n            EffectiveCulture = commandCulture;\n            EffectiveUiCulture = commandCulture;\n            ShouldApplyThreadCulture = true;\n            return;\n        }\n\n        var preference = LoadPersistedPreference();\n        ShouldApplyThreadCulture = Normalize(preference) != System;\n        EffectiveCulture = ResolveCulture(preference, SystemCulture);\n        EffectiveUiCulture = ResolveCulture(preference, SystemUiCulture);\n")

# 2) Normalize new persisted settings, but keep the user's master-capsule preference
# even while its dependent runtime modes are temporarily disabled.
replace_once(
    "StateStore.cs",
    "        state.ColorScheme = ColorSchemes.Normalize(state.ColorScheme);\n",
    "        state.UiLanguage = UiLanguages.Normalize(state.UiLanguage);\n        state.ColorScheme = ColorSchemes.Normalize(state.ColorScheme);\n")

replace_once(
    "StateStore.cs",
    "        state.DeepCapsuleSide = DeepCapsuleSides.Normalize(state.DeepCapsuleSide);\n        state.DeepCapsuleMonitorDeviceName = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(state.DeepCapsuleMonitorDeviceName);\n",
    "        state.DeepCapsuleSide = DeepCapsuleSides.Normalize(state.DeepCapsuleSide);\n        state.DeepCapsuleGapSize = DeepCapsuleGapSizes.Normalize(state.DeepCapsuleGapSize);\n        state.DeepCapsuleMonitorDeviceName = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(state.DeepCapsuleMonitorDeviceName);\n")

replace_once(
    "StateStore.cs",
    "        if (!state.UseCapsuleMode || !state.UseDeepCapsuleMode)\n        {\n            state.UseCapsuleCollapseAll = false;\n        }\n\n        if (!state.UseCapsuleCollapseAll)\n        {\n            state.CapsuleCollapseAllActive = false;\n        }\n        state.CapsuleCollapseAllActiveQueues ??= new Dictionary<string, bool>();\n        state.CapsuleCollapseAllActiveQueues = NormalizeCollapseAllActiveQueues(state.CapsuleCollapseAllActiveQueues);\n        if (!state.UseCapsuleCollapseAll)\n        {\n            state.CapsuleCollapseAllActiveQueues.Clear();\n        }\n        else\n",
    "        var collapseAllRuntimeEnabled =\n            state.UseCapsuleMode &&\n            state.UseDeepCapsuleMode &&\n            state.UseCapsuleCollapseAll;\n        if (!collapseAllRuntimeEnabled)\n        {\n            state.CapsuleCollapseAllActive = false;\n        }\n        state.CapsuleCollapseAllActiveQueues ??= new Dictionary<string, bool>();\n        state.CapsuleCollapseAllActiveQueues = NormalizeCollapseAllActiveQueues(state.CapsuleCollapseAllActiveQueues);\n        if (!collapseAllRuntimeEnabled)\n        {\n            state.CapsuleCollapseAllActiveQueues.Clear();\n        }\n        else\n")

replace_once(
    "StateStore.cs",
    "        var keepDeepCapsuleStartTopMargins = state.UseCapsuleMode && state.UseDeepCapsuleMode && state.UseCapsuleCollapseAll;\n",
    "        var keepDeepCapsuleStartTopMargins = collapseAllRuntimeEnabled;\n")

# Invariants for review findings.
app = read("App.xaml.cs")
ui = read("UiLanguages.cs")
store = read("StateStore.cs")
assert "if (UiLanguages.ShouldApplyThreadCulture)" in app
assert "ShouldApplyThreadCulture = Normalize(preference) != System;" in ui
assert "state.UseCapsuleCollapseAll = false;" not in store
assert "state.UiLanguage = UiLanguages.Normalize(state.UiLanguage);" in store
assert "state.DeepCapsuleGapSize = DeepCapsuleGapSizes.Normalize(state.DeepCapsuleGapSize);" in store
print("PR #124 review fixes applied.")
