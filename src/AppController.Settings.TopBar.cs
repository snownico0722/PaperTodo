using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private const string TopBarShortcutDragFormat = "PaperTodo.TopBarShortcut";

    private UIElement BuildTopBarShortcutSettings()
    {
        var root = new StackPanel
        {
            AllowDrop = true,
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 4, 0, 4)
        };
        root.Children.Add(SettingsSectionLabelWithHint(
            Strings.Get("TopBarSettingsTitle"), "TipTopBarShortcutSettings"));

        var actions = new StackPanel { Background = Brushes.Transparent };
        actions.Children.Add(BuildTopBarShortcutHeading("TopBarShownHeading"));
        var shownGroup = BuildTopBarShortcutDropGroup(
            PaperTopBarShortcuts.Pinned(State), pinned: true);
        actions.Children.Add(shownGroup);

        // This is an in-editor divider, not the roomy separator between settings.
        var divider = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 5, 0, 6),
            Background = TrayBorderBrush,
            Opacity = 0.4,
            SnapsToDevicePixels = true
        };
        actions.Children.Add(divider);
        actions.Children.Add(BuildTopBarShortcutHeading("TopBarMenuHeading"));
        actions.Children.Add(BuildTopBarShortcutDropGroup(
            PaperTopBarShortcuts.Menu(State), pinned: false));
        root.Children.Add(new Border
        {
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 7, 8, 8),
            Margin = new Thickness(0, 2, 0, 3),
            Child = actions
        });

        // Items own their exact before/after insertion targets. The enclosing editor
        // accepts drops on headings, the separator and gaps between groups too.
        // These regions must not be dead spots when crossing the divider.
        root.DragOver += (_, e) =>
        {
            if (GetTopBarDraggedAction(e) == null) return;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        };
        root.Drop += (_, e) =>
        {
            var action = GetTopBarDraggedAction(e);
            if (action == null) return;
            var y = e.GetPosition(root).Y;
            var shownStart = shownGroup.TranslatePoint(new Point(0, 0), root).Y;
            var dividerMiddle = divider.TranslatePoint(
                new Point(0, divider.ActualHeight / 2), root).Y;
            if (y < shownStart)
            {
                MoveTopBarShortcut(action, pinned: true, destinationIndex: 0);
            }
            else if (y < dividerMiddle)
            {
                MoveTopBarShortcut(action, pinned: true,
                    destinationIndex: PaperTopBarShortcuts.Pinned(State)
                        .Count(item => item != action));
            }
            else
            {
                // The separator's lower half and menu heading insert at the
                // start of the lower section; its rows handle their own slots.
                MoveTopBarShortcut(action, pinned: false, destinationIndex: 0);
            }
            e.Handled = true;
        };
        return root;
    }

    private static double TopBarHideColumnWidth => AppTypography.Scale(44);

    private static UIElement BuildTopBarShortcutHeading(string titleKey)
    {
        var heading = new DockPanel { Margin = new Thickness(7, 0, 7, 0) };
        var hide = SettingsFieldLabel(Strings.Get("TopBarHideShortcut"));
        hide.Width = TopBarHideColumnWidth;
        hide.TextAlignment = TextAlignment.Center;
        DockPanel.SetDock(hide, Dock.Right);
        heading.Children.Add(hide);
        heading.Children.Add(SettingsFieldLabel(Strings.Get(titleKey)));
        return heading;
    }

    private UIElement BuildTopBarShortcutDropGroup(
        System.Collections.Generic.IReadOnlyList<string> actions, bool pinned)
    {
        var panel = new StackPanel
        {
            AllowDrop = true,
            Background = Brushes.Transparent,
            MinHeight = AppTypography.Scale(24),
            Margin = new Thickness(0, 4, 0, 0)
        };
        panel.DragOver += (_, e) =>
        {
            var action = GetTopBarDraggedAction(e);
            if (action == null) return;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        };
        panel.Drop += (_, e) =>
        {
            var action = GetTopBarDraggedAction(e);
            if (action == null) return;
            // A drop in the group's remaining whitespace inserts at the end.
            var count = actions.Count(item => item != action);
            MoveTopBarShortcut(action, pinned, count);
            e.Handled = true;
        };

        if (actions.Count == 0)
        {
            panel.Children.Add(new Border
            {
                MinHeight = AppTypography.Scale(32),
                Padding = new Thickness(7, 6, 7, 6),
                CornerRadius = new CornerRadius(5),
                BorderBrush = TrayBorderBrush,
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent,
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = Strings.Get("TopBarEmptyDropArea"),
                    Foreground = TrayWeakTextBrush,
                    FontSize = AppTypography.Scale(10.5),
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            });
        }

        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            var rowIndex = index;
            var frame = new Grid
            {
                AllowDrop = true,
                Margin = new Thickness(0, 2, 0, 2),
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeAll
            };
            var border = new Border
            {
                BorderBrush = TrayBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Background = Brushes.Transparent,
                Padding = new Thickness(7, 6, 7, 6)
            };
            var row = new Grid
            {
                MinHeight = AppTypography.FitChrome(20),
                VerticalAlignment = VerticalAlignment.Center,
                // The feature is inactive, but its placement remains editable.
                Opacity = (action == PaperTopBarShortcuts.Workspace && !WorkspaceOptions.Enabled) ||
                          (action == PaperTopBarShortcuts.Association &&
                           !State.EnableTodoPaperLinks && !State.ExperimentalWindowTethering)
                    ? 0.5 : 1
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var grip = new Grid { Width = AppTypography.Scale(22), IsHitTestVisible = false };
            grip.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M1 1h0.1 M5 1h0.1 M1 5h0.1 M5 5h0.1 M1 9h0.1 M5 9h0.1"),
                Stroke = TrayWeakTextBrush,
                StrokeThickness = AppTypography.Scale(1.8),
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Stretch = Stretch.Uniform,
                Width = AppTypography.Scale(6), Height = AppTypography.Scale(10),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(grip);
            var icon = CreateTopBarShortcutIcon(action);
            Grid.SetColumn(icon, 1);
            row.Children.Add(icon);
            var name = new TextBlock
            {
                Text = TopBarShortcutName(action),
                Foreground = TrayTextBrush,
                FontSize = AppTypography.Scale(11.5),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 2);
            row.Children.Add(name);
            var content = new DockPanel { LastChildFill = true };
            var controls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Arrow
            };
            if (action == PaperTopBarShortcuts.RunScript)
            {
                var hint = CreateSettingsHintGlyph("TipTopBarRunScript", new Thickness(3, 0, 0, 0));
                Grid.SetColumn(hint, 3);
                row.Children.Add(hint);
                var edit = SettingsTextButton(SettingsSidebarLocalized(
                    "编辑", "Edit", "編集", "편집"));
                edit.ToolTip = Strings.Get("TopBarEditScript");
                System.Windows.Automation.AutomationProperties.SetName(edit,
                    Strings.Get("TopBarEditScript"));
                edit.MinWidth = 0;
                // Smaller than the row icon: the script action must not increase row height.
                edit.Height = AppTypography.FitChrome(18);
                edit.Padding = new Thickness(5, 0, 5, 0);
                edit.BorderBrush = TrayBorderBrush;
                edit.BorderThickness = new Thickness(1);
                edit.FontSize = AppTypography.Scale(10.5);
                edit.Margin = new Thickness(3, 0, 2, 0);
                edit.Click += (_, _) => EditTopBarScript();
                controls.Children.Add(edit);
            }
            var hide = SettingsToggle(string.Empty,
                PaperTopBarShortcuts.IsHidden(State, action),
                () => SetTopBarShortcutHidden(action, !PaperTopBarShortcuts.IsHidden(State, action)));
            hide.Style = BuildSettingsCheckBoxStyle(markOnly: true);
            hide.Width = TopBarHideColumnWidth;
            hide.Margin = new Thickness(0);
            System.Windows.Automation.AutomationProperties.SetName(hide,
                Strings.Get("TopBarHideShortcut") + ": " + TopBarShortcutName(action));
            controls.Children.Add(hide);
            DockPanel.SetDock(controls, Dock.Right);
            content.Children.Add(controls);
            content.Children.Add(row);
            border.Child = content;
            frame.Children.Add(border);

            // This overlay marks the exact insertion edge without affecting row height.
            var insertion = new Border
            {
                Height = AppTypography.Scale(2),
                Background = Theme.ActiveBrush,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            frame.Children.Add(insertion);

            Point? start = null;
            frame.PreviewMouseLeftButtonDown += (_, e) =>
            {
                start = IsTopBarShortcutControl(e.OriginalSource as DependencyObject)
                    ? null : e.GetPosition(frame);
            };
            frame.PreviewMouseLeftButtonUp += (_, _) => start = null;
            frame.PreviewMouseRightButtonDown += (_, _) => start = null;
            frame.LostMouseCapture += (_, _) => start = null;
            frame.PreviewMouseMove += (_, e) =>
            {
                // A mouse-up may happen outside the row without routing back to it.
                // Never reuse an old press on a later gesture.
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    start = null;
                    return;
                }
                if (start is not { } origin) return;
                var current = e.GetPosition(frame);
                if (Math.Abs(current.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(current.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance)
                    return;
                start = null;
                try
                {
                    border.Opacity = 0.55;
                    DragDrop.DoDragDrop(frame,
                        new DataObject(TopBarShortcutDragFormat, action),
                        DragDropEffects.Move);
                }
                finally { border.Opacity = 1.0; }
                e.Handled = true;
            };
            frame.MouseEnter += (_, _) => border.Background = TrayHoverBrush;
            frame.MouseLeave += (_, _) =>
            {
                start = null;
                border.Background = Brushes.Transparent;
            };

            frame.DragOver += (_, e) =>
            {
                var dragged = GetTopBarDraggedAction(e);
                if (dragged == null) return;
                e.Effects = DragDropEffects.Move;
                insertion.VerticalAlignment = e.GetPosition(frame).Y < frame.ActualHeight / 2
                    ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                insertion.Visibility = Visibility.Visible;
                e.Handled = true;
            };
            frame.DragLeave += (_, _) => insertion.Visibility = Visibility.Collapsed;
            frame.Drop += (_, e) =>
            {
                insertion.Visibility = Visibility.Collapsed;
                var dragged = GetTopBarDraggedAction(e);
                if (dragged == null) return;
                var targetBefore = e.GetPosition(frame).Y < frame.ActualHeight / 2;
                var sectionIndex = rowIndex + (targetBefore ? 0 : 1);
                // The destination section is measured without the item being moved.
                var indexWithoutDragged = actions.Take(sectionIndex)
                    .Count(item => item != dragged);
                MoveTopBarShortcut(dragged, pinned, indexWithoutDragged);
                e.Handled = true;
            };
            panel.Children.Add(frame);
        }
        return panel;
    }

    private static bool IsTopBarShortcutControl(DependencyObject? source)
    {
        for (var current = source; current != null; current = current is Visual
                 ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is ButtonBase || current is Border hint && hint.Cursor == Cursors.Help) return true;
        return false;
    }

    private static string? GetTopBarDraggedAction(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(TopBarShortcutDragFormat)) return null;
        var action = e.Data.GetData(TopBarShortcutDragFormat) as string;
        return PaperTopBarShortcuts.All.Contains(action, StringComparer.Ordinal) ? action : null;
    }

    private static string TopBarShortcutName(string action) => action switch
    {
        PaperTopBarShortcuts.NewTodo => Strings.Get("ShortcutNewTodo"),
        PaperTopBarShortcuts.NewNote => Strings.Get("ShortcutNewNote"),
        PaperTopBarShortcuts.Workspace => Strings.Get("WorkspaceSettingsTitle"),
        PaperTopBarShortcuts.RunScript => Strings.Get("TopBarRunScript"),
        PaperTopBarShortcuts.Association => Strings.Get("TopBarAssociation"),
        PaperTopBarShortcuts.ExternalEditor => Strings.Get("TopBarExternalEditor"),
        _ => action
    };

    private FrameworkElement CreateTopBarShortcutIcon(string action)
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentProperty,
            new TemplateBindingExtension(ContentControl.ContentProperty));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var slot = new ContentControl
        {
            Width = AppTypography.Scale(26), Height = AppTypography.FitChrome(20),
            Foreground = TrayWeakTextBrush, FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(10.5), Focusable = false, IsHitTestVisible = false,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Template = new ControlTemplate(typeof(ContentControl)) { VisualTree = presenter }
        };
        slot.Content = action switch
        {
            PaperTopBarShortcuts.NewTodo => PaperWindow.CreateTopBarNewTodoIcon(slot),
            PaperTopBarShortcuts.NewNote => PaperWindow.CreateTopBarNewNoteIcon(slot),
            PaperTopBarShortcuts.RunScript => PaperWindow.CreateTopBarRunScriptIcon(slot),
            PaperTopBarShortcuts.Association => PaperWindow.CreateTopBarAssociationIcon(slot, active: false),
            PaperTopBarShortcuts.Workspace => PaperWindow.CreateWorkspaceCycleIcon(slot, WorkspaceNumber, out _),
            PaperTopBarShortcuts.ExternalEditor => new TextBlock
            {
                Text = PaperWindow.ExternalOpenButtonLabel(State.ExternalMarkdownExtension),
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            _ => null
        };
        return slot;
    }
}
