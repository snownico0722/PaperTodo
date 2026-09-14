using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool HasMaterialHeader => Theme.Skin != PaperSkins.Paper && !SystemParameters.HighContrast;

    internal void RefreshSkin()
    {
        SkinBorder.Refresh(_paperChrome);
        _edgeCapsuleHost?.RefreshSkin();
        _experimentalTetherCapsule?.UpdateTheme();
        _todoCheckBoxStyle = null;
        if (_topBarTitleHost != null) _topBarTitleHost.CornerRadius = new CornerRadius(Theme.IsPixelSkin ? 0 : RadiusControl);
        if (_newTodoButton != null) _newTodoButton.Content = CreateTopBarNewTodoIcon(_newTodoButton);
        if (_newNoteButton != null) _newNoteButton.Content = CreateTopBarNewNoteIcon(_newNoteButton);
        if (_closeButton != null) RefreshCloseButton();
    }
    private static FrameworkElement CreatePixelIcon(Button owner, params string[] rows)
    {
        var icon = new PixelIconElement(rows)
        {
            Width = 18 * TopBarIconScale(), Height = 18 * TopBarIconScale(),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetBinding(PixelIconElement.ForegroundProperty, CreateForegroundBinding(owner));
        return icon;
    }
}
