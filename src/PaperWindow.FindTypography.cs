using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private void RefreshBuiltInFindTypographyFromOwner()
    {
        if (!IsBuiltInFindOpen || _findHost == null)
        {
            return;
        }

        UpdateBuiltInFindVisuals();
        _findHost.Language = AppTypography.Language;
        AppTypography.ApplyTextRendering(_findHost);
        RepositionBuiltInFindPopup();
    }

    private void QueueBuiltInFindPopupFocusExitCheck()
    {
        if (!IsBuiltInFindOpen)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke((Action)(() =>
        {
            if (!IsBuiltInFindOpen ||
                _findHost?.IsKeyboardFocusWithin == true ||
                IsActive)
            {
                return;
            }

            HideBuiltInFind(restoreFocus: false);
            ScheduleExperimentalAutoCollapse(blockedAtDeactivation: false);
        }), DispatcherPriority.ContextIdle);
    }
}
