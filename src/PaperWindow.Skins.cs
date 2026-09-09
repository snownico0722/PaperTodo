using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Button = System.Windows.Controls.Button;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal void RefreshSkin()
    {
        SkinBorder.Refresh(_paperChrome);
        _edgeCapsuleHost?.RefreshSkin();
        _experimentalTetherCapsule?.UpdateTheme();
        _todoCheckBoxStyle = null;
        if (_newTodoButton != null) _newTodoButton.Content = CreateTopBarNewTodoIcon(_newTodoButton);
        if (_newNoteButton != null) _newNoteButton.Content = CreateTopBarNewNoteIcon(_newNoteButton);
        if (_closeButton != null) RefreshCloseButton();
    }
    private static FrameworkElement CreatePixelIcon(Button owner, params string[] rows)
    {
        var geometry = new GeometryGroup();
        for (var y = 0; y < rows.Length; y++)
        for (var x = 0; x < rows[y].Length; x++)
            if (rows[y][x] == '#') geometry.Children.Add(new RectangleGeometry(new Rect(x, y, 1, 1)));
        geometry.Freeze();
        var icon = new Path
        {
            Data = geometry, Width = rows[0].Length * 1.5 * TopBarIconScale(),
            Height = rows.Length * 1.5 * TopBarIconScale(), Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false, SnapsToDevicePixels = true, UseLayoutRounding = true
        };
        RenderOptions.SetEdgeMode(icon, EdgeMode.Aliased);
        icon.SetBinding(Shape.FillProperty, CreateForegroundBinding(owner));
        return icon;
    }
}
