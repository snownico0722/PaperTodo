using System;
using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

public sealed partial class AppController
{
    private void SetUiLanguage(string language)
    {
        var normalized = UiLanguages.Normalize(language);
        if (string.Equals(State.UiLanguage, normalized, StringComparison.Ordinal)) return;
        State.UiLanguage = normalized;
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private UIElement CreateUiLanguageSettingsRow()
    {
        var panel = new StackPanel();
        panel.Children.Add(WrapWithHint(
            SettingsFieldLabel(Strings.Get("SettingsUiLanguage"), topMargin: 4),
            "TipSettingsUiLanguage"));
        panel.Children.Add(CreateUiLanguageSelector());
        if (State.AdvancedSettingsMode)
        {
            panel.Children.Add(CreateAnonymousUsageStatisticsSettingsCard());
        }
        return panel;
    }

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

    private void SetDeepCapsuleGapSize(string size)
    {
        var normalized = DeepCapsuleGapSizes.Normalize(size);
        if (State.DeepCapsuleGapSize == normalized) return;
        State.DeepCapsuleGapSize = normalized;
        SaveNow();
        ArrangeDeepCapsules(animate: State.EnableAnimations);
        RefreshSettingsWindowContent();
    }

    private UIElement CreateDeepCapsuleGapSelector()
    {
        return CreateSettingsSelect(
            [
                (DeepCapsuleGapSizes.Narrow, Strings.Get("DeepCapsuleGapNarrow")),
                (DeepCapsuleGapSizes.Standard, Strings.Get("DeepCapsuleGapStandard")),
                (DeepCapsuleGapSizes.Wide, Strings.Get("DeepCapsuleGapWide"))
            ],
            DeepCapsuleGapSizes.Normalize(State.DeepCapsuleGapSize),
            SetDeepCapsuleGapSize);
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
            Focusable = false,
            Margin = new Thickness(0, 4, 0, 10)
        };
        SettingsSelectControl.ApplyAppTheme(combo, AppTypography.Scale(12));
        ComboBoxItem? selected = null;
        foreach (var (key, label) in choices)
        {
            var item = new ComboBoxItem { Tag = key, Content = label };
            combo.Items.Add(item);
            if (string.Equals(key, selectedKey, StringComparison.Ordinal)) selected = item;
        }
        if (combo.Items.Count > 0) combo.SelectedItem = selected ?? combo.Items[0];
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string key }) onSelect(key);
        };
        return combo;
    }
}
