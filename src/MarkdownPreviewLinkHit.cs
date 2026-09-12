using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

// The two drawing paths share the same native interaction contract, not just the same URL policy.
internal static class MarkdownPreviewLinkHit
{
    [ThreadStatic] private static ControlTemplate? _template;

    internal static Button Create(string target, Size size, Action<string> openExternal)
    {
        if (_template == null)
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            _template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        }
        var button = new Button
        {
            Background = Brushes.Transparent, Template = _template, ClickMode = ClickMode.Release,
            Padding = new Thickness(), BorderThickness = new Thickness(),
            Width = size.Width, Height = size.Height, Cursor = Cursors.Hand,
            Focusable = true, ToolTip = target
        };
        EdgeCapsulePreviewInteraction.SetConsumesPointer(button, true);
        AutomationProperties.SetName(button, target);
        button.Click += (_, e) => { openExternal(target); e.Handled = true; };
        return button;
    }
}
