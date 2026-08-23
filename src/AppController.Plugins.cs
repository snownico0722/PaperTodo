using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    private static readonly TimeSpan PluginTextSettingPropagationDebounce =
        TimeSpan.FromMilliseconds(180);

    private readonly PaperBodyPluginRegistry _paperBodyPlugins;
    private readonly Dictionary<string, System.Windows.Threading.DispatcherTimer>
        _pluginSettingPropagationTimers = new(StringComparer.Ordinal);

    internal PaperBodyPluginRegistry PaperBodyPlugins => _paperBodyPlugins;

    private UIElement BuildPluginsSettingsPage()
    {
        // Rebuilding the settings tree is also a recording boundary. Never let a stale recorder
        // survive into a fresh visual tree with only one of the two hotkey managers restored.
        if (_pluginShortcutRecordingCommandId != null)
        {
            _pluginShortcutRecordingCommandId = null;
            RestoreAllHotkeysAfterPluginRecording();
        }
        else
        {
            RefreshPluginShortcuts();
        }

        var root = new StackPanel
        {
            Margin = new Thickness(2, 4, 4, 0)
        };
        root.Unloaded += (_, _) =>
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

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Text = Strings.Get("PluginsPageTitle"),
            Foreground = TrayTextBrush,
            FontSize = AppTypography.Scale(13),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var openFolder = PluginPageButton(Strings.Get("PluginsOpenFolder"));
        openFolder.Margin = new Thickness(8, 0, 0, 0);
        openFolder.ToolTip = _paperBodyPlugins.PluginRoot;
        openFolder.Click += (_, _) => OpenPluginFolder();
        Grid.SetColumn(title, 0);
        Grid.SetColumn(openFolder, 1);
        header.Children.Add(title);
        header.Children.Add(openFolder);
        root.Children.Add(header);

        root.Children.Add(new TextBlock
        {
            Text = Strings.Get("PluginsIntro"),
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(11.5),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 0)
        });

        var descriptors = _paperBodyPlugins.Descriptors;
        root.Children.Add(SettingsSectionLabel(
            Strings.Format("PluginsLoadedCountFormat", descriptors.Count)));
        foreach (var descriptor in descriptors)
        {
            root.Children.Add(BuildPluginDescriptorCard(descriptor));
        }

        if (_paperBodyPlugins.Issues.Count > 0)
        {
            root.Children.Add(SettingsSectionLabel(Strings.Get("PluginsLoadProblems")));
            foreach (var issue in _paperBodyPlugins.Issues)
            {
                root.Children.Add(BuildPluginIssueCard(issue));
            }
        }

        return root;
    }

    private Button PluginPageButton(string text)
    {
        return new Button
        {
            Content = text,
            MinWidth = 72,
            Padding = new Thickness(10, 4, 10, 4),
            Style = BuildDialogButtonStyle(),
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(11.5),
            Focusable = false
        };
    }

    private UIElement BuildPluginDescriptorCard(PaperBodyPluginDescriptor descriptor)
    {
        var card = new Border
        {
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Padding = new Thickness(11, 8, 11, 9),
            Margin = new Thickness(0, 5, 0, 3)
        };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });

        var settings = descriptor.Manifest?.Settings ?? [];
        PaperBodyPluginDataReadIssue? dataIssue = null;
        if (descriptor.Kind != PaperBodyPluginKind.BuiltIn &&
            _paperBodyPlugins.DataStore.TryGetReadIssue(
                descriptor.Id,
                out var detectedDataIssue))
        {
            dataIssue = detectedDataIssue;
        }
        if (settings.Length > 0)
        {
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Min(255, SettingsWindowWidth() * 0.34))
            });
        }

        var text = new StackPanel();
        var status = PluginStatusFor(descriptor, dataIssue != null);
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        var statusDot = CreatePluginStatusDot(status);
        _pluginStatusRefreshers[descriptor.Id] = () =>
            ApplyPluginStatusDot(
                statusDot,
                PluginStatusFor(descriptor, dataIssue != null));
        Grid.SetColumn(statusDot, 0);
        titleRow.Children.Add(statusDot);

        var titleFlow = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = Strings.Format(
                "PluginsProtocolTooltipFormat",
                descriptor.ApiVersion)
        };
        titleFlow.Inlines.Add(new System.Windows.Documents.Run(descriptor.DisplayName)
        {
            Foreground = TrayTextBrush,
            FontSize = AppTypography.Scale(13),
            FontWeight = FontWeights.SemiBold
        });
        titleFlow.Inlines.Add(new System.Windows.Documents.Run(
            $" — {PluginKindText(descriptor.Kind)} · v{PluginVersionText(descriptor.Version)} · s{descriptor.ApiVersion}")
        {
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(10.5),
            FontWeight = FontWeights.Medium
        });
        Grid.SetColumn(titleFlow, 1);
        titleRow.Children.Add(titleFlow);
        text.Children.Add(titleRow);
        if (!string.IsNullOrWhiteSpace(descriptor.Description))
        {
            text.Children.Add(new TextBlock
            {
                Text = descriptor.Description,
                Foreground = TrayWeakTextBrush,
                FontSize = AppTypography.Scale(11.5),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, settings.Length > 0 ? 14 : 0, 0)
            });
        }
        text.Children.Add(new TextBlock
        {
            Text = descriptor.Id,
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(10.5),
            Margin = new Thickness(0, 4, settings.Length > 0 ? 14 : 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = descriptor.SourcePath
        });
        if (dataIssue != null)
        {
            text.Children.Add(new TextBlock
            {
                Text = Strings.Get(
                    dataIssue.UsingEmptyState
                        ? "PluginsDataRecoveryPending"
                        : "PluginsDataRecoveryActive"),
                Foreground = Theme.DangerBrush,
                FontSize = AppTypography.Scale(10.5),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, settings.Length > 0 ? 14 : 0, 0),
                ToolTip = string.IsNullOrWhiteSpace(dataIssue.Details)
                    ? dataIssue.ActivePath
                    : $"{dataIssue.ActivePath}{Environment.NewLine}{dataIssue.Details}"
            });
        }
        Grid.SetColumn(text, 0);
        content.Children.Add(text);

        if (settings.Length > 0)
        {
            var settingsPanel = BuildPluginSettingsPanel(descriptor, settings);
            Grid.SetColumn(settingsPanel, 1);
            content.Children.Add(settingsPanel);
        }

        card.Child = content;
        return card;
    }

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
        PaperBodyPluginDescriptor descriptor,
        PaperBodyPluginSettingManifest setting)
    {
        if (setting.Type == "boolean")
        {
            var value = _paperBodyPlugins.DataStore
                .GetSettingValue(descriptor, setting)
                .GetBoolean();
            var toggle = SettingsToggle(
                setting.Name,
                value,
                () => CommitPluginSetting(
                    descriptor,
                    setting,
                    JsonSerializer.SerializeToElement(
                        !_paperBodyPlugins.DataStore
                            .GetSettingValue(descriptor, setting)
                            .GetBoolean())));
            toggle.Margin = new Thickness(0, 4, 0, 0);
            toggle.ToolTip = PluginSettingToolTip(setting);
            return toggle;
        }

        var row = new Grid
        {
            Margin = new Thickness(0, 5, 0, 0),
            ToolTip = PluginSettingToolTip(setting)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = setting.Name,
            Foreground = TrayTextBrush,
            FontSize = AppTypography.Scale(11.5),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0)
        });

        FrameworkElement editor = setting.Type switch
        {
            "string" => BuildPluginStringSetting(descriptor, setting),
            "number" => BuildPluginNumberSetting(descriptor, setting),
            "select" => BuildPluginSelectSetting(descriptor, setting),
            "shortcut" => BuildPluginShortcutSetting(descriptor, setting),
            _ => new TextBlock()
        };
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        return row;
    }

    private FrameworkElement BuildPluginStringSetting(
        PaperBodyPluginDescriptor descriptor,
        PaperBodyPluginSettingManifest setting)
    {
        var current = _paperBodyPlugins.DataStore.GetSettingValue(descriptor, setting);
        var editor = new TextBox
        {
            Text = current.ValueKind == JsonValueKind.String
                ? current.GetString() ?? ""
                : "",
            Width = 125,
            MinHeight = 27,
            MaxLength = setting.MaxLength ?? 0,
            Style = BuildSettingsTextBoxStyle(),
            FontSize = AppTypography.Scale(11.5),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        if (!string.IsNullOrWhiteSpace(setting.Placeholder))
        {
            editor.ToolTip = setting.Placeholder;
        }
        editor.TextChanged += (_, _) => CommitPluginSetting(
            descriptor,
            setting,
            JsonSerializer.SerializeToElement(editor.Text));
        return editor;
    }

    private FrameworkElement BuildPluginNumberSetting(
        PaperBodyPluginDescriptor descriptor,
        PaperBodyPluginSettingManifest setting)
    {
        var current = _paperBodyPlugins.DataStore.GetSettingValue(descriptor, setting);
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        var editor = new TextBox
        {
            Text = current.TryGetDouble(out var number)
                ? number.ToString("G", UiLanguages.EffectiveCulture)
                : "0",
            Width = string.IsNullOrWhiteSpace(setting.Suffix) ? 112 : 86,
            MinHeight = 27,
            Style = BuildSettingsTextBoxStyle(),
            FontSize = AppTypography.Scale(11.5),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        editor.TextChanged += (_, _) =>
        {
            if (TryParsePluginNumber(editor.Text, out var parsed))
            {
                CommitPluginSetting(
                    descriptor,
                    setting,
                    JsonSerializer.SerializeToElement(parsed));
            }
        };
        editor.LostKeyboardFocus += (_, _) =>
        {
            if (!TryParsePluginNumber(editor.Text, out var parsed))
            {
                var stored = _paperBodyPlugins.DataStore.GetSettingValue(descriptor, setting);
                editor.Text = stored.GetDouble().ToString("G", UiLanguages.EffectiveCulture);
                return;
            }

            var normalized = CommitPluginSetting(
                descriptor,
                setting,
                JsonSerializer.SerializeToElement(parsed));
            editor.Text = normalized.GetDouble().ToString("G", UiLanguages.EffectiveCulture);
        };
        panel.Children.Add(editor);
        if (!string.IsNullOrWhiteSpace(setting.Suffix))
        {
            panel.Children.Add(new TextBlock
            {
                Text = setting.Suffix,
                Foreground = TrayWeakTextBrush,
                FontSize = AppTypography.Scale(10.5),
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        return panel;
    }

    private FrameworkElement BuildPluginSelectSetting(
        PaperBodyPluginDescriptor descriptor,
        PaperBodyPluginSettingManifest setting)
    {
        var current = _paperBodyPlugins.DataStore.GetSettingValue(descriptor, setting);
        var selected = current.ValueKind == JsonValueKind.String
            ? current.GetString() ?? ""
            : "";

        string SelectedName() =>
            setting.Options.FirstOrDefault(option =>
                string.Equals(option.Value, selected, StringComparison.Ordinal))?.Name
            ?? selected;

        var valueText = new TextBlock
        {
            Text = SelectedName(),
            Foreground = TrayTextBrush,
            FontSize = AppTypography.Scale(11.5),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var arrow = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0 1 L 4 5 L 8 1"),
            Stroke = TrayWeakTextBrush,
            StrokeThickness = 1.35,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 8,
            Height = 6,
            Margin = new Thickness(10, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            SnapsToDevicePixels = true
        };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(valueText);
        Grid.SetColumn(arrow, 1);
        content.Children.Add(arrow);

        var editor = new Border
        {
            Width = 132,
            MinHeight = 28,
            Padding = new Thickness(9, 2, 8, 2),
            CornerRadius = new CornerRadius(6),
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Focusable = true,
            SnapsToDevicePixels = true,
            Child = content
        };

        // Keep the settings selector on the existing ContextMenu lifecycle. A standalone Popup
        // previously caused the settings window to terminate when its first select was opened.
        var menu = CreateTrayMenu();
        menu.MinWidth = 132;
        menu.MaxWidth = 320;
        menu.MaxHeight = 236;
        menu.PlacementTarget = editor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 4;
        var optionRows = new List<(string Value, string Name, MenuItem Row)>();

        void RefreshOptionRows()
        {
            foreach (var (value, name, row) in optionRows)
            {
                var active = string.Equals(value, selected, StringComparison.Ordinal);
                row.Header = active ? $"✓  {name}" : $"   {name}";
                row.Foreground = active ? Theme.ActiveBrush : TrayTextBrush;
                row.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
                row.Background = active
                    ? Theme.Tint((byte)(Theme.IsDark ? 45 : 28))
                    : Brushes.Transparent;
            }
        }

        foreach (var option in setting.Options)
        {
            var optionRow = new MenuItem
            {
                Header = option.Name,
                Style = SharedTrayMenuItemStyle,
                ToolTip = option.Name
            };
            optionRow.Click += (_, _) =>
            {
                var normalized = CommitPluginSetting(
                    descriptor,
                    setting,
                    JsonSerializer.SerializeToElement(option.Value));
                selected = normalized.ValueKind == JsonValueKind.String
                    ? normalized.GetString() ?? option.Value
                    : option.Value;
                valueText.Text = SelectedName();
                RefreshOptionRows();
                menu.IsOpen = false;
                editor.Focus();
            };
            optionRows.Add((option.Value, option.Name, optionRow));
            menu.Items.Add(optionRow);
        }

        RefreshOptionRows();

        void SetOpenVisual(bool open)
        {
            arrow.RenderTransform = open
                ? new RotateTransform(180)
                : Transform.Identity;
            editor.Background = open ? TrayHoverBrush : Brushes.Transparent;
            editor.BorderBrush = open ? Theme.ActiveBrush : TrayBorderBrush;
        }

        void OpenPopup()
        {
            RefreshOptionRows();
            SetOpenVisual(true);
            menu.PlacementTarget = editor;
            menu.IsOpen = true;
        }

        void SelectByOffset(int offset)
        {
            if (setting.Options.Length == 0)
            {
                return;
            }
            var index = Array.FindIndex(
                setting.Options,
                option => string.Equals(
                    option.Value,
                    selected,
                    StringComparison.Ordinal));
            index = index < 0 ? 0 : index;
            index = (index + offset + setting.Options.Length) %
                setting.Options.Length;
            var option = setting.Options[index];
            var normalized = CommitPluginSetting(
                descriptor,
                setting,
                JsonSerializer.SerializeToElement(option.Value));
            selected = normalized.ValueKind == JsonValueKind.String
                ? normalized.GetString() ?? option.Value
                : option.Value;
            valueText.Text = SelectedName();
            RefreshOptionRows();
        }

        editor.MouseEnter += (_, _) =>
        {
            if (!menu.IsOpen)
            {
                editor.Background = TrayHoverBrush;
            }
        };
        editor.MouseLeave += (_, _) =>
        {
            if (!menu.IsOpen)
            {
                editor.Background = Brushes.Transparent;
            }
        };
        editor.MouseLeftButtonDown += (_, e) =>
        {
            if (menu.IsOpen)
            {
                menu.IsOpen = false;
            }
            else
            {
                OpenPopup();
            }
            e.Handled = true;
        };
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down)
            {
                SelectByOffset(1);
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                SelectByOffset(-1);
                e.Handled = true;
            }
            else if (e.Key is Key.Enter or Key.Space)
            {
                if (menu.IsOpen) menu.IsOpen = false;
                else OpenPopup();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && menu.IsOpen)
            {
                menu.IsOpen = false;
                e.Handled = true;
            }
        };
        menu.Closed += (_, _) => SetOpenVisual(false);
        return editor;
    }

    private JsonElement CommitPluginSetting(
        PaperBodyPluginDescriptor descriptor,
        PaperBodyPluginSettingManifest setting,
        JsonElement value)
    {
        var normalized = _paperBodyPlugins.DataStore.SetSettingValue(
            descriptor,
            setting,
            value);

        if (setting.Type is "string" or "number")
        {
            QueuePluginSettingPropagation(descriptor.Id);
        }
        else
        {
            PropagatePluginSettings(descriptor.Id);
        }
        return normalized;
    }

    private void QueuePluginSettingPropagation(string providerId)
    {
        if (!_pluginSettingPropagationTimers.TryGetValue(providerId, out var timer))
        {
            timer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background,
                Application.Current.Dispatcher)
            {
                Interval = PluginTextSettingPropagationDebounce
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!IsExiting)
                {
                    PropagatePluginSettings(providerId);
                }
            };
            _pluginSettingPropagationTimers.Add(providerId, timer);
        }

        timer.Stop();
        timer.Start();
    }

    private void PropagatePluginSettings(string providerId)
    {
        if (_pluginSettingPropagationTimers.TryGetValue(providerId, out var pendingTimer))
        {
            pendingTimer.Stop();
        }

        if (!_paperBodyPlugins.TryGet(providerId, out var descriptor))
        {
            return;
        }

        var settingsJson = _paperBodyPlugins.DataStore.GetSettingsJson(descriptor);
        RetryFailedPluginAppRuntimeAfterSettingsChanged(providerId);
        foreach (var window in _windows.Values.ToList())
        {
            window.NotifyPaperBodyPluginSettingsChanged(providerId, settingsJson);
        }
    }

    private static bool TryParsePluginNumber(string text, out double value)
    {
        var parsed = double.TryParse(
                text,
                NumberStyles.Float,
                UiLanguages.EffectiveCulture,
                out value) ||
            double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        return parsed && double.IsFinite(value);
    }

    private static object? PluginSettingToolTip(PaperBodyPluginSettingManifest setting) =>
        string.IsNullOrWhiteSpace(setting.Description)
            ? null
            : setting.Description;

    private UIElement BuildPluginIssueCard(PaperBodyPluginLoadIssue issue)
    {
        var label = issue.RestartRequired
            ? $"{issue.Message} · {Strings.Get("PluginsRestartRequired")}"
            : issue.Message;
        return new Border
        {
            BorderBrush = Theme.Danger(72),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = Theme.Danger((byte)(Theme.IsDark ? 20 : 12)),
            Padding = new Thickness(11, 8, 11, 8),
            Margin = new Thickness(0, 5, 0, 3),
            Child = new StackPanel
            {
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children =
                        {
                            CreatePluginStatusDot(PluginPageStatus.Issue),
                            new TextBlock
                            {
                                Text = Path.GetFileName(issue.SourcePath),
                                Foreground = Theme.DangerBrush,
                                FontSize = AppTypography.Scale(12),
                                FontWeight = FontWeights.SemiBold
                            }
                        }
                    },
                    new TextBlock
                    {
                        Text = label,
                        Foreground = TrayWeakTextBrush,
                        FontSize = AppTypography.Scale(11.5),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 3, 0, 0),
                        ToolTip = issue.SourcePath
                    }
                }
            }
        };
    }

    private static string PluginKindText(PaperBodyPluginKind kind) => kind switch
    {
        PaperBodyPluginKind.Native => Strings.Get("PluginsNative"),
        PaperBodyPluginKind.Web => Strings.Get("PluginsWeb"),
        _ => Strings.Get("PluginsBuiltIn")
    };

    private static string PluginVersionText(Version version)
    {
        if (version.Revision > 0)
        {
            return version.ToString(4);
        }
        if (version.Build > 0)
        {
            return version.ToString(3);
        }
        return version.ToString(2);
    }

    private void OpenPluginFolder()
    {
        try
        {
            Directory.CreateDirectory(_paperBodyPlugins.PluginRoot);
            Process.Start(new ProcessStartInfo
            {
                FileName = _paperBodyPlugins.PluginRoot,
                UseShellExecute = true
            });
        }
        catch
        {
            // Settings remains usable if Explorer cannot open the directory.
        }
    }

    private void DisposePaperBodyPlugins()
    {
        foreach (var timer in _pluginSettingPropagationTimers.Values)
        {
            timer.Stop();
        }
        _pluginSettingPropagationTimers.Clear();
        DisposePluginShortcuts();
        DisposePaperPluginHostRuntime();
        _paperBodyPlugins.Dispose();
    }
}
