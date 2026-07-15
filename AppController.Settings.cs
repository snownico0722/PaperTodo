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
        SaveNow();
        RefreshThemeSurfaces();
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
        SaveNow();
        RefreshThemeSurfaces();
    }

    private void RefreshThemeSurfaces()
    {
        Theme.Invalidate();
        foreach (var window in _windows.Values)
        {
            window.UpdateTheme();
        }
        foreach (var m in _masterCapsules.Values) m.UpdateTheme();

        RebuildTrayMenu();
        RefreshSettingsWindowContent();
    }

    private void InvalidateSystemThemeCacheIfNeeded()
    {
        if (State.Theme == "system")
        {
            Theme.Invalidate();
        }
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

    private void SetUiFontPreset(string preset)
    {
        var normalized = UiFontPresets.Normalize(preset);
        if (State.UiFontPreset == normalized)
        {
            return;
        }

        State.UiFontPreset = normalized;
        ApplyTypographySettingsChange();
    }

    private UIElement CreateUiFontPresetSegmentSelector()
    {
        var segments = new[]
        {
            (UiFontPresets.Default, Strings.Get("UiFontDefault")),
            (UiFontPresets.YaHei, Strings.Get("UiFontYaHei")),
            (UiFontPresets.DengXian, Strings.Get("UiFontDengXian"))
        };

        return CreateSegmentSelector(segments, UiFontPresets.Normalize(State.UiFontPreset), SetUiFontPreset);
    }

    private void SetOverallFontScale(double scale)
    {
        var normalized = OverallFontScales.Normalize(scale);
        if (Math.Abs(State.Zoom - normalized) < 0.001)
        {
            return;
        }

        State.Zoom = normalized;
        ApplyTypographySettingsChange();
    }

    private void SetNoteTextSize(string size)
    {
        var normalized = VisualTextSizes.Normalize(size);
        if (State.NoteTextSize == normalized)
        {
            return;
        }

        State.NoteTextSize = normalized;
        ApplyTypographySettingsChange();
    }

    private void ToggleNoteTextBold()
    {
        State.NoteTextBold = !State.NoteTextBold;
        ApplyTypographySettingsChange();
    }

    private void ToggleTodoTextBold()
    {
        State.TodoTextBold = !State.TodoTextBold;
        ApplyTypographySettingsChange();
    }

    private void SetTitleTextSize(string size)
    {
        var normalized = VisualTextSizes.Normalize(size);
        if (State.TitleTextSize == normalized)
        {
            return;
        }

        State.TitleTextSize = normalized;
        ApplyTypographySettingsChange();
    }

    private void ToggleTitleTextBold()
    {
        State.TitleTextBold = !State.TitleTextBold;
        ApplyTypographySettingsChange();
    }

    private void SetCapsuleTextSize(string size)
    {
        var normalized = VisualTextSizes.Normalize(size);
        if (State.CapsuleTextSize == normalized)
        {
            return;
        }

        State.CapsuleTextSize = normalized;
        ApplyTypographySettingsChange();
    }

    private void ToggleCapsuleTextBold()
    {
        State.CapsuleTextBold = !State.CapsuleTextBold;
        ApplyTypographySettingsChange();
    }

    private void ApplyTypographySettingsChange()
    {
        AppTypography.Configure(State.UiFontPreset, State.Zoom);
        NoteTypography.Configure(State.NoteTextSize, State.NoteTextBold);
        SaveNow();
        RefreshTypography();
        RefreshSettingsWindowContent();
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
        ApplyTypographySettingsChange();
    }

    private UIElement CreateTodoVisualSizeSegmentSelector()
    {
        var segments = new[]
        {
            (TodoVisualSizes.Small, Strings.Get("TodoVisualSizeSmall")),
            (TodoVisualSizes.Medium, Strings.Get("TodoVisualSizeMedium")),
            (TodoVisualSizes.Large, Strings.Get("TodoVisualSizeLarge"))
        };

        return CreateSegmentSelector(segments, TodoVisualSizes.Normalize(State.TodoVisualSize), SetTodoVisualSize);
    }

    private UIElement CreateVisualTextSizeSegmentSelector(string activeSize, Action<string> onSelect)
    {
        var segments = new[]
        {
            (VisualTextSizes.Small, Strings.Get("TodoVisualSizeSmall")),
            (VisualTextSizes.Medium, Strings.Get("TodoVisualSizeMedium")),
            (VisualTextSizes.Large, Strings.Get("TodoVisualSizeLarge"))
        };

        return CreateSegmentSelector(segments, VisualTextSizes.Normalize(activeSize), onSelect);
    }

    private UIElement CreateOverallFontScaleStepper()
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
            Text = OverallFontScaleText(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = AppTypography.Scale(13),
            FontWeight = FontWeights.SemiBold,
            Foreground = TrayTextBrush
        };
        Grid.SetColumn(valueText, 1);

        Border StepButton(string glyph, int column, double delta)
        {
            var glyphText = new TextBlock
            {
                Text = glyph,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = AppTypography.SymbolFontFamily,
                FontSize = AppTypography.Scale(15),
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
                SetOverallFontScale(State.Zoom + delta);
                e.Handled = true;
            };
            Grid.SetColumn(button, column);
            return button;
        }

        grid.Children.Add(StepButton("−", 0, -OverallFontScales.Step));
        grid.Children.Add(valueText);
        grid.Children.Add(StepButton("＋", 2, OverallFontScales.Step));
        container.Child = grid;
        return container;
    }

    private string OverallFontScaleText()
    {
        return $"{Math.Round(OverallFontScales.Normalize(State.Zoom) * 100):0}%";
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
            FontSize = AppTypography.Scale(13),
            Height = AppTypography.FitChrome(28),
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

    private void CommitSettingsExternalMarkdownEditor(bool saveImmediately = true)
    {
        if (_settingsExternalMarkdownTextBox != null)
        {
            CommitExternalMarkdownExtension(_settingsExternalMarkdownTextBox, saveImmediately);
        }
    }

    private void CommitExternalMarkdownExtension(TextBox textBox, bool saveImmediately = true)
    {
        var normalized = ExternalMarkdownFileExtensions.Normalize(textBox.Text);
        if (textBox.Text != normalized)
        {
            textBox.Text = normalized;
            textBox.CaretIndex = textBox.Text.Length;
        }

        SetExternalMarkdownExtension(normalized, saveImmediately);
    }

    private void SetExternalMarkdownExtension(string extension, bool saveImmediately = true)
    {
        var normalized = ExternalMarkdownFileExtensions.Normalize(extension);
        if (State.ExternalMarkdownExtension == normalized)
        {
            return;
        }

        State.ExternalMarkdownExtension = normalized;
        if (saveImmediately)
        {
            SaveNow();
        }

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
                FontSize = AppTypography.Scale(12),
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
            FontSize = AppTypography.Scale(13),
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
                FontFamily = AppTypography.SymbolFontFamily,
                FontSize = AppTypography.Scale(15),
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
        ShowSettingsWindow(SettingsPage.General);
    }

    private void ShowSettingsWindow(SettingsPage page)
    {
        var previousPage = _settingsPage;
        _settingsPage = page;
        if (previousPage == SettingsPage.Shortcuts && page != SettingsPage.Shortcuts)
        {
            // A rejected auto-save remains visible long enough to explain the rollback, then clears
            // when the user leaves the shortcut page or starts the next shortcut interaction.
            ClearShortcutApplyFailure();
        }
        if (page == SettingsPage.Shortcuts)
        {
            EnsureShortcutDraft();
        }
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
            // Height is fitted from measured page content in RefreshSettingsWindowContent.
            SizeToContent = SizeToContent.Manual,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(12),
            Language = AppTypography.Language,
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
        window.PreviewKeyDown += OnSettingsWindowPreviewKeyDown;
        window.PreviewKeyUp += OnSettingsWindowPreviewKeyUp;
        window.Deactivated += (_, _) => CommitSettingsExternalMarkdownEditor();
        window.Closed += (_, _) =>
        {
            CommitSettingsExternalMarkdownEditor();
            _settingsExternalMarkdownTextBox = null;
            _settingsHidePapersFromTaskbarCheckBox = null;
            _settingsHidePapersFromWindowSwitcherCheckBox = null;
            _settingsCapsuleModeCheckBox = null;
            _settingsDeepCapsuleModeCheckBox = null;
            _settingsDeepCapsuleExpandedSlotCheckBox = null;
            _settingsRememberDeepCapsuleExpandedPositionCheckBox = null;
            _settingsCollapseExpandedDeepCapsuleOnClickCheckBox = null;
            _settingsCapsuleCollapseAllCheckBox = null;
            _settingsPageScrollViewer = null;
            _settingsPageScrollViewerPage = null;
            DiscardShortcutDraft();
            _settingsWindow = null;
        };
        _settingsWindow = window;
        RefreshSettingsWindowContent();
        // Resolve the final fitted size before the first frame, then switch to manual positioning.
        // Later typography changes keep this top-left anchor and grow only toward the bottom.
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        CenterSettingsWindow(window);
        window.Show();
        window.Activate();
    }

    private void RefreshSettingsWindowContent()
    {
        if (_settingsWindow == null)
        {
            return;
        }

        var window = _settingsWindow;
        var previousScrollOffset = _settingsPageScrollViewerPage == _settingsPage
            ? _settingsPageScrollViewer?.VerticalOffset ?? 0
            : 0;
        var preserveAnchor = window.IsVisible &&
            double.IsFinite(window.Left) &&
            double.IsFinite(window.Top);
        var anchoredLeft = window.Left;
        var anchoredTop = window.Top;

        InvalidateSystemThemeCacheIfNeeded();
        var width = SettingsWindowWidth();

        // Measure natural page chrome (no ScrollViewer). Only enable scrolling when the tallest
        // page exceeds the work-area cap — otherwise Auto scrollbars appear even with free space.
        var naturalHeight = MeasureRequiredSettingsWindowHeight(width);
        var maxHeight = SettingsWindowMaxHeight();
        var needsScroll = naturalHeight > maxHeight + 0.5;
        var fittedHeight = Math.Min(naturalHeight, maxHeight);
        // Pin border height only when scrolling (viewport must be capped). When content fits,
        // leave the border unconstrained so a slightly short measure cannot clip the last rows;
        // the window height still uses the fitted value (with slack) as the outer frame.
        var content = BuildSettingsWindowContent(
            window,
            fittedHeight: needsScroll ? fittedHeight : null,
            enableScroll: needsScroll);
        if (_settingsPageScrollViewer is { } scrollViewer && previousScrollOffset > 0)
        {
            scrollViewer.Loaded += (_, _) => scrollViewer.Dispatcher.BeginInvoke(
                (Action)(() => scrollViewer.ScrollToVerticalOffset(
                    Math.Min(previousScrollOffset, scrollViewer.ScrollableHeight))),
                DispatcherPriority.ContextIdle);
        }

        if (preserveAnchor)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = anchoredLeft;
            window.Top = anchoredTop;
        }

        // Replace the content before resizing the native window. With a manual top-left anchor,
        // a larger fitted height extends downward instead of recentering around the old bounds.
        window.Title = Strings.Get("TraySettings");
        window.SizeToContent = SizeToContent.Manual;
        window.FontFamily = AppTypography.UiFontFamily;
        window.FontSize = AppTypography.Scale(12);
        window.Language = AppTypography.Language;
        window.Content = content;
        window.Width = width;
        window.Height = fittedHeight;

        if (preserveAnchor)
        {
            // WPF/Win32 may round the new bounds to device pixels; explicitly restore the anchor.
            window.Left = anchoredLeft;
            window.Top = anchoredTop;
        }

        ApplyToolTipSetting(window);
    }

    private void RefreshTypography()
    {
        RebuildTrayMenu();

        foreach (var window in _windows.Values)
        {
            window.UpdateTypography();
        }

        foreach (var masterCapsule in _masterCapsules.Values)
        {
            masterCapsule.UpdateTypography();
        }
        ArrangeDeepCapsules(animate: false);
    }

    private UIElement BuildSettingsWindowContent(
        Window window,
        double? fittedHeight = null,
        bool enableScroll = false)
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
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                var previousWorkArea = WindowWorkAreaHelper.WorkAreaFor(window);
                try { window.DragMove(); } catch { }
                if (!previousWorkArea.Equals(WindowWorkAreaHelper.WorkAreaFor(window)))
                {
                    window.Dispatcher.BeginInvoke(
                        (Action)RefreshSettingsWindowContent,
                        DispatcherPriority.Background);
                }
            }
        };

        var title = new TextBlock
        {
            Text = Strings.Get("TraySettings"),
            Foreground = TrayTextBrush,
            FontSize = AppTypography.Scale(15),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 0);
        titleRow.Children.Add(title);

        var pageSelector = (FrameworkElement)CreateSettingsPageSelector();
        pageSelector.HorizontalAlignment = HorizontalAlignment.Left;
        pageSelector.VerticalAlignment = VerticalAlignment.Center;
        pageSelector.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(pageSelector, 1);
        titleRow.Children.Add(pageSelector);

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
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
            Style = BuildSettingsCloseButtonStyle()
        };
        closeButton.Click += (_, _) => window.Close();
        Grid.SetColumn(closeButton, 2);
        titleRow.Children.Add(closeButton);

        DockPanel.SetDock(titleRow, Dock.Top);
        root.Children.Add(titleRow);

        if (_settingsPage == SettingsPage.Shortcuts)
        {
            root.Children.Add(WrapSettingsPageContent(BuildShortcutSettingsPage(), enableScroll));
            return WrapSettingsWindowContent(root, fittedHeight, enableScroll);
        }

        if (_settingsPage == SettingsPage.Visual)
        {
            root.Children.Add(WrapSettingsPageContent(BuildVisualSettingsPage(), enableScroll));
            return WrapSettingsWindowContent(root, fittedHeight, enableScroll);
        }

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

        // Left: everyday desktop / window behavior. Right: paper features, capsule first.
        leftColumn.Children.Add(SettingsSectionLabel(Strings.Get("SettingsGeneral")));
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("TrayStartup"), SystemSettingsHelper.IsStartupEnabled(), ToggleStartup), "TipStartup"));
        _settingsHidePapersFromTaskbarCheckBox = SettingsToggle(Strings.Get("SettingsHidePapersFromTaskbar"), State.HidePapersFromTaskbar, ToggleHidePapersFromTaskbar);
        _settingsHidePapersFromWindowSwitcherCheckBox = SettingsToggle(Strings.Get("SettingsHidePapersFromWindowSwitcher"), State.HidePapersFromWindowSwitcher, ToggleHidePapersFromWindowSwitcher);
        leftColumn.Children.Add(WrapWithHint(_settingsHidePapersFromTaskbarCheckBox, "TipHidePapersFromTaskbar"));
        leftColumn.Children.Add(WrapWithHint(_settingsHidePapersFromWindowSwitcherCheckBox, "TipHidePapersFromWindowSwitcher"));
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsEnableToolTips"), State.EnableToolTips, ToggleToolTips), "TipEnableToolTips"));
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsEnableAnimations"), State.EnableAnimations, ToggleAnimations), "TipEnableAnimations"));
        leftColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsFullscreenTopmostMode"), topMargin: 8), "TipFullscreenTopmostMode"));
        leftColumn.Children.Add(CreateFullscreenTopmostModeSegmentSelector());
        leftColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("TrayMarkdownRenderMode"), topMargin: 8), "TipMarkdownRender"));
        leftColumn.Children.Add(CreateMarkdownRenderSegmentSelector());

        leftColumn.Children.Add(SettingsSectionLabel(Strings.Get("SettingsTopBarButtons")));
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsShowTopBarNewTodoButton"), State.ShowTopBarNewTodoButton, ToggleTopBarNewTodoButton), "TipNewTodoButton"));
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsShowTopBarNewNoteButton"), State.ShowTopBarNewNoteButton, ToggleTopBarNewNoteButton), "TipNewNoteButton"));
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsShowTopBarExternalOpenButton"), State.ShowTopBarExternalOpenButton, ToggleTopBarExternalOpenButton), "TipExternalOpenButton"));

        leftColumn.Children.Add(SettingsSectionLabel(Strings.Get("SettingsExternalOpen")));
        leftColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsExternalMarkdownExtension")), "TipExternalExtension"));
        leftColumn.Children.Add(CreateExternalMarkdownExtensionEditor());

        // Keep script options on the shorter left column so they stay visible without scrolling.
        leftColumn.Children.Add(SettingsSectionLabel(Strings.Get("SettingsScriptCapsule")));
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsPersistentPowerShellProcess"), State.UsePersistentPowerShellProcess, TogglePersistentPowerShellProcess), "TipPersistentPowerShellProcess"));
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsPreferPowerShell7"), State.PreferPowerShell7, TogglePreferPowerShell7), "TipPreferPowerShell7"));
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsHideScriptRunWindow"), State.HideScriptRunWindow, ToggleHideScriptRunWindow), "TipHideScriptRunWindow"));

        rightColumn.Children.Add(SettingsSectionLabel(Strings.Get("SettingsCapsule")));
        _settingsCapsuleModeCheckBox = SettingsToggle(Strings.Get("TrayCapsuleMode"), State.UseCapsuleMode, ToggleCapsuleMode);
        _settingsDeepCapsuleModeCheckBox = SettingsToggle(Strings.Get("TrayDeepCapsuleMode"), State.UseDeepCapsuleMode, ToggleDeepCapsuleMode);
        _settingsDeepCapsuleExpandedSlotCheckBox = SettingsToggle(Strings.Get("SettingsShowDeepCapsuleWhileExpanded"), State.ShowDeepCapsuleWhileExpanded, ToggleDeepCapsuleExpandedSlot);
        _settingsRememberDeepCapsuleExpandedPositionCheckBox = SettingsToggle(Strings.Get("SettingsRememberDeepCapsuleExpandedPosition"), State.RememberDeepCapsuleExpandedPosition, ToggleRememberDeepCapsuleExpandedPosition);
        _settingsCollapseExpandedDeepCapsuleOnClickCheckBox = SettingsToggle(Strings.Get("SettingsCollapseExpandedDeepCapsuleOnClick"), State.CollapseExpandedDeepCapsuleOnClick, ToggleCollapseExpandedDeepCapsuleOnClick);
        _settingsCapsuleCollapseAllCheckBox = SettingsToggle(Strings.Get("SettingsCapsuleCollapseAll"), State.UseCapsuleCollapseAll, ToggleCapsuleCollapseAll);
        rightColumn.Children.Add(WrapWithHint(_settingsCapsuleModeCheckBox, "TipCapsuleMode"));
        rightColumn.Children.Add(WrapWithHint(_settingsDeepCapsuleModeCheckBox, "TipDeepCapsuleMode"));
        rightColumn.Children.Add(WrapWithHint(_settingsDeepCapsuleExpandedSlotCheckBox, "TipShowDeepCapsuleWhileExpanded"));
        rightColumn.Children.Add(WrapWithHint(_settingsRememberDeepCapsuleExpandedPositionCheckBox, "TipRememberDeepCapsuleExpandedPosition"));
        rightColumn.Children.Add(WrapWithHint(_settingsCollapseExpandedDeepCapsuleOnClickCheckBox, "TipCollapseExpandedDeepCapsuleOnClick"));
        rightColumn.Children.Add(WrapWithHint(_settingsCapsuleCollapseAllCheckBox, "TipCapsuleCollapseAll"));
        RefreshSettingsCapsuleToggleStates();
        rightColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsMaxTitleLength"), topMargin: 8), "TipMaxTitleLength"));
        rightColumn.Children.Add(CreateMaxTitleLengthStepper());
        rightColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsDeepCapsuleTitleMeasureLimit"), topMargin: 8), "TipDeepCapsuleTitleMeasureLimit"));
        rightColumn.Children.Add(CreateDeepCapsuleTitleMeasureLimitStepper());

        rightColumn.Children.Add(SettingsSectionLabel(Strings.Get("SettingsTodoNote")));
        rightColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsAutoCompressLargeImages"), State.AutoCompressLargeImages, ToggleAutoCompressLargeImages), "TipAutoCompressLargeImages"));
        rightColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsAutoClearCompletedTodos"), State.AutoClearCompletedTodos, ToggleAutoClearCompletedTodos), "TipAutoClearCompletedTodos"));
        rightColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsEnableTodoNoteLinks"), State.EnableTodoNoteLinks, ToggleTodoNoteLinks), "TipEnableTodoNoteLinks"));
        var showLinkedNoteNameToggle = SettingsToggle(Strings.Get("SettingsShowLinkedNoteName"), State.ShowLinkedNoteName, ToggleLinkedNoteNameDisplay);
        showLinkedNoteNameToggle.IsEnabled = State.EnableTodoNoteLinks;
        rightColumn.Children.Add(WrapWithHint(showLinkedNoteNameToggle, "TipShowLinkedNoteName"));
        var allowLongLinkedNoteTitlesToggle = SettingsToggle(Strings.Get("SettingsAllowLongLinkedNoteTitles"), State.AllowLongLinkedNoteTitles, ToggleLongLinkedNoteTitles);
        allowLongLinkedNoteTitlesToggle.IsEnabled = State.EnableTodoNoteLinks && State.ShowLinkedNoteName;
        rightColumn.Children.Add(WrapWithHint(allowLongLinkedNoteTitlesToggle, "TipAllowLongLinkedNoteTitles"));
        var hideLinkedNotesFromCapsulesToggle = SettingsToggle(Strings.Get("SettingsHideLinkedNotesFromCapsules"), State.HideLinkedNotesFromCapsules, ToggleHideLinkedNotesFromCapsules);
        hideLinkedNotesFromCapsulesToggle.IsEnabled = State.EnableTodoNoteLinks;
        rightColumn.Children.Add(WrapWithHint(hideLinkedNotesFromCapsulesToggle, "TipHideLinkedNotesFromCapsules"));
        var runLinkedScriptCapsulesToggle = SettingsToggle(Strings.Get("SettingsRunLinkedScriptCapsulesOnClick"), State.RunLinkedScriptCapsulesOnClick, ToggleRunLinkedScriptCapsulesOnClick);
        runLinkedScriptCapsulesToggle.IsEnabled = State.EnableTodoNoteLinks;
        rightColumn.Children.Add(WrapWithHint(runLinkedScriptCapsulesToggle, "TipRunLinkedScriptCapsulesOnClick"));

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

        root.Children.Add(WrapSettingsPageContent(columns, enableScroll));

        return WrapSettingsWindowContent(root, fittedHeight, enableScroll);
    }

    private UIElement BuildVisualSettingsPage()
    {
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
        leftColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsUiFont")), "TipUiFont"));
        leftColumn.Children.Add(CreateUiFontPresetSegmentSelector());
        leftColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsOverallFontScale")), "TipOverallFontScale"));
        leftColumn.Children.Add(CreateOverallFontScaleStepper());

        void AddTextStyleEditor(
            StackPanel column,
            string sectionKey,
            string tipKey,
            string activeSize,
            Action<string> setSize,
            bool isBold,
            Action toggleBold,
            bool leadingDivider)
        {
            if (leadingDivider)
            {
                column.Children.Add(SettingsSoftDivider());
            }

            // One shared tip on the section title: size + bold are the same style group.
            column.Children.Add(SettingsSectionLabelWithHint(Strings.Get(sectionKey), tipKey));
            column.Children.Add(CreateVisualTextSizeSegmentSelector(activeSize, setSize));
            column.Children.Add(SettingsToggle(Strings.Get("SettingsTextBold"), isBold, toggleBold));
        }

        AddTextStyleEditor(
            rightColumn,
            "SettingsNoteBodyText",
            "TipNoteBodyTextStyle",
            State.NoteTextSize,
            SetNoteTextSize,
            State.NoteTextBold,
            ToggleNoteTextBold,
            leadingDivider: false);

        rightColumn.Children.Add(SettingsSoftDivider());
        rightColumn.Children.Add(SettingsSectionLabelWithHint(
            Strings.Get("SettingsTodoBodyText"),
            "TipTodoBodyTextStyle"));
        rightColumn.Children.Add(CreateTodoVisualSizeSegmentSelector());
        rightColumn.Children.Add(SettingsToggle(
            Strings.Get("SettingsTextBold"),
            State.TodoTextBold,
            ToggleTodoTextBold));

        AddTextStyleEditor(
            rightColumn,
            "SettingsTitleText",
            "TipTitleTextStyle",
            State.TitleTextSize,
            SetTitleTextSize,
            State.TitleTextBold,
            ToggleTitleTextBold,
            leadingDivider: true);
        AddTextStyleEditor(
            rightColumn,
            "SettingsCapsuleText",
            "TipCapsuleTextStyle",
            State.CapsuleTextSize,
            SetCapsuleTextSize,
            State.CapsuleTextBold,
            ToggleCapsuleTextBold,
            leadingDivider: true);

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

        return columns;
    }

    private UIElement CreateSettingsPageSelector()
    {
        const string generalKey = "general";
        const string visualKey = "visual";
        const string shortcutsKey = "shortcuts";
        var segments = new[]
        {
            (Key: generalKey, Label: Strings.Get("SettingsBehavior")),
            (Key: visualKey, Label: Strings.Get("SettingsVisual")),
            (Key: shortcutsKey, Label: Strings.Get("SettingsShortcuts"))
        };
        var activeKey = _settingsPage switch
        {
            SettingsPage.Visual => visualKey,
            SettingsPage.Shortcuts => shortcutsKey,
            _ => generalKey
        };

        // Premium main segmented capsule container
        var container = new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = TrayHoverBrush, // Sunken tab track background
            Margin = new Thickness(0),
            Height = 24,
            Width = 228,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var grid = new Grid();
        foreach (var _ in segments)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (int i = 0; i < segments.Length; i++)
        {
            var key = segments[i].Key;
            var label = segments[i].Label;
            var isActive = activeKey == key;

            // Segment item card
            var segmentBorder = new Border
            {
                CornerRadius = new CornerRadius(3.5),
                Margin = new Thickness(1.5), // Micro margin for inline capsule
                Background = isActive ? Theme.ActiveBrush : Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var textBlock = new TextBlock
            {
                Text = label,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = AppTypography.Scale(11),
                FontWeight = isActive ? FontWeights.Bold : FontWeights.Medium,
                Foreground = isActive ? TrayPaperBrush : TrayWeakTextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            segmentBorder.Child = textBlock;

            // Micro-interaction hover behavior
            if (!isActive)
            {
                segmentBorder.MouseEnter += (_, _) =>
                {
                    textBlock.Foreground = TrayTextBrush; // Elevate text readability on hover
                };
                segmentBorder.MouseLeave += (_, _) =>
                {
                    textBlock.Foreground = TrayWeakTextBrush;
                };
            }

            segmentBorder.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (activeKey == key)
                {
                    return;
                }
                ShowSettingsWindow(key switch
                {
                    visualKey => SettingsPage.Visual,
                    shortcutsKey => SettingsPage.Shortcuts,
                    _ => SettingsPage.General
                });
            };

            Grid.SetColumn(segmentBorder, i);
            grid.Children.Add(segmentBorder);
        }

        container.Child = grid;
        return container;
    }

    private UIElement WrapSettingsPageContent(UIElement content, bool enableScroll)
    {
        // Overlay signature sits on the bottom-right; keep bottom inset so the last row is not
        // hidden under it. Only use ScrollViewer when the window is capped by the work area.
        var body = new Border
        {
            Padding = new Thickness(0, 0, 0, enableScroll ? 28 : 24),
            Child = content
        };

        if (!enableScroll)
        {
            _settingsPageScrollViewer = null;
            _settingsPageScrollViewerPage = _settingsPage;
            return body;
        }

        var scrollViewer = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            PanningMode = PanningMode.VerticalOnly
        };
        _settingsPageScrollViewer = scrollViewer;
        _settingsPageScrollViewerPage = _settingsPage;
        return scrollViewer;
    }

    private Border WrapSettingsWindowContent(
        DockPanel root,
        double? fittedHeight = null,
        bool reserveScrollBar = false)
    {
        var overlay = new Grid();
        overlay.Children.Add(root);

        var signature = BuildSettingsSignature(reserveScrollBar);
        Panel.SetZIndex(signature, 10);
        overlay.Children.Add(signature);

        var border = new Border
        {
            Background = TrayPaperBrush,
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Width = SettingsWindowWidth(),
            Padding = new Thickness(14, 12, 14, 14),
            // Fill the window client area so shorter pages keep a stable frame without clipping
            // when the outer window is sized to the tallest measured page.
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = overlay
        };
        if (fittedHeight is > 0)
        {
            // Only when scrolling: pin the chrome so the ScrollViewer gets a finite viewport.
            border.Height = fittedHeight.Value;
        }

        return border;
    }

    private double MeasureRequiredSettingsWindowHeight(double windowWidth)
    {
        if (_settingsWindow == null)
        {
            return Math.Min(660, SettingsWindowMaxHeight());
        }

        var previousPage = _settingsPage;
        var maxHeight = 0.0;
        try
        {
            // Use the tallest page so switching tabs does not resize, and options on dense pages stay visible.
            foreach (var page in new[]
                     {
                         SettingsPage.General,
                         SettingsPage.Visual,
                         SettingsPage.Shortcuts
                     })
            {
                _settingsPage = page;
                if (page == SettingsPage.Shortcuts)
                {
                    EnsureShortcutDraft();
                }

                // Probe without ScrollViewer / fixed height so DesiredSize is true content chrome.
                var probe = BuildSettingsWindowContent(_settingsWindow, fittedHeight: null, enableScroll: false);
                probe.Measure(new Size(windowWidth, double.PositiveInfinity));
                maxHeight = Math.Max(maxHeight, probe.DesiredSize.Height);
            }
        }
        finally
        {
            _settingsPage = previousPage;
        }

        if (maxHeight < 1)
        {
            maxHeight = 400;
        }

        // Generous slack for DPI rounding, UseLayoutRounding, and font metric variance after the
        // live tree is attached — too little here clips the last settings rows without a scrollbar.
        // Do not clamp to work-area here — caller decides scroll vs grow.
        return Math.Ceiling(maxHeight + 16);
    }

    private UIElement BuildSettingsSignature(bool reserveScrollBar)
    {
        var signatureText = new TextBlock
        {
            Text = AuthorName,
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(11),
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center
        };

        var signature = new Border
        {
            Background = TrayPaperBrush,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            // Keep the overlay clear of the vertical scrollbar when the page is capped.
            Margin = new Thickness(
                0,
                0,
                reserveScrollBar ? SystemParameters.VerticalScrollBarWidth + 4 : 4,
                0),
            Padding = new Thickness(6, 2, 0, 2),
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

    private double SettingsWindowMaxHeight()
    {
        return Math.Max(260, WindowWorkAreaHelper.WorkAreaFor(_settingsWindow).Height - 48);
    }

    private static TextBlock SettingsSectionLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(12),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 2)
        };
    }

    private UIElement SettingsSectionLabelWithHint(string text, string tipKey)
    {
        // Same layout as WrapWithHint: section title left, ⓘ pinned to the far right.
        var grid = new Grid
        {
            Margin = new Thickness(0, 2, 0, 4)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = text,
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(12),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var hint = CreateSettingsHintGlyph(tipKey, margin: new Thickness(6, 0, 0, 0));
        Grid.SetColumn(hint, 1);
        grid.Children.Add(hint);
        return grid;
    }

    private static UIElement SettingsSoftDivider()
    {
        return new Border
        {
            Height = 1,
            Margin = new Thickness(0, 14, 0, 8),
            Background = TrayBorderBrush,
            Opacity = 0.4,
            SnapsToDevicePixels = true
        };
    }

    private static TextBlock SettingsFieldLabel(string text, double topMargin = 0)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(11),
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
            FontSize = AppTypography.Scale(13),
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

        var hint = CreateSettingsHintGlyph(tipKey, margin: new Thickness(6, 0, 0, 0));
        Grid.SetColumn(hint, 1);
        grid.Children.Add(hint);

        return grid;
    }

    private Border CreateSettingsHintGlyph(string tipKey, Thickness margin)
    {
        var hintGlyph = new TextBlock
        {
            Text = "ⓘ",
            Foreground = TrayWeakTextBrush,
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = AppTypography.Scale(12),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var hint = new Border
        {
            Width = 18,
            Height = 18,
            Margin = margin,
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Help,
            VerticalAlignment = VerticalAlignment.Center,
            Child = hintGlyph,
            ToolTip = BuildSettingsHintTooltip(Strings.Get(tipKey))
        };
        ToolTipPreferences.SetAlwaysEnabled(hint, true);
        ToolTipService.SetInitialShowDelay(hint, 200);
        ToolTipService.SetShowDuration(hint, 20000);
        ToolTipService.SetBetweenShowDelay(hint, 0);
        hint.MouseEnter += (_, _) => hintGlyph.Foreground = TrayTextBrush;
        hint.MouseLeave += (_, _) => hintGlyph.Foreground = TrayWeakTextBrush;
        return hint;
    }

    private ToolTip BuildSettingsHintTooltip(string text)
    {
        return new ToolTip
        {
            Content = new TextBlock
            {
                Text = text,
                Foreground = TrayTextBrush,
                FontSize = AppTypography.Scale(12),
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
        RefreshSettingsSystemVisibilityToggleStates();

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
        if (_settingsRememberDeepCapsuleExpandedPositionCheckBox != null)
        {
            _settingsRememberDeepCapsuleExpandedPositionCheckBox.IsChecked = State.RememberDeepCapsuleExpandedPosition;
            _settingsRememberDeepCapsuleExpandedPositionCheckBox.IsEnabled = State.UseCapsuleMode && State.UseDeepCapsuleMode;
        }
        if (_settingsCollapseExpandedDeepCapsuleOnClickCheckBox != null)
        {
            _settingsCollapseExpandedDeepCapsuleOnClickCheckBox.IsChecked = State.CollapseExpandedDeepCapsuleOnClick;
            _settingsCollapseExpandedDeepCapsuleOnClickCheckBox.IsEnabled = State.UseCapsuleMode && State.UseDeepCapsuleMode &&
                State.ShowDeepCapsuleWhileExpanded;
        }
        if (_settingsCapsuleCollapseAllCheckBox != null)
        {
            _settingsCapsuleCollapseAllCheckBox.IsChecked = State.UseCapsuleCollapseAll;
            _settingsCapsuleCollapseAllCheckBox.IsEnabled = State.UseCapsuleMode && State.UseDeepCapsuleMode;
        }
    }

    private void RefreshSettingsSystemVisibilityToggleStates()
    {
        if (_settingsHidePapersFromTaskbarCheckBox != null)
        {
            _settingsHidePapersFromTaskbarCheckBox.IsChecked = State.HidePapersFromTaskbar;
            _settingsHidePapersFromTaskbarCheckBox.IsEnabled = !State.HidePapersFromWindowSwitcher;
        }
        if (_settingsHidePapersFromWindowSwitcherCheckBox != null)
        {
            _settingsHidePapersFromWindowSwitcherCheckBox.IsChecked = State.HidePapersFromWindowSwitcher;
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
        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, false);
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

    private Style BuildSettingsCloseButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
        style.Setters.Add(new Setter(FrameworkElement.UseLayoutRoundingProperty, true));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button))
        {
            VisualTree = border
        };

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, TrayHoverBrush, "Bd"));
        hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, TrayBorderBrush, "Bd"));
        hoverTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1), "Bd"));
        hoverTrigger.Setters.Add(new Setter(Control.ForegroundProperty, TrayTextBrush));

        var pressedTrigger = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, Theme.ActiveBrush, "Bd"));
        pressedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.Transparent, "Bd"));
        pressedTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1), "Bd"));
        pressedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, TrayPaperBrush));

        template.Triggers.Add(hoverTrigger);
        template.Triggers.Add(pressedTrigger);

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
        var height = window.ActualHeight > 1
            ? window.ActualHeight
            : double.IsFinite(window.Height) && window.Height > 1
                ? window.Height
                : 280;
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
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Window or UserPreferenceCategory.Desktop)
        {
            ScheduleDisplayMetricsRefresh();
        }

        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            if (State.Theme == "system")
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (State.Theme == "system")
                    {
                        RefreshThemeSurfaces();
                    }
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
        if (!State.EnableAnimations)
        {
            foreach (var window in _windows.Values)
            {
                window.SettleAnimationsForDisabledSetting();
            }
            ArrangeDeepCapsules(animate: false);
        }
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleAutoClearCompletedTodos()
    {
        State.AutoClearCompletedTodos = !State.AutoClearCompletedTodos;
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleAutoCompressLargeImages()
    {
        State.AutoCompressLargeImages = !State.AutoCompressLargeImages;
        _imageStore.AutoCompressLargeImages = State.AutoCompressLargeImages;
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleHidePapersFromTaskbar()
    {
        if (State.HidePapersFromWindowSwitcher)
        {
            State.HidePapersFromTaskbar = true;
            RefreshSettingsSystemVisibilityToggleStates();
            return;
        }

        State.HidePapersFromTaskbar = !State.HidePapersFromTaskbar;
        SaveNow();
        RefreshPaperSystemVisibility(reapplyTaskbarShellState: true);
        RefreshSettingsSystemVisibilityToggleStates();
    }

    private void ToggleHidePapersFromWindowSwitcher()
    {
        State.HidePapersFromWindowSwitcher = !State.HidePapersFromWindowSwitcher;
        if (State.HidePapersFromWindowSwitcher)
        {
            State.HidePapersFromTaskbar = true;
        }

        SaveNow();
        RefreshPaperSystemVisibility(reapplyTaskbarShellState: true);
        RefreshSettingsSystemVisibilityToggleStates();
    }

    private void NormalizePaperSystemVisibilitySettings()
    {
        if (State.HidePapersFromWindowSwitcher)
        {
            State.HidePapersFromTaskbar = true;
        }
    }

    private void RefreshPaperSystemVisibility(bool reapplyTaskbarShellState = false)
    {
        foreach (var window in _windows.Values)
        {
            window.ApplySystemVisibility(reapplyTaskbarShellState);
        }
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
        }

        // Keep IsCollapsed intact until each live window has consumed the mode change.
        // UpdateCapsuleMode uses that state to perform the capsule-to-paper visual transition.
        foreach (var window in _windows.Values)
        {
            window.UpdateCapsuleMode();
        }

        if (!State.UseCapsuleMode)
        {
            // Window-backed papers are already expanded. This also covers papers that do
            // not currently have a live window.
            foreach (var paper in State.Papers)
            {
                SetPaperCollapsedRuntime(paper, collapsed: false, animate: false, saveGeometry: false);
            }
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

    private void ToggleLongLinkedNoteTitles()
    {
        State.AllowLongLinkedNoteTitles = !State.AllowLongLinkedNoteTitles;

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

    private void ToggleCollapseExpandedDeepCapsuleOnClick()
    {
        State.CollapseExpandedDeepCapsuleOnClick = !State.CollapseExpandedDeepCapsuleOnClick;
        SaveNow();
        RefreshSettingsCapsuleToggleStates();
    }

    private void ToggleRememberDeepCapsuleExpandedPosition()
    {
        State.RememberDeepCapsuleExpandedPosition = !State.RememberDeepCapsuleExpandedPosition;
        SaveNow();
        RefreshSettingsCapsuleToggleStates();
    }

    private void RestoreMissingVisiblePaperSurfaces()
    {
        foreach (var paper in State.Papers.ToList())
        {
            if (!paper.IsVisible ||
                !_windows.TryGetValue(paper.Id, out var window) ||
                window.WindowState == WindowState.Minimized ||
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

        Rect? snapTileBounds = null;
        if (!paper.IsCollapsed && window.TryGetRememberedSnapTileBoundsForRestore(out var rememberedSnapTileBounds))
        {
            snapTileBounds = rememberedSnapTileBounds;
        }

        var targetBounds = snapTileBounds is Rect snapTile
            ? snapTile
            : new Rect(paper.X, paper.Y, paper.Width, paper.Height);
        window.Left = targetBounds.Left;
        window.Top = targetBounds.Top;
        if (paper.IsCollapsed && State.UseCapsuleMode)
        {
            window.Width = window.DesiredCapsuleWindowWidth;
            window.Height = PaperLayoutDefaults.CapsuleHeight;
        }
        else
        {
            window.Width = targetBounds.Width;
            window.Height = targetBounds.Height;
        }

        var restoreOpacity = window.Opacity > 0 ? window.Opacity : 1.0;
        window.Opacity = snapTileBounds.HasValue ? 0.0 : restoreOpacity;
        if (!window.IsVisible)
        {
            window.Show();
        }
        if (snapTileBounds is Rect visibleTarget)
        {
            window.Dispatcher.InvokeAsync(() =>
            {
                if (!paper.IsVisible)
                {
                    return;
                }

                window.RestoreSnapTilePresentation(visibleTarget);
                window.Opacity = restoreOpacity;
            }, DispatcherPriority.Render);
        }
        window.RefreshEffectiveTopmost();
    }

}
