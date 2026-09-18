using System.ComponentModel;
using System.Windows.Interop;

namespace PaperTodo;

// Comparison branch only: isolate whether WPF's stale native-owner bookkeeping causes
// the observed Z0 -> Z2/Z3 jump, without explicitly choosing a replacement foreground.
public sealed partial class PaperWindow
{
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive &&
            _windowSwitcherHiddenOwner != System.IntPtr.Zero)
        {
            new WindowInteropHelper(this).Owner = _windowSwitcherHiddenOwner;
        }

        base.OnClosing(e);
    }
}
