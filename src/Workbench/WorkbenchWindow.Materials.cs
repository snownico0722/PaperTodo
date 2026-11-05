using Microsoft.Win32;
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
    private Line? _connectionPreview;
    private bool _replayingWorkspaceHistory;

    private void InitializeWorkspaceTools()
    {
        AddButton("WorkbenchImportMaterials", ImportMaterialsFromPicker, _rightCommands);
        AddButton("WorkbenchNewNode", () => AddNode(ActivePoint(ViewportCenter)), _rightCommands);
        _groupButton = NewButton(Strings.Get("WorkbenchGroupSelection"));
        _groupButton.Visibility = Visibility.Collapsed;
        _groupButton.Click += (_, _) => CreateGroupFromSelection();
        _layoutCommands.Children.Add(_groupButton); _chromeButtons.Add(_groupButton);
        _ungroupButton = NewButton(Strings.Get("WorkbenchUngroup"));
        _ungroupButton.Visibility = Visibility.Collapsed;
        _ungroupButton.Click += (_, _) => UngroupSelection();
        _layoutCommands.Children.Add(_ungroupButton); _chromeButtons.Add(_ungroupButton);
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
            else if (mods == ModifierKeys.None && e.Key == Key.Delete && _selectedCards.Count == 1)
            {
                var id = _selectedCards.Single();
                if (_controller.FindWorkbenchMaterial(id) != null)
                {
                    if (MessageBox.Show(this, Strings.Get("WorkbenchDeleteMaterialConfirm"), Title,
                        MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                    {
                        _controller.DeleteWorkbenchMaterial(id);
                        _selectedCards.Clear(); SyncActiveScene();
                    }
                    e.Handled = true;
                }
            }
        };
    }

    private void ImportMaterialsFromPicker()
    {
        if (_closed || IsSuspended) return;
        var picker = new OpenFileDialog
        {
            Title = Strings.Get("WorkbenchImportMaterials"),
            Filter = Strings.Get("WorkbenchImportDialogFilter"),
            Multiselect = true,
            CheckFileExists = true,
            CheckPathExists = true
        };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            // Exactly the same import path as files dragged onto the canvas:
            // images belong to the image library, other files stay as path references.
            var data = new DataObject(DataFormats.FileDrop, picker.FileNames);
            var ids = _controller.ImportWorkbenchData(data, ActivePoint(ViewportCenter));
            SyncActiveScene();
            _selectedCards.Clear();
            _selectedCards.UnionWith(ids);
            ApplySelectionStyle();
        }
        catch (Exception ex) { Report(ex); }
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
        { _connectionSource = null; ClearConnectionPreview(); SetHint(); return true; }
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
            CancelLayout(); _undo.Pop(); _redo.Push(new(_controller.CaptureWorkbenchHistoryLayout()));
            RestoreHistoryLayout(entry.Layout);
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
            CancelLayout(); _redo.Pop(); _undo.Push(new(_controller.CaptureWorkbenchHistoryLayout()));
            RestoreHistoryLayout(entry.Layout);
        }
        _modelSignature = ""; SyncActiveScene();
    }

    private void RestoreHistoryLayout(WorkbenchState layout)
    {
        _storingLayout = true;
        try { _controller.RestoreWorkbenchHistoryLayout(layout); }
        finally { _storingLayout = false; }
    }

    private void OnContentTransfersRetired(IReadOnlySet<Guid> ids)
    {
        RemoveRetired(_undo);
        RemoveRetired(_redo);
        void RemoveRetired(Stack<WorkbenchUndoEntry> stack)
        {
            var retained = stack.Where(entry => entry.TransferId is not { } id || !ids.Contains(id)).Reverse().ToArray();
            stack.Clear();
            foreach (var entry in retained) stack.Push(entry);
        }
    }

    private void OnTransferHistoryBlocked() => ShowStatus(Strings.Get("WorkbenchTransferHistoryBlocked"));

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

    private string AddNode(WorkbenchPoint point)
    {
        if (IsSuspended) return "";
        var id = _controller.CreateWorkbenchMaterial(WorkspaceMaterialKinds.Node, "", "", "", point);
        if (string.IsNullOrEmpty(id)) return "";
        _focusAfterRefresh = id;
        SyncActiveScene(); SelectOnly(id);
        return id;
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

    private void ClearConnectionPreview()
    {
        if (_connectionPreview == null) return;
        foreach (var scene in _scenes.Values) scene.Canvas.Children.Remove(_connectionPreview);
        _connectionPreview = null;
    }

    private void WireConnectionDrag(Card card)
    {
        var id = card.Snapshot.Id;
        card.Connect.DragStarted += (_, _) =>
        {
            if (_closed || IsSuspended || ActiveScene is not { } scene || !scene.Cards.ContainsKey(id)) return;
            CancelWorkspaceGesture();
            _connectionSource = id;
            var start = scene.Cards[id].Bounds.Center;
            _connectionPreview = new Line
            {
                X1 = start.X, Y1 = start.Y, X2 = start.X, Y2 = start.Y,
                Stroke = Accent, StrokeThickness = 2, Opacity = .7, IsHitTestVisible = false
            };
            Panel.SetZIndex(_connectionPreview, 8);
            scene.Canvas.Children.Add(_connectionPreview);
        };
        card.Connect.DragDelta += (_, _) =>
        {
            if (_connectionSource != id || _connectionPreview == null) return;
            var at = ActivePoint(Mouse.GetPosition(_viewport));
            _connectionPreview.X2 = at.X; _connectionPreview.Y2 = at.Y;
        };
        card.Connect.DragCompleted += (_, e) =>
        {
            if (_connectionSource != id) return;
            var target = ActiveScene is { } scene && !e.Canceled
                ? CardAt(Mouse.GetPosition(_viewport)) : null;
            _connectionSource = null;
            ClearConnectionPreview();
            if (target != null && target.Snapshot.Id != id) CreateConnection(id, target.Snapshot.Id);
            else SetHint();
        };
    }

    private bool ApplyContentTransfer(WorkbenchContentTransferPayload payload, string? target, WorkbenchPoint? point)
    {
        if (!_controller.CanTransferWorkbenchContent(payload, target) ||
            !_controller.TransferWorkbenchContent(payload, target, point))
        { ShowStatus(Strings.Get("WorkbenchTransferRejected")); return false; }
        _undo.Push(new(null, _controller.LastWorkbenchContentTransferId)); _redo.Clear();
        SyncActiveScene();
        return true;
    }

    private Card CreateMaterialCard(WorkbenchPaperSnapshot paper)
    {
        var item = _controller.FindWorkbenchMaterial(paper.Id);
        if (item?.Kind is WorkspaceMaterialKinds.File or WorkspaceMaterialKinds.Link)
            return CreateExternalIconCard(paper, item);
        var card = CreateCardShell(paper, compact: true);
        var body = new DockPanel { Margin = item?.Kind == WorkspaceMaterialKinds.Image
            ? new Thickness(5) : new Thickness(8, 3, 8, 6) };
        if (item?.Kind == WorkspaceMaterialKinds.Image)
        {
            card.MaterialImage = new Image { Stretch = Stretch.Uniform, Source = _controller.ReadWorkbenchMaterialImage(paper.Id) };
            WireMaterialImageDrag(card);
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
        card.Pin.Click += (_, _) => ToggleWorkbenchCardPinned(paper.Id);
        card.Fold.Click += (_, _) => ToggleWorkbenchCardCollapsed(paper.Id);
        card.Frame.ContextMenu = CreateMaterialContextMenu(paper.Id);
        WireCardSelection(card); WireConnectionDrag(card); WireDrag(card, card.Move, false); WireDrag(card, card.Resize, true);
        return card;
    }

    private static ControlTemplate IconTileDragTemplate()
    {
        var visual = new FrameworkElementFactory(typeof(Border));
        visual.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        visual.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        return new ControlTemplate(typeof(Thumb)) { VisualTree = visual };
    }

    private Card CreateExternalIconCard(WorkbenchPaperSnapshot paper, WorkspaceMaterialItem item)
    {
        var card = new Card(paper);
        card.Frame.Background = Brushes.Transparent;
        card.Frame.BorderBrush = Brushes.Transparent;
        card.Frame.BorderThickness = new Thickness(0);
        card.Frame.CornerRadius = new CornerRadius(11);
        card.Frame.ClipToBounds = true;
        var surface = new Grid();
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 6, 6, 8)
        };
        card.MaterialImage = new Image
        {
            Width = 48, Height = 48, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        card.FileTitle = new TextBlock
        {
            Text = item.Title, Foreground = Ink, FontSize = AppTypography.Scale(12),
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 40,
            MaxWidth = 104, Margin = new Thickness(0, 7, 0, 0)
        };
        content.Children.Add(card.MaterialImage);
        content.Children.Add(card.FileTitle);
        surface.Children.Add(content);

        card.Move.Template = IconTileDragTemplate();
        card.Move.Background = Brushes.Transparent;
        card.Move.Cursor = paper.Placement?.Pinned == true ? Cursors.Arrow : Cursors.SizeAll;
        surface.Children.Add(card.Move);
        card.Pin.Content = LockIcon(paper.Placement?.Pinned == true);
        card.Pin.ToolTip = Strings.Get(paper.Placement?.Pinned == true ? "WorkbenchUnlockCard" : "WorkbenchLockCard");
        card.Pin.Background = Brushes.Transparent;
        card.Pin.BorderThickness = new Thickness(0);
        card.Pin.Padding = new Thickness(2);
        card.Pin.Template = RoundedButtonTemplate();
        card.Pin.HorizontalAlignment = HorizontalAlignment.Right;
        card.Pin.VerticalAlignment = VerticalAlignment.Top;
        Panel.SetZIndex(card.Pin, 3);
        surface.Children.Add(card.Pin);
        ConfigureConnectionThumb(card.Connect);
        card.Connect.HorizontalAlignment = HorizontalAlignment.Right;
        card.Connect.VerticalAlignment = VerticalAlignment.Bottom;
        Panel.SetZIndex(card.Connect, 3);
        surface.Children.Add(card.Connect);
        card.Fold.Visibility = Visibility.Collapsed;
        card.Resize.Visibility = Visibility.Collapsed;
        card.Frame.Child = surface;
        card.Frame.Tag = card;
        Panel.SetZIndex(card.Frame, 2);
        Position(card, paper.Placement?.Bounds ?? new(32, 32, paper.Width, paper.Height));
        card.Move.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2) return;
            e.Handled = true;
            OpenMaterial(paper.Id);
        };
        card.Pin.Click += (_, _) => ToggleWorkbenchCardPinned(paper.Id);
        card.Frame.ContextMenu = CreateMaterialContextMenu(paper.Id);
        WireCardSelection(card); WireConnectionDrag(card); WireDrag(card, card.Move, false);
        card.MaterialVersion = item.Source;
        _ = LoadExternalIconAsync(card, item.Source, item.Kind == WorkspaceMaterialKinds.Link);
        return card;
    }

    private async Task LoadExternalIconAsync(Card card, string source, bool link)
    {
        try
        {
            // Shell icon extraction may touch slow/remote disks. Never block the WPF UI thread.
            var icon = await Task.Run(() => WorkbenchShellIcon.Load(source, link));
            if (_closed || IsSuspended || card.MaterialImage == null ||
                _controller.FindWorkbenchMaterial(card.Snapshot.Id)?.Source != source) return;
            card.MaterialImage.Source = icon;
        }
        catch (Exception ex) { if (!_closed) Report(ex); }
    }

    private void WireMaterialImageDrag(Card card)
    {
        if (card.MaterialImage is not { } image) return;
        Point? dragStart = null;
        image.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 1 && e.ChangedButton == MouseButton.Left)
                dragStart = e.GetPosition(image);
        };
        image.PreviewMouseLeftButtonUp += (_, _) => dragStart = null;
        image.PreviewMouseMove += (_, e) =>
        {
            if (dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed ||
                (e.GetPosition(image) - start).LengthSquared <
                SystemParameters.MinimumHorizontalDragDistance * SystemParameters.MinimumHorizontalDragDistance)
                return;
            dragStart = null;
            var item = _controller.FindWorkbenchMaterial(card.Snapshot.Id);
            var id = item == null ? null : MarkdownImageReferences.CollectImageIds(item.Content).FirstOrDefault();
            var bitmap = id == null ? null : _controller.ImageStore.GetBitmapSourceForClipboard(id);
            if (bitmap == null) return;
            var data = new DataObject();
            data.SetData(DataFormats.Bitmap, bitmap);
            DragDrop.DoDragDrop(image, data, DragDropEffects.Copy);
            e.Handled = true;
        };
    }

    private ContextMenu CreateMaterialContextMenu(string id)
    {
        var menu = new ContextMenu();
        void Populate()
        {
            menu.Items.Clear();
            menu.Background = CardBrush;
            menu.Foreground = Ink;
            menu.BorderBrush = Outline;
            menu.BorderThickness = new Thickness(1);
            if (_controller.FindWorkbenchMaterial(id) is not { } material) return;
            void Add(string key, Action action)
            {
                var item = new MenuItem { Header = Strings.Get(key) };
                item.Click += (_, _) => action();
                menu.Items.Add(item);
            }
            Add("WorkbenchDeleteMaterial", () =>
            {
                if (MessageBox.Show(this, Strings.Get("WorkbenchDeleteMaterialConfirm"), Title,
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                _controller.DeleteWorkbenchMaterial(id);
                SyncActiveScene();
            });
        }
        Populate();
        menu.Opened += (_, _) => Populate();
        return menu;
    }

    private void RefreshMaterialCard(Card card)
    {
        if (!WorkspaceMaterials.IsMaterialId(card.Snapshot.Id) ||
            _controller.FindWorkbenchMaterial(card.Snapshot.Id) is not { } item) return;
        if (card.MaterialEditor is { IsKeyboardFocusWithin: false } editor && editor.Text != item.Content)
            editor.Text = item.Content;
        if (item.Kind is WorkspaceMaterialKinds.File or WorkspaceMaterialKinds.Link)
        {
            if (card.MaterialVersion != item.Source)
            {
                card.MaterialVersion = item.Source;
                _ = LoadExternalIconAsync(card, item.Source, item.Kind == WorkspaceMaterialKinds.Link);
            }
            return;
        }
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
        return scene.Cards.Values.Where(c => c.Frame.Visibility == Visibility.Visible && c.Bounds.Contains(p))
            .OrderByDescending(c => Panel.GetZIndex(c.Frame)).FirstOrDefault();
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
            // Left-double-click a connection to edit its label; select it and press
            // Delete to remove it. No right-click menu on the canvas.
            var hit = new Line
            {
                X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y,
                Stroke = Brushes.Transparent, StrokeThickness = 14, Focusable = true
            };
            hit.MouseLeftButtonDown += (_, e) =>
            {
                _selectedCards.Clear(); ApplySelectionStyle();
                hit.Focus();
                if (e.ClickCount == 2) EditConnectionLabel(scene, connection.Id, middle);
                e.Handled = true;
            };
            hit.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Delete) return;
                RememberLayout(); var state = _controller.WorkbenchLayout.Copy();
                state.Connections.RemoveAll(c => c.Id == connection.Id);
                StoreLayout(state); _modelSignature = ""; SyncActiveScene();
                e.Handled = true;
            };
            AddConnectionVisual(scene, hit, 0, 0);
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
