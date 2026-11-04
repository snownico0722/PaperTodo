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
    // Custom original SVG contour in assets/workspace-cycle.svg. Keep the WPF Geometry inline
    // so the single-file application has no SVG renderer or loose-resource dependency.
    private const string WorkspaceCycleSvgPath = "M4.2 7.8V6.25C4.2 5.05 5.15 4.1 6.35 4.1H10.25C11.45 4.1 12.4 5.05 12.4 6.25V7.65M10.35 5.65L12.4 7.7 14.45 5.65M12.4 9.35V10.15C12.4 11.35 11.45 12.3 10.25 12.3H6.35C5.15 12.3 4.2 11.35 4.2 10.15V8.75M2.15 10.8L4.2 8.75 6.25 10.8";

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
            Data = Geometry.Parse(WorkspaceCycleSvgPath),
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
            if (_controller.IsWorkspaceSwitching) return;
            if (_controller.WorkspaceOptions.TopBarNamePicker)
            {
                ShowNamedWorkspaceSelector(button);
                return;
            }
            // Leave the Button mouse-capture/input turn before ending its owning workspace.
            await Dispatcher.Yield(DispatcherPriority.Background);
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
        _workspaceButton.Visibility = options.Enabled && options.SlotNumbers.Count > 1 &&
            PaperTopBarShortcuts.Contains(_controller.State, _paper.Type, PaperTopBarShortcuts.Workspace)
                ? Visibility.Visible : Visibility.Collapsed;
        _workspaceButton.IsEnabled = !_controller.IsWorkspaceSwitching;
        _workspaceButton.Foreground = TextBrush;
        if (_workspaceNumberBadge != null)
        {
            _workspaceNumberBadge.Text = number.ToString(UiLanguages.EffectiveCulture);
            _workspaceNumberBadge.FontFamily = AppTypography.UiFontFamily;
        }
        var next = options.NextSlot(number);
        _workspaceButton.ToolTip = options.TopBarNamePicker
            ? options.DisplayName(number)
            : Strings.Format("WorkspaceCycleTooltip", number, next);
        AutomationProperties.SetName(_workspaceButton, Strings.Format("WorkspaceCurrent", number));
        UpdateTopBarResponsiveLayout();
    }

    private void ShowNamedWorkspaceSelector(Button button)
    {
        var menu = CreateContextMenu();
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        PopulateNamedWorkspaceChoices(menu, menu);
        menu.IsOpen = true;
    }

    private MenuItem BuildWorkspaceShortcutMenuItem(ContextMenu menu)
    {
        if (!_controller.WorkspaceOptions.TopBarNamePicker)
        {
            var next = _controller.WorkspaceOptions.NextSlot(_controller.WorkspaceNumber);
            return MenuItem(WorkspaceMenuText(
                "切换到工作区" + next,
                "Switch to workspace " + next,
                "ワークスペース" + next + "に切り替える",
                "작업 공간 " + next + "(으)로 전환"),
                (_, _) =>
                {
                    menu.IsOpen = false;
                    _ = Dispatcher.InvokeAsync(
                        () => { if (!IsClosed) _ = _controller.CycleWorkspaceAsync(); },
                        DispatcherPriority.Background);
                });
        }

        var selector = new MenuItem
        {
            Header = WorkspaceMenuText("切换工作区", "Switch workspace",
                "ワークスペースを切り替える", "작업 공간 전환")
        };
        PopulateNamedWorkspaceChoices(selector, menu);
        return selector;
    }

    // Both top-bar and overflow use the same named-choice handler. Only the placement
    // differs: a popup below the button versus a submenu beside the overflow item.
    private void PopulateNamedWorkspaceChoices(ItemsControl choices, ContextMenu ownerMenu)
    {
        foreach (var slot in _controller.WorkspaceOptions.SlotNumbers)
        {
            var target = slot;
            var current = slot == _controller.WorkspaceNumber;
            var item = new MenuItem
            {
                Header = _controller.WorkspaceOptions.DisplayName(slot),
                IsCheckable = true,
                IsChecked = current,
                IsEnabled = !current && !_controller.IsWorkspaceSwitching
            };
            item.Click += (_, _) =>
            {
                ownerMenu.IsOpen = false;
                _ = Dispatcher.InvokeAsync(
                    () => { if (!IsClosed) _ = _controller.SwitchWorkspaceAsync(target); },
                    DispatcherPriority.Background);
            };
            choices.Items.Add(item);
        }
    }

    private static string WorkspaceMenuText(string zh, string en, string ja, string ko) =>
        UiLanguages.EffectiveUiCulture.TwoLetterISOLanguageName switch
        {
            "en" => en,
            "ja" => ja,
            "ko" => ko,
            _ => zh
        };
}
