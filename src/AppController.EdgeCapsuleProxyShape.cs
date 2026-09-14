namespace PaperTodo;

public sealed partial class AppController
{
    internal void UpdateEdgeCapsuleProxyShape(PaperWindow window,
        EdgeCapsulePresentationFrame frame, EdgeCapsuleTransition? transition)
    {
        if (_edgeCapsuleQueueCompositionProxyByWindow.TryGetValue(window, out var proxy))
            proxy.UpdateProxyShape(window, frame, transition);
    }
}
