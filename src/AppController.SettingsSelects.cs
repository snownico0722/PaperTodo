using System;
using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

public sealed partial class AppController
{
    private void SetUiLanguage(string language)
    {
        var normalized = UiLanguages.Normalize(language);
        if (string.Equals(State.UiLanguage, normalized, StringComparison.Ordinal))
        {
            return;
        }

        State.UiLanguage = normalized;
        SaveNow();
    }

    private UIElement CreateUiLanguageSettingsRow() =>
        CompactSettingsField(
            Strings.Get("SettingsUiLanguage"),
            CreateUiLanguageSelector(),
            editorWidth: 156,
            tipKey: "TipSettingsUiLanguage",
            topMargin: 4);

    private UIElement CreateUiLanguageSelector()
    {
        return CreateSettingsSelect(
            [
                (UiLanguages.System, Strings.Get("UiLanguageSystem")),
                (UiLanguages.ChineseSimplified, Strings.Get("UiLanguageZhHans")),
                (UiLanguages.English, Strings.Get("UiLanguageEnglish")),
                (UiLanguages.Japanese, Strings.Get("UiLanguageJapanese")),
                (UiLanguages.Korean, Strings.Get("UiLanguageKorean"))
            ],
            UiLanguages.Normalize(State.UiLanguage),
            SetUiLanguage);
    }

    private UIElement CreateSettingsSelect(
        (string Key, string Label)[] choices,
        string selectedKey,
        Action<string> onSelect)
    {
        var combo = new ComboBox
        {
            Height = AppTypography.FitChrome(28),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Focusable = false
        };
        PaperSelectControl.ApplyAppTheme(combo, AppTypography.Scale(12));

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

        if (combo.Items.Count > 0)
        {
            combo.SelectedItem = selected ?? combo.Items[0];
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string key })
            {
                onSelect(key);
            }
        };
        return combo;
    }

    private UIElement CreateTodoVisualSizeSelector()
    {
        return CreateSettingsSelect(
            [
                (TodoVisualSizes.Small, Strings.Get("TodoVisualSizeSmall")),
                (TodoVisualSizes.Medium, Strings.Get("TodoVisualSizeMedium")),
                (TodoVisualSizes.Large, Strings.Get("TodoVisualSizeLarge"))
            ],
            TodoVisualSizes.Normalize(State.TodoVisualSize),
            SetTodoVisualSize);
    }

    private UIElement CreateVisualTextSizeSelector(
        string activeSize,
        Action<string> onSelect)
    {
        return CreateSettingsSelect(
            [
                (VisualTextSizes.Small, Strings.Get("TodoVisualSizeSmall")),
                (VisualTextSizes.Medium, Strings.Get("TodoVisualSizeMedium")),
                (VisualTextSizes.Large, Strings.Get("TodoVisualSizeLarge"))
            ],
            VisualTextSizes.Normalize(activeSize),
            onSelect);
    }

    private UIElement CreateEdgeCapsuleHoverIntentSensitivitySelector()
    {
        return CreateSettingsSelect(
            [
                (EdgeCapsuleHoverIntentSensitivities.VeryLow,
                    Strings.Get("EdgeCapsuleHoverIntentSensitivityVeryLow")),
                (EdgeCapsuleHoverIntentSensitivities.Low,
                    Strings.Get("EdgeCapsuleHoverIntentSensitivityLow")),
                (EdgeCapsuleHoverIntentSensitivities.Medium,
                    Strings.Get("EdgeCapsuleHoverIntentSensitivityMedium")),
                (EdgeCapsuleHoverIntentSensitivities.High,
                    Strings.Get("EdgeCapsuleHoverIntentSensitivityHigh")),
                (EdgeCapsuleHoverIntentSensitivities.VeryHigh,
                    Strings.Get("EdgeCapsuleHoverIntentSensitivityVeryHigh"))
            ],
            EdgeCapsuleHoverIntentSensitivities.Normalize(
                State.ExperimentalEdgeCapsuleHoverIntentSensitivity),
            SetExperimentalEdgeCapsuleHoverIntentSensitivity);
    }

    private UIElement CreateWindowTetherEdgeSelector()
    {
        return CreateSettingsSelect(
            [
                (ExperimentalWindowTetherOptions.Auto,
                    Strings.Get("LabsWindowTetherEdgeAuto")),
                (ExperimentalWindowTetherOptions.Left,
                    Strings.Get("LabsWindowTetherEdgeLeft")),
                (ExperimentalWindowTetherOptions.Right,
                    Strings.Get("LabsWindowTetherEdgeRight")),
                (ExperimentalWindowTetherOptions.Top,
                    Strings.Get("LabsWindowTetherEdgeTop")),
                (ExperimentalWindowTetherOptions.Bottom,
                    Strings.Get("LabsWindowTetherEdgeBottom"))
            ],
            ExperimentalWindowTetherOptions.NormalizeEdge(
                State.ExperimentalWindowTetherPreferredEdge),
            SetExperimentalWindowTetherPreferredEdge);
    }

    private UIElement CreateTextStyleRow(
        string labelText,
        string tipKey,
        UIElement sizeSelector,
        bool isBold,
        Action toggleBold)
    {
        var row = new Grid
        {
            Margin = new Thickness(0, 3, 0, 2)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
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
            FontSize = AppTypography.Scale(12),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        labelHost.Children.Add(CreateSettingsHintGlyph(
            tipKey,
            new Thickness(4, 0, 0, 0)));
        Grid.SetColumn(labelHost, 0);
        row.Children.Add(labelHost);

        var boldHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        boldHost.Children.Add(new TextBlock
        {
            Text = Strings.Get("SettingsTextBold"),
            Foreground = TrayTextBrush,
            FontSize = AppTypography.Scale(12),
            VerticalAlignment = VerticalAlignment.Center
        });
        var boldToggle = SettingsToggle(string.Empty, isBold, toggleBold);
        boldToggle.Margin = new Thickness(4, 0, 0, 0);
        boldToggle.VerticalAlignment = VerticalAlignment.Center;
        boldHost.Children.Add(boldToggle);
        Grid.SetColumn(boldHost, 1);
        row.Children.Add(boldHost);

        if (sizeSelector is FrameworkElement selector)
        {
            selector.Width = 92;
            selector.Margin = new Thickness(8, 0, 0, 0);
            selector.HorizontalAlignment = HorizontalAlignment.Right;
            selector.VerticalAlignment = VerticalAlignment.Center;
        }
        Grid.SetColumn(sizeSelector, 2);
        row.Children.Add(sizeSelector);
        return row;
    }
}
