using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>A presentation owner for the continuous desk, not an alternative content store.</summary>
internal sealed class WorkbenchWindow : Window
{
    private readonly AppController _controller;
    private readonly Grid _viewport = new() { ClipToBounds = true };
    private readonly Canvas _world = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _pan = new();
    private readonly TextBox _search = new() { Width = 180, Margin = new Thickness(8, 4, 8, 4), Padding = new Thickness(8, 5, 8, 5) };
    private readonly TextBlock _status = new() { Margin = new Thickness(20, 6, 20, 8), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _zoom = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 48, TextAlignment = TextAlignment.Center };
    private readonly WrapPanel _toolbar = new() { Margin = new Thickness(12, 8, 12, 8) };
    private readonly Dictionary<int, Scene> _scenes = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly Stack<WorkbenchState> _undo = new();
    private CancellationTokenSource? _layoutCancellation;
    private CancellationTokenSource? _overviewCancellation;
    private long _layoutRevision;
    private string _modelSignature = "";
    private string? _focusAfterRefresh;
    private int _searchCursor;
    private Point? _panPointer;
    private Point _panStart;
    private bool _closed, _shutdown, _dragging, _storingLayout;
    internal bool IsSuspended { get; private set; }
    internal int HostedPaperCount => _scenes.Values.Sum(s => s.Cards.Count(c => c.Value.Lease != null));
    private Scene? ActiveScene => _scenes.GetValueOrDefault(_controller.WorkspaceNumber);
    private static Brush DeskBrush => new SolidColorBrush(Theme.IsDark ? Color.FromRgb(25, 28, 34) : Color.FromRgb(239, 242, 247));
    private static Brush CardBrush => new SolidColorBrush(Theme.IsDark ? Color.FromRgb(36, 40, 48) : Colors.White);
    private static Brush Ink => new SolidColorBrush(Theme.IsDark ? Color.FromRgb(228, 232, 240) : Color.FromRgb(38, 48, 67));
    private static Brush Muted => new SolidColorBrush(Theme.IsDark ? Color.FromRgb(148, 160, 181) : Color.FromRgb(112, 128, 151));
    private static Brush Accent => new SolidColorBrush(Color.FromRgb(77, 124, 216));

    internal WorkbenchWindow(AppController controller)
    {
        _controller = controller;
        Title = Strings.Get("WorkbenchTitle");
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = DeskBrush;
        Foreground = Ink;
        MinWidth = 640; MinHeight = 420;
        var root = new Grid();
        Content = root;
        root.Children.Add(_viewport);
        _viewport.Background = DeskBrush;
        var transform = new TransformGroup(); transform.Children.Add(_scale); transform.Children.Add(_pan);
        _world.RenderTransform = transform;
        _viewport.Children.Add(_world);

        // Use the compact floating workbench bar from #398, not a permanently docked
        // ribbon. Navigation and creation stay available without losing canvas space.
        var toolbarHost = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = CardBrush, BorderBrush = Muted, BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 6, 4), Margin = new Thickness(16, 14, 16, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top
        };
        var toolbarGrid = new Grid();
        toolbarGrid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        toolbarGrid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        toolbarGrid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        toolbarGrid.Children.Add(new TextBlock
        {
            Text = Title, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = Ink, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 16, 0)
        });
        _toolbar.Margin = new Thickness(0);
        Grid.SetColumn(_toolbar, 2);
        toolbarGrid.Children.Add(_toolbar);
        toolbarHost.Child = toolbarGrid;
        Panel.SetZIndex(toolbarHost, 10);
        root.Children.Add(toolbarHost);
        _status.VerticalAlignment = VerticalAlignment.Bottom;
        Panel.SetZIndex(_status, 10);
        root.Children.Add(_status);

        AddButton("WorkbenchArrange", () => { RememberLayout(); _ = LayoutActiveAsync(arrange: true); });
        AddButton("WorkbenchUndo", UndoLayout);
        AddButton("WorkbenchCurrent", () => FitActive());
        AddButton("WorkbenchOverview", () => FitOverview());
        AddButton("WorkbenchNewNote", () => CreatePaper(PaperTypes.Note));
        AddButton("WorkbenchNewTodo", () => CreatePaper(PaperTypes.Todo));
        _search.ToolTip = Strings.Get("WorkbenchSearch");
        _search.TextChanged += (_, _) => { _searchCursor = 0; ApplySearch(); };
        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SearchNext(); e.Handled = true; } };
        _toolbar.Children.Add(_search);
        AddSymbolButton("-", () => ZoomAt(_scale.ScaleX / 1.2, ViewportCenter));
        _toolbar.Children.Add(_zoom);
        AddSymbolButton("+", () => ZoomAt(_scale.ScaleX * 1.2, ViewportCenter));
        AddButton("WorkbenchClose", Close);
        _viewport.PreviewMouseDown += BeginPan;
        _viewport.PreviewMouseMove += MovePan;
        _viewport.PreviewMouseUp += EndPan;
        _viewport.LostMouseCapture += (_, _) => _panPointer = null;
        _viewport.PreviewMouseWheel += (_, e) =>
        {
            // Preserve ordinary wheel scrolling and Ctrl+wheel text zoom inside editors.
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0 && !ReferenceEquals(e.OriginalSource, _viewport)) return;
            ZoomAt(_scale.ScaleX * Math.Pow(1.15, e.Delta / 120.0), e.GetPosition(_viewport));
            e.Handled = true;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled)
            {
                if (_search.Text.Length > 0) _search.Clear();
                else Close();
                e.Handled = true;
            }
            if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            { _search.Focus(); _search.SelectAll(); e.Handled = true; }
        };
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(180) };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); SyncActiveScene(); };
        controller.WorkbenchModelChanged += QueueModelRefresh;
        Loaded += async (_, _) =>
        {
            SyncActiveScene();
            await RefreshOverviewAsync();
        };
        Closing += (_, _) => { if (!_shutdown) CommitPendingTitles(); };
        Closed += (_, _) => DisposePresentation();
        SetHint(); UpdateZoomLabel();
    }

    private void AddButton(string key, Action action) => AddSymbolButton(Strings.Get(key), action);
    private void AddSymbolButton(string text, Action action)
    {
        var button = NewButton(text);
        button.Click += (_, _) => action(); _toolbar.Children.Add(button);
    }
    private static Button NewButton(string text) => new()
    {
        Content = text, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(3),
        Background = CardBrush, Foreground = Ink, BorderBrush = Muted, BorderThickness = new Thickness(.5),
        FocusVisualStyle = null
    };
    private Point ViewportCenter => new(Math.Max(1, _viewport.ActualWidth) / 2, Math.Max(1, _viewport.ActualHeight) / 2);
    private void SetHint() => _status.Text = Strings.Get("WorkbenchHint");
    private void Report(Exception error)
    {
        Trace.TraceError("Workbench: {0}", error);
        _status.Text = Strings.Format("WorkbenchError", error.GetBaseException().Message);
    }
    private void UpdateZoomLabel() => _zoom.Text = $"{_scale.ScaleX:P0}";
    private void ZoomAt(double zoom, Point point)
    {
        zoom = Math.Clamp(zoom, .035, 2.0);
        var x = (point.X - _pan.X) / _scale.ScaleX;
        var y = (point.Y - _pan.Y) / _scale.ScaleY;
        _scale.ScaleX = _scale.ScaleY = zoom;
        _pan.X = point.X - x * zoom; _pan.Y = point.Y - y * zoom;
        UpdateZoomLabel();
    }
    private void Fit(WorkbenchRect bounds, double maxZoom = 1)
    {
        if (_viewport.ActualWidth <= 0 || _viewport.ActualHeight <= 0) return;
        var zoom = Math.Clamp(Math.Min((_viewport.ActualWidth - 80) / Math.Max(1, bounds.Width),
            (_viewport.ActualHeight - 60) / Math.Max(1, bounds.Height)), .035, maxZoom);
        _scale.ScaleX = _scale.ScaleY = zoom;
        _pan.X = ViewportCenter.X - bounds.Center.X * zoom;
        _pan.Y = ViewportCenter.Y - bounds.Center.Y * zoom;
        UpdateZoomLabel();
    }
    private void FitActive() { if (ActiveScene is { } scene) Fit(scene.Bounds.Offset(scene.Origin)); }
    private void FitOverview() => Fit(WorkbenchRect.Union(_scenes.Values.Select(s => s.Bounds.Offset(s.Origin))));
    private void BeginPan(object sender, MouseButtonEventArgs e)
    {
        if (_dragging || Mouse.Captured != null) return;
        if (e.ChangedButton != MouseButton.Middle && !(e.ChangedButton == MouseButton.Left && ReferenceEquals(e.OriginalSource, _viewport))) return;
        _panPointer = e.GetPosition(_viewport); _panStart = new(_pan.X, _pan.Y);
        _viewport.CaptureMouse(); e.Handled = true;
    }
    private void MovePan(object sender, MouseEventArgs e)
    {
        if (_panPointer is not { } start) return;
        var point = e.GetPosition(_viewport); _pan.X = _panStart.X + point.X - start.X; _pan.Y = _panStart.Y + point.Y - start.Y;
        e.Handled = true;
    }
    private void EndPan(object sender, MouseButtonEventArgs e)
    {
        if (_panPointer == null) return;
        _panPointer = null; _viewport.ReleaseMouseCapture(); e.Handled = true;
    }

    internal void QueueModelRefresh(string? focusId)
    {
        if (focusId != null) _focusAfterRefresh = focusId;
        QueueModelRefresh();
        if (focusId != null && !IsActive) Activate();
    }
    private void QueueModelRefresh()
    {
        if (_closed || IsSuspended || _storingLayout) return;
        _refreshTimer.Stop(); _refreshTimer.Start();
    }
    private void SyncActiveScene()
    {
        if (_closed || IsSuspended || _dragging || _controller.IsDataReloading) return;
        try
        {
            var snapshot = _controller.SnapshotCurrentWorkbench();
            var scene = GetScene(snapshot.Number);
            scene.Snapshot = snapshot;
            var ids = snapshot.Papers.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in scene.Cards.Keys.Where(id => !ids.Contains(id)).ToArray()) RemoveCard(scene, id, false);
            foreach (var paper in snapshot.Papers)
            {
                if (!scene.Cards.TryGetValue(paper.Id, out var card))
                {
                    card = CreateActiveCard(paper); scene.Cards.Add(paper.Id, card); scene.Canvas.Children.Add(card.Frame);
                }
                card.Snapshot = paper;
                if (!card.Title.IsKeyboardFocusWithin)
                {
                    card.Title.Text = paper.Title;
                    card.Move.Tag = paper.Title;
                }
                card.Pin.Content = paper.Placement?.Pinned == true ? "\u25c6" : "\u25c7";
                card.Eye.Content = EyeIcon(paper.DesktopVisible);
                card.Eye.ToolTip = Strings.Get(paper.DesktopVisible ? "WorkbenchHideOnDesktop" : "WorkbenchShowOnDesktop");
                card.Move.Cursor = paper.Placement?.Pinned == true ? Cursors.Arrow : Cursors.SizeAll;
                card.Binding.Text = paper.BindingTitle;
                card.Binding.Visibility = paper.BindingTitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            var signature = string.Join("|", snapshot.Nodes.Select(n => $"{n.Id}:{n.Width}:{n.Height}:{n.Affinity}:{n.Kind}:{n.Pinned}:{n.Previous}"))
                + string.Join("|", snapshot.Edges.Select(e => $"{e.From}>{e.To}"));
            if (signature != _modelSignature || scene.Layout == null)
            { _modelSignature = signature; _ = LayoutActiveAsync(); }
            else FocusRequestedPaper();
            ApplySearch();
        }
        catch (Exception ex) { Report(ex); }
    }

    private Scene GetScene(int number)
    {
        if (_scenes.TryGetValue(number, out var scene)) return scene;
        scene = new Scene(number); _scenes.Add(number, scene); _world.Children.Add(scene.Canvas);
        return scene;
    }

    private async Task RefreshOverviewAsync()
    {
        _overviewCancellation?.Cancel(); _overviewCancellation?.Dispose();
        var cancellation = _overviewCancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var generation = _controller.WorkspaceGeneration;
        try
        {
            var snapshots = await _controller.ReadOtherWorkbenchWorkspacesAsync(token);
            foreach (var snapshot in snapshots)
            {
                var layout = await Task.Run(() => WorkbenchLayoutEngine.Calculate(snapshot.Nodes, snapshot.Edges,
                    cancellationToken: token), token);
                if (_closed || IsSuspended || token.IsCancellationRequested || generation != _controller.WorkspaceGeneration) return;
                var scene = GetScene(snapshot.Number);
                scene.Snapshot = snapshot; scene.Layout = layout;
                foreach (var id in scene.Cards.Keys.ToArray()) RemoveCard(scene, id, false);
                foreach (var paper in snapshot.Papers)
                {
                    var preview = CreatePreview(paper, snapshot.Number);
                    scene.Cards.Add(paper.Id, preview); scene.Canvas.Children.Add(preview.Frame);
                    Position(preview, layout.Cards[paper.Id]);
                }
                DrawDecorations(scene);
            }
            var validNumbers = snapshots.Select(s => s.Number).Append(_controller.WorkspaceNumber).ToHashSet();
            foreach (var number in _scenes.Keys.Where(n => !validNumbers.Contains(n)).ToArray())
            {
                var old = _scenes[number]; foreach (var id in old.Cards.Keys.ToArray()) RemoveCard(old, id, false);
                _world.Children.Remove(old.Canvas); _scenes.Remove(number);
            }
            if (_closed || IsSuspended || token.IsCancellationRequested || generation != _controller.WorkspaceGeneration) return;
            var oldOrigin = ActiveScene?.Origin;
            ArrangeWorkspaces();
            if (oldOrigin is { } previous && ActiveScene is { } active)
            {
                _pan.X += (previous.X - active.Origin.X) * _scale.ScaleX;
                _pan.Y += (previous.Y - active.Origin.Y) * _scale.ScaleY;
            }
            ApplySearch();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) Report(ex); }
    }

    private async Task LayoutActiveAsync(bool arrange = false, string? preferredId = null, HashSet<string>? scope = null)
    {
        if (_closed || IsSuspended || _dragging) return;
        CancelLayout();
        var revision = _layoutRevision;
        var cancellation = _layoutCancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var generation = _controller.WorkspaceGeneration;
        var snapshot = _controller.SnapshotCurrentWorkbench();
        try
        {
            var nodes = snapshot.Nodes;
            if (scope != null) nodes = nodes.Select(n => !scope.Contains(n.Id) && n.Previous != null ? n with { Pinned = true } : n).ToArray();
            var result = await Task.Run(() => WorkbenchLayoutEngine.Calculate(nodes, snapshot.Edges, arrange,
                preferredId, token), token);
            if (_closed || IsSuspended || _dragging || revision != _layoutRevision || generation != _controller.WorkspaceGeneration || token.IsCancellationRequested) return;
            var scene = ActiveScene;
            if (scene == null || !result.Cards.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(scene.Cards.Keys)) return;
            var firstLayout = scene.Layout == null;
            var oldOrigin = scene.Origin;
            var state = _controller.WorkbenchLayout.Copy();
            foreach (var pair in result.Cards)
            {
                if (!state.Cards.TryGetValue(pair.Key, out var placement)) state.Cards[pair.Key] = placement = new();
                placement.X = pair.Value.X; placement.Y = pair.Value.Y;
                placement.Width = pair.Value.Width; placement.Height = pair.Value.Height;
                Position(scene.Cards[pair.Key], pair.Value);
            }
            scene.Layout = result; scene.Snapshot = snapshot;
            StoreLayout(state);
            _modelSignature = "";
            DrawDecorations(scene); ArrangeWorkspaces();
            // Keep the active desk under the camera when another region changes its envelope.
            _pan.X += (oldOrigin.X - scene.Origin.X) * _scale.ScaleX;
            _pan.Y += (oldOrigin.Y - scene.Origin.Y) * _scale.ScaleY;
            if (firstLayout || arrange && scope == null) FitActive();
            if (result.LockedOverlaps.Count > 0) _status.Text = Strings.Get("WorkbenchPinConflict");
            else SetHint();
            FocusRequestedPaper(); ApplySearch();
            // Cache the applied geometry, otherwise every ordinary content edit would relayout.
            var applied = _controller.SnapshotCurrentWorkbench();
            _modelSignature = string.Join("|", applied.Nodes.Select(n => $"{n.Id}:{n.Width}:{n.Height}:{n.Affinity}:{n.Kind}:{n.Pinned}:{n.Previous}"))
                + string.Join("|", applied.Edges.Select(e => $"{e.From}>{e.To}"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && revision == _layoutRevision) Report(ex); }
    }

    private void StoreLayout(WorkbenchState state)
    {
        _storingLayout = true;
        try { _controller.StoreWorkbenchLayout(state); }
        finally { _storingLayout = false; }
    }
    private void CancelLayout() { _layoutRevision++; _layoutCancellation?.Cancel(); _layoutCancellation?.Dispose(); _layoutCancellation = null; }
    private void RememberLayout()
    {
        CancelLayout();
        if (_undo.Count >= 32)
        {
            var newest = _undo.Take(31).Reverse().ToArray(); _undo.Clear(); foreach (var item in newest) _undo.Push(item);
        }
        _undo.Push(_controller.WorkbenchLayout.Copy());
    }
    private void UndoLayout()
    {
        if (_dragging || _undo.Count == 0 || IsSuspended) return;
        CancelLayout(); StoreLayout(_undo.Pop());
        _modelSignature = ""; SyncActiveScene();
    }
    private void CreatePaper(string type)
    {
        if (IsSuspended) return;
        RememberLayout();
        _controller.CreatePaper(type, show: false);
        SyncActiveScene();
    }
    private void FocusRequestedPaper()
    {
        if (_focusAfterRefresh is not { } id || ActiveScene is not { } scene || !scene.Cards.TryGetValue(id, out var card)) return;
        Fit(card.Bounds.Inflate(64).Offset(scene.Origin), 1);
        _focusAfterRefresh = null;
    }
    private static bool SearchMatches(WorkbenchPaperSnapshot paper, string query) =>
        paper.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        paper.Summary.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        paper.BindingTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private void ApplySearch()
    {
        var query = _search.Text.Trim();
        foreach (var scene in _scenes.Values)
        foreach (var card in scene.Cards.Values)
            card.Frame.Opacity = query.Length == 0 || SearchMatches(card.Snapshot, query) ? 1 : .3;
    }

    private void SearchNext()
    {
        var query = _search.Text.Trim();
        if (query.Length == 0) return;
        var matches = _scenes.Values.OrderBy(s => s.Number)
            .SelectMany(scene => scene.Cards.Values.Where(c => SearchMatches(c.Snapshot, query))
                .Select(card => (Scene: scene, Card: card))).ToArray();
        if (matches.Length == 0) { _status.Text = Strings.Get("WorkbenchSearchNoResults"); return; }
        var found = matches[_searchCursor++ % matches.Length];
        Fit(found.Card.Bounds.Inflate(64).Offset(found.Scene.Origin));
        _status.Text = Strings.Format("WorkbenchSearchResult", (_searchCursor - 1) % matches.Length + 1, matches.Length);
    }

    private Card CreateActiveCard(WorkbenchPaperSnapshot paper)
    {
        var card = CreateCardShell(paper);
        var owner = _controller.WorkbenchPaperWindow(paper.Id);
        card.Lease = owner.BorrowWorkbenchBody(this, card.Body);
        // The entire title area is a direct drag surface. Only a double-click changes it
        // into a text editor; the borrowed paper body keeps all of its own mouse behavior.
        card.Move.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2) return;
            e.Handled = true;
            card.Move.Visibility = Visibility.Collapsed;
            card.Title.Visibility = Visibility.Visible;
            card.Title.Focus();
            card.Title.SelectAll();
        };
        card.Title.LostKeyboardFocus += (_, _) =>
        {
            if (card.Title.Visibility != Visibility.Visible) return;
            if (!_closed && !IsSuspended && _controller.FindPaper(paper.Id) is { } live)
            {
                _controller.UpdatePaperTitle(live, card.Title.Text);
                card.Title.Text = live.Title;
            }
            card.Move.Tag = card.Title.Text;
            card.Title.Visibility = Visibility.Collapsed;
            card.Move.Visibility = Visibility.Visible;
        };
        card.Title.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (_controller.FindPaper(paper.Id) is { } live) card.Title.Text = live.Title;
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter) { Keyboard.ClearFocus(); e.Handled = true; }
        };
        card.Pin.Click += (_, _) =>
        {
            RememberLayout(); var state = _controller.WorkbenchLayout.Copy();
            if (state.Cards.TryGetValue(paper.Id, out var placement)) placement.Pinned = !placement.Pinned;
            StoreLayout(state); SyncActiveScene();
        };
        card.Eye.Click += (_, _) =>
        {
            if (_controller.FindPaper(paper.Id) is not { } current) return;
            _controller.SetWorkbenchDesktopVisibility(current, !current.IsVisible);
            card.Eye.Content = EyeIcon(current.IsVisible);
            card.Eye.ToolTip = Strings.Get(current.IsVisible ? "WorkbenchHideOnDesktop" : "WorkbenchShowOnDesktop");
        };
        card.Menu.Click += (_, _) =>
        {
            var menu = owner.CreateWorkbenchBodyMenu();
            var delete = new MenuItem { Header = Strings.Get("WorkbenchDelete") };
            delete.Click += (_, _) =>
            {
                if (MessageBox.Show(this, Strings.Get("WorkbenchDeleteConfirm"), Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                    _controller.DeletePaper(_controller.FindPaper(paper.Id)!);
            };
            menu.Items.Add(delete); menu.PlacementTarget = card.Menu; menu.IsOpen = true;
        };
        WireDrag(card, card.Move, resize: false);
        WireDrag(card, card.Resize, resize: true);
        return card;
    }

    private Card CreatePreview(WorkbenchPaperSnapshot paper, int workspace)
    {
        var card = CreateCardShell(paper);
        card.Title.IsReadOnly = true;
        card.Pin.Visibility = card.Menu.Visibility = card.Resize.Visibility = card.Move.Visibility = Visibility.Collapsed;
        card.Eye.IsEnabled = false;
        var preview = new StackPanel { Margin = new Thickness(16) };
        preview.Children.Add(new TextBlock { Text = paper.Summary, TextWrapping = TextWrapping.Wrap, MaxHeight = Math.Max(80, paper.Height - 120), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ink });
        var open = NewButton(Strings.Get("WorkbenchOpenWorkspace"));
        open.Click += async (_, _) => await ActivateWorkspaceAsync(workspace, paper.Id);
        preview.Children.Add(open); card.Body.Child = preview;
        return card;
    }

    private Card CreateCardShell(WorkbenchPaperSnapshot paper)
    {
        var card = new Card(paper);
        card.Frame.Background = CardBrush; card.Frame.BorderBrush = Muted;
        card.Frame.BorderThickness = new Thickness(1); card.Frame.CornerRadius = new CornerRadius(9);
        card.Frame.ClipToBounds = true;
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = new GridLength(12) });
        card.Frame.Child = grid;
        var header = new Grid { Margin = new Thickness(6, 4, 6, 2) };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        card.Move.Cursor = Cursors.SizeAll;
        card.Move.Background = Brushes.Transparent;
        card.Move.MinHeight = 32;
        card.Move.Tag = paper.Title;
        card.Move.Template = TitleDragTemplate(card.Move);
        header.Children.Add(card.Move);
        card.Title.Text = paper.Title; card.Title.FontWeight = FontWeights.SemiBold;
        card.Title.Background = Brushes.Transparent; card.Title.Foreground = Ink; card.Title.BorderThickness = new Thickness(0);
        card.Title.Padding = new Thickness(12, 5, 4, 5);
        card.Title.Visibility = Visibility.Collapsed;
        Grid.SetColumn(card.Title, 0); header.Children.Add(card.Title);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { card.Pin, card.Eye, card.Menu }) { button.Padding = new Thickness(5, 3, 5, 3); button.Margin = new Thickness(1); button.Background = Brushes.Transparent; button.Foreground = Ink; button.BorderThickness = new Thickness(0); actions.Children.Add(button); }
        card.Pin.Content = paper.Placement?.Pinned == true ? "\u25c6" : "\u25c7";
        card.Pin.ToolTip = Strings.Get("WorkbenchPin");
        card.Eye.Content = EyeIcon(paper.DesktopVisible);
        card.Eye.ToolTip = Strings.Get(paper.DesktopVisible ? "WorkbenchHideOnDesktop" : "WorkbenchShowOnDesktop");
        card.Menu.Content = "\u22ef";
        Grid.SetColumn(actions, 1); header.Children.Add(actions); grid.Children.Add(header);
        card.Binding.Text = paper.BindingTitle; card.Binding.Foreground = Muted; card.Binding.FontSize = 11;
        card.Binding.Margin = new Thickness(14, 0, 14, 4); card.Binding.TextTrimming = TextTrimming.CharacterEllipsis;
        card.Binding.Visibility = paper.BindingTitle.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(card.Binding, 1); grid.Children.Add(card.Binding);
        Grid.SetRow(card.Body, 2); grid.Children.Add(card.Body);
        card.Resize.Width = 22; card.Resize.HorizontalAlignment = HorizontalAlignment.Right; card.Resize.Cursor = Cursors.SizeNWSE;
        card.Resize.Template = ThumbTemplate("\u25e2"); Grid.SetRow(card.Resize, 3); grid.Children.Add(card.Resize);
        Panel.SetZIndex(card.Frame, 2);
        Position(card, paper.Placement?.Bounds ?? new(32, 32, paper.Width, paper.Height));
        return card;
    }
    private static FrameworkElement EyeIcon(bool shown)
    {
        var visual = new Grid { Width = 18, Height = 14 };
        visual.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M1,7 C4,2 14,2 17,7 C14,12 4,12 1,7 Z"),
            Stroke = Ink, StrokeThickness = 1.35, Fill = Brushes.Transparent
        });
        visual.Children.Add(new Ellipse
        {
            Width = 4.5, Height = 4.5, Fill = Ink,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (!shown)
            visual.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M2,13 L16,1"),
                Stroke = Ink, StrokeThickness = 2
            });
        return visual;
    }

    private static ControlTemplate TitleDragTemplate(Thumb owner)
    {
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(FrameworkElement.Tag))
            { Source = owner });
        label.SetValue(TextBlock.ForegroundProperty, Ink);
        label.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        label.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        label.SetValue(TextBlock.MarginProperty, new Thickness(12, 4, 4, 4));
        surface.AppendChild(label);
        return new ControlTemplate(typeof(Thumb)) { VisualTree = surface };
    }

    private static ControlTemplate ThumbTemplate(string glyph)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextProperty, glyph); text.SetValue(TextBlock.ForegroundProperty, Muted);
        text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        return new ControlTemplate(typeof(Thumb)) { VisualTree = text };
    }
    private static void Position(Card card, WorkbenchRect bounds)
    {
        card.Bounds = bounds; Canvas.SetLeft(card.Frame, bounds.X); Canvas.SetTop(card.Frame, bounds.Y);
        card.Frame.Width = bounds.Width; card.Frame.Height = bounds.Height;
    }
    private void WireDrag(Card card, Thumb thumb, bool resize)
    {
        WorkbenchRect before = default;
        thumb.DragStarted += (_, _) =>
        {
            if (IsSuspended || (!resize && card.Snapshot.Placement?.Pinned == true)) return;
            RememberLayout(); _dragging = true; before = card.Bounds;
        };
        thumb.DragDelta += (_, e) =>
        {
            if (!_dragging) return;
            // Thumb deltas are in the thumb's local (already transformed) coordinate space.
            var r = card.Bounds;
            r = resize ? r with { Width = Math.Clamp(r.Width + e.HorizontalChange, 240, 1600), Height = Math.Clamp(r.Height + e.VerticalChange, 240, 1600) }
                : r with { X = Math.Clamp(r.X + e.HorizontalChange, -10_000_000, 10_000_000), Y = Math.Clamp(r.Y + e.VerticalChange, -10_000_000, 10_000_000) };
            Position(card, r);
            if (ActiveScene is { } scene) DrawDecorations(scene, dragging: true);
        };
        thumb.DragCompleted += (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            if (e.Canceled) { Position(card, before); if (_undo.Count > 0) _undo.Pop(); if (ActiveScene is { } canceled) DrawDecorations(canceled); return; }
            var state = _controller.WorkbenchLayout.Copy();
            if (!state.Cards.TryGetValue(card.Snapshot.Id, out var placement)) state.Cards[card.Snapshot.Id] = placement = new();
            placement.X = card.Bounds.X; placement.Y = card.Bounds.Y; placement.Width = card.Bounds.Width; placement.Height = card.Bounds.Height;
            placement.Preferred = true;
            StoreLayout(state); _ = LayoutActiveAsync(preferredId: card.Snapshot.Id);
        };
    }

    private void DrawDecorations(Scene scene, bool dragging = false)
    {
        foreach (var element in scene.Decorations) scene.Canvas.Children.Remove(element);
        scene.Decorations.Clear();
        void Add(UIElement element, double x, double y)
        { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); Panel.SetZIndex(element, 0); scene.Canvas.Children.Add(element); scene.Decorations.Add(element); }
        var bounds = scene.Bounds;
        var frame = new Rectangle { Width = bounds.Width, Height = bounds.Height, RadiusX = 22, RadiusY = 22,
            Stroke = Muted, StrokeThickness = 1.5, Fill = Brushes.Transparent, IsHitTestVisible = false, Opacity = .3 };
        Add(frame, bounds.X, bounds.Y);
        var title = NewButton(Strings.Format("WorkbenchWorkspace", scene.Number));
        title.FontSize = 22; title.FontWeight = FontWeights.SemiBold;
        title.Click += async (_, _) => { if (scene.Number == _controller.WorkspaceNumber) FitActive(); else await ActivateWorkspaceAsync(scene.Number, null); };
        Add(title, bounds.X + 14, bounds.Y - 50);
        if (scene.Snapshot?.Error is { } error)
        { Add(new TextBlock { Text = Strings.Format("WorkbenchReadError", error), Width = 560, TextWrapping = TextWrapping.Wrap, Foreground = Ink, Margin = new Thickness(20) }, bounds.X, bounds.Y); return; }
        if (scene.Layout is not { } layout) return;
        foreach (var section in layout.Sections)
        {
            var sectionBounds = dragging ? WorkbenchRect.Union(section.Members.Where(scene.Cards.ContainsKey).Select(id => scene.Cards[id].Bounds)).Inflate(32) : section.Bounds;
            Add(new Rectangle { Width = sectionBounds.Width, Height = sectionBounds.Height, RadiusX = 12, RadiusY = 12,
                Stroke = Accent, StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 6, 6 }, Opacity = .45,
                IsHitTestVisible = false }, sectionBounds.X, sectionBounds.Y);
        }
        foreach (var route in layout.Routes)
        {
            var points = dragging && scene.Cards.TryGetValue(route.From, out var from) && scene.Cards.TryGetValue(route.To, out var to)
                ? new[] { from.Bounds.Center, to.Bounds.Center } : route.Points;
            var line = new Polyline { Stroke = Accent, StrokeThickness = 2, Opacity = .65,
                Points = new PointCollection(points.Select(p => new Point(p.X, p.Y))), IsHitTestVisible = false };
            Add(line, 0, 0);

        }
        if (scene.Cards.Count == 0)
            Add(new TextBlock { Text = Strings.Get("WorkbenchEmpty"), FontSize = 18, Foreground = Muted, Margin = new Thickness(32) }, bounds.X, bounds.Y);
    }

    private void ArrangeWorkspaces()
    {
        var ordered = _scenes.Values.OrderBy(s => s.Number).ToArray();
        var x = 0.0; var y = 0.0; var rowHeight = 0.0; var column = 0;
        foreach (var scene in ordered)
        {
            var bounds = scene.Bounds;
            scene.Origin = new(x - bounds.X, y - bounds.Y);
            Canvas.SetLeft(scene.Canvas, scene.Origin.X); Canvas.SetTop(scene.Canvas, scene.Origin.Y);
            x += bounds.Width + 240; rowHeight = Math.Max(rowHeight, bounds.Height);
            if (++column == 2) { column = 0; x = 0; y += rowHeight + 280; rowHeight = 0; }
        }
    }
    private async Task ActivateWorkspaceAsync(int number, string? paperId)
    {
        if (_closed || IsSuspended || _dragging) return;
        _focusAfterRefresh = paperId;
        await _controller.SwitchWorkspaceAsync(number);
        if (!_closed && number == _controller.WorkspaceNumber) FocusRequestedPaper();
    }
    internal void SuspendWorkspace()
    {
        if (_closed || IsSuspended) return;
        IsSuspended = true; _toolbar.IsEnabled = false; _viewport.IsEnabled = false;
        CancelLayout(); _overviewCancellation?.Cancel(); _refreshTimer.Stop();
        foreach (var scene in _scenes.Values)
            foreach (var id in scene.Cards.Keys.ToArray()) RemoveCard(scene, id, false);
        _world.Children.Clear(); _scenes.Clear(); _modelSignature = ""; _undo.Clear();
    }
    internal void ResumeWorkspace()
    {
        if (_closed || !IsSuspended) return;
        IsSuspended = false; _toolbar.IsEnabled = true; _viewport.IsEnabled = true;
        SyncActiveScene(); _ = RefreshOverviewAsync();
    }
    internal void CommitPendingTitles()
    {
        if (_closed || IsSuspended || ActiveScene is not { } scene) return;
        foreach (var card in scene.Cards.Values)
            if (card.Title.IsKeyboardFocusWithin && _controller.FindPaper(card.Snapshot.Id) is { } paper)
                _controller.UpdatePaperTitle(paper, card.Title.Text);
    }
    internal void CloseForShutdown() { _shutdown = true; Close(); }
    private static void RemoveCard(Scene scene, string id, bool restoreDesktop)
    {
        if (!scene.Cards.Remove(id, out var card)) return;
        card.Lease?.Return(restoreDesktop); card.Lease = null; scene.Canvas.Children.Remove(card.Frame);
    }
    private void DisposePresentation()
    {
        if (_closed) return;
        _closed = true; _controller.WorkbenchModelChanged -= QueueModelRefresh;
        _refreshTimer.Stop(); CancelLayout(); _overviewCancellation?.Cancel(); _overviewCancellation?.Dispose();
        foreach (var scene in _scenes.Values)
            foreach (var id in scene.Cards.Keys.ToArray()) RemoveCard(scene, id, !_shutdown);
        _scenes.Clear(); _world.Children.Clear();
    }
    private sealed class Scene(int number)
    {
        internal int Number { get; } = number;
        internal Canvas Canvas { get; } = new();
        internal Dictionary<string, Card> Cards { get; } = new(StringComparer.Ordinal);
        internal List<UIElement> Decorations { get; } = new();
        internal WorkbenchWorkspaceSnapshot? Snapshot;
        internal WorkbenchLayoutResult? Layout;
        internal WorkbenchPoint Origin;
        internal WorkbenchRect Bounds => (Layout?.Bounds ?? WorkbenchRect.Union(Cards.Values.Select(c => c.Bounds)).Inflate(56));
    }
    private sealed class Card(WorkbenchPaperSnapshot paper)
    {
        internal WorkbenchPaperSnapshot Snapshot = paper;
        internal Border Frame { get; } = new();
        internal TextBox Title { get; } = new();
        internal TextBlock Binding { get; } = new();
        internal Border Body { get; } = new() { ClipToBounds = true };
        internal Thumb Move { get; } = new();
        internal Thumb Resize { get; } = new();
        internal Button Pin { get; } = new();
        internal Button Eye { get; } = new();
        internal Button Menu { get; } = new();
        internal PaperWindow.WorkbenchBodyLease? Lease;
        internal WorkbenchRect Bounds;
    }
}
