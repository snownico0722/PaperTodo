using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

public sealed partial class AppController
{
    private void SetPaperSkin(string id)
    {
        if (!PaperSkins.IsValid(id) || PaperSkins.Resolve(State) == id) return;
        State.PaperSkin = id;
        // Retain the last native recipe for older experimental builds.
        if (PaperSkins.IsSystemMaterial(id)) State.MicaBackdropType = PaperSkins.NativeBackdrop(id);
        SaveNow();
        RefreshThemeSurfaces();
    }
    private UIElement CreateSkinSettings()
    {
        var panel = new StackPanel();
        var skin = PaperSkins.Resolve(State);
        panel.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsPaperSkin")), skin switch
        {
            PaperSkins.TracingPaper => "TipSkinTracingPaper", PaperSkins.LiquidGlass => "TipSkinLiquidGlass",
            PaperSkins.Aero => "TipSkinAero", PaperSkins.Pixel => "TipSkinPixel", _ => "TipPaperSkin"
        }));
        panel.Children.Add(CreateSettingsSelect(
            PaperSkins.All.Select(id => (id, Strings.Get(PaperSkins.LabelKey(id)))).ToArray(), skin, SetPaperSkin));
        if (skin != PaperSkins.Paper)
            panel.Children.Add(WrapWithHint(SettingsToggle(
                Strings.Get("SettingsMatchAuxiliaryMaterial"), State.MatchAuxiliaryMaterialStrength, () =>
                {
                    State.MatchAuxiliaryMaterialStrength = !State.MatchAuxiliaryMaterialStrength;
                    SaveNow(); RefreshThemeSurfaces();
                }), "TipMatchAuxiliaryMaterial"));
        if (PaperSkins.UsesNativeBackdrop(skin) && skin != PaperSkins.Aero)
        {
            panel.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsLiveRefraction"), State.LiquidGlassRefraction, () =>
            {
                State.LiquidGlassRefraction = !State.LiquidGlassRefraction;
                SaveNow(); RefreshSkinSurfaces();
            }), "TipLiveRefraction"));
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
    private void RefreshSkinSurfaces()
    {
        foreach (var window in _windows.Values) window.RefreshSkin();
        foreach (var master in _masterCapsules.Values) master.UpdateTheme();
        if (_settingsWindow?.Content is Border border) SkinBorder.Refresh(border);
        SkinBorder.RefreshLoadedSurfaces();
    }
}
