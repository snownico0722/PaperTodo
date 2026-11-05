using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PaperTodo;

// Menus use the normal WPF opening, placement, focus and dismissal path.
// The retired full-material pre-capture no longer delays the first frame.
internal sealed class MaterialContextMenu : ContextMenu
{
    internal Action? BeforeWpfOpen { get; set; }

    static MaterialContextMenu() => IsOpenProperty.OverrideMetadata(typeof(MaterialContextMenu),
        new FrameworkPropertyMetadata(false, null, CoerceOpen));

    private static object CoerceOpen(DependencyObject d, object value)
    {
        var menu = (MaterialContextMenu)d;
        menu.ConfigurePopupAnimation();
        var requested = (bool)value;
        if (requested && !menu.IsOpen)
            menu.BeforeWpfOpen?.Invoke();
        return requested;
    }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        ConfigurePopupAnimation();
    }

    private void ConfigurePopupAnimation()
    {
        if (LogicalTreeHelper.GetParent(this) is Popup popup)
            popup.SetResourceReference(Popup.PopupAnimationProperty, SystemParameters.MenuPopupAnimationKey);
    }
}

internal sealed class MaterialSubmenuPopup : Popup
{
    private static readonly CoerceValueCallback? BaseOpenCoercion =
        IsOpenProperty.GetMetadata(typeof(Popup)).CoerceValueCallback;

    static MaterialSubmenuPopup() => IsOpenProperty.OverrideMetadata(typeof(MaterialSubmenuPopup),
        new FrameworkPropertyMetadata(false, null, CoerceOpen));

    private static object CoerceOpen(DependencyObject d, object value)
    {
        var popup = (MaterialSubmenuPopup)d;
        popup.SetValue(PopupAnimationProperty, PopupAnimation.Fade);
        // Preserve Popup's disconnected-tree/Loaded guard; no second async open state.
        return BaseOpenCoercion?.Invoke(d, value) ?? value;
    }
}
