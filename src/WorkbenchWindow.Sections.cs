using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using PaperTodo.Plugin;
using Brushes = System.Windows.Media.Brushes;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MenuItem = System.Windows.Controls.MenuItem;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace PaperTodo;

internal sealed partial class WorkbenchWindow
{
    /// <summary>One workspace canvas. Only the live section maps to the controller's state.</summary>
    private sealed class WorkbenchSection
    {
        public required int Number { get; init; }
        public required bool IsLive { get; init; }
        public required IReadOnlyList<PaperData> Papers { get; init; }
        public string? Unavailable { get; init; }
        public Size Extension { get; set; }
        public Size CanvasSize { get; set; }
        public Point WorldOrigin { get; set; }
        public Canvas Host { get; } = new();
        public Border Frame { get; } = new();
        public Canvas Content { get; } = new() { ClipToBounds = false };
        public Canvas RegionLayer { get; } = new() { IsHitTestVisible = false };
        public Canvas LinkLayer { get; } = new();
        public Canvas CardLayer { get; } = new();
        public Border Prompt { get; } = new();
        public Dictionary<string, WorkbenchCard> Cards { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Point> Origins { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (WorkbenchCardKind Kind, string GroupKey)> Kinds { get; } =
            new(StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------ build

    private void BuildSections()
    {
        var extensions = _sections.ToDictionary(section => section.Number, section => section.Extension);
        _sections.Clear();
        _world.Children.Clear();
        _live = null;

        var count = _controller.WorkbenchWorkspaceCount;
        var active = _controller.WorkbenchActiveWorkspace;
        for (var number = 1; number <= count; number++)
        {
            var preview = _controller.LoadWorkbenchPreview(number);
            var section = new WorkbenchSection
            {
                Number = number,
                IsLive = number == active,
                Papers = preview.Papers,
                Unavailable = preview.Unavailable,
                Extension = extensions.GetValueOrDefault(number)
            };
            _sections.Add(section);
            if (section.IsLive) _live = section;
            BuildSection(section);
            _world.Children.Add(section.Host);
        }
        LayoutWorld();
    }

    private void RebuildSection(WorkbenchSection section)
    {
        if (section.IsLive && !ReferenceEquals(section.Papers, _controller.State.Papers))
        {
            // The live document was replaced (workspace switch or reload): rebuild everything.
            BuildSections();
            return;
        }
        BuildSection(section);
        LayoutWorld();
    }

    private void ReloadPreview(int number)
    {
        var index = _sections.FindIndex(section => section.Number == number);
        if (index < 0) return;
        var old = _sections[index];
        var preview = _controller.LoadWorkbenchPreview(number);
        var section = new WorkbenchSection
        {
            Number = number,
            IsLive = old.IsLive,
            Papers = preview.Papers,
            Unavailable = preview.Unavailable,
            Extension = old.Extension
        };
        _sections[index] = section;
        _world.Children.Remove(old.Host);
        BuildSection(section);
        _world.Children.Add(section.Host);
        LayoutWorld();
    }

    private void BuildSection(WorkbenchSection section)
    {
        section.Host.Children.Clear();
        section.Content.Children.Clear();
        section.RegionLayer.Children.Clear();
        section.LinkLayer.Children.Clear();
        section.CardLayer.Children.Clear();
        section.Cards.Clear();
        section.Origins.Clear();
        section.Kinds.Clear();

        foreach (var paper in section.Papers)
        {
            var kind = _controller.WorkbenchKindOf(paper, section.IsLive);
            section.Kinds.TryAdd(paper.Id, (kind, AppController.WorkbenchGroupKey(paper, kind)));
        }
        PlaceUnplaced(section);
        section.CanvasSize = WorkbenchLayout.CanvasSize(section.Origins.Values, section.Extension);

        // Header above the solid workspace frame.
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = SectionHeaderHeight,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(new TextBlock
        {
            Text = AppController.WorkbenchWorkspaceLabel(section.Number),
            Foreground = Theme.TextBrush,
            FontWeight = FontWeights.SemiBold,
            FontSize = AppTypography.Scale(20),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 12, 0)
        });
        if (section.IsLive)
        {
            header.Children.Add(SectionTag(Strings.Get("WorkbenchCurrentWorkspace"), Theme.ActiveBrush));
        }
        else if (_controller.WorkbenchWorkspaceCount > 1)
        {
            header.Children.Add(SwitchButton(section.Number));
        }
        Canvas.SetLeft(header, 0);
        Canvas.SetTop(header, -SectionHeaderHeight);
        section.Host.Children.Add(header);

        section.Frame.CornerRadius = new CornerRadius(16);
        section.Frame.BorderThickness = new Thickness(section.IsLive ? 2.5 : 2);
        section.Frame.BorderBrush = section.IsLive ? Theme.ActiveBrush : Theme.WorkbenchFrameBrush;
        section.Frame.Background = Theme.WorkbenchCanvasBrush;
        section.Frame.Opacity = section.IsLive ? 1 : 0.82;
        section.Frame.Child = null;
        section.Host.Children.Add(section.Frame);

        section.Content.Children.Add(section.RegionLayer);
        section.Content.Children.Add(section.LinkLayer);
        section.Content.Children.Add(section.CardLayer);
        section.Content.IsHitTestVisible = section.IsLive;
        section.Content.Background = section.IsLive ? Brushes.Transparent : null;
        section.Host.Children.Add(section.Content);

        if (section.Unavailable is { } unavailable)
        {
            var message = new TextBlock
            {
                Text = unavailable,
                Foreground = Theme.WeakTextBrush,
                FontSize = AppTypography.Scale(18),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Width = 520
            };
            Canvas.SetLeft(message, (section.CanvasSize.Width - 520) / 2);
            Canvas.SetTop(message, section.CanvasSize.Height / 2 - 30);
            section.Content.Children.Add(message);
        }

        var z = 0;
        foreach (var paper in section.Papers)
        {
            if (section.Cards.ContainsKey(paper.Id)) continue;
            var (kind, _) = section.Kinds[paper.Id];
            var card = new WorkbenchCard(
                _controller,
                paper,
                section.IsLive,
                kind,
                _controller.WorkbenchTitle(paper, section.IsLive, section.Papers),
                ShowStatus);
            var origin = section.Origins[paper.Id];
            Canvas.SetLeft(card, origin.X);
            Canvas.SetTop(card, origin.Y);
            Panel.SetZIndex(card, z++);
            section.Cards[paper.Id] = card;
            section.CardLayer.Children.Add(card);
            if (section.IsLive) AttachCardInteraction(section, card);
        }

        section.Prompt.Visibility = Visibility.Collapsed;
        section.Prompt.CornerRadius = new CornerRadius(16);
        section.Prompt.IsHitTestVisible = false;
        section.Host.Children.Add(section.Prompt);

        ApplySectionSize(section);
        RefreshDecorations(section);
    }

    private UIElement SectionTag(string text, Brush brush) => new Border
    {
        CornerRadius = new CornerRadius(6),
        BorderBrush = brush,
        BorderThickness = new Thickness(1.2),
        Padding = new Thickness(8, 2, 8, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = AppTypography.Scale(13)
        }
    };

    private UIElement SwitchButton(int number)
    {
        var button = new WorkbenchSectionButton
        {
            CornerRadius = new CornerRadius(6),
            BorderBrush = Theme.WorkbenchFrameBrush,
            BorderThickness = new Thickness(1.2),
            Background = Theme.PaperBrush,
            Padding = new Thickness(10, 3, 10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = Strings.Get("WorkbenchSwitchHere"),
                Foreground = Theme.TextBrush,
                FontSize = AppTypography.Scale(13)
            }
        };
        button.MouseEnter += (_, _) => button.Background = Theme.HoverBrush;
        button.MouseLeave += (_, _) => button.Background = Theme.PaperBrush;
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            await SwitchToWorkspaceAsync(number);
        };
        return button;
    }

    private async Task SwitchToWorkspaceAsync(int number)
    {
        if (!_controller.CanSwitchWorkspaceFromWorkbench) return;
        CommitFocusedEditor();
        ShowStatus(Strings.Get("WorkspaceSwitching"));
        _holdOpen++;
        bool switched;
        try
        {
            switched = await _controller.SwitchWorkspaceAsync(number);
        }
        finally
        {
            _holdOpen--;
        }
        if (_closing) return;
        _undoArrange = null;
        _undoArrangeButton.Visibility = Visibility.Collapsed;
        BuildSections();
        if (switched) FitLive();
        ActivateFromCommand();
        ShowStatus(switched
            ? Strings.Format("WorkbenchSwitched", AppController.WorkbenchWorkspaceLabel(number))
            : Strings.Get("WorkbenchSwitchFailed"));
    }

    private void ApplySectionSize(WorkbenchSection section)
    {
        var size = section.CanvasSize;
        section.Frame.Width = size.Width;
        section.Frame.Height = size.Height;
        section.Content.Width = size.Width;
        section.Content.Height = size.Height;
        section.Prompt.Width = size.Width;
        section.Prompt.Height = size.Height;
    }

    /// <summary>Sections sit left to right in workspace order; growth pushes later ones right.</summary>
    private void LayoutWorld()
    {
        var x = 0.0;
        foreach (var section in _sections)
        {
            section.WorldOrigin = new Point(x, SectionHeaderHeight);
            Canvas.SetLeft(section.Host, section.WorldOrigin.X);
            Canvas.SetTop(section.Host, section.WorldOrigin.Y);
            x += section.CanvasSize.Width + SectionGap;
        }
    }

    // ------------------------------------------------------------------ light auto-tidy

    private void PlaceUnplaced(WorkbenchSection section)
    {
        var placed = new Dictionary<string, Point>(StringComparer.Ordinal);
        var unplaced = new List<PaperData>();
        foreach (var paper in section.Papers)
        {
            if (WorkbenchPaperState.HasPosition(paper))
                placed[paper.Id] = new Point(paper.WorkbenchX!.Value, paper.WorkbenchY!.Value);
            else
                unplaced.Add(paper);
        }

        var edges = LayoutEdges(section);
        var assigned = new Dictionary<string, Point>(StringComparer.Ordinal);
        if (placed.Count == 0 && unplaced.Count > 0)
        {
            // First visit: the full engine lays the whole canvas out.
            var cards = LayoutCards(section, includePositions: false);
            var width = WorkbenchLayout.PreferredWidth(cards, edges, ViewportAspect());
            foreach (var (id, origin) in WorkbenchLayout.Arrange(cards, edges, width)) assigned[id] = origin;
        }
        else if (unplaced.Count > 0)
        {
            var rects = placed.ToDictionary(pair => pair.Key, pair => WorkbenchLayout.CardRect(pair.Value),
                StringComparer.Ordinal);
            var contentWidth = Math.Max(WorkbenchLayout.MinCanvasWidth,
                rects.Values.Max(rect => rect.Right)) - WorkbenchLayout.CanvasPadding;
            foreach (var paper in unplaced)
            {
                var linked = edges
                    .Where(edge => edge.A == paper.Id || edge.B == paper.Id)
                    .Select(edge => edge.A == paper.Id ? edge.B : edge.A)
                    .ToArray();
                var group = section.Kinds[paper.Id].GroupKey;
                var sameGroup = section.Kinds
                    .Where(pair => pair.Key != paper.Id && pair.Value.GroupKey == group)
                    .Select(pair => pair.Key)
                    .ToArray();
                var origin = WorkbenchLayout.PlaceNew(rects, linked, sameGroup, contentWidth);
                rects[paper.Id] = WorkbenchLayout.CardRect(origin);
                assigned[paper.Id] = origin;
            }
        }

        foreach (var (id, origin) in placed) section.Origins[id] = origin;
        foreach (var (id, origin) in assigned) section.Origins[id] = origin;
        // Only the live workspace persists what the engine chose. Previews stay read-only.
        if (section.IsLive && assigned.Count > 0) _controller.SetWorkbenchOrigins(assigned);
    }

    private IReadOnlyList<WorkbenchLayoutCard> LayoutCards(WorkbenchSection section, bool includePositions) =>
        section.Papers
            .Select(paper => new WorkbenchLayoutCard(
                paper.Id,
                section.Kinds[paper.Id].Kind,
                section.Kinds[paper.Id].GroupKey,
                includePositions && section.Origins.TryGetValue(paper.Id, out var origin) ? origin : null))
            .ToArray();

    private static IReadOnlyList<(string A, string B)> LayoutEdges(WorkbenchSection section) =>
        WorkbenchPaperState.Pairs(section.Papers)
            .Concat(WorkbenchPaperState.TodoLinkPairs(section.Papers).Select(pair => (pair.Todo, pair.Linked)))
            .ToArray();

    private double ViewportAspect()
    {
        var width = _viewport.ActualWidth > 0 ? _viewport.ActualWidth : SystemParameters.PrimaryScreenWidth;
        var height = _viewport.ActualHeight > 0 ? _viewport.ActualHeight : SystemParameters.PrimaryScreenHeight;
        return Math.Clamp(width / Math.Max(1, height - 120), 0.6, 3.5);
    }

    // ------------------------------------------------------------------ strong arrange

    private void ArrangeLive()
    {
        if (_live == null || _drag != null) return;
        CommitFocusedEditor();
        var section = _live;
        var cards = LayoutCards(section, includePositions: true);
        var edges = LayoutEdges(section);
        var width = WorkbenchLayout.PreferredWidth(cards, edges, ViewportAspect());
        var arranged = WorkbenchLayout.Arrange(cards, edges, width);
        if (arranged.Count == 0) return;

        _undoArrange = new Dictionary<string, Point>(section.Origins, StringComparer.Ordinal);
        _undoArrangeButton.Visibility = Visibility.Visible;
        _controller.SetWorkbenchOrigins(arranged);
        section.Extension = default;
        MoveCards(section, arranged);
        ShowStatus(Strings.Get("WorkbenchArranged"));
    }

    private void UndoArrange()
    {
        if (_live == null || _undoArrange == null || _drag != null) return;
        var restore = _undoArrange
            .Where(pair => _live.Cards.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        _undoArrange = null;
        _undoArrangeButton.Visibility = Visibility.Collapsed;
        _controller.SetWorkbenchOrigins(restore);
        MoveCards(_live, restore);
    }

    private void MoveCards(WorkbenchSection section, IReadOnlyDictionary<string, Point> origins)
    {
        foreach (var (id, origin) in origins) section.Origins[id] = origin;
        section.CanvasSize = WorkbenchLayout.CanvasSize(section.Origins.Values, section.Extension);
        ApplySectionSize(section);
        LayoutWorld();
        var animate = _controller.State.EnableAnimations;
        foreach (var (id, origin) in origins)
        {
            if (!section.Cards.TryGetValue(id, out var card)) continue;
            if (animate)
            {
                Animate(card, Canvas.LeftProperty, origin.X);
                Animate(card, Canvas.TopProperty, origin.Y);
            }
            else
            {
                card.BeginAnimation(Canvas.LeftProperty, null);
                card.BeginAnimation(Canvas.TopProperty, null);
                Canvas.SetLeft(card, origin.X);
                Canvas.SetTop(card, origin.Y);
            }
        }
        if (animate)
        {
            // Lines and regions follow the final layout once the cards have arrived.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!_closing) RefreshDecorations(section);
            };
            timer.Start();
        }
        RefreshDecorations(section);
    }

    private static void Animate(UIElement element, DependencyProperty property, double to)
    {
        var from = (double)element.GetValue(property);
        element.BeginAnimation(property, null);
        element.SetValue(property, to);
        if (!double.IsFinite(from) || Math.Abs(from - to) < 0.5) return;
        // The base value is already final; the clock only draws the way there.
        element.BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }

    // ------------------------------------------------------------------ regions & links

    private void RefreshDecorations(WorkbenchSection section)
    {
        DrawRegions(section);
        DrawLinks(section);
    }

    private void DrawRegions(WorkbenchSection section)
    {
        section.RegionLayer.Children.Clear();
        var cards = section.Origins
            .Select(pair => (pair.Key, WorkbenchLayout.CardRect(pair.Value)))
            .ToArray();
        var edges = LayoutEdges(section);
        foreach (var region in WorkbenchLayout.Regions(cards))
        {
            var frame = new Rectangle
            {
                Width = region.Bounds.Width,
                Height = region.Bounds.Height,
                RadiusX = 18,
                RadiusY = 18,
                Stroke = Theme.WorkbenchRegionBrush,
                StrokeThickness = 1.6,
                StrokeDashArray = new DoubleCollection { 6, 4 },
                Fill = Theme.WorkbenchRegionFillBrush
            };
            Canvas.SetLeft(frame, region.Bounds.Left);
            Canvas.SetTop(frame, region.Bounds.Top);
            section.RegionLayer.Children.Add(frame);

            var label = new TextBlock
            {
                Text = RegionLabel(section, region, edges),
                Foreground = Theme.WeakTextBrush,
                FontSize = AppTypography.Scale(12),
                Background = Theme.WorkbenchCanvasBrush,
                Padding = new Thickness(6, 0, 6, 0)
            };
            Canvas.SetLeft(label, region.Bounds.Left + 16);
            Canvas.SetTop(label, region.Bounds.Top - 9);
            section.RegionLayer.Children.Add(label);
        }
    }

    private string RegionLabel(WorkbenchSection section, WorkbenchRegion region,
        IReadOnlyList<(string A, string B)> edges)
    {
        var count = region.CardIds.Count;
        var groups = region.CardIds
            .Select(id => section.Kinds.TryGetValue(id, out var kind) ? kind : default)
            .Distinct()
            .ToArray();
        string? name = null;
        if (groups.Length == 1)
        {
            name = groups[0].Kind switch
            {
                WorkbenchCardKind.Todo => Strings.Get("WorkbenchRegionTodo"),
                WorkbenchCardKind.Note => Strings.Get("WorkbenchRegionNote"),
                WorkbenchCardKind.Tethered => Strings.Get("WorkbenchRegionTethered"),
                _ => section.Papers.FirstOrDefault(paper => paper.Id == region.CardIds[0]) is { } plugin
                    ? _controller.WorkbenchPluginName(plugin)
                    : null
            };
        }
        if (name == null && IsConnected(region.CardIds, edges)) name = Strings.Get("WorkbenchRegionLinked");
        return name == null ? count.ToString() : Strings.Format("WorkbenchRegionLabel", name, count);
    }

    private static bool IsConnected(IReadOnlyList<string> ids, IReadOnlyList<(string A, string B)> edges)
    {
        if (ids.Count < 2) return false;
        var members = ids.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { ids[0] };
        var queue = new Queue<string>();
        queue.Enqueue(ids[0]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var (a, b) in edges)
            {
                var next = a == current ? b : b == current ? a : null;
                if (next != null && members.Contains(next) && seen.Add(next)) queue.Enqueue(next);
            }
        }
        return seen.Count == members.Count;
    }

    private void DrawLinks(WorkbenchSection section)
    {
        section.LinkLayer.Children.Clear();
        var byId = PapersById(section);
        foreach (var (todo, linked) in WorkbenchPaperState.TodoLinkPairs(section.Papers))
        {
            if (!section.Origins.TryGetValue(todo, out var a) || !section.Origins.TryGetValue(linked, out var b)) continue;
            var path = new Path
            {
                Data = LinkGeometry(a, b),
                Stroke = Theme.WeakTextBrush,
                StrokeThickness = 1.4,
                StrokeDashArray = new DoubleCollection { 3, 3 },
                Opacity = 0.75,
                IsHitTestVisible = false
            };
            section.LinkLayer.Children.Add(path);
        }

        foreach (var (first, second) in WorkbenchPaperState.Pairs(section.Papers))
        {
            if (!section.Origins.TryGetValue(first, out var a) || !section.Origins.TryGetValue(second, out var b)) continue;
            var geometry = LinkGeometry(a, b);
            var visible = new Path
            {
                Data = geometry,
                Stroke = Theme.ActiveBrush,
                StrokeThickness = 2,
                Opacity = 0.8,
                IsHitTestVisible = false
            };
            section.LinkLayer.Children.Add(visible);
            if (!section.IsLive) continue;

            var hit = new Path
            {
                Data = geometry,
                Stroke = Brushes.Transparent,
                StrokeThickness = 12,
                Cursor = Cursors.Hand,
                ToolTip = Strings.Format("WorkbenchLinkTip",
                    _controller.WorkbenchTitle(byId[first], live: true, section.Papers),
                    _controller.WorkbenchTitle(byId[second], live: true, section.Papers))
            };
            hit.MouseEnter += (_, _) => { visible.StrokeThickness = 3.2; visible.Opacity = 1; };
            hit.MouseLeave += (_, _) => { visible.StrokeThickness = 2; visible.Opacity = 0.8; };
            var menu = new ContextMenu();
            var unlink = new MenuItem { Header = Strings.Get("WorkbenchUnlink") };
            unlink.Click += (_, _) =>
            {
                if (_controller.UnlinkWorkbenchPapers(byId[first], byId[second])) RefreshDecorations(section);
            };
            menu.Items.Add(unlink);
            AttachMenu(hit, menu);
            section.LinkLayer.Children.Add(hit);
        }
    }

    /// <summary>A soft mind-map curve between the facing sides of two cards.</summary>
    private static Geometry LinkGeometry(Point a, Point b)
    {
        var ra = WorkbenchLayout.CardRect(a);
        var rb = WorkbenchLayout.CardRect(b);
        var ca = new Point(ra.Left + ra.Width / 2, ra.Top + ra.Height / 2);
        var cb = new Point(rb.Left + rb.Width / 2, rb.Top + rb.Height / 2);
        Point start, end, c1, c2;
        if (Math.Abs(cb.X - ca.X) >= Math.Abs(cb.Y - ca.Y) * 1.2)
        {
            var dir = cb.X >= ca.X ? 1 : -1;
            start = new Point(dir > 0 ? ra.Right : ra.Left, ca.Y);
            end = new Point(dir > 0 ? rb.Left : rb.Right, cb.Y);
            var bend = Math.Max(30, Math.Abs(end.X - start.X) / 2);
            c1 = new Point(start.X + dir * bend, start.Y);
            c2 = new Point(end.X - dir * bend, end.Y);
        }
        else
        {
            var dir = cb.Y >= ca.Y ? 1 : -1;
            start = new Point(ca.X, dir > 0 ? ra.Bottom : ra.Top);
            end = new Point(cb.X, dir > 0 ? rb.Top : rb.Bottom);
            var bend = Math.Max(24, Math.Abs(end.Y - start.Y) / 2);
            c1 = new Point(start.X, start.Y + dir * bend);
            c2 = new Point(end.X, end.Y - dir * bend);
        }
        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new BezierSegment(c1, c2, end, isStroked: true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    // ------------------------------------------------------------------ context menus

    private void AttachMenu(FrameworkElement element, ContextMenu menu)
    {
        menu.Opened += (_, _) => _holdOpen++;
        menu.Closed += (_, _) => _holdOpen = Math.Max(0, _holdOpen - 1);
        element.ContextMenu = menu;
    }

    private void OnViewportRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || _live == null || _drag != null) return;
        if (FindAncestor<WorkbenchCard>(e.OriginalSource as DependencyObject) != null) return;
        var point = e.GetPosition(_live.Content);
        if (point.X < 0 || point.Y < 0 || point.X > _live.CanvasSize.Width || point.Y > _live.CanvasSize.Height) return;

        var menu = new ContextMenu();
        var newTodo = new MenuItem { Header = Strings.Get("WorkbenchNewTodoHere") };
        newTodo.Click += (_, _) => CreateAt(PaperTypes.Todo, point);
        var newNote = new MenuItem { Header = Strings.Get("WorkbenchNewNoteHere") };
        newNote.Click += (_, _) => CreateAt(PaperTypes.Note, point);
        var arrange = new MenuItem { Header = Strings.Get("WorkbenchArrange") };
        arrange.Click += (_, _) => ArrangeLive();
        menu.Items.Add(newTodo);
        menu.Items.Add(newNote);
        menu.Items.Add(new Separator());
        menu.Items.Add(arrange);
        menu.Opened += (_, _) => _holdOpen++;
        menu.Closed += (_, _) => _holdOpen = Math.Max(0, _holdOpen - 1);
        menu.PlacementTarget = _viewport;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void CreateAt(string type, Point point)
    {
        if (_live == null) return;
        var section = _live;
        var origin = new Point(point.X - WorkbenchLayout.CardWidth / 2, point.Y - 14);
        origin = WorkbenchLayout.ResolveDrop(origin,
            section.Origins.Values.Select(WorkbenchLayout.CardRect).ToArray(), section.CanvasSize);
        PaperData? paper = null;
        HoldOpen(() =>
        {
            try
            {
                paper = _controller.CreateWorkbenchPaper(type, origin);
            }
            catch (PaperCommandException ex)
            {
                ShowStatus(Strings.Format("WorkbenchEditFailed", ex.Message));
            }
        });
        if (paper == null) return;
        RebuildSection(section);
        if (_live != null && _live.Cards.TryGetValue(paper.Id, out var card))
        {
            Dispatcher.InvokeAsync(() => FocusFirstEditor(card), DispatcherPriority.Input);
        }
    }

    // Preview documents are not validated for identity; the first paper of an id wins.
    private static Dictionary<string, PaperData> PapersById(WorkbenchSection section)
    {
        var byId = new Dictionary<string, PaperData>(StringComparer.Ordinal);
        foreach (var paper in section.Papers) byId.TryAdd(paper.Id, paper);
        return byId;
    }

    private static void FocusFirstEditor(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is System.Windows.Controls.TextBox { IsEnabled: true } editor)
            {
                editor.Focus();
                return;
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
    }
}
