using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private Button? _workspaceButton;
    private TextBlock? _workspaceNumberBadge;

    private Button CreateWorkspaceCycleButton()
    {
        var button = _workspaceButton = IconButton("", "");
        button.Width = 25;
        AutomationProperties.SetAutomationId(button, "WorkspaceCycle");
        var icon = new Grid { Width = 20, Height = 19, IsHitTestVisible = false };
        var loop = new Path
        {
            Data = Geometry.Parse("M 3,9 A 5.5,5.5 0 1 1 12,10 M 12,10 L 12,6 M 12,10 L 16,10"),
            StrokeThickness = 1.35,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        loop.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = button });
        icon.Children.Add(loop);
        _workspaceNumberBadge = new TextBlock
        {
            FontSize = 8,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, -1)
        };
        _workspaceNumberBadge.SetBinding(TextBlock.ForegroundProperty,
            new Binding(nameof(Control.Foreground)) { Source = button });
        icon.Children.Add(_workspaceNumberBadge);
        button.Content = icon;
        button.Click += async (_, _) =>
        {
            // Leave the Button mouse-capture/input turn before ending its owning workspace.
            if (_controller.IsWorkspaceSwitching) return;
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
            if (!IsClosed) await _controller.CycleWorkspaceAsync();
        };
        RefreshWorkspaceButton();
        return button;
    }

    internal void RefreshWorkspaceButton()
    {
        if (_workspaceButton == null || IsClosed) return;
        var options = _controller.WorkspaceOptions;
        var number = _controller.WorkspaceNumber;
        _workspaceButton.Visibility = options.Enabled ? Visibility.Visible : Visibility.Collapsed;
        _workspaceButton.IsEnabled = !_controller.IsWorkspaceSwitching;
        _workspaceButton.Foreground = TextBrush;
        if (_workspaceNumberBadge != null)
        {
            _workspaceNumberBadge.Text = number.ToString(UiLanguages.EffectiveCulture);
            _workspaceNumberBadge.FontFamily = AppTypography.UiFontFamily;
        }
        var next = number == options.Count ? 1 : number + 1;
        _workspaceButton.ToolTip = Strings.Format("WorkspaceCycleTooltip", number, options.Count, next);
        AutomationProperties.SetName(_workspaceButton, Strings.Format("WorkspaceCurrent", number));
        UpdateTopBarResponsiveLayout();
    }

    private double WorkspacePersistentButtonWidth() => _workspaceButton?.Visibility == Visibility.Visible
        ? TopBarOuterWidth(_workspaceButton) : 0;
}
