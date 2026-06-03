using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace PaperTodo;

public sealed partial class AppController : IDisposable
{
    public static AppController Current { get; private set; } = null!;

    private readonly StateStore _store = new();
    private readonly Dictionary<string, PaperWindow> _windows = new();
    private readonly DispatcherTimer _saveTimer;

    private TaskbarIcon? _trayIcon;
    private ContextMenu? _trayMenu;
    private bool _isExiting;
    private bool _suppressDirty;
    private bool _hasShownSaveFailure;
    private bool _ignoreSaveFailures;
    private int _trayRefreshSuppressionDepth;
    private long _saveVersion;

    private static Brush TrayPaperBrush => Theme.PaperBrush;
    private static Brush TrayBorderBrush => Theme.PaperBorderBrush;
    private static Brush TrayTextBrush => Theme.TextBrush;
    private static Brush TrayWeakTextBrush => Theme.WeakTextBrush;
    private static Brush TrayHoverBrush => Theme.HoverBrush;

    public AppState State { get; private set; }

    [GeneratedRegex(@"\[(.*?)\]\((.*?)\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^\s*#{1,6}\s*")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^\s*[-*+]\s+")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^\s*>\s*")]
    private static partial Regex QuoteRegex();

    public AppController()
    {
        Current = this;
        State = _store.Load();

        _saveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void Start(bool createDefaultPaper = true)
    {
        CreateTrayIcon();

        if (State.Papers.Count == 0)
        {
            if (createDefaultPaper)
            {
                CreatePaper(PaperTypes.Todo, show: true);
                SaveNow();
            }
            return;
        }

        var rescuedPapers = EnsurePapersOnScreen();

        // Closing a paper only hides it for the current session; a fresh app start
        // restores every non-deleted paper so a paper never feels "lost".
        _suppressDirty = true;
        try
        {
            foreach (var paper in State.Papers.ToList())
            {
                ShowPaper(paper);
            }
        }
        finally
        {
            _suppressDirty = false;
        }

        if (rescuedPapers)
        {
            SaveNow();
        }
    }

    public PaperData? CreatePaper(string type, bool show = true, PaperData? sourcePaper = null)
    {
        if (State.Papers.Count >= 100)
        {
            MessageBox.Show(Strings.Get("PaperLimitMessage"), Strings.Get("PaperLimitTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var offset = State.Papers.Count * 24;

        double newX = 140 + offset;
        double newY = 140 + offset;

        if (sourcePaper != null)
        {
            newX = sourcePaper.X + 30;
            newY = sourcePaper.Y + 30;
        }

        while (State.Papers.Any(p => Math.Abs(p.X - newX) < 5 && Math.Abs(p.Y - newY) < 5))
        {
            newX += 30;
            newY += 30;
        }

        var paper = new PaperData
        {
            Type = type == PaperTypes.Note ? PaperTypes.Note : PaperTypes.Todo,
            X = newX,
            Y = newY,
            Width = type == PaperTypes.Note ? PaperLayoutDefaults.NoteDefaultWidth : PaperLayoutDefaults.TodoDefaultWidth,
            Height = type == PaperTypes.Note ? PaperLayoutDefaults.NoteDefaultHeight : PaperLayoutDefaults.TodoDefaultHeight,
            IsVisible = show
        };

        RescuePaperIfOffScreen(paper, State.Papers.Count);

        if (paper.Type == PaperTypes.Todo)
        {
            paper.Items.Add(new PaperItem
            {
                Text = "",
                Done = false,
                Order = 0
            });
        }

        State.Papers.Add(paper);

        if (show)
        {
            _trayRefreshSuppressionDepth++;
            try
            {
                ShowPaper(paper);
            }
            finally
            {
                _trayRefreshSuppressionDepth--;
            }
        }

        RefreshTrayMenu();
        MarkDirty();
        return paper;
    }

    public void TogglePaperVisibility(PaperData paper)
    {
        if (IsPaperShown(paper))
        {
            HidePaper(paper);
        }
        else
        {
            ShowPaper(paper);
        }
    }

    public void ExecuteStartupCommand(StartupCommand command)
    {
        switch (command.Kind)
        {
            case StartupCommandKind.Show:
                ShowAllPapers();
                break;
            case StartupCommandKind.Hide:
                HideAllPapers();
                break;
            case StartupCommandKind.Toggle:
                if (State.Papers.Any(IsPaperShown))
                {
                    HideAllPapers();
                }
                else
                {
                    ShowAllPapers();
                }
                break;
            case StartupCommandKind.NewTodo:
                CreatePaper(PaperTypes.Todo, show: true);
                break;
            case StartupCommandKind.NewNote:
                CreatePaper(PaperTypes.Note, show: true);
                break;
            case StartupCommandKind.Exit:
                Exit();
                break;
        }
    }

    public void ShowPaper(PaperData paper)
    {
        paper.IsVisible = true;
        RescuePaperIfOffScreen(paper, State.Papers.IndexOf(paper));

        if (!_windows.TryGetValue(paper.Id, out var window))
        {
            window = new PaperWindow(paper, this);
            _windows[paper.Id] = window;
        }

        if (!window.IsVisible)
        {
            window.Left = paper.X;
            window.Top = paper.Y;
            if (paper.IsCollapsed && State.UseCapsuleMode)
            {
                window.Width = PaperLayoutDefaults.CapsuleWidth;
                window.Height = PaperLayoutDefaults.CapsuleHeight;
            }
            else
            {
                window.Width = paper.Width;
                window.Height = paper.Height;
            }
            // To prevent a 1-frame DWM cache flash when a window's size changes while hidden,
            // we show it fully transparent first, then restore opacity after layout is complete.
            double originalOpacity = window.Opacity;
            window.Opacity = 0;
            window.Show();

            window.Dispatcher.InvokeAsync(() =>
            {
                window.Opacity = originalOpacity;
            }, System.Windows.Threading.DispatcherPriority.Render);
        }

        window.Activate();
        if (State.UseCapsuleMode && State.UseDeepCapsuleMode && paper.IsCollapsed)
        {
            ArrangeDeepCapsules();
        }
        RefreshTrayMenu();
        MarkDirty();
    }

    public void HidePaper(PaperData paper)
    {
        paper.IsVisible = false;

        if (_windows.TryGetValue(paper.Id, out var window))
        {
            var saveGeometry = !window.IsDeepCapsulePlaced;
            window.Hide();
            if (paper.IsCollapsed)
            {
                window.SetCollapsedState(false, animate: false, saveGeometry: saveGeometry);
            }
        }
        else
        {
            paper.IsCollapsed = false;
        }

        ArrangeDeepCapsules();
        RefreshTrayMenu();
        MarkDirty();
    }

    public void ShowAllPapers()
    {
        EnsurePapersOnScreen();

        var wasSuppressingDirty = _suppressDirty;
        _suppressDirty = true;
        _trayRefreshSuppressionDepth++;
        try
        {
            foreach (var paper in State.Papers)
            {
                ShowPaper(paper);
            }
        }
        finally
        {
            _trayRefreshSuppressionDepth--;
            _suppressDirty = wasSuppressingDirty;
        }

        RefreshTrayMenu();
        MarkDirty();
    }

    public void HideAllPapers()
    {
        foreach (var paper in State.Papers)
        {
            paper.IsVisible = false;
        }

        foreach (var window in _windows.Values)
        {
            window.Hide();
            window.SetCollapsedState(false, animate: false, saveGeometry: !window.IsDeepCapsulePlaced);
        }

        foreach (var paper in State.Papers)
        {
            paper.IsCollapsed = false;
        }

        RefreshTrayMenu();
        MarkDirty();
    }

    public void DeletePaper(PaperData paper)
    {
        if (_windows.TryGetValue(paper.Id, out var window))
        {
            window.CloseForReal();
            _windows.Remove(paper.Id);
        }

        State.Papers.RemoveAll(p => p.Id == paper.Id);

        if (State.Papers.Count == 0)
        {
            _trayRefreshSuppressionDepth++;
            try
            {
                CreatePaper(PaperTypes.Todo, show: true);
            }
            finally
            {
                _trayRefreshSuppressionDepth--;
            }
        }

        ArrangeDeepCapsules();
        RefreshTrayMenu();
        MarkDirty();
    }

    public void UpdateGeometry(PaperData paper, Window window)
    {
        if (window is PaperWindow paperWindow && paperWindow.SuppressGeometrySave)
        {
            return;
        }

        if (double.IsNaN(window.Left) || double.IsNaN(window.Top))
        {
            return;
        }

        paper.X = Math.Round(window.Left);
        paper.Y = Math.Round(window.Top);
        if (!paper.IsCollapsed)
        {
            paper.Width = Math.Round(Math.Max(window.ActualWidth > 0 ? window.ActualWidth : window.Width, PaperLayoutDefaults.MinWidth));
            paper.Height = Math.Round(Math.Max(window.ActualHeight > 0 ? window.ActualHeight : window.Height, PaperLayoutDefaults.MinHeight));
        }

        MarkDirty();
    }

    public void ArrangeDeepCapsules()
    {
        if (!State.UseCapsuleMode || !State.UseDeepCapsuleMode)
        {
            foreach (var window in _windows.Values)
            {
                window.ClearDeepCapsulePlacement();
            }
            return;
        }

        var capsuleIndex = 0;
        foreach (var paper in State.Papers)
        {
            if (!_windows.TryGetValue(paper.Id, out var window))
            {
                continue;
            }

            if (paper.IsVisible && paper.IsCollapsed && window.IsVisible)
            {
                window.ApplyDeepCapsulePlacement(capsuleIndex);
                capsuleIndex++;
            }
            else
            {
                window.ClearDeepCapsulePlacement();
            }
        }
    }

    public void MarkDirty()
    {
        if (_isExiting || _suppressDirty)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow(bool sync = false)
    {
        try
        {
            _saveTimer.Stop();
            var version = Interlocked.Increment(ref _saveVersion);
            var json = _store.SerializeState(State);
            if (sync)
            {
                _store.SaveJsonSync(json, version);
                _hasShownSaveFailure = false;
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _store.SaveJsonAsync(json, version);
                        Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                        {
                            _hasShownSaveFailure = false;
                        }));
                    }
                    catch (Exception ex)
                    {
                        Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                        {
                            HandleSaveFailure(ex);
                        }));
                    }
                });
            }
        }
        catch (Exception ex)
        {
            HandleSaveFailure(ex);
        }
    }

    private static Style BuildDialogButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 4, 10, 4)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Theme.TextBrush));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        var mouseOver = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        mouseOver.Setters.Add(new Setter(Control.BackgroundProperty, Theme.HoverBrush));

        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.7));

        template.Triggers.Add(mouseOver);
        template.Triggers.Add(pressed);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));

        return style;
    }

    private void HandleSaveFailure(Exception ex)
    {
        if (!_hasShownSaveFailure && !_ignoreSaveFailures)
        {
            _hasShownSaveFailure = true;
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                var dlg = new Window
                {
                    Title = Strings.Get("SaveFailureTitle"),
                    Width = 360,
                    Height = 180,
                    WindowStyle = WindowStyle.None,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    Topmost = true
                };
                var border = new Border
                {
                    Background = Theme.PaperBrush,
                    BorderBrush = Theme.PaperBorderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(20)
                };
                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var txt = new TextBlock
                {
                    Text = Strings.Format("SaveFailureMessage", ex.Message),
                    Foreground = Theme.TextBrush,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14
                };
                grid.Children.Add(txt);
                var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetRow(btnPanel, 1);
                var btnOk = new Button { Content = Strings.Get("CommonOk"), Width = 80, Margin = new Thickness(0, 0, 10, 0), Style = BuildDialogButtonStyle() };
                btnOk.Click += (s, e) => { _hasShownSaveFailure = false; dlg.Close(); };
                var btnIgnore = new Button { Content = Strings.Get("SaveFailureIgnore"), Width = 110, Style = BuildDialogButtonStyle() };
                btnIgnore.Click += (s, e) => { _ignoreSaveFailures = true; dlg.Close(); };
                btnPanel.Children.Add(btnOk);
                btnPanel.Children.Add(btnIgnore);
                grid.Children.Add(btnPanel);
                border.Child = grid;
                dlg.Content = border;
                dlg.ShowDialog();
            });
        }
    }

    private bool IsPaperShown(PaperData paper)
    {
        return paper.IsVisible && _windows.TryGetValue(paper.Id, out var window) && window.IsVisible;
    }

    private bool EnsurePapersOnScreen()
    {
        var changed = false;
        for (var i = 0; i < State.Papers.Count; i++)
        {
            changed |= RescuePaperIfOffScreen(State.Papers[i], i);
        }

        return changed;
    }

    private static bool RescuePaperIfOffScreen(PaperData paper, int offsetIndex)
    {
        if (IsPaperOnAnyScreen(paper))
        {
            return false;
        }

        var area = SystemParameters.WorkArea;
        var offset = Math.Min(Math.Max(offsetIndex, 0), 8) * 22;

        paper.Width = Math.Clamp(paper.Width, PaperLayoutDefaults.MinWidth, Math.Max(PaperLayoutDefaults.MinWidth, area.Width - 80));
        paper.Height = Math.Clamp(paper.Height, PaperLayoutDefaults.MinHeight, Math.Max(PaperLayoutDefaults.MinHeight, area.Height - 80));
        paper.X = area.Left + 40 + offset;
        paper.Y = area.Top + 40 + offset;
        return true;
    }

    private static bool IsPaperOnAnyScreen(PaperData paper)
    {
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        var paperRect = new Rect(
            paper.X,
            paper.Y,
            Math.Max(paper.Width, 80),
            Math.Max(paper.Height, 80));

        return virtualScreen.IntersectsWith(paperRect);
    }

    private void CreateTrayIcon()
    {
        _trayMenu = CreateTrayMenu();
        _trayMenu.Opened += (_, _) => RebuildTrayMenu();

        RebuildTrayMenu();

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "PaperTodo",
            IconSource = LoadTrayIconSource(),
            ContextMenu = _trayMenu,
            Visibility = Visibility.Visible
        };

        _trayIcon.TrayMouseDoubleClick += (_, _) =>
        {
            Application.Current.Dispatcher.Invoke(ShowAllPapers);
        };

    }

    private ImageSource LoadTrayIconSource()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "PaperTodo.ico");
        try
        {
            if (File.Exists(iconPath))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(iconPath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }
        catch
        {
            // Fall back to the embedded icon if the custom external file is corrupt or locked.
        }

        try
        {
            var resourceName = typeof(AppController).Assembly
                .GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(".PaperTodo.ico", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(resourceName))
            {
                using var stream = typeof(AppController).Assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = stream;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
            }
        }
        catch
        {
            // Fallback to vector icon if the embedded resource cannot be loaded.
        }

        return CreateFallbackTrayIcon();
    }

    private static ImageSource CreateFallbackTrayIcon()
    {
        const int size = 32;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var paper = FrozenBrush(Color.FromRgb(255, 248, 230));
            var border = new Pen(FrozenBrush(Color.FromRgb(126, 96, 58)), 2);
            var check = new Pen(FrozenBrush(Color.FromRgb(80, 96, 60)), 3)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };

            dc.DrawRoundedRectangle(paper, border, new Rect(5, 4, 22, 24), 4, 4);
            dc.DrawLine(check, new Point(10, 18), new Point(15, 23));
            dc.DrawLine(check, new Point(15, 23), new Point(23, 12));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static readonly ControlTemplate SharedTrayMenuTemplate = BuildTrayMenuTemplate();
    private static readonly ControlTemplate SharedSeparatorTemplate = BuildSeparatorTemplate();
    private static readonly ControlTemplate SharedTrayMenuItemTemplate = BuildTrayMenuItemTemplate();
    private static readonly ControlTemplate SharedSegmentMenuItemTemplate = BuildSegmentMenuItemTemplate();
    private static readonly ControlTemplate SharedTrayContentMenuItemTemplate = BuildTrayContentMenuItemTemplate();
    private static readonly Style SharedTrayMenuItemStyle = BuildTrayMenuItemStyle();
    private static readonly Style SharedTrayContentMenuItemStyle = BuildTrayContentMenuItemStyle();
    private static readonly Style SharedTrayHeaderStyle = BuildTrayHeaderStyle();

    private static ControlTemplate BuildSegmentMenuItemTemplate()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);

        return new ControlTemplate(typeof(MenuItem))
        {
            VisualTree = presenter
        };
    }

    private static ControlTemplate BuildTrayMenuTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        border.AppendChild(presenter);

        return new ControlTemplate(typeof(ContextMenu))
        {
            VisualTree = border
        };
    }

    private static ControlTemplate BuildSeparatorTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.HeightProperty, 1.0);
        border.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("TrayBorderBrushKey"));
        border.SetValue(UIElement.OpacityProperty, 0.45);

        return new ControlTemplate(typeof(Separator))
        {
            VisualTree = border
        };
    }

    private static ControlTemplate BuildTrayMenuItemTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var content = new FrameworkElementFactory(typeof(TextBlock));
        content.SetBinding(TextBlock.TextProperty, new Binding("Header") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        content.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        content.SetValue(FrameworkElement.MaxWidthProperty, 170.0);
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(MenuItem))
        {
            VisualTree = border
        };

        var hover = new Trigger
        {
            Property = MenuItem.IsHighlightedProperty,
            Value = true
        };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("TrayHoverBrushKey"), "Bd"));

        var disabled = new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false
        };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.72));

        template.Triggers.Add(hover);
        template.Triggers.Add(disabled);

        return template;
    }

    private static ControlTemplate BuildTrayContentMenuItemTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(MenuItem))
        {
            VisualTree = border
        };

        var hover = new Trigger
        {
            Property = MenuItem.IsHighlightedProperty,
            Value = true
        };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("TrayHoverBrushKey"), "Bd"));

        template.Triggers.Add(hover);
        return template;
    }

    private static Style BuildTrayMenuItemStyle()
    {
        var style = new Style(typeof(MenuItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TrayTextBrushKey")));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 4, 12, 4)));
        style.Setters.Add(new Setter(Control.MinHeightProperty, 24.0));
        style.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Hand));
        style.Setters.Add(new Setter(Control.TemplateProperty, SharedTrayMenuItemTemplate));

        return style;
    }

    private static Style BuildTrayContentMenuItemStyle()
    {
        var style = new Style(typeof(MenuItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TrayTextBrushKey")));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 3, 6, 3)));
        style.Setters.Add(new Setter(Control.MinHeightProperty, 24.0));
        style.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Hand));
        style.Setters.Add(new Setter(Control.TemplateProperty, SharedTrayContentMenuItemTemplate));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

        return style;
    }

    private static Style BuildTrayHeaderStyle()
    {
        var style = new Style(typeof(MenuItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TrayWeakTextBrushKey")));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 2, 12, 2)));
        style.Setters.Add(new Setter(Control.MinHeightProperty, 22.0));
        style.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Arrow));
        style.Setters.Add(new Setter(Control.TemplateProperty, SharedTrayMenuItemTemplate));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));

        return style;
    }

    private ContextMenu CreateTrayMenu()
    {
        var menu = new ContextMenu
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 4, 4, 4),
            HasDropShadow = true,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            MinWidth = 190,
            Template = SharedTrayMenuTemplate
        };
        UpdateTrayMenuResources(menu);
        return menu;
    }

    private void UpdateTrayMenuResources(ContextMenu menu)
    {
        menu.Resources["TrayPaperBrushKey"] = TrayPaperBrush;
        menu.Resources["TrayBorderBrushKey"] = TrayBorderBrush;
        menu.Resources["TrayTextBrushKey"] = TrayTextBrush;
        menu.Resources["TrayWeakTextBrushKey"] = TrayWeakTextBrush;
        menu.Resources["TrayHoverBrushKey"] = TrayHoverBrush;
    }

    private void RebuildTrayMenu()
    {
        if (_trayMenu == null)
        {
            return;
        }

        UpdateTrayMenuResources(_trayMenu);

        _trayMenu.Background = TrayPaperBrush;
        _trayMenu.BorderBrush = TrayBorderBrush;
        _trayMenu.Foreground = TrayTextBrush;

        _trayMenu.Items.Clear();

        _trayMenu.Items.Add(TrayHeader(AppDisplayName));
        _trayMenu.Items.Add(TrayItem(Strings.Get("TrayNewTodo"), () => CreatePaper(PaperTypes.Todo, show: true)));
        _trayMenu.Items.Add(TrayItem(Strings.Get("TrayNewNote"), () => CreatePaper(PaperTypes.Note, show: true)));
        _trayMenu.Items.Add(TraySeparator());

        _trayMenu.Items.Add(TrayHeader(Strings.Get("TrayThemeMode")));

        var themeMenuItem = new MenuItem
        {
            Header = CreateThemeSegmentSelector(),
            Template = SharedSegmentMenuItemTemplate,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Focusable = false,
            IsTabStop = false
        };
        _trayMenu.Items.Add(themeMenuItem);

        var startupPrefix = SystemSettingsHelper.IsStartupEnabled() ? "☑ " : "☐ ";
        _trayMenu.Items.Add(TrayItem(startupPrefix + Strings.Get("TrayStartup"), ToggleStartup));

        var capsulePrefix = State.UseCapsuleMode ? "☑ " : "☐ ";
        _trayMenu.Items.Add(TrayItem(capsulePrefix + Strings.Get("TrayCapsuleMode"), ToggleCapsuleMode));
        var deepCapsulePrefix = State.UseDeepCapsuleMode ? "☑ " : "☐ ";
        _trayMenu.Items.Add(TrayItem(deepCapsulePrefix + Strings.Get("TrayDeepCapsuleMode"), ToggleDeepCapsuleMode));
        _trayMenu.Items.Add(TraySeparator());

        _trayMenu.Items.Add(TrayItem(Strings.Get("TrayShowAll"), ShowAllPapers));
        _trayMenu.Items.Add(TrayItem(Strings.Get("TrayHideAll"), HideAllPapers));

        if (State.Papers.Count > 0)
        {
            _trayMenu.Items.Add(TraySeparator());
            _trayMenu.Items.Add(TrayHeader(Strings.Get("TrayPapers")));

            for (var index = 0; index < State.Papers.Count; index++)
            {
                var paper = State.Papers[index];
                _trayMenu.Items.Add(TrayPaperItem(paper, index + 1));
            }
        }

        _trayMenu.Items.Add(TraySeparator());
        _trayMenu.Items.Add(TrayItem(Strings.Get("TrayExit"), Exit));
    }

    private void SetTheme(string theme)
    {
        State.Theme = theme;
        SaveNow();

        foreach (var window in _windows.Values)
        {
            window.UpdateTheme();
        }

        RebuildTrayMenu();
    }

    private UIElement CreateThemeSegmentSelector()
    {
        var container = new Border
        {
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Margin = new Thickness(10, 2, 10, 4),
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var segments = new[]
        {
            ("system", Strings.Get("ThemeSystem")),
            ("light", Strings.Get("ThemeLight")),
            ("dark", Strings.Get("ThemeDark"))
        };

        for (int i = 0; i < segments.Length; i++)
        {
            var themeKey = segments[i].Item1;
            var label = segments[i].Item2;
            var isActive = State.Theme == themeKey;

            var segmentBorder = new Border
            {
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(1),
                Background = isActive ? Theme.ActiveBrush : Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var textBlock = new TextBlock
            {
                Text = label,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isActive ? TrayPaperBrush : TrayTextBrush
            };

            segmentBorder.Child = textBlock;

            if (!isActive)
            {
                segmentBorder.MouseEnter += (_, _) =>
                {
                    segmentBorder.Background = TrayHoverBrush;
                };
                segmentBorder.MouseLeave += (_, _) =>
                {
                    segmentBorder.Background = Brushes.Transparent;
                };
            }

            segmentBorder.MouseLeftButtonDown += (_, _) =>
            {
                if (_trayMenu != null)
                {
                    _trayMenu.IsOpen = false;
                }
                SetTheme(themeKey);
            };

            Grid.SetColumn(segmentBorder, i);
            grid.Children.Add(segmentBorder);
        }

        container.Child = grid;
        return container;
    }

    private void RefreshTrayMenu()
    {
        if (_trayRefreshSuppressionDepth > 0)
        {
            return;
        }

        if (_trayMenu != null && _trayMenu.IsOpen)
        {
            RebuildTrayMenu();
        }
    }

    private static MenuItem TrayItem(string text, Action action)
    {
        var item = new MenuItem
        {
            Header = text,
            Style = SharedTrayMenuItemStyle
        };

        item.Click += (_, _) => Application.Current.Dispatcher.Invoke(action);
        return item;
    }

    private MenuItem TrayPaperItem(PaperData paper, int index)
    {
        var item = new MenuItem
        {
            Style = SharedTrayContentMenuItemStyle,
            StaysOpenOnClick = true
        };

        var grid = new Grid
        {
            MinWidth = 168
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var prefix = IsPaperShown(paper) ? "☑ " : "☐ ";
        var normalLabelText = prefix + PaperLabel(paper, index);
        var label = new TextBlock
        {
            Text = normalLabelText,
            Foreground = TrayTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 142,
            VerticalAlignment = VerticalAlignment.Center
        };

        var deleteText = new TextBlock
        {
            Text = "×",
            Foreground = TrayWeakTextBrush,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var deleteArea = new Border
        {
            Width = 24,
            MinHeight = 20,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = deleteText
        };

        var confirmText = new TextBlock
        {
            Text = Strings.Get("TrayInlineConfirmAction"),
            Foreground = System.Windows.Media.Brushes.Red,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var confirmArea = new Border
        {
            Width = 42,
            MinHeight = 20,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Visibility = Visibility.Collapsed,
            Child = confirmText
        };

        bool confirmMode = false;

        static void ResetActionArea(Border area, TextBlock text, Brush foreground)
        {
            area.Background = Brushes.Transparent;
            area.Opacity = 1.0;
            text.Foreground = foreground;
        }

        void ResetDeleteVisual()
        {
            ResetActionArea(deleteArea, deleteText, confirmMode ? TrayTextBrush : TrayWeakTextBrush);
            ResetActionArea(confirmArea, confirmText, System.Windows.Media.Brushes.Red);
        }

        void ExitConfirmMode()
        {
            confirmMode = false;
            label.Text = normalLabelText;
            label.Foreground = TrayTextBrush;
            label.FontWeight = FontWeights.Normal;
            deleteText.Text = "×";
            deleteArea.Width = 24;
            deleteText.FontSize = 14;
            confirmArea.Visibility = Visibility.Collapsed;
            ResetDeleteVisual();
        }

        void EnterConfirmMode()
        {
            confirmMode = true;
            label.Text = Strings.Get("TrayInlineConfirmDelete");
            label.Foreground = System.Windows.Media.Brushes.Red;
            label.FontWeight = FontWeights.SemiBold;
            deleteText.Text = Strings.Get("CommonCancel");
            deleteArea.Width = 42;
            deleteText.FontSize = 12;
            deleteText.Foreground = TrayTextBrush;
            confirmArea.Visibility = Visibility.Visible;
        }

        static void AttachActionVisual(Border area, TextBlock text, Func<Brush> normalForeground, Brush hoverForeground)
        {
            area.MouseEnter += (_, _) =>
            {
                area.Background = TrayHoverBrush;
                text.Foreground = hoverForeground;
            };
            area.MouseLeave += (_, _) => ResetActionArea(area, text, normalForeground());
            area.MouseLeftButtonDown += (_, e) =>
            {
                area.Opacity = 0.72;
                e.Handled = true;
            };
        }

        AttachActionVisual(deleteArea, deleteText, () => confirmMode ? TrayTextBrush : TrayWeakTextBrush, TrayTextBrush);
        AttachActionVisual(confirmArea, confirmText, () => System.Windows.Media.Brushes.Red, System.Windows.Media.Brushes.Red);

        deleteArea.MouseLeftButtonUp += (_, e) =>
        {
            deleteArea.Opacity = 1.0;
            if (!confirmMode)
            {
                EnterConfirmMode();
            }
            else
            {
                ExitConfirmMode();
            }
            e.Handled = true;
        };
        confirmArea.MouseLeftButtonUp += (_, e) =>
        {
            confirmArea.Opacity = 1.0;
            if (confirmMode)
            {
                DeletePaper(paper);
            }
            e.Handled = true;
        };

        Grid.SetColumn(label, 0);
        Grid.SetColumn(confirmArea, 1);
        Grid.SetColumn(deleteArea, 2);
        grid.Children.Add(label);
        grid.Children.Add(confirmArea);
        grid.Children.Add(deleteArea);

        item.Header = grid;
        item.Click += (_, _) =>
        {
            if (confirmMode)
            {
                return;
            }

            Application.Current.Dispatcher.Invoke(() => TogglePaperVisibility(paper));
            if (_trayMenu != null)
            {
                _trayMenu.IsOpen = false;
            }
        };

        return item;
    }

    private static MenuItem TrayHeader(string text)
    {
        return new MenuItem
        {
            Header = text,
            IsEnabled = false,
            Style = SharedTrayHeaderStyle
        };
    }

    private static string AppDisplayName
    {
        get
        {
            var version = typeof(AppController).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion
                .Split('+')[0];
            return string.IsNullOrWhiteSpace(version) ? "PaperTodo" : $"PaperTodo v{version}";
        }
    }

    private static Separator TraySeparator()
    {
        return new Separator
        {
            Margin = new Thickness(8, 3, 8, 3),
            Template = SharedSeparatorTemplate
        };
    }

    private string PaperLabel(PaperData paper, int index)
    {
        var kind = paper.Type == PaperTypes.Note ? Strings.Get("PaperKindNote") : Strings.Get("PaperKindTodo");
        var preview = paper.Type == PaperTypes.Note
            ? FirstMeaningfulLine(paper.Content)
            : FirstTodoPreview(paper);

        if (string.IsNullOrWhiteSpace(preview))
        {
            preview = paper.Type == PaperTypes.Note ? Strings.Get("EmptyNotePaper") : Strings.Get("EmptyTodoPaper");
        }

        preview = CleanPreview(preview);
        if (preview.Length > 24)
        {
            preview = preview[..24] + "…";
        }

        return Strings.Format("PaperLabelFormat", kind, index, preview);
    }

    private static string FirstTodoPreview(PaperData paper)
    {
        var ordered = paper.Items.OrderBy(i => i.Order).ToList();

        var firstOpen = ordered.FirstOrDefault(i => !i.Done && !string.IsNullOrWhiteSpace(i.Text));
        if (firstOpen != null)
        {
            return firstOpen.Text;
        }

        return ordered.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Text))?.Text ?? "";
    }

    private static string FirstMeaningfulLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        return text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "";
    }

    private static string CleanPreview(string text)
    {
        text = LinkRegex().Replace(text, "$1");
        text = HeaderRegex().Replace(text, "");
        text = BulletRegex().Replace(text, "");
        text = QuoteRegex().Replace(text, "");

        return text
            .Replace("**", "")
            .Replace("__", "")
            .Replace("~~", "")
            .Replace("`", "")
            .Replace("- [ ]", "")
            .Replace("- [x]", "")
            .Trim();
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void Exit()
    {
        _isExiting = true;
        _saveTimer.Stop();
        if (_trayMenu != null)
        {
            _trayMenu.IsOpen = false;
        }
        SaveNow(sync: true);

        _trayIcon?.Dispose();
        _trayIcon = null;
        _trayMenu = null;

        foreach (var window in _windows.Values.ToList())
        {
            window.CloseForReal();
        }

        Application.Current.Shutdown();
        Environment.Exit(0);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
        {
            if (State.Theme == "system")
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var window in _windows.Values)
                    {
                        window.UpdateTheme();
                    }
                    RebuildTrayMenu();
                }));
            }
        }
    }

    private void ToggleStartup()
    {
        var enabled = SystemSettingsHelper.IsStartupEnabled();
        if (!SystemSettingsHelper.ToggleStartup(!enabled))
        {
            _trayIcon?.ShowBalloonTip(
                Strings.Get("StartupFailureTitle"),
                Strings.Get("StartupFailureMessage"),
                BalloonIcon.Warning);
        }
        RebuildTrayMenu();
    }

    private void ToggleCapsuleMode()
    {
        State.UseCapsuleMode = !State.UseCapsuleMode;

        foreach (var window in _windows.Values)
        {
            window.UpdateCapsuleMode();
        }

        if (!State.UseCapsuleMode)
        {
            State.UseDeepCapsuleMode = false;
            foreach (var paper in State.Papers)
            {
                paper.IsCollapsed = false;
            }
        }

        ArrangeDeepCapsules();
        SaveNow();
        RebuildTrayMenu();
    }

    private void ToggleDeepCapsuleMode()
    {
        State.UseDeepCapsuleMode = !State.UseDeepCapsuleMode;

        if (State.UseDeepCapsuleMode && !State.UseCapsuleMode)
        {
            State.UseCapsuleMode = true;
            foreach (var window in _windows.Values)
            {
                window.UpdateCapsuleMode();
            }
        }

        foreach (var window in _windows.Values)
        {
            window.UpdateDeepCapsuleMode();
        }

        ArrangeDeepCapsules();
        SaveNow();
        RebuildTrayMenu();
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _saveTimer.Stop();
        if (_trayMenu != null)
        {
            _trayMenu.IsOpen = false;
        }
        _trayIcon?.Dispose();
        _trayIcon = null;
        _trayMenu = null;
    }
}
