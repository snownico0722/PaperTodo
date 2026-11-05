using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PaperTodo;

internal sealed partial class WorkbenchWindow
{
    private readonly HashSet<string> _selectedCards = new(StringComparer.Ordinal);
    private Point? _marqueeStart;
    private HashSet<string> _marqueeInitial = new(StringComparer.Ordinal);
    private Rectangle? _marquee;
    private string? _focusedGroupId;
    private Camera? _cameraBeforeGroupFocus;
    private Button? _leaveGroupButton;
    private Button? _groupButton;
    private Button? _ungroupButton;

    private WorkbenchState SceneState(Scene scene) => scene.Number == _controller.WorkspaceNumber
        ? _controller.WorkbenchLayout : scene.Snapshot?.Layout ?? new WorkbenchState();

    private void SelectOnly(string id)
    {
        _selectedCards.Clear();
        _selectedCards.Add(id);
        ApplySelectionStyle();
    }

    private void ApplySelectionStyle()
    {
        if (ActiveScene is not { } scene) return;
        foreach (var card in scene.Cards.Values)
        {
            card.Frame.BorderBrush = _selectedCards.Contains(card.Snapshot.Id) ? Accent : Outline;
            card.Frame.BorderThickness = new Thickness(_selectedCards.Contains(card.Snapshot.Id) ? 2 : 1);
        }
        if (_groupButton != null)
            _groupButton.Visibility = _selectedCards.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_ungroupButton != null)
        {
            var matches = _controller.WorkbenchLayout.Groups.Values.Count(g => g.Members.Any(_selectedCards.Contains));
            _ungroupButton.Visibility = matches == 1 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void WireCardSelection(Card card)
    {
        card.Move.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.Handled || e.ClickCount != 1) return;
            _viewport.Focus();
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
            {
                if (!_selectedCards.Add(card.Snapshot.Id)) _selectedCards.Remove(card.Snapshot.Id);
                ApplySelectionStyle();
                e.Handled = true;
            }
            else if (!_selectedCards.Contains(card.Snapshot.Id)) SelectOnly(card.Snapshot.Id);
        };
    }

    private void BeginMarquee(MouseButtonEventArgs e)
    {
        if (IsSuspended || ActiveScene == null) return;
        if (_connectionSource != null) { CancelWorkspaceGesture(); e.Handled = true; return; }
        StopCameraAnimation();
        _viewport.Focus();
        _marqueeStart = e.GetPosition(_viewport);
        _marqueeInitial = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0
            ? new(_selectedCards, StringComparer.Ordinal) : new(StringComparer.Ordinal);
        _selectedCards.Clear(); _selectedCards.UnionWith(_marqueeInitial);
        _marquee = new Rectangle
        {
            Stroke = Accent, StrokeThickness = 1, Fill = Theme.Tint(35),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed
        };
        Panel.SetZIndex(_marquee, 20);
        _viewport.Children.Add(_marquee);
        _viewport.CaptureMouse();
        ApplySelectionStyle();
        e.Handled = true;
    }

    private bool MoveMarquee(MouseEventArgs e)
    {
        if (_marqueeStart is not { } start || _marquee == null || ActiveScene is not { } scene) return false;
        var current = e.GetPosition(_viewport);
        var bounds = new Rect(start, current);
        if ((current - start).LengthSquared < 16) return true;
        _marquee.Visibility = Visibility.Visible;
        _marquee.Margin = new Thickness(bounds.Left, bounds.Top, 0, 0);
        _marquee.Width = bounds.Width; _marquee.Height = bounds.Height;
        SelectCardsInRectangle(scene, bounds, _marqueeInitial);
        e.Handled = true;
        return true;
    }

    private void SelectCardsInRectangle(Scene scene, Rect screenBounds, IEnumerable<string>? initial = null)
    {
        _selectedCards.Clear();
        if (initial != null) _selectedCards.UnionWith(initial);
        foreach (var card in scene.Cards.Values)
        {
            if (card.Frame.Visibility != Visibility.Visible) continue;
            var b = card.Bounds.Offset(scene.Origin);
            var rect = new Rect(b.X * _scale.ScaleX + _pan.X, b.Y * _scale.ScaleY + _pan.Y,
                b.Width * _scale.ScaleX, b.Height * _scale.ScaleY);
            if (screenBounds.IntersectsWith(rect)) _selectedCards.Add(card.Snapshot.Id);
        }
        ApplySelectionStyle();
    }

    private bool EndMarquee(MouseButtonEventArgs e)
    {
        if (_marqueeStart is not { } start || e.ChangedButton != MouseButton.Left) return false;
        var clicked = (e.GetPosition(_viewport) - start).LengthSquared < 16;
        CancelMarquee();
        _viewport.ReleaseMouseCapture();
        if (clicked) ClearPaperFocus(restoreCamera: true);
        e.Handled = true;
        return true;
    }

    private void CancelMarquee()
    {
        _marqueeStart = null;
        if (_marquee != null) _viewport.Children.Remove(_marquee);
        _marquee = null;
    }

    private string[] MovingSelection(string fallback)
    {
        var ids = new HashSet<string>(_selectedCards, StringComparer.Ordinal) { fallback };
        foreach (var group in _controller.WorkbenchLayout.Groups.Values)
            if (group.Members.Any(ids.Contains)) ids.UnionWith(group.Members);
        return ids.ToArray();
    }

    private void UngroupSelection()
    {
        var groups = _controller.WorkbenchLayout.Groups.Values.Where(g => g.Members.Any(_selectedCards.Contains)).ToArray();
        if (groups.Length == 1) Ungroup(groups[0].Id);
    }

    private void CreateGroupFromSelection()
    {
        if (IsSuspended || ActiveScene is not { } scene) return;
        var members = _selectedCards.Where(scene.Cards.ContainsKey).ToList();
        if (members.Count < 2) return;
        RememberLayout();
        var state = _controller.WorkbenchLayout.Copy();
        foreach (var old in state.Groups.Values) old.Members.RemoveAll(members.Contains);
        var group = new WorkbenchGroupState
        {
            Id = "group:" + Guid.NewGuid().ToString("N"),
            Title = Strings.Get("WorkbenchGroupDefault"), Members = members
        };
        state.Groups[group.Id] = group;
        StoreLayout(state);
        ApplySceneVisibility(); ApplySelectionStyle(); DrawDecorations(scene);
    }

    private void ToggleGroupCollapsed(string id)
    {
        if (IsSuspended || !_controller.WorkbenchLayout.Groups.ContainsKey(id)) return;
        RememberLayout();
        var state = _controller.WorkbenchLayout.Copy();
        state.Groups[id].Collapsed = !state.Groups[id].Collapsed;
        StoreLayout(state);
        ApplySceneVisibility();
        if (ActiveScene is { } scene) DrawDecorations(scene);
    }

    private void Ungroup(string id)
    {
        if (!_controller.WorkbenchLayout.Groups.ContainsKey(id)) return;
        RememberLayout(); var state = _controller.WorkbenchLayout.Copy();
        state.Groups.Remove(id); StoreLayout(state);
        if (_focusedGroupId == id) LeaveGroupFocus();
        ApplySceneVisibility(); ApplySelectionStyle();
        if (ActiveScene is { } scene) DrawDecorations(scene);
    }

    private void FocusGroup(string id)
    {
        if (ActiveScene is not { } scene || !_controller.WorkbenchLayout.Groups.TryGetValue(id, out var group)) return;
        _cameraBeforeGroupFocus ??= new Camera(_scale.ScaleX, _pan.X, _pan.Y);
        _focusedGroupId = id;
        _cameraBeforePaperFocus = null; _focusedPaperId = null;
        ApplySceneVisibility(); DrawDecorations(scene);
        var cards = group.Members.Where(scene.Cards.ContainsKey).Select(k => scene.Cards[k].Bounds).ToArray();
        if (cards.Length > 0) Fit(WorkbenchRect.Union(cards).Inflate(64).Offset(scene.Origin), 1, animate: true);
    }

    private void LeaveGroupFocus()
    {
        var previous = _cameraBeforeGroupFocus;
        _focusedGroupId = null; _cameraBeforeGroupFocus = null;
        ApplySceneVisibility();
        foreach (var scene in _scenes.Values) DrawDecorations(scene);
        if (previous is { } camera) SetCamera(camera, animate: true);
    }

    private HashSet<string> HiddenBranches(Scene scene)
    {
        var state = SceneState(scene);
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        if (scene.Number == _controller.WorkspaceNumber)
        {
            foreach (var card in scene.Cards.Values)
                if (_controller.FindWorkbenchMaterial(card.Snapshot.Id)?.Collapsed == true) queue.Enqueue(card.Snapshot.Id);
        }
        else
        {
            foreach (var card in scene.Cards.Values)
                if (card.Snapshot.MaterialCollapsed) queue.Enqueue(card.Snapshot.Id);
        }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (queue.TryDequeue(out var parent))
        {
            if (!visited.Add(parent)) continue;
            foreach (var edge in state.Connections.Where(c => c.IsBranch && c.From == parent))
                if (hidden.Add(edge.To)) queue.Enqueue(edge.To);
        }
        return hidden;
    }

    private void ApplySceneVisibility()
    {
        if (_focusedGroupId != null && !_controller.WorkbenchLayout.Groups.ContainsKey(_focusedGroupId))
        {
            _focusedGroupId = null;
            if (_cameraBeforeGroupFocus is { } previous) SetCamera(previous, animate: false);
            _cameraBeforeGroupFocus = null;
        }
        foreach (var scene in _scenes.Values)
        {
            scene.Canvas.Visibility = _focusedGroupId != null && scene.Number != _controller.WorkspaceNumber
                ? Visibility.Collapsed : Visibility.Visible;
            var state = SceneState(scene);
            var hidden = HiddenBranches(scene);
            foreach (var group in state.Groups.Values.Where(g => g.Collapsed && g.Id != _focusedGroupId)) hidden.UnionWith(group.Members);
            var focus = _focusedGroupId == null ? null : state.Groups.GetValueOrDefault(_focusedGroupId);
            foreach (var card in scene.Cards.Values)
                card.Frame.Visibility = hidden.Contains(card.Snapshot.Id) || focus != null && !focus.Members.Contains(card.Snapshot.Id)
                    ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_leaveGroupButton != null) _leaveGroupButton.Visibility = _focusedGroupId == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool IsCardShown(Scene scene, string id) =>
        scene.Cards.TryGetValue(id, out var card) && card.Frame.Visibility == Visibility.Visible;

    private void RevealCard(Scene scene, string id)
    {
        var active = scene.Number == _controller.WorkspaceNumber;
        if (_focusedGroupId != null && (!active || !_controller.WorkbenchLayout.Groups.TryGetValue(_focusedGroupId, out var focused) || !focused.Members.Contains(id))) LeaveGroupFocus();
        var state = SceneState(scene).Copy();
        var changed = false;
        foreach (var group in state.Groups.Values.Where(g => g.Collapsed && g.Members.Contains(id)))
        { group.Collapsed = false; changed = true; }
        // Reveal ancestors only; unrelated collapsed branches stay closed.
        var current = id; var visited = new HashSet<string>(StringComparer.Ordinal);
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(current))
        {
            var parent = state.Connections.FirstOrDefault(e => e.IsBranch && e.To == current)?.From;
            if (parent == null) break;
            ancestors.Add(parent);
            if (active && _controller.FindWorkbenchMaterial(parent)?.Collapsed == true)
                _controller.SetWorkbenchMaterialCollapsed(parent, false);
            current = parent;
        }
        if (active)
        {
            if (changed) StoreLayout(state);
        }
        else if (scene.Snapshot is { } snapshot)
        {
            // Inactive workspaces are read-only. Reveal only this overview's copied
            // layout and immutable summaries; a refresh restores their saved collapse state.
            var papers = snapshot.Papers.Select(p => ancestors.Contains(p.Id)
                ? p with { MaterialCollapsed = false } : p).ToArray();
            scene.Snapshot = snapshot with { Layout = state, Papers = papers };
            foreach (var paper in papers)
                if (scene.Cards.TryGetValue(paper.Id, out var card)) card.Snapshot = paper;
        }
        ApplySceneVisibility(); DrawDecorations(scene);
    }

    private void DrawWorkspaceGroups(Scene scene)
    {
        var state = SceneState(scene);
        foreach (var group in state.Groups.Values)
        {
            if (_focusedGroupId != null && group.Id != _focusedGroupId) continue;
            var cards = group.Members.Where(scene.Cards.ContainsKey).Select(id => scene.Cards[id]).ToArray();
            if (cards.Length == 0) continue;
            var b = WorkbenchRect.Union(cards.Select(c => c.Bounds)).Inflate(24);
            var collapsed = group.Collapsed && group.Id != _focusedGroupId;
            var frame = new Border
            {
                Width = collapsed ? 260 : b.Width, Height = collapsed ? 92 : b.Height + 42,
                Background = collapsed ? CardBrush : null,
                BorderBrush = Accent, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(9)
            };
            var head = new Grid { Height = 38, VerticalAlignment = VerticalAlignment.Top, Background = RegionFill };
            head.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var thumb = new Thumb { Tag = group.Title, Foreground = Ink, Cursor = Cursors.SizeAll, Background = Brushes.Transparent };
            thumb.Template = TitleDragTemplate(thumb); head.Children.Add(thumb);
            var collapseButton = NewButton(collapsed ? "▸" : "▾");
            collapseButton.Padding = new Thickness(5, 2, 5, 2);
            collapseButton.Background = Brushes.Transparent;
            collapseButton.BorderThickness = new Thickness(0);
            collapseButton.ToolTip = Strings.Get(collapsed ? "WorkbenchExpandGroup" : "WorkbenchCollapseGroup");
            Grid.SetColumn(collapseButton, 1); head.Children.Add(collapseButton);
            frame.Child = head;
            Canvas.SetLeft(frame, b.X); Canvas.SetTop(frame, b.Y - 42);
            Panel.SetZIndex(frame, collapsed ? 4 : 1);
            scene.Canvas.Children.Add(frame); scene.Decorations.Add(frame);
            if (scene.Number != _controller.WorkspaceNumber) { frame.IsHitTestVisible = false; continue; }
            thumb.PreviewMouseLeftButtonDown += (_, e) =>
            {
                _viewport.Focus();
                _selectedCards.Clear(); _selectedCards.UnionWith(group.Members); ApplySelectionStyle();
                if (e.ClickCount == 2) { RenameGroup(scene, group.Id, head, thumb); e.Handled = true; }
            };
            collapseButton.Click += (_, _) => ToggleGroupCollapsed(group.Id);
            Dictionary<string, WorkbenchRect>? before = null;
            var changed = false;
            thumb.DragStarted += (_, _) =>
            {
                if (IsSuspended || cards.Any(c => c.Snapshot.Placement?.Pinned == true)) return;
                before = cards.ToDictionary(c => c.Snapshot.Id, c => c.Bounds, StringComparer.Ordinal);
                foreach (var card in cards) Panel.SetZIndex(card.Frame, 12);
                Panel.SetZIndex(frame, 14);
                changed = false; _dragging = true;
            };
            thumb.DragDelta += (_, e) =>
            {
                if (!_dragging || before == null) return;
                if (Math.Abs(e.HorizontalChange) + Math.Abs(e.VerticalChange) < .001) return;
                if (!changed) { CancelLayout(); changed = true; }
                foreach (var card in cards) Position(card, card.Bounds.Offset(new(e.HorizontalChange, e.VerticalChange)));
                Canvas.SetLeft(frame, Canvas.GetLeft(frame) + e.HorizontalChange);
                Canvas.SetTop(frame, Canvas.GetTop(frame) + e.VerticalChange);
            };
            thumb.DragCompleted += (_, e) =>
            {
                if (!_dragging || before == null) return;
                _dragging = false;
                foreach (var card in cards) Panel.SetZIndex(card.Frame, 2);
                Panel.SetZIndex(frame, collapsed ? 4 : 1);
                FinishCardMove(before, e.Canceled || !changed); before = null;
            };
        }
    }

    private void RenameGroup(Scene scene, string id, Grid head, Thumb thumb)
    {
        if (!_controller.WorkbenchLayout.Groups.TryGetValue(id, out var group)) return;
        var editor = new TextBox { Text = group.Title, Style = WorkbenchInputStyle(), Foreground = Ink, Background = CardBrush };
        var done = false;
        void Finish(bool save)
        {
            if (done) return; done = true;
            if (save && _controller.WorkbenchLayout.Groups.ContainsKey(id))
            {
                RememberLayout(); var state = _controller.WorkbenchLayout.Copy();
                state.Groups[id].Title = editor.Text.Trim(); StoreLayout(state);
            }
            head.Children.Remove(editor); thumb.Visibility = Visibility.Visible;
            DrawDecorations(scene);
        }
        thumb.Visibility = Visibility.Collapsed; head.Children.Add(editor);
        editor.LostKeyboardFocus += (_, _) => Finish(true);
        editor.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape) { Finish(e.Key == Key.Enter); e.Handled = true; }
        };
        editor.Focus(); editor.SelectAll();
    }

}
