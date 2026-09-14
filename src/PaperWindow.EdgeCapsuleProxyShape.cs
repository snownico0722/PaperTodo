namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal bool TryAcquireProxySource(out EdgeCapsuleProxySourceLease? lease)
    {
        lease = null;
        return _windowLifecycle == PaperWindowLifecycleState.Alive && !IsClosed &&
            _edgeCapsuleHost != null && _edgeCapsuleHost.TryAcquireProxySource(out lease);
    }
}
