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
    private enum SettingsPage
    {
        General,
        Visual,
        Shortcuts,
        Plugins,
        Labs
    }

    private const string AuthorName = "Designed by trigger";
    private const string AuthorGithubUrl = "https://github.com/snownico0722";
    private SettingsPage _settingsPage;
    private ScrollViewer? _settingsPageScrollViewer;
    private SettingsPage? _settingsPageScrollViewerPage;
    private Window? _settingsWindow;
    private TextBox? _settingsExternalMarkdownTextBox;
    private CheckBox? _settingsHidePapersFromTaskbarCheckBox;
    private CheckBox? _settingsHidePapersFromWindowSwitcherCheckBox;
    private CheckBox? _settingsCapsuleModeCheckBox;
    private CheckBox? _settingsDeepCapsuleModeCheckBox;
    private CheckBox? _settingsDeepCapsuleExpandedSlotCheckBox;
    private CheckBox? _settingsRememberDeepCapsuleExpandedPositionCheckBox;
    private CheckBox? _settingsCollapseExpandedDeepCapsuleOnClickCheckBox;
    private CheckBox? _settingsCapsuleCollapseAllCheckBox;
    private readonly Dictionary<string, Action> _settingsRegionRefreshers =
        new(StringComparer.Ordinal);

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
        RefreshApplicationThemeResources();
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
            RefreshApplicationThemeResources();
        }
    }

    private void RefreshApplicationThemeResources()
    {
        var resources = Application.Current?.Resources;
        if (resources == null)
        {
            return;
        }

        resources["PaperScrollThumbBrush"] = Theme.ScrollThumbBrush;
        resources["PaperScrollThumbHoverBrush"] = Theme.ScrollThumbHoverBrush;
        resources["PaperResizeGripBrush"] = Theme.ResizeGripBrush(State.ResizeGripMode);
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

    private void SetTextRenderingProfile(string profile)
    {
        var normalized = TextRenderingProfiles.Normalize(profile);
        if (State.TextRenderingProfile == normalized)
        {
            return;
        }

        State.TextRenderingProfile = normalized;
        ApplyTypographySettingsChange();
    }

    private UIElement CreateTextRenderingProfileSegmentSelector()
    {
        var segments = new[]
        {
            (TextRenderingProfiles.Standard, Strings.Get("TextRenderingStandard")),
            (TextRenderingProfiles.Soft, Strings.Get("TextRenderingSoft")),
            (TextRenderingProfiles.Sharp, Strings.Get("TextRenderingSharp"))
        };

        return CreateSegmentSelector(
            segments,
            TextRenderingProfiles.Normalize(State.TextRenderingProfile),
            SetTextRenderingProfile);
    }

    private void ToggleAdvancedSettingsMode()
    {
        // Compact mode only hides less common controls; stored values stay in effect.
        State.AdvancedSettingsMode = !State.AdvancedSettingsMode;
        if (!State.AdvancedSettingsMode && _settingsPage == SettingsPage.Labs)
        {
            _settingsPage = SettingsPage.General;
        }
        _shortcutRecordingCommandId = null;
        ClearShortcutApplyFailure();
        SaveNow();
        RefreshSettingsWindowContent();
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

    private void ToggleExperimentalInactivePaperOpacity()
    {
        State.ExperimentalInactivePaperOpacity = !State.ExperimentalInactivePaperOpacity;
        SaveNow();
        RefreshExperimentalOpacitySurfaces();
        RefreshSettingsRegions("labs.focus");
    }

    private void SetExperimentalInactivePaperOpacityLevel(double opacity)
    {
        var normalized = ExperimentalOpacityLevels.Normalize(
            opacity,
            ExperimentalOpacityLevels.DefaultInactivePaper);
        if (Math.Abs(State.ExperimentalInactivePaperOpacityLevel - normalized) < 0.001)
        {
            return;
        }

        State.ExperimentalInactivePaperOpacityLevel = normalized;
        SaveNow();
        RefreshExperimentalOpacitySurfaces();
        RefreshSettingsRegions("labs.focus");
    }

    private void ToggleExperimentalRestingCapsuleOpacity()
    {
        State.ExperimentalRestingCapsuleOpacity = !State.ExperimentalRestingCapsuleOpacity;
        SaveNow();
        RefreshExperimentalOpacitySurfaces();
        RefreshSettingsRegions("labs.focus");
    }

    private void ToggleExperimentalRestingCapsuleOpacityIncludesMaster()
    {
        State.ExperimentalRestingCapsuleOpacityIncludesMaster =
            !State.ExperimentalRestingCapsuleOpacityIncludesMaster;
        SaveNow();
        RefreshExperimentalOpacitySurfaces();
        RefreshSettingsRegions("labs.focus");
    }

    private void ToggleExperimentalRestingCapsuleOpacityAlways()
    {
        State.ExperimentalRestingCapsuleOpacityAlways =
            !State.ExperimentalRestingCapsuleOpacityAlways;
        SaveNow();
        RefreshExperimentalOpacitySurfaces();
        RefreshSettingsRegions("labs.focus");
    }

    private void SetExperimentalRestingCapsuleOpacityLevel(double opacity)
    {
        var normalized = ExperimentalOpacityLevels.Normalize(
            opacity,
            ExperimentalOpacityLevels.DefaultRestingCapsule);
        if (Math.Abs(State.ExperimentalRestingCapsuleOpacityLevel - normalized) < 0.001)
        {
            return;
        }

        State.ExperimentalRestingCapsuleOpacityLevel = normalized;
        SaveNow();
        RefreshExperimentalOpacitySurfaces();
        RefreshSettingsRegions("labs.focus");
    }

    private void RefreshExperimentalOpacitySurfaces(bool animate = true)
    {
        foreach (var window in _windows.Values)
        {
            window.UpdateExperimentalOpacitySettings(animate);
        }
        foreach (var master in _masterCapsules.Values)
        {
            master.UpdateExperimentalOpacity();
        }
    }

    private void ToggleExperimentalHideInactiveTopBarButtons()
    {
        State.ExperimentalHideInactiveTopBarButtons =
            !State.ExperimentalHideInactiveTopBarButtons;
        SaveNow();
        RefreshExperimentalFocusPresentationSurfaces();
        RefreshSettingsRegions("labs.focus");
    }

    private void ToggleExperimentalHideInactiveTitleBar()
    {
        State.ExperimentalHideInactiveTitleBar =
            !State.ExperimentalHideInactiveTitleBar;
        SaveNow();
        RefreshExperimentalFocusPresentationSurfaces();
        RefreshSettingsRegions("labs.focus");
    }

    private void RefreshExperimentalFocusPresentationSurfaces()
    {
        foreach (var window in _windows.Values)
        {
            window.UpdateExperimentalFocusPresentationSettings();
        }
    }

    private void ToggleExperimentalCollapsePaperOnDeactivate()
    {
        State.ExperimentalCollapsePaperOnDeactivate =
            !State.ExperimentalCollapsePaperOnDeactivate;
        SaveNow();
        RefreshSettingsRegions("labs.focus");
    }

    private void ToggleExperimentalDockedCapsulesNonTopmost()
    {
        State.ExperimentalDockedCapsulesNonTopmost =
            !State.ExperimentalDockedCapsulesNonTopmost;
        SaveNow();
        foreach (var window in _windows.Values.ToList())
        {
            window.RefreshDeepCapsuleSlotTopmost();
        }
        foreach (var master in _masterCapsules.Values.ToList())
        {
            master.RefreshEffectiveTopmost();
        }
        RefreshSettingsRegions("labs.dockedCapsule");
    }

    private void ToggleExperimentalEdgeCapsuleHoverPreview()
    {
        State.ExperimentalEdgeCapsuleHoverPreview =
            !State.ExperimentalEdgeCapsuleHoverPreview;
        SaveNow();
        if (!State.ExperimentalEdgeCapsuleHoverPreview)
        {
            CloseEdgeCapsulePreview(animate: false, arrange: true);
        }
        RefreshEdgeCapsuleHoverIntentRuntime();
        RefreshSettingsRegions("labs.edgePreviewIntent");
    }

    private void ToggleExperimentalEdgeCapsuleHoverIntent()
    {
        State.ExperimentalEdgeCapsuleHoverIntent =
            !State.ExperimentalEdgeCapsuleHoverIntent;
        SaveNow();
        RefreshEdgeCapsuleHoverIntentRuntime();
        RefreshSettingsRegions("labs.edgePreviewIntent");
    }

    private void SetExperimentalEdgeCapsuleHoverIntentSensitivity(
        string sensitivity)
    {
        var normalized =
            EdgeCapsuleHoverIntentSensitivities.Normalize(sensitivity);
        if (string.Equals(
                State.ExperimentalEdgeCapsuleHoverIntentSensitivity,
                normalized,
                StringComparison.Ordinal))
        {
            return;
        }

        State.ExperimentalEdgeCapsuleHoverIntentSensitivity = normalized;
        SaveNow();
        RefreshEdgeCapsuleHoverIntentRuntime();
    }

    private void ToggleExperimentalAllowLockIconUnlock()
    {
        State.ExperimentalAllowLockIconUnlock =
            !State.ExperimentalAllowLockIconUnlock;
        SaveNow();
        RefreshAdvancedShortcutSurfaces();
        RefreshSettingsRegions("labs.passive");
    }

    private void SetExperimentalShortcutOpacityLevel(double opacity)
    {
        var normalized = ExperimentalOpacityLevels.Normalize(opacity, 0.35);
        if (Math.Abs(State.ExperimentalShortcutOpacityLevel - normalized) < 0.001)
        {
            return;
        }

        State.ExperimentalShortcutOpacityLevel = normalized;
        SaveNow();
        RefreshAdvancedShortcutSurfaces();
        RefreshSettingsRegions("labs.passive");
    }

    private void ToggleExperimentalTodoReminders()
    {
        State.ExperimentalTodoReminders = !State.ExperimentalTodoReminders;
        SaveNow();
        RefreshTodoReminderFeature();
        RefreshSettingsRegions("labs.reminders");
    }

    private void ToggleExperimentalTodoReminderShowButton()
    {
        State.ExperimentalTodoReminderShowButton =
            !State.ExperimentalTodoReminderShowButton;
        SaveNow();
        RefreshTodoReminderFeature();
        RefreshSettingsRegions("labs.reminders");
    }

    private void SetExperimentalTodoReminderQuickMinutes(int minutes)
    {
        var normalized =
            ExperimentalTodoReminderOptions.NormalizeQuickMinutes(minutes);
        if (State.ExperimentalTodoReminderQuickMinutes == normalized)
        {
            return;
        }

        State.ExperimentalTodoReminderQuickMinutes = normalized;
        SaveNow();
        RefreshSettingsRegions("labs.reminders");
    }

    private void ToggleExperimentalTodoReminderSoundEnabled()
    {
        State.ExperimentalTodoReminderSoundEnabled =
            !State.ExperimentalTodoReminderSoundEnabled;
        SaveNow();
        RefreshSettingsRegions("labs.reminders");
    }

    private void SetExperimentalTodoReminderSound(string sound)
    {
        var normalized = TodoReminderSoundOptions.Normalize(sound);
        if (string.Equals(
                State.ExperimentalTodoReminderSound,
                normalized,
                StringComparison.Ordinal))
        {
            return;
        }

        State.ExperimentalTodoReminderSound = normalized;
        SaveNow();
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
        AppTypography.Configure(
            State.UiFontPreset,
            State.Zoom,
            State.CustomFontEnhancedBold,
            State.TextRenderingProfile);
        NoteTypography.Configure(State.NoteTextSize, State.NoteTextBold);
        SaveNow();
        RefreshTypography();
        RefreshSettingsWindowContent();
    }

    private void ToggleCustomFontEnhancedBold()
    {
        State.CustomFontEnhancedBold = !State.CustomFontEnhancedBold;
        ApplyTypographySettingsChange();
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
    }

    private UIElement CreateMarkdownRenderSegmentSelector()
    {
        var segments = new[]
        {
            (MarkdownRenderModes.Off, Strings.Get("MarkdownRenderOff")),
            (MarkdownRenderModes.Basic, Strings.Get("MarkdownRenderBasic")),
            (MarkdownRenderModes.Enhanced, Strings.Get("MarkdownRenderEnhanced")),
            (MarkdownRenderModes.Full, Strings.Get("MarkdownRenderFull"))
        };

        return CreateSegmentSelector(segments, State.MarkdownRenderMode, SetMarkdownRenderMode);
    }

    private void SetImageReferenceTextMode(string mode)
    {
        var normalized = ImageReferenceTextModes.Normalize(mode);
        if (State.ImageReferenceTextMode == normalized)
        {
            return;
        }

        State.ImageReferenceTextMode = normalized;
        SaveNow();

        foreach (var window in _windows.Values)
        {
            window.UpdateImageReferenceTextMode();
        }
    }

    private UIElement CreateImageReferenceTextModeSelector()
    {
        var segments = new[]
        {
            (ImageReferenceTextModes.Always, Strings.Get("ImageReferenceTextAlways")),
            (ImageReferenceTextModes.Editing, Strings.Get("ImageReferenceTextEditing")),
            (ImageReferenceTextModes.Hidden, Strings.Get("ImageReferenceTextHidden"))
        };

        return CreateSegmentSelector(
            segments,
            ImageReferenceTextModes.Normalize(State.ImageReferenceTextMode),
            SetImageReferenceTextMode);
    }

    private void SetFullscreenTopmostMode(string mode)
    {
        var normalized = FullscreenTopmostModes.Normalize(mode);
        if (State.FullscreenTopmostMode == normalized)
        {
            return;
        }

        State.FullscreenTopmostMode = normalized;
        RefreshFullscreenAvoidanceRuntime();
        SaveNow();
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

        var selectedKey = activeKey;
        var refreshSegments = new List<Action>();
        var grid = new Grid();
        for (var i = 0; i < segments.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (int i = 0; i < segments.Length; i++)
        {
            var key = segments[i].Key;
            var label = segments[i].Label;

            var segmentBorder = new Border
            {
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var textBlock = new TextBlock
            {
                Text = label,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = AppTypography.Scale(12),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            segmentBorder.Child = textBlock;

            void RefreshSegment()
            {
                var isActive = string.Equals(
                    selectedKey,
                    key,
                    StringComparison.Ordinal);
                segmentBorder.Background = isActive
                    ? Theme.ActiveBrush
                    : Brushes.Transparent;
                textBlock.FontWeight = isActive
                    ? FontWeights.SemiBold
                    : FontWeights.Normal;
                textBlock.Foreground = isActive
                    ? TrayPaperBrush
                    : TrayTextBrush;
            }
            refreshSegments.Add(RefreshSegment);
            RefreshSegment();

            segmentBorder.MouseEnter += (_, _) =>
            {
                if (!string.Equals(selectedKey, key, StringComparison.Ordinal))
                {
                    segmentBorder.Background = TrayHoverBrush;
                }
            };
            segmentBorder.MouseLeave += (_, _) => RefreshSegment();

            segmentBorder.MouseLeftButtonDown += (_, _) =>
            {
                if (string.Equals(selectedKey, key, StringComparison.Ordinal))
                {
                    return;
                }

                onSelect(key);
                selectedKey = key;
                foreach (var refresh in refreshSegments)
                {
                    refresh();
                }
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
        ClampPaperTitlesToMaxLength(normalized);

        foreach (var window in _windows.Values)
        {
            window.RefreshPaperTitle();
        }

        ArrangeDeepCapsules(animate: true);
        SaveNow();
        RebuildTrayMenu();
    }

    private void ClampPaperTitlesToMaxLength(int maxLength)
    {
        foreach (var paper in State.Papers)
        {
            paper.Title = PaperTitles.CleanCustomTitle(paper.Title, maxLength);
        }
    }

    internal void OpenSettingsWindow()
    {
        ShowSettingsWindow();
    }

    private void ShowSettingsWindow()
    {
        ShowSettingsWindow(SettingsPage.General);
    }

    private void ShowSettingsWindow(SettingsPage page)
    {
        if (page == SettingsPage.Labs && !State.AdvancedSettingsMode)
        {
            page = SettingsPage.General;
        }

        var previousPage = _settingsPage;
        _settingsPage = page;
        if (previousPage != page && SupportsShortcutRecording(previousPage))
        {
            // A rejected auto-save remains visible long enough to explain the rollback, then clears
            // when the user leaves a shortcut-bearing page or starts the next interaction.
            _shortcutRecordingCommandId = null;
            ClearShortcutApplyFailure();
        }
        if (SupportsShortcutRecording(page))
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
        AppTypography.ApplyTextRendering(window);

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

        // Keep the frame based on the original three settings pages. Labs uses that same
        // viewport and scrolls inside it, so adding experiments never grows the window.
        var naturalHeight = MeasureRequiredSettingsWindowHeight(width);
        var maxHeight = SettingsWindowMaxHeight();
        var needsScroll = naturalHeight > maxHeight + 0.5 ||
            _settingsPage is SettingsPage.Labs or SettingsPage.Plugins;
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
        AppTypography.ApplyTextRendering(window);
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
        _settingsRegionRefreshers.Clear();
        _pluginStatusRefreshers.Clear();
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

        var advancedModeToggle = SettingsToggle(
            Strings.Get("SettingsAdvancedMode"),
            State.AdvancedSettingsMode,
            ToggleAdvancedSettingsMode);
        advancedModeToggle.FontSize = AppTypography.Scale(11.5);
        advancedModeToggle.Margin = new Thickness(8, 0, 8, 0);
        advancedModeToggle.VerticalAlignment = VerticalAlignment.Center;
        advancedModeToggle.ToolTip = BuildSettingsHintTooltip(Strings.Get("TipAdvancedSettingsMode"));
        Grid.SetColumn(advancedModeToggle, 2);
        titleRow.Children.Add(advancedModeToggle);

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
        Grid.SetColumn(closeButton, 3);
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

        if (_settingsPage == SettingsPage.Labs)
        {
            root.Children.Add(WrapSettingsPageContent(BuildLabsSettingsPage(), enableScroll));
            return WrapSettingsWindowContent(root, fittedHeight, enableScroll);
        }

        if (_settingsPage == SettingsPage.Plugins)
        {
            var pluginsPage = BuildPluginsSettingsPage();
            root.Children.Add(WrapSettingsPageContent(pluginsPage, enableScroll));
            return WrapSettingsWindowContent(root, fittedHeight, enableScroll);
        }

        root.Children.Add(WrapSettingsPageContent(BuildGeneralSettingsPage(), enableScroll));
        return WrapSettingsWindowContent(root, fittedHeight, enableScroll);
    }

    private UIElement BuildLabsSettingsPage()
    {
        var root = new StackPanel
        {
            Margin = new Thickness(2, 4, 4, 0)
        };

        root.Children.Add(new TextBlock
        {
            Text = Strings.Get("SettingsLabsIntro"),
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(12),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = AppTypography.Scale(19)
        });

        var columns = new Grid
        {
            Margin = new Thickness(0, 12, 0, 0)
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

        AddLabsMajorSection(
            leftColumn,
            Strings.Get("LabsFocusBehavior"),
            BuildSettingsLiveRegion("labs.focus", BuildLabsFocusBehaviorSettings));
        AddLabsMajorSection(
            leftColumn,
            Strings.Get("LabsEdgeCapsuleHoverIntent"),
            BuildSettingsLiveRegion(
                "labs.edgePreviewIntent",
                BuildLabsEdgeCapsuleHoverIntentSettings));
        AddLabsMajorSection(
            leftColumn,
            Strings.Get("LabsWindowCoordination"),
            BuildSettingsLiveRegion("labs.window", BuildLabsWindowCoordinationSettings));

        AddLabsMajorSection(
            rightColumn,
            Strings.Get("LabsDockedCapsuleBehavior"),
            BuildSettingsLiveRegion(
                "labs.dockedCapsule",
                BuildLabsDockedCapsuleBehaviorSettings));
        AddLabsMajorSection(
            rightColumn,
            Strings.Get("LabsTodoReminders"),
            BuildSettingsLiveRegion("labs.reminders", BuildLabsTodoReminderSettings));
        AddLabsMajorSection(
            rightColumn,
            Strings.Get("LabsMcp"),
            BuildSettingsLiveRegion("labs.mcp", BuildLabsMcpSettings));
        AddLabsMajorSection(
            rightColumn,
            Strings.Get("LabsAdvancedShortcuts"),
            BuildSettingsLiveRegion("labs.passive", BuildLabsPassiveModeSettings));

        var separator = new Border
        {
            Width = 1,
            Margin = new Thickness(0, 4, 0, 0),
            Background = TrayBorderBrush,
            Opacity = 0.65
        };

        Grid.SetColumn(leftColumn, 0);
        Grid.SetColumn(separator, 1);
        Grid.SetColumn(rightColumn, 2);
        columns.Children.Add(leftColumn);
        columns.Children.Add(separator);
        columns.Children.Add(rightColumn);
        root.Children.Add(columns);

        return WithSettingsPageRestoreFooter(root, RestoreLabsSettingsPageDefaults);
    }

    private void AddLabsMajorSection(
        StackPanel column,
        string title,
        UIElement content)
    {
        if (column.Children.Count > 0)
        {
            column.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 11, 0, 2),
                Background = TrayBorderBrush,
                Opacity = 0.65
            });
        }

        column.Children.Add(SettingsSectionLabel(title));
        column.Children.Add(content);
    }

    private static void MovePluginMoreSettingsButtonsToTail(DependencyObject root)
    {
        if (root is Panel panel)
        {
            Button? moreButton = null;
            for (var index = panel.Children.Count - 1; index >= 0; index--)
            {
                var child = panel.Children[index];
                if (child is Button directButton && IsPluginMoreSettingsButton(directButton))
                {
                    moreButton = directButton;
                    panel.Children.RemoveAt(index);
                    continue;
                }

                if (child is WrapPanel wrap)
                {
                    var nestedButton = wrap.Children
                        .OfType<Button>()
                        .FirstOrDefault(IsPluginMoreSettingsButton);
                    if (nestedButton != null)
                    {
                        wrap.Children.Remove(nestedButton);
                        moreButton = nestedButton;
                        if (wrap.Children.Count == 1)
                        {
                            var remaining = wrap.Children[0];
                            wrap.Children.RemoveAt(0);
                            panel.Children.RemoveAt(index);
                            panel.Children.Insert(index, remaining);
                        }
                        else if (wrap.Children.Count == 0)
                        {
                            panel.Children.RemoveAt(index);
                        }
                    }
                }
            }

            foreach (UIElement child in panel.Children)
            {
                MovePluginMoreSettingsButtonsToTail(child);
            }

            if (moreButton != null)
            {
                moreButton.Margin = new Thickness(0, 8, 0, 0);
                moreButton.HorizontalAlignment = HorizontalAlignment.Left;
                panel.Children.Add(moreButton);
            }
            return;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            MovePluginMoreSettingsButtonsToTail(VisualTreeHelper.GetChild(root, index));
        }
    }

    private static bool IsPluginMoreSettingsButton(Button button) =>
        string.Equals(
            button.Content?.ToString(),
            Strings.Get("PluginsMoreSettings"),
            StringComparison.Ordinal);

    private UIElement BuildLabsWindowCoordinationSettings()
    {
        var content = new StackPanel();
        content.Children.Add(BuildLabsWindowTetherSettings());
        content.Children.Add(BuildLabsCapsuleMagnetSettings());
        return content;
    }

    private UIElement BuildLabsDockedCapsuleBehaviorSettings()
    {
        var card = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 3, 0, 5),
            Margin = new Thickness(0, 1, 0, 3)
        };
        card.Child = WrapWithHint(
            SettingsToggle(
                Strings.Get("LabsDockedCapsulesNonTopmost"),
                State.ExperimentalDockedCapsulesNonTopmost,
                ToggleExperimentalDockedCapsulesNonTopmost),
            "TipLabsDockedCapsulesNonTopmost");
        return card;
    }

    private UIElement BuildLabsFocusBehaviorSettings()
    {
        var card = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 3, 0, 5),
            Margin = new Thickness(0, 1, 0, 3)
        };
        var content = new StackPanel();

        content.Children.Add(SettingsFieldLabel(
            Strings.Get("LabsFocusInactiveGroup")));

        var autoCollapse = SettingsToggle(
            Strings.Get("LabsCollapsePaperOnDeactivate"),
            State.ExperimentalCollapsePaperOnDeactivate,
            ToggleExperimentalCollapsePaperOnDeactivate);
        autoCollapse.IsEnabled = State.UseCapsuleMode;
        autoCollapse.Opacity = State.UseCapsuleMode ? 1.0 : 0.55;
        content.Children.Add(WrapWithHint(
            autoCollapse,
            "TipLabsCollapsePaperOnDeactivate"));

        content.Children.Add(WrapWithHint(
            SettingsToggle(
                Strings.Get("LabsHideInactiveTopBarButtons"),
                State.ExperimentalHideInactiveTopBarButtons,
                ToggleExperimentalHideInactiveTopBarButtons),
            "TipLabsHideInactiveTopBarButtons"));

        content.Children.Add(WrapWithHint(
            SettingsToggle(
                Strings.Get("LabsHideInactiveTitleBar"),
                State.ExperimentalHideInactiveTitleBar,
                ToggleExperimentalHideInactiveTitleBar),
            "TipLabsHideInactiveTitleBar"));

        content.Children.Add(CompactSettingsToggleField(
            Strings.Get("LabsEnableInactivePaperOpacity"),
            State.ExperimentalInactivePaperOpacity,
            ToggleExperimentalInactivePaperOpacity,
            "TipLabsInactivePaperOpacity",
            CreateLabsPercentageStepper(
                () => State.ExperimentalInactivePaperOpacityLevel,
                SetExperimentalInactivePaperOpacityLevel,
                State.ExperimentalInactivePaperOpacity),
            editorWidth: 112,
            topMargin: 6));

        content.Children.Add(SettingsFieldLabel(
            Strings.Get("LabsFocusRestingGroup"),
            topMargin: 10));

        content.Children.Add(CompactSettingsToggleField(
            Strings.Get("LabsFocusRestingOpacity"),
            State.ExperimentalRestingCapsuleOpacity,
            ToggleExperimentalRestingCapsuleOpacity,
            "TipLabsFocusRestingOpacity",
            CreateLabsPercentageStepper(
                () => State.ExperimentalRestingCapsuleOpacityLevel,
                SetExperimentalRestingCapsuleOpacityLevel,
                State.ExperimentalRestingCapsuleOpacity),
            editorWidth: 112,
            topMargin: 4));

        var capsuleOpacityOptions = new StackPanel
        {
            Margin = new Thickness(22, 1, 0, 0),
            IsEnabled = State.ExperimentalRestingCapsuleOpacity,
            Opacity = State.ExperimentalRestingCapsuleOpacity ? 1.0 : 0.55
        };
        capsuleOpacityOptions.Children.Add(SettingsToggle(
            Strings.Get("LabsFocusRestingIncludeMaster"),
            State.ExperimentalRestingCapsuleOpacityIncludesMaster,
            ToggleExperimentalRestingCapsuleOpacityIncludesMaster));
        capsuleOpacityOptions.Children.Add(SettingsToggle(
            Strings.Get("LabsFocusRestingAlways"),
            State.ExperimentalRestingCapsuleOpacityAlways,
            ToggleExperimentalRestingCapsuleOpacityAlways));
        content.Children.Add(capsuleOpacityOptions);

        card.Child = content;
        return card;
    }

    private UIElement BuildLabsEdgeCapsuleHoverIntentSettings()
    {
        var edgePreviewAvailable =
            State.UseCapsuleMode && State.UseDeepCapsuleMode;
        var previewEnabled =
            State.ExperimentalEdgeCapsuleHoverPreview;
        var intentEnabled =
            State.ExperimentalEdgeCapsuleHoverIntent;
        var card = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 3, 0, 5),
            Margin = new Thickness(0, 1, 0, 3)
        };
        var content = new StackPanel();

        var previewToggle = SettingsToggle(
            Strings.Get("LabsEnableEdgeCapsuleHoverPreview"),
            previewEnabled,
            ToggleExperimentalEdgeCapsuleHoverPreview);
        previewToggle.IsEnabled = edgePreviewAvailable;
        previewToggle.Opacity = edgePreviewAvailable ? 1.0 : 0.55;
        content.Children.Add(WrapWithHint(
            previewToggle,
            "TipLabsEdgeCapsuleHoverPreview"));

        var toggle = SettingsToggle(
            Strings.Get("LabsEnableEdgeCapsuleHoverIntent"),
            intentEnabled,
            ToggleExperimentalEdgeCapsuleHoverIntent);
        var intentToggleEnabled =
            edgePreviewAvailable && previewEnabled;
        toggle.IsEnabled = intentToggleEnabled;
        toggle.Opacity = intentToggleEnabled ? 1.0 : 0.55;
        content.Children.Add(WrapWithHint(
            toggle,
            "TipLabsEdgeCapsuleHoverIntent"));

        var optionsEnabled =
            edgePreviewAvailable && previewEnabled && intentEnabled;
        var options = new StackPanel
        {
            IsEnabled = optionsEnabled,
            Opacity = optionsEnabled ? 1.0 : 0.55
        };
        options.Children.Add(CompactSettingsField(
            Strings.Get("LabsEdgeCapsuleHoverIntentSensitivity"),
            CreateEdgeCapsuleHoverIntentSensitivitySelector(),
            editorWidth: 132,
            tipKey: "TipLabsEdgeCapsuleHoverIntentSensitivity",
            topMargin: 4));
        content.Children.Add(options);
        card.Child = content;
        return card;
    }

    private UIElement BuildLabsPassiveModeSettings()
    {
        var card = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 3, 0, 5),
            Margin = new Thickness(0, 1, 0, 3)
        };
        var content = new StackPanel();
        if (GlobalShortcutCatalog.Find(GlobalShortcutCatalog.CurrentPaperPassive) is { } currentPassive)
        {
            content.Children.Add(BuildLabsShortcutSetting(
                currentPassive,
                "TipLabsCurrentPaperPassive"));
        }
        if (GlobalShortcutCatalog.Find(GlobalShortcutCatalog.AllSurfacesPassive) is { } allPassive)
        {
            content.Children.Add(BuildLabsShortcutSetting(
                allPassive,
                "TipLabsAllSurfacesPassive"));
        }

        content.Children.Add(SettingsFieldLabel(
            Strings.Get("LabsInteractionLock"),
            topMargin: 10));
        if (GlobalShortcutCatalog.Find(GlobalShortcutCatalog.LockAllPapers) is { } lockAll)
        {
            content.Children.Add(BuildLabsShortcutSetting(
                lockAll,
                "TipLabsLockAllPapers"));
        }
        content.Children.Add(WrapWithHint(
            SettingsToggle(
                Strings.Get("LabsAllowLockIconUnlock"),
                State.ExperimentalAllowLockIconUnlock,
                ToggleExperimentalAllowLockIconUnlock),
            "TipLabsAllowLockIconUnlock"));

        content.Children.Add(SettingsFieldLabel(
            Strings.Get("LabsShortcutTransparency"),
            topMargin: 10));
        content.Children.Add(SettingsFieldLabel(
            Strings.Get("LabsShortcutOpacityLevel"),
            topMargin: 5));
        content.Children.Add(CreateLabsPercentageStepper(
            () => State.ExperimentalShortcutOpacityLevel,
            SetExperimentalShortcutOpacityLevel,
            isEnabled: true));
        if (GlobalShortcutCatalog.Find(GlobalShortcutCatalog.AllPapersTransparent) is { } allPapers)
        {
            content.Children.Add(BuildLabsShortcutSetting(
                allPapers,
                "TipLabsAllPapersTransparent"));
        }
        if (GlobalShortcutCatalog.Find(GlobalShortcutCatalog.AllCapsulesTransparent) is { } allCapsules)
        {
            content.Children.Add(BuildLabsShortcutSetting(
                allCapsules,
                "TipLabsAllCapsulesTransparent"));
        }
        if (GlobalShortcutCatalog.Find(GlobalShortcutCatalog.CurrentPaperTransparent) is { } currentPaper)
        {
            content.Children.Add(BuildLabsShortcutSetting(
                currentPaper,
                "TipLabsCurrentPaperTransparent"));
        }
        card.Child = content;
        return card;
    }

    private UIElement BuildLabsCapsuleMagnetSettings()
    {
        var enabled = State.ExperimentalCapsuleMagnetism;
        var card = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 3, 0, 5),
            Margin = new Thickness(0, 1, 0, 3)
        };
        var content = new StackPanel();
        content.Children.Add(WrapWithHint(
            SettingsToggle(
                Strings.Get("LabsEnableCapsuleMagnetism"),
                enabled,
                ToggleExperimentalCapsuleMagnetism),
            "TipLabsCapsuleMagnetism"));
        if (State.UseDeepCapsuleMode)
        {
            content.Children.Add(new TextBlock
            {
                Text = Strings.Get("LabsCapsuleMagnetEdgeModeNotice"),
                Foreground = TrayWeakTextBrush,
                FontSize = AppTypography.Scale(11),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 5, 2, 0)
            });
        }

        var targets = new Grid
        {
            Margin = new Thickness(0, 7, 0, 0),
            IsEnabled = enabled,
            Opacity = enabled ? 1.0 : 0.55
        };
        targets.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        targets.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        var screenEdges = SettingsToggle(
            Strings.Get("LabsMagnetScreenEdges"),
            State.ExperimentalCapsuleMagnetScreenEdges,
            ToggleExperimentalCapsuleMagnetScreenEdges);
        var windowEdges = SettingsToggle(
            Strings.Get("LabsMagnetWindowEdges"),
            State.ExperimentalCapsuleMagnetWindowEdges,
            ToggleExperimentalCapsuleMagnetWindowEdges);
        Grid.SetColumn(screenEdges, 0);
        Grid.SetColumn(windowEdges, 1);
        targets.Children.Add(screenEdges);
        targets.Children.Add(windowEdges);
        content.Children.Add(targets);

        content.Children.Add(CompactSettingsField(
            Strings.Get("LabsMagnetDistance"),
            CreateLabsMagnetDistanceStepper(enabled),
            editorWidth: 132,
            topMargin: 8));
        card.Child = content;
        return card;
    }

    private UIElement CreateLabsMagnetDistanceStepper(bool isEnabled)
    {
        var container = new Border
        {
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 4, 0, 0),
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = isEnabled,
            Opacity = isEnabled ? 1.0 : 0.55
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var valueText = new TextBlock
        {
            Text = Strings.Format(
                "LabsMagnetDistanceValueFormat",
                State.ExperimentalCapsuleMagnetDistance),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = AppTypography.Scale(13),
            FontWeight = FontWeights.SemiBold,
            Foreground = TrayTextBrush
        };
        Grid.SetColumn(valueText, 1);

        Border StepButton(string glyph, int column, int delta)
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
                Cursor = Cursors.Hand,
                Child = glyphText
            };
            button.MouseEnter += (_, _) => button.Background = TrayHoverBrush;
            button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
            button.MouseLeftButtonDown += (_, e) =>
            {
                SetExperimentalCapsuleMagnetDistance(
                    State.ExperimentalCapsuleMagnetDistance + delta);
                e.Handled = true;
            };
            Grid.SetColumn(button, column);
            return button;
        }

        grid.Children.Add(StepButton(
            "−",
            0,
            -ExperimentalWindowAttachmentOptions.SnapDistanceStep));
        grid.Children.Add(valueText);
        grid.Children.Add(StepButton(
            "＋",
            2,
            ExperimentalWindowAttachmentOptions.SnapDistanceStep));
        container.Child = grid;
        return container;
    }

    private UIElement BuildLabsWindowTetherSettings()
    {
        var enabled = State.ExperimentalWindowTethering;
        var card = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 3, 0, 5),
            Margin = new Thickness(0, 1, 0, 3)
        };
        var content = new StackPanel();
        content.Children.Add(WrapWithHint(
            SettingsToggle(
                Strings.Get("LabsEnableWindowTethering"),
                enabled,
                ToggleExperimentalWindowTethering),
            "TipLabsWindowTetheringFixed"));

        var options = new StackPanel
        {
            IsEnabled = enabled,
            Opacity = enabled ? 1.0 : 0.55
        };
        options.Children.Add(CompactSettingsField(
            Strings.Get("LabsWindowTetherPreferredEdge"),
            CreateWindowTetherEdgeSelector(),
            editorWidth: 132,
            topMargin: 8));
        options.Children.Add(CompactSettingsField(
            Strings.Get("LabsWindowTetherGap"),
            CreateLabsWindowTetherGapStepper(),
            editorWidth: 132,
            topMargin: 4));
        content.Children.Add(options);
        card.Child = content;
        return card;
    }

    private UIElement CreateLabsWindowTetherGapStepper()
    {
        var container = new Border
        {
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 4, 0, 0),
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var valueText = new TextBlock
        {
            Text = Strings.Format(
                "LabsWindowTetherGapValueFormat",
                State.ExperimentalWindowTetherGap),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = AppTypography.Scale(13),
            FontWeight = FontWeights.SemiBold,
            Foreground = TrayTextBrush
        };
        Grid.SetColumn(valueText, 1);

        Border StepButton(string glyph, int column, int delta)
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
                Cursor = Cursors.Hand,
                Child = glyphText
            };
            button.MouseEnter += (_, _) => button.Background = TrayHoverBrush;
            button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
            button.MouseLeftButtonDown += (_, e) =>
            {
                SetExperimentalWindowTetherGap(
                    State.ExperimentalWindowTetherGap + delta);
                e.Handled = true;
            };
            Grid.SetColumn(button, column);
            return button;
        }

        grid.Children.Add(StepButton(
            "−",
            0,
            -ExperimentalWindowTetherOptions.GapStep));
        grid.Children.Add(valueText);
        grid.Children.Add(StepButton(
            "＋",
            2,
            ExperimentalWindowTetherOptions.GapStep));
        container.Child = grid;
        return container;
    }

    private UIElement BuildLabsTodoReminderSettings()
    {
        var card = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 3, 0, 5),
            Margin = new Thickness(0, 1, 0, 3)
        };
        var content = new StackPanel();
        content.Children.Add(WrapWithHint(
            SettingsToggle(
                Strings.Get("LabsEnableTodoReminders"),
                State.ExperimentalTodoReminders,
                ToggleExperimentalTodoReminders),
            "TipLabsTodoReminders"));

        var showButtonToggle = WrapWithHint(
            SettingsToggle(
                Strings.Get("LabsTodoReminderShowButton"),
                State.ExperimentalTodoReminderShowButton,
                ToggleExperimentalTodoReminderShowButton),
            "TipLabsTodoReminderShowButton");
        showButtonToggle.IsEnabled = State.ExperimentalTodoReminders;
        showButtonToggle.Opacity =
            State.ExperimentalTodoReminders ? 1.0 : 0.55;
        content.Children.Add(showButtonToggle);

        var soundToggle = WrapWithHint(
            SettingsToggle(
                Strings.Get("LabsTodoReminderSoundEnabled"),
                State.ExperimentalTodoReminderSoundEnabled,
                ToggleExperimentalTodoReminderSoundEnabled),
            "TipLabsTodoReminderSoundEnabled");
        soundToggle.IsEnabled = State.ExperimentalTodoReminders;
        soundToggle.Opacity =
            State.ExperimentalTodoReminders ? 1.0 : 0.55;
        content.Children.Add(soundToggle);

        content.Children.Add(CompactSettingsField(
            Strings.Get("LabsTodoReminderQuickMinutes"),
            CreateLabsTodoReminderMinutesStepper(),
            editorWidth: 132,
            topMargin: 7));

        var soundSelector = CreateTodoReminderSoundSelector();
        soundSelector.IsEnabled =
            State.ExperimentalTodoReminders &&
            State.ExperimentalTodoReminderSoundEnabled;
        soundSelector.Opacity = soundSelector.IsEnabled ? 1.0 : 0.55;
        content.Children.Add(CompactSettingsField(
            Strings.Get("LabsTodoReminderSound"),
            soundSelector,
            editorWidth: 156,
            tipKey: "TipLabsTodoReminderSound",
            topMargin: 7));

        card.Child = content;
        return card;
    }

    private UIElement CreateLabsTodoReminderMinutesStepper()
    {
        var isEnabled = State.ExperimentalTodoReminders;
        var container = new Border
        {
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 4, 0, 0),
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = isEnabled,
            Opacity = isEnabled ? 1.0 : 0.55
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var valueText = new TextBlock
        {
            Text = Strings.Format(
                "TodoReminderMinutesValueFormat",
                State.ExperimentalTodoReminderQuickMinutes),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = AppTypography.Scale(13),
            FontWeight = FontWeights.SemiBold,
            Foreground = TrayTextBrush
        };
        Grid.SetColumn(valueText, 1);

        Border StepButton(string glyph, int column, int delta)
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
                Cursor = Cursors.Hand,
                Child = glyphText
            };
            button.MouseEnter += (_, _) => button.Background = TrayHoverBrush;
            button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
            button.MouseLeftButtonDown += (_, e) =>
            {
                SetExperimentalTodoReminderQuickMinutes(
                    State.ExperimentalTodoReminderQuickMinutes + delta);
                e.Handled = true;
            };
            Grid.SetColumn(button, column);
            return button;
        }

        grid.Children.Add(StepButton(
            "−",
            0,
            -ExperimentalTodoReminderOptions.QuickMinutesStep));
        grid.Children.Add(valueText);
        grid.Children.Add(StepButton(
            "＋",
            2,
            ExperimentalTodoReminderOptions.QuickMinutesStep));
        container.Child = grid;
        return container;
    }

    private UIElement CreateTodoReminderSoundSelector()
    {
        var choices = new[]
        {
            (TodoReminderSoundOptions.Asterisk,
                Strings.Get("TodoReminderSoundAsterisk")),
            (TodoReminderSoundOptions.Beep,
                Strings.Get("TodoReminderSoundBeep")),
            (TodoReminderSoundOptions.Exclamation,
                Strings.Get("TodoReminderSoundExclamation")),
            (TodoReminderSoundOptions.Hand,
                Strings.Get("TodoReminderSoundHand")),
            (TodoReminderSoundOptions.Question,
                Strings.Get("TodoReminderSoundQuestion"))
        };
        var selectedKey = TodoReminderSoundOptions.Normalize(
            State.ExperimentalTodoReminderSound);
        var combo = new ComboBox
        {
            Height = AppTypography.FitChrome(28),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Focusable = false
        };
        PaperSelectControl.ApplyAppTheme(
            combo,
            AppTypography.Scale(12));

        ComboBoxItem? selected = null;
        foreach (var (key, label) in choices)
        {
            var item = new ComboBoxItem
            {
                Tag = key,
                Content = label
            };
            combo.Items.Add(item);
            if (string.Equals(key, selectedKey, StringComparison.Ordinal))
            {
                selected = item;
            }
        }
        combo.SelectedItem = selected ?? combo.Items[0];
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string key })
            {
                SetExperimentalTodoReminderSound(key);
            }
        };
        return combo;
    }

    private UIElement BuildLabsMcpSettings()
    {
        var card = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 3, 0, 5),
            Margin = new Thickness(0, 1, 0, 3)
        };
        var content = new StackPanel();
        content.Children.Add(WrapWithHint(
            SettingsToggle(
                Strings.Get("LabsMcpEnable"),
                State.McpEnabled,
                ToggleMcpEnabled),
            "TipLabsMcpEnable"));
        var blankWrites = SettingsToggle(
            Strings.Get("LabsMcpBlankWrites"),
            State.McpAllowBlankWrites || State.McpAllowFullWrites,
            ToggleMcpBlankWrites);
        blankWrites.IsEnabled = !State.McpAllowFullWrites;
        content.Children.Add(WrapWithHint(
            blankWrites,
            "TipLabsMcpBlankWrites"));
        content.Children.Add(WrapWithHint(
            SettingsToggle(
                Strings.Get("LabsMcpFullWrites"),
                State.McpAllowFullWrites,
                ToggleMcpFullWrites),
            "TipLabsMcpFullWrites"));
        var directDeletes = SettingsToggle(
            Strings.Get("LabsMcpDeletes"),
            State.McpAllowDeletes,
            ToggleMcpDeletes);
        if (State.McpAllowDeletes)
        {
            directDeletes.Foreground = Theme.DangerBrush;
        }
        content.Children.Add(WrapWithHint(
            directDeletes,
            "TipLabsMcpDeletes"));

        var status = new TextBlock
        {
            Text = State.McpEnabled
                ? Strings.Get("LabsMcpStatusReady")
                : Strings.Get("LabsMcpStatusDisabled"),
            Foreground = State.McpEnabled
                ? Theme.ActiveBrush
                : TrayWeakTextBrush,
            FontSize = AppTypography.Scale(11.5),
            Margin = new Thickness(2, 8, 2, 0)
        };
        content.Children.Add(status);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 9, 0, 0)
        };
        var copyJson = SettingsTextButton(
            Strings.Get("LabsMcpCopyJson"));
        var copySkill = SettingsTextButton(
            Strings.Get("LabsMcpCopyAiSkill"));
        copySkill.Margin = new Thickness(8, 0, 0, 0);
        var feedback = new TextBlock
        {
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(11),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 0, 0)
        };
        copyJson.Click += (_, _) =>
        {
            feedback.Text = ClipboardHelper.TrySetText(
                BuildJsonMcpConfiguration())
                ? Strings.Get("LabsMcpCopied")
                : Strings.Get("LabsMcpCopyFailed");
        };
        copySkill.Click += (_, _) =>
        {
            feedback.Text = ClipboardHelper.TrySetText(
                BuildAiMcpSkill())
                ? Strings.Get("LabsMcpCopied")
                : Strings.Get("LabsMcpCopyFailed");
        };
        actions.Children.Add(copyJson);
        actions.Children.Add(copySkill);
        actions.Children.Add(feedback);
        content.Children.Add(actions);
        card.Child = content;
        return card;
    }

    private UIElement CreateLabsPercentageStepper(
        Func<double> getValue,
        Action<double> setValue,
        bool isEnabled)
    {
        var container = new Border
        {
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 4, 0, 10),
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = isEnabled,
            Opacity = isEnabled ? 1.0 : 0.55
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var valueText = new TextBlock
        {
            Text = $"{Math.Round(getValue() * 100):0}%",
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
                Cursor = Cursors.Hand,
                Child = glyphText
            };
            button.MouseEnter += (_, _) => button.Background = TrayHoverBrush;
            button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
            button.MouseLeftButtonDown += (_, e) =>
            {
                setValue(getValue() + delta);
                e.Handled = true;
            };
            Grid.SetColumn(button, column);
            return button;
        }

        grid.Children.Add(StepButton("−", 0, -ExperimentalOpacityLevels.Step));
        grid.Children.Add(valueText);
        grid.Children.Add(StepButton("＋", 2, ExperimentalOpacityLevels.Step));
        container.Child = grid;
        return container;
    }

    private void RestoreLabsSettingsPageDefaults()
    {
        State.ExperimentalInactivePaperOpacity = false;
        State.ExperimentalInactivePaperOpacityLevel =
            ExperimentalOpacityLevels.DefaultInactivePaper;
        State.ExperimentalRestingCapsuleOpacity = false;
        State.ExperimentalRestingCapsuleOpacityLevel =
            ExperimentalOpacityLevels.DefaultRestingCapsule;
        State.ExperimentalRestingCapsuleOpacityIncludesMaster = false;
        State.ExperimentalRestingCapsuleOpacityAlways = false;
        State.ExperimentalCollapsePaperOnDeactivate = false;
        State.ExperimentalHideInactiveTopBarButtons = false;
        State.ExperimentalHideInactiveTitleBar = false;
        State.ExperimentalDockedCapsulesNonTopmost = false;
        State.ExperimentalEdgeCapsuleHoverPreview = true;
        State.ExperimentalEdgeCapsuleHoverIntent = true;
        State.ExperimentalEdgeCapsuleHoverIntentSensitivity =
            EdgeCapsuleHoverIntentSensitivities.Medium;
        State.ExperimentalAllowLockIconUnlock = true;
        State.ExperimentalShortcutOpacityLevel = 0.35;
        ClearAdvancedShortcutRuntimeState();
        State.ExperimentalTodoReminders = false;
        State.ExperimentalTodoReminderShowButton = true;
        State.ExperimentalTodoReminderQuickMinutes =
            ExperimentalTodoReminderOptions.DefaultQuickMinutes;
        State.ExperimentalTodoReminderSoundEnabled = false;
        State.ExperimentalTodoReminderSound =
            TodoReminderSoundOptions.Asterisk;
        State.McpEnabled = false;
        State.McpAllowBlankWrites = false;
        State.McpAllowFullWrites = false;
        State.McpAllowDeletes = false;
        State.ExperimentalCapsuleMagnetism = false;
        State.ExperimentalCapsuleMagnetScreenEdges = true;
        State.ExperimentalCapsuleMagnetWindowEdges = true;
        State.ExperimentalCapsuleMagnetDistance =
            ExperimentalWindowAttachmentOptions.DefaultSnapDistance;
        State.ExperimentalWindowTethering = false;
        State.ExperimentalWindowTetherPreferredEdge =
            ExperimentalWindowTetherOptions.Auto;
        State.ExperimentalWindowTetherGap =
            ExperimentalWindowTetherOptions.DefaultGap;
        State.ExperimentalTetherVisibilityLink = false;
        State.ExperimentalTetherMinimizedBehavior =
            ExperimentalTetherVisibilityModes.Hide;
        RestoreLabsShortcutDefaults();

        foreach (var window in _windows.Values.ToList())
        {
            window.RefreshDeepCapsuleSlotTopmost();
            window.DisableExperimentalCapsuleMagnet();
            window.DisableExperimentalTetherVisibilityLink();
            window.DisableExperimentalWindowTether();
        }
        foreach (var master in _masterCapsules.Values.ToList())
        {
            master.RefreshEffectiveTopmost();
        }
        RefreshExperimentalWindowRuntime();
        RefreshEdgeCapsuleHoverIntentRuntime();
        RefreshMcpRuntime();
        SaveNow();
        RefreshExperimentalOpacitySurfaces(animate: false);
        RefreshExperimentalFocusPresentationSurfaces();
        RefreshTodoReminderFeature();
        RefreshSettingsWindowContent();
    }

    private UIElement BuildGeneralSettingsPage()
    {
        _settingsExternalMarkdownTextBox = null;
        _settingsHidePapersFromTaskbarCheckBox = null;
        _settingsHidePapersFromWindowSwitcherCheckBox = null;
        _settingsCapsuleModeCheckBox = null;
        _settingsDeepCapsuleModeCheckBox = null;
        _settingsDeepCapsuleExpandedSlotCheckBox = null;
        _settingsRememberDeepCapsuleExpandedPositionCheckBox = null;
        _settingsCollapseExpandedDeepCapsuleOnClickCheckBox = null;
        _settingsCapsuleCollapseAllCheckBox = null;

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

        var advanced = State.AdvancedSettingsMode;

        // Left: everyday desktop / window behavior. Right: paper features, capsule first.
        leftColumn.Children.Add(SettingsSectionLabel(Strings.Get("SettingsGeneral")));
        leftColumn.Children.Add(CreateUiLanguageSettingsRow());
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("TrayStartup"), SystemSettingsHelper.IsStartupEnabled(), ToggleStartup), "TipStartup"));
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsEnableToolTips"), State.EnableToolTips, ToggleToolTips), "TipEnableToolTips"));
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsEnableAnimations"), State.EnableAnimations, ToggleAnimations), "TipEnableAnimations"));
        if (advanced)
        {
            _settingsHidePapersFromTaskbarCheckBox = MarkAdvancedSetting(SettingsToggle(Strings.Get("SettingsHidePapersFromTaskbar"), State.HidePapersFromTaskbar, ToggleHidePapersFromTaskbar));
            _settingsHidePapersFromWindowSwitcherCheckBox = MarkAdvancedSetting(SettingsToggle(Strings.Get("SettingsHidePapersFromWindowSwitcher"), State.HidePapersFromWindowSwitcher, ToggleHidePapersFromWindowSwitcher));
            leftColumn.Children.Add(AdvancedSettingsBlock(
                WrapWithHint(_settingsHidePapersFromTaskbarCheckBox, "TipHidePapersFromTaskbar"),
                WrapWithHint(_settingsHidePapersFromWindowSwitcherCheckBox, "TipHidePapersFromWindowSwitcher"),
                CompactSettingsField(
                    Strings.Get("SettingsFullscreenTopmostMode"),
                    CreateFullscreenTopmostModeSegmentSelector(),
                    editorWidth: 156,
                    tipKey: "TipFullscreenTopmostMode",
                    topMargin: 8)));
        }

        leftColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("TrayMarkdownRenderMode"), topMargin: 8), "TipMarkdownRender"));
        leftColumn.Children.Add(CreateMarkdownRenderSegmentSelector());

        leftColumn.Children.Add(SettingsSectionLabel(Strings.Get("SettingsTopBarButtons")));
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsShowTopBarNewTodoButton"), State.ShowTopBarNewTodoButton, ToggleTopBarNewTodoButton), "TipNewTodoButton"));
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsShowTopBarNewNoteButton"), State.ShowTopBarNewNoteButton, ToggleTopBarNewNoteButton), "TipNewNoteButton"));
        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsShowTopBarExternalOpenButton"), State.ShowTopBarExternalOpenButton, ToggleTopBarExternalOpenButton), "TipExternalOpenButton"));

        leftColumn.Children.Add(SettingsSectionLabel(Strings.Get("SettingsExternalOpen")));
        leftColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsExternalMarkdownExtension")), "TipExternalExtension"));
        leftColumn.Children.Add(CreateExternalMarkdownExtensionEditor());

        if (advanced)
        {
            // Keep script options on the shorter left column so they stay visible without scrolling.
            leftColumn.Children.Add(AdvancedSettingsBlock(
                SettingsSectionLabel(Strings.Get("SettingsScriptCapsule")),
                WrapWithHint(MarkAdvancedSetting(SettingsToggle(Strings.Get("SettingsPersistentPowerShellProcess"), State.UsePersistentPowerShellProcess, TogglePersistentPowerShellProcess)), "TipPersistentPowerShellProcess"),
                WrapWithHint(MarkAdvancedSetting(SettingsToggle(Strings.Get("SettingsPreferPowerShell7"), State.PreferPowerShell7, TogglePreferPowerShell7)), "TipPreferPowerShell7"),
                WrapWithHint(MarkAdvancedSetting(SettingsToggle(Strings.Get("SettingsHideScriptRunWindow"), State.HideScriptRunWindow, ToggleHideScriptRunWindow)), "TipHideScriptRunWindow")));
        }

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
        // Master-capsule control sits one slot above "collapse expanded on click".
        rightColumn.Children.Add(WrapWithHint(_settingsCapsuleCollapseAllCheckBox, "TipCapsuleCollapseAll"));
        rightColumn.Children.Add(WrapWithHint(_settingsCollapseExpandedDeepCapsuleOnClickCheckBox, "TipCollapseExpandedDeepCapsuleOnClick"));
        RefreshSettingsCapsuleToggleStates();
        if (advanced)
        {
            rightColumn.Children.Add(AdvancedSettingsBlock(
                WrapWithHint(
                    MarkAdvancedSetting(SettingsToggle(
                        Strings.Get("SettingsHideEdgeCapsuleCloseButtonOnHover"),
                        State.HideEdgeCapsuleCloseButtonOnHover,
                        ToggleHideEdgeCapsuleCloseButtonOnHover)),
                    "TipHideEdgeCapsuleCloseButtonOnHover"),
                CompactSettingsField(
                    Strings.Get("SettingsMaxTitleLength"),
                    CreateMaxTitleLengthStepper(),
                    editorWidth: 132,
                    tipKey: "TipMaxTitleLength",
                    topMargin: 8),
                CompactSettingsField(
                    Strings.Get("SettingsDeepCapsuleTitleMeasureLimit"),
                    CreateDeepCapsuleTitleMeasureLimitStepper(),
                    editorWidth: 132,
                    tipKey: "TipDeepCapsuleTitleMeasureLimit",
                    topMargin: 8)));
        }

        rightColumn.Children.Add(BuildSettingsLiveRegion(
            "general.todos",
            BuildGeneralTodoPaperSettings));
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

        return WithSettingsPageRestoreFooter(columns, RestoreGeneralSettingsPageDefaults);
    }

    private UIElement BuildGeneralTodoPaperSettings()
    {
        var content = new StackPanel();
        content.Children.Add(SettingsSectionLabel(Strings.Get("SettingsTodoPaper")));
        if (State.AdvancedSettingsMode)
        {
            content.Children.Add(AdvancedSettingsBlock(
                WrapWithHint(
                    MarkAdvancedSetting(SettingsToggle(
                        Strings.Get("SettingsAutoCompressLargeImages"),
                        State.AutoCompressLargeImages,
                        ToggleAutoCompressLargeImages)),
                    "TipAutoCompressLargeImages")));
        }

        content.Children.Add(WrapWithHint(
            SettingsToggle(
                Strings.Get("SettingsAutoClearCompletedTodos"),
                State.AutoClearCompletedTodos,
                ToggleAutoClearCompletedTodos),
            "TipAutoClearCompletedTodos"));

        var autoMoveCompletedToggle = SettingsToggle(
            Strings.Get("SettingsAutoMoveCompletedTodosToBottom"),
            State.AutoMoveCompletedTodosToBottom,
            ToggleAutoMoveCompletedTodosToBottom);
        autoMoveCompletedToggle.IsEnabled = !State.AutoClearCompletedTodos;
        autoMoveCompletedToggle.Opacity =
            autoMoveCompletedToggle.IsEnabled ? 1.0 : 0.55;
        content.Children.Add(WrapWithHint(
            autoMoveCompletedToggle,
            "TipAutoMoveCompletedTodosToBottom"));

        content.Children.Add(WrapWithHint(
            SettingsToggle(
                Strings.Get("SettingsEnableTodoPaperLinks"),
                State.EnableTodoPaperLinks,
                ToggleTodoPaperLinks),
            "TipEnableTodoPaperLinks"));

        var showLinkedPaperNameToggle = SettingsToggle(
            Strings.Get("SettingsShowLinkedPaperName"),
            State.ShowLinkedPaperName,
            ToggleLinkedPaperNameDisplay);
        showLinkedPaperNameToggle.IsEnabled = State.EnableTodoPaperLinks;
        content.Children.Add(WrapWithHint(
            showLinkedPaperNameToggle,
            "TipShowLinkedPaperName"));

        var allowLongLinkedPaperTitlesToggle = SettingsToggle(
            Strings.Get("SettingsAllowLongLinkedPaperTitles"),
            State.AllowLongLinkedPaperTitles,
            ToggleLongLinkedPaperTitles);
        allowLongLinkedPaperTitlesToggle.IsEnabled =
            State.EnableTodoPaperLinks && State.ShowLinkedPaperName;
        content.Children.Add(WrapWithHint(
            allowLongLinkedPaperTitlesToggle,
            "TipAllowLongLinkedPaperTitles"));

        var linkedPathExtensionOnlyToggle = SettingsToggle(
            Strings.Get("SettingsShowLinkedPathExtensionOnly"),
            State.ShowLinkedPathExtensionOnly,
            ToggleLinkedPathExtensionOnly);
        linkedPathExtensionOnlyToggle.IsEnabled =
            State.EnableTodoPaperLinks &&
            State.ShowLinkedPaperName &&
            !State.AllowLongLinkedPaperTitles;
        content.Children.Add(WrapWithHint(
            linkedPathExtensionOnlyToggle,
            "TipShowLinkedPathExtensionOnly"));

        var hideLinkedPapersFromCapsulesToggle = SettingsToggle(
            Strings.Get("SettingsHideLinkedPapersFromCapsules"),
            State.HideLinkedPapersFromCapsules,
            ToggleHideLinkedPapersFromCapsules);
        hideLinkedPapersFromCapsulesToggle.IsEnabled = State.EnableTodoPaperLinks;
        content.Children.Add(WrapWithHint(
            hideLinkedPapersFromCapsulesToggle,
            "TipHideLinkedPapersFromCapsules"));

        var runLinkedScriptCapsulesToggle = SettingsToggle(
            Strings.Get("SettingsRunLinkedScriptCapsulesOnClick"),
            State.RunLinkedScriptCapsulesOnClick,
            ToggleRunLinkedScriptCapsulesOnClick);
        runLinkedScriptCapsulesToggle.IsEnabled = State.EnableTodoPaperLinks;
        content.Children.Add(WrapWithHint(
            runLinkedScriptCapsulesToggle,
            "TipRunLinkedScriptCapsulesOnClick"));
        return content;
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
        leftColumn.Children.Add(WrapWithHint(
            SettingsFieldLabel(Strings.Get("SettingsResizeGripMode")),
            "TipResizeGripMode"));
        leftColumn.Children.Add(CreateResizeGripModeSegmentSelector());
        leftColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsDeepCapsuleGap")), "TipDeepCapsuleGap"));
        leftColumn.Children.Add(CreateDeepCapsuleGapSegmentSelector());
        leftColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsUiFont")), "TipUiFont"));
        leftColumn.Children.Add(CreateUiFontPresetSegmentSelector());
        leftColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsTextRenderingProfile")), "TipTextRenderingProfile"));
        leftColumn.Children.Add(CreateTextRenderingProfileSegmentSelector());
        var customBoldToggle = SettingsToggle(
            Strings.Get("SettingsCustomFontEnhancedBold"),
            State.CustomFontEnhancedBold,
            ToggleCustomFontEnhancedBold);
        // Only meaningful with papertodo + papertodo_bold (or PaperTodo_Bold) beside the exe.
        // Keep an already-enabled setting clickable when a file disappears so the user can turn
        // it off; AppTypography itself refuses to mix a system regular face with a custom bold one.
        customBoldToggle.IsEnabled =
            (AppTypography.HasCustomFont && AppTypography.HasCustomBoldFont) ||
            State.CustomFontEnhancedBold;
        leftColumn.Children.Add(WrapWithHint(customBoldToggle, "TipCustomFontEnhancedBold"));
        leftColumn.Children.Add(CompactSettingsField(
            Strings.Get("SettingsOverallFontScale"),
            CreateOverallFontScaleStepper(),
            editorWidth: 132,
            tipKey: "TipOverallFontScale",
            topMargin: 7));
        if (State.AdvancedSettingsMode)
        {
            leftColumn.Children.Add(AdvancedSettingsBlock(
                WrapWithHint(
                    MarkAdvancedSetting(SettingsFieldLabel(Strings.Get("SettingsImageReferenceText"), topMargin: 8)),
                    "TipImageReferenceText"),
                CreateImageReferenceTextModeSelector()));
        }

        void AddTextStyleEditor(
            StackPanel column,
            string sectionKey,
            string tipKey,
            UIElement sizeSelector,
            bool isBold,
            Action toggleBold,
            bool leadingDivider)
        {
            if (leadingDivider)
            {
                column.Children.Add(SettingsSoftDivider());
            }

            column.Children.Add(CreateTextStyleRow(
                Strings.Get(sectionKey),
                tipKey,
                sizeSelector,
                isBold,
                toggleBold));
        }

        AddTextStyleEditor(
            rightColumn,
            "SettingsNoteBodyText",
            "TipNoteBodyTextStyle",
            CreateVisualTextSizeSelector(State.NoteTextSize, SetNoteTextSize),
            State.NoteTextBold,
            ToggleNoteTextBold,
            leadingDivider: false);

        AddTextStyleEditor(
            rightColumn,
            "SettingsTodoBodyText",
            "TipTodoBodyTextStyle",
            CreateTodoVisualSizeSelector(),
            State.TodoTextBold,
            ToggleTodoTextBold,
            leadingDivider: true);

        AddTextStyleEditor(
            rightColumn,
            "SettingsTitleText",
            "TipTitleTextStyle",
            CreateVisualTextSizeSelector(State.TitleTextSize, SetTitleTextSize),
            State.TitleTextBold,
            ToggleTitleTextBold,
            leadingDivider: true);
        AddTextStyleEditor(
            rightColumn,
            "SettingsCapsuleText",
            "TipCapsuleTextStyle",
            CreateVisualTextSizeSelector(State.CapsuleTextSize, SetCapsuleTextSize),
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

        return WithSettingsPageRestoreFooter(columns, RestoreVisualSettingsPageDefaults);
    }

    private UIElement WithSettingsPageRestoreFooter(UIElement content, Action restorePageDefaults)
    {
        var root = new DockPanel
        {
            LastChildFill = true
        };

        var actions = new Grid
        {
            Margin = new Thickness(0, 14, 2, 4)
        };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var restore = SettingsTextButton(Strings.Get("SettingsRestorePageDefaults"));
        restore.MinWidth = 108;
        restore.Padding = new Thickness(18, 0, 18, 0);
        restore.Click += (_, _) => restorePageDefaults();
        Grid.SetColumn(restore, 1);
        actions.Children.Add(restore);

        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
        root.Children.Add(content);
        return root;
    }

    private void RestoreGeneralSettingsPageDefaults()
    {
        // Only fields on the Behavior page; does not touch visual/theme or hotkeys.
        State.HidePapersFromTaskbar = true;
        State.HidePapersFromWindowSwitcher = true;
        State.EnableToolTips = true;
        State.EnableAnimations = true;
        State.UiLanguage = UiLanguages.Default;
        State.FullscreenTopmostMode = FullscreenTopmostModes.Avoid;
        State.MarkdownRenderMode = MarkdownRenderModes.Enhanced;
        State.ShowTopBarNewTodoButton = true;
        State.ShowTopBarNewNoteButton = true;
        State.ShowTopBarExternalOpenButton = true;
        State.ExternalMarkdownExtension = ExternalMarkdownFileExtensions.Default;
        State.UsePersistentPowerShellProcess = false;
        State.PreferPowerShell7 = true;
        State.HideScriptRunWindow = true;
        State.UseCapsuleMode = true;
        State.UseDeepCapsuleMode = true;
        State.ShowDeepCapsuleWhileExpanded = true;
        State.HideEdgeCapsuleCloseButtonOnHover = false;
        State.RememberDeepCapsuleExpandedPosition = true;
        State.UseCapsuleCollapseAll = true;
        State.CollapseExpandedDeepCapsuleOnClick = false;
        State.MaxTitleLength = PaperTitles.DefaultMaxTitleLength;
        State.DeepCapsuleTitleMeasureCharacterLimit = 0;
        State.AutoCompressLargeImages = true;
        State.AutoClearCompletedTodos = false;
        State.AutoMoveCompletedTodosToBottom = false;
        State.EnableTodoPaperLinks = true;
        State.ShowLinkedPaperName = false;
        State.AllowLongLinkedPaperTitles = false;
        State.ShowLinkedPathExtensionOnly = false;
        State.HideLinkedPapersFromCapsules = false;
        State.RunLinkedScriptCapsulesOnClick = false;
        NormalizePaperSystemVisibilitySettings();
        ClampPaperTitlesToMaxLength(State.MaxTitleLength);
        _imageStore.AutoCompressLargeImages = State.AutoCompressLargeImages;

        if (!State.UsePersistentPowerShellProcess)
        {
            PaperWindow.StopPersistentScriptProcesses();
        }

        SaveNow();
        ApplyGeneralSettingsAfterRestore();
        RefreshSettingsWindowContent();
    }

    private void RestoreVisualSettingsPageDefaults()
    {
        // Theme lives on the visual page with color scheme / fonts.
        State.Theme = "system";
        State.ColorScheme = ColorSchemes.Warm;
        State.UiFontPreset = UiFontPresets.Default;
        State.TextRenderingProfile = TextRenderingProfiles.Standard;
        State.CustomFontEnhancedBold = false;
        State.Zoom = 1.0;
        State.ImageReferenceTextMode = ImageReferenceTextModes.Always;
        State.NoteTextSize = VisualTextSizes.Medium;
        State.NoteTextBold = false;
        State.TodoVisualSize = TodoVisualSizes.Medium;
        State.TodoTextBold = false;
        State.TitleTextSize = VisualTextSizes.Medium;
        State.TitleTextBold = true;
        State.CapsuleTextSize = VisualTextSizes.Medium;
        State.CapsuleTextBold = false;
        State.ResizeGripMode = ResizeGripModes.Soft;
        State.DeepCapsuleGapSize = DeepCapsuleGapSizes.Standard;

        AppTypography.Configure(
            State.UiFontPreset,
            State.Zoom,
            State.CustomFontEnhancedBold,
            State.TextRenderingProfile);
        NoteTypography.Configure(State.NoteTextSize, State.NoteTextBold);
        foreach (var window in _windows.Values)
        {
            window.UpdateImageReferenceTextMode();
        }

        SaveNow();
        ApplyTypographySettingsChange();
        RefreshThemeSurfaces();
    }

    private void ApplyGeneralSettingsAfterRestore()
    {
        var windows = _windows.Values.ToList();
        foreach (var window in windows)
        {
            window.PrepareForCapsulePresentationModeChange();
        }

        RefreshPaperSystemVisibility(reapplyTaskbarShellState: true);
        RefreshTopBarNewPaperButtonsSetting();
        RefreshTopmostForForegroundWindow();

        foreach (var window in windows)
        {
            window.UpdateMarkdownRenderMode();
            window.UpdateExternalMarkdownExtension();
            window.UpdateTodoLinkFeature();
            window.RefreshPaperTitle();
            window.UpdateCapsuleMode();
            window.UpdateDeepCapsuleMode();
            window.UpdateDeepCapsuleExpandedSlotMode();
            window.UpdateEdgeCapsuleCloseButtonMode();
        }

        ArrangeDeepCapsules(animate: false);
        RebuildTrayMenu();
        RefreshToolTipSetting();
    }

    private UIElement CreateSettingsPageSelector()
    {
        const string generalKey = "general";
        const string visualKey = "visual";
        const string shortcutsKey = "shortcuts";
        const string pluginsKey = "plugins";
        const string labsKey = "labs";
        var segments = new List<(string Key, string Label)>
        {
            (Key: generalKey, Label: Strings.Get("SettingsBehavior")),
            (Key: visualKey, Label: Strings.Get("SettingsVisual")),
            (Key: shortcutsKey, Label: Strings.Get("SettingsShortcuts")),
            (Key: pluginsKey, Label: Strings.Get("SettingsPlugins"))
        };
        if (State.AdvancedSettingsMode)
        {
            segments.Add((Key: labsKey, Label: Strings.Get("SettingsLabs")));
        }

        var activeKey = _settingsPage switch
        {
            SettingsPage.Visual => visualKey,
            SettingsPage.Shortcuts => shortcutsKey,
            SettingsPage.Plugins => pluginsKey,
            SettingsPage.Labs when State.AdvancedSettingsMode => labsKey,
            _ => generalKey
        };

        // Premium main segmented capsule container
        var container = new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = TrayHoverBrush, // Sunken tab track background
            Margin = new Thickness(0),
            Height = 24,
            Width = segments.Count * (segments.Count >= 5 ? 68 : 76),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var grid = new Grid();
        foreach (var _ in segments)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (int i = 0; i < segments.Count; i++)
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
                    pluginsKey => SettingsPage.Plugins,
                    labsKey => SettingsPage.Labs,
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
            Padding = new Thickness(0, 0, enableScroll ? 4 : 0, enableScroll ? 28 : 24),
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
            // Preserve the pre-Labs sizing rule: only the original three pages
            // determine the settings-window frame. Labs receives this fixed viewport.
            var pages = new[]
            {
                SettingsPage.General,
                SettingsPage.Visual,
                SettingsPage.Shortcuts
            };
            foreach (var page in pages)
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

    private double SettingsWindowWidth()
    {
        return SettingsContentWidth() + 32;
    }

    private double SettingsContentWidth()
    {
        var availableWidth = WindowWorkAreaHelper.WorkAreaFor(_settingsWindow).Width - 96;
        // Slightly under the previous 540–640 frame for a denser settings window.
        return Math.Clamp(availableWidth, 520, 620);
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

    private UIElement CompactSettingsField(
        string labelText,
        UIElement editor,
        double editorWidth = 136,
        string? tipKey = null,
        double topMargin = 7)
    {
        var row = new Grid
        {
            Margin = new Thickness(0, topMargin, 0, 2)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });

        var labelHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        labelHost.Children.Add(new TextBlock
        {
            Text = labelText,
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(11),
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (!string.IsNullOrWhiteSpace(tipKey))
        {
            labelHost.Children.Add(CreateSettingsHintGlyph(
                tipKey,
                new Thickness(4, 0, 0, 0)));
        }
        Grid.SetColumn(labelHost, 0);
        row.Children.Add(labelHost);

        if (editor is FrameworkElement element)
        {
            element.Width = editorWidth;
            element.Margin = new Thickness(10, 0, 0, 0);
            element.HorizontalAlignment = HorizontalAlignment.Right;
            element.VerticalAlignment = VerticalAlignment.Center;
        }
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        return row;
    }

    private UIElement CompactSettingsToggleField(
        string labelText,
        bool isChecked,
        Action onToggle,
        string tipKey,
        UIElement editor,
        double editorWidth = 132,
        double topMargin = 4)
    {
        var row = new Grid
        {
            Margin = new Thickness(0, topMargin, 0, 2)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });

        var toggle = SettingsToggle(labelText, isChecked, onToggle);
        toggle.Margin = new Thickness(0);
        var toggleHost = WrapWithHint(toggle, tipKey);
        if (toggleHost is FrameworkElement toggleElement)
        {
            toggleElement.Margin = new Thickness(0);
            toggleElement.VerticalAlignment = VerticalAlignment.Center;
        }
        Grid.SetColumn(toggleHost, 0);
        row.Children.Add(toggleHost);

        if (editor is FrameworkElement element)
        {
            element.Width = editorWidth;
            element.Margin = new Thickness(10, 0, 0, 0);
            element.HorizontalAlignment = HorizontalAlignment.Right;
            element.VerticalAlignment = VerticalAlignment.Center;
        }
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        return row;
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

    private UIElement BuildSettingsLiveRegion(
        string key,
        Func<UIElement> build)
    {
        var host = new ContentPresenter
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        void Refresh() => host.Content = build();
        _settingsRegionRefreshers[key] = Refresh;
        Refresh();
        return host;
    }

    private void RefreshSettingsRegions(params string[] keys)
    {
        if (_settingsWindow is not { IsVisible: true })
        {
            return;
        }

        var distinctKeys = keys
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (!distinctKeys.Any(_settingsRegionRefreshers.ContainsKey))
        {
            return;
        }

        _ = _settingsWindow.Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                foreach (var key in distinctKeys)
                {
                    if (_settingsRegionRefreshers.TryGetValue(key, out var refresh))
                    {
                        refresh();
                    }
                }
            }),
            DispatcherPriority.Background);
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
    }

    private void SetResizeGripMode(string mode)
    {
        var normalized = ResizeGripModes.Normalize(mode);
        if (State.ResizeGripMode == normalized)
        {
            return;
        }

        State.ResizeGripMode = normalized;
        SaveNow();
        RefreshApplicationThemeResources();
    }

    private UIElement CreateResizeGripModeSegmentSelector()
    {
        var segments = new[]
        {
            (ResizeGripModes.Standard, Strings.Get("ResizeGripModeStandard")),
            (ResizeGripModes.Soft, Strings.Get("ResizeGripModeSoft")),
            (ResizeGripModes.Hidden, Strings.Get("ResizeGripModeHidden"))
        };

        return CreateSegmentSelector(
            segments,
            ResizeGripModes.Normalize(State.ResizeGripMode),
            SetResizeGripMode);
    }

    private void ToggleAutoClearCompletedTodos()
    {
        State.AutoClearCompletedTodos = !State.AutoClearCompletedTodos;
        SaveNow();
        RefreshSettingsRegions("general.todos");
    }

    private void ToggleAutoMoveCompletedTodosToBottom()
    {
        State.AutoMoveCompletedTodosToBottom =
            !State.AutoMoveCompletedTodosToBottom;

        if (State.AutoMoveCompletedTodosToBottom)
        {
            foreach (var paper in State.Papers.Where(
                         paper => paper.Type == PaperTypes.Todo))
            {
                var ordered = paper.Items
                    .OrderBy(item => item.Order)
                    .ToList();
                var regrouped = ordered
                    .Where(item => !item.Done)
                    .Concat(ordered.Where(item => item.Done))
                    .ToList();
                if (ordered.Select(item => item.Id)
                    .SequenceEqual(regrouped.Select(item => item.Id)))
                {
                    continue;
                }

                paper.Items = regrouped;
                TodoRules.NormalizeOrders(paper.Items);
                if (_windows.TryGetValue(paper.Id, out var window))
                {
                    window.RefreshTodoRowsForExternalChange();
                }
            }
        }

        SaveNow();
        RefreshSettingsRegions("general.todos");
    }

    private void ToggleAutoCompressLargeImages()
    {
        State.AutoCompressLargeImages = !State.AutoCompressLargeImages;
        _imageStore.AutoCompressLargeImages = State.AutoCompressLargeImages;
        SaveNow();
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
    }

    private void TogglePreferPowerShell7()
    {
        State.PreferPowerShell7 = !State.PreferPowerShell7;
        PaperWindow.StopPersistentScriptProcesses();
        PaperWindow.EnsurePersistentScriptProcessForSettings(State);
        SaveNow();
    }

    private void ToggleHideScriptRunWindow()
    {
        State.HideScriptRunWindow = !State.HideScriptRunWindow;
        PaperWindow.StopPersistentScriptProcesses();
        PaperWindow.EnsurePersistentScriptProcessForSettings(State);
        SaveNow();
    }

    private void ToggleToolTips()
    {
        State.EnableToolTips = !State.EnableToolTips;
        SaveNow();
        RefreshToolTipSetting();
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
        var windows = _windows.Values.ToList();
        foreach (var window in windows)
        {
            window.PrepareForCapsulePresentationModeChange();
        }

        State.UseCapsuleMode = !State.UseCapsuleMode;

        if (!State.UseCapsuleMode)
        {
            State.UseDeepCapsuleMode = false;
            // Preserve the user's "show master capsule" preference. Disabling capsule mode only
            // clears live collapse state; the dependent setting remains checked and disabled.
            State.CapsuleCollapseAllActiveQueues.Clear();
            ResetDeepCapsuleStartTopMargins();
        }

        // Keep IsCollapsed intact until each live window has consumed the mode change.
        // UpdateCapsuleMode uses that state to perform the capsule-to-paper visual transition.
        foreach (var window in windows)
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

    private void ToggleLinkedPaperNameDisplay()
    {
        State.ShowLinkedPaperName = !State.ShowLinkedPaperName;

        foreach (var window in _windows.Values)
        {
            window.RefreshTodoRowsForExternalChange();
        }

        SaveNow();
        RefreshSettingsRegions("general.todos");
    }

    private void ToggleLongLinkedPaperTitles()
    {
        State.AllowLongLinkedPaperTitles = !State.AllowLongLinkedPaperTitles;

        foreach (var window in _windows.Values)
        {
            window.RefreshTodoRowsForExternalChange();
        }

        SaveNow();
        RefreshSettingsRegions("general.todos");
    }

    private void ToggleHideLinkedPapersFromCapsules()
    {
        State.HideLinkedPapersFromCapsules = !State.HideLinkedPapersFromCapsules;
        RefreshCapsuleEligibilityForLinkedPapers();
        SaveNow();
        RefreshSettingsRegions("general.todos");
    }

    private void ToggleRunLinkedScriptCapsulesOnClick()
    {
        State.RunLinkedScriptCapsulesOnClick = !State.RunLinkedScriptCapsulesOnClick;

        foreach (var window in _windows.Values)
        {
            window.RefreshTodoRowsForExternalChange();
        }

        SaveNow();
        RefreshSettingsRegions("general.todos");
    }

    private void ToggleTodoPaperLinks()
    {
        State.EnableTodoPaperLinks = !State.EnableTodoPaperLinks;
        ClearPaperLinkDropTarget();

        foreach (var window in _windows.Values)
        {
            window.UpdateTodoLinkFeature();
        }

        RefreshCapsuleEligibilityForLinkedPapers();
        SaveNow();
        RefreshSettingsRegions("general.todos");
    }

    private void RefreshTopBarNewPaperButtonsSetting()
    {
        foreach (var window in _windows.Values)
        {
            window.UpdateTopBarNewPaperButtons();
        }

        SaveNow();
    }

    private void ToggleDeepCapsuleMode()
    {
        var windows = _windows.Values.ToList();
        foreach (var window in windows)
        {
            window.PrepareForCapsulePresentationModeChange();
        }

        List<(PaperWindow Window, PaperWindow.DeepCapsuleModeHandoff Handoff)>? handoffs = null;
        if (State.UseDeepCapsuleMode)
        {
            // Capture normal queue slots before disabling collapse-all and resetting queue
            // margins; once the hosts are detached, only the stale ordinary X/Y remains.
            handoffs = new List<(PaperWindow, PaperWindow.DeepCapsuleModeHandoff)>();
            foreach (var window in windows)
            {
                if (window.TryCaptureDeepCapsuleModeHandoff(out var handoff))
                {
                    handoffs.Add((window, handoff));
                }
            }
        }

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
            // Keep the stored master-capsule preference while the docked mode is unavailable.
            State.CapsuleCollapseAllActiveQueues.Clear();
            ResetDeepCapsuleStartTopMargins();
        }

        foreach (var window in windows)
        {
            window.UpdateDeepCapsuleMode();
        }

        ArrangeDeepCapsules();
        if (handoffs != null)
        {
            foreach (var (window, handoff) in handoffs)
            {
                window.RestoreCollapsedSurfaceAfterDeepCapsuleModeDisabled(handoff);
            }
        }
        RestoreMissingVisiblePaperSurfaces();
        SaveNow();
        RebuildTrayMenu();
        RefreshSettingsCapsuleToggleStates();
    }

    private void ToggleDeepCapsuleExpandedSlot()
    {
        var windows = _windows.Values.ToList();
        foreach (var window in windows)
        {
            window.PrepareForCapsulePresentationModeChange();
        }

        State.ShowDeepCapsuleWhileExpanded = !State.ShowDeepCapsuleWhileExpanded;

        foreach (var window in windows)
        {
            window.UpdateDeepCapsuleExpandedSlotMode();
        }

        ArrangeDeepCapsules(animate: State.EnableAnimations);
        SaveNow();
        RefreshSettingsCapsuleToggleStates();
    }

    private void ToggleHideEdgeCapsuleCloseButtonOnHover()
    {
        State.HideEdgeCapsuleCloseButtonOnHover = !State.HideEdgeCapsuleCloseButtonOnHover;
        foreach (var window in _windows.Values)
        {
            window.UpdateEdgeCapsuleCloseButtonMode();
        }

        SaveNow();
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
            // A newly assigned edge slot can be between target planning and its first applied
            // frame. It already owns the paper's presentation even though its HWND is not visible
            // yet; restoring the main window here would detach every real slot and leave only the
            // independently hosted master capsule.
            if (!paper.IsVisible ||
                !_windows.TryGetValue(paper.Id, out var window) ||
                window.WindowState == WindowState.Minimized ||
                window.IsExperimentalTetherPresentationSuppressed ||
                (State.UseDeepCapsuleMode &&
                 window.OccupiesDeepCapsuleSlot) ||
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
        window.EnsureShellBuilt();
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
