using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// One live preview surface. Size is frozen by the queue session, while the content may refresh
/// from the current paper model. The first content tree is built while detached so the shell can
/// switch from compact text to preview text without ever showing both or exposing a blank frame.
/// </summary>
internal abstract class EdgeCapsuleLivePreviewView : Grid
{
    private DispatcherOperation? _refreshOperation;
    private bool _contentDirty = true;
    private bool _hasRenderedContent;
    private bool _subscribed;
    private bool _refreshing;
    private long _invalidationVersion;
    private Action? _invalidationHandler;

    protected EdgeCapsuleLivePreviewView(
        EdgeCapsulePreviewContext context,
        EdgeCapsulePreviewSize size)
    {
        Context = context;
        PreviewSize = size;
        Background = System.Windows.Media.Brushes.Transparent;
        ClipToBounds = true;

        Loaded += (_, _) =>
        {
            Subscribe();
            QueueRefresh();
        };
        Unloaded += (_, _) =>
        {
            Unsubscribe();
            CancelQueuedRefresh();
            _contentDirty = true;
        };
        IsVisibleChanged += (_, _) => QueueRefresh();
    }

    protected EdgeCapsulePreviewContext Context { get; }
    protected EdgeCapsulePreviewSize PreviewSize { get; }

    protected void InitializeLiveContent()
    {
        _contentDirty = true;
        QueueRefresh();
    }

    internal void PrepareForFirstDisplay()
    {
        if (_hasRenderedContent || _refreshing)
        {
            return;
        }

        // Subscribe before detached preparation. An invalidation between this rebuild and Loaded
        // must leave the view dirty instead of being silently absorbed by the mounting gap.
        Subscribe();
        CancelQueuedRefresh();
        var rebuildVersion = Interlocked.Read(ref _invalidationVersion);
        _contentDirty = false;
        _refreshing = true;
        try
        {
            RebuildContent();
            _hasRenderedContent = true;
        }
        catch
        {
            // A detached first render is optional. Keep the dirty bit so the normal Loaded pass
            // can retry instead of exposing the exception to the edge-capsule transaction.
            _contentDirty = true;
        }
        finally
        {
            _refreshing = false;
            if (Interlocked.Read(ref _invalidationVersion) != rebuildVersion)
            {
                _contentDirty = true;
            }
        }
    }

    protected abstract void RebuildContent();

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        var source = Context.InvalidationSource;
        var weakView = new WeakReference<EdgeCapsuleLivePreviewView>(this);
        Action? handler = null;
        handler = () =>
        {
            if (weakView.TryGetTarget(out var view))
            {
                view.OnContentInvalidated();
            }
            else if (handler != null)
            {
                source.Invalidated -= handler;
            }
        };
        _invalidationHandler = handler;
        source.Invalidated += handler;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        if (_invalidationHandler != null)
        {
            Context.InvalidationSource.Invalidated -= _invalidationHandler;
            _invalidationHandler = null;
        }
        _invalidationHandler = null;
        _subscribed = false;
    }

    private void OnContentInvalidated()
    {
        Interlocked.Increment(ref _invalidationVersion);
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                (Action)MarkContentDirty,
                DispatcherPriority.Background);
            return;
        }

        MarkContentDirty();
    }

    private void MarkContentDirty()
    {
        _contentDirty = true;
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (!_contentDirty ||
            !IsLoaded ||
            !IsVisible ||
            _refreshOperation is { Status: DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing })
        {
            return;
        }

        _refreshOperation = Dispatcher.BeginInvoke(
            (Action)RefreshIfDirty,
            _hasRenderedContent
                ? DispatcherPriority.Background
                : DispatcherPriority.Loaded);
    }

    private void CancelQueuedRefresh()
    {
        if (_refreshOperation is { Status: DispatcherOperationStatus.Pending })
        {
            _refreshOperation.Abort();
        }
        _refreshOperation = null;
    }

    private void RefreshIfDirty()
    {
        _refreshOperation = null;
        if (!_contentDirty || !IsLoaded || !IsVisible || _refreshing)
        {
            return;
        }

        var rebuildVersion = Interlocked.Read(ref _invalidationVersion);
        _contentDirty = false;
        _refreshing = true;
        try
        {
            RebuildContent();
            _hasRenderedContent = true;
        }
        catch
        {
            // The preview is optional. Do not immediately queue the same failing rebuild forever;
            // only a real invalidation that happened during or after this attempt may retry it.
        }
        finally
        {
            _refreshing = false;
            if (Interlocked.Read(ref _invalidationVersion) != rebuildVersion)
            {
                _contentDirty = true;
            }
        }

        QueueRefresh();
    }
}

internal static class EdgeCapsulePreviewMeasure
{
    private const double BaseApproximateGlyphWidthDip = 6.4;
    private const double FixedChromeReserveWidthDip = 72;
    private static double ApproximateGlyphWidthDip =>
        AppTypography.Scale(BaseApproximateGlyphWidthDip);

    public static double MeasureWidth(
        string? title,
        string? body,
        double minimum,
        double maximum) =>
        MeasureWidth(
            title,
            body,
            minimum,
            maximum,
            FixedChromeReserveWidthDip);

    public static double MeasureWidth(
        string? title,
        string? body,
        double minimum,
        double maximum,
        double fixedReserveWidthDip,
        double bodyScale = 1.0)
    {
        // Note zoom changes the body, not the UI title or the fixed chrome. Other providers
        // retain the existing estimate by leaving bodyScale at one.
        var longest = Math.Max(
            DisplayWidth(title),
            (body ?? string.Empty)
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Split('\n')
                .Take(32)
                .Select(DisplayWidth)
                .DefaultIfEmpty(0)
                .Max() * bodyScale);
        var desired = Math.Max(0, fixedReserveWidthDip) +
            Math.Min(64, longest) * ApproximateGlyphWidthDip;
        return Math.Clamp(Math.Ceiling(desired), minimum, maximum);
    }

    public static int EstimateWrappedLines(string? text, double contentWidthDip)
    {
        var unitsPerLine = Math.Max(
            12,
            (int)Math.Floor(contentWidthDip / ApproximateGlyphWidthDip));
        var total = 0;
        foreach (var line in (text ?? string.Empty)
                     .Replace("\r", string.Empty, StringComparison.Ordinal)
                     .Split('\n')
                     .Take(80))
        {
            total += Math.Max(
                1,
                (int)Math.Ceiling(DisplayWidth(line) / (double)unitsPerLine));
        }
        return Math.Max(1, total);
    }

    public static int DisplayWidth(string? text) =>
        EdgeCapsuleLayout.DisplayWidth(text ?? string.Empty);
}

internal sealed class PluginFallbackEdgeCapsulePreviewView : EdgeCapsuleLivePreviewView
{
    private readonly TextBlock _title;
    private readonly TextBlock _status;

    public PluginFallbackEdgeCapsulePreviewView(
        EdgeCapsulePreviewContext context,
        EdgeCapsulePreviewSize size)
        : base(context, size)
    {
        Margin = new Thickness(12, 10, 10, 11);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _title = new TextBlock
        {
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(13),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushKey");
        Children.Add(_title);

        _status = new TextBlock
        {
            Margin = new Thickness(0, 14, 0, 0),
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(12),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top
        };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        Grid.SetRow(_status, 1);
        Children.Add(_status);

        var form = new TextBlock
        {
            Text = Context.PaperExpanded ? "●" : "○",
            Margin = new Thickness(0, 10, 0, 0),
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = AppTypography.Scale(10),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        form.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        Grid.SetRow(form, 2);
        Children.Add(form);

        InitializeLiveContent();
    }

    protected override void RebuildContent()
    {
        var title = Context.Title;
        var status = Context.ReadPluginStatus();
        _title.Text = title;
        _title.ToolTip = title;
        _status.Text = string.IsNullOrWhiteSpace(status) ? "◇" : status;
    }
}
