using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace PaperTodo;

// The same thin-line icon is used by the paper top bar and the shortcut
// settings preview. Keep this path in sync with assets/workspace-cycle.svg.
internal static class WorkspaceCycleIcon
{
    internal const string PathData =
        "M7 2H16A2 2 0 0 1 18 4V13 M6 4H14A2 2 0 0 1 16 6V14A2 2 0 0 1 14 16H6A2 2 0 0 1 4 14V6A2 2 0 0 1 6 4Z";
    internal const double StrokeThickness = 1.2;
}

public sealed partial class PaperWindow
{
    private Button? _workspaceButton;
    private TextBlock? _workspaceNumberBadge;

    private Button CreateWorkspaceCycleButton()
    {
        var button = _workspaceButton = IconButton("", "");
        button.Width = 25;
        AutomationProperties.SetAutomationId(button, "WorkspaceCycle");
        button.Content = CreateWorkspaceCycleIcon(button, _controller.WorkspaceNumber, out _workspaceNumberBadge);
        button.Click += async (_, _) =>
        {
            if (_controller.IsWorkspaceSwitching) return;
            if (_controller.WorkspaceOptions.TopBarNamePicker)
            {
                ShowNamedWorkspaceSelector(button);
                return;
            }
            if (_controller.WorkspaceOptions.SlotNumbers.Count == 1)
            {
                await OfferCreateWorkspaceAsync();
                return;
            }
            // Leave the Button mouse-capture/input turn before ending its owning workspace.
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!IsClosed) await _controller.CycleWorkspaceAsync();
        };
        RefreshWorkspaceButton();
        return button;
    }

    // Draw the shared path directly so single-file builds need no external SVG renderer.
    internal static Viewbox CreateWorkspaceCycleIcon(Control owner, int number, out TextBlock numberBadge)
    {
        // The stacked outline spans x=4..18, y=2..16 in this 20-unit drawing.
        // Move both layers together so the number stays centered on the front page.
        var icon = new Grid
        {
            Width = 20, Height = 20, IsHitTestVisible = false,
            RenderTransform = new TranslateTransform(-1, 1)
        };
        var loop = new Path
        {
            Data = Geometry.Parse(WorkspaceCycleIcon.PathData),
            StrokeThickness = WorkspaceCycleIcon.StrokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        loop.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = owner });
        icon.Children.Add(loop);
        numberBadge = new TextBlock
        {
            Text = number.ToString(UiLanguages.EffectiveCulture),
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        numberBadge.SetBinding(TextBlock.ForegroundProperty,
            new Binding(nameof(Control.Foreground)) { Source = owner });
        icon.Children.Add(numberBadge);
        var size = Math.Round(18 * TopBarIconScale(), 1);
        return new Viewbox { Width = size, Height = size, Child = icon };
    }

    internal void RefreshWorkspaceButton()
    {
        if (_workspaceButton == null || IsClosed) return;
        var options = _controller.WorkspaceOptions;
        var number = _controller.WorkspaceNumber;
        _workspaceButton.Visibility = options.Enabled &&
            PaperTopBarShortcuts.Contains(_controller.State, _paper.Type, PaperTopBarShortcuts.Workspace)
                ? Visibility.Visible : Visibility.Collapsed;
        _workspaceButton.IsEnabled = !_controller.IsWorkspaceSwitching &&
            (!_controller.IsPaperExportActive || options.TopBarNamePicker || options.SlotNumbers.Count == 1);
        if (_workspaceButton.Content is Viewbox icon)
            icon.Width = icon.Height = Math.Round(18 * TopBarIconScale(), 1);
        if (_workspaceNumberBadge != null)
        {
            _workspaceNumberBadge.Text = number.ToString(UiLanguages.EffectiveCulture);
            _workspaceNumberBadge.FontFamily = AppTypography.UiFontFamily;
        }
        var next = options.NextSlot(number);
        _workspaceButton.ToolTip = options.SlotNumbers.Count == 1
            ? Strings.Get("WorkspaceSingleMessage") : options.TopBarNamePicker
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
        PopulateWorkspaceMenu(menu, menu);
        menu.IsOpen = true;
    }

    private MenuItem BuildWorkspaceShortcutMenuItem(ContextMenu menu)
    {
        var selector = new MenuItem { Header = Strings.Get("WorkspaceSettingsTitle") };
        AutomationProperties.SetAutomationId(selector, "PaperWorkspace");
        PopulateWorkspaceMenu(selector, menu);
        return selector;
    }

    private async System.Threading.Tasks.Task OfferCreateWorkspaceAsync()
    {
        // Leave the menu/button input turn before opening a modal dialog.
        await Dispatcher.Yield(DispatcherPriority.Background);
        if (IsClosed || _controller.IsWorkspaceSwitching || !_controller.WorkspaceOptions.Enabled ||
            _controller.WorkspaceOptions.SlotNumbers.Count != 1) return;
        var owner = IsVisible && PresentationSource.FromVisual(this) != null ? this : null;
        if (PaperNoticeDialog.ShowChoice(owner, Strings.Get("WorkspaceSettingsTitle"),
                Strings.Get("WorkspaceSingleMessage"), Strings.Get("CommonCancel"),
                Strings.Get("WorkspaceCreate")))
            await _controller.CreateWorkspaceAsync();
    }

    // Only the container differs between the top-bar popup and a paper/capsule submenu.
    private void PopulateWorkspaceMenu(ItemsControl choices, ContextMenu ownerMenu)
    {
        choices.Items.Clear();
        choices.Items.Add(MenuHeader(Strings.Get("WorkspaceSwitchGroup")));
        PopulateWorkspaceChoices(choices, ownerMenu, movePaper: false);
        choices.Items.Add(MenuSeparator());
        choices.Items.Add(MenuHeader(Strings.Get("WorkspaceMoveGroup")));
        PopulateWorkspaceChoices(choices, ownerMenu, movePaper: true);
        choices.Items.Add(MenuSeparator());
        var create = MenuItem(Strings.Get("WorkspaceCreateNew"), async (_, _) =>
        {
            await _controller.CreateWorkspaceAsync();
            if (IsClosed || !ownerMenu.IsOpen) return;
            PopulateWorkspaceMenu(choices, ownerMenu);
            ownerMenu.Focus();
        });
        create.IsEnabled = !_controller.IsWorkspaceSwitching &&
            _controller.WorkspaceOptions.SlotNumbers.Count < 5;
        create.Opacity = create.IsEnabled ? 1 : 0.45;
        create.StaysOpenOnClick = true;
        AutomationProperties.SetAutomationId(create, "CreateWorkspace");
        choices.Items.Add(create);
    }

    private void PopulateWorkspaceChoices(ItemsControl choices, ContextMenu ownerMenu, bool movePaper)
    {
        foreach (var slot in _controller.WorkspaceOptions.SlotNumbers)
        {
            var target = slot;
            var current = slot == _controller.WorkspaceNumber;
            var name = _controller.WorkspaceOptions.DisplayName(slot);
            var item = new MenuItem
            {
                IsEnabled = !current && !_controller.IsWorkspaceSwitching &&
                    (movePaper || !_controller.IsPaperExportActive)
            };
            // Workspace navigation is not a checkbox: keep a small, aligned dot slot
            // without changing the shared style used by real checkable menu items.
            var dot = new Ellipse
            {
                Width = AppTypography.Scale(5),
                Height = AppTypography.Scale(5),
                Margin = new Thickness(0, 0, AppTypography.Scale(5), 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = !movePaper && current ? Visibility.Visible : Visibility.Hidden
            };
            dot.SetBinding(Shape.FillProperty,
                new Binding(nameof(Control.Foreground)) { Source = item });
            var header = new DockPanel { IsHitTestVisible = false };
            header.Children.Add(dot);
            header.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            item.Header = header;
            item.Opacity = item.IsEnabled ? 1 : 0.45;
            AutomationProperties.SetName(item, name);
            AutomationProperties.SetAutomationId(item, (movePaper ? "MoveToWorkspace" : "SwitchWorkspace") + slot);
            item.Click += (_, _) =>
            {
                ownerMenu.IsOpen = false;
                _ = Dispatcher.InvokeAsync(
                    () =>
                    {
                        if (IsClosed) return;
                        if (movePaper) _ = _controller.MovePaperToWorkspaceAsync(_paper, target);
                        else _ = _controller.SwitchWorkspaceAsync(target);
                    },
                    DispatcherPriority.Background);
            };
            choices.Items.Add(item);
        }
    }

    internal bool HasLivePluginBodyForWorkspaceMove =>
        _paper.Type == PaperTypes.Note && !IsCurrentBodyProviderMarkdown && _paperBodyHost.HasCurrent;

    internal void DetachPluginBodyForWorkspaceMove()
    {
        // Commit/Dispose may save once more. The existing detach boundary drains
        // those writes and invalidates the old context before its state is copied.
        RemoveCurrentPaperBody();
    }

    internal void RestorePluginBodyAfterFailedWorkspaceMove()
    {
        if (_windowLifecycle == PaperWindowLifecycleState.Alive &&
            _isShellBuilt && !_paperBodyHost.HasCurrent)
            ReloadCurrentPaperBody();
    }

}
