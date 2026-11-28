using PaperTodo.Plugin;

namespace PaperTodo.Plugin.GoodLock;

public sealed class GoodLockPlugin : IPaperBodyPlugin
{
    public IPaperBodySession Create(PaperBodyContext context) => new GoodLockSession(context);
}
