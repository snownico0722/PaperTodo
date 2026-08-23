from pathlib import Path
import json
import re

ROOT = Path('.')


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding='utf-8')


def write(path: str, text: str) -> None:
    (ROOT / path).write_text(text, encoding='utf-8', newline='\n')


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected one match, found {count}')
    return text.replace(old, new, 1)


# Manifest contract: 2.0 only. The new standalone advanced-settings UX is explicit opt-in.
path = 'src/PaperBodyPluginRegistry.cs'
text = read(path)
text = replace_once(
    text,
    '    public string[] Capabilities { get; set; } = [];\n    public int? PrimarySettings { get; set; }',
    '    public string[] Capabilities { get; set; } = [];\n    public bool AdvancedSettings { get; set; }\n    public int? PrimarySettings { get; set; }',
    'add advancedSettings manifest flag')
text = text.replace(
    '    internal const string MinimumPluginApiVersion = "1.8";',
    '    internal const string MinimumPluginApiVersion = "2.0";')
text = replace_once(
    text,
    '''        if (!string.IsNullOrWhiteSpace(manifest.MiniEntry))
        {
            if (!ApiAtLeast(manifest.ApiVersion, 1, 8))
            {
                throw new InvalidDataException(
                    "miniEntry requires apiVersion 1.8 or newer.");
            }
            if (kind != PaperBodyPluginKind.Web)
''',
    '''        if (!string.IsNullOrWhiteSpace(manifest.MiniEntry))
        {
            if (kind != PaperBodyPluginKind.Web)
''',
    'remove 1.8 miniEntry gate')
old_version_validator = '''    private static void ValidateManifestApiVersion(string pluginApiVersion)
    {
        if (string.Equals(pluginApiVersion, "1.8", StringComparison.Ordinal) ||
            string.Equals(pluginApiVersion, SupportedPluginApiVersion, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidDataException(
            $"Unsupported plugin API version {pluginApiVersion}; host supports 1.8 compatibility and {SupportedPluginApiVersion}.");
    }
'''
new_version_validator = '''    private static void ValidateManifestApiVersion(string pluginApiVersion)
    {
        if (string.Equals(
                pluginApiVersion,
                SupportedPluginApiVersion,
                StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidDataException(
            $"Unsupported plugin API version {pluginApiVersion}; host requires {SupportedPluginApiVersion}.");
    }
'''
text = replace_once(text, old_version_validator, new_version_validator, '2.0-only validator')
text = text.replace(
    '"apiVersion must be a quoted major.minor string such as \\"1.2\\"."',
    '"apiVersion must be a quoted major.minor string such as \\"2.0\\"."')
write(path, text)

# Settings metadata: keep legacy quick/inline behavior by default; advanced page only when opted in.
path = 'src/PaperBodyPluginRegistry.Settings.cs'
text = read(path)
old_prelude = '''        manifest.Settings ??= [];
        manifest.SettingCategories ??= [];
        if (manifest.PrimarySettings is < 1 or > 3)
        {
            throw new InvalidDataException("primarySettings must be between 1 and 3.");
        }
        if (!ApiAtLeast(manifest.ApiVersion, 2, 0) &&
            (manifest.PrimarySettings.HasValue || manifest.SettingCategories.Length > 0))
        {
            throw new InvalidDataException(
                "primarySettings and settingCategories require apiVersion 2.0 or newer.");
        }

        var categoryNames = new HashSet<string>(StringComparer.Ordinal);
'''
new_prelude = '''        manifest.Settings ??= [];
        manifest.SettingCategories ??= [];
        if (!manifest.AdvancedSettings &&
            (manifest.PrimarySettings.HasValue || manifest.SettingCategories.Length > 0))
        {
            throw new InvalidDataException(
                "primarySettings and settingCategories require advancedSettings: true.");
        }
        if (manifest.PrimarySettings is < 1 or > 3)
        {
            throw new InvalidDataException("primarySettings must be between 1 and 3.");
        }

        var categoryNames = new HashSet<string>(StringComparer.Ordinal);
'''
text = replace_once(text, old_prelude, new_prelude, 'advanced settings validation prelude')
text = replace_once(
    text,
    '        var ids = new HashSet<string>(StringComparer.Ordinal);\n        foreach (var setting in manifest.Settings)',
    '        var ids = new HashSet<string>(StringComparer.Ordinal);\n        var quickCount = 0;\n        foreach (var setting in manifest.Settings)',
    'restore quick counter')
text = replace_once(
    text,
    '''            if (setting.Category.Length > 0 && !ApiAtLeast(manifest.ApiVersion, 2, 0))
            {
                throw new InvalidDataException(
                    $"Plugin setting '{setting.Id}' category requires apiVersion 2.0 or newer.");
            }
''',
    '''            if (setting.Category.Length > 0 && !manifest.AdvancedSettings)
            {
                throw new InvalidDataException(
                    $"Plugin setting '{setting.Id}' category requires advancedSettings: true.");
            }
''',
    'category opt-in gate')
text = replace_once(
    text,
    '''            if (setting.MaxLength is < 0)
''',
    '''            if (!manifest.AdvancedSettings && setting.Quick && ++quickCount > 3)
            {
                throw new InvalidDataException("A plugin may expose at most three quick settings.");
            }
            if (setting.MaxLength is < 0)
''',
    'restore quick validation')
text = replace_once(
    text,
    '    public JsonElement Default { get; set; }\n    public string Category { get; set; } = "";',
    '    public JsonElement Default { get; set; }\n    public bool Quick { get; set; }\n    public string Category { get; set; } = "";',
    'restore quick property')
write(path, text)

# UI: unchanged legacy inline expansion unless advancedSettings=true.
path = 'src/AppController.Plugins.cs'
text = read(path)
pattern = re.compile(
    r'    private FrameworkElement BuildPluginSettingsPanel\(.*?^    private void ShowPluginSettingsWindow\(',
    re.S | re.M)
replacement = r'''    private FrameworkElement BuildPluginSettingsPanel(
        PaperBodyPluginDescriptor descriptor,
        IReadOnlyList<PaperBodyPluginSettingManifest> settings)
    {
        return descriptor.Manifest?.AdvancedSettings == true
            ? BuildAdvancedPluginSettingsPanel(descriptor, settings)
            : BuildInlinePluginSettingsPanel(descriptor, settings);
    }

    private FrameworkElement BuildInlinePluginSettingsPanel(
        PaperBodyPluginDescriptor descriptor,
        IReadOnlyList<PaperBodyPluginSettingManifest> settings)
    {
        var root = new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0)
        };
        var quick = settings.Where(item => item.Quick).Take(3).ToArray();
        var remaining = settings.Where(item => !item.Quick).ToArray();
        if (remaining.Length == 0)
        {
            foreach (var setting in quick)
            {
                root.Children.Add(BuildPluginSettingControl(descriptor, setting));
            }
            return root;
        }

        var more = new StackPanel
        {
            Visibility = Visibility.Collapsed
        };
        foreach (var setting in remaining)
        {
            more.Children.Add(BuildPluginSettingControl(descriptor, setting));
        }

        var toggle = PluginPageButton(Strings.Get("PluginsMoreSettings"));
        toggle.MinWidth = 0;
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        toggle.Click += (_, _) =>
        {
            var expand = more.Visibility != Visibility.Visible;
            more.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = Strings.Get(
                expand ? "PluginsLessSettings" : "PluginsMoreSettings");
        };

        if (quick.Length == 0)
        {
            toggle.HorizontalAlignment = HorizontalAlignment.Right;
            root.Children.Add(toggle);
        }
        else
        {
            foreach (var setting in quick[..^1])
            {
                root.Children.Add(BuildPluginSettingControl(descriptor, setting));
            }

            var tail = new Grid();
            tail.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            tail.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });
            var finalQuick = BuildPluginSettingControl(descriptor, quick[^1]);
            finalQuick.Margin = new Thickness(0, 4, 8, 0);
            toggle.Margin = new Thickness(8, 4, 0, 0);
            toggle.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(finalQuick, 0);
            Grid.SetColumn(toggle, 1);
            tail.Children.Add(finalQuick);
            tail.Children.Add(toggle);
            root.Children.Add(tail);
        }

        root.Children.Add(more);
        return root;
    }

    private FrameworkElement BuildAdvancedPluginSettingsPanel(
        PaperBodyPluginDescriptor descriptor,
        IReadOnlyList<PaperBodyPluginSettingManifest> settings)
    {
        var root = new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0)
        };
        var primaryCount = Math.Min(
            settings.Count,
            descriptor.Manifest?.PrimarySettings ?? 3);
        for (var index = 0; index < primaryCount; index++)
        {
            root.Children.Add(BuildPluginSettingControl(descriptor, settings[index]));
        }

        if (settings.Count > primaryCount)
        {
            var more = PluginPageButton(Strings.Get("PluginsMoreSettings"));
            more.MinWidth = 0;
            more.Margin = new Thickness(0, 8, 0, 0);
            more.HorizontalAlignment = HorizontalAlignment.Left;
            more.Click += (_, _) => ShowPluginSettingsWindow(descriptor, settings);
            root.Children.Add(more);
        }

        return root;
    }

    private void ShowPluginSettingsWindow('''
text, count = pattern.subn(replacement, text, count=1)
if count != 1:
    raise RuntimeError(f'settings panel replacement: expected one match, found {count}')
text = replace_once(
    text,
    '        window.ShowDialog();\n    }',
    '''        window.ShowDialog();
        if (!IsExiting)
        {
            RefreshSettingsWindowContent();
        }
    }''',
    'refresh card after advanced dialog')
text = replace_once(
    text,
    '''        var useColumns = elements.Any(item =>
                item.Column is "left" or "right") ||
            naturalHeight > availableHeight;''',
    '''        var useColumns = elements.Any(item =>
                item.Column is "left" or "right") ||
            (elements.Count > 1 && naturalHeight > availableHeight);''',
    'avoid useless two-column single category')
write(path, text)

# Protocol policy checks: advanced mode exists, legacy quick remains, and only 2.0 is accepted.
path = 'tests/PaperTodo.ProtocolPolicyChecks/Program.cs'
text = read(path)
pattern = re.compile(
    r'    private static void CheckSettingsLayoutManifest\(Assembly host\).*?^    private static void CheckProtocolBoundaries\(Assembly host\)',
    re.S | re.M)
replacement = r'''    private static void CheckSettingsLayoutManifest(Assembly host)
    {
        var registryType = RequireType(host, "PaperTodo.PaperBodyPluginRegistry");
        var manifestType = RequireType(host, "PaperTodo.PaperBodyPluginManifest");
        var settingType = RequireType(host, "PaperTodo.PaperBodyPluginSettingManifest");
        var categoryType = RequireType(host, "PaperTodo.PaperBodyPluginSettingCategoryManifest");

        Assert(
            manifestType.GetProperty("AdvancedSettings")?.PropertyType == typeof(bool),
            "Plugin manifest must expose explicit advancedSettings opt-in metadata.");
        Assert(
            manifestType.GetProperty("PrimarySettings")?.PropertyType == typeof(int?),
            "Plugin manifest must expose optional primarySettings metadata.");
        Assert(
            manifestType.GetProperty("SettingCategories")?.PropertyType == categoryType.MakeArrayType(),
            "Plugin manifest must expose settingCategories metadata.");
        Assert(
            settingType.GetProperty("Quick")?.PropertyType == typeof(bool),
            "Legacy inline settings must retain per-setting quick metadata.");
        Assert(
            settingType.GetProperty("Category")?.PropertyType == typeof(string),
            "Advanced plugin settings must expose an optional category name.");
        Assert(
            categoryType.GetProperty("Name")?.PropertyType == typeof(string) &&
            categoryType.GetProperty("Column")?.PropertyType == typeof(string),
            "Setting categories must carry their display name and optional column placement.");

        var supported = registryType.GetField(
            "SupportedPluginApiVersion",
            BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue()?.ToString();
        var minimum = registryType.GetField(
            "MinimumPluginApiVersion",
            BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue()?.ToString();
        Assert(supported == "2.0" && minimum == "2.0",
            "The plugin host must be 2.0-only; 1.8 compatibility must not remain enabled.");
    }

    private static void CheckProtocolBoundaries(Assembly host)'''
text, count = pattern.subn(replacement, text, count=1)
if count != 1:
    raise RuntimeError(f'policy check replacement: expected one match, found {count}')
write(path, text)

# Sample manifests: complex examples opt into the new mode. TopBar deliberately keeps the default
# inline quick/expand mode so the samples cover both behaviors.
advanced_ids = {
    'sample.focus-timer.native',
    'sample.clock.native',
    'sample.review-archive.native',
    'official.clock.web',
}
manifest_paths = list((ROOT / 'plugin-samples').rglob('plugin.json')) + list((ROOT / 'plugins').rglob('plugin.json'))
for manifest_path in manifest_paths:
    data = json.loads(manifest_path.read_text(encoding='utf-8'))
    if data.get('kind') not in {'native', 'web'}:
        continue
    data['apiVersion'] = '2.0'
    plugin_id = data.get('id', '')
    if plugin_id in advanced_ids:
        data['advancedSettings'] = True
        if plugin_id == 'sample.focus-timer.native':
            data['primarySettings'] = 2
        for setting in data.get('settings', []):
            setting.pop('quick', None)
    elif plugin_id == 'sample.topbar.web':
        data.pop('advancedSettings', None)
        data.pop('primarySettings', None)
        data.pop('settingCategories', None)
        settings = data.get('settings', [])
        for setting in settings:
            setting.pop('category', None)
            setting.pop('quick', None)
        if settings:
            settings[0]['quick'] = True
    manifest_path.write_text(
        json.dumps(data, ensure_ascii=False, indent=2) + '\n',
        encoding='utf-8',
        newline='\n')

# Main plugin manual: 2.0 only, and advanced settings are explicitly opt-in.
path = 'plugin-samples/README.md'
text = read(path)
text = replace_once(
    text,
    '宿主继续兼容加载既有 `1.8` 插件，但 **Top Bar / app runtime 扩展只属于 2.0**；新开发和升级中的插件不要再把 1.8 当作目标版本。',
    '当前宿主只接受 `2.0` 插件；旧 `1.8` manifest 不再兼容加载。',
    'manual version policy')
text = text.replace(
    '| `apiVersion` | 新插件使用 `"2.0"`；宿主兼容既有 `"1.8"`，但 1.8 没有 Top Bar/app runtime 能力 |',
    '| `apiVersion` | 必须为 `"2.0"` |')
text = text.replace(
    '| `primarySettings` | 可选，2.0；插件卡片直接显示前 1～3 个设置，省略时默认 3 |',
    '| `advancedSettings` | 可选，默认 `false`；声明 `true` 后启用独立完整设置页 |\n| `primarySettings` | 可选；仅 `advancedSettings: true` 时有效，插件卡片直接显示前 1～3 个设置，省略时默认 3 |')
text = text.replace(
    '| `settingCategories` | 可选，2.0；完整设置页的分类及可选 `left` / `right` 列位置 |',
    '| `settingCategories` | 可选；仅 `advancedSettings: true` 时有效，声明完整设置页分类及可选 `left` / `right` 列位置 |')
text = text.replace(
    '| `settings` | 可选；由宿主绘制和保存的全局设置；2.0 设置项可写 `category` |',
    '| `settings` | 可选；由宿主绘制和保存的全局设置；高级模式下设置项可写 `category` |')
pattern = re.compile(
    r'### 5\.3 全局 settings\n\n.*?\nNative paper session 从 `SettingsJson`',
    re.S)
replacement = '''### 5.3 全局 settings

宿主支持：`boolean`、`string`、`number`、`select`、`shortcut`。设置仍只有一份存储和读写协议，下面两种只是宿主展示方式。

默认不声明 `advancedSettings`（或为 `false`）时，行为保持原样：最多三个 `quick: true` 设置直接显示在插件卡片上，其余设置通过“更多设置”在**当前卡片内**展开/收起。没有 `quick` 时不会自动猜主要设置。

声明 `"advancedSettings": true` 后才启用新的高级设置模式：插件卡片自动直接显示 `settings` 前 3 项，超过后“更多设置”打开独立完整设置页；可用 `primarySettings: 1..3` 覆盖直接显示数量。完整页的设置项可写 `category`，同名分类自动归组；顶层 `settingCategories` 可以给分类指定 `column: "left"` 或 `"right"`，不写列就交给宿主自动安排。宿主先尝试单列，纵向放不下且确实有多个可分配块时才自动分成左右两列；同一分类不会被拆开。

```json
{
  "advancedSettings": true,
  "primarySettings": 2,
  "settingCategories": [
    { "name": "常规", "column": "left" },
    { "name": "网络", "column": "right" },
    { "name": "调试" }
  ],
  "settings": [
    { "id": "enabled", "type": "boolean", "name": "启用", "category": "常规" },
    { "id": "mode", "type": "select", "name": "模式", "category": "常规", "options": [
      { "value": "auto", "name": "自动" },
      { "value": "manual", "name": "手动" }
    ] },
    { "id": "timeout", "type": "number", "name": "超时", "category": "网络" },
    { "id": "debug", "type": "boolean", "name": "调试日志", "category": "调试" }
  ]
}
```

Native paper session 从 `SettingsJson`'''
text, count = pattern.subn(replacement, text, count=1)
if count != 1:
    raise RuntimeError(f'manual settings section: expected one match, found {count}')
write(path, text)

# Shortcut guide/example uses the unchanged default inline mode.
path = 'plugin-samples/PROTOCOL-2.0-SHORTCUTS.md'
text = read(path)
text = replace_once(
    text,
    '      "default": "Ctrl+Alt+Shift+U",\n      "shortcutAction": "paper.toggle"',
    '      "default": "Ctrl+Alt+Shift+U",\n      "shortcutAction": "paper.toggle",\n      "quick": true',
    'shortcut guide quick example')
write(path, text)

# Sample README headlines must describe their current API target, not the removed 1.8 compatibility target.
readme_replacements = {
    'plugin-samples/PaperTodo.Plugin.FocusTimer/README.md': (
        '当前按 PaperTodo 协议 1.8 构建',
        '当前按 PaperTodo 协议 2.0 构建'),
    'plugin-samples/PaperTodo.Plugin.CloudGenshin/README.md': (
        '这是一个**完全独立的协议 1.8 原生正文插件**。',
        '这是一个**完全独立的协议 2.0 原生正文插件**。'),
    'plugin-samples/PaperTodo.Plugin.ReviewArchive/README.md': (
        '这是按 PaperTodo 协议 1.8 构建的完整原生插件',
        '这是按 PaperTodo 协议 2.0 构建的完整原生插件'),
    'plugin-samples/PaperTodo.Plugin.SampleClock/README.md': (
        '这是一个完全由 WPF 控件构成的 PaperTodo 原生主示例。',
        '这是一个完全由 WPF 控件构成、当前使用协议 2.0 的 PaperTodo 原生主示例。'),
}
for readme_path, (old, new) in readme_replacements.items():
    text = read(readme_path)
    if old not in text:
        raise RuntimeError(f'{readme_path}: expected text not found')
    write(readme_path, text.replace(old, new, 1))
