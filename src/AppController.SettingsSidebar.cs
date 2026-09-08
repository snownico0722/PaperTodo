using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private readonly Dictionary<SettingsPage, double> _settingsPageScrollOffsets = new();

    private void RefreshSettingsWindowContent()
    {
        RefreshPluginPopupThemeForSettingsRefresh();

        if (_settingsWindow is not { } window)
        {
            return;
        }

        // Focus loss after detaching the old tree is deferred. Commit the active editor
        // before its replacement reads State, so a theme/display refresh keeps the draft.
        // Unfocused editors may still show values superseded by a page-defaults restore.
        if (_settingsExternalMarkdownTextBox is { IsKeyboardFocusWithin: true } editor)
        {
            CommitExternalMarkdownExtension(editor);
        }

        // The viewer belongs to the page that was displayed before navigation or refresh.
        // A tree rebuilt again before Loaded has not restored its saved offset yet.
        if (_settingsPageScrollViewerPage is { } displayedPage &&
            _settingsPageScrollViewer is { IsLoaded: true } previousViewer)
        {
            _settingsPageScrollOffsets[displayedPage] = previousViewer.VerticalOffset;
        }

        if (!State.AdvancedSettingsMode && _settingsPage == SettingsPage.Labs)
        {
            _settingsPage = SettingsPage.General;
            _shortcutRecordingCommandId = null;
            ClearShortcutApplyFailure();
        }
        if (SupportsShortcutRecording(_settingsPage))
        {
            EnsureShortcutDraft();
        }

        InvalidateSystemThemeCacheIfNeeded();
        _settingsRegionRefreshers.Clear();
        _pluginStatusRefreshers.Clear();
        _settingsExternalMarkdownTextBox = null;
        _settingsHidePapersFromTaskbarCheckBox = null;
        _settingsHidePapersFromWindowSwitcherCheckBox = null;
        _settingsCapsuleModeCheckBox = null;
        _settingsDeepCapsuleModeCheckBox = null;
        _settingsDeepCapsuleExpandedSlotCheckBox = null;
        _settingsRememberDeepCapsuleExpandedPositionCheckBox = null;
        _settingsCollapseExpandedDeepCapsuleOnClickCheckBox = null;
        _settingsCapsuleCollapseAllCheckBox = null;

        window.Title = Strings.Get("TraySettings");
        window.SizeToContent = SizeToContent.Manual;
        window.FontFamily = AppTypography.UiFontFamily;
        window.FontSize = AppTypography.Scale(12);
        window.Language = AppTypography.Language;
        AppTypography.ApplyTextRendering(window);
        window.Content = BuildSettingsSidebarWindowContent(window);
        ApplyToolTipSetting(window);
        ApplySettingsSidebarFrame(window);
    }

    private UIElement BuildSettingsSidebarWindowContent(Window window)
    {
        var frame = new Border
        {
            Background = TrayPaperBrush,
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            SnapsToDevicePixels = true
        };

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(158) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var navigation = BuildSettingsSidebarNavigation(window);
        Grid.SetColumn(navigation, 0);
        root.Children.Add(navigation);

        var separator = new Border
        {
            Background = TrayBorderBrush,
            Opacity = 0.65
        };
        Grid.SetColumn(separator, 1);
        root.Children.Add(separator);

        var pageArea = new Grid();
        pageArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        pageArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(pageArea, 2);
        root.Children.Add(pageArea);

        var closeRow = BuildSettingsSidebarCloseRow(window);
        Grid.SetRow(closeRow, 0);
        pageArea.Children.Add(closeRow);

        var pageHost = BuildSettingsPageHost();
        Grid.SetRow(pageHost, 1);
        pageArea.Children.Add(pageHost);

        frame.Child = root;

        var checkMarkAlignmentQueued = false;
        void QueueCheckMarkAlignment()
        {
            if (checkMarkAlignmentQueued)
            {
                return;
            }

            checkMarkAlignmentQueued = true;
            frame.Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    checkMarkAlignmentQueued = false;
                    if (frame.IsLoaded)
                    {
                        AlignSettingsCheckMarks(frame);
                    }
                }),
                DispatcherPriority.Loaded);
        }

        frame.Loaded += (_, _) => QueueCheckMarkAlignment();

        var registeredRefreshers =
            new List<KeyValuePair<string, Action>>(_settingsRegionRefreshers);
        foreach (var entry in registeredRefreshers)
        {
            var refresh = entry.Value;
            _settingsRegionRefreshers[entry.Key] = () =>
            {
                refresh();
                QueueCheckMarkAlignment();
            };
        }

        return frame;
    }

    private Grid BuildSettingsSidebarTitleRow(Window window)
    {
        var titleRow = new Grid
        {
            Height = 44,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            Margin = new Thickness(14, 4, 10, 0)
        };
        AttachSettingsSidebarDragBehavior(titleRow, window);

        titleRow.Children.Add(new TextBlock
        {
            Text = Strings.Get("TraySettings"),
            Foreground = TrayTextBrush,
            FontSize = AppTypography.Scale(15),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        return titleRow;
    }

    private Grid BuildSettingsSidebarCloseRow(Window window)
    {
        var closeRow = new Grid
        {
            Height = 20,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll
        };
        AttachSettingsSidebarDragBehavior(closeRow, window);

        var closeGlyph = new Path
        {
            Data = Geometry.Parse("M 1,1 L 7,7 M 7,1 L 1,7"),
            Stroke = TrayWeakTextBrush,
            StrokeThickness = 1.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 8,
            Height = 8,
            Stretch = Stretch.None,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var closeButton = new Button
        {
            Content = closeGlyph,
            Width = 30,
            Height = 18,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 1, 1, 0),
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            BorderBrush = TrayBorderBrush,
            Cursor = Cursors.Hand,
            Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = BuildSettingsWindowCloseButtonStyle()
        };
        closeButton.Click += (_, _) => window.Close();
        closeRow.Children.Add(closeButton);
        return closeRow;
    }

    private Style BuildSettingsWindowCloseButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, TrayBorderBrush));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(
            Border.BackgroundProperty,
            new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(
            Border.BorderBrushProperty,
            new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(
            Border.BorderThicknessProperty,
            new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(
            ContentPresenter.ContentProperty,
            new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(
            FrameworkElement.HorizontalAlignmentProperty,
            HorizontalAlignment.Center);
        content.SetValue(
            FrameworkElement.VerticalAlignmentProperty,
            VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button))
        {
            VisualTree = border
        };
        var hover = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, TrayHoverBrush));
        template.Triggers.Add(hover);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static void AlignSettingsCheckMarks(DependencyObject root)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is Path { Name: "CheckMark" } checkMark)
            {
                checkMark.RenderTransform = new TranslateTransform(-0.5, -0.5);
            }
            AlignSettingsCheckMarks(child);
        }
    }

    private void AttachSettingsSidebarDragBehavior(UIElement surface, Window window)
    {
        surface.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left)
            {
                return;
            }

            var previousWorkArea = WindowWorkAreaHelper.WorkAreaFor(window);
            try
            {
                window.DragMove();
            }
            catch
            {
                // DragMove can throw if the button is released before WPF enters the move loop.
            }

            if (!previousWorkArea.Equals(WindowWorkAreaHelper.WorkAreaFor(window)))
            {
                RefreshSettingsSidebarAfterMonitorChange(window);
            }
        };
    }

    private UIElement BuildSettingsSidebarNavigation(Window window)
    {
        var shell = new Border
        {
            Background = Theme.Tint((byte)(Theme.IsDark ? 12 : 8)),
            CornerRadius = new CornerRadius(9, 0, 0, 9),
            Margin = new Thickness(1, 1, 0, 1)
        };
        var root = new DockPanel
        {
            LastChildFill = true,
            Background = Brushes.Transparent
        };
        shell.Child = root;

        var titleRow = BuildSettingsSidebarTitleRow(window);
        DockPanel.SetDock(titleRow, Dock.Top);
        root.Children.Add(titleRow);

        var footer = new StackPanel
        {
            Margin = new Thickness(12, 8, 12, 12)
        };
        footer.Children.Add(new Border
        {
            Height = 1,
            Background = TrayBorderBrush,
            Opacity = 0.55,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var advancedModeToggle = SettingsToggle(
            Strings.Get("SettingsAdvancedMode"),
            State.AdvancedSettingsMode,
            ToggleAdvancedSettingsMode);
        advancedModeToggle.FontSize = AppTypography.Scale(11.5);
        advancedModeToggle.Margin = new Thickness(2, 2, 0, 0);
        advancedModeToggle.ToolTip = BuildSettingsHintTooltip(Strings.Get("TipAdvancedSettingsMode"));
        footer.Children.Add(advancedModeToggle);

        footer.Children.Add(BuildSettingsSignature());
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var navigationItems = new StackPanel
        {
            Margin = new Thickness(10, 10, 10, 0)
        };
        foreach (var page in SettingsPages())
        {
            navigationItems.Children.Add(BuildSettingsSidebarNavigationItem(page));
        }
        root.Children.Add(new ScrollViewer
        {
            Content = navigationItems,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            PanningMode = PanningMode.VerticalOnly
        });
        return shell;
    }

    private IEnumerable<SettingsPage> SettingsPages()
    {
        yield return SettingsPage.General;
        yield return SettingsPage.Todo;
        yield return SettingsPage.Note;
        yield return SettingsPage.Visual;
        yield return SettingsPage.Shortcuts;
        yield return SettingsPage.Plugins;
        if (State.AdvancedSettingsMode)
        {
            yield return SettingsPage.Labs;
        }
    }

    private UIElement BuildSettingsSidebarNavigationItem(SettingsPage page)
    {
        var active = page == _settingsPage;
        var border = new Border
        {
            Height = 34,
            CornerRadius = new CornerRadius(6),
            Background = active
                ? Theme.Tint((byte)(Theme.IsDark ? 34 : 18))
                : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 1, 0, 1)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var indicator = new Border
        {
            Width = 3,
            Height = 18,
            CornerRadius = new CornerRadius(1.5),
            Background = active ? Theme.ActiveBrush : Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(indicator, 0);
        grid.Children.Add(indicator);

        var label = new TextBlock
        {
            Text = SettingsPageLabel(page),
            Foreground = active ? TrayTextBrush : TrayWeakTextBrush,
            FontSize = AppTypography.Scale(12.5),
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        border.Child = grid;

        border.MouseEnter += (_, _) =>
        {
            if (page != _settingsPage)
            {
                border.Background = TrayHoverBrush;
                label.Foreground = TrayTextBrush;
            }
        };
        border.MouseLeave += (_, _) =>
        {
            if (page != _settingsPage)
            {
                border.Background = Brushes.Transparent;
                label.Foreground = TrayWeakTextBrush;
            }
        };
        border.MouseLeftButtonDown += (_, e) =>
        {
            if (page != _settingsPage)
            {
                ShowSettingsWindow(page);
            }
            e.Handled = true;
        };
        return border;
    }

    private UIElement BuildSettingsPageHost()
    {
        var root = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(16, 0, 10, 14)
        };

        var content = new Border
        {
            Width = SettingsContentWidth() + 16,
            Padding = new Thickness(8, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = BuildSettingsPage()
        };

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CanContentScroll = false,
            PanningMode = PanningMode.Both,
            Content = content
        };

        void FitContentWidthToViewport()
        {
            var viewportWidth = scrollViewer.ViewportWidth;
            if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
            {
                return;
            }

            var targetWidth = Math.Max(SettingsContentWidth() + 16, viewportWidth);
            if (!double.IsFinite(content.Width) || Math.Abs(content.Width - targetWidth) > 0.5)
            {
                content.Width = targetWidth;
            }
        }

        var widthFitQueued = false;
        void QueueFitContentWidth()
        {
            if (widthFitQueued)
            {
                return;
            }

            widthFitQueued = true;
            scrollViewer.Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    widthFitQueued = false;
                    FitContentWidthToViewport();
                }),
                DispatcherPriority.Loaded);
        }

        scrollViewer.Loaded += (_, _) => QueueFitContentWidth();
        scrollViewer.ScrollChanged += (_, e) =>
        {
            if (Math.Abs(e.ViewportWidthChange) > 0.01)
            {
                QueueFitContentWidth();
            }
        };

        _settingsPageScrollViewer = scrollViewer;
        _settingsPageScrollViewerPage = _settingsPage;
        if (_settingsPageScrollOffsets.TryGetValue(_settingsPage, out var offset) && offset > 0)
        {
            scrollViewer.Loaded += (_, _) => scrollViewer.Dispatcher.BeginInvoke(
                (Action)(() => scrollViewer.ScrollToVerticalOffset(
                    Math.Min(offset, scrollViewer.ScrollableHeight))),
                DispatcherPriority.ContextIdle);
        }
        root.Children.Add(scrollViewer);
        return root;
    }

    private UIElement BuildSettingsPage() => _settingsPage switch
    {
        SettingsPage.General => BuildSettingsSidebarGeneralPage(),
        SettingsPage.Todo => BuildSettingsSidebarTodoPage(),
        SettingsPage.Note => BuildSettingsSidebarNotePage(),
        SettingsPage.Visual => BuildVisualSettingsPage(),
        SettingsPage.Shortcuts => BuildShortcutSettingsPage(),
        SettingsPage.Plugins => BuildPluginsSettingsPage(),
        SettingsPage.Labs => BuildLabsSettingsPage(),
        _ => BuildSettingsSidebarGeneralPage()
    };

    private string SettingsPageLabel(SettingsPage page) => page switch
    {
        SettingsPage.General => SettingsSidebarLocalized(
            "常规", "General", "一般", "일반"),
        SettingsPage.Todo => Strings.Get("MenuTodo"),
        SettingsPage.Note => Strings.Get("PaperKindNote"),
        SettingsPage.Visual => Strings.Get("SettingsVisual"),
        SettingsPage.Shortcuts => Strings.Get("SettingsShortcuts"),
        SettingsPage.Plugins => Strings.Get("SettingsPlugins"),
        SettingsPage.Labs => Strings.Get("SettingsLabs"),
        _ => Strings.Get("TraySettings")
    };

    private static string SettingsSidebarLocalized(
        string chinese,
        string english,
        string japanese,
        string korean)
    {
        return UiLanguages.EffectiveUiCulture.TwoLetterISOLanguageName switch
        {
            "en" => english,
            "ja" => japanese,
            "ko" => korean,
            _ => chinese
        };
    }

    private void ApplySettingsSidebarFrame(Window window)
    {
        var workArea = WindowWorkAreaHelper.WorkAreaFor(window);
        var (targetWidth, targetHeight) = SettingsSidebarSizeForWorkArea(workArea);

        var wasVisible = window.IsVisible;
        var oldLeft = window.Left;
        var oldTop = window.Top;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Width = targetWidth;
        window.Height = targetHeight;

        if (!wasVisible || !double.IsFinite(oldLeft) || !double.IsFinite(oldTop))
        {
            window.Left = workArea.Left + (workArea.Width - targetWidth) / 2;
            window.Top = workArea.Top + (workArea.Height - targetHeight) / 2;
            return;
        }

        window.Left = ClampWindowCoordinate(
            oldLeft,
            workArea.Left + 16,
            workArea.Right - targetWidth - 16);
        window.Top = ClampWindowCoordinate(
            oldTop,
            workArea.Top + 16,
            workArea.Bottom - targetHeight - 16);
    }
}
