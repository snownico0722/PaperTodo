using System.Diagnostics;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleHost
{
    [System.Diagnostics.Conditional("DEBUG")]
    internal void TraceCompositionVisibility(string context)
    {
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.visibility {context} " +
            WindowNative.DescribeCompositionVisibility(Handle) +
            $" wpfVisible={Window.IsVisible} opacity={Window.Opacity:F3} " +
            $"contentOpacity={Root.Opacity:F3} " +
            $"layoutValid={VisualSurface.IsMeasureValid && VisualSurface.IsArrangeValid} " +
            $"surface={_appliedFrame.Surface} " +
            $"bounds={_appliedFrame.Bounds.Left},{_appliedFrame.Bounds.Top}," +
            $"{_appliedFrame.Bounds.Width}x{_appliedFrame.Bounds.Height}");
#endif
    }

    /// <summary>
    /// Drains this host's WPF layout without crossing the desktop-composition boundary. The queue
    /// compositor prepares every endpoint first, then performs one Render dispatch and one DWM
    /// flush for the complete queue instead of paying both barriers once per paper.
    /// </summary>
    internal bool PrepareCompositionSourceLayoutForBatchHandoff()
    {
        if (_disposed || !Window.IsVisible)
        {
            return false;
        }

        try
        {
            Window.UpdateLayout();
            VisualSurface.UpdateLayout();
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "Edge capsule batched endpoint layout failed. Paper={0}; Exception={1}",
                _options.DiagnosticId,
                ex);
            return false;
        }
    }
}
