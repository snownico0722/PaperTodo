using System.Windows;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    internal PaperInjectionApplyResult ApplyUserInjection(PaperInjectionPatch? patch)
    {
        if (!IsRunning) throw new UserInjectionException("app_exiting", "PaperTodo is exiting.");
        return UserInjection.Apply(patch, RefreshUserInjectionRuntime);
    }

    internal void RestartForUserInjection(string? returnToPaperId)
    {
        if (!IsRunning) throw new UserInjectionException("app_exiting", "PaperTodo is exiting.");
        var app = (App)Application.Current;
        // Return from the plugin call before the normal exit path releases its session.
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsRunning) return;
            app.RequestRestartAfterExit(returnToPaperId);
            Exit();
            if (IsRunning) app.CancelRestartAfterExit();
        }));
    }

    internal void RestoreUserInjectionPaper(IReadOnlyList<string> args)
    {
        for (var index = 0; index + 1 < args.Count; index++)
        {
            if (!string.Equals(args[index], "--return-paper", StringComparison.Ordinal)) continue;
            var paper = State.Papers.FirstOrDefault(paper =>
                string.Equals(paper.Id, args[index + 1], StringComparison.Ordinal));
            if (paper != null) ApplyPaperPresentation(paper, PaperPresentationAction.Expand, activate: true);
            return;
        }
    }
}
