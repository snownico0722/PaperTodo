using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Application = System.Windows.Application;

namespace PaperTodo;

public sealed partial class AppController
{
    private void CreateTrayIcon()
    {
        _trayMenu = CreateTrayMenu();

        var trayIcon = new TaskbarIcon();
        trayIcon.Visibility = Visibility.Hidden;
        trayIcon.ToolTipText = "PaperTodo";
        trayIcon.IconSource = LoadTrayIconSource();
        trayIcon.ContextMenu = _trayMenu;
        trayIcon.PreviewTrayContextMenuOpen += (_, _) =>
        {
            if (IsExiting)
            {
                return;
            }

            RebuildTrayMenu();
        };
        trayIcon.TrayMouseDoubleClick += (_, _) =>
        {
            if (IsExiting)
            {
                return;
            }

            Application.Current.Dispatcher.Invoke(ShowAllPapers);
        };

        _trayIcon = trayIcon;

        trayIcon.Visibility = Visibility.Visible;
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
    private static readonly Style SharedTrayToolbarItemStyle = BuildTrayToolbarItemStyle();

    private static ControlTemplate BuildSegmentMenuItemTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        border.AppendChild(presenter);

        return new ControlTemplate(typeof(MenuItem))
        {
            VisualTree = border
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

        var scrollViewer = new FrameworkElementFactory(typeof(ScrollViewer));
        scrollViewer.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scrollViewer.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scrollViewer.SetValue(ScrollViewer.CanContentScrollProperty, false);
        scrollViewer.SetValue(ScrollViewer.PanningModeProperty, PanningMode.VerticalOnly);
        scrollViewer.SetValue(FrameworkElement.MaxHeightProperty, new TemplateBindingExtension(FrameworkElement.MaxHeightProperty));

        var presenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        scrollViewer.AppendChild(presenter);
        border.AppendChild(scrollViewer);

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
        var root = new FrameworkElementFactory(typeof(Grid));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var contentPanel = new FrameworkElementFactory(typeof(DockPanel));
        contentPanel.SetValue(FrameworkElement.MinWidthProperty, 0.0);
        contentPanel.SetValue(DockPanel.LastChildFillProperty, true);

        var arrow = new FrameworkElementFactory(typeof(TextBlock));
        arrow.Name = "SubmenuArrow";
        arrow.SetValue(TextBlock.TextProperty, "›");
        arrow.SetValue(TextBlock.MarginProperty, new Thickness(10, 0, 0, 0));
        arrow.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("TrayWeakTextBrushKey"));
        arrow.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        arrow.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(DockPanel.DockProperty, Dock.Right);
        contentPanel.AppendChild(arrow);

        var content = new FrameworkElementFactory(typeof(TextBlock));
        content.SetBinding(TextBlock.TextProperty, new Binding("Header") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        content.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        content.SetValue(FrameworkElement.MaxWidthProperty, 170.0);
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        contentPanel.AppendChild(content);

        border.AppendChild(contentPanel);
        root.AppendChild(border);

        var popup = new FrameworkElementFactory(typeof(Popup));
        popup.Name = "PART_Popup";
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(Popup.FocusableProperty, false);
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Right);
        popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
        popup.SetBinding(Popup.IsOpenProperty, new Binding("IsSubmenuOpen") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        popup.SetBinding(Popup.PlacementTargetProperty, new Binding { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });

        var popupBorder = new FrameworkElementFactory(typeof(Border));
        popupBorder.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("TrayPaperBrushKey"));
        popupBorder.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("TrayBorderBrushKey"));
        popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        popupBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        popupBorder.SetValue(Border.PaddingProperty, new Thickness(4));
        popupBorder.SetValue(FrameworkElement.MinWidthProperty, 190.0);

        var popupItems = new FrameworkElementFactory(typeof(ItemsPresenter));
        popupItems.SetValue(KeyboardNavigation.DirectionalNavigationProperty, KeyboardNavigationMode.Cycle);
        popupBorder.AppendChild(popupItems);
        popup.AppendChild(popupBorder);
        root.AppendChild(popup);

        var template = new ControlTemplate(typeof(MenuItem))
        {
            VisualTree = root
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

        var hasItems = new Trigger
        {
            Property = ItemsControl.HasItemsProperty,
            Value = true
        };
        hasItems.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "SubmenuArrow"));

        template.Triggers.Add(hover);
        template.Triggers.Add(disabled);
        template.Triggers.Add(hasItems);

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

    private static Style BuildTrayToolbarItemStyle()
    {
        var style = new Style(typeof(MenuItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TrayWeakTextBrushKey")));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 2, 6, 2)));
        style.Setters.Add(new Setter(Control.MinHeightProperty, 26.0));
        style.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Arrow));
        style.Setters.Add(new Setter(Control.TemplateProperty, SharedSegmentMenuItemTemplate));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

        return style;
    }

    private ContextMenu CreateTrayMenu()
    {
        var menu = new ContextMenu
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 4, 4, 4),
            HasDropShadow = true,
            FontFamily = AppTypography.UiFontFamily,
            Language = AppTypography.Language,
            FontSize = AppTypography.Scale(13),
            Focusable = true,
            MinWidth = 190,
            MaxHeight = TrayMenuMaxHeight(),
            Template = SharedTrayMenuTemplate
        };
        AppTypography.ApplyTextRendering(menu);
        UpdateTrayMenuResources(menu);
        menu.Opened += (_, _) => ActivateTrayContextMenu(menu);
        return menu;
    }

    private static void ActivateTrayContextMenu(ContextMenu menu)
    {
        void Activate()
        {
            if (!menu.IsOpen)
            {
                return;
            }

            _ = menu.Focus();
            _ = Keyboard.Focus(menu);
            if (PresentationSource.FromVisual(menu) is HwndSource source)
            {
                WindowNative.TrySetForegroundWindow(source.Handle);
            }
        }

        Activate();
        _ = menu.Dispatcher.InvokeAsync(Activate, DispatcherPriority.Input);
    }

    private static double TrayMenuMaxHeight()
    {
        return Math.Max(260, SystemParameters.WorkArea.Height - 72);
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

        InvalidateSystemThemeCacheIfNeeded();
        UpdateTrayMenuResources(_trayMenu);

        _trayMenu.Background = TrayPaperBrush;
        _trayMenu.BorderBrush = TrayBorderBrush;
        _trayMenu.Foreground = TrayTextBrush;
        _trayMenu.FontFamily = AppTypography.UiFontFamily;
        _trayMenu.FontSize = AppTypography.Scale(13);
        _trayMenu.Language = AppTypography.Language;
        AppTypography.ApplyTextRendering(_trayMenu);
        _trayMenu.MaxHeight = TrayMenuMaxHeight();

        _trayMenu.Items.Clear();

        _trayMenu.Items.Add(TrayTitleBar());
        _trayMenu.Items.Add(TraySeparator());

        _trayMenu.Items.Add(TrayPaperToolbar());

        if (State.Papers.Count > 0)
        {
            for (var index = 0; index < State.Papers.Count; index++)
            {
                var paper = State.Papers[index];
                _trayMenu.Items.Add(TrayPaperItem(paper));
            }
        }

        _trayMenu.Items.Add(TraySeparator());
        _trayMenu.Items.Add(TrayItem(Strings.Get("TrayExit"), Exit));
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

    private MenuItem TrayItem(string text, Action action)
    {
        var item = new MenuItem
        {
            Header = text,
            Style = SharedTrayMenuItemStyle
        };

        item.Click += (_, _) =>
        {
            InvokeTrayAction(action);
        };
        return item;
    }

    private void InvokeTrayAction(Action action)
    {
        if (_trayMenu != null)
        {
            _trayMenu.IsOpen = false;
        }
        _ = Application.Current.Dispatcher.InvokeAsync(action, DispatcherPriority.Background);
    }

    private MenuItem TrayTitleBar()
    {
        var item = new MenuItem
        {
            Style = SharedTrayToolbarItemStyle,
            StaysOpenOnClick = true
        };

        var grid = new Grid
        {
            MinWidth = 168
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        KeyboardNavigation.SetTabNavigation(grid, KeyboardNavigationMode.Cycle);

        var label = new TextBlock
        {
            Text = AppDisplayName,
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(12),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(3, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var settingsTip = Strings.Get("TraySettings");
        var settingsButton = TrayToolbarAction(
            CreateTraySettingsIcon(),
            settingsTip,
            ShowSettingsWindow);

        Grid.SetColumn(label, 0);
        Grid.SetColumn(settingsButton, 1);
        grid.Children.Add(label);
        grid.Children.Add(settingsButton);

        item.Header = grid;
        item.Click += (_, e) => e.Handled = true;
        item.PreviewKeyDown += (_, e) =>
        {
            if (e.OriginalSource == item && (e.Key is Key.Enter or Key.Space))
            {
                InvokeTrayAction(ShowSettingsWindow);
                e.Handled = true;
            }
        };
        return item;
    }

    private MenuItem TrayPaperToolbar()
    {
        var paperCount = State.Papers.Count;
        var shownCount = State.Papers.Count(IsPaperShown);
        var anyShown = shownCount > 0;
        var allShown = paperCount > 0 && shownCount == paperCount;

        var item = new MenuItem
        {
            Style = SharedTrayToolbarItemStyle,
            StaysOpenOnClick = true
        };

        var grid = new Grid
        {
            MinWidth = 168
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        KeyboardNavigation.SetTabNavigation(grid, KeyboardNavigationMode.Cycle);

        var visibilityTip = Strings.Get(anyShown ? "TrayHideAll" : "TrayShowAll");
        Action visibilityAction = anyShown ? HideAllPapers : ShowAllPapers;
        var visibilityButton = TrayToolbarAction(
            CreateTrayVisibilityIcon(anyShown, allShown),
            visibilityTip,
            visibilityAction,
            isEnabled: paperCount > 0);

        var label = new TextBlock
        {
            Text = Strings.Get("TrayPapers"),
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(12),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(3, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var newTodoButton = TrayToolbarAction(
            CreateTrayAddIcon("✓"),
            Strings.Get("TrayNewTodo"),
            () => CreatePaper(PaperTypes.Todo, show: true));
        var newNoteButton = TrayToolbarAction(
            CreateTrayAddIcon("✎"),
            Strings.Get("TrayNewNote"),
            () => CreatePaper(PaperTypes.Note, show: true));

        Grid.SetColumn(visibilityButton, 0);
        Grid.SetColumn(label, 1);
        Grid.SetColumn(newTodoButton, 2);
        Grid.SetColumn(newNoteButton, 3);
        grid.Children.Add(visibilityButton);
        grid.Children.Add(label);
        grid.Children.Add(newTodoButton);
        grid.Children.Add(newNoteButton);

        item.Header = grid;
        item.Click += (_, e) => e.Handled = true;
        item.PreviewKeyDown += (_, e) =>
        {
            if (paperCount > 0 && e.OriginalSource == item && (e.Key is Key.Enter or Key.Space))
            {
                InvokeTrayAction(visibilityAction);
                e.Handled = true;
            }
        };
        return item;
    }

    private Border TrayToolbarAction(
        FrameworkElement icon,
        string toolTip,
        Action action,
        bool isEnabled = true)
    {
        const double normalOpacity = 0.74;

        var area = new Border
        {
            Width = 26,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = isEnabled ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow,
            Focusable = isEnabled,
            IsHitTestVisible = isEnabled,
            Opacity = isEnabled ? normalOpacity : 0.28,
            ToolTip = isEnabled ? toolTip : null,
            Child = icon
        };
        AutomationProperties.SetName(area, toolTip);
        KeyboardNavigation.SetIsTabStop(area, isEnabled);
        ToolTipService.SetInitialShowDelay(area, 300);

        if (!isEnabled)
        {
            return area;
        }

        void ResetVisual()
        {
            area.Background = area.IsKeyboardFocusWithin ? TrayHoverBrush : Brushes.Transparent;
            area.Opacity = area.IsKeyboardFocusWithin ? 1.0 : normalOpacity;
        }

        void InvokeAction()
        {
            InvokeTrayAction(action);
        }

        area.MouseEnter += (_, _) =>
        {
            area.Background = TrayHoverBrush;
            area.Opacity = 1.0;
        };
        area.MouseLeave += (_, _) => ResetVisual();
        area.GotKeyboardFocus += (_, _) =>
        {
            area.Background = TrayHoverBrush;
            area.Opacity = 1.0;
        };
        area.LostKeyboardFocus += (_, _) => ResetVisual();
        area.PreviewMouseLeftButtonDown += (_, e) =>
        {
            area.CaptureMouse();
            area.Opacity = 0.58;
            e.Handled = true;
        };
        area.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var point = Mouse.GetPosition(area);
            var releasedInside = point.X >= 0 &&
                point.Y >= 0 &&
                point.X <= area.ActualWidth &&
                point.Y <= area.ActualHeight;
            if (area.IsMouseCaptured)
            {
                area.ReleaseMouseCapture();
            }
            ResetVisual();
            if (releasedInside)
            {
                InvokeAction();
            }
            e.Handled = true;
        };
        area.LostMouseCapture += (_, _) => ResetVisual();
        area.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                InvokeAction();
                e.Handled = true;
            }
        };

        return area;
    }

    private FrameworkElement CreateTraySettingsIcon()
    {
        return new System.Windows.Shapes.Path
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Data = Geometry.Parse(
                "M 6.1,0.7 L 7.9,0.7 L 8.25,2.35 C 8.72,2.5 9.15,2.72 9.53,3 L 11.1,2.2 L 12.3,3.4 L 11.5,4.97 " +
                "C 11.78,5.35 12,5.78 12.15,6.25 L 13.8,6.6 L 13.8,8.4 L 12.15,8.75 C 12,9.22 11.78,9.65 11.5,10.03 " +
                "L 12.3,11.6 L 11.1,12.8 L 9.53,12 L 8.25,12.65 L 7.9,14.3 L 6.1,14.3 L 5.75,12.65 C 5.28,12.5 4.85,12.28 " +
                "4.47,12 L 2.9,12.8 L 1.7,11.6 L 2.5,10.03 C 2.22,9.65 2,9.22 1.85,8.75 L 0.2,8.4 L 0.2,6.6 L 1.85,6.25 " +
                "C 2,5.78 2.22,5.35 2.5,4.97 L 1.7,3.4 L 2.9,2.2 L 4.47,3 C 4.85,2.72 5.28,2.5 5.75,2.35 Z M 7,4.6 " +
                "C 5.67,4.6 4.6,5.67 4.6,7 C 4.6,8.33 5.67,9.4 7,9.4 C 8.33,9.4 9.4,8.33 9.4,7 C 9.4,5.67 8.33,4.6 7,4.6 Z"),
            Stroke = TrayTextBrush,
            StrokeThickness = 1.2,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private FrameworkElement CreateTrayVisibilityIcon(bool anyShown, bool allShown)
    {
        var icon = new Grid
        {
            Width = 16,
            Height = 16
        };

        var outline = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 1.4,8 C 3.2,4.8 5.5,3.1 8,3.1 C 10.5,3.1 12.8,4.8 14.6,8 C 12.8,11.2 10.5,12.9 8,12.9 C 5.5,12.9 3.2,11.2 1.4,8 Z"),
            Stroke = TrayTextBrush,
            StrokeThickness = 1.35,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent
        };
        var pupil = new System.Windows.Shapes.Ellipse
        {
            Width = 4,
            Height = 4,
            Fill = TrayTextBrush,
            Opacity = allShown ? 1.0 : 0.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.Children.Add(outline);
        icon.Children.Add(pupil);

        if (!anyShown)
        {
            icon.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 2.2,2.2 L 13.8,13.8"),
                Stroke = TrayTextBrush,
                StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        return icon;
    }

    private FrameworkElement CreateTrayAddIcon(string glyph)
    {
        var icon = new Grid
        {
            Width = 17,
            Height = 17
        };
        icon.Children.Add(new TextBlock
        {
            Text = glyph,
            Foreground = TrayTextBrush,
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = AppTypography.Scale(12),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 4, 3),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        icon.Children.Add(new TextBlock
        {
            Text = "+",
            Foreground = TrayTextBrush,
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = AppTypography.Scale(9),
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        });
        return icon;
    }

    private MenuItem TrayPaperItem(PaperData paper)
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

        var labelPanel = new Grid();
        var checkColumn = new ColumnDefinition { Width = new GridLength(18) };
        var iconColumn = new ColumnDefinition { Width = new GridLength(22) };
        labelPanel.ColumnDefinitions.Add(checkColumn);
        labelPanel.ColumnDefinitions.Add(iconColumn);
        labelPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var checkMark = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 3,6.8 L 5.7,9.4 L 10.2,4"),
            Stroke = TrayPaperBrush,
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Visibility = IsPaperShown(paper) ? Visibility.Visible : Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var checkBoxHost = new Grid();
        checkBoxHost.Children.Add(checkMark);

        var checkBox = new Border
        {
            Width = 13,
            Height = 13,
            CornerRadius = new CornerRadius(3),
            BorderThickness = IsPaperShown(paper) ? new Thickness(0) : new Thickness(1.3),
            BorderBrush = TrayWeakTextBrush,
            Background = IsPaperShown(paper) ? Theme.ActiveBrush : Brushes.Transparent,
            Opacity = IsPaperShown(paper) ? 0.92 : 0.72,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = checkBoxHost
        };

        var iconText = new TextBlock
        {
            Text = PaperTypeIcon(paper),
            Foreground = TrayTextBrush,
            Opacity = 0.82,
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = PaperTypeIconFontSize(paper),
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var normalLabelText = PaperLabel(paper);
        var label = new TextBlock
        {
            Text = normalLabelText,
            Foreground = TrayTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 118,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(checkBox, 0);
        Grid.SetColumn(iconText, 1);
        Grid.SetColumn(label, 2);
        labelPanel.Children.Add(checkBox);
        labelPanel.Children.Add(iconText);
        labelPanel.Children.Add(label);

        var deleteText = new TextBlock
        {
            Text = "×",
            Foreground = TrayWeakTextBrush,
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = AppTypography.Scale(14),
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
            FontSize = AppTypography.Scale(12),
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
        bool suppressRowClick = false;
        int suppressRowClickToken = 0;
        ProcessInputEventHandler? rowClickSuppressionInputHandler = null;

        static void ResetActionArea(Border area, TextBlock text, Brush foreground)
        {
            area.Background = Brushes.Transparent;
            area.Opacity = 1.0;
            text.Foreground = foreground;
        }

        void RemoveRowClickSuppressionInputHandler()
        {
            if (rowClickSuppressionInputHandler == null)
            {
                return;
            }

            InputManager.Current.PostProcessInput -= rowClickSuppressionInputHandler;
            rowClickSuppressionInputHandler = null;
        }

        void QueueClearRowClickSuppression(int token)
        {
            _ = item.Dispatcher.InvokeAsync(() =>
            {
                if (token == suppressRowClickToken && Mouse.LeftButton != MouseButtonState.Pressed)
                {
                    suppressRowClick = false;
                }
            }, DispatcherPriority.ContextIdle);
        }

        void BeginActionGesture(Border area)
        {
            suppressRowClick = true;
            suppressRowClickToken++;
            RemoveRowClickSuppressionInputHandler();
            var token = suppressRowClickToken;
            rowClickSuppressionInputHandler = (_, _) =>
            {
                if (token != suppressRowClickToken)
                {
                    RemoveRowClickSuppressionInputHandler();
                    return;
                }

                if (Mouse.LeftButton != MouseButtonState.Pressed)
                {
                    RemoveRowClickSuppressionInputHandler();
                    QueueClearRowClickSuppression(token);
                }
            };
            InputManager.Current.PostProcessInput += rowClickSuppressionInputHandler;
            area.CaptureMouse();
        }

        void EndActionGesture(Border area)
        {
            if (area.IsMouseCaptured)
            {
                area.ReleaseMouseCapture();
            }

            var token = suppressRowClickToken;
            if (Mouse.LeftButton != MouseButtonState.Pressed)
            {
                RemoveRowClickSuppressionInputHandler();
                QueueClearRowClickSuppression(token);
            }
        }

        static bool PointerIsInside(FrameworkElement element)
        {
            var p = Mouse.GetPosition(element);
            return p.X >= 0 &&
                p.Y >= 0 &&
                p.X <= element.ActualWidth &&
                p.Y <= element.ActualHeight;
        }

        void ResetDeleteVisual()
        {
            ResetActionArea(deleteArea, deleteText, confirmMode ? TrayTextBrush : TrayWeakTextBrush);
            ResetActionArea(confirmArea, confirmText, System.Windows.Media.Brushes.Red);
        }

        void ExitConfirmMode()
        {
            confirmMode = false;
            checkColumn.Width = new GridLength(18);
            iconColumn.Width = new GridLength(22);
            checkBox.Visibility = Visibility.Visible;
            iconText.Visibility = Visibility.Visible;
            label.Text = normalLabelText;
            label.Foreground = TrayTextBrush;
            label.FontWeight = FontWeights.Normal;
            deleteText.Text = "×";
            deleteArea.Width = 24;
            deleteText.FontSize = AppTypography.Scale(14);
            confirmArea.Visibility = Visibility.Collapsed;
            ResetDeleteVisual();
        }

        void EnterConfirmMode()
        {
            confirmMode = true;
            checkColumn.Width = new GridLength(0);
            iconColumn.Width = new GridLength(0);
            checkBox.Visibility = Visibility.Collapsed;
            iconText.Visibility = Visibility.Collapsed;
            label.Text = Strings.Get("TrayInlineConfirmDelete");
            label.Foreground = System.Windows.Media.Brushes.Red;
            label.FontWeight = FontWeights.SemiBold;
            deleteText.Text = Strings.Get("CommonCancel");
            deleteArea.Width = 42;
            deleteText.FontSize = AppTypography.Scale(12);
            deleteText.Foreground = TrayTextBrush;
            confirmArea.Visibility = Visibility.Visible;
        }

        void AttachActionVisual(Border area, TextBlock text, Func<Brush> normalForeground, Brush hoverForeground)
        {
            area.MouseEnter += (_, _) =>
            {
                area.Background = TrayHoverBrush;
                text.Foreground = hoverForeground;
            };
            area.MouseLeave += (_, _) => ResetActionArea(area, text, normalForeground());
            area.PreviewMouseLeftButtonDown += (_, e) =>
            {
                BeginActionGesture(area);
                area.Opacity = 0.72;
                e.Handled = true;
            };
            area.LostMouseCapture += (_, _) =>
            {
                area.Opacity = 1.0;
                EndActionGesture(area);
            };
        }

        AttachActionVisual(deleteArea, deleteText, () => confirmMode ? TrayTextBrush : TrayWeakTextBrush, TrayTextBrush);
        AttachActionVisual(confirmArea, confirmText, () => System.Windows.Media.Brushes.Red, System.Windows.Media.Brushes.Red);

        deleteArea.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var releasedInside = PointerIsInside(deleteArea);
            deleteArea.Opacity = 1.0;
            EndActionGesture(deleteArea);
            if (!releasedInside)
            {
                e.Handled = true;
                return;
            }

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
        confirmArea.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var releasedInside = PointerIsInside(confirmArea);
            confirmArea.Opacity = 1.0;
            EndActionGesture(confirmArea);
            if (confirmMode && releasedInside)
            {
                _ = Application.Current.Dispatcher.InvokeAsync(() => DeletePaper(paper), DispatcherPriority.Background);
            }
            e.Handled = true;
        };

        Grid.SetColumn(labelPanel, 0);
        Grid.SetColumn(confirmArea, 1);
        Grid.SetColumn(deleteArea, 2);
        grid.Children.Add(labelPanel);
        grid.Children.Add(confirmArea);
        grid.Children.Add(deleteArea);

        item.Header = grid;
        item.Click += (_, e) =>
        {
            if (suppressRowClick || confirmMode)
            {
                e.Handled = true;
                return;
            }

            if (_trayMenu != null)
            {
                _trayMenu.IsOpen = false;
            }
            _ = Application.Current.Dispatcher.InvokeAsync(() => TogglePaperVisibility(paper), DispatcherPriority.Background);
            e.Handled = true;
        };

        return item;
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

    private string PaperLabel(PaperData paper)
    {
        return PaperCapsuleTitle(paper);
    }

    private string PaperTypeIcon(PaperData paper)
    {
        if (paper.Type == PaperTypes.Note && IsCurrentScriptCapsule(paper))
        {
            return "⚡";
        }

        return paper.Type == PaperTypes.Note ? "✎" : "✓";
    }

    private double PaperTypeIconFontSize(PaperData paper)
    {
        return AppTypography.Scale(
            paper.Type == PaperTypes.Note && IsCurrentScriptCapsule(paper)
                ? 15.0
                : 14.0);
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

}
