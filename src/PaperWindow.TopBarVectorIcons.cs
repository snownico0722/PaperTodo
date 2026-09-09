using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private static FrameworkElement CreateTopBarAssociationIcon(
        Button owner,
        bool active)
    {
        var scale = TopBarIconScale();
        var icon = new VectorPrimitiveIconElement(
            active
                ? VectorPrimitiveIconKind.AssociationActive
                : VectorPrimitiveIconKind.AssociationIdle)
        {
            Width = Math.Round(14 * scale, 1),
            Height = Math.Round(14 * scale, 1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetBinding(
            VectorPrimitiveIconElement.ForegroundProperty,
            CreateForegroundBinding(owner));
        return icon;
    }

    private static FrameworkElement CreateTopBarNewTodoIcon(Button owner)
    {
        return CreateTopBarNewPaperIcon(
            owner,
            VectorPrimitiveIconKind.Check,
            width: 21);
    }

    private static FrameworkElement CreateTopBarNewNoteIcon(Button owner)
    {
        return CreateTopBarNewPaperIcon(
            owner,
            VectorPrimitiveIconKind.Note,
            width: 22);
    }

    private static FrameworkElement CreateTopBarNewPaperIcon(
        Button owner,
        VectorPrimitiveIconKind kind,
        double width)
    {
        var scale = TopBarIconScale();
        var icon = new Grid
        {
            Width = Math.Round(width * scale, 1),
            Height = Math.Round(16 * scale, 1),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            IsHitTestVisible = false
        };

        var plus = new VectorPrimitiveIconElement(
            VectorPrimitiveIconKind.Plus)
        {
            Width = Math.Round(9 * scale, 1),
            Height = Math.Round(9 * scale, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        plus.SetBinding(
            VectorPrimitiveIconElement.ForegroundProperty,
            CreateForegroundBinding(owner));
        icon.Children.Add(plus);

        var mark = new VectorPrimitiveIconElement(kind)
        {
            IconSize = AppTypography.Scale(13),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        mark.SetBinding(
            VectorPrimitiveIconElement.ForegroundProperty,
            CreateForegroundBinding(owner));
        icon.Children.Add(mark);
        return icon;
    }

    private static FrameworkElement CreateTopBarCloseIcon(
        Button owner,
        bool collapse)
    {
        if (collapse)
        {
            var scale = TopBarIconScale();
            var minus = new VectorPrimitiveIconElement(
                VectorPrimitiveIconKind.Minus)
            {
                Width = Math.Round(16 * scale, 1),
                Height = Math.Round(10 * scale, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            minus.SetBinding(
                VectorPrimitiveIconElement.ForegroundProperty,
                CreateForegroundBinding(owner));
            return minus;
        }

        var close = new VectorPrimitiveIconElement(VectorPrimitiveIconKind.Close)
        {
            IconSize = AppTypography.Scale(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        close.SetBinding(
            VectorPrimitiveIconElement.ForegroundProperty,
            CreateForegroundBinding(owner));
        return close;
    }

    private static double TopBarIconScale()
    {
        return Math.Clamp(
            AppTypography.ScaleFactor,
            0.85,
            1.15);
    }
}
