using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class AppController
{
    private UIElement AdvancedSettingsBlock(params UIElement[] items)
    {
        var content = new StackPanel();
        foreach (var item in items)
        {
            content.Children.Add(item);
        }

        return new Border
        {
            Background = Theme.Tint((byte)(Theme.IsDark ? 24 : 14)),
            BorderBrush = Theme.Tint((byte)(Theme.IsDark ? 42 : 28)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            // The negative horizontal margin grows only the background. Matching padding keeps
            // every control aligned with the ordinary settings above and below this block.
            Padding = new Thickness(8, 5, 8, 8),
            Margin = new Thickness(-8, 5, -8, 7),
            Child = content
        };
    }

    private void ToggleLinkedPathExtensionOnly()
    {
        State.ShowLinkedPathExtensionOnly = !State.ShowLinkedPathExtensionOnly;
        foreach (var window in _windows.Values)
        {
            window.RefreshTodoRowsForExternalChange();
        }

        SaveNow();
        RefreshSettingsRegions("general.todos");
    }

    private void SetDeepCapsuleGapSize(string size)
    {
        var normalized = DeepCapsuleGapSizes.Normalize(size);
        if (State.DeepCapsuleGapSize == normalized)
        {
            return;
        }

        State.DeepCapsuleGapSize = normalized;
        SaveNow();
        ArrangeDeepCapsules(animate: State.EnableAnimations);
    }

    private UIElement CreateDeepCapsuleGapSegmentSelector()
    {
        var segments = new[]
        {
            (DeepCapsuleGapSizes.Narrow, Strings.Get("DeepCapsuleGapNarrow")),
            (DeepCapsuleGapSizes.Standard, Strings.Get("DeepCapsuleGapStandard")),
            (DeepCapsuleGapSizes.Wide, Strings.Get("DeepCapsuleGapWide"))
        };

        return CreateSegmentSelector(
            segments,
            DeepCapsuleGapSizes.Normalize(State.DeepCapsuleGapSize),
            SetDeepCapsuleGapSize);
    }
}
