using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace PaperTodo;

public sealed partial class AppController
{
    private const string AuthorName = "Designed by trigger";
    private const string AuthorGithubUrl = "https://github.com/snownico0722";

    private void SetTheme(string theme)
    {
        State.Theme = theme;
        Theme.Invalidate();
        SaveNow();

        foreach (var window in _windows.Values)
        {
            window.UpdateTheme();
        }
        foreach (var m in _masterCapsules.Values) m.UpdateTheme();

        RebuildTrayMenu();
        RefreshSettingsWindowContent();
    }

    private UIElement CreateThemeSegmentSelector()
    {
        var segments = new[]
        {
            ("system", Strings.Get("ThemeSystem")),
            ("light", Strings.Get("ThemeLight")),
            ("dark", Strings.Get("ThemeDark"))
        };

        return CreateSegmentSelector(segments, State.Theme, SetTheme);
    }

    private void SetColorScheme(string scheme)
    {
        if (!ColorSchemes.IsValid(scheme))
        {
            return;
        }

        State.ColorScheme = scheme;
        Theme.Invalidate();
        SaveNow();

        foreach (var window in _windows.Values)
        {
            window.UpdateTheme();
        }
        foreach (var m in _masterCapsules.Values) m.UpdateTheme();

        RebuildTrayMenu();
        RefreshSettingsWindowContent();
    }

    private UIElement CreateColorSchemeSegmentSelector()
    {
        var segments = new[]
        {
            (ColorSchemes.Warm, Strings.Get("ColorSchemeWarm")),
            (ColorSchemes.Ink, Strings.Get("ColorSchemeInk")),
            (ColorSchemes.Forest, Strings.Get("ColorSchemeForest")),
            (ColorSchemes.Rose, Strings.Get("ColorSchemeRose"))
        };

        return CreateSegmentSelector(segments, ColorSchemes.Normalize(State.ColorScheme), SetColorScheme);
    }

    private void SetMarkdownRenderMode(string mode)
    {
        if (!MarkdownRenderModes.IsValid(mode))
        {
            return;
        }

        State.MarkdownRenderMode = mode;
        SaveNow();

        foreach (var window in _windows.Values)
        {
            window.UpdateMarkdownRenderMode();
        }

        RebuildTrayMenu();
        RefreshSettingsWindowContent();
    }

    private UIElement CreateMarkdownRenderSegmentSelector()
    {
        var segments = new[]
        {
            (MarkdownRenderModes.Off, Strings.Get("MarkdownRenderOff")),
            (MarkdownRenderModes.Basic, Strings.Get("MarkdownRenderBasic")),
            (MarkdownRenderModes.Enhanced, Strings.Get("MarkdownRenderEnhanced"))
        };

        return CreateSegmentSelector(segments, State.MarkdownRenderMode, SetMarkdownRenderMode);
    }

    private void SetFullscreenTopmostMode(string mode)
    {
        var normalized = FullscreenTopmostModes.Normalize(mode);
        if (State.FullscreenTopmostMode == normalized)
        {
            return;
        }

        State.FullscreenTopmostMode = normalized;
        RefreshTopmostForForegroundWindow();
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private UIElement CreateFullscreenTopmostModeSegmentSelector()
    {
        var segments = new[]
        {
            (FullscreenTopmostModes.Avoid, Strings.Get("FullscreenTopmostModeAvoid")),
            (FullscreenTopmostModes.StayOnTop, Strings.Get("FullscreenTopmostModeStayOnTop"))
        };

        return CreateSegmentSelector(segments, FullscreenTopmostModes.Normalize(State.FullscreenTopmostMode), SetFullscreenTopmostMode);
    }

    private void SetTodoVisualSize(string size)
    {
        var normalized = TodoVisualSizes.Normalize(size);
        if (State.TodoVisualSize == normalized)
        {
            return;
        }

        State.TodoVisualSize = normalized;
        SaveNow();

        foreach (var window in _windows.Values)
        {
            window.UpdateTodoVisualSize();
        }

        RefreshSettingsWindowContent();
    }

    private UIElement CreateTodoVisualSizeSegmentSelector()
    {
        var segments = new[]
        {
            (TodoVisualSizes.Small, Strings.Get("TodoVisualSizeSmall")),
            (TodoVisualSizes.Medium, Strings.Get("TodoVisualSizeMedium")),
            (TodoVisualSizes.Large, Strings.Get("TodoVisualSizeLarge")),
            (TodoVisualSizes.ExtraLarge, Strings.Get("TodoVisualSizeExtraLarge"))
        };

        return CreateSegmentSelector(segments, TodoVisualSizes.Normalize(State.TodoVisualSize), SetTodoVisualSize);
    }

    private UIElement CreateExternalMarkdownExtensionEditor()
    {
        var textBox = new TextBox
        {
            Text = ExternalMarkdownFileExtensions.Normalize(State.ExternalMarkdownExtension),
            Foreground = TrayTextBrush,
            CaretBrush = TrayTextBrush,
            Background = Brushes.Transparent,
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 4, 0, 8),
            FontSize = 13,
            Height = 28,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = BuildSettingsTextBoxStyle()
        };

        _settingsExternalMarkdownTextBox = textBox;
        textBox.GotKeyboardFocus += (_, _) => textBox.SelectAll();
        textBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitExternalMarkdownExtension(textBox);
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                textBox.Text = ExternalMarkdownFileExtensions.Normalize(State.ExternalMarkdownExtension);
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        };
        textBox.LostKeyboardFocus += (_, _) => CommitExternalMarkdownExtension(textBox);

        return textBox;
    }

    private void CommitSettingsExternalMarkdownEditor()
    {
        if (_settingsExternalMarkdownTextBox != null)
        {
            CommitExternalMarkdownExtension(_settingsExternalMarkdownTextBox);
        }
    }

    private void CommitExternalMarkdownExtension(TextBox textBox)
    {
        var normalized = ExternalMarkdownFileExtensions.Normalize(textBox.Text);
        if (textBox.Text != normalized)
        {
            textBox.Text = normalized;
            textBox.CaretIndex = textBox.Text.Length;
        }

        SetExternalMarkdownExtension(normalized);
    }

    private void SetExternalMarkdownExtension(string extension)
    {
        var normalized = ExternalMarkdownFileExtensions.Normalize(extension);
        if (State.ExternalMarkdownExtension == normalized)
        {
            return;
        }

        State.ExternalMarkdownExtension = normalized;
        SaveNow();

        foreach (var window in _windows.Values)
        {
            window.UpdateExternalMarkdownExtension();
        }
    }

    private UIElement CreateSegmentSelector((string Key, string Label)[] segments, string activeKey, Action<string> onSelect)
    {
        var container = new Border
        {
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 4, 0, 10),
            Height = 26,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var grid = new Grid();
        for (var i = 0; i < segments.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (int i = 0; i < segments.Length; i++)
        {
            var key = segments[i].Key;
            var label = segments[i].Label;
            var isActive = activeKey == key;

            var segmentBorder = new Border
            {
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(1),
                Background = isActive ? Theme.ActiveBrush : Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var textBlock = new TextBlock
            {
                Text = label,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isActive ? TrayPaperBrush : TrayTextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            segmentBorder.Child = textBlock;

            if (!isActive)
            {
                segmentBorder.MouseEnter += (_, _) =>
                {
                    segmentBorder.Background = TrayHoverBrush;
                };
                segmentBorder.MouseLeave += (_, _) =>
                {
                    segmentBorder.Background = Brushes.Transparent;
                };
            }

            segmentBorder.MouseLeftButtonDown += (_, _) =>
            {
                if (activeKey == key)
                {
                    return;
                }

                onSelect(key);
            };

            Grid.SetColumn(segmentBorder, i);
            grid.Children.Add(segmentBorder);
        }

        container.Child = grid;
        return container;
    }

    private UIElement CreateMaxTitleLengthStepper()
    {
        var container = new Border
        {
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 4, 0, 10),
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var valueText = new TextBlock
        {
            Text = State.MaxTitleLength.ToString(CultureInfo.InvariantCulture),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TrayTextBrush
        };
        Grid.SetColumn(valueText, 1);

        Border StepButton(string glyph, int column, Action onClick)
        {
            var glyphText = new TextBlock
            {
                Text = glyph,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 15,
                Foreground = TrayTextBrush
            };
            var button = new Border
            {
                Width = 34,
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = glyphText
            };
            button.MouseEnter += (_, _) => button.Background = TrayHoverBrush;
            button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
            button.MouseLeftButtonDown += (_, e) =>
            {
                onClick();
                valueText.Text = State.MaxTitleLength.ToString(CultureInfo.InvariantCulture);
                e.Handled = true;
            };
            Grid.SetColumn(button, column);
            return button;
        }

        grid.Children.Add(StepButton("−", 0, () => SetMaxTitleLength(State.MaxTitleLength - 1)));
        grid.Children.Add(valueText);
        grid.Children.Add(StepButton("＋", 2, () => SetMaxTitleLength(State.MaxTitleLength + 1)));

        container.Child = grid;
        return container;
    }

    private void SetMaxTitleLength(int value)
    {
        var normalized = PaperTitles.NormalizeMaxTitleLength(value);
        if (State.MaxTitleLength == normalized)
        {
            return;
        }

        State.MaxTitleLength = normalized;

        // Re-clamp existing custom titles to the new limit and refresh everything that shows them.
        foreach (var paper in State.Papers)
        {
            paper.Title = PaperTitles.CleanCustomTitle(paper.Title, normalized);
        }

        foreach (var window in _windows.Values)
        {
            window.RefreshPaperTitle();
        }

        ArrangeDeepCapsules(animate: true);
        SaveNow();
        RebuildTrayMenu();
        RefreshSettingsWindowContent();
    }

    private void ShowSettingsWindow()
    {
        if (_trayMenu != null)
        {
            _trayMenu.IsOpen = false;
        }

        if (_settingsWindow != null)
        {
            RefreshSettingsWindowContent();
            _settingsWindow.Show();
            _settingsWindow.Activate();
            return;
        }

        var window = new Window
        {
            Title = Strings.Get("TraySettings"),
            Width = SettingsWindowWidth(),
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            FontFamily = new FontFamily("Segoe UI"),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        window.PreviewMouseDown += (_, e) =>
        {
            if (_settingsExternalMarkdownTextBox is not { IsKeyboardFocusWithin: true } textBox ||
                IsWithinElement(e.OriginalSource as DependencyObject, textBox))
            {
                return;
            }

            CommitExternalMarkdownExtension(textBox);
            Keyboard.ClearFocus();
        };
        window.Deactivated += (_, _) => CommitSettingsExternalMarkdownEditor();
        window.Closed += (_, _) =>
        {
            CommitSettingsExternalMarkdownEditor();
            _settingsExternalMarkdownTextBox = null;
            _settingsCapsuleModeCheckBox = null;
            _settingsDeepCapsuleModeCheckBox = null;
            _settingsDeepCapsuleExpandedSlotCheckBox = null;
            _settingsCapsuleCollapseAllCheckBox = null;
            _settingsWindow = null;
        };
        _settingsWindow = window;
        RefreshSettingsWindowContent();
        window.Show();
        CenterSettingsWindow(window);
        window.Activate();
        window.Dispatcher.BeginInvoke(() => CenterSettingsWindow(window), DispatcherPriority.Loaded);
    }

    private void RefreshSettingsWindowContent()
    {
        if (_settingsWindow == null)
        {
            return;
        }

        _settingsWindow.Content = BuildSettingsWindowContent(_settingsWindow);
        ApplyToolTipSetting(_settingsWindow);
    }

    private UIElement BuildSettingsWindowContent(Window window)
    {
        var root = new DockPanel
        {
            Width = SettingsContentWidth(),
            LastChildFill = true
        };

        var titleRow = new Grid
        {
            Margin = new Thickness(0, 0, 0, 10),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.SizeAll
        };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try { window.DragMove(); } catch { }
            }
        };

        var title = new TextBlock
        {
            Text = Strings.Get("TraySettings"),
            Foreground = TrayTextBrush,
            FontSize = 15,
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
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = TrayWeakTextBrush,
            FontSize = 16,
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false
        };
        closeButton.Click += (_, _) => window.Close();
        Grid.SetColumn(closeButton, 1);
        titleRow.Children.Add(closeButton);

        DockPanel.SetDock(titleRow, Dock.Top);
        root.Children.Add(titleRow);

        var columns = new Grid
        {
            Margin = new Thickness(0, 0, 4, 0)
        };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var leftColumn = new StackPanel
        {
            Margin = new Thickness(0, 0, 14, 0)
        };
        var rightColumn = new StackPanel
        {
            Margin = new Thickness(14, 0, 0, 0)
        };

        leftColumn.Children.Add(SettingsSectionLabel(Strings.Get("SettingsDisplay")));
        leftColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("TrayThemeMode")), "TipThemeMode"));
        leftColumn.Children.Add(CreateThemeSegmentSelector());
        leftColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsColorScheme")), "TipColorScheme"));
        leftColumn.Children.Add(CreateColorSchemeSegmentSelector());
        leftColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("TrayMarkdownRenderMode")), "TipMarkdownRender"));
        leftColumn.Children.Add(CreateMarkdownRenderSegmentSelector());
        leftColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsFullscreenTopmostMode")), "TipFullscreenTopmostMode"));
        leftColumn.Children.Add(CreateFullscreenTopmostModeSegmentSelector());
        leftColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsTodoVisualSize")), "TipTodoVisualSize"));
        leftColumn.Children.Add(CreateTodoVisualSizeSegmentSelector());

        leftColumn.Children.Add(SettingsSectionLabel(Strings.Get("SettingsTopBarButtons")));
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsShowTopBarNewTodoButton"), State.ShowTopBarNewTodoButton, ToggleTopBarNewTodoButton), "TipNewTodoButton"));
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsShowTopBarNewNoteButton"), State.ShowTopBarNewNoteButton, ToggleTopBarNewNoteButton), "TipNewNoteButton"));
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsShowTopBarExternalOpenButton"), State.ShowTopBarExternalOpenButton, ToggleTopBarExternalOpenButton), "TipExternalOpenButton"));

        leftColumn.Children.Add(SettingsSectionLabel(Strings.Get("SettingsGeneral")));
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("TrayStartup"), SystemSettingsHelper.IsStartupEnabled(), ToggleStartup), "TipStartup"));
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsEnableToolTips"), State.EnableToolTips, ToggleToolTips), "TipEnableToolTips"));
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsEnableAnimations"), State.EnableAnimations, ToggleAnimations), "TipEnableAnimations"));

        rightColumn.Children.Add(SettingsSectionLabel(Strings.Get("SettingsTodoNote")));
        rightColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsEnableTodoNoteLinks"), State.EnableTodoNoteLinks, ToggleTodoNoteLinks), "TipEnableTodoNoteLinks"));
        var showLinkedNoteNameToggle = SettingsToggle(Strings.Get("SettingsShowLinkedNoteName"), State.ShowLinkedNoteName, ToggleLinkedNoteNameDisplay);
        showLinkedNoteNameToggle.IsEnabled = State.EnableTodoNoteLinks;
        rightColumn.Children.Add(WrapWithHint(showLinkedNoteNameToggle, "TipShowLinkedNoteName"));
        var hideLinkedNotesFromCapsulesToggle = SettingsToggle(Strings.Get("SettingsHideLinkedNotesFromCapsules"), State.HideLinkedNotesFromCapsules, ToggleHideLinkedNotesFromCapsules);
        hideLinkedNotesFromCapsulesToggle.IsEnabled = State.EnableTodoNoteLinks;
        rightColumn.Children.Add(WrapWithHint(hideLinkedNotesFromCapsulesToggle, "TipHideLinkedNotesFromCapsules"));
        var runLinkedScriptCapsulesToggle = SettingsToggle(Strings.Get("SettingsRunLinkedScriptCapsulesOnClick"), State.RunLinkedScriptCapsulesOnClick, ToggleRunLinkedScriptCapsulesOnClick);
        runLinkedScriptCapsulesToggle.IsEnabled = State.EnableTodoNoteLinks;
        rightColumn.Children.Add(WrapWithHint(runLinkedScriptCapsulesToggle, "TipRunLinkedScriptCapsulesOnClick"));

        rightColumn.Children.Add(SettingsSectionLabel(Strings.Get("SettingsExternalOpen")));
        rightColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsExternalMarkdownExtension")), "TipExternalExtension"));
        rightColumn.Children.Add(CreateExternalMarkdownExtensionEditor());

        rightColumn.Children.Add(SettingsSectionLabel(Strings.Get("SettingsScriptCapsule")));
        rightColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsPersistentPowerShellProcess"), State.UsePersistentPowerShellProcess, TogglePersistentPowerShellProcess), "TipPersistentPowerShellProcess"));
        rightColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsPreferPowerShell7"), State.PreferPowerShell7, TogglePreferPowerShell7), "TipPreferPowerShell7"));
        rightColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsHideScriptRunWindow"), State.HideScriptRunWindow, ToggleHideScriptRunWindow), "TipHideScriptRunWindow"));

        rightColumn.Children.Add(SettingsSectionLabel(Strings.Get("SettingsCapsule")));
        _settingsCapsuleModeCheckBox = SettingsToggle(Strings.Get("TrayCapsuleMode"), State.UseCapsuleMode, ToggleCapsuleMode);
        _settingsDeepCapsuleModeCheckBox = SettingsToggle(Strings.Get("TrayDeepCapsuleMode"), State.UseDeepCapsuleMode, ToggleDeepCapsuleMode);
        _settingsDeepCapsuleExpandedSlotCheckBox = SettingsToggle(Strings.Get("SettingsShowDeepCapsuleWhileExpanded"), State.ShowDeepCapsuleWhileExpanded, ToggleDeepCapsuleExpandedSlot);
        _settingsCapsuleCollapseAllCheckBox = SettingsToggle(Strings.Get("SettingsCapsuleCollapseAll"), State.UseCapsuleCollapseAll, ToggleCapsuleCollapseAll);
        rightColumn.Children.Add(WrapWithHint(_settingsCapsuleModeCheckBox, "TipCapsuleMode"));
        rightColumn.Children.Add(WrapWithHint(_settingsDeepCapsuleModeCheckBox, "TipDeepCapsuleMode"));
        rightColumn.Children.Add(WrapWithHint(_settingsDeepCapsuleExpandedSlotCheckBox, "TipShowDeepCapsuleWhileExpanded"));
        rightColumn.Children.Add(WrapWithHint(_settingsCapsuleCollapseAllCheckBox, "TipCapsuleCollapseAll"));
        RefreshSettingsCapsuleToggleStates();
        rightColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsMaxTitleLength"), topMargin: 8), "TipMaxTitleLength"));
        rightColumn.Children.Add(CreateMaxTitleLengthStepper());

        var separator = new Border
        {
            Width = 1,
            Margin = new Thickness(0, 10, 0, 4),
            Background = TrayBorderBrush,
            Opacity = 0.65
        };

        Grid.SetColumn(leftColumn, 0);
        Grid.SetColumn(separator, 1);
        Grid.SetColumn(rightColumn, 2);
        columns.Children.Add(leftColumn);
        columns.Children.Add(separator);
        columns.Children.Add(rightColumn);

        var scrollViewer = new ScrollViewer
        {
            Content = columns,
            MaxHeight = SettingsOptionsMaxHeight(),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            PanningMode = PanningMode.VerticalOnly
        };

        var footer = BuildSettingsFooter();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        root.Children.Add(scrollViewer);

        return new Border
        {
            Background = TrayPaperBrush,
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            MaxHeight = SettingsWindowMaxHeight(),
            Padding = new Thickness(14, 12, 14, 14),
            Child = root
        };
    }

    private UIElement BuildSettingsFooter()
    {
        var signatureText = new TextBlock
        {
            Text = AuthorName,
            Foreground = TrayWeakTextBrush,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center
        };

        var signature = new Border
        {
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 2, 0),
            Padding = new Thickness(6, 2, 0, 0),
            Child = signatureText,
            ToolTip = AuthorGithubUrl
        };
        ToolTipService.SetInitialShowDelay(signature, 300);
        ToolTipService.SetShowDuration(signature, 12000);
        signature.MouseEnter += (_, _) => signatureText.Foreground = TrayTextBrush;
        signature.MouseLeave += (_, _) => signatureText.Foreground = TrayWeakTextBrush;
        signature.MouseLeftButtonUp += (_, e) =>
        {
            OpenAuthorGithub();
            e.Handled = true;
        };

        return signature;
    }

    private static void OpenAuthorGithub()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AuthorGithubUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // Opening an external browser should not affect settings interaction.
        }
    }

    private static double SettingsWindowWidth()
    {
        return SettingsContentWidth() + 32;
    }

    private static double SettingsContentWidth()
    {
        return Math.Clamp(SystemParameters.WorkArea.Width - 96, 560, 680);
    }

    private static double SettingsWindowMaxHeight()
    {
        return Math.Max(260, SystemParameters.WorkArea.Height - 48);
    }

    private static double SettingsOptionsMaxHeight()
    {
        const double verticalPadding = 26;
        const double titleRowHeight = 34;
        const double footerHeight = 24;
        return Math.Max(180, SettingsWindowMaxHeight() - verticalPadding - titleRowHeight - footerHeight);
    }

    private static TextBlock SettingsSectionLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = TrayWeakTextBrush,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 3)
        };
    }

    private static TextBlock SettingsFieldLabel(string text, double topMargin = 0)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = TrayWeakTextBrush,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, topMargin, 0, 0)
        };
    }

    private CheckBox SettingsToggle(string text, bool isChecked, Action onToggle)
    {
        var checkBox = new CheckBox
        {
            Content = text,
            IsChecked = isChecked,
            Foreground = TrayTextBrush,
            FontSize = 13,
            Margin = new Thickness(0, 4, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
            Style = BuildSettingsCheckBoxStyle()
        };

        checkBox.Click += (_, _) => onToggle();
        return checkBox;
    }

    // Lays the option out as: [option .....stretch.....] [ⓘ]. The trailing ⓘ shows a themed
    // tooltip with the detailed explanation on hover, so every row stays short while the full
    // description is one hover away. tipKey is a Strings resource key.
    private UIElement WrapWithHint(FrameworkElement option, string tipKey)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The option keeps its own top margin via its style; reset it here so the row controls spacing.
        option.Margin = new Thickness(0);
        option.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(option, 0);
        grid.Children.Add(option);

        var hintGlyph = new TextBlock
        {
            Text = "ⓘ",
            Foreground = TrayWeakTextBrush,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var hint = new Border
        {
            Width = 18,
            Height = 18,
            Margin = new Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Help,
            Child = hintGlyph,
            ToolTip = BuildSettingsHintTooltip(Strings.Get(tipKey))
        };
        ToolTipPreferences.SetAlwaysEnabled(hint, true);
        ToolTipService.SetInitialShowDelay(hint, 200);
        ToolTipService.SetShowDuration(hint, 20000);
        ToolTipService.SetBetweenShowDelay(hint, 0);
        hint.MouseEnter += (_, _) => hintGlyph.Foreground = TrayTextBrush;
        hint.MouseLeave += (_, _) => hintGlyph.Foreground = TrayWeakTextBrush;
        Grid.SetColumn(hint, 1);
        grid.Children.Add(hint);

        return grid;
    }

    private ToolTip BuildSettingsHintTooltip(string text)
    {
        return new ToolTip
        {
            Content = new TextBlock
            {
                Text = text,
                Foreground = TrayTextBrush,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 240
            },
            Background = TrayPaperBrush,
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7),
            HasDropShadow = true
        };
    }

    private void RefreshSettingsCapsuleToggleStates()
    {
        if (_settingsCapsuleModeCheckBox != null)
        {
            _settingsCapsuleModeCheckBox.IsChecked = State.UseCapsuleMode;
        }
        if (_settingsDeepCapsuleModeCheckBox != null)
        {
            _settingsDeepCapsuleModeCheckBox.IsChecked = State.UseDeepCapsuleMode;
            _settingsDeepCapsuleModeCheckBox.IsEnabled = State.UseCapsuleMode;
        }
        if (_settingsDeepCapsuleExpandedSlotCheckBox != null)
        {
            _settingsDeepCapsuleExpandedSlotCheckBox.IsChecked = State.ShowDeepCapsuleWhileExpanded;
            _settingsDeepCapsuleExpandedSlotCheckBox.IsEnabled = State.UseCapsuleMode && State.UseDeepCapsuleMode;
        }
        if (_settingsCapsuleCollapseAllCheckBox != null)
        {
            _settingsCapsuleCollapseAllCheckBox.IsChecked = State.UseCapsuleCollapseAll;
            _settingsCapsuleCollapseAllCheckBox.IsEnabled = State.UseCapsuleMode && State.UseDeepCapsuleMode;
        }
    }

    private Style BuildSettingsTextBoxStyle()
    {
        var style = new Style(typeof(TextBox));
        style.Setters.Add(new Setter(Control.ForegroundProperty, TrayTextBrush));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, TrayBorderBrush));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
        style.Setters.Add(new Setter(FrameworkElement.UseLayoutRoundingProperty, true));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var contentHost = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        contentHost.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        contentHost.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        border.AppendChild(contentHost);

        var template = new ControlTemplate(typeof(TextBox))
        {
            VisualTree = border
        };

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, TrayWeakTextBrush, "Bd"));

        var focusTrigger = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focusTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.ActiveBrush, "Bd"));

        var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55));

        template.Triggers.Add(hoverTrigger);
        template.Triggers.Add(focusTrigger);
        template.Triggers.Add(disabledTrigger);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static bool IsWithinElement(DependencyObject? current, DependencyObject ancestor)
    {
        while (current != null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = GetElementParent(current);
        }

        return false;
    }

    private static DependencyObject? GetElementParent(DependencyObject current)
    {
        if (current is FrameworkElement fe && fe.Parent is DependencyObject parent)
        {
            return parent;
        }

        if (current is FrameworkContentElement fce && fce.Parent is DependencyObject contentParent)
        {
            return contentParent;
        }

        try
        {
            return VisualTreeHelper.GetParent(current);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private Style BuildSettingsCheckBoxStyle()
    {
        var style = new Style(typeof(CheckBox));
        style.Setters.Add(new Setter(Control.ForegroundProperty, TrayTextBrush));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Hand));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        style.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
        style.Setters.Add(new Setter(FrameworkElement.UseLayoutRoundingProperty, true));

        var root = new FrameworkElementFactory(typeof(StackPanel));
        root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        root.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var markHost = new FrameworkElementFactory(typeof(Grid));
        markHost.SetValue(FrameworkElement.WidthProperty, 16.0);
        markHost.SetValue(FrameworkElement.HeightProperty, 16.0);
        markHost.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        markHost.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "CheckBorder";
        border.SetValue(FrameworkElement.WidthProperty, 16.0);
        border.SetValue(FrameworkElement.HeightProperty, 16.0);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BorderBrushProperty, TrayBorderBrush);
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        markHost.AppendChild(border);

        var path = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        path.Name = "CheckMark";
        path.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 4,8.1 L 7,11 L 12,5"));
        path.SetValue(System.Windows.Shapes.Path.StrokeProperty, TrayPaperBrush);
        path.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 2.0);
        path.SetValue(System.Windows.Shapes.Path.StrokeStartLineCapProperty, PenLineCap.Round);
        path.SetValue(System.Windows.Shapes.Path.StrokeEndLineCapProperty, PenLineCap.Round);
        path.SetValue(System.Windows.Shapes.Path.StrokeLineJoinProperty, PenLineJoin.Round);
        path.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        path.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        path.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        markHost.AppendChild(path);

        root.AppendChild(markHost);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        root.AppendChild(content);

        var template = new ControlTemplate(typeof(CheckBox))
        {
            VisualTree = root
        };

        var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, Theme.ActiveBrush, "CheckBorder"));
        checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.Transparent, "CheckBorder"));
        checkedTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CheckMark"));

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, TrayHoverBrush, "CheckBorder"));

        var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55));

        template.Triggers.Add(hoverTrigger);
        template.Triggers.Add(checkedTrigger);
        template.Triggers.Add(disabledTrigger);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static void CenterSettingsWindow(Window? window)
    {
        if (window == null)
        {
            return;
        }

        var area = SystemParameters.WorkArea;
        var width = window.ActualWidth > 1 ? window.ActualWidth : window.Width;
        var height = window.ActualHeight > 1 ? window.ActualHeight : 280;
        var minLeft = area.Left + 16;
        var minTop = area.Top + 16;
        var maxLeft = area.Right - width - 16;
        var maxTop = area.Bottom - height - 16;
        var centeredLeft = area.Left + (area.Width - width) / 2;
        var centeredTop = area.Top + (area.Height - height) / 2;

        window.Left = ClampWindowCoordinate(centeredLeft, minLeft, maxLeft);
        window.Top = ClampWindowCoordinate(centeredTop, minTop, maxTop);
    }

    private static double ClampWindowCoordinate(double value, double min, double max)
    {
        return max < min ? min : Math.Clamp(value, min, max);
    }


    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
        {
            if (State.Theme == "system")
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    Theme.Invalidate();
                    foreach (var window in _windows.Values)
                    {
                        window.UpdateTheme();
                    }
                    foreach (var m in _masterCapsules.Values) m.UpdateTheme();
                    RebuildTrayMenu();
                    RefreshSettingsWindowContent();
                }));
            }
        }
    }

    private void ToggleStartup()
    {
        var enabled = SystemSettingsHelper.IsStartupEnabled();
        if (!SystemSettingsHelper.ToggleStartup(!enabled))
        {
            _trayIcon?.ShowBalloonTip(
                Strings.Get("StartupFailureTitle"),
                Strings.Get("StartupFailureMessage"),
                BalloonIcon.Warning);
        }
        RebuildTrayMenu();
        RefreshSettingsWindowContent();
    }

    private void ToggleAnimations()
    {
        State.EnableAnimations = !State.EnableAnimations;
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void TogglePersistentPowerShellProcess()
    {
        State.UsePersistentPowerShellProcess = !State.UsePersistentPowerShellProcess;
        if (!State.UsePersistentPowerShellProcess)
        {
            PaperWindow.StopPersistentScriptProcesses();
        }
        else
        {
            PaperWindow.EnsurePersistentScriptProcessForSettings(State);
        }

        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void TogglePreferPowerShell7()
    {
        State.PreferPowerShell7 = !State.PreferPowerShell7;
        PaperWindow.StopPersistentScriptProcesses();
        PaperWindow.EnsurePersistentScriptProcessForSettings(State);
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleHideScriptRunWindow()
    {
        State.HideScriptRunWindow = !State.HideScriptRunWindow;
        PaperWindow.StopPersistentScriptProcesses();
        PaperWindow.EnsurePersistentScriptProcessForSettings(State);
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleToolTips()
    {
        State.EnableToolTips = !State.EnableToolTips;
        SaveNow();
        RefreshToolTipSetting();
        RefreshSettingsWindowContent();
    }

    private void RefreshToolTipSetting()
    {
        foreach (var window in _windows.Values)
        {
            window.UpdateToolTipSetting();
        }

        foreach (var m in _masterCapsules.Values) m.UpdateToolTipSetting();

        if (_settingsWindow != null)
        {
            ApplyToolTipSetting(_settingsWindow);
        }
    }

    private void ApplyToolTipSetting(Window window)
    {
        ToolTipPreferences.Apply(window, State.EnableToolTips);
    }

    private void ToggleCapsuleMode()
    {
        State.UseCapsuleMode = !State.UseCapsuleMode;

        if (!State.UseCapsuleMode)
        {
            State.UseDeepCapsuleMode = false;
            State.UseCapsuleCollapseAll = false;
            State.CapsuleCollapseAllActive = false;
            State.CapsuleCollapseAllActiveQueues.Clear();
            ResetDeepCapsuleStartTopMargins();
            foreach (var paper in State.Papers)
            {
                paper.IsCollapsed = false;
            }
        }

        foreach (var window in _windows.Values)
        {
            window.UpdateCapsuleMode();
        }

        ArrangeDeepCapsules();
        RestoreMissingVisiblePaperSurfaces();
        SaveNow();
        RebuildTrayMenu();
        RefreshSettingsCapsuleToggleStates();
    }

    private void ToggleTopBarNewTodoButton()
    {
        State.ShowTopBarNewTodoButton = !State.ShowTopBarNewTodoButton;
        RefreshTopBarNewPaperButtonsSetting();
    }

    private void ToggleTopBarNewNoteButton()
    {
        State.ShowTopBarNewNoteButton = !State.ShowTopBarNewNoteButton;
        RefreshTopBarNewPaperButtonsSetting();
    }

    private void ToggleTopBarExternalOpenButton()
    {
        State.ShowTopBarExternalOpenButton = !State.ShowTopBarExternalOpenButton;
        RefreshTopBarNewPaperButtonsSetting();
    }

    private void ToggleLinkedNoteNameDisplay()
    {
        State.ShowLinkedNoteName = !State.ShowLinkedNoteName;

        foreach (var window in _windows.Values)
        {
            window.RefreshTodoRowsForExternalChange();
        }

        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleHideLinkedNotesFromCapsules()
    {
        State.HideLinkedNotesFromCapsules = !State.HideLinkedNotesFromCapsules;
        RefreshCapsuleEligibilityForLinkedNotes();
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleRunLinkedScriptCapsulesOnClick()
    {
        State.RunLinkedScriptCapsulesOnClick = !State.RunLinkedScriptCapsulesOnClick;

        foreach (var window in _windows.Values)
        {
            window.RefreshTodoRowsForExternalChange();
        }

        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleTodoNoteLinks()
    {
        State.EnableTodoNoteLinks = !State.EnableTodoNoteLinks;
        ClearNoteLinkDropTarget();

        foreach (var window in _windows.Values)
        {
            window.UpdateTodoLinkFeature();
        }

        RefreshCapsuleEligibilityForLinkedNotes();
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void RefreshTopBarNewPaperButtonsSetting()
    {
        foreach (var window in _windows.Values)
        {
            window.UpdateTopBarNewPaperButtons();
        }

        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleDeepCapsuleMode()
    {
        State.UseDeepCapsuleMode = !State.UseDeepCapsuleMode;

        if (State.UseDeepCapsuleMode && !State.UseCapsuleMode)
        {
            State.UseCapsuleMode = true;
            foreach (var window in _windows.Values)
            {
                window.UpdateCapsuleMode();
            }
        }
        else if (!State.UseDeepCapsuleMode)
        {
            State.UseCapsuleCollapseAll = false;
            State.CapsuleCollapseAllActive = false;
            State.CapsuleCollapseAllActiveQueues.Clear();
            ResetDeepCapsuleStartTopMargins();
        }

        foreach (var window in _windows.Values)
        {
            window.UpdateDeepCapsuleMode();
        }

        ArrangeDeepCapsules();
        RestoreMissingVisiblePaperSurfaces();
        SaveNow();
        RebuildTrayMenu();
        RefreshSettingsCapsuleToggleStates();
    }

    private void ToggleDeepCapsuleExpandedSlot()
    {
        State.ShowDeepCapsuleWhileExpanded = !State.ShowDeepCapsuleWhileExpanded;

        foreach (var window in _windows.Values)
        {
            window.UpdateDeepCapsuleExpandedSlotMode();
        }

        ArrangeDeepCapsules(animate: State.EnableAnimations);
        SaveNow();
        RefreshSettingsCapsuleToggleStates();
    }

    private void RestoreMissingVisiblePaperSurfaces()
    {
        foreach (var paper in State.Papers.ToList())
        {
            if (!paper.IsVisible ||
                !_windows.TryGetValue(paper.Id, out var window) ||
                window.HasVisibleSurface)
            {
                continue;
            }

            RestoreExistingPaperWindowSurface(paper, window);
        }
    }

    private void RestoreExistingPaperWindowSurface(PaperData paper, PaperWindow window)
    {
        RescuePaperIfOffScreen(paper, State.Papers.IndexOf(paper));
        window.CancelPendingVisibilityTransitions();
        window.DetachFromDeepCapsuleStack(animate: false);

        window.Left = paper.X;
        window.Top = paper.Y;
        if (paper.IsCollapsed && State.UseCapsuleMode)
        {
            window.Width = window.DesiredCapsuleWindowWidth;
            window.Height = PaperLayoutDefaults.CapsuleHeight;
        }
        else
        {
            window.Width = paper.Width;
            window.Height = paper.Height;
        }

        window.Opacity = 1.0;
        if (!window.IsVisible)
        {
            window.Show();
        }
        window.RefreshEffectiveTopmost();
    }

}
