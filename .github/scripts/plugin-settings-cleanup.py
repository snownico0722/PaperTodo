from pathlib import Path
import re
import textwrap


def class_block(body: str) -> str:
    return textwrap.indent(textwrap.dedent(body).strip("\n"), "    ")


app_path = Path("src/AppController.Plugins.cs")
app_text = app_path.read_text(encoding="utf-8")
app_pattern = re.compile(
    r"    private FrameworkElement BuildPluginSettingsPanel\(.*?^    private FrameworkElement BuildPluginSettingControl\(",
    re.S | re.M,
)
app_body = r'''
private FrameworkElement BuildPluginSettingsPanel(
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

private void ShowPluginSettingsWindow(
    PaperBodyPluginDescriptor descriptor,
    IReadOnlyList<PaperBodyPluginSettingManifest> settings)
{
    var height = Math.Min(
        680,
        Math.Max(420, SystemParameters.WorkArea.Height - 120));
    var window = new Window
    {
        Title = $"{descriptor.DisplayName} · {Strings.Get("PluginsMoreSettings")}",
        Width = 720,
        Height = height,
        SizeToContent = SizeToContent.Manual,
        WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize,
        AllowsTransparency = true,
        Background = Brushes.Transparent,
        ShowInTaskbar = false,
        Topmost = false,
        WindowStartupLocation = _settingsWindow != null
            ? WindowStartupLocation.CenterOwner
            : WindowStartupLocation.CenterScreen,
        FontFamily = AppTypography.UiFontFamily,
        FontSize = AppTypography.Scale(12),
        Language = AppTypography.Language,
        SnapsToDevicePixels = true,
        UseLayoutRounding = true
    };
    if (_settingsWindow != null)
    {
        window.Owner = _settingsWindow;
    }
    AppTypography.ApplyTextRendering(window);
    window.PreviewKeyDown += OnSettingsWindowPreviewKeyDown;
    window.PreviewKeyUp += OnSettingsWindowPreviewKeyUp;
    window.Closed += (_, _) =>
    {
        if (_pluginShortcutRecordingCommandId == null)
        {
            return;
        }

        _pluginShortcutRecordingCommandId = null;
        if (!IsExiting)
        {
            RestoreAllHotkeysAfterPluginRecording();
        }
    };
    window.Content = BuildPluginSettingsWindowContent(window, descriptor, settings);
    ApplyToolTipSetting(window);
    window.ShowDialog();
}

private UIElement BuildPluginSettingsWindowContent(
    Window window,
    PaperBodyPluginDescriptor descriptor,
    IReadOnlyList<PaperBodyPluginSettingManifest> settings)
{
    var frame = new Border
    {
        Background = TrayPaperBrush,
        BorderBrush = TrayBorderBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14),
        Effect = new DropShadowEffect
        {
            BlurRadius = 20,
            ShadowDepth = 2,
            Opacity = 0.22
        }
    };
    var root = new DockPanel();
    var titleRow = new Grid
    {
        Margin = new Thickness(0, 0, 0, 10),
        Background = Brushes.Transparent,
        Cursor = Cursors.SizeAll
    };
    titleRow.ColumnDefinitions.Add(new ColumnDefinition
    {
        Width = new GridLength(1, GridUnitType.Star)
    });
    titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    titleRow.MouseLeftButtonDown += (_, e) =>
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        try { window.DragMove(); } catch { }
    };

    var title = new TextBlock
    {
        Text = $"{descriptor.DisplayName} · {Strings.Get("PluginsMoreSettings")}",
        Foreground = TrayTextBrush,
        FontSize = AppTypography.Scale(15),
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center
    };
    Grid.SetColumn(title, 0);
    titleRow.Children.Add(title);

    var closeButton = new Button
    {
        Content = "×",
        Width = 28,
        Height = 24,
        Padding = new Thickness(0),
        BorderThickness = new Thickness(1),
        Background = Brushes.Transparent,
        Foreground = TrayWeakTextBrush,
        FontFamily = AppTypography.SymbolFontFamily,
        FontSize = AppTypography.Scale(16),
        Cursor = Cursors.Hand,
        Focusable = false,
        Style = BuildSettingsCloseButtonStyle()
    };
    closeButton.Click += (_, _) => window.Close();
    Grid.SetColumn(closeButton, 1);
    titleRow.Children.Add(closeButton);
    DockPanel.SetDock(titleRow, Dock.Top);
    root.Children.Add(titleRow);

    var availableWidth = Math.Max(480, window.Width - 56);
    var availableHeight = Math.Max(260, window.Height - 92);
    var layout = BuildPluginFullSettingsLayout(
        descriptor,
        settings,
        availableWidth,
        availableHeight);
    var scroll = new ScrollViewer
    {
        Content = layout,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Margin = new Thickness(0, 2, 0, 0)
    };
    root.Children.Add(scroll);
    frame.Child = root;
    return frame;
}

private FrameworkElement BuildPluginFullSettingsLayout(
    PaperBodyPluginDescriptor descriptor,
    IReadOnlyList<PaperBodyPluginSettingManifest> settings,
    double availableWidth,
    double availableHeight)
{
    var categoryColumns = (descriptor.Manifest?.SettingCategories ?? [])
        .ToDictionary(
            item => item.Name,
            item => item.Column,
            StringComparer.Ordinal);
    var groupedCategories = new HashSet<string>(StringComparer.Ordinal);
    var units = new List<(
        string Category,
        string Column,
        PaperBodyPluginSettingManifest[] Settings)>();

    foreach (var setting in settings)
    {
        if (string.IsNullOrWhiteSpace(setting.Category))
        {
            units.Add(("", "", new[] { setting }));
            continue;
        }
        if (!groupedCategories.Add(setting.Category))
        {
            continue;
        }

        categoryColumns.TryGetValue(setting.Category, out var column);
        units.Add((
            setting.Category,
            column ?? "",
            settings.Where(item => string.Equals(
                item.Category,
                setting.Category,
                StringComparison.Ordinal)).ToArray()));
    }

    var elements = new List<(FrameworkElement Element, string Column)>();
    var naturalHeight = 0d;
    foreach (var unit in units)
    {
        var element = BuildPluginSettingCategory(
            descriptor,
            unit.Category,
            unit.Settings);
        element.Measure(new Size(availableWidth, double.PositiveInfinity));
        naturalHeight += element.DesiredSize.Height + 8;
        elements.Add((element, unit.Column));
    }

    var useColumns = elements.Any(item =>
            item.Column is "left" or "right") ||
        naturalHeight > availableHeight;
    if (!useColumns)
    {
        var single = new StackPanel();
        foreach (var item in elements)
        {
            item.Element.Margin = new Thickness(0, 0, 0, 8);
            single.Children.Add(item.Element);
        }
        return single;
    }

    var columns = new Grid();
    columns.ColumnDefinitions.Add(new ColumnDefinition
    {
        Width = new GridLength(1, GridUnitType.Star)
    });
    columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
    columns.ColumnDefinitions.Add(new ColumnDefinition
    {
        Width = new GridLength(1, GridUnitType.Star)
    });
    var left = new StackPanel();
    var right = new StackPanel();
    var halfWidth = Math.Max(220, (availableWidth - 25) / 2);
    var leftHeight = 0d;
    var rightHeight = 0d;

    foreach (var item in elements)
    {
        item.Element.Measure(new Size(halfWidth, double.PositiveInfinity));
        var measuredHeight = item.Element.DesiredSize.Height + 8;
        var placeRight = string.Equals(item.Column, "right", StringComparison.Ordinal) ||
            (!string.Equals(item.Column, "left", StringComparison.Ordinal) &&
             rightHeight < leftHeight);
        item.Element.Margin = new Thickness(0, 0, 0, 8);
        if (placeRight)
        {
            right.Children.Add(item.Element);
            rightHeight += measuredHeight;
        }
        else
        {
            left.Children.Add(item.Element);
            leftHeight += measuredHeight;
        }
    }

    var separator = new Border
    {
        Width = 1,
        Background = TrayBorderBrush,
        Opacity = 0.65,
        HorizontalAlignment = HorizontalAlignment.Center
    };
    Grid.SetColumn(left, 0);
    Grid.SetColumn(separator, 1);
    Grid.SetColumn(right, 2);
    columns.Children.Add(left);
    columns.Children.Add(separator);
    columns.Children.Add(right);
    return columns;
}

private FrameworkElement BuildPluginSettingCategory(
    PaperBodyPluginDescriptor descriptor,
    string category,
    IReadOnlyList<PaperBodyPluginSettingManifest> settings)
{
    var root = new StackPanel();
    if (!string.IsNullOrWhiteSpace(category))
    {
        root.Children.Add(SettingsSectionLabel(category));
    }
    foreach (var setting in settings)
    {
        root.Children.Add(BuildPluginSettingControl(descriptor, setting));
    }
    return root;
}

private FrameworkElement BuildPluginSettingControl(
'''
app_text, app_count = app_pattern.subn(class_block(app_body), app_text, count=1)
if app_count != 1:
    raise RuntimeError(f"AppController.Plugins.cs cleanup expected 1 block, found {app_count}")
app_path.write_text(app_text, encoding="utf-8", newline="\n")


settings_path = Path("src/PaperBodyPluginRegistry.Settings.cs")
settings_text = settings_path.read_text(encoding="utf-8")
settings_text = settings_text.replace(
    "        if (!ApiAtLeast(manifest.ApiVersion, 2, 0) &&\n  (manifest.PrimarySettings.HasValue || manifest.SettingCategories.Length > 0))",
    "        if (!ApiAtLeast(manifest.ApiVersion, 2, 0) &&\n            (manifest.PrimarySettings.HasValue || manifest.SettingCategories.Length > 0))",
)
settings_text = settings_text.replace(
    '''    private static JsonElement DefaultSettingValueWithoutDeclaredDefault(
        PaperBodyPluginSettingManifest setting) => setting.Type switch
        {
            "boolean" => JsonSerializer.SerializeToElement(false),
            "number" => JsonSerializer.SerializeToElement(NormalizeNumber(setting, 0d)),
            "select" => JsonSerializer.SerializeToElement(setting.Options[0].Value),
            "shortcut" => JsonSerializer.SerializeToElement(""),
            _ => JsonSerializer.SerializeToElement(NormalizeString(setting, ""))
        };''',
    '''    private static JsonElement DefaultSettingValueWithoutDeclaredDefault(
        PaperBodyPluginSettingManifest setting) => setting.Type switch
    {
        "boolean" => JsonSerializer.SerializeToElement(false),
        "number" => JsonSerializer.SerializeToElement(NormalizeNumber(setting, 0d)),
        "select" => JsonSerializer.SerializeToElement(setting.Options[0].Value),
        "shortcut" => JsonSerializer.SerializeToElement(""),
        _ => JsonSerializer.SerializeToElement(NormalizeString(setting, ""))
    };''',
)
settings_path.write_text(settings_text, encoding="utf-8", newline="\n")


test_path = Path("tests/PaperTodo.ProtocolPolicyChecks/Program.cs")
test_text = test_path.read_text(encoding="utf-8")
test_pattern = re.compile(
    r"    private static void CheckSettingsLayoutManifest\(Assembly host\).*?^    private static void CheckProtocolBoundaries\(Assembly host\)",
    re.S | re.M,
)
test_body = r'''
private static void CheckSettingsLayoutManifest(Assembly host)
{
    var manifestType = RequireType(host, "PaperTodo.PaperBodyPluginManifest");
    var settingType = RequireType(host, "PaperTodo.PaperBodyPluginSettingManifest");
    var categoryType = RequireType(host, "PaperTodo.PaperBodyPluginSettingCategoryManifest");

    Assert(
        manifestType.GetProperty("PrimarySettings")?.PropertyType == typeof(int?),
        "Plugin manifest must expose optional primarySettings metadata.");
    Assert(
        manifestType.GetProperty("SettingCategories")?.PropertyType == categoryType.MakeArrayType(),
        "Plugin manifest must expose settingCategories metadata.");
    Assert(
        settingType.GetProperty("Category")?.PropertyType == typeof(string),
        "Plugin settings must expose an optional category name.");
    Assert(
        settingType.GetProperty("Quick") == null,
        "Per-setting quick metadata must not remain in the 2.0 settings contract.");
    Assert(
        categoryType.GetProperty("Name")?.PropertyType == typeof(string) &&
        categoryType.GetProperty("Column")?.PropertyType == typeof(string),
        "Setting categories must carry their display name and optional column placement.");
}

private static void CheckProtocolBoundaries(Assembly host)
'''
test_text, test_count = test_pattern.subn(class_block(test_body), test_text, count=1)
if test_count != 1:
    raise RuntimeError(f"Program.cs cleanup expected 1 block, found {test_count}")
test_path.write_text(test_text, encoding="utf-8", newline="\n")
