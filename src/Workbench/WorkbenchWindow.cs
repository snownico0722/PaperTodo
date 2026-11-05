using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>A presentation owner for the continuous desk, not an alternative content store.</summary>
internal sealed partial class WorkbenchWindow : Window
{
    private readonly AppController _controller;
    private readonly Grid _viewport = new() { ClipToBounds = true, Focusable = true };
    private readonly Canvas _world = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _pan = new();
    private readonly TextBox _search = new() { Width = 220, Margin = new Thickness(8, 4, 8, 4), Padding = new Thickness(8, 4, 8, 4) };
    private readonly TextBlock _status = new() { Margin = new Thickness(20, 6, 20, 8), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _zoom = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 48, TextAlignment = TextAlignment.Center };
    private readonly WorkbenchToolbarPanel _toolbar = new();
    private readonly WrapPanel _layoutCommands = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly WrapPanel _rightCommands = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Border _toolbarHost = new();
    private readonly List<Button> _chromeButtons = new();
    private readonly Dictionary<int, Scene> _scenes = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly Stack<WorkbenchUndoEntry> _undo = new();
    private readonly Stack<WorkbenchUndoEntry> _redo = new();
    private sealed record WorkbenchUndoEntry(WorkbenchState? Layout, Guid? TransferId = null);
    private CancellationTokenSource? _layoutCancellation;
    private CancellationTokenSource? _overviewCancellation;
    private long _layoutRevision;
    private string _modelSignature = "";
    private bool _refreshPendingAfterDrag;
    private string? _focusAfterRefresh;
    private int _searchCursor;
    private Point? _panPointer;
    private Point _panStart;
    private Camera? _cameraBeforePaperFocus;
    private string? _focusedPaperId;
    private readonly record struct Camera(double Zoom, double X, double Y);
    private bool _closed, _shutdown, _dragging, _storingLayout;
    internal bool IsSuspended { get; private set; }
    internal int HostedPaperCount => _scenes.Values.Sum(s => s.Cards.Count(c => c.Value.Lease != null));
    private Scene? ActiveScene => _scenes.GetValueOrDefault(_controller.WorkspaceNumber);
    // Use PaperTodo's own palette (including the selected color scheme), not fixed blue/grey.
    private static Brush DeskBrush => Theme.WorkbenchDeskBrush;
    private static Brush CanvasBrush => Theme.WorkbenchCanvasBrush;
    private static Brush FrameBrush => Theme.WorkbenchFrameBrush;
    private static Brush RegionBrush => Theme.WorkbenchRegionBrush;
    private static Brush RegionFill => Theme.WorkbenchRegionFillBrush;
    private static Brush CardBrush => Theme.PaperBrush;
    private static Brush Outline => Theme.PaperBorderBrush;
    private static Brush Ink => Theme.TextBrush;
    private static Brush Muted => Theme.WeakTextBrush;
    private static Brush Accent => Theme.ActiveBrush;

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

        // Keep search centered on wide windows. The toolbar measures localized commands
        // before choosing additional rows, so narrow windows never cover their buttons.
        _toolbarHost.CornerRadius = new CornerRadius(10);
        _toolbarHost.BorderThickness = new Thickness(1);
        _toolbarHost.Padding = new Thickness(8, 4, 8, 4);
        _toolbarHost.Margin = new Thickness(16, 14, 16, 0);
        _toolbarHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        _toolbarHost.VerticalAlignment = VerticalAlignment.Top;
        _toolbar.Children.Add(_layoutCommands);
        _toolbar.Children.Add(_search);
        _toolbar.Children.Add(_rightCommands);
        _toolbarHost.Child = _toolbar;
        Panel.SetZIndex(_toolbarHost, 10);
        root.Children.Add(_toolbarHost);
        _status.VerticalAlignment = VerticalAlignment.Bottom;
        Panel.SetZIndex(_status, 10);
        root.Children.Add(_status);

        AddButton("WorkbenchArrange", () => { RememberLayout(); _ = LayoutActiveAsync(arrange: true); }, _layoutCommands);
        AddButton("WorkbenchUndo", UndoWorkspaceAction, _layoutCommands);
        _search.ToolTip = Strings.Get("WorkbenchSearch");
        _search.Height = AppTypography.FitChrome(28);
        _search.FontSize = AppTypography.Scale(13);
        _search.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) => { _searchCursor = 0; ApplySearch(); };
        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SearchNext(); e.Handled = true; } };
        AddButton("WorkbenchNewNote", () => CreatePaper(PaperTypes.Note), _rightCommands);
        AddButton("WorkbenchNewTodo", () => CreatePaper(PaperTypes.Todo), _rightCommands);
        InitializeWorkspaceTools();
        AddSymbolButton("-", () => ZoomAt(_scale.ScaleX / 1.2, ViewportCenter), _rightCommands);
        _rightCommands.Children.Add(_zoom);
        AddSymbolButton("+", () => ZoomAt(_scale.ScaleX * 1.2, ViewportCenter), _rightCommands);
        AddButton("WorkbenchClose", Close, _rightCommands);
        RefreshTheme();
        _viewport.PreviewMouseDown += BeginPan;
        _viewport.PreviewMouseMove += MovePan;
        _viewport.PreviewMouseUp += EndPan;
        _viewport.LostMouseCapture += (_, _) => { _panPointer = null; CancelMarquee(); };
        _viewport.PreviewMouseWheel += (_, e) =>
        {
            // Preserve ordinary wheel scrolling and Ctrl+wheel text zoom inside editors.
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0 && !ReferenceEquals(e.OriginalSource, _viewport)) return;
            ZoomAt(_scale.ScaleX * Math.Pow(1.15, e.Delta / 120.0), e.GetPosition(_viewport));
            e.Handled = true;
        };
        // Capture before the borrowed editor handles the keystroke. Both Ctrl+F
        // and the existing Ctrl+Shift+F navigate the single workbench search.
        PreviewKeyDown += (_, e) =>
        {
            if (!e.Handled && TryFocusSearch(e.Key, Keyboard.Modifiers)) e.Handled = true;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled)
            {
                if (CancelWorkspaceGesture()) { }
                else if (_search.Text.Length > 0) _search.Clear();
                else if (_focusedGroupId != null) LeaveGroupFocus();
                else Close();
                e.Handled = true;
            }
        };
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(180) };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); SyncActiveScene(); };
        controller.WorkbenchModelChanged += QueueModelRefresh;
        controller.WorkbenchContentTransferReplayed += OnContentTransferReplayed;
        controller.WorkbenchContentTransferBlocked += OnTransferHistoryBlocked;
        controller.WorkbenchContentTransfersRetired += OnContentTransfersRetired;
        Loaded += async (_, _) =>
        {
            SyncActiveScene();
            await RefreshOverviewAsync();
        };
        Closing += (_, _) => { if (!_shutdown) CommitPendingTitles(); };
        Closed += (_, _) => DisposePresentation();
        SetHint(); UpdateZoomLabel();
    }

    private bool TryFocusSearch(Key key, ModifierKeys modifiers)
    {
        if (_closed || IsSuspended || key != Key.F ||
            (modifiers != ModifierKeys.Control &&
             modifiers != (ModifierKeys.Control | ModifierKeys.Shift)))
            return false;
        _search.Focus();
        _search.SelectAll();
        return true;
    }

    private void AddButton(string key, Action action, Panel destination) => AddSymbolButton(Strings.Get(key), action, destination);
    private void AddSymbolButton(string text, Action action, Panel destination)
    {
        var button = NewButton(text);
        button.Click += (_, _) => action();
        destination.Children.Add(button);
        _chromeButtons.Add(button);
    }
    private static Button NewButton(string text) => new()
    {
        Content = text, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(3),
        Background = CardBrush, Foreground = Ink, BorderBrush = Outline, BorderThickness = new Thickness(.5),
        FocusVisualStyle = null
    };
    internal void RefreshTheme()
    {
        if (_closed) return;
        Background = DeskBrush; Foreground = Ink;
        _viewport.Background = DeskBrush;
        _toolbarHost.Background = CardBrush;
        _toolbarHost.BorderBrush = Outline;
        _search.Background = Brushes.Transparent;
        _search.Foreground = Ink;
        _search.BorderBrush = Outline;
        _search.BorderThickness = new Thickness(1);
        _search.CaretBrush = Ink;
        _search.SelectionBrush = Theme.Tint(100);
        _search.Style = WorkbenchInputStyle();
        _zoom.Foreground = Ink;
        _status.Foreground = Muted;
        foreach (var button in _chromeButtons)
        {
            button.Background = CardBrush;
            button.Foreground = Ink;
            button.BorderBrush = Outline;
        }
        foreach (var scene in _scenes.Values)
        {
            foreach (var card in scene.Cards.Values)
            {
                card.Frame.Background = CardBrush;
                card.Frame.BorderBrush = _selectedCards.Contains(card.Snapshot.Id) ? Accent : Outline;
                card.Header.Background = WorkspaceMaterials.IsMaterialId(card.Snapshot.Id) ? Brushes.Transparent : Theme.TitleBarBrush(opaque: true);
                card.Title.Background = Brushes.Transparent;
                card.Title.Foreground = Ink;
                card.Title.BorderBrush = Outline;
                card.Title.Style = WorkbenchInputStyle();
                card.Binding.Foreground = Muted;
                card.Move.Foreground = Ink;
                card.Resize.Foreground = Muted;
                foreach (var button in new[] { card.Pin, card.Menu })
                    button.Foreground = Ink;
                card.Eye.Child = EyeIcon(card.Snapshot.DesktopVisible);
                if (card.PreviewText != null) card.PreviewText.Foreground = Ink;
                if (card.MaterialEditor != null)
                { card.MaterialEditor.Foreground = Ink; card.MaterialEditor.CaretBrush = Ink; }
                if (card.MaterialOpen != null)
                { card.MaterialOpen.Background = CardBrush; card.MaterialOpen.Foreground = Ink; card.MaterialOpen.BorderBrush = Outline; }
            }
            DrawDecorations(scene);
        }
    }

    // Match the rounded external-open extension editor in PaperTodo settings.
    private static Style WorkbenchInputStyle()
    {
        var style = new Style(typeof(TextBox));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
        style.Setters.Add(new Setter(FrameworkElement.UseLayoutRoundingProperty, true));
        var frame = new FrameworkElementFactory(typeof(Border), "Bd");
        frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        frame.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        frame.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        frame.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        frame.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        var content = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        content.SetValue(FrameworkElement.VerticalAlignmentProperty,
            new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        frame.AppendChild(content);
        var template = new ControlTemplate(typeof(TextBox)) { VisualTree = frame };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, Muted, "Bd"));
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "Bd"));
        template.Triggers.Add(hover);
        template.Triggers.Add(focus);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private Point ViewportCenter => new(Math.Max(1, _viewport.ActualWidth) / 2, Math.Max(1, _viewport.ActualHeight) / 2);
    private void SetHint() => _status.Text = Strings.Get("WorkbenchHint");
    private void Report(Exception error)
    {
        Trace.TraceError("Workbench: {0}", error);
        _status.Text = Strings.Format("WorkbenchError", error.GetBaseException().Message);
    }
    private void UpdateZoomLabel() => _zoom.Text = $"{_scale.ScaleX:P0}";
    private void StopCameraAnimation()
    {
        // Keep the visible interpolated camera when a wheel, pan or new focus interrupts
        // the short transition; clearing animation clocks alone would snap to the target.
        var current = new Camera(_scale.ScaleX, _pan.X, _pan.Y);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _pan.BeginAnimation(TranslateTransform.XProperty, null);
        _pan.BeginAnimation(TranslateTransform.YProperty, null);
        _scale.ScaleX = _scale.ScaleY = current.Zoom;
        _pan.X = current.X; _pan.Y = current.Y;
    }

    private void SetCamera(Camera target, bool animate)
    {
        var from = new Camera(_scale.ScaleX, _pan.X, _pan.Y);
        StopCameraAnimation();
        // Stop fill behavior leaves the target as the real base value. No extra timers
        // or completion state are required, even if another gesture interrupts it.
        _scale.ScaleX = _scale.ScaleY = target.Zoom;
        _pan.X = target.X; _pan.Y = target.Y;
        if (animate && _controller.State.EnableAnimations)
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            DoubleAnimation Step(double initial, double final) =>
                new(initial, final, TimeSpan.FromMilliseconds(180))
                { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, Step(from.Zoom, target.Zoom));
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, Step(from.Zoom, target.Zoom));
            _pan.BeginAnimation(TranslateTransform.XProperty, Step(from.X, target.X));
            _pan.BeginAnimation(TranslateTransform.YProperty, Step(from.Y, target.Y));
        }
        _zoom.Text = $"{target.Zoom:P0}";
    }

    private void ZoomAt(double zoom, Point point)
    {
        StopCameraAnimation();
        zoom = Math.Clamp(zoom, .035, 2.0);
        var x = (point.X - _pan.X) / _scale.ScaleX;
        var y = (point.Y - _pan.Y) / _scale.ScaleY;
        _scale.ScaleX = _scale.ScaleY = zoom;
        _pan.X = point.X - x * zoom; _pan.Y = point.Y - y * zoom;
        UpdateZoomLabel();
    }
    private void Fit(WorkbenchRect bounds, double maxZoom = 1, bool animate = false)
    {
        if (_viewport.ActualWidth <= 0 || _viewport.ActualHeight <= 0) return;
        var zoom = Math.Clamp(Math.Min((_viewport.ActualWidth - 80) / Math.Max(1, bounds.Width),
            (_viewport.ActualHeight - 60) / Math.Max(1, bounds.Height)), .035, maxZoom);
        SetCamera(new Camera(zoom, ViewportCenter.X - bounds.Center.X * zoom,
            ViewportCenter.Y - bounds.Center.Y * zoom), animate);
    }
    private void FocusPaper(Card card)
    {
        if (_closed || IsSuspended || _dragging || ActiveScene is not { } scene ||
            !scene.Cards.ContainsKey(card.Snapshot.Id) || _focusedPaperId == card.Snapshot.Id) return;
        _cameraBeforePaperFocus ??= new Camera(_scale.ScaleX, _pan.X, _pan.Y);
        _focusedPaperId = card.Snapshot.Id;
        Fit(card.Bounds.Inflate(64).Offset(scene.Origin), 1.35, animate: true);
    }

    private void ClearPaperFocus(bool restoreCamera)
    {
        var previous = _cameraBeforePaperFocus;
        _focusedPaperId = null;
        _cameraBeforePaperFocus = null;
        if (restoreCamera && previous is { } camera) SetCamera(camera, animate: true);
        else StopCameraAnimation();
    }

    private void FitActive() { if (ActiveScene is { } scene) Fit(scene.Bounds.Offset(scene.Origin)); }
    private void FitOverview() => Fit(WorkbenchRect.Union(_scenes.Values.Select(s => s.Bounds.Offset(s.Origin))));
    private void BeginPan(object sender, MouseButtonEventArgs e)
    {
        // Preview routing reaches editors and materials before their right-click menus.
        if (e.ChangedButton == MouseButton.Right && !_dragging && !IsSuspended)
        {
            StopCameraAnimation();
            _panPointer = e.GetPosition(_viewport); _panStart = new(_pan.X, _pan.Y);
            _viewport.CaptureMouse(); e.Handled = true;
            return;
        }
        if (_dragging || Mouse.Captured != null) return;
        if (e.ChangedButton == MouseButton.Left && ReferenceEquals(e.OriginalSource, _viewport))
        { BeginMarquee(e); return; }
        if (e.ChangedButton != MouseButton.Middle) return;
        StopCameraAnimation();
        _panPointer = e.GetPosition(_viewport); _panStart = new(_pan.X, _pan.Y);
        _viewport.CaptureMouse(); e.Handled = true;
    }
    private void MovePan(object sender, MouseEventArgs e)
    {
        if (MoveMarquee(e)) return;
        if (_panPointer is not { } start) return;
        var point = e.GetPosition(_viewport); _pan.X = _panStart.X + point.X - start.X; _pan.Y = _panStart.Y + point.Y - start.Y;
        e.Handled = true;
    }
    private void EndPan(object sender, MouseButtonEventArgs e)
    {
        if (EndMarquee(e)) return;
        if (_panPointer is not { } start) return;
        var emptyClick = e.ChangedButton == MouseButton.Left &&
            (e.GetPosition(_viewport) - start).LengthSquared < 16;
        _panPointer = null; _viewport.ReleaseMouseCapture();
        if (emptyClick) ClearPaperFocus(restoreCamera: true);
        e.Handled = true;
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
        if (_dragging) { _refreshPendingAfterDrag = true; return; }
        _refreshTimer.Stop(); _refreshTimer.Start();
    }
    private void SyncActiveScene()
    {
        if (_closed || IsSuspended || _controller.IsDataReloading) return;
        if (_dragging) { _refreshPendingAfterDrag = true; return; }
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
                card.Pin.Content = LockIcon(paper.Placement?.Pinned == true);
                card.Eye.Child = EyeIcon(paper.DesktopVisible);
                card.Move.Cursor = paper.Placement?.Pinned == true ? Cursors.Arrow : Cursors.SizeAll;
                card.Binding.Text = paper.BindingTitle;
                card.Binding.Visibility = paper.BindingTitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                RefreshMaterialCard(card);
            }
            var signature = string.Join("|", snapshot.Nodes.Select(n => $"{n.Id}:{n.Width}:{n.Height}:{n.Affinity}:{n.Kind}:{n.Pinned}:{n.Previous}"))
                + string.Join("|", snapshot.Edges.Select(e => $"{e.From}>{e.To}"));
            if (signature != _modelSignature || scene.Layout == null)
            { _modelSignature = signature; _ = LayoutActiveAsync(); }
            else FocusRequestedPaper();
            _selectedCards.IntersectWith(ids);
            ApplySelectionStyle();
            ApplySceneVisibility();
            DrawDecorations(scene);
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
                var layout = await Task.Run(() => WorkbenchLayoutEngine.Calculate(NodesForLayout(snapshot), snapshot.Edges,
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
                ApplySceneVisibility();
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

    private static WorkbenchLayoutNode[] NodesForLayout(WorkbenchWorkspaceSnapshot snapshot)
    {
        // Only manual groups affect proximity, not paper kinds or plugin affinities.
        var membership = new Dictionary<string, string>(StringComparer.Ordinal);
        if (snapshot.Layout != null)
            foreach (var group in snapshot.Layout.Groups.Values)
                foreach (var member in group.Members)
                    membership.TryAdd(member, group.Id);
        return snapshot.Nodes.Select(n => n with { Affinity = membership.GetValueOrDefault(n.Id) ?? "" }).ToArray();
    }

    private async Task LayoutActiveAsync(bool arrange = false, string? preferredId = null, HashSet<string>? scope = null, HashSet<string>? fixedIds = null)
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
            var nodes = NodesForLayout(snapshot);
            // Automatic updates keep the internal arrangement of a manual group intact.
            if (!arrange)
            {
                var grouped = _controller.WorkbenchLayout.Groups.Values.SelectMany(g => g.Members).ToHashSet(StringComparer.Ordinal);
                nodes = nodes.Select(n => grouped.Contains(n.Id) && n.Previous != null ? n with { Pinned = true } : n).ToArray();
            }
            if (fixedIds != null) nodes = nodes.Select(n => fixedIds.Contains(n.Id) && n.Previous != null ? n with { Pinned = true } : n).ToArray();
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
            ApplySceneVisibility();
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
        _undo.Push(new(_controller.CaptureWorkbenchHistoryLayout()));
        _redo.Clear();
    }
    private void UndoLayout()
    {
        UndoWorkspaceAction();
    }

    private void CreatePaper(string type, WorkbenchPoint? at = null)
    {
        if (IsSuspended) return;
        var paper = _controller.CreatePaper(type, show: false);
        if (paper == null) return;
        RememberLayout();
        if (at is { } point)
        {
            var state = _controller.WorkbenchLayout.Copy();
            state.Cards[paper.Id] = new WorkbenchCardState
            {
                X = point.X - WorkbenchCardState.ValidSize(paper.Width, 320, 240, 640) / 2,
                Y = point.Y - 16,
                Width = WorkbenchCardState.ValidSize(paper.Width, 320, 240, 640),
                Height = WorkbenchCardState.ValidSize(paper.Height, 360, 240, 720),
                Preferred = true
            };
            StoreLayout(state);
        }
        SyncActiveScene();
    }

    private void FocusRequestedPaper()
    {
        if (_focusAfterRefresh is not { } id || ActiveScene is not { } scene || !scene.Cards.TryGetValue(id, out var card)) return;
        RevealCard(scene, id);
        Fit(card.Bounds.Inflate(64).Offset(scene.Origin), 1);
        _focusAfterRefresh = null;
        if (card.MaterialEditor != null)
            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (!_closed && !IsSuspended && ActiveScene?.Cards.GetValueOrDefault(id) == card)
                    card.MaterialEditor.Focus();
            }), DispatcherPriority.Input);
    }
    private static bool SearchMatches(WorkbenchPaperSnapshot paper, string query) =>
        paper.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        paper.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
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
        ClearPaperFocus(restoreCamera: false);
        var found = matches[_searchCursor++ % matches.Length];
        RevealCard(found.Scene, found.Card.Snapshot.Id);
        Fit(found.Card.Bounds.Inflate(64).Offset(found.Scene.Origin));
        _status.Text = Strings.Format("WorkbenchSearchResult", (_searchCursor - 1) % matches.Length + 1, matches.Length);
    }

    private Card CreateActiveCard(WorkbenchPaperSnapshot paper)
    {
        if (WorkspaceMaterials.IsMaterialId(paper.Id)) return CreateMaterialCard(paper);
        var card = CreateCardShell(paper);
        var owner = _controller.WorkbenchPaperWindow(paper.Id);
        card.Lease = owner.BorrowWorkbenchBody(this, card.Body);
        // Focus after an actual click, not during text selection, Todo dragging or
        // the editor's button-down. The original camera is kept for the outside click.
        Point? bodyDown = null;
        card.Body.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left) bodyDown = e.GetPosition(_viewport);
        };
        card.Body.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (bodyDown is not { } origin) return;
            bodyDown = null;
            if ((e.GetPosition(_viewport) - origin).LengthSquared > 16) return;
            Dispatcher.BeginInvoke((Action)(() => FocusPaper(card)), DispatcherPriority.Background);
        };
        // The entire title area is a direct drag surface. Only a double-click changes it
        // into a text editor; the borrowed paper body keeps all of its own mouse behavior.
        card.Move.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2 || Keyboard.Modifiers != ModifierKeys.None) return;
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
        WireCardSelection(card);
        WireContentDrop(card);
        WireDrag(card, card.Move, resize: false);
        WireDrag(card, card.Resize, resize: true);
        return card;
    }

    private Card CreatePreview(WorkbenchPaperSnapshot paper, int workspace)
    {
        var card = CreateCardShell(paper);
        card.Title.IsReadOnly = true;
        card.Title.Focusable = false;
        card.Title.IsHitTestVisible = false;
        card.Title.Visibility = Visibility.Visible;
        card.Pin.Visibility = card.Resize.Visibility = card.Move.Visibility = Visibility.Collapsed;
        var preview = new StackPanel { Margin = new Thickness(16) };
        card.PreviewText = new TextBlock { Text = paper.Summary, TextWrapping = TextWrapping.Wrap, MaxHeight = Math.Max(80, paper.Height - 120), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ink };
        preview.Children.Add(card.PreviewText);
        var open = NewButton(Strings.Get("WorkbenchOpenWorkspace"));
        open.Click += async (_, _) => await ActivateWorkspaceAsync(workspace, paper.Id);
        preview.Children.Add(open); card.Body.Child = preview;
        return card;
    }

    private Card CreateCardShell(WorkbenchPaperSnapshot paper)
    {
        var card = new Card(paper);
        card.Frame.Background = CardBrush; card.Frame.BorderBrush = Outline;
        card.Frame.BorderThickness = new Thickness(1); card.Frame.CornerRadius = new CornerRadius(9);
        card.Frame.ClipToBounds = true;
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        card.Frame.Child = grid;
        var header = card.Header;
        header.Margin = new Thickness(6, 4, 6, 2);
        header.Background = Theme.TitleBarBrush(opaque: true);
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        card.Move.Cursor = Cursors.SizeAll;
        card.Move.Foreground = Ink;
        card.Move.Background = Brushes.Transparent;
        card.Move.MinHeight = 32;
        card.Move.Tag = paper.Title;
        card.Move.Template = TitleDragTemplate(card.Move);
        header.Children.Add(card.Move);
        card.Title.Text = paper.Title; card.Title.FontWeight = FontWeights.SemiBold;
        card.Title.Style = WorkbenchInputStyle();
        card.Title.Background = Brushes.Transparent; card.Title.Foreground = Ink;
        card.Title.BorderBrush = Outline; card.Title.BorderThickness = new Thickness(1);
        card.Title.Padding = new Thickness(12, 5, 4, 5);
        card.Title.Visibility = Visibility.Collapsed;
        Grid.SetColumn(card.Title, 0); header.Children.Add(card.Title);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { card.Pin })
        {
            button.Padding = new Thickness(5, 3, 5, 3);
            button.Margin = new Thickness(1);
            button.Background = Brushes.Transparent;
            button.Foreground = Ink;
            button.BorderThickness = new Thickness(0);
            actions.Children.Add(button);
        }
        card.Pin.Content = LockIcon(paper.Placement?.Pinned == true);
        card.Pin.ToolTip = Strings.Get("WorkbenchPin");
        Grid.SetColumn(actions, 1); header.Children.Add(actions); grid.Children.Add(header);
        card.Binding.Text = paper.BindingTitle; card.Binding.Foreground = Muted; card.Binding.FontSize = 11;
        card.Binding.Margin = new Thickness(14, 0, 14, 4); card.Binding.TextTrimming = TextTrimming.CharacterEllipsis;
        card.Binding.Visibility = paper.BindingTitle.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(card.Binding, 1); grid.Children.Add(card.Binding);
        Grid.SetRow(card.Body, 2); grid.Children.Add(card.Body);
        // The left eye is display-only. It does not intercept pointer input, and the
        // resize grip on the right remains the only interactive footer control.
        var footer = new Grid { Height = 30, Margin = new Thickness(8, 0, 8, 2) };
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        card.Eye.Width = 18; card.Eye.Height = 14;
        card.Eye.Margin = new Thickness(8, 0, 0, 0);
        card.Eye.HorizontalAlignment = HorizontalAlignment.Left;
        card.Eye.VerticalAlignment = VerticalAlignment.Center;
        card.Eye.IsHitTestVisible = false;
        card.Eye.Child = EyeIcon(paper.DesktopVisible);
        footer.Children.Add(card.Eye);
        card.Resize.Width = 26; card.Resize.HorizontalAlignment = HorizontalAlignment.Right;
        card.Resize.Cursor = Cursors.SizeNWSE;
        card.Resize.Foreground = Muted;
        card.Resize.Template = ThumbTemplate("\u25e2");
        Grid.SetColumn(card.Resize, 1); footer.Children.Add(card.Resize);
        Grid.SetRow(footer, 3); grid.Children.Add(footer);
        card.Frame.Tag = card;
        Panel.SetZIndex(card.Frame, 2);
        Position(card, paper.Placement?.Bounds ?? new(32, 32, paper.Width, paper.Height));
        return card;
    }
    private static FrameworkElement LockIcon(bool locked) => new System.Windows.Shapes.Path
    {
        Data = Geometry.Parse(locked
            ? "M5,9 L5,6 C5,0 15,0 15,6 L15,9 M3,9 L17,9 L17,19 L3,19 Z M10,12 L10,16"
            : "M5,9 L5,6 C5,0 15,0 15,6 M3,9 L17,9 L17,19 L3,19 Z M10,12 L10,16"),
        Stroke = Ink, StrokeThickness = 1.65, StrokeLineJoin = PenLineJoin.Round,
        Fill = Brushes.Transparent, Width = 17, Height = 17, Stretch = Stretch.Uniform
    };

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
        label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Control.Foreground))
            { Source = owner });
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
        text.SetValue(TextBlock.TextProperty, glyph);
        text.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        return new ControlTemplate(typeof(Thumb)) { VisualTree = text };
    }
    private static void Position(Card card, WorkbenchRect bounds)
    {
        card.Bounds = bounds; Canvas.SetLeft(card.Frame, bounds.X); Canvas.SetTop(card.Frame, bounds.Y);
        card.Frame.Width = bounds.Width; card.Frame.Height = bounds.Height;
    }
    private void ResumeDeferredModelRefresh()
    {
        if (!_refreshPendingAfterDrag) return;
        _refreshPendingAfterDrag = false;
        _refreshTimer.Stop();
        SyncActiveScene();
    }

    private void WireDrag(Card card, Thumb thumb, bool resize)
    {
        Dictionary<string, WorkbenchRect>? before = null;
        var changed = false;
        thumb.DragStarted += (_, _) =>
        {
            if (IsSuspended || ActiveScene is not { } scene || (!resize && card.Snapshot.Placement?.Pinned == true)) return;
            if (!resize && !_selectedCards.Contains(card.Snapshot.Id)) SelectOnly(card.Snapshot.Id);
            var ids = resize ? new[] { card.Snapshot.Id } : MovingSelection(card.Snapshot.Id);
            if (!resize && ids.Any(id => scene.Cards.TryGetValue(id, out var selected) && selected.Snapshot.Placement?.Pinned == true)) return;
            before = ids.Where(scene.Cards.ContainsKey).ToDictionary(id => id, id => scene.Cards[id].Bounds, StringComparer.Ordinal);
            if (before.Count == 0) return;
            foreach (var id in before.Keys) Panel.SetZIndex(scene.Cards[id].Frame, 12);
            Panel.SetZIndex(card.Frame, 13);
            changed = false;
            _dragging = true;
        };
        thumb.DragDelta += (_, e) =>
        {
            if (!_dragging || before == null || ActiveScene is not { } scene) return;
            var dx = e.HorizontalChange; var dy = e.VerticalChange;
            if (Math.Abs(dx) + Math.Abs(dy) < .001) return;
            if (!changed) { CancelLayout(); changed = true; }
            foreach (var id in before.Keys)
            {
                if (!scene.Cards.TryGetValue(id, out var selected)) continue;
                var r = selected.Bounds;
                Position(selected, resize
                    ? r with { Width = Math.Clamp(r.Width + dx, ResizeMinimum(card).Width, 1600), Height = Math.Clamp(r.Height + dy, ResizeMinimum(card).Height, 1600) }
                    : r with { X = Math.Clamp(r.X + dx, -10_000_000, 10_000_000), Y = Math.Clamp(r.Y + dy, -10_000_000, 10_000_000) });
            }
            DrawDecorations(scene, dragging: true);
        };
        thumb.DragCompleted += (_, e) =>
        {
            if (!_dragging || before == null) return;
            _dragging = false;
            if (ActiveScene is { } active)
                foreach (var id in before.Keys)
                    if (active.Cards.TryGetValue(id, out var selected)) Panel.SetZIndex(selected.Frame, 2);
            FinishCardMove(before, e.Canceled || !changed);
            before = null;
        };
    }

    private void FinishCardMove(Dictionary<string, WorkbenchRect> before, bool cancel)
    {
        if (ActiveScene is not { } scene) return;
        var changed = before.Any(p => scene.Cards.TryGetValue(p.Key, out var card) && card.Bounds != p.Value);
        if (cancel || !changed)
        {
            foreach (var pair in before)
                if (scene.Cards.TryGetValue(pair.Key, out var card)) Position(card, pair.Value);
            DrawDecorations(scene);
            ResumeDeferredModelRefresh();
            return;
        }
        RememberLayout();
        var state = _controller.WorkbenchLayout.Copy();
        foreach (var id in before.Keys)
        {
            if (!scene.Cards.TryGetValue(id, out var card)) continue;
            if (!state.Cards.TryGetValue(id, out var placement)) state.Cards[id] = placement = new();
            placement.X = card.Bounds.X; placement.Y = card.Bounds.Y;
            placement.Width = card.Bounds.Width; placement.Height = card.Bounds.Height;
            placement.Preferred = true;
        }
        StoreLayout(state);
        ResumeDeferredModelRefresh();
        _modelSignature = "";
        _ = LayoutActiveAsync(fixedIds: before.Keys.ToHashSet(StringComparer.Ordinal));
    }

    private void DrawDecorations(Scene scene, bool dragging = false)
    {
        foreach (var element in scene.Decorations) scene.Canvas.Children.Remove(element);
        scene.Decorations.Clear();
        void Add(UIElement element, double x, double y)
        { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); Panel.SetZIndex(element, 0); scene.Canvas.Children.Add(element); scene.Decorations.Add(element); }
        var bounds = scene.Bounds;
        var frame = new Rectangle { Width = bounds.Width, Height = bounds.Height, RadiusX = 18, RadiusY = 18,
            Stroke = FrameBrush, StrokeThickness = scene.Number == _controller.WorkspaceNumber ? 2.5 : 2,
            Fill = CanvasBrush, IsHitTestVisible = false, Opacity = scene.Number == _controller.WorkspaceNumber ? 1 : .82 };
        Add(frame, bounds.X, bounds.Y);
        var title = NewButton(_controller.WorkbenchWorkspaceName(scene.Number));
        title.FontSize = 22; title.FontWeight = FontWeights.SemiBold;
        title.Click += async (_, _) => { if (scene.Number == _controller.WorkspaceNumber) FitActive(); else await ActivateWorkspaceAsync(scene.Number, null); };
        Add(title, bounds.X + 14, bounds.Y - 50);
        if (scene.Snapshot?.Error is { } error)
        { Add(new TextBlock { Text = Strings.Format("WorkbenchReadError", error), Width = 560, TextWrapping = TextWrapping.Wrap, Foreground = Ink, Margin = new Thickness(20) }, bounds.X, bounds.Y); return; }
        if (scene.Layout is not { } layout) return;
        foreach (var route in layout.Routes)
        {
            if (!IsCardShown(scene, route.From) || !IsCardShown(scene, route.To) ||
                SceneState(scene).Connections.Any(c => c.From == route.From && c.To == route.To)) continue;
            var points = dragging && scene.Cards.TryGetValue(route.From, out var from) && scene.Cards.TryGetValue(route.To, out var to)
                ? new[] { from.Bounds.Center, to.Bounds.Center } : route.Points;
            var line = new Polyline { Stroke = Accent, StrokeThickness = 2, Opacity = .65,
                Points = new PointCollection(points.Select(p => new Point(p.X, p.Y))), IsHitTestVisible = false };
            Add(line, 0, 0);

        }
        DrawWorkspaceGroups(scene);
        DrawWorkspaceConnections(scene);
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
        CancelWorkspaceGesture();
        _selectedCards.Clear(); _focusedGroupId = null; _cameraBeforeGroupFocus = null;
        ClearPaperFocus(restoreCamera: false);
        IsSuspended = true; _toolbar.IsEnabled = false; _viewport.IsEnabled = false;
        _dragging = false; _refreshPendingAfterDrag = false;
        CancelLayout(); _overviewCancellation?.Cancel(); _refreshTimer.Stop();
        foreach (var scene in _scenes.Values)
            foreach (var id in scene.Cards.Keys.ToArray()) RemoveCard(scene, id, false);
        _world.Children.Clear(); _scenes.Clear(); _modelSignature = ""; _undo.Clear(); _redo.Clear();
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
            if (card.Title.IsKeyboardFocusWithin) CommitCardTitle(card);
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
        _closed = true; ClearPaperFocus(restoreCamera: false);
        _controller.WorkbenchModelChanged -= QueueModelRefresh;
        _controller.WorkbenchContentTransferReplayed -= OnContentTransferReplayed;
        _controller.WorkbenchContentTransferBlocked -= OnTransferHistoryBlocked;
        _controller.WorkbenchContentTransfersRetired -= OnContentTransfersRetired;
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
        internal Grid Header { get; } = new();
        internal TextBox Title { get; } = new();
        internal TextBlock Binding { get; } = new();
        internal TextBlock? PreviewText;
        internal TextBox? MaterialEditor;
        internal Image? MaterialImage;
        internal Button? MaterialOpen;
        internal string MaterialVersion = "";
        internal Border Body { get; } = new() { ClipToBounds = true };
        internal Thumb Move { get; } = new();
        internal Thumb Resize { get; } = new();
        internal Button Pin { get; } = new();
        internal Border Eye { get; } = new();
        internal PaperWindow.WorkbenchBodyLease? Lease;
        internal WorkbenchRect Bounds;
    }
}
