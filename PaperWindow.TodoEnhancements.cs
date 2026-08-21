using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool MoveTodoItemAfterDoneChange(PaperItem item, bool done)
    {
        if (!_controller.State.AutoMoveCompletedTodosToBottom ||
            _controller.State.AutoClearCompletedTodos)
        {
            return false;
        }

        var before = OrderedItems().ToList();
        var currentIndex = before.FindIndex(candidate => candidate.Id == item.Id);
        if (currentIndex < 0)
        {
            return false;
        }

        var remaining = before.Where(candidate => candidate.Id != item.Id).ToList();
        var insertIndex = done
            ? remaining.Count
            : remaining.FindIndex(candidate => candidate.Done);
        if (insertIndex < 0)
        {
            insertIndex = remaining.Count;
        }
        remaining.Insert(insertIndex, item);
        if (before.Select(candidate => candidate.Id)
            .SequenceEqual(remaining.Select(candidate => candidate.Id)))
        {
            return false;
        }

        _paper.Items = remaining;
        NormalizeOrders();
        return true;
    }

    private void ConfigureTodoPathDrop(Border row, PaperItem item)
    {
        void UpdateEffect(DragEventArgs e)
        {
            var paths = GetTodoFileDropPaths(e.Data);
            if (paths.Length == 1)
            {
                e.Effects = DragDropEffects.Link;
                row.Background = NoteLinkTargetBgBrush;
                row.BorderBrush = NoteLinkTargetBorderBrush;
                row.BorderThickness = new Thickness(1);
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = paths.Length > 0;
        }

        row.AddHandler(DragDrop.DragEnterEvent, new DragEventHandler((_, e) => UpdateEffect(e)), true);
        row.AddHandler(DragDrop.DragOverEvent, new DragEventHandler((_, e) => UpdateEffect(e)), true);
        row.AddHandler(DragDrop.DragLeaveEvent, new DragEventHandler((_, e) =>
        {
            if (GetTodoFileDropPaths(e.Data).Length == 0) return;
            row.Background = Brushes.Transparent;
            row.BorderBrush = Brushes.Transparent;
            row.BorderThickness = new Thickness(0, 2, 0, 2);
            e.Handled = true;
        }), true);
        row.AddHandler(DragDrop.DropEvent, new DragEventHandler((_, e) =>
        {
            var paths = GetTodoFileDropPaths(e.Data);
            if (paths.Length == 0) return;
            try
            {
                if (paths.Length != 1)
                {
                    MessageBox.Show(this, Strings.Get("LinkedPathSingleDropMessage"),
                        Strings.Get("LinkedPathDropFailureTitle"), MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
                var path = Path.GetFullPath(paths[0]);
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    MessageBox.Show(this, Strings.Format("LinkedPathMissingMessage", path),
                        Strings.Get("LinkedPathOpenFailureTitle"), MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
                LinkPathToTodo(item, path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, Strings.Format("LinkedPathDropFailureMessage", ex.Message),
                    Strings.Get("LinkedPathDropFailureTitle"), MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                row.Background = Brushes.Transparent;
                row.BorderBrush = Brushes.Transparent;
                row.BorderThickness = new Thickness(0, 2, 0, 2);
                e.Handled = true;
            }
        }), true);
    }

    private static string[] GetTodoFileDropPaths(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) ||
            data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return [];
        }
        return paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
    }

    private void LinkPathToTodo(PaperItem item, string path)
    {
        if (string.Equals(item.LinkedPath, path, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(item.LinkedPaperId))
        {
            return;
        }
        var focusedId = CurrentFocusedTodoItemId() ?? item.Id;
        var previousItems = CloneItems(_paper.Items);
        PushUndoSnapshot();
        item.LinkedPath = path;
        item.LinkedPaperId = null;
        _controller.MarkDirty();
        RebuildTodoRows(focusedId);
        RefreshCapsuleEligibilityForLinkedNoteChanges(previousItems);
    }

    private void UnlinkPathFromTodoItem(PaperItem item)
    {
        if (string.IsNullOrWhiteSpace(item.LinkedPath)) return;
        var focusedId = CurrentFocusedTodoItemId() ?? item.Id;
        PushUndoSnapshot();
        item.LinkedPath = null;
        _controller.MarkDirty();
        RebuildTodoRows(focusedId);
    }

    private Border BuildTodoPathLinkButton(PaperItem item, TodoTextBox text, TodoVisualMetrics metrics)
    {
        var path = item.LinkedPath ?? "";
        var showName = _controller.State.ShowLinkedNoteName;
        var longName = showName && _controller.State.AllowLongLinkedNoteTitles;
        var displayName = PathDisplayName(path);
        if (showName && !longName && _controller.State.ShowLinkedPathExtensionOnly && File.Exists(path))
        {
            var extension = Path.GetExtension(path);
            if (!string.IsNullOrWhiteSpace(extension)) displayName = extension;
        }
        if (showName && !longName)
        {
            displayName = CompactLinkedNoteTitle(displayName, 7, 6);
        }

        var glyph = new TextBlock
        {
            Text = "",
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = longName ? AppTypography.Scale(150) : AppTypography.Scale(70)
        };
        var button = new Border
        {
            MinWidth = Math.Max(23, metrics.CheckColumnWidth),
            MaxWidth = longName ? AppTypography.Scale(160) : AppTypography.Scale(80),
            MinHeight = Math.Max(22, metrics.RowMinHeight - 2),
            Margin = new Thickness(1, 0, 0, 0),
            Padding = showName ? new Thickness(5, 1, 5, 1) : new Thickness(0),
            CornerRadius = new CornerRadius(RadiusControl),
            Cursor = Cursors.Hand,
            Child = glyph
        };

        void Refresh(bool hovered)
        {
            var valid = File.Exists(path) || Directory.Exists(path);
            var isDirectory = valid && Directory.Exists(path);
            glyph.Text = valid
                ? showName ? displayName : isDirectory ? "\uE8B7" : "\uE7C3"
                : "!";
            glyph.FontFamily = showName || !valid
                ? AppTypography.UiFontFamily
                : new FontFamily("Segoe MDL2 Assets");
            glyph.FontSize = showName ? metrics.LinkedNoteNameFontSize : metrics.LinkedNoteIconFontSize;
            glyph.Foreground = valid ? (hovered ? TextBrush : WeakTextBrush) : TrashTextBrush;
            glyph.Opacity = valid ? (hovered ? 1.0 : 0.72) : 1.0;
            button.Background = valid
                ? (hovered ? LinkedNoteLightBgBrush : LinkedNoteNormalBgBrush)
                : (hovered ? TrashHoverBgBrush : TrashBgBrush);
            button.ToolTip = valid
                ? Strings.Format("ToolTipOpenLinkedPath", path)
                : Strings.Format("ToolTipLinkedPathMissing", path);
        }

        Refresh(false);
        button.MouseEnter += (_, _) => Refresh(true);
        button.MouseLeave += (_, _) => { Refresh(false); button.Opacity = 1.0; };
        button.MouseLeftButtonDown += (_, e) => { button.Opacity = 0.72; e.Handled = true; };
        button.MouseLeftButtonUp += (_, e) =>
        {
            button.Opacity = 1.0;
            OpenLinkedPath(path);
            e.Handled = true;
        };
        return button;
    }

    private static string PathDisplayName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private void OpenLinkedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                MessageBox.Show(this, Strings.Format("LinkedPathMissingMessage", path),
                    Strings.Get("LinkedPathOpenFailureTitle"), MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Strings.Format("LinkedPathOpenFailureMessage", ex.Message),
                Strings.Get("LinkedPathOpenFailureTitle"), MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OpenLinkedPathLocation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
                return;
            }

            var current = Path.GetDirectoryName(path);
            while (!string.IsNullOrWhiteSpace(current) && !Directory.Exists(current))
            {
                current = Path.GetDirectoryName(current);
            }
            if (string.IsNullOrWhiteSpace(current))
            {
                MessageBox.Show(this, Strings.Format("LinkedPathMissingMessage", path),
                    Strings.Get("LinkedPathOpenFailureTitle"), MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = current, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Strings.Format("LinkedPathOpenFailureMessage", ex.Message),
                Strings.Get("LinkedPathOpenFailureTitle"), MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
