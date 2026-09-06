using System.Diagnostics;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace PaperTodo;

public sealed partial class AppController
{
    private readonly MicaMaterialCache _micaMaterial = new(
        () => Task.Run(MicaMaterial.LoadDesktopWallpaper));
    private bool _micaPreferenceRefreshQueued;
    private bool _micaIsDark;
    private bool _micaHighContrast;

    internal Brush? MicaSurfaceBrush => Theme.IsDark
        ? _micaMaterial.Current?.Dark
        : _micaMaterial.Current?.Light;

    private void RefreshMicaWallpaper(bool invalidate = false)
    {
        var dispatcher = Application.Current.Dispatcher;
        dispatcher.VerifyAccess();
        _micaIsDark = Theme.IsDark;
        _micaHighContrast = SystemParameters.HighContrast;
        var enabled = !IsExiting && State.ColorScheme == ColorSchemes.Mica &&
            !_micaHighContrast && AreTransparencyEffectsEnabled();
        // RefreshAsync clears a disabled material synchronously, before theme resources read it.
        var refresh = _micaMaterial.RefreshAsync(enabled, invalidate);
        _ = ApplyMicaWallpaperAsync(refresh, dispatcher);
    }

    private async Task ApplyMicaWallpaperAsync(Task<bool> refresh, Dispatcher dispatcher)
    {
        try
        {
            if (!await refresh.ConfigureAwait(false) || dispatcher.HasShutdownStarted) return;
            await dispatcher.InvokeAsync(() =>
            {
                if (IsExiting || State.ColorScheme != ColorSchemes.Mica) return;
                // Only swap backgrounds: a wallpaper read must not rebuild editors, move the
                // caret, close menus, rebuild settings, or interfere with a drag transaction.
                RefreshMicaSurfaces();
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { /* Dispatcher is shutting down. */ }
        catch (Exception ex)
        {
            // Observe the decoration-only task even if an OS decoder/dispatcher rejects it.
            Debug.WriteLine($"Mica wallpaper refresh failed: {ex.Message}");
        }
    }

    private void RefreshMicaSurfaces()
    {
        foreach (var window in _windows.Values) window.RefreshSurfaceMaterial();
        foreach (var master in _masterCapsules.Values) master.RefreshSurfaceMaterial();
        if (_settingsWindow?.Content is Border chrome) chrome.Background = Theme.SurfaceBrush;
    }

    private void QueueMicaPreferenceRefresh()
    {
        var dispatcher = Application.Current.Dispatcher;
        dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsExiting || State.ColorScheme != ColorSchemes.Mica || _micaPreferenceRefreshQueued) return;
            _micaPreferenceRefreshQueued = true;
            dispatcher.BeginInvoke(new Action(() =>
            {
                _micaPreferenceRefreshQueued = false;
                if (IsExiting || State.ColorScheme != ColorSchemes.Mica) return;
                var oldDark = _micaIsDark;
                var oldHighContrast = _micaHighContrast;
                Theme.Invalidate();
                var colorsChanged = oldDark != Theme.IsDark || oldHighContrast != SystemParameters.HighContrast;
                RefreshMicaWallpaper(invalidate: true);
                if (colorsChanged) RefreshThemeSurfaces();
                else RefreshMicaSurfaces();
            }), DispatcherPriority.Background);
        }));
    }

    private static bool AreTransparencyEffectsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int enabled || enabled != 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }
}
