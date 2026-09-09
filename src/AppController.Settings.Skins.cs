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
        if (PaperSkins.UsesNativeBackdrop(id)) State.MicaBackdropType = PaperSkins.NativeBackdrop(id);
        SaveNow();
        RefreshThemeSurfaces();
    }
    private UIElement CreateSkinSettings()
    {
        var panel = new StackPanel();
        panel.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsPaperSkin")), "TipPaperSkin"));
        var skin = PaperSkins.Resolve(State);
        panel.Children.Add(CreateSettingsSelect(
            PaperSkins.All.Select(id => (id, Strings.Get(PaperSkins.LabelKey(id)))).ToArray(), skin, SetPaperSkin));
        panel.Children.Add(new TextBlock
        {
            Text = Strings.Get(skin switch
            {
                PaperSkins.Pearl => "TipSkinPearl", PaperSkins.TracingPaper => "TipSkinTracingPaper",
                PaperSkins.LiquidGlass => "TipSkinLiquidGlass", PaperSkins.Ceramic => "TipSkinCeramic",
                PaperSkins.Aero => "TipSkinAero", PaperSkins.Pixel => "TipSkinPixel", _ => "TipPaperSkin"
            }),
            TextWrapping = TextWrapping.Wrap, Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(11), Margin = new Thickness(2, 4, 2, 5)
        });
        if (PaperSkins.UsesNativeBackdrop(skin))
        {
            panel.Children.Add(new TextBlock
            {
                Text = Strings.Get(!NativeMicaBackdrop.IsSupported ? "SkinNativeUnsupported" :
                    !UsesNativeMicaWindows ? "SkinRestartRequired" : "SkinNativeScope"),
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
    }
}
