using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

internal sealed class TodoEdgeCapsulePreviewProvider : IEdgeCapsulePreviewProvider
{
    // Keep row creation bounded; totals still describe the complete model and the paper remains
    // the place for browsing the full list.
    internal const int MaximumRenderedItems = 12;
    internal const int MaximumItemCharacters = 512;
    internal const double LinkedTargetButtonSizeDip = 20;

    // Total card width lost before todo text reaches its Grid column:
    // host close/chrome 22 + view margins 19 + items right margin 2 + row padding 7 +
    // checkbox column 24 + text margins 6 = 80 DIPs. Marker width is added separately per row.
    private const double TodoTextLaneFixedReserveDip = 80;

    public static TodoEdgeCapsulePreviewProvider Instance { get; } = new();

    private TodoEdgeCapsulePreviewProvider()
    {
    }

    public EdgeCapsulePreviewDescriptor Describe(EdgeCapsulePreviewContext context)
    {
        var snapshot = CaptureSnapshot(context.Paper);
        var items = snapshot.Items;
        var body = string.Join(
            Environment.NewLine,
            items.Select(item => PreviewItemText(item.Text)));
        var markerReserve = MaximumMarkerReserveDip(items);
        var textLaneReserve = TodoTextLaneFixedReserveDip + markerReserve;
        var width = EdgeCapsulePreviewMeasure.MeasureWidth(
            context.Title,
            body,
            minimum: EdgeCapsulePreviewSize.MinimumWidthDip,
            maximum: 450,
            fixedReserveWidthDip: textLaneReserve);
        // Height estimation must subtract the same fixed lane that width estimation added. Keeping
        // these two calculations on one reserve prevents the renderer from wrapping text that the
        // size estimator believed still had room beside the optional marker controls.
        var availableTextWidth = Math.Max(1, width - textLaneReserve);
        var estimatedLines = items.Count == 0
            ? 1
            : items.Sum(item => Math.Clamp(
                EdgeCapsulePreviewMeasure.EstimateWrappedLines(
                    PreviewItemText(item.Text),
                    availableTextWidth),
                1,
                3));
        var height = items.Count == 0
            ? 120
            : Math.Clamp(
                62 + Math.Min(MaximumRenderedItems, estimatedLines) *
                    AppTypography.Scale(28),
                150,
                400);
        if (items.Count == 0)
        {
            width = Math.Max(130, width);
        }

        return new EdgeCapsulePreviewDescriptor(
            new EdgeCapsulePreviewSize(width, height),
            size => new TodoEdgeCapsulePreviewView(context, size, snapshot));
    }

    internal static TodoEdgeCapsulePreviewSnapshot CaptureSnapshot(PaperData paper)
    {
        var selected = new List<PaperItem>(MaximumRenderedItems);
        var total = 0;
        var done = 0;
        foreach (var item in paper.Items)
        {
            if (!TodoRules.HasMeaningfulContent(item))
            {
                continue;
            }

            total++;
            if (item.Done)
            {
                done++;
            }

            // Keep only the stable first 12 items by Order. With a fixed-size insertion list this
            // is one model pass, O(n * 12), and never sorts/materializes the complete todo model.
            var insertionIndex = selected.Count;
            for (var index = 0; index < selected.Count; index++)
            {
                if (item.Order < selected[index].Order)
                {
                    insertionIndex = index;
                    break;
                }
            }
            if (insertionIndex == selected.Count)
            {
                if (selected.Count < MaximumRenderedItems)
                {
                    selected.Add(item);
                }
                continue;
            }

            selected.Insert(insertionIndex, item);
            if (selected.Count > MaximumRenderedItems)
            {
                selected.RemoveAt(MaximumRenderedItems);
            }
        }

        return new TodoEdgeCapsulePreviewSnapshot(selected, total, done);
    }

    internal static string PreviewItemText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "—";
        }

        // Bound work before trimming/measuring. A single malformed or pasted multi-megabyte item
        // must not make opening the preview proportional to its complete text length.
        var truncated = value.Length > MaximumItemCharacters;
        var bounded = truncated
            ? value[..MaximumItemCharacters]
            : value;
        var text = bounded.Trim();
        if (text.Length == 0)
        {
            return truncated ? "…" : "—";
        }
        if (!truncated)
        {
            return text;
        }
        return text.Length < MaximumItemCharacters
            ? text + "…"
            : text[..(MaximumItemCharacters - 1)] + "…";
    }

    private static double MaximumMarkerReserveDip(IReadOnlyList<PaperItem> items)
    {
        var maximum = 0.0;
        foreach (var item in items)
        {
            var reserve = 0.0;
            if (item.ReminderAt.HasValue || item.ReminderTriggered)
            {
                reserve += AppTypography.Scale(10.5) + 2;
            }
            if (HasLinkedTarget(item))
            {
                // 20 DIP button + its horizontal margin. The final 2 DIP below belongs to the
                // marker lane itself and is shared with a reminder marker on the same row.
                reserve += LinkedTargetButtonSizeDip + 2;
            }
            if (reserve > 0)
            {
                reserve += 2;
            }
            maximum = Math.Max(maximum, reserve);
        }
        return maximum;
    }

    private static bool HasLinkedTarget(PaperItem item) =>
        !string.IsNullOrWhiteSpace(item.LinkedPaperId) ||
        !string.IsNullOrWhiteSpace(item.LinkedPath);
}

internal sealed record TodoEdgeCapsulePreviewSnapshot(
    IReadOnlyList<PaperItem> Items,
    int Total,
    int Done);

internal sealed class TodoEdgeCapsulePreviewView : EdgeCapsuleLivePreviewView
{
    private readonly TextBlock _title;
    private readonly TextBlock _summary;
    private readonly StackPanel _items;
    private readonly TodoEdgeCapsulePreviewViewport _viewport;
    private bool _rebuilding;
    private TodoEdgeCapsulePreviewSnapshot? _initialSnapshot;

    public TodoEdgeCapsulePreviewView(
        EdgeCapsulePreviewContext context,
        EdgeCapsulePreviewSize size,
        TodoEdgeCapsulePreviewSnapshot initialSnapshot)
        : base(context, size)
    {
        _initialSnapshot = initialSnapshot;
        Margin = new Thickness(10, 9, 9, 10);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        var heading = new Grid
        {
            Margin = new Thickness(2, 0, 1, 7)
        };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition());

        _title = new TextBlock
        {
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(13),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = Math.Max(48, size.WidthDip - 86)
        };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushKey");
        heading.Children.Add(_title);

        _summary = new TextBlock
        {
            Margin = new Thickness(6, 0, 0, 0),
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(11),
            VerticalAlignment = VerticalAlignment.Center
        };
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        Grid.SetColumn(_summary, 1);
        heading.Children.Add(_summary);
        Children.Add(heading);

        _items = new StackPanel
        {
            Margin = new Thickness(0, 0, 2, 0)
        };
        _viewport = new TodoEdgeCapsulePreviewViewport(_items);
        Grid.SetRow(_viewport, 1);
        Children.Add(_viewport);

        InitializeLiveContent();
    }

    protected override void RebuildContent()
    {
        var snapshot = _initialSnapshot ??
            TodoEdgeCapsulePreviewProvider.CaptureSnapshot(Context.Paper);
        _initialSnapshot = null;
        var meaningful = snapshot.Items;

        _title.Text = Context.Title;
        _title.ToolTip = Context.Title;
        _summary.Text = $"{snapshot.Done}/{snapshot.Total}";

        _rebuilding = true;
        try
        {
            _items.Children.Clear();
            if (meaningful.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = "—",
                    Margin = new Thickness(8, 18, 8, 8),
                    FontFamily = AppTypography.UiFontFamily,
                    FontSize = AppTypography.Scale(16),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
                _items.Children.Add(empty);
            }
            else
            {
                foreach (var item in meaningful)
                {
                    _items.Children.Add(BuildRow(item));
                }
            }
        }
        finally
        {
            _rebuilding = false;
        }

        _viewport.SetSourceTruncated(snapshot.Total > meaningful.Count);
    }

    private FrameworkElement BuildRow(PaperItem item)
    {
        var row = new Border
        {
            Margin = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(3, 3, 4, 3),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent
        };
        row.MouseEnter += (_, _) =>
            row.SetResourceReference(Border.BackgroundProperty, "HoverBrushKey");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new CheckBox
        {
            IsChecked = item.Done,
            Width = 20,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
            Cursor = Cursors.Hand,
            Focusable = false,
            FocusVisualStyle = null,
            Style = Context.ReadTodoCheckStyle()
        };
        EdgeCapsulePreviewInteraction.SetConsumesPointer(check, true);
        check.Click += (_, _) =>
        {
            if (_rebuilding)
            {
                return;
            }

            var requested = check.IsChecked == true;
            if (!Context.SetTodoDone(item.Id, requested))
            {
                _rebuilding = true;
                check.IsChecked = item.Done;
                _rebuilding = false;
                return;
            }
        };
        grid.Children.Add(check);

        var text = new TextBlock
        {
            Text = TodoEdgeCapsulePreviewProvider.PreviewItemText(item.Text),
            Margin = new Thickness(1, 0, 5, 0),
            FontFamily = AppTypography.FontFamilyFor(content: true, bold: false),
            FontSize = AppTypography.Scale(12),
            FontWeight = FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.SetResourceReference(
            TextBlock.ForegroundProperty,
            item.Done ? "WeakTextBrushKey" : "TextBrushKey");
        if (item.Done)
        {
            text.TextDecorations = TextDecorations.Strikethrough;
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var marker = BuildItemMarker(item);
        Grid.SetColumn(marker, 2);
        grid.Children.Add(marker);

        row.Child = grid;
        return row;
    }

    private FrameworkElement BuildItemMarker(PaperItem item)
    {
        var markers = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(1, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        if (item.ReminderAt.HasValue || item.ReminderTriggered)
        {
            markers.Children.Add(CreateMarkerText("◷"));
        }

        string? linkedMarker = null;
        if (!string.IsNullOrWhiteSpace(item.LinkedPaperId))
        {
            linkedMarker = "↗";
        }
        else if (!string.IsNullOrWhiteSpace(item.LinkedPath))
        {
            linkedMarker = "⌁";
        }

        if (linkedMarker != null)
        {
            var glyph = CreateMarkerText(linkedMarker);
            glyph.Margin = new Thickness(0);
            var link = new Button
            {
                Content = glyph,
                Width = TodoEdgeCapsulePreviewProvider.LinkedTargetButtonSizeDip,
                Height = TodoEdgeCapsulePreviewProvider.LinkedTargetButtonSizeDip,
                Margin = new Thickness(1, 0, 1, 0),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                FocusVisualStyle = null,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            EdgeCapsulePreviewInteraction.SetConsumesPointer(link, true);
            link.MouseEnter += (_, _) =>
            {
                link.SetResourceReference(
                    Control.BackgroundProperty,
                    "HoverBrushKey");
                glyph.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    "LinkBrushKey");
            };
            link.MouseLeave += (_, _) =>
            {
                link.Background = Brushes.Transparent;
                glyph.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    "WeakTextBrushKey");
            };
            link.Click += (_, e) =>
            {
                Context.OpenTodoLinkedTarget(item.Id);
                e.Handled = true;
            };
            markers.Children.Add(link);
        }

        return markers;
    }

    private static TextBlock CreateMarkerText(string text)
    {
        var marker = new TextBlock
        {
            Text = text,
            Margin = new Thickness(1, 0, 1, 0),
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = AppTypography.Scale(10.5),
            VerticalAlignment = VerticalAlignment.Center
        };
        marker.SetResourceReference(
            TextBlock.ForegroundProperty,
            "WeakTextBrushKey");
        return marker;
    }
}

// A fixed top excerpt, not a hidden ScrollViewer. No offset, keyboard scrolling, wheel scrolling
// or BringIntoView scrolling exists; clipping also keeps off-card controls out of pointer hit tests.
internal sealed class TodoEdgeCapsulePreviewViewport : Panel
{
    private readonly StackPanel _items;
    private readonly TextBlock _overflowIndicator;
    private readonly RectangleGeometry _itemsClip = new();
    private bool _sourceTruncated;

    public TodoEdgeCapsulePreviewViewport(StackPanel items)
    {
        ClipToBounds = true;
        _items = items;
        _items.Clip = _itemsClip;
        _overflowIndicator = new TextBlock
        {
            Text = "…",
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(14),
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false
        };
        _overflowIndicator.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        Children.Add(_items);
        Children.Add(_overflowIndicator);
    }

    public void SetSourceTruncated(bool truncated)
    {
        _sourceTruncated = truncated;
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var naturalSize = new Size(availableSize.Width, double.PositiveInfinity);
        _items.Measure(naturalSize);
        _overflowIndicator.Measure(naturalSize);
        return new Size(
            Math.Min(availableSize.Width, Math.Max(_items.DesiredSize.Width, _overflowIndicator.DesiredSize.Width)),
            Math.Min(availableSize.Height, _items.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var overflow = _sourceTruncated || _items.DesiredSize.Height > finalSize.Height;
        var indicatorHeight = overflow ? Math.Min(finalSize.Height, _overflowIndicator.DesiredSize.Height) : 0;
        var visibleHeight = finalSize.Height - indicatorHeight;
        _itemsClip.Rect = new Rect(0, 0, finalSize.Width, visibleHeight);
        _items.Arrange(new Rect(0, 0, finalSize.Width, _items.DesiredSize.Height));
        _overflowIndicator.Opacity = overflow ? 1 : 0;
        _overflowIndicator.Arrange(new Rect(0, visibleHeight, finalSize.Width, indicatorHeight));
        return finalSize;
    }
}
