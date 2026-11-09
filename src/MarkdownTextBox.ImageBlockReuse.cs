using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    private readonly List<CachedImageBlock> _cachedImageBlocks = new();
    private MarkdownImageElementGenerator? _reusingImageElementGenerator;
    private DispatcherTimer? _imageBlockCachePruneTimer;
    private bool _imageBlockReuseInitialized;

    private void InitializeImageBlockReuse()
    {
        if (_imageBlockReuseInitialized)
        {
            return;
        }

        var generators = TextArea.TextView.ElementGenerators;
        var generatorIndex = _semanticImageElementGenerator == null
            ? -1
            : generators.IndexOf(_semanticImageElementGenerator);
        if (generatorIndex < 0)
        {
            return;
        }

        _reusingImageElementGenerator = new MarkdownImageElementGenerator(this, reuseBlocks: true);
        generators.RemoveAt(generatorIndex);
        generators.Insert(generatorIndex, _reusingImageElementGenerator);
        Document.Changed += OnImageBlockReuseDocumentChanged;
        TextArea.TextView.VisualLinesChanged += (_, _) => ScheduleImageBlockCachePrune();
        Unloaded += (_, _) =>
        {
            _imageBlockCachePruneTimer?.Stop();
            ClearImageBlockCache();
        };
        _imageBlockReuseInitialized = true;
    }

    private void OnImageBlockReuseDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_cachedImageBlocks.Count == 0 || Document == null)
        {
            return;
        }

        var changeStart = e.Offset;
        var changeEnd = e.Offset + Math.Max(1, Math.Max(e.RemovalLength, e.InsertionLength));
        for (var index = _cachedImageBlocks.Count - 1; index >= 0; index--)
        {
            var cached = _cachedImageBlocks[index];
            if (cached.ReferenceAnchor.IsDeleted)
            {
                _cachedImageBlocks.RemoveAt(index);
                continue;
            }

            DocumentLine line;
            try
            {
                line = Document.GetLineByOffset(
                    Math.Clamp(cached.ReferenceAnchor.Offset, 0, Document.TextLength));
            }
            catch
            {
                _cachedImageBlocks.RemoveAt(index);
                continue;
            }

            var lineEnd = line.EndOffset + line.DelimiterLength;
            if (changeStart <= lineEnd && changeEnd >= line.Offset)
            {
                _cachedImageBlocks.RemoveAt(index);
            }
        }
    }

    private void ClearImageBlockCache()
    {
        _cachedImageBlocks.Clear();
    }

    private void ScheduleImageBlockCachePrune()
    {
        if (!_imageBlockReuseInitialized || _isImageResizePreview)
        {
            return;
        }

        _imageBlockCachePruneTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(200),
            DispatcherPriority.Background,
            (_, _) =>
            {
                _imageBlockCachePruneTimer!.Stop();
                if (_imageRenderingSuspended || !IsLoaded)
                {
                    ClearImageBlockCache();
                    return;
                }

                if (_isImageResizePreview)
                {
                    return;
                }

                PruneImageBlockCacheToCurrentVisualTree();
            },
            Dispatcher);

        _imageBlockCachePruneTimer.Stop();
        _imageBlockCachePruneTimer.Start();
    }

    private void PruneImageBlockCacheToCurrentVisualTree()
    {
        PruneImageBlockCache();
        if (_cachedImageBlocks.Count == 0)
        {
            return;
        }

        var attachedHosts = new HashSet<Border>();
        CollectAttachedBorders(TextArea.TextView, attachedHosts);
        for (var index = _cachedImageBlocks.Count - 1; index >= 0; index--)
        {
            if (!attachedHosts.Contains(_cachedImageBlocks[index].Host))
            {
                _cachedImageBlocks.RemoveAt(index);
            }
        }
    }

    private static void CollectAttachedBorders(DependencyObject node, ISet<Border> borders)
    {
        if (node is Border border)
        {
            borders.Add(border);
        }

        int childCount;
        try
        {
            childCount = VisualTreeHelper.GetChildrenCount(node);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        for (var index = 0; index < childCount; index++)
        {
            DependencyObject child;
            try
            {
                child = VisualTreeHelper.GetChild(node, index);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            CollectAttachedBorders(child, borders);
        }
    }

    private FrameworkElement GetOrCreateReusableImageBlock(
        MarkdownImageReference reference,
        NoteImageAsset? asset,
        DocumentLine referenceLine)
    {
        PruneImageBlockCache();

        for (var index = 0; index < _cachedImageBlocks.Count; index++)
        {
            var cached = _cachedImageBlocks[index];
            if (!string.Equals(cached.NoteId, _noteId, StringComparison.Ordinal) ||
                !string.Equals(cached.ImageId, reference.ImageId, StringComparison.Ordinal) ||
                cached.ReferenceAnchor.IsDeleted ||
                cached.ReferenceAnchor.Offset != referenceLine.Offset)
            {
                continue;
            }

            if (TryUpdateReusableImageBlock(cached, reference, asset, referenceLine))
            {
                return cached.Host;
            }

            _cachedImageBlocks.RemoveAt(index);
            break;
        }

        var created = CreateImageBlock(reference, asset, referenceLine);
        if (created is Border host &&
            host.Child is Image image &&
            host.Tag is ImageBlockTag tag)
        {
            _cachedImageBlocks.Add(new CachedImageBlock(
                _noteId,
                reference.ImageId,
                tag.ReferenceAnchor,
                host,
                image,
                Theme.IsDark));
            ApplyCurrentScalingMode(image);
        }

        return created;
    }

    private void PruneImageBlockCache()
    {
        for (var index = _cachedImageBlocks.Count - 1; index >= 0; index--)
        {
            var cached = _cachedImageBlocks[index];
            if (cached.ReferenceAnchor.IsDeleted ||
                !string.Equals(cached.NoteId, _noteId, StringComparison.Ordinal) ||
                cached.IsDark != Theme.IsDark)
            {
                _cachedImageBlocks.RemoveAt(index);
            }
        }
    }

    private bool TryUpdateReusableImageBlock(
        CachedImageBlock cached,
        MarkdownImageReference reference,
        NoteImageAsset? asset,
        DocumentLine referenceLine)
    {
        if (asset == null || !ReferenceEquals(cached.Host.Child, cached.Image))
        {
            return false;
        }

        var targetWidth = ImageTargetWidth();
        var displayWidth = ResolveImageDisplayWidth(reference.DisplayOptions, asset, targetWidth);
        var decodePixelWidth = ImageDecodePixelWidth(Math.Min(targetWidth, displayWidth));
        var bitmap = _imageStore?.GetBitmapSource(
            asset.Id,
            decodePixelWidth,
            allowDecodeUpgrade: !_isImageResizePreview,
            protectInViewport: true);
        if (bitmap == null)
        {
            return false;
        }

        cached.Host.Width = targetWidth;
        cached.Host.ToolTip = asset.OriginalName;
        cached.Host.BorderBrush = IsImageReferenceSelected(referenceLine)
            ? Theme.CapsuleFocusBorderBrush
            : Brushes.Transparent;

        if (cached.Host.Tag is ImageBlockTag tag)
        {
            cached.Host.Tag = new ImageBlockTag(
                tag.ReferenceAnchor,
                tag.CaretAnchor,
                reference.ImageId,
                reference.DisplayOptions,
                Math.Max(1, asset.Width));
        }

        if (!ReferenceEquals(cached.Image.Source, bitmap))
        {
            cached.Image.Source = bitmap;
        }
        cached.Image.Width = displayWidth;
        ApplyCurrentScalingMode(cached.Image);
        return true;
    }

    private void ApplyCurrentScalingMode(Image image)
    {
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(
            image,
            _isImageResizePreview
                ? BitmapScalingMode.LowQuality
                : BitmapScalingMode.HighQuality);
    }

    private sealed class CachedImageBlock
    {
        public CachedImageBlock(
            string noteId,
            string imageId,
            TextAnchor referenceAnchor,
            Border host,
            Image image,
            bool isDark)
        {
            NoteId = noteId;
            ImageId = imageId;
            ReferenceAnchor = referenceAnchor;
            Host = host;
            Image = image;
            IsDark = isDark;
        }

        public string NoteId { get; }
        public string ImageId { get; }
        public TextAnchor ReferenceAnchor { get; }
        public Border Host { get; }
        public Image Image { get; }
        public bool IsDark { get; }
    }
}
