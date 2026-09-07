using System.Windows;
using System.Windows.Controls;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private void AttachPluginPaperMenuActions(ContextMenu menu)
        => AttachPluginPaperMenuActions(menu, _controller, _paper.Id);

    // The Paper owns the action, but its visible menu may belong to an EdgeCapsuleHost.
    // Keep the renderer independent of the hidden PaperWindow so placement uses the real UI.
    internal static void AttachPluginPaperMenuActions(ContextMenu menu, AppController controller, string paperId)
    {
        var insertionIndex = menu.Items.Count;
        var contributed = new List<MenuItem>();
        void Refresh()
        {
            foreach (var item in contributed) menu.Items.Remove(item);
            contributed.Clear();
            foreach (var binding in controller.GetPluginPaperActions(paperId, PaperActionPlacement.ContextMenu))
            {
                var item = MenuItem(binding.Action.Text, (_, _) => { });
                item.IsEnabled = binding.Action.Enabled;
                item.ToolTip = string.IsNullOrEmpty(binding.Action.ToolTip) ? null : binding.Action.ToolTip;
                item.Icon = CreatePluginTopBarIcon(item, binding.Action.Icon);
                item.Click += (_, _) => controller.InvokePluginPaperAction(
                    binding, PaperActionPlacement.ContextMenu, item,
                    menu.PlacementTarget is { } target ? Window.GetWindow(target) : null,
                    transientSource: true);
                menu.Items.Insert(insertionIndex + contributed.Count, item);
                contributed.Add(item);
            }
        }
        Refresh();
        // Paper menus may outlive their first opening. Read descriptors each time and validate
        // the registration revision again on click; stale menus cannot execute replaced actions.
        menu.Opened += (_, _) => Refresh();
    }
}
