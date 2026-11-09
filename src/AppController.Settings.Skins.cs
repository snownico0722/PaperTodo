using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace PaperTodo;

public sealed partial class AppController
{
    // Session policy only, not a second saved preference. AllowsTransparency cannot change
    // after HWND creation; keep live editors/undo stacks instead of rebuilding them on selection.
    internal bool UsesNativeMicaWindows { get; private set; }
    private NativeMicaBackdrop? _settingsMica;
    private bool _nativeMicaPreferenceRefreshQueued;

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
        if (PaperSkins.UsesNativeBackdrop(skin))
        {
            panel.Children.Add(SettingsFieldLabel(SettingsSidebarLocalized(
                "展开纸片材质透明度", "Expanded paper material transparency", "展開した紙片の素材透明度", "펼친 메모의 재질 투명도")));
            panel.Children.Add(CreateSegmentSelector(
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
        panel.Children.Add(WrapWithHint(SettingsToggle(
            SettingsSidebarLocalized("隐藏外轮廓", "Hide outer border", "外枠を隠す", "외곽선 숨기기"),
            State.HideSurfaceOutline, () =>
            {
                SetSettingFromUi("appearance.hide_surface_outline", !State.HideSurfaceOutline);
            }), "TipHideSurfaceOutline"));
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

    // Also refresh decorated skins in fixed light/dark mode when accessibility changes.
    private void QueueNativeMicaPreferenceRefresh()
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsExiting || _nativeMicaPreferenceRefreshQueued) return;
            _nativeMicaPreferenceRefreshQueued = true;
            dispatcher.BeginInvoke(new Action(() =>
            {
                _nativeMicaPreferenceRefreshQueued = false;
                if (IsExiting) return;
                DwmMicaApi.Instance.InvalidateEnvironment();
                // Refresh semantic foreground colors too, including fixed light/dark in HC.
                RefreshThemeSurfaces();
            }), DispatcherPriority.Background);
        }));
    }
}
