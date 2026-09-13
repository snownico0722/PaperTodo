using System.Windows;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleHost
{
    private bool _startupFirstPresentationBatchActive;

    internal bool ApplyStartupFirstPresentation(EdgeCapsulePresentationFrame frame)
    {
        if (_disposed || Window.IsVisible || !frame.Visible)
        {
            return false;
        }

        _startupFirstPresentationBatchActive = true;
        try
        {
            return Apply(frame);
        }
        finally
        {
            _startupFirstPresentationBatchActive = false;
        }
    }

    internal bool RevealStartupFirstPresentation(EdgeCapsulePresentationFrame frame)
    {
        if (_disposed ||
            !Window.IsVisible ||
            _appliedFrame != frame ||
            !MatchesNativePresentationLayout(frame))
        {
            if (!_disposed)
            {
                ResetForFreshApply();
            }
            return false;
        }

        ApplyCommittedVisualState(frame, reveal: true);
        return true;
    }

    private void ApplyCommittedVisualState(
        EdgeCapsulePresentationFrame frame,
        bool reveal)
    {
        var contentOpacity = reveal
            ? Math.Clamp(frame.ContentOpacity, 0, 1)
            : 0;
        if (Math.Abs(Root.Opacity - contentOpacity) > 0.001)
        {
            Root.Opacity = contentOpacity;
        }

        var hitTestVisible = reveal && frame.IsHitTestVisible;
        if (Root.IsHitTestVisible != hitTestVisible)
        {
            Root.IsHitTestVisible = hitTestVisible;
        }

        var outlineVisibility = frame.OutlineVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (Outline.Visibility != outlineVisibility)
        {
            Outline.Visibility = outlineVisibility;
        }

        var opacity = reveal
            ? Math.Clamp(frame.Opacity, 0, 1)
            : 0;
        if (Math.Abs(Window.Opacity - opacity) > 0.001)
        {
            Window.Opacity = opacity;
        }
    }
}
