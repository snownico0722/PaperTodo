namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal bool HasDeepCapsuleFloatingDragCover =>
        _deepCapsuleFloatingDragHost != null;
}
