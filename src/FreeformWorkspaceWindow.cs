using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class AppController
{
    private FreeformWorkspaceWindow? _freeformWorkspaceWindow;
    public void ShowFreeformWorkspace(string? focusPaperId = null)
    {
        if (IsExiting) return;
        if (_freeformWorkspaceWindow is { IsLoaded: true } current)
        {
            if (!current.IsVisible) current.Show();
            current.FocusPaper(focusPaperId);
            current.Activate();
            return;
        }
        var window = new FreeformWorkspaceWindow(this, focusPaperId);
        _freeformWorkspaceWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_freeformWorkspaceWindow, window)) _freeformWorkspaceWindow = null; };
        window.Show();
        window.Activate();
    }
    internal void SaveWorkspaceLayout() => MarkDirty();
}

internal sealed class FreeformWorkspaceWindow : Window
{
    private const double W = 250, H = 178, GapX = 34, GapY = 30;
    private readonly AppController _controller;
    private readonly Canvas _canvas = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _offset = new();
    private readonly TextBox _search = new();
    private readonly Dictionary<string, Border> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaperData> _papers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaperItem> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _itemCards = new(StringComparer.Ordinal);
    private readonly Canvas _connections = new();
    private Border? _selected, _dragCard;
    private string? _connectFromNodeId;
    private Point _dragPoint, _dragOrigin, _panPoint;
    private double _panX, _panY;
    private bool _panning;
    private readonly string? _focusPaperId;

    public FreeformWorkspaceWindow(AppController controller, string? focusPaperId = null)
    {
        _controller = controller;
        _focusPaperId = focusPaperId;
        Title = Strings.Get("WorkspaceOpen");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Normal;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Topmost = true;
        ShowInTaskbar = true;
        Background = Theme.PaperBrush;
        Foreground = Theme.TextBrush;
        FontFamily = AppTypography.UiFontFamily;
        FontSize = AppTypography.Scale(13);
        Language = AppTypography.Language;

        var root = new Grid { Background = Theme.PaperBrush };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(27) });
        var toolbar = BuildToolbar();
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        _canvas.Background = new DrawingBrush
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 24, 24),
            ViewportUnits = BrushMappingMode.Absolute,
            Drawing = new GeometryDrawing(Brushes.Transparent,
                new Pen(Theme.PaperBorderBrush, 0.6),
                new GeometryGroup { Children = { new LineGeometry(new Point(0, 0), new Point(24, 0)), new LineGeometry(new Point(0, 0), new Point(0, 24)) } })
        };
        _canvas.RenderTransform = new TransformGroup { Children = new TransformCollection { _scale, _offset } };
        _connections.IsHitTestVisible = false;
        _connections.RenderTransform = _canvas.RenderTransform;
        _canvas.ClipToBounds = true;
        _canvas.MouseDown += CanvasDown;
        _canvas.MouseMove += CanvasMove;
        _canvas.MouseUp += CanvasUp;
        _canvas.MouseWheel += CanvasWheel;
        var canvasHost = new Grid();
        canvasHost.Children.Add(_connections);
        canvasHost.Children.Add(_canvas);
        Grid.SetRow(canvasHost, 1);
        root.Children.Add(canvasHost);

        var footer = new TextBlock
        {
            Text = Strings.Get("WorkspaceHint") + " · " + Strings.Get("WorkspaceConnectHint"),
            Foreground = Theme.WeakTextBrush,
            FontSize = AppTypography.Scale(10.5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
            Background = Theme.Tint((byte)(Theme.IsDark ? 14 : 9))
        };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
        Loaded += (_, _) => { BuildCards(); if (focusPaperId == null) _search.Focus(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (_search.Text.Length > 0) _search.Clear();
                else Close();
                e.Handled = true;
            }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                _search.Focus(); _search.SelectAll(); e.Handled = true;
            }
            else if (e.Key == Key.Enter && !_search.IsKeyboardFocusWithin &&
                     _selected?.Tag is string id && _papers.TryGetValue(id, out var paper))
            {
                _controller.ShowPaper(paper); e.Handled = true;
            }
        };
    }

    private UIElement BuildToolbar()
    {
        var grid = new Grid { Margin = new Thickness(14, 8, 14, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = Strings.Get("WorkspaceOpen"), FontSize = AppTypography.Scale(17), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        Grid.SetColumn(title, 0); grid.Children.Add(title);
        _search.MinWidth = 190; _search.MaxWidth = 360; _search.Height = 32; _search.Padding = new Thickness(10, 5, 10, 5);
        _search.VerticalContentAlignment = VerticalAlignment.Center; _search.BorderBrush = Theme.PaperBorderBrush; _search.BorderThickness = new Thickness(1);
        _search.Background = Theme.PaperBrush; _search.Foreground = Theme.TextBrush; _search.CaretBrush = Theme.TextBrush;
        _search.ToolTip = Strings.Get("WorkspaceSearch"); _search.TextChanged += (_, _) => FilterCards();
        Grid.SetColumn(_search, 1); grid.Children.Add(_search);
        Button(2, Strings.Get("WorkspaceNewNote"), "＋", () => NewPaper(PaperTypes.Note));
        Button(3, Strings.Get("WorkspaceNewTodo"), "✓", () => NewPaper(PaperTypes.Todo));
        Button(4, Strings.Get("WorkspaceFit"), "⤢", FitCanvas);
        Button(5, Strings.Get("WorkspaceClose"), "×", Close);
        return new Border { Background = Theme.PaperBrush, BorderBrush = Theme.PaperBorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
        void Button(int col, string label, string glyph, Action action)
        {
            var b = new Button { Content = glyph + "  " + label, Margin = new Thickness(7, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), Background = Brushes.Transparent, Foreground = Theme.TextBrush, BorderBrush = Theme.PaperBorderBrush, Cursor = Cursors.Hand, ToolTip = label };
            b.Click += (_, _) => action(); Grid.SetColumn(b, col); grid.Children.Add(b);
        }
    }

    private void BuildCards()
    {
        _canvas.Children.Clear(); _cards.Clear(); _papers.Clear();
        var papers = _controller.State.Papers.ToList();
        for (int i = 0; i < papers.Count; i++)
        {
            var p = papers[i]; _papers[p.Id] = p;
            var hadWorkspacePosition = p.WorkspaceX.HasValue && p.WorkspaceY.HasValue;
            p.WorkspaceX ??= 90 + (i % 4) * (W + GapX);
            p.WorkspaceY ??= 70 + (i / 4) * (H + GapY);
            if (!hadWorkspacePosition) _controller.SaveWorkspaceLayout();
            var card = MakeCard(p);
            _cards[p.Id] = card;
            Canvas.SetLeft(card, p.WorkspaceX.Value); Canvas.SetTop(card, p.WorkspaceY.Value); _canvas.Children.Add(card);
            if (p.Type == PaperTypes.Todo)
            {
                var rows = p.Items.Where(item => !string.IsNullOrWhiteSpace(item.Text)).OrderBy(item => item.Order).ToList();
                for (var j = 0; j < rows.Count; j++)
                {
                    var item = rows[j]; _items[item.Id] = item;
                    item.WorkspaceX ??= p.WorkspaceX.Value + W + 50;
                    item.WorkspaceY ??= p.WorkspaceY.Value + j * 96;
                    var node = MakeItemNode(p, item); _itemCards[item.Id] = node;
                    Canvas.SetLeft(node, item.WorkspaceX.Value); Canvas.SetTop(node, item.WorkspaceY.Value); _canvas.Children.Add(node);
                }
            }
        }
        DrawConnections();
        FilterCards();
        FitCanvas();
    }

    private Border MakeCard(PaperData p)
    {
        var todo = p.Type == PaperTypes.Todo;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(p.Title) ? (todo ? Strings.Get("TrayNewTodo") : Strings.Get("TrayNewNote")) : p.Title, FontWeight = FontWeights.SemiBold, FontSize = AppTypography.Scale(13), Foreground = Theme.TextBrush, TextTrimming = TextTrimming.CharacterEllipsis });
        var preview = todo
            ? string.Join("\n", p.Items.OrderBy(x => x.Order).Where(x => !string.IsNullOrWhiteSpace(x.Text)).Take(4).Select(x => (x.Done ? "☑ " : "☐ ") + x.Text.Replace('\n', ' ')))
            : string.Join(" ", (p.Content ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        panel.Children.Add(new TextBlock { Text = preview, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 120, Margin = new Thickness(0, 9, 0, 0), Foreground = Theme.WeakTextBrush, FontSize = AppTypography.Scale(11.5) });
        var card = new Border { Width = W, Height = H, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(10), Background = Theme.PaperBrush, BorderBrush = Theme.PaperBorderBrush, BorderThickness = new Thickness(1), Child = panel, Cursor = Cursors.SizeAll, Tag = p.Id };
        card.MouseLeftButtonDown += (_, e) =>
        {
            Select(card);
            if (e.ClickCount > 1) { _controller.ShowPaper(p); e.Handled = true; return; }
            _dragCard = card; _dragPoint = e.GetPosition(_canvas); _dragOrigin = new Point(Canvas.GetLeft(card), Canvas.GetTop(card)); card.CaptureMouse(); e.Handled = true;
        };
        card.MouseMove += (_, e) => { if (ReferenceEquals(_dragCard, card) && e.LeftButton == MouseButtonState.Pressed) { var pt = e.GetPosition(_canvas); Canvas.SetLeft(card, _dragOrigin.X + pt.X - _dragPoint.X); Canvas.SetTop(card, _dragOrigin.Y + pt.Y - _dragPoint.Y); } };
        card.MouseLeftButtonUp += (_, e) => { if (!ReferenceEquals(_dragCard, card)) return; p.WorkspaceX = Canvas.GetLeft(card); p.WorkspaceY = Canvas.GetTop(card); _dragCard = null; card.ReleaseMouseCapture(); _controller.SaveWorkspaceLayout(); e.Handled = true; };
        card.LostMouseCapture += (_, _) => { if (ReferenceEquals(_dragCard, card)) _dragCard = null; };
        return card;
    }

    internal void FocusPaper(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !_cards.TryGetValue(id, out var card)) return;
        Select(card); CenterCard(card);
    }

    private Border MakeItemNode(PaperData owner, PaperItem item)
    {
        var check = new CheckBox { IsChecked = item.Done, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0), Cursor = Cursors.Hand };
        check.Checked += (_, _) => { item.Done = true; _controller.SaveWorkspaceLayout(); };
        check.Unchecked += (_, _) => { item.Done = false; _controller.SaveWorkspaceLayout(); };
        var editor = new TextBox { Text = item.Text, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 36, MaxHeight = 110, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Theme.TextBrush, Padding = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Top };
        editor.TextChanged += (_, _) => { if (editor.IsKeyboardFocusWithin && item.Text != editor.Text) { item.Text = editor.Text; _controller.SaveWorkspaceLayout(); } };
        var row = new DockPanel(); DockPanel.SetDock(check, Dock.Left); row.Children.Add(check); row.Children.Add(editor);
        var card = new Border { Width = 220, MinHeight = 68, MaxHeight = 150, Padding = new Thickness(10), CornerRadius = new CornerRadius(8), Background = Theme.PaperBrush, BorderBrush = Theme.PaperBorderBrush, BorderThickness = new Thickness(1), Child = row, Cursor = Cursors.SizeAll, Tag = "item:" + item.Id };
        card.MouseLeftButtonDown += (_, e) =>
        {
            Select(card);
            if (e.ClickCount > 1) { _controller.ShowPaper(owner); e.Handled = true; return; }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { var id = "item:" + item.Id; if (_connectFromNodeId == null) _connectFromNodeId = id; else { AddConnection(_connectFromNodeId, id); _connectFromNodeId = null; } e.Handled = true; return; }
            _dragCard = card; _dragPoint = e.GetPosition(_canvas); _dragOrigin = new Point(Canvas.GetLeft(card), Canvas.GetTop(card)); card.CaptureMouse(); e.Handled = true;
        };
        card.MouseMove += (_, e) => { if (ReferenceEquals(_dragCard, card) && e.LeftButton == MouseButtonState.Pressed) { var pt = e.GetPosition(_canvas); Canvas.SetLeft(card, _dragOrigin.X + pt.X - _dragPoint.X); Canvas.SetTop(card, _dragOrigin.Y + pt.Y - _dragPoint.Y); } };
        card.MouseLeftButtonUp += (_, e) => { if (!ReferenceEquals(_dragCard, card)) return; item.WorkspaceX = Canvas.GetLeft(card); item.WorkspaceY = Canvas.GetTop(card); _dragCard = null; card.ReleaseMouseCapture(); _controller.SaveWorkspaceLayout(); DrawConnections(); e.Handled = true; };
        card.LostMouseCapture += (_, _) => { if (ReferenceEquals(_dragCard, card)) _dragCard = null; };
        return card;
    }

    private void AddConnection(string from, string to)
    {
        if (from == to || _controller.State.WorkspaceConnections.Any(c => c.FromNodeId == from && c.ToNodeId == to || c.FromNodeId == to && c.ToNodeId == from)) return;
        _controller.State.WorkspaceConnections.Add(new WorkspaceConnection { FromNodeId = from, ToNodeId = to });
        _controller.SaveWorkspaceLayout(); DrawConnections();
    }

    private void DrawConnections()
    {
        _connections.Children.Clear();
        foreach (var link in _controller.State.WorkspaceConnections)
        {
            if (!TryGetNodeCenter(link.FromNodeId, out var a) || !TryGetNodeCenter(link.ToNodeId, out var b)) continue;
            var dx = Math.Max(35, Math.Abs(b.X - a.X) * .45);
            var geometry = new PathGeometry(); var figure = new PathFigure { StartPoint = a };
            figure.Segments.Add(new BezierSegment(new Point(a.X + (b.X >= a.X ? dx : -dx), a.Y), new Point(b.X - (b.X >= a.X ? dx : -dx), b.Y), b, true)); geometry.Figures.Add(figure);
            _connections.Children.Add(new System.Windows.Shapes.Path { Data = geometry, Stroke = Theme.ActiveBrush, StrokeThickness = 1.7, Opacity = .75 });
        }
    }

    private bool TryGetNodeCenter(string id, out Point point)
    {
        if (id.StartsWith("item:", StringComparison.Ordinal) && _itemCards.TryGetValue(id[5..], out var item)) { point = new Point(Canvas.GetLeft(item) + item.ActualWidth / 2, Canvas.GetTop(item) + item.ActualHeight / 2); return true; }
        if (_cards.TryGetValue(id, out var card)) { point = new Point(Canvas.GetLeft(card) + card.ActualWidth / 2, Canvas.GetTop(card) + card.ActualHeight / 2); return true; }
        point = default; return false;
    }

    private void Select(Border card)
    {
        if (_selected != null) { _selected.BorderBrush = Theme.PaperBorderBrush; _selected.BorderThickness = new Thickness(1); }
        _selected = card; card.BorderBrush = Theme.ActiveBrush; card.BorderThickness = new Thickness(2);
    }

    private void FilterCards()
    {
        var q = _search.Text.Trim();
        foreach (var pair in _cards)
        {
            var p = _papers[pair.Key];
            var text = p.Type == PaperTypes.Todo ? string.Join("\n", p.Items.Select(x => x.Text)) : p.Content;
            pair.Value.Visibility = q.Length == 0 || ($"{p.Title}\n{text}").Contains(q, StringComparison.CurrentCultureIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var pair in _itemCards) pair.Value.Visibility = q.Length == 0 || _items[pair.Key].Text.Contains(q, StringComparison.CurrentCultureIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        DrawConnections();
    }

    private void NewPaper(string type)
    {
        var p = _controller.CreatePaper(type, show: false);
        if (p == null) return;
        p.WorkspaceX = Math.Max(30, (-_offset.X + _canvas.ActualWidth / 2) / _scale.ScaleX - W / 2);
        p.WorkspaceY = Math.Max(30, (-_offset.Y + _canvas.ActualHeight / 2) / _scale.ScaleY - H / 2);
        _controller.SaveWorkspaceLayout(); BuildCards(); _search.Text = "";
        if (_cards.TryGetValue(p.Id, out var card)) { Select(card); CenterCard(card); }
        _controller.ShowPaper(p);
    }

    private void FitCanvas()
    {
        var cards = _cards.Values.Concat(_itemCards.Values).Where(c => c.Visibility == Visibility.Visible).ToList();
        if (cards.Count == 0 || _canvas.ActualWidth < 1 || _canvas.ActualHeight < 1) return;
        var l = cards.Min(Canvas.GetLeft); var t = cards.Min(Canvas.GetTop);
        var r = cards.Max(c => Canvas.GetLeft(c) + c.ActualWidth); var b = cards.Max(c => Canvas.GetTop(c) + c.ActualHeight);
        var w = Math.Max(1, r - l); var h = Math.Max(1, b - t);
        var z = Math.Clamp(Math.Min((_canvas.ActualWidth - 80) / w, (_canvas.ActualHeight - 60) / h), .35, 1.25);
        _scale.ScaleX = _scale.ScaleY = z;
        _offset.X = (_canvas.ActualWidth - w * z) / 2 - l * z;
        _offset.Y = (_canvas.ActualHeight - h * z) / 2 - t * z;
    }

    private void CenterCard(FrameworkElement c)
    {
        _scale.ScaleX = _scale.ScaleY = 1;
        _offset.X = _canvas.ActualWidth / 2 - Canvas.GetLeft(c) - c.ActualWidth / 2;
        _offset.Y = _canvas.ActualHeight / 2 - Canvas.GetTop(c) - c.ActualHeight / 2;
    }

    private void CanvasDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        _panning = true; _panPoint = e.GetPosition(this); _panX = _offset.X; _panY = _offset.Y; _canvas.CaptureMouse(); Cursor = Cursors.SizeAll; e.Handled = true;
    }
    private void CanvasMove(object sender, MouseEventArgs e)
    {
        if (_panning && e.MiddleButton == MouseButtonState.Pressed) { var p = e.GetPosition(this); _offset.X = _panX + p.X - _panPoint.X; _offset.Y = _panY + p.Y - _panPoint.Y; }
    }
    private void CanvasUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        _panning = false; _canvas.ReleaseMouseCapture(); Cursor = Cursors.Arrow; e.Handled = true;
    }
    private void CanvasWheel(object sender, MouseWheelEventArgs e)
    {
        var p = e.GetPosition(_canvas); var old = _scale.ScaleX;
        var z = Math.Clamp(old * (e.Delta > 0 ? 1.1 : 1 / 1.1), .25, 2);
        var factor = z / old;
        // Keep the canvas point under the pointer stationary while changing scale.
        _offset.X += p.X * old * (1 - factor);
        _offset.Y += p.Y * old * (1 - factor);
        _scale.ScaleX = _scale.ScaleY = z; e.Handled = true;
    }
}
