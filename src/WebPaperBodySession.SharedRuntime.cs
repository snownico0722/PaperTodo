using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace PaperTodo;

// These narrow accessors keep the body-session implementation private while allowing the
// provider/paper runtime infrastructure to share one environment pool and local-origin policy.
internal sealed partial class WebPaperBodySession
{
    internal static Task<CoreWebView2Environment> SharedPluginEnvironmentAsync(
        string pluginDirectory) =>
        GetPluginEnvironmentAsync(pluginDirectory, backgroundRuntime: true);

    internal static string SharedWebHostName(string pluginId) =>
        WebHostName(pluginId);
}

/// <summary>
/// Common non-surface-specific Web plugin runtime services. Body sessions own only visible UI;
/// provider and per-paper runtimes use the hidden host below and never move their WebViews into a
/// PaperWindow.
/// </summary>
internal static class WebPluginRuntimeInfrastructure
{
    private static class BackgroundWebViewHost
    {
        private static Window? _window;
        private static Grid? _root;

        public static bool TryAttach(WebView2CompositionControl webView)
        {
            try
            {
                Application.Current.Dispatcher.VerifyAccess();
                if (webView.Parent is Panel parent)
                {
                    parent.Children.Remove(webView);
                }
                else if (webView.Parent != null)
                {
                    return false;
                }

                EnsureWindow();
                _root!.Children.Add(webView);
                webView.Width = 1;
                webView.Height = 1;
                webView.HorizontalAlignment = HorizontalAlignment.Stretch;
                webView.VerticalAlignment = VerticalAlignment.Stretch;
                if (_window!.IsVisible == false)
                {
                    _window.Show();
                }
                return true;
            }
            catch
            {
                if (_root?.Children.Contains(webView) == true)
                {
                    _root.Children.Remove(webView);
                }
                return false;
            }
        }

        public static void Detach(WebView2CompositionControl webView)
        {
            if (_root?.Children.Contains(webView) == true)
            {
                _root.Children.Remove(webView);
            }
            if (_root?.Children.Count == 0 && _window?.IsVisible == true)
            {
                _window.Hide();
            }
        }

        private static void EnsureWindow()
        {
            if (_window != null)
            {
                return;
            }

            _root = new Grid
            {
                Width = 1,
                Height = 1,
                Background = Brushes.Transparent,
                ClipToBounds = true
            };
            _window = new Window
            {
                Content = _root,
                Width = 1,
                Height = 1,
                Left = -32000,
                Top = -32000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Opacity = 0.01,
                ShowActivated = false,
                ShowInTaskbar = false,
                Focusable = false,
                IsHitTestVisible = false
            };
        }
    }

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static Task<CoreWebView2Environment> EnvironmentAsync(string pluginDirectory) =>
        WebPaperBodySession.SharedPluginEnvironmentAsync(pluginDirectory);

    public static bool AttachBackground(WebView2CompositionControl webView) =>
        BackgroundWebViewHost.TryAttach(webView);

    public static void DetachBackground(WebView2CompositionControl webView) =>
        BackgroundWebViewHost.Detach(webView);

    public static string Origin(string pluginId) =>
        $"https://{WebPaperBodySession.SharedWebHostName(pluginId)}";

    public static string HostName(string pluginId) =>
        WebPaperBodySession.SharedWebHostName(pluginId);

    public static Uri LocalEntryUri(
        string expectedOrigin,
        string webRoot,
        string entryPath)
    {
        var relative = Path.GetRelativePath(webRoot, entryPath).Replace('\\', '/');
        return new Uri(
            $"{expectedOrigin}/{Uri.EscapeDataString(relative).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}");
    }

    public static bool IsSameOrigin(string? value, string expectedOrigin) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(
            uri.GetLeftPart(UriPartial.Authority),
            expectedOrigin,
            StringComparison.OrdinalIgnoreCase);

    public static void ConfigureBackgroundCore(CoreWebView2 core)
    {
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
    }

    public static string RequiredString(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new PaperTodo.Plugin.PaperTodoPluginException(
                "invalid_params",
                $"{name} is required.");
        }
        return value.GetString()!;
    }

    public static JsonElement ParametersOrEmpty(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty("params", out var paramsValue)
            ? paramsValue
            : JsonSerializer.SerializeToElement(new { });
}
