using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
        actions.Children.Add(SettingsFieldLabel(Strings.Get("TopBarShownHeading")));
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
        actions.Children.Add(SettingsFieldLabel(Strings.Get("TopBarMenuHeading")));
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
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                IsHitTestVisible = false
            };
            row.Children.Add(new TextBlock
            {
                Text = "⋮⋮",
                Foreground = TrayWeakTextBrush,
                FontSize = AppTypography.Scale(12),
                Width = AppTypography.Scale(22),
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(CreateTopBarShortcutIcon(action));
            row.Children.Add(new TextBlock
            {
                Text = TopBarShortcutName(action),
                Foreground = TrayTextBrush,
                FontSize = AppTypography.Scale(11.5),
                VerticalAlignment = VerticalAlignment.Center
            });
            border.Child = row;
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
                start = e.GetPosition(frame);
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
        PaperTopBarShortcuts.Association => Strings.Get("TopBarAssociation"),
        PaperTopBarShortcuts.ExternalEditor => Strings.Get("TopBarExternalEditor"),
        _ => action
    };

    // Keep the settings icon column aligned. The two action icons are WPF vector
    // strokes (24-unit Lucide-style outlines) rather than font-dependent symbols.
    private static FrameworkElement CreateTopBarShortcutIcon(string action)
    {
        string? pathData = action switch
        {
            PaperTopBarShortcuts.Association =>
                "M 10 13 A 5 5 0 0 0 17.54 13.54 L 20.54 10.54 " +
                "A 5 5 0 0 0 13.47 3.47 L 11.75 5.18 " +
                "M 14 11 A 5 5 0 0 0 6.46 10.46 L 3.46 13.46 " +
                "A 5 5 0 0 0 10.53 20.53 L 12.24 18.82",
            PaperTopBarShortcuts.ExternalEditor =>
                "M 15 3 H 21 V 9 M 10 14 L 21 3 " +
                "M 21 13 V 19 A 2 2 0 0 1 19 21 H 5 " +
                "A 2 2 0 0 1 3 19 V 5 A 2 2 0 0 1 5 3 H 11",
            _ => null
        };
        // Both font glyphs and vector paths live in the same centered slot.
        // Their advance widths must not shift the icon column between rows.
        var slot = new Grid
        {
            Width = AppTypography.Scale(24),
            Height = AppTypography.Scale(20),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        if (pathData == null)
        {
            slot.Children.Add(new TextBlock
            {
                Text = action == PaperTopBarShortcuts.NewTodo ? "✓" : "✎",
                FontFamily = AppTypography.SymbolFontFamily,
                FontWeight = FontWeights.SemiBold,
                Foreground = TrayWeakTextBrush,
                FontSize = AppTypography.Scale(12),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            return slot;
        }

        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(pathData),
            Stroke = TrayWeakTextBrush,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        });
        slot.Children.Add(new Viewbox
        {
            Width = AppTypography.Scale(16),
            Height = AppTypography.Scale(16),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = canvas
        });
        return slot;
    }
}
