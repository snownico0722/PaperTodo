using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PaperTodo;

internal sealed partial class WorkbenchWindow
{
    private const string ContentTransferFormat = "PaperTodo.WorkbenchContent";
    private string? _connectionSource;
    private bool _replayingWorkspaceHistory;

    private void InitializeWorkspaceTools()
    {
        AddButton("WorkbenchNewNode", () => AddNode(ActivePoint(ViewportCenter)), _rightCommands);
        _leaveGroupButton = NewButton(Strings.Get("WorkbenchLeaveGroup"));
        _leaveGroupButton.Visibility = Visibility.Collapsed;
        _leaveGroupButton.Click += (_, _) => LeaveGroupFocus();
        _layoutCommands.Children.Add(_leaveGroupButton); _chromeButtons.Add(_leaveGroupButton);
        _viewport.AllowDrop = true;
        _viewport.DragOver += OnWorkspaceDragOver;
        _viewport.Drop += OnWorkspaceDrop;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Handled || IsSuspended) return;
            var mods = Keyboard.Modifiers;
            if (e.Key == Key.Escape && _connectionSource != null)
            { CancelWorkspaceGesture(); e.Handled = true; return; }
            // Keep text editor shortcuts native; selection commands belong to the canvas.
            if (Keyboard.FocusedElement is TextBox || HasTextEditorFocus()) return;
            if (mods == ModifierKeys.Control && e.Key == Key.G)
            { CreateGroupFromSelection(); e.Handled = true; }
            else if (mods == ModifierKeys.Control && e.Key == Key.A && ActiveScene is { } scene)
            {
                _selectedCards.Clear(); _selectedCards.UnionWith(scene.Cards.Values.Where(c => c.Frame.IsVisible).Select(c => c.Snapshot.Id));
                ApplySelectionStyle(); e.Handled = true;
            }
            else if (mods == ModifierKeys.Control && e.Key == Key.Z)
            { UndoWorkspaceAction(); e.Handled = true; }
            else if (mods == ModifierKeys.Control && e.Key == Key.Y)
            { RedoWorkspaceAction(); e.Handled = true; }
        };
    }

    private bool HasTextEditorFocus() => ActiveScene?.Cards.Values.Any(c => c.Body.IsKeyboardFocusWithin) == true;

    private WorkbenchPoint ActivePoint(Point screen)
    {
        var origin = ActiveScene?.Origin ?? new WorkbenchPoint();
        return new((screen.X - _pan.X) / _scale.ScaleX - origin.X, (screen.Y - _pan.Y) / _scale.ScaleY - origin.Y);
    }

    private bool CancelWorkspaceGesture()
    {
        if (_connectionSource != null)
        { _connectionSource = null; SetHint(); return true; }
        if (_marqueeStart != null)
        { CancelMarquee(); _viewport.ReleaseMouseCapture(); return true; }
        return false;
    }

    private void UndoWorkspaceAction()
    {
        if (_dragging || IsSuspended || !_undo.TryPeek(out var entry)) return;
        if (entry.Layout == null)
        {
            _replayingWorkspaceHistory = true;
            bool replayed;
            try { replayed = _controller.TryUndoWorkbenchContentTransfer(entry.TransferId); }
            finally { _replayingWorkspaceHistory = false; }
            if (!replayed) { OnTransferHistoryBlocked(); return; }
            _undo.Pop(); _redo.Push(entry);
        }
        else
        {
            CancelLayout(); _undo.Pop(); _redo.Push(new(_controller.WorkbenchLayout.Copy()));
            StoreLayout(entry.Layout);
        }
        _modelSignature = ""; SyncActiveScene();
    }

    private void RedoWorkspaceAction()
    {
        if (_dragging || IsSuspended || !_redo.TryPeek(out var entry)) return;
        if (entry.Layout == null)
        {
            _replayingWorkspaceHistory = true;
            bool replayed;
            try { replayed = _controller.TryRedoWorkbenchContentTransfer(entry.TransferId); }
            finally { _replayingWorkspaceHistory = false; }
            if (!replayed) { OnTransferHistoryBlocked(); return; }
            _redo.Pop(); _undo.Push(entry);
        }
        else
        {
            CancelLayout(); _redo.Pop(); _undo.Push(new(_controller.WorkbenchLayout.Copy()));
            StoreLayout(entry.Layout);
        }
        _modelSignature = ""; SyncActiveScene();
    }

    private void OnTransferHistoryBlocked() => _status.Text = Strings.Get("WorkbenchTransferHistoryBlocked");

    private void OnContentTransferReplayed(Guid id, bool redo)
    {
        if (_closed || IsSuspended || _replayingWorkspaceHistory) return;
        // Original editor commands and canvas commands replay the same transaction. Keep
        // the canvas cursor aligned when a native editor performs the shared undo/redo.
        RemoveToken(_undo); RemoveToken(_redo);
        (redo ? _undo : _redo).Push(new(null, id));
        void RemoveToken(Stack<WorkbenchUndoEntry> stack)
        {
            var retained = stack.Where(entry => entry.TransferId != id).Reverse().ToArray();
            stack.Clear();
            foreach (var entry in retained) stack.Push(entry);
        }
    }

    private void AddWorkspaceCanvasMenu(ContextMenu menu, WorkbenchPoint point)
    {
        AddMenu(menu, "WorkbenchNewNode", () => AddNode(point));
        AddMenu(menu, "WorkbenchViewMaterials", _controller.OpenWorkspaceMaterialsNote);
        if (_selectedCards.Count > 1) AddMenu(menu, "WorkbenchGroupSelection", CreateGroupFromSelection);
        if (_focusedGroupId != null) AddMenu(menu, "WorkbenchLeaveGroup", LeaveGroupFocus);
    }

    private string AddNode(WorkbenchPoint point, string? parentId = null)
    {
        if (IsSuspended) return "";
        var id = _controller.CreateWorkbenchMaterial(WorkspaceMaterialKinds.Node, "", "", "", point);
        if (string.IsNullOrEmpty(id)) return "";
        if (parentId != null) CreateConnection(parentId, id, branch: true);
        _focusAfterRefresh = id;
        SyncActiveScene(); SelectOnly(id);
        return id;
    }

    private void AddBranch(string id, bool sibling)
    {
        if (ActiveScene is not { } scene || !scene.Cards.TryGetValue(id, out var source)) return;
        var parent = sibling ? _controller.WorkbenchLayout.Connections.FirstOrDefault(c => c.IsBranch && c.To == id)?.From : id;
        var point = sibling
            ? new WorkbenchPoint(source.Bounds.X, source.Bounds.Bottom + 40)
            : new WorkbenchPoint(source.Bounds.Right + 72, source.Bounds.Y);
        if (parent != null) _controller.SetWorkbenchMaterialCollapsed(parent, false);
        AddNode(point, parent);
    }

    private void CreateConnection(string from, string to, bool branch = false)
    {
        if (from == to || ActiveScene is not { } scene || !scene.Cards.ContainsKey(from) ||
            !(_controller.FindWorkbenchMaterial(to) != null || scene.Cards.ContainsKey(to))) return;
        var state = _controller.WorkbenchLayout.Copy();
        if (state.Connections.Any(c => c.From == from && c.To == to)) return;
        RememberLayout();
        state.Connections.Add(new WorkbenchConnection
        {
            Id = "connection:" + Guid.NewGuid().ToString("N"), From = from, To = to, IsBranch = branch
        });
        StoreLayout(state); _modelSignature = ""; SyncActiveScene();
    }

    private void CompleteConnection(string target)
    {
        var source = _connectionSource; _connectionSource = null;
        if (source != null) CreateConnection(source, target);
        SetHint();
    }

    private void AddCardWorkspaceMenu(ContextMenu menu, Card card, PaperWindow? owner)
    {
        if (!_selectedCards.Contains(card.Snapshot.Id)) SelectOnly(card.Snapshot.Id);
        if (_selectedCards.Count > 1) AddMenu(menu, "WorkbenchGroupSelection", CreateGroupFromSelection);
        foreach (var group in _controller.WorkbenchLayout.Groups.Values.Where(g => g.Members.Contains(card.Snapshot.Id)))
        {
            AddMenu(menu, "WorkbenchFocusGroup", () => FocusGroup(group.Id));
            AddMenu(menu, group.Collapsed ? "WorkbenchExpandGroup" : "WorkbenchCollapseGroup", () => ToggleGroupCollapsed(group.Id));
            AddMenu(menu, "WorkbenchUngroup", () => Ungroup(group.Id));
        }
        AddMenu(menu, "WorkbenchConnect", () =>
        { _connectionSource = card.Snapshot.Id; _status.Text = Strings.Get("WorkbenchConnectHint"); });
        AddMenu(menu, "WorkbenchAddChild", () => AddBranch(card.Snapshot.Id, sibling: false));
        if (_controller.FindWorkbenchMaterial(card.Snapshot.Id) is { } material)
        {
            AddMenu(menu, "WorkbenchAddSibling", () => AddBranch(card.Snapshot.Id, sibling: true));
            if (_controller.WorkbenchLayout.Connections.Any(c => c.IsBranch && c.From == card.Snapshot.Id))
                AddMenu(menu, material.Collapsed ? "WorkbenchExpandBranch" : "WorkbenchCollapseBranch", () =>
                {
                    _controller.SetWorkbenchMaterialCollapsed(card.Snapshot.Id, !material.Collapsed);
                    ApplySceneVisibility(); if (ActiveScene is { } scene) DrawDecorations(scene);
                });
        }
        if (owner == null) return;
        menu.Items.Add(new Separator());
        AddMenu(menu, "WorkbenchSplitSelection", () =>
        {
            if (!owner.TryCreateWorkbenchContentTransfer(out var payload)) { _status.Text = Strings.Get("WorkbenchNoSelection"); return; }
            ApplyContentTransfer(payload, null, new(card.Bounds.Right + 40, card.Bounds.Y));
        });
        AddTransferTargets(menu, card, owner, wholePaper: false);
        AddTransferTargets(menu, card, owner, wholePaper: true);
    }

    private void AddTransferTargets(ContextMenu menu, Card card, PaperWindow owner, bool wholePaper)
    {
        var targets = new MenuItem { Header = Strings.Get(wholePaper ? "WorkbenchMergePaperTo" : "WorkbenchMoveSelectionTo") };
        foreach (var paper in _controller.State.Papers.Where(p => p.Id != card.Snapshot.Id &&
            p.WorkspaceMaterials == null && p.Type == card.Snapshot.Kind))
        {
            var target = new MenuItem { Header = _controller.PaperTitleText(paper) };
            target.Click += (_, _) =>
            {
                if (!owner.TryCreateWorkbenchContentTransfer(out var payload, wholePaper))
                { _status.Text = Strings.Get("WorkbenchNoSelection"); return; }
                ApplyContentTransfer(payload, paper.Id, null);
            };
            targets.Items.Add(target);
        }
        targets.IsEnabled = targets.Items.Count > 0; menu.Items.Add(targets);
    }

    private bool ApplyContentTransfer(WorkbenchContentTransferPayload payload, string? target, WorkbenchPoint? point)
    {
        if (!_controller.CanTransferWorkbenchContent(payload, target) ||
            !_controller.TransferWorkbenchContent(payload, target, point))
        { _status.Text = Strings.Get("WorkbenchTransferRejected"); return false; }
        _undo.Push(new(null, _controller.LastWorkbenchContentTransferId)); _redo.Clear();
        SyncActiveScene();
        return true;
    }

    private Card CreateMaterialCard(WorkbenchPaperSnapshot paper)
    {
        var card = CreateCardShell(paper);
        card.Eye.Visibility = Visibility.Collapsed;
        if (paper.Kind == "material:" + WorkspaceMaterialKinds.Node && card.Frame.Child is Grid shell)
        {
            card.Header.Margin = new Thickness(3, 1, 3, 0);
            card.Move.MinHeight = 22;
            foreach (var button in new[] { card.Pin, card.Menu }) button.Padding = new Thickness(3, 0, 3, 0);
            if (shell.Children.OfType<Grid>().LastOrDefault(g => Grid.GetRow(g) == 3) is { } footer) footer.Height = 16;
        }
        var item = _controller.FindWorkbenchMaterial(paper.Id);
        var body = new DockPanel { Margin = new Thickness(12, 4, 12, 10) };
        if (item?.Kind == WorkspaceMaterialKinds.Image)
        {
            card.MaterialImage = new Image { Stretch = Stretch.Uniform, Source = _controller.ReadWorkbenchMaterialImage(paper.Id) };
            body.Children.Add(card.MaterialImage);
        }
        else
        {
            if (item?.Kind is WorkspaceMaterialKinds.Link or WorkspaceMaterialKinds.File)
            {
                var open = card.MaterialOpen = NewButton(Strings.Get("WorkbenchOpenMaterial"));
                DockPanel.SetDock(open, Dock.Bottom);
                open.Click += (_, _) => OpenMaterial(paper.Id);
                body.Children.Add(open);
            }
            var editor = new TextBox
            {
                Text = item?.Content ?? paper.Summary, MaxLength = PaperWindow.NoteTextMaxLength,
                AcceptsReturn = true, AcceptsTab = false,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.Transparent, Foreground = Ink, CaretBrush = Ink,
                BorderThickness = new Thickness(0), Padding = new Thickness(4),
                FontSize = AppTypography.Scale(item?.Kind == WorkspaceMaterialKinds.Node ? 16 : 14)
            };
            _controller.AttachWorkspaceMaterialImagePaste(editor, Report);
            card.MaterialEditor = editor;
            editor.TextChanged += (_, _) =>
            {
                if (_closed || IsSuspended || !editor.IsKeyboardFocusWithin) return;
                try
                {
                    if (_controller.FindWorkbenchMaterial(paper.Id) is { } live)
                        _controller.UpdateWorkbenchMaterial(paper.Id, live.Title, editor.Text);
                }
                catch (Exception ex) { Report(ex); }
            };
            if (item?.Kind == WorkspaceMaterialKinds.Node)
                editor.PreviewKeyDown += (_, e) =>
                {
                    if (Keyboard.Modifiers != ModifierKeys.None) return;
                    if (e.Key is Key.Tab or Key.Enter)
                    { AddBranch(paper.Id, sibling: e.Key == Key.Enter); e.Handled = true; }
                };
            body.Children.Add(editor);
        }
        card.Body.Child = body;
        card.MaterialVersion = item?.Content ?? "";
        card.Move.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2 || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true; card.Move.Visibility = Visibility.Collapsed; card.Title.Visibility = Visibility.Visible;
            card.Title.Focus(); card.Title.SelectAll();
        };
        card.Title.LostKeyboardFocus += (_, _) =>
        {
            if (card.Title.Visibility != Visibility.Visible) return;
            CommitCardTitle(card); card.Move.Visibility = Visibility.Visible; card.Title.Visibility = Visibility.Collapsed;
        };
        card.Title.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            if (e.Key == Key.Escape) card.Title.Text = _controller.FindWorkbenchMaterial(paper.Id)?.Title ?? "";
            CommitCardTitle(card); Keyboard.ClearFocus(); e.Handled = true;
        };
        card.Pin.Click += (_, _) =>
        {
            RememberLayout(); var state = _controller.WorkbenchLayout.Copy();
            if (state.Cards.TryGetValue(paper.Id, out var placement)) placement.Pinned = !placement.Pinned;
            StoreLayout(state); SyncActiveScene();
        };
        card.Menu.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = card.Menu };
            AddCardWorkspaceMenu(menu, card, null);
            menu.Items.Add(new Separator());
            AddMenu(menu, "WorkbenchDeleteMaterial", () =>
            {
                if (MessageBox.Show(this, Strings.Get("WorkbenchDeleteMaterialConfirm"), Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                _controller.DeleteWorkbenchMaterial(paper.Id); SyncActiveScene();
            });
            menu.IsOpen = true;
        };
        WireCardSelection(card); WireDrag(card, card.Move, false); WireDrag(card, card.Resize, true);
        return card;
    }

    private void RefreshMaterialCard(Card card)
    {
        if (!WorkspaceMaterials.IsMaterialId(card.Snapshot.Id) ||
            _controller.FindWorkbenchMaterial(card.Snapshot.Id) is not { } item) return;
        if (card.MaterialEditor is { IsKeyboardFocusWithin: false } editor && editor.Text != item.Content)
            editor.Text = item.Content;
        if (card.MaterialImage != null && card.MaterialVersion != item.Content)
            card.MaterialImage.Source = _controller.ReadWorkbenchMaterialImage(card.Snapshot.Id);
        card.MaterialVersion = item.Content;
    }

    private void CommitCardTitle(Card card)
    {
        if (_closed || IsSuspended) return;
        if (_controller.FindWorkbenchMaterial(card.Snapshot.Id) is { } item)
            _controller.UpdateWorkbenchMaterial(card.Snapshot.Id, card.Title.Text, item.Content);
        else if (_controller.FindPaper(card.Snapshot.Id) is { } paper)
            _controller.UpdatePaperTitle(paper, card.Title.Text);
        card.Move.Tag = card.Title.Text;
    }

    private void OpenMaterial(string id)
    {
        try { _controller.OpenWorkbenchMaterial(id); }
        catch (Exception ex) { Report(ex); }
    }

    private Card? CardAt(Point screen)
    {
        if (ActiveScene is not { } scene) return null;
        var p = ActivePoint(screen);
        return scene.Cards.Values.LastOrDefault(c => c.Frame.Visibility == Visibility.Visible && c.Bounds.Contains(p));
    }

    private bool IsReadOnlyWorkspacePoint(Point screen)
    {
        var worldPoint = new WorkbenchPoint((screen.X - _pan.X) / _scale.ScaleX,
            (screen.Y - _pan.Y) / _scale.ScaleY);
        return _scenes.Values.Any(scene => scene.Number != _controller.WorkspaceNumber &&
            scene.Canvas.Visibility == Visibility.Visible && scene.Bounds.Offset(scene.Origin).Contains(worldPoint));
    }

    private void WireContentDrop(Card card)
    {
        card.Frame.AllowDrop = true;
        card.Frame.PreviewDragOver += (_, e) =>
        {
            if (e.Data.GetData(ContentTransferFormat) is not WorkbenchContentTransferPayload payload || payload.SourcePaperId == card.Snapshot.Id) return;
            e.Effects = _controller.CanTransferWorkbenchContent(payload, card.Snapshot.Id) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        card.Frame.PreviewDrop += (_, e) =>
        {
            if (e.Data.GetData(ContentTransferFormat) is not WorkbenchContentTransferPayload payload || payload.SourcePaperId == card.Snapshot.Id) return;
            var allowed = ApplyContentTransfer(payload, card.Snapshot.Id, null);
            e.Effects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
    }

    private static Size ResizeMinimum(Card card) => card.Snapshot.Kind == "material:" + WorkspaceMaterialKinds.Node
        ? new Size(120, 96) : WorkspaceMaterials.IsMaterialId(card.Snapshot.Id) ? new Size(200, 160) : new Size(240, 240);

    private void OnWorkspaceDragOver(object sender, DragEventArgs e)
    {
        if (e.Handled || IsSuspended || _closed) return;
        var screen = e.GetPosition(_viewport);
        if (IsReadOnlyWorkspacePoint(screen))
        { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        var card = CardAt(screen);
        if (card != null) return; // Body editors retain their native drag/drop behavior.
        if (e.Data.GetData(ContentTransferFormat) is WorkbenchContentTransferPayload payload)
            e.Effects = _controller.CanTransferWorkbenchContent(payload, null) ? DragDropEffects.Move : DragDropEffects.None;
        else if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText) ||
                 e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.Bitmap) ||
                 e.Data.GetDataPresent("UniformResourceLocatorW") || e.Data.GetDataPresent("UniformResourceLocator"))
            e.Effects = DragDropEffects.Copy;
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWorkspaceDrop(object sender, DragEventArgs e)
    {
        if (e.Handled || IsSuspended || _closed) return;
        var screen = e.GetPosition(_viewport);
        // Overview regions are read-only. Unassigned canvas space still belongs to the
        // active workspace, but a visible neighboring workspace must never redirect a drop.
        if (IsReadOnlyWorkspacePoint(screen))
        { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (CardAt(screen) != null) return;
        var point = ActivePoint(screen);
        try
        {
            if (e.Data.GetData(ContentTransferFormat) is WorkbenchContentTransferPayload payload)
            {
                var allowed = ApplyContentTransfer(payload, null, point);
                e.Effects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
            }
            else
            {
                var ids = _controller.ImportWorkbenchData(e.Data, point);
                e.Effects = ids.Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
                SyncActiveScene(); _selectedCards.Clear(); _selectedCards.UnionWith(ids); ApplySelectionStyle();
            }
        }
        catch (Exception ex) { Report(ex); e.Effects = DragDropEffects.None; }
        e.Handled = true;
    }

    private void DrawWorkspaceConnections(Scene scene)
    {
        foreach (var connection in SceneState(scene).Connections)
        {
            if (!IsCardShown(scene, connection.From) || !IsCardShown(scene, connection.To)) continue;
            var from = scene.Cards[connection.From].Bounds; var to = scene.Cards[connection.To].Bounds;
            var start = ConnectionEdge(from, to.Center); var end = ConnectionEdge(to, from.Center);
            var line = new Line
            {
                X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y,
                Stroke = Accent, StrokeThickness = 2, Opacity = .85
            };
            AddConnectionVisual(scene, line, 0, 0);
            var vector = new Vector(end.X - start.X, end.Y - start.Y);
            if (vector.Length > .01)
            {
                vector.Normalize(); var side = new Vector(-vector.Y, vector.X);
                var arrow = new Polygon { Fill = Accent, IsHitTestVisible = false,
                    Points = new PointCollection { new(end.X, end.Y), new(end.X - vector.X * 10 + side.X * 5, end.Y - vector.Y * 10 + side.Y * 5),
                        new(end.X - vector.X * 10 - side.X * 5, end.Y - vector.Y * 10 - side.Y * 5) } };
                AddConnectionVisual(scene, arrow, 0, 0);
            }
            var middle = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);
            if (!string.IsNullOrWhiteSpace(connection.Label))
                AddConnectionVisual(scene, new Border { Background = CardBrush, Padding = new Thickness(5, 2, 5, 2), CornerRadius = new CornerRadius(4),
                    Child = new TextBlock { Text = connection.Label, Foreground = Ink, MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis } }, middle.X, middle.Y);
            if (scene.Number != _controller.WorkspaceNumber) continue;
            var hit = new Line { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, Stroke = Brushes.Transparent, StrokeThickness = 14 };
            var menu = new ContextMenu();
            AddMenu(menu, "WorkbenchConnectionLabel", () => EditConnectionLabel(scene, connection.Id, middle));
            AddMenu(menu, "WorkbenchRemoveConnection", () =>
            {
                RememberLayout(); var state = _controller.WorkbenchLayout.Copy();
                state.Connections.RemoveAll(c => c.Id == connection.Id); StoreLayout(state); _modelSignature = ""; SyncActiveScene();
            });
            hit.ContextMenu = menu; AddConnectionVisual(scene, hit, 0, 0);
        }
    }

    private static WorkbenchPoint ConnectionEdge(WorkbenchRect bounds, WorkbenchPoint toward)
    {
        var center = bounds.Center; var dx = toward.X - center.X; var dy = toward.Y - center.Y;
        if (Math.Abs(dx) + Math.Abs(dy) < .001) return center;
        var scale = Math.Min(Math.Abs(dx) < .001 ? double.PositiveInfinity : bounds.Width / 2 / Math.Abs(dx),
            Math.Abs(dy) < .001 ? double.PositiveInfinity : bounds.Height / 2 / Math.Abs(dy));
        return new(center.X + dx * scale, center.Y + dy * scale);
    }

    private static void AddConnectionVisual(Scene scene, UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x); Canvas.SetTop(element, y); Panel.SetZIndex(element, 1);
        scene.Canvas.Children.Add(element); scene.Decorations.Add(element);
    }

    private void EditConnectionLabel(Scene scene, string id, Point middle)
    {
        var connection = _controller.WorkbenchLayout.Connections.FirstOrDefault(c => c.Id == id);
        if (connection == null) return;
        var editor = new TextBox { Text = connection.Label, Width = 180, Background = CardBrush, Foreground = Ink, Style = WorkbenchInputStyle() };
        AddConnectionVisual(scene, editor, middle.X, middle.Y); Panel.SetZIndex(editor, 8);
        var finished = false;
        void Finish(bool save)
        {
            if (finished) return; finished = true;
            if (save)
            {
                RememberLayout(); var state = _controller.WorkbenchLayout.Copy();
                var target = state.Connections.FirstOrDefault(c => c.Id == id);
                if (target != null) target.Label = editor.Text.Trim();
                StoreLayout(state);
            }
            DrawDecorations(scene);
        }
        editor.LostKeyboardFocus += (_, _) => Finish(true);
        editor.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Escape) { Finish(e.Key == Key.Enter); e.Handled = true; } };
        editor.Focus(); editor.SelectAll();
    }
}
