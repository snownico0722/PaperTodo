using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

public sealed partial class AppController
{
    private void SetPaperSkin(string id)
    {
        if (!PaperSkins.IsValid(id) || PaperSkins.Resolve(State) == id) return;
        if (!SetSettingFromUi("appearance.paper_skin", id)) return;

        var restartRequired = PaperSkins.UsesNativeBackdrop(id) &&
            NativeMicaBackdrop.IsSupported &&
            !UsesNativeMicaWindows;
        if (!restartRequired || _settingsWindow == null)
        {
            return;
        }

        if (!PaperNoticeDialog.ShowChoice(
                _settingsWindow,
                SettingsSidebarLocalized(
                    "重启 PaperTodo",
                    "Restart PaperTodo",
                    "PaperTodo を再起動",
                    "PaperTodo 다시 시작"),
                SettingsSidebarLocalized(
                    "原生材质将在重启 PaperTodo 后生效。要现在立即重启吗？",
                    "The native material will take effect after restarting PaperTodo. Restart now?",
                    "ネイティブ素材は PaperTodo の再起動後に反映されます。今すぐ再起動しますか？",
                    "네이티브 재질은 PaperTodo를 다시 시작한 후 적용됩니다. 지금 다시 시작할까요?"),
                SettingsSidebarLocalized("稍后", "Later", "後で", "나중에"),
                SettingsSidebarLocalized(
                    "立即重启",
                    "Restart now",
                    "今すぐ再起動",
                    "지금 다시 시작")))
        {
            return;
        }

        if (Application.Current is App app)
        {
            app.RequestRestartAfterExit();
        }
        Exit();
    }
    private UIElement CreateSkinSettings()
    {
        var panel = new StackPanel();
        var skin = PaperSkins.Resolve(State);
        panel.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsPaperSkin")), skin switch
        {
            PaperSkins.TracingPaper => "TipSkinTracingPaper",
            PaperSkins.Aero => "TipSkinAero", PaperSkins.Pixel => "TipSkinPixel", _ => "TipPaperSkin"
        }));
        panel.Children.Add(CreateSettingsSelect(
            PaperSkins.All.Select(id => (id, Strings.Get(PaperSkins.LabelKey(id)))).ToArray(), skin, SetPaperSkin));
        panel.Children.Add(SettingsToggle(
            SettingsSidebarLocalized("隐藏外轮廓", "Hide outer border", "外枠を隠す", "외곽선 숨기기"),
            State.HideSurfaceOutline, () =>
            {
                SetSettingFromUi("appearance.hide_surface_outline", !State.HideSurfaceOutline);
            }));
        if (PaperSkins.UsesNativeBackdrop(skin))
        {
            panel.Children.Add(SettingsFieldLabel(SettingsSidebarLocalized(
                "材质透明度", "Material transparency", "素材の透明度", "재질 투명도")));
            panel.Children.Add(CreateSettingsSelect(
                [
                    (MaterialTransparencyLevels.VeryLow, SettingsSidebarLocalized("最低", "Very low", "最低", "매우 낮음")),
                    (MaterialTransparencyLevels.Low, SettingsSidebarLocalized("较低", "Low", "低め", "낮음")),
                    (MaterialTransparencyLevels.Medium, SettingsSidebarLocalized("中", "Medium", "中", "중간")),
                    (MaterialTransparencyLevels.High, SettingsSidebarLocalized("较高", "High", "高め", "높음")),
                    (MaterialTransparencyLevels.VeryHigh, SettingsSidebarLocalized("最高", "Very high", "最高", "매우 높음"))
                ],
                MaterialTransparencyLevels.Normalize(State.MaterialTransparency),
                value => SetSettingFromUi("appearance.material_transparency", value)));
        }
        if (skin != PaperSkins.Paper)
            panel.Children.Add(WrapWithHint(SettingsToggle(
                Strings.Get("SettingsMatchAuxiliaryMaterial"), State.MatchAuxiliaryMaterialStrength, () =>
                {
                    SetSettingFromUi("appearance.match_auxiliary_material", !State.MatchAuxiliaryMaterialStrength);
                }), "TipMatchAuxiliaryMaterial"));
        if (PaperSkins.UsesSampledAuxiliary(skin))
        {
            panel.Children.Add(new TextBlock
            {
                Text = Strings.Get("SkinCaptureNotice"), TextWrapping = TextWrapping.Wrap,
                Foreground = TrayWeakTextBrush, FontSize = AppTypography.Scale(11), Margin = new Thickness(2, 2, 2, 5)
            });
        }
        if (PaperSkins.UsesNativeBackdrop(skin))
        {
            if (!NativeMicaBackdrop.IsSupported || !UsesNativeMicaWindows)
                panel.Children.Add(new TextBlock
                {
                    Text = Strings.Get(!NativeMicaBackdrop.IsSupported ? "SkinNativeUnsupported" : "SkinRestartRequired"),
                    TextWrapping = TextWrapping.Wrap, Foreground = TrayWeakTextBrush,
                    FontSize = AppTypography.Scale(11), Margin = new Thickness(2, 2, 2, 5)
                });
            panel.Children.Add(WrapWithHint(SettingsToggle(
                Strings.Get("SettingsMicaAlwaysActive"), State.MicaAlwaysActive, ToggleMicaAlwaysActive), "TipMicaAlwaysActive"));
        }
        return panel;
    }
    // Paint subscriptions only: do not rebuild editors when animations are toggled.
    private void RefreshSkinSurfaces() => SkinBorder.RefreshLoadedSurfaces();
}
