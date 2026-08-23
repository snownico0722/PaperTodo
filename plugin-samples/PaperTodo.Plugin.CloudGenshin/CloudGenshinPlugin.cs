using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.CloudGenshin;

public sealed class CloudGenshinPlugin : IPaperBodyPlugin
{
    public string Id => "sample.cloudgenshin.native";
    public string DisplayName => "云·原神（实验）";
    public string Description => "在 PaperTodo 纸片中直接打开云·原神网页版。";
    public Version Version => new(1, 3, 0);
    public string ApiVersion => "2.0";
    public int StateVersion => 1;
    public PaperBodyRuntimeRequirements RuntimeRequirements => PaperBodyRuntimeRequirements.BackgroundUpdates;
    public PaperBodyCapabilities Capabilities => PaperBodyCapabilities.None;

    public IPaperBodySession Create(PaperBodyContext context) =>
        new CloudGenshinSession(context);

    private sealed class CloudGenshinSession : IPaperBodySession, IPaperMiniViewProvider
    {
        private enum RetryMode
        {
            NavigateHome,
            ReloadWebView,
            RecreateSession
        }

        private static readonly Uri StartUri = new("https://ys.mihoyo.com/cloud/#/");
        private static readonly object EnvironmentGate = new();
        private static Task<CoreWebView2Environment>? _environmentTask;

        private readonly PaperBodyContext _context;
        private readonly Grid _root;
        private readonly WebView2CompositionControl _webView;
        private readonly Border _status;
        private readonly TextBlock _statusText;
        private readonly Button _retryButton;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly HashSet<ulong> _externallyOpenedNavigationIds = [];

        private bool _initializationStarted;
        private bool _initialized;
        private bool _documentReady;
        private bool _runtimeVisible;
        private bool _presentationVisible;
        private bool _disposed;
        private RetryMode _retryMode = RetryMode.NavigateHome;
        private PaperBodyInputClaims _inputClaims;
        private string _miniStatusText = "云原神 · 加载中";
        private PaperCapsuleTone _miniStatusTone = PaperCapsuleTone.Muted;
        private CloudGenshinMiniView? _miniView;

        public CloudGenshinSession(PaperBodyContext context)
        {
            _context = context;
            _context.Paper.SetTitle("云·原神");
            SetPaperStatus("云原神 · 加载中", PaperCapsuleTone.Muted);

            _root = new Grid
            {
                Background = Brushes.Black,
                ClipToBounds = true
            };

            _webView = new WebView2CompositionControl
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };
            _webView.SetValue(UIElement.OpacityProperty, 0.0);

            _statusText = new TextBlock
            {
                Text = "正在启动云·原神…",
                Foreground = Brushes.White,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            _retryButton = new Button
            {
                Content = "重新加载",
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 14, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            _retryButton.Click += OnRetryClick;

            var statusPanel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 420,
                Margin = new Thickness(24)
            };
            statusPanel.Children.Add(_statusText);
            statusPanel.Children.Add(_retryButton);

            _status = new Border
            {
                Background = Brushes.Black,
                Child = statusPanel
            };

            _root.Children.Add(_webView);
            _root.Children.Add(_status);
            _root.Loaded += OnRootLoaded;
            _root.SizeChanged += OnRootSizeChanged;
        }

        public FrameworkElement View => _root;

        public PaperMiniViewSize PreferredMiniViewSize => new(240, 140);

        public FrameworkElement? CreateMiniView(PaperMiniViewContext context)
        {
            _miniView = new CloudGenshinMiniView(context.Theme);
            _miniView.Update(_miniStatusText, _miniStatusTone);
            return _miniView;
        }

        private sealed class CloudGenshinMiniView : Grid
        {
            private readonly TextBlock _title;
            private readonly TextBlock _status;
            private readonly System.Windows.Shapes.Ellipse _dot;
            private PaperBodyTheme _theme;
            private PaperCapsuleTone _tone;

            public CloudGenshinMiniView(PaperBodyTheme theme)
            {
                _theme = theme;
                Margin = new Thickness(16, 13, 16, 15);
                Background = Brushes.Transparent;
                RowDefinitions.Add(new RowDefinition());
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                _title = new TextBlock
                {
                    Text = "云·原神",
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 20,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Children.Add(_title);

                _dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Margin = new Thickness(0, 0, 7, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                _status = new TextBlock
                {
                    FontSize = 11.5,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var statusRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                statusRow.Children.Add(_dot);
                statusRow.Children.Add(_status);
                Grid.SetRow(statusRow, 1);
                Children.Add(statusRow);
                ApplyTheme(theme);
            }

            public void Update(string text, PaperCapsuleTone tone)
            {
                _status.Text = text;
                _tone = tone;
                _dot.Fill = ToneBrush(_theme, tone);
            }

            public void ApplyTheme(PaperBodyTheme theme)
            {
                _theme = theme;
                var font = new FontFamily(theme.FontFamily);
                _title.FontFamily = font;
                _status.FontFamily = font;
                _title.Foreground = Brush(theme.TextColor, "#202020");
                _status.Foreground = Brush(theme.WeakTextColor, "#707070");
                _dot.Fill = ToneBrush(theme, _tone);
            }

            private static Brush ToneBrush(PaperBodyTheme theme, PaperCapsuleTone tone) =>
                tone switch
                {
                    PaperCapsuleTone.Accent => Brush(theme.AccentColor, "#B07A31"),
                    PaperCapsuleTone.Warning => Brush("#D28A20", "#D28A20"),
                    PaperCapsuleTone.Danger => Brush("#C94B4B", "#C94B4B"),
                    PaperCapsuleTone.Muted => Brush(theme.WeakTextColor, "#707070"),
                    _ => Brush(theme.TextColor, "#202020")
                };

            private static SolidColorBrush Brush(string value, string fallback)
            {
                try
                {
                    return new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(value)!);
                }
                catch
                {
                    return new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(fallback)!);
                }
            }
        }

        private void OnRootLoaded(object sender, RoutedEventArgs e)
        {
            TryStartInitialization();
        }

        private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
        {
            TryStartInitialization();
        }

        private void TryStartInitialization()
        {
            if (_initializationStarted ||
                !_runtimeVisible ||
                _disposed ||
                !_root.IsLoaded ||
                _root.ActualWidth <= 0 ||
                _root.ActualHeight <= 0)
            {
                return;
            }

            _initializationStarted = true;
            _root.SizeChanged -= OnRootSizeChanged;
            _ = InitializeAsync(_lifetime.Token);
        }

        private async Task InitializeAsync(CancellationToken token)
        {
            try
            {
                ShowStatus("正在初始化 WebView2…");
                var environment = await GetEnvironmentAsync();
                token.ThrowIfCancellationRequested();

                await _webView.EnsureCoreWebView2Async(environment);
                token.ThrowIfCancellationRequested();

                var core = _webView.CoreWebView2
                    ?? throw new InvalidOperationException("WebView2 初始化后未返回 CoreWebView2。 ");

                core.Settings.AreDefaultContextMenusEnabled = true;
#if DEBUG
                core.Settings.AreDevToolsEnabled = true;
#else
                core.Settings.AreDevToolsEnabled = false;
#endif
                core.Settings.AreBrowserAcceleratorKeysEnabled = true;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = true;

                core.NavigationStarting += OnNavigationStarting;
                core.NavigationCompleted += OnNavigationCompleted;
                core.NewWindowRequested += OnNewWindowRequested;
                core.ProcessFailed += OnProcessFailed;

                _initialized = true;
                NavigateHome();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _initializationStarted = false;
                ShowFailure(ex.GetBaseException().Message, RetryMode.RecreateSession);
            }
        }

        private static async Task<CoreWebView2Environment> GetEnvironmentAsync()
        {
            Task<CoreWebView2Environment> task;
            lock (EnvironmentGate)
            {
                task = _environmentTask ??= CreateEnvironmentAsync();
            }

            try
            {
                return await task;
            }
            catch
            {
                lock (EnvironmentGate)
                {
                    if (ReferenceEquals(_environmentTask, task))
                    {
                        _environmentTask = null;
                    }
                }
                throw;
            }
        }

        private static Task<CoreWebView2Environment> CreateEnvironmentAsync()
        {
            var pluginDirectory = Path.GetDirectoryName(typeof(CloudGenshinPlugin).Assembly.Location)
                ?? AppContext.BaseDirectory;
            var userDataFolder = Path.Combine(pluginDirectory, ".runtime", "webview2");
            Directory.CreateDirectory(userDataFolder);
            return CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: null);
        }

        private void NavigateHome()
        {
            if (_disposed || !_initialized || _webView.CoreWebView2 == null)
            {
                return;
            }

            _documentReady = false;
            _retryMode = RetryMode.NavigateHome;
            UpdatePresentation();
            ShowStatus("正在加载云·原神…");
            SetPaperStatus("云原神 · 加载中", PaperCapsuleTone.Muted);
            _webView.CoreWebView2.Navigate(StartUri.AbsoluteUri);
        }

        private void OnNavigationStarting(
            object? sender,
            CoreWebView2NavigationStartingEventArgs e)
        {
            if (!IsAllowedNavigation(e.Uri))
            {
                e.Cancel = true;
                _externallyOpenedNavigationIds.Add(e.NavigationId);
                OpenExternal(e.Uri);
                return;
            }

            _documentReady = false;
            UpdatePresentation();
            ShowStatus("正在加载云·原神…");
        }

        private void OnNavigationCompleted(
            object? sender,
            CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_externallyOpenedNavigationIds.Remove(e.NavigationId))
            {
                return;
            }

            if (!e.IsSuccess)
            {
                ShowFailure($"网页加载失败：{e.WebErrorStatus}");
                return;
            }

            _documentReady = true;
            _retryMode = RetryMode.NavigateHome;
            _status.Visibility = Visibility.Collapsed;
            SetPaperStatus("云原神", PaperCapsuleTone.Accent);
            UpdatePresentation();
            if (_presentationVisible)
            {
                _webView.Focus();
            }
        }

        private void OnNewWindowRequested(
            object? sender,
            CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            if (IsAllowedNavigation(e.Uri) && _webView.CoreWebView2 != null)
            {
                _webView.CoreWebView2.Navigate(e.Uri);
                return;
            }

            OpenExternal(e.Uri);
        }

        private void OnProcessFailed(
            object? sender,
            CoreWebView2ProcessFailedEventArgs e)
        {
            switch (e.ProcessFailedKind)
            {
                case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                    _documentReady = false;
                    UpdatePresentation();
                    ShowStatus("WebView2 浏览器进程已退出，正在重建…");
                    SetPaperStatus("云原神 · 正在重启", PaperCapsuleTone.Warning);
                    _context.Body.RequestReload();
                    break;

                case CoreWebView2ProcessFailedKind.RenderProcessExited:
                    ShowFailure(
                        "WebView2 渲染进程异常退出。",
                        RetryMode.ReloadWebView);
                    break;

                default:
                    // GPU、Utility、子框架及短暂无响应等故障由 WebView2 自行恢复。
                    Debug.WriteLine(
                        $"CloudGenshin WebView2 process event: {e.ProcessFailedKind}, " +
                        $"reason={e.Reason}, exitCode={e.ExitCode}");
                    break;
            }
        }

        private static bool IsAllowedNavigation(string? value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (string.Equals(uri.Scheme, "about", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return IsDomain(uri.Host, "mihoyo.com") ||
                   IsDomain(uri.Host, "hoyoverse.com") ||
                   IsDomain(uri.Host, "hoyolab.com");
        }

        private static bool IsDomain(string host, string domain) =>
            string.Equals(host, domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase);

        private void OpenExternal(string? value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https" or "mailto"))
            {
                return;
            }

            try
            {
                _context.Body.OpenExternal(uri.AbsoluteUri);
            }
            catch
            {
            }
        }

        private void ShowStatus(string message)
        {
            if (_disposed)
            {
                return;
            }

            _statusText.Text = message;
            _retryButton.Visibility = Visibility.Collapsed;
            _status.Visibility = Visibility.Visible;
        }

        private void ShowFailure(
            string message,
            RetryMode retryMode = RetryMode.NavigateHome)
        {
            if (_disposed)
            {
                return;
            }

            _documentReady = false;
            _retryMode = retryMode;
            UpdatePresentation();
            _statusText.Text = $"云·原神加载失败\n\n{message}";
            _retryButton.Visibility = Visibility.Visible;
            _status.Visibility = Visibility.Visible;
            SetPaperStatus("云原神 · 错误", PaperCapsuleTone.Danger);
        }

        private void UpdatePresentation()
        {
            var show = _runtimeVisible &&
                _presentationVisible &&
                _documentReady &&
                !_disposed;
            _webView.SetValue(UIElement.OpacityProperty, show ? 1.0 : 0.0);
            _webView.IsHitTestVisible = show;
            SetInputClaims(show
                ? PaperBodyInputClaims.EscapeKey | PaperBodyInputClaims.ContextMenu
                : PaperBodyInputClaims.None);
        }

        private void SetPaperStatus(string text, PaperCapsuleTone tone)
        {
            _miniStatusText = text;
            _miniStatusTone = tone;
            _miniView?.Update(text, tone);
            _context.Paper.SetHeaderText(text);
            _context.Paper.SetCapsulePresentation(new PaperCapsulePresentation
            {
                PreferredWidth = PaperCapsulePresentation.AutomaticWidth,
                PlainText = text,
                ToolTip = text,
                Components =
                [
                    new PaperCapsuleComponent
                    {
                        Kind = PaperCapsuleComponentKind.StatusDot,
                        Tone = tone
                    },
                    new PaperCapsuleComponent
                    {
                        Kind = PaperCapsuleComponentKind.Text,
                        Text = text,
                        Fill = true
                    }
                ]
            });
        }

        private void SetInputClaims(PaperBodyInputClaims claims)
        {
            if (_inputClaims == claims)
            {
                return;
            }

            _inputClaims = claims;
            _context.Body.SetInputClaims(claims);
        }

        private void OnRetryClick(object sender, RoutedEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                switch (_retryMode)
                {
                    case RetryMode.NavigateHome when _webView.CoreWebView2 != null:
                        NavigateHome();
                        break;

                    case RetryMode.ReloadWebView when _webView.CoreWebView2 != null:
                        _documentReady = false;
                        UpdatePresentation();
                        ShowStatus("正在重新加载云·原神…");
                        SetPaperStatus("云原神 · 加载中", PaperCapsuleTone.Muted);
                        _webView.CoreWebView2.Reload();
                        break;

                    default:
                        _context.Body.RequestReload();
                        break;
                }
            }
            catch
            {
                _context.Body.RequestReload();
            }
        }

        public void OnActivated()
        {
            if (_documentReady && _runtimeVisible && _presentationVisible)
            {
                _webView.Focus();
            }
        }

        public void OnVisibilityChanged(bool visible)
        {
            _runtimeVisible = visible;
            if (visible)
            {
                TryStartInitialization();
            }
            UpdatePresentation();
        }

        public void OnPresentationChanged(bool visible)
        {
            _presentationVisible = visible;
            UpdatePresentation();
        }

        public void OnThemeChanged(PaperBodyTheme theme) =>
            _miniView?.ApplyTheme(theme);

        public void OnTypographyChanged(PaperBodyTheme theme) =>
            _miniView?.ApplyTheme(theme);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            SetInputClaims(PaperBodyInputClaims.None);
            _disposed = true;
            _lifetime.Cancel();
            _root.Loaded -= OnRootLoaded;
            _root.SizeChanged -= OnRootSizeChanged;
            _retryButton.Click -= OnRetryClick;

            if (_webView.CoreWebView2 is { } core)
            {
                core.NavigationStarting -= OnNavigationStarting;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.NewWindowRequested -= OnNewWindowRequested;
                core.ProcessFailed -= OnProcessFailed;
                try
                {
                    core.Stop();
                }
                catch
                {
                }
            }

            try
            {
                _webView.Dispose();
            }
            catch
            {
            }

            _lifetime.Dispose();
            _miniView = null;
        }
    }
}
