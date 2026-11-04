using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ContextMenu = System.Windows.Controls.ContextMenu;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MenuItem = System.Windows.Controls.MenuItem;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace PaperTodo;

internal sealed partial class WorkbenchWindow
{
    private const double AutoPanMargin = 40;

    private sealed class CardDrag
    {
        public required WorkbenchSection Section { get; init; }
        public required WorkbenchCard Card { get; init; }
        public required Vector GrabOffset { get; init; }
        public required Point PressPosition { get; init; }
        public bool Moved { get; set; }
        public Point Origin { get; set; }
        public WorkbenchEdgeState Edge { get; set; }
        public WorkbenchSection? Target { get; set; }
        public Dictionary<int, string?> Blockers { get; } = new();
        public DateTime LastTick { get; set; } = DateTime.UtcNow;
    }

    private sealed class LinkDrag
    {
        public required WorkbenchSection Section { get; init; }
        public required WorkbenchCard Source { get; init; }
        public required Path Line { get; init; }
        public WorkbenchCard? Target { get; set; }
    }

    private readonly DispatcherTimer _dragTimer;
    private CardDrag? _drag;
    private LinkDrag? _linkDrag;
    private int _topZ = 1000;

    private void AttachCardInteraction(WorkbenchSection section, WorkbenchCard card)
    {
        var header = card.DragArea;
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.Handled || card.IsEditingTitle) return;
            if (e.ClickCount == 2)
            {
                CancelCardDrag(restore: true);
                card.BeginTitleEdit();
                e.Handled = true;
                return;
            }
            CommitFocusedEditor();
            var pointer = e.GetPosition(section.Content);
            var origin = section.Origins[card.Paper.Id];
            _drag = new CardDrag
            {
                Section = section,
                Card = card,
                GrabOffset = pointer - origin,
                PressPosition = e.GetPosition(_viewport),
                Origin = origin
            };
            Panel.SetZIndex(card, ++_topZ);
            header.CaptureMouse();
            e.Handled = true;
        };
        header.MouseMove += (_, _) =>
        {
            if (_drag?.Card != card) return;
            UpdateCardDrag(extend: false);
        };
        header.MouseLeftButtonUp += (_, e) =>
        {
            if (_drag?.Card != card) return;
            e.Handled = true;
            FinishCardDrag();
        };
        header.LostMouseCapture += (_, _) =>
        {
            if (_drag?.Card == card) FinishCardDrag();
        };

        var handle = card.LinkHandle;
        handle.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            CommitFocusedEditor();
            BeginLinkDrag(section, card);
            handle.CaptureMouse();
        };
        handle.MouseMove += (_, e) =>
        {
            if (_linkDrag?.Source == card) UpdateLinkDrag(e.GetPosition(section.Content));
        };
        handle.MouseLeftButtonUp += (_, e) =>
        {
            if (_linkDrag?.Source != card) return;
            e.Handled = true;
            // Finishing releases the capture itself, after the drop target was read.
            FinishLinkDrag();
        };
        handle.LostMouseCapture += (_, _) =>
        {
            if (_linkDrag?.Source == card) CancelLinkDrag();
        };

        var menu = new ContextMenu();
        var open = new MenuItem { Header = Strings.Get("WorkbenchOpenPaper") };
        open.Click += (_, _) => _controller.OpenPaperFromWorkbench(card.Paper);
        var rename = new MenuItem { Header = Strings.Get("WorkbenchRename") };
        rename.Click += (_, _) => card.BeginTitleEdit();
        var unlinkAll = new MenuItem { Header = Strings.Get("WorkbenchUnlinkAll") };
        unlinkAll.Click += (_, _) =>
        {
            if (_controller.ClearWorkbenchLinks(card.Paper)) RefreshDecorations(section);
        };
        menu.Opened += (_, _) => unlinkAll.IsEnabled =
            WorkbenchPaperState.Pairs(section.Papers).Any(pair => pair.A == card.Paper.Id || pair.B == card.Paper.Id);
        menu.Items.Add(open);
        menu.Items.Add(rename);
        menu.Items.Add(new Separator());
        menu.Items.Add(unlinkAll);
        AttachMenu(header, menu);
    }

    // ------------------------------------------------------------------ card drag

    private void UpdateCardDrag(bool extend)
    {
        if (_drag is not { } drag) return;
        var section = drag.Section;
        if (!drag.Moved)
        {
            var press = drag.PressPosition;
            var now = Mouse.GetPosition(_viewport);
            if (Math.Abs(now.X - press.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(now.Y - press.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }
            drag.Moved = true;
            drag.LastTick = DateTime.UtcNow;
            _dragTimer.Start();
        }

        var pointer = Mouse.GetPosition(section.Content);
        var index = _sections.IndexOf(section);
        var edge = WorkbenchLayout.EvaluateEdge(
            pointer,
            section.CanvasSize,
            hasLeftNeighbor: index > 0,
            hasRightNeighbor: index >= 0 && index < _sections.Count - 1);
        drag.Edge = edge;

        if (extend && edge.Extends)
        {
            var now = DateTime.UtcNow;
            var dt = Math.Clamp((now - drag.LastTick).TotalSeconds, 0, 0.05);
            var grow = WorkbenchLayout.ExtendSpeed * dt;
            var size = section.CanvasSize;
            section.Extension = new Size(
                edge.ExtendRight ? size.Width + grow : Math.Max(section.Extension.Width, size.Width),
                edge.ExtendDown ? size.Height + grow : Math.Max(section.Extension.Height, size.Height));
            section.CanvasSize = WorkbenchLayout.CanvasSize(section.Origins.Values, section.Extension);
            ApplySectionSize(section);
            LayoutWorld();
        }

        var origin = WorkbenchLayout.ClampOrigin(pointer - drag.GrabOffset, section.CanvasSize);
        drag.Origin = origin;
        section.Origins[drag.Card.Paper.Id] = origin;
        Canvas.SetLeft(drag.Card, origin.X);
        Canvas.SetTop(drag.Card, origin.Y);
        RefreshDecorations(section);
        ShowCrossing(drag, edge.Cross == 0 ? null : _sections[index + edge.Cross]);
    }

    private void OnDragTick()
    {
        if (_drag is not { Moved: true } drag)
        {
            _dragTimer.Stop();
            return;
        }
        var now = DateTime.UtcNow;
        var dt = Math.Clamp((now - drag.LastTick).TotalSeconds, 0, 0.05);
        AutoPan(dt);
        UpdateCardDrag(extend: true);
        drag.LastTick = now;
    }

    // Auto-pan runs below ExtendRatio × ExtendSpeed so a pointer held at the window edge keeps
    // growing the canvas and settles near 90% instead of drifting into the crossing zone.
    private void AutoPan(double dt)
    {
        var pointer = Mouse.GetPosition(_viewport);
        var width = _viewport.ActualWidth;
        var height = _viewport.ActualHeight;
        var step = WorkbenchLayout.AutoPanSpeed * dt * _scale.ScaleX;
        var dx = pointer.X > width - AutoPanMargin ? -step : pointer.X < AutoPanMargin ? step : 0;
        var dy = pointer.Y > height - AutoPanMargin ? -step : pointer.Y < AutoPanMargin + 50 ? step : 0;
        if (dx == 0 && dy == 0) return;
        _translate.X += dx;
        _translate.Y += dy;
        ClampTranslate();
    }

    private void ShowCrossing(CardDrag drag, WorkbenchSection? target)
    {
        if (ReferenceEquals(drag.Target, target)) return;
        if (drag.Target != null) drag.Target.Prompt.Visibility = Visibility.Collapsed;
        drag.Target = target;
        drag.Card.SetCrossing(target != null);
        if (target == null) return;

        if (!drag.Blockers.TryGetValue(target.Number, out var blocker))
        {
            blocker = _controller.WorkbenchTransferBlocker(drag.Card.Paper, target.Number);
            drag.Blockers[target.Number] = blocker;
        }
        var label = AppController.WorkbenchWorkspaceLabel(target.Number);
        target.Prompt.BorderThickness = new Thickness(4);
        target.Prompt.BorderBrush = blocker == null ? Theme.ActiveBrush : Theme.DangerBrush;
        target.Prompt.Background = blocker == null ? Theme.Tint(40) : Theme.Danger(26);
        target.Prompt.Child = new TextBlock
        {
            Text = blocker == null
                ? Strings.Format("WorkbenchDropToWorkspace", label)
                : Strings.Format("WorkbenchCannotMove", label, blocker),
            Foreground = blocker == null ? Theme.TextBrush : Theme.DangerBrush,
            FontSize = AppTypography.Scale(30),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(60),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        target.Prompt.Visibility = Visibility.Visible;
    }

    private void FinishCardDrag()
    {
        if (_drag is not { } drag) return;
        _drag = null;
        _dragTimer.Stop();
        if (drag.Card.DragArea.IsMouseCaptured) drag.Card.DragArea.ReleaseMouseCapture();
        if (!drag.Moved)
        {
            drag.Card.Focus();
            return;
        }

        var section = drag.Section;
        var target = drag.Target;
        if (target != null)
        {
            target.Prompt.Visibility = Visibility.Collapsed;
            drag.Card.SetCrossing(false);
            if (drag.Blockers.GetValueOrDefault(target.Number) == null && TryMoveAcross(drag, target)) return;
        }

        var others = section.Origins
            .Where(pair => pair.Key != drag.Card.Paper.Id)
            .Select(pair => WorkbenchLayout.CardRect(pair.Value))
            .ToArray();
        var final = WorkbenchLayout.ResolveDrop(drag.Origin, others, section.CanvasSize);
        _controller.SetWorkbenchOrigins(new Dictionary<string, Point>(StringComparer.Ordinal)
        {
            [drag.Card.Paper.Id] = final
        });
        MoveCards(section, new Dictionary<string, Point>(StringComparer.Ordinal) { [drag.Card.Paper.Id] = final });
    }

    private bool TryMoveAcross(CardDrag drag, WorkbenchSection target)
    {
        // Land in open space of the target canvas, next to papers of the same kind.
        var rects = new Dictionary<string, Rect>(StringComparer.Ordinal);
        foreach (var (id, origin) in target.Origins) rects[id] = WorkbenchLayout.CardRect(origin);
        var group = drag.Section.Kinds[drag.Card.Paper.Id].GroupKey;
        var sameGroup = target.Kinds.Where(pair => pair.Value.GroupKey == group).Select(pair => pair.Key).ToArray();
        var landing = WorkbenchLayout.PlaceNew(rects, Array.Empty<string>(), sameGroup,
            target.CanvasSize.Width - 2 * WorkbenchLayout.CanvasPadding);

        string message = "";
        var moved = false;
        HoldOpen(() => moved = _controller.TryTransferWorkbenchPaper(drag.Card.Paper, target.Number, landing, out message));
        ShowStatus(message);
        if (!moved) return false;
        _undoArrange = null;
        _undoArrangeButton.Visibility = Visibility.Collapsed;
        ReloadPreview(target.Number);
        if (_live != null) RebuildSection(_live);
        return true;
    }

    private void CancelCardDrag(bool restore)
    {
        if (_drag is not { } drag) return;
        _drag = null;
        _dragTimer.Stop();
        if (drag.Target != null) drag.Target.Prompt.Visibility = Visibility.Collapsed;
        drag.Card.SetCrossing(false);
        if (drag.Card.DragArea.IsMouseCaptured) drag.Card.DragArea.ReleaseMouseCapture();
        if (!restore || drag.Card.Paper.WorkbenchX is not double x || drag.Card.Paper.WorkbenchY is not double y) return;
        MoveCards(drag.Section, new Dictionary<string, Point>(StringComparer.Ordinal) { [drag.Card.Paper.Id] = new Point(x, y) });
    }

    // ------------------------------------------------------------------ link drag

    private void BeginLinkDrag(WorkbenchSection section, WorkbenchCard source)
    {
        CancelLinkDrag();
        var line = new Path
        {
            Stroke = Theme.ActiveBrush,
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false
        };
        section.LinkLayer.Children.Add(line);
        _linkDrag = new LinkDrag { Section = section, Source = source, Line = line };
        UpdateLinkDrag(Mouse.GetPosition(section.Content));
    }

    private void UpdateLinkDrag(Point pointer)
    {
        if (_linkDrag is not { } link) return;
        var section = link.Section;
        var start = section.Origins[link.Source.Paper.Id];
        var from = new Point(start.X + WorkbenchLayout.CardWidth / 2, start.Y + 15);
        var geometry = new LineGeometry(from, pointer);
        geometry.Freeze();
        link.Line.Data = geometry;

        WorkbenchCard? target = null;
        foreach (var (id, origin) in section.Origins)
        {
            if (id == link.Source.Paper.Id) continue;
            if (!WorkbenchLayout.CardRect(origin).Contains(pointer)) continue;
            if (section.Cards.TryGetValue(id, out var card) &&
                (target == null || Panel.GetZIndex(card) > Panel.GetZIndex(target)))
            {
                target = card;
            }
        }
        if (ReferenceEquals(target, link.Target)) return;
        link.Target?.SetLinkTarget(false);
        link.Target = target;
        target?.SetLinkTarget(true);
    }

    private void FinishLinkDrag()
    {
        if (_linkDrag is not { } link) return;
        var target = link.Target;
        CancelLinkDrag();
        if (target == null) return;
        if (_controller.LinkWorkbenchPapers(link.Source.Paper, target.Paper))
        {
            RefreshDecorations(link.Section);
        }
        else if (WorkbenchPaperState.AreLinked(link.Source.Paper, target.Paper))
        {
            ShowStatus(Strings.Get("WorkbenchAlreadyLinked"));
        }
        else
        {
            ShowStatus(Strings.Get("WorkbenchLinkLimit"));
        }
    }

    private void CancelLinkDrag()
    {
        if (_linkDrag is not { } link) return;
        _linkDrag = null;
        link.Target?.SetLinkTarget(false);
        link.Section.LinkLayer.Children.Remove(link.Line);
        if (link.Source.LinkHandle.IsMouseCaptured) link.Source.LinkHandle.ReleaseMouseCapture();
    }
}
