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
            : Strings.Format("WorkspaceCycleTooltip", number, options.SlotNumbers.Count, next);
        AutomationProperties.SetName(_workspaceButton, Strings.Format("WorkspaceCurrent", number));
        UpdateTopBarResponsiveLayout();
    }

    private void ShowNamedWorkspaceSelector(Button button)
    {
        var menu = CreateContextMenu();
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        foreach (var slot in _controller.WorkspaceOptions.SlotNumbers)
        {
            var target = slot;
            var item = new MenuItem
            {
                Header = _controller.WorkspaceOptions.DisplayName(slot),
                IsCheckable = true,
                IsChecked = slot == _controller.WorkspaceNumber
            };
            item.Click += (_, _) =>
            {
                menu.IsOpen = false;
                if (target != _controller.WorkspaceNumber)
                    _ = Dispatcher.InvokeAsync(
                        () => { if (!IsClosed) _ = _controller.SwitchWorkspaceAsync(target); },
                        DispatcherPriority.Background);
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // The ordinary overflow menu contains one horizontal row. Selection replaces that
    // very row with a confirmation; no separate dialog or second line is introduced.
    private MenuItem BuildWorkspaceSelectionMenuItem(ContextMenu menu)
    {
        var item = new MenuItem
        {
            StaysOpenOnClick = true,
            Padding = new Thickness(8, 4, 8, 4)
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        void RenderChoices()
        {
            row.Children.Clear();
            row.Children.Add(new TextBlock
            {
                Text = Strings.Get("WorkspaceSettingsTitle"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = TextBrush
            });
            foreach (var slot in _controller.WorkspaceOptions.SlotNumbers)
            {
                var target = slot;
                var choice = new Button
                {
                    Content = slot.ToString(UiLanguages.EffectiveCulture),
                    MinWidth = 27,
                    Height = 26,
                    Margin = new Thickness(1, 0, 1, 0),
                    FontWeight = slot == _controller.WorkspaceNumber
                        ? FontWeights.Bold : FontWeights.Normal,
                    IsEnabled = slot != _controller.WorkspaceNumber
                };
                choice.Click += (_, e) =>
                {
                    e.Handled = true;
                    row.Children.Clear();
                    row.Children.Add(new TextBlock
                    {
                        Text = SettingsWorkspaceConfirmation(target),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 6, 0),
                        Foreground = TextBrush
                    });
                    var yes = new Button
                    {
                        Content = WorkspaceMenuText("确定", "OK", "確定", "확인"),
                        Padding = new Thickness(5, 2, 5, 2),
                        Margin = new Thickness(0, 0, 4, 0)
                    };
                    yes.Click += (_, args) =>
                    {
                        args.Handled = true;
                        menu.IsOpen = false;
                        _ = Dispatcher.InvokeAsync(
                            () => { if (!IsClosed) _ = _controller.SwitchWorkspaceAsync(target); },
                            DispatcherPriority.Background);
                    };
                    var no = new Button { Content = Strings.Get("CommonCancel"), Padding = new Thickness(5, 2, 5, 2) };
                    no.Click += (_, args) => { args.Handled = true; RenderChoices(); };
                    row.Children.Add(yes);
                    row.Children.Add(no);
                };
                row.Children.Add(choice);
            }
        }
        RenderChoices();
        item.Header = row;
        return item;
    }

    private static string WorkspaceMenuText(string zh, string en, string ja, string ko) =>
        UiLanguages.EffectiveUiCulture.TwoLetterISOLanguageName switch
        {
            "en" => en,
            "ja" => ja,
            "ko" => ko,
            _ => zh
        };

    private static string SettingsWorkspaceConfirmation(int number) =>
        WorkspaceMenuText("确定切换为工作区" + number + "？",
            "Switch to workspace " + number + "?",
            "ワークスペース" + number + "に切り替えますか？",
            "작업 공간 " + number + "(으)로 전환할까요?");
}
