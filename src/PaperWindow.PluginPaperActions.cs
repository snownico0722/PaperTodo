using System.Windows.Controls;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private void AttachPluginPaperMenuActions(ContextMenu menu) =>
        // The shared paper menu already rebuilds every item on Opened. Registering
        // another handler here would accumulate callbacks on each rebuild.
        AttachPluginPaperMenuActions(menu, _controller, _paper.Id, refreshOnOpen: false);

    internal static void AttachPluginPaperMenuActions(ContextMenu menu, AppController controller,
        string paperId, bool refreshOnOpen = true)
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
        // Standalone plugin menus still refresh lazily, but the paper's single builder
        // already owns refresh and must not accumulate another Opened subscriber.
        if (refreshOnOpen)
            menu.Opened += (_, _) => Refresh();
    }
}
