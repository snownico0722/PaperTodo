using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

public sealed partial class AppController
{
    private bool _skinRestartPromptDeferred;

    private void SetPaperSkin(string id)
    {
        if (!PaperSkins.IsValid(id) || PaperSkins.Resolve(State) == id) return;
        State.PaperSkin = id;
        // Retain the last native recipe for older experimental builds.
        if (PaperSkins.IsSystemMaterial(id)) State.MicaBackdropType = PaperSkins.NativeBackdrop(id);
        SaveNow();
        RefreshThemeSurfaces();

        var restartRequired = PaperSkins.UsesNativeBackdrop(id) &&
            NativeMicaBackdrop.IsSupported &&
            !UsesNativeMicaWindows;
        if (!restartRequired)
        {
            _skinRestartPromptDeferred = false;
            return;
        }
        if (_skinRestartPromptDeferred)
        {
            return;
        }

        _skinRestartPromptDeferred = true;
        var result = _settingsWindow != null
            ? MessageBox.Show(
                _settingsWindow,
                Strings.Get("SkinRestartRequired"),
                Strings.Get("SettingsPaperSkin"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel)
            : MessageBox.Show(
                Strings.Get("SkinRestartRequired"),
                Strings.Get("SettingsPaperSkin"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        if (!AppRestart.TryLaunchAfterCurrentProcessExit(out var error))
        {
            _skinRestartPromptDeferred = false;
            var message = Strings.Get("SkinRestartRequired");
            if (!string.IsNullOrWhiteSpace(error))
            {
                message += $"{Environment.NewLine}{Environment.NewLine}{error}";
            }
            if (_settingsWindow != null)
            {
                MessageBox.Show(
                    _settingsWindow,
                    message,
                    Strings.Get("SettingsPaperSkin"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            else
            {
                MessageBox.Show(
                    message,
                    Strings.Get("SettingsPaperSkin"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            return;
        }

        Exit();
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
