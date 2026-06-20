using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
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
        trayIcon.PreviewTrayContextMenuOpen += (_, _) => RebuildTrayMenu();
        trayIcon.TrayMouseDoubleClick += (_, _) =>
        {
            Application.Current.Dispatcher.Invoke(ShowAllPapers);
        };

        _trayIcon = trayIcon;

        RebuildTrayMenu();

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
        arrow.SetValue(TextBlock.FontSizeProperty, 13.0);
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
            Focusable = true,
            MinWidth = 190,
            MaxHeight = TrayMenuMaxHeight(),
            Template = SharedTrayMenuTemplate
        };
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

        UpdateTrayMenuResources(_trayMenu);

        _trayMenu.Background = TrayPaperBrush;
        _trayMenu.BorderBrush = TrayBorderBrush;
        _trayMenu.Foreground = TrayTextBrush;
        _trayMenu.MaxHeight = TrayMenuMaxHeight();

        _trayMenu.Items.Clear();

        _trayMenu.Items.Add(TrayHeader(AppDisplayName));
        _trayMenu.Items.Add(TrayItem(Strings.Get("TrayNewTodo"), () => CreatePaper(PaperTypes.Todo, show: true)));
        _trayMenu.Items.Add(TrayItem(Strings.Get("TrayNewNote"), () => CreatePaper(PaperTypes.Note, show: true)));
        _trayMenu.Items.Add(TraySeparator());

        _trayMenu.Items.Add(TrayItem(Strings.Get("TraySettings"), ShowSettingsWindow));
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
            if (_trayMenu != null)
            {
                _trayMenu.IsOpen = false;
            }
            _ = Application.Current.Dispatcher.InvokeAsync(action, DispatcherPriority.Background);
        };
        return item;
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
            deleteText.FontSize = 14;
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
            deleteText.FontSize = 12;
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

    private string PaperLabel(PaperData paper)
    {
        return PaperCapsuleTitle(paper);
    }

    private static string PaperTypeIcon(PaperData paper)
    {
        if (paper.Type == PaperTypes.Note && PaperWindow.IsScriptCapsuleContent(paper.Content))
        {
            return "⚡";
        }

        return paper.Type == PaperTypes.Note ? "✎" : "✓";
    }

    private static double PaperTypeIconFontSize(PaperData paper)
    {
        return paper.Type == PaperTypes.Note && PaperWindow.IsScriptCapsuleContent(paper.Content)
            ? 15.0
            : 14.0;
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

}
