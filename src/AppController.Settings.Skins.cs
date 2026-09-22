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
        if (!SetSettingFromUi("appearance.paper_skin", id)) return;

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

        // A settings-triggered restart must not discard unpersisted edits. Unlike an
        // explicit Exit, a failed preflight leaves the running application intact.
        CommitSettingsExternalMarkdownEditor(saveImmediately: false);
        foreach (var window in _windows.Values.ToList())
        {
            window.CommitPendingEditsForSave();
        }
        if (!TrySaveNow(sync: true))
        {
            _skinRestartPromptDeferred = false;
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
        if (skin != PaperSkins.Paper)
            panel.Children.Add(WrapWithHint(SettingsToggle(
                Strings.Get("SettingsMatchAuxiliaryMaterial"), State.MatchAuxiliaryMaterialStrength, () =>
                {
                    SetSettingFromUi("appearance.match_auxiliary_material", !State.MatchAuxiliaryMaterialStrength);
                }), "TipMatchAuxiliaryMaterial"));
        if (PaperSkins.UsesNativeBackdrop(skin) && skin != PaperSkins.Aero)
        {
            panel.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsLiveBackgroundProcessing"), State.LiveBackgroundProcessing, () =>
            {
                SetSettingFromUi("appearance.live_background_processing", !State.LiveBackgroundProcessing);
            }), "TipLiveBackgroundProcessing"));
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
