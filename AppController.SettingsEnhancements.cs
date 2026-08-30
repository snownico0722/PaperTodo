using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class AppController
{
    private static T MarkAdvancedSetting<T>(T element)
        where T : FrameworkElement
    {
        // Advanced sections already have a distinct background. Keep their controls on the same
        // alignment line as ordinary settings instead of adding a per-item badge.
        return element;
    }

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
}
