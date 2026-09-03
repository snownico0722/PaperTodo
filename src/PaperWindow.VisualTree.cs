using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private static bool IsDescendantOf(DependencyObject? current, DependencyObject target)
    {
        while (current != null)
        {
            if (ReferenceEquals(current, target))
            {
                return true;
            }
            current = GetSafeParent(current);
        }
        return false;
    }

    private static bool IsScrollBarInteractionSource(DependencyObject? current, DependencyObject scope)
    {
        while (current != null)
        {
            if (current is ScrollBar or Thumb or Track or RepeatButton)
            {
                return true;
            }

            if (ReferenceEquals(current, scope))
            {
                return false;
            }

            current = GetSafeParent(current);
        }

        return false;
    }

    // 父级 PreviewMouseLeftButtonDown handler 用于识别 Hyperlink 点击。
    // Hyperlink 自己的 OnMouseLeftButtonDown 会跟踪 click,MouseLeftButtonUp
    // 触发 Click + RequestNavigate 打开浏览器;若在 handler 里 e.Handled = true 会打断。
    private static bool IsHyperlinkInteractionSource(DependencyObject? current)
    {
        while (current != null)
        {
            if (current is Hyperlink)
            {
                return true;
            }
            current = GetSafeParent(current);
        }
        return false;
    }
}
