using System.Windows.Controls;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private void AttachPluginPaperMenuActions(ContextMenu menu) =>
        AttachPluginPaperMenuActions(menu, _controller, _paper.Id);

    internal static void AttachPluginPaperMenuActions(ContextMenu menu, AppController controller, string paperId)
    {
        var insertionIndex = menu.Items.Count;
        var contributed = new List<MenuItem>();
        void Refresh()
        {
            foreach (var item in contributed) menu.Items.Remove(item);
            contributed.Clear();
            foreach (var binding in controller.GetPluginPaperActions(paperId))
            {
                // Use the existing menu renderer unchanged: no icon, tooltip or custom template.
                var item = MenuItem(binding.Action.Text, (_, _) => controller.InvokePluginPaperAction(binding));
                menu.Items.Insert(insertionIndex + contributed.Count, item);
                contributed.Add(item);
            }
        }
        Refresh();
        // Cached paper/capsule menus read current registrations; stale callbacks are also rejected.
        menu.Opened += (_, _) => Refresh();
    }
}
