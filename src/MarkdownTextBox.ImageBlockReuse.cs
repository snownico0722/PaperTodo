using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    private readonly List<CachedImageBlock> _cachedImageBlocks = new();
    private ReusingMarkdownImageElementGenerator? _reusingImageElementGenerator;
    private bool _imageBlockReuseInitialized;

    private void InitializeImageBlockReuse()
    {
        if (_imageBlockReuseInitialized)
        {
            return;
        }

        var generators = TextArea.TextView.ElementGenerators;
        var generatorIndex = generators.IndexOf(_imageElementGenerator);
        if (generatorIndex < 0)
        {
            return;
        }

        _reusingImageElementGenerator = new ReusingMarkdownImageElementGenerator(this);
        generators.RemoveAt(generatorIndex);
        generators.Insert(generatorIndex, _reusingImageElementGenerator);
        Document.Changed += OnImageBlockReuseDocumentChanged;
        Unloaded += (_, _) => ClearImageBlockCache();
        _imageBlockReuseInitialized = true;
    }

    private void OnImageBlockReuseDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        // Text edits are much rarer than resize-driven VisualLine rebuilds. Clearing here keeps
        // anchor/context-menu lifetime simple while preserving the hot resize reuse path.
        ClearImageBlockCache();
    }

    private void ClearImageBlockCache()
    {
        _cachedImageBlocks.Clear();
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

            UpdateReusableImageBlock(cached.Host, reference, asset);
            return cached.Host;
        }

        var created = CreateImageBlock(reference, asset, referenceLine);
        if (created is Border host && host.Tag is ImageBlockTag tag)
        {
            _cachedImageBlocks.Add(new CachedImageBlock(
                _noteId,
                reference.ImageId,
                tag.ReferenceAnchor,
                host,
                Theme.IsDark));
            ApplyCurrentScalingMode(host.Child);
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

    private void UpdateReusableImageBlock(
        Border host,
        MarkdownImageReference reference,
        NoteImageAsset? asset)
    {
        var targetWidth = ImageTargetWidth();
        var displayWidth = ResolveImageDisplayWidth(reference.DisplayOptions, asset, targetWidth);
        var decodePixelWidth = ImageDecodePixelWidth(Math.Min(targetWidth, displayWidth));
        var bitmap = asset == null
            ? null
            : _imageStore?.GetBitmapSource(
                asset.Id,
                decodePixelWidth,
                allowDecodeUpgrade: !_isImageResizePreview,
                protectInViewport: true);
        var isCorrupted = _imageStore?.IsImageCorrupted(reference.ImageId) == true;
        var isSelected = host.Tag is ImageBlockTag oldTag &&
            !oldTag.ReferenceAnchor.IsDeleted &&
            Document != null &&
            IsImageReferenceSelected(Document.GetLineByOffset(
                Math.Clamp(oldTag.ReferenceAnchor.Offset, 0, Document.TextLength)));

        host.Width = targetWidth;
        host.ToolTip = asset?.OriginalName;
        host.BorderBrush = isSelected ? Theme.CapsuleFocusBorderBrush : Brushes.Transparent;

        if (host.Tag is ImageBlockTag tag)
        {
            host.Tag = new ImageBlockTag(
                tag.ReferenceAnchor,
                tag.CaretAnchor,
                reference.ImageId,
                reference.DisplayOptions,
                Math.Max(1, asset?.Width ?? 180));
        }

        if (host.ContextMenu?.Items.Count > 0 && host.ContextMenu.Items[0] is MenuItem copyItem)
        {
            copyItem.IsEnabled = bitmap != null;
        }

        if (bitmap == null)
        {
            UpdateOrCreateImagePlaceholder(host, targetWidth, displayWidth, isCorrupted);
            return;
        }

        if (host.Child is not Image image)
        {
            image = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            host.Child = image;
        }

        if (!ReferenceEquals(image.Source, bitmap))
        {
            image.Source = bitmap;
        }
        image.Width = displayWidth;
        ApplyCurrentScalingMode(image);
    }

    private void UpdateOrCreateImagePlaceholder(
        Border host,
        double targetWidth,
        double displayWidth,
        bool isCorrupted)
    {
        if (host.Child is not Border placeholder || placeholder.Child is not TextBlock label)
        {
            label = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            placeholder = new Border
            {
                Height = 42,
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                Child = label
            };
            host.Child = placeholder;
        }

        placeholder.Width = Math.Max(120, Math.Min(targetWidth, displayWidth));
        placeholder.Background = isCorrupted
            ? Theme.Danger((byte)(Theme.IsDark ? 30 : 18))
            : Theme.Tint((byte)(Theme.IsDark ? 30 : 18));
        placeholder.BorderBrush = isCorrupted ? Theme.Danger(70) : Theme.PaperBorderBrush;
        label.Text = Strings.Get(isCorrupted ? "ImageCorrupted" : "ImageMissing");
        label.Foreground = isCorrupted ? Theme.DangerBrush : Theme.WeakTextBrush;
        label.FontSize = ScaledFontSize(NoteTypography.FontSize);
    }

    private void ApplyCurrentScalingMode(DependencyObject? element)
    {
        if (element is Image image)
        {
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(
                image,
                _isImageResizePreview
                    ? BitmapScalingMode.LowQuality
                    : BitmapScalingMode.HighQuality);
        }
    }

    private sealed class ReusingMarkdownImageElementGenerator : VisualLineElementGenerator
    {
        private readonly MarkdownTextBox _owner;

        public ReusingMarkdownImageElementGenerator(MarkdownTextBox owner)
        {
            _owner = owner;
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (!_owner.ShouldRenderImages)
            {
                return -1;
            }

            var document = CurrentContext.Document;
            if (document == null || document.TextLength <= 0)
            {
                return -1;
            }

            var referenceLine = CurrentContext.VisualLine.FirstDocumentLine;
            return referenceLine.EndOffset >= startOffset &&
                _owner.TryGetImageReferenceForLine(referenceLine, out _, out _)
                ? referenceLine.EndOffset
                : -1;
        }

        public override VisualLineElement ConstructElement(int offset)
        {
            if (!_owner.ShouldRenderImages)
            {
                return null!;
            }

            var document = CurrentContext.Document;
            if (document == null || offset < 0 || offset > document.TextLength)
            {
                return null!;
            }

            var referenceLine = CurrentContext.VisualLine.FirstDocumentLine;
            if (referenceLine.EndOffset != offset ||
                !_owner.TryGetImageReferenceForLine(referenceLine, out var reference, out var asset))
            {
                return null!;
            }

            var element = _owner.GetOrCreateReusableImageBlock(reference, asset, referenceLine);
            return new BlockImageElement(element);
        }
    }

    private sealed class CachedImageBlock
    {
        public CachedImageBlock(
            string noteId,
            string imageId,
            TextAnchor referenceAnchor,
            Border host,
            bool isDark)
        {
            NoteId = noteId;
            ImageId = imageId;
            ReferenceAnchor = referenceAnchor;
            Host = host;
            IsDark = isDark;
        }

        public string NoteId { get; }
        public string ImageId { get; }
        public TextAnchor ReferenceAnchor { get; }
        public Border Host { get; }
        public bool IsDark { get; }
    }
}
