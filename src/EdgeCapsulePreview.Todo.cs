using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

internal sealed class TodoEdgeCapsulePreviewProvider : IEdgeCapsulePreviewProvider
{
    // Keep row creation bounded; totals still describe the complete model and the paper remains
    // the place for browsing the full list.
    internal const int MaximumRenderedItems = 12;
    internal const int MaximumItemCharacters = 512;

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
        var width = EdgeCapsulePreviewMeasure.MeasureWidth(
            context.Title,
            body,
            minimum: EdgeCapsulePreviewSize.MinimumWidthDip,
            maximum: 450);
        width = Math.Max(items.Count == 0 ? 130 : 180, width);
        var availableTextWidth = Math.Max(64, width - 60);
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
                104 + Math.Min(MaximumRenderedItems, estimatedLines) *
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

    internal static TodoPreviewSelection<PaperItem> CaptureSnapshot(
        PaperData paper,
        bool incompleteOnly = false) =>
        TodoPreviewSelection.Capture(
            paper.Items,
            TodoRules.HasMeaningfulContent,
            item => item.Done,
            item => item.Order,
            incompleteOnly,
            MaximumRenderedItems);

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
}

internal sealed class TodoEdgeCapsulePreviewView : EdgeCapsuleLivePreviewView
{
    private readonly TextBlock _title;
    private readonly TextBlock _summary;
    private readonly StackPanel _items;
    private readonly ScrollViewer _scrollViewer;
    private readonly ToggleButton _filter;
    private readonly TextBlock _openPaper;
    private bool _incompleteOnly;
    private bool _scrollToStart;
    private int _contentGeneration;
    private bool _rebuilding;
    private TodoPreviewSelection<PaperItem>? _initialSnapshot;

    public TodoEdgeCapsulePreviewView(
        EdgeCapsulePreviewContext context,
        EdgeCapsulePreviewSize size,
        TodoPreviewSelection<PaperItem> initialSnapshot)
        : base(context, size)
    {
        _initialSnapshot = initialSnapshot;
        Margin = new Thickness(10, 9, 9, 10);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

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

        _filter = CreateIncompleteFilter();
        Grid.SetRow(_filter, 1);
        Children.Add(_filter);

        _items = new StackPanel
        {
            Margin = new Thickness(0, 0, 2, 0)
        };
        _scrollViewer = new ScrollViewer
        {
            Content = _items,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Padding = new Thickness(0)
        };
        Grid.SetRow(_scrollViewer, 2);
        Children.Add(_scrollViewer);

        // Deliberately non-consuming: the existing host-owned background click opens the paper.
        // Do not add a second activation path or close/recreate the preview on filter changes.
        _openPaper = new TextBlock
        {
            Margin = new Thickness(3, 5, 1, 0),
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(11),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Cursor = Cursors.Hand,
            ToolTip = InteractionStrings.Format(
                "OpenPaperTip", TodoEdgeCapsulePreviewProvider.MaximumRenderedItems)
        };
        _openPaper.SetResourceReference(TextBlock.ForegroundProperty, "LinkBrushKey");
        Grid.SetRow(_openPaper, 3);
        Children.Add(_openPaper);

        InitializeLiveContent();
    }

    private ToggleButton CreateIncompleteFilter()
    {
        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        chrome.SetBinding(Border.BackgroundProperty,
            new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        chrome.SetBinding(Border.PaddingProperty,
            new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
        chrome.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var style = new Style(typeof(ToggleButton));
        style.Setters.Add(new Setter(Control.TemplateProperty,
            new ControlTemplate(typeof(ToggleButton)) { VisualTree = chrome }));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        foreach (var property in new[] { IsMouseOverProperty, ToggleButton.IsCheckedProperty })
        {
            var trigger = new Trigger { Property = property, Value = true };
            trigger.Setters.Add(new Setter(Control.BackgroundProperty,
                new DynamicResourceExtension("HoverBrushKey")));
            style.Triggers.Add(trigger);
        }
        var filter = new ToggleButton
        {
            Content = InteractionStrings.Get("OnlyIncomplete"),
            ToolTip = InteractionStrings.Get("OnlyIncompleteTip"),
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(11),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 0, 4),
            Cursor = Cursors.Hand,
            Focusable = false,
            IsTabStop = false,
            Style = style
        };
        filter.SetResourceReference(Control.ForegroundProperty, "TextBrushKey");
        System.Windows.Automation.AutomationProperties.SetName(
            filter, InteractionStrings.Get("OnlyIncomplete"));
        EdgeCapsulePreviewInteraction.SetConsumesPointer(filter, true);
        filter.Click += (_, _) =>
        {
            _incompleteOnly = filter.IsChecked == true;
            filter.Content = (_incompleteOnly ? "✓ " : "") + InteractionStrings.Get("OnlyIncomplete");
            _initialSnapshot = null;
            _scrollToStart = true;
            InitializeLiveContent();
        };
        return filter;
    }

    protected override void RebuildContent()
    {
        var generation = ++_contentGeneration;
        var offset = _scrollToStart ? 0 : _scrollViewer.VerticalOffset;
        _scrollToStart = false;
        var snapshot = _initialSnapshot ??
            TodoEdgeCapsulePreviewProvider.CaptureSnapshot(Context.Paper, _incompleteOnly);
        _initialSnapshot = null;
        var meaningful = snapshot.Items;

        _title.Text = Context.Title;
        _title.ToolTip = Context.Title;
        _summary.Text = $"{snapshot.Done}/{snapshot.Total}";
        _summary.ToolTip = InteractionStrings.Format("CompletionSummary", snapshot.Done, snapshot.Total);
        _filter.Visibility = snapshot.Total == 0 ? Visibility.Collapsed : Visibility.Visible;
        _openPaper.Text = snapshot.RemainingCount > 0
            ? InteractionStrings.Format("MoreItems", snapshot.RemainingCount)
            : InteractionStrings.Get("OpenPaper");

        _rebuilding = true;
        try
        {
            _items.Children.Clear();
            if (meaningful.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = InteractionStrings.Get(
                        _incompleteOnly && snapshot.Total > 0 ? "AllCompleted" : "NoItems"),
                    Margin = new Thickness(8, 18, 8, 8),
                    FontFamily = AppTypography.UiFontFamily,
                    FontSize = AppTypography.Scale(12),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
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

        Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (IsLoaded && generation == _contentGeneration)
                {
                    _scrollViewer.ScrollToVerticalOffset(offset);
                }
            }),
            DispatcherPriority.Loaded);
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
            var link = CreateMarkerText(linkedMarker);
            link.Cursor = Cursors.Hand;
            EdgeCapsulePreviewInteraction.SetConsumesPointer(link, true);
            link.MouseEnter += (_, _) =>
                link.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    "LinkBrushKey");
            link.MouseLeave += (_, _) =>
                link.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    "WeakTextBrushKey");
            link.MouseLeftButtonUp += (_, e) =>
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
