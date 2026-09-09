using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private McpApiHost? _mcpApiHost;
    private McpCommandService? _mcpCommands;

    private void RefreshMcpRuntime()
    {
        if (IsExiting || !State.McpEnabled)
        {
            DisposeMcpRuntime();
            return;
        }

        _mcpCommands ??= new McpCommandService(this, PaperCommands);
        _mcpApiHost ??= new McpApiHost(
            Application.Current.Dispatcher,
            _mcpCommands);
        _mcpApiHost.Start();
    }

    private void DisposeMcpRuntime()
    {
        _mcpApiHost?.Dispose();
        _mcpApiHost = null;
        _mcpCommands = null;
    }

    private void ToggleMcpEnabled()
    {
        State.McpEnabled = !State.McpEnabled;
        RefreshMcpRuntime();
        SaveNow();
        RefreshSettingsRegions("labs.mcp");
    }

    private void ToggleMcpBlankWrites()
    {
        State.McpAllowBlankWrites = !State.McpAllowBlankWrites;
        SaveNow();
        RefreshSettingsRegions("labs.mcp");
    }

    private void ToggleMcpFullWrites()
    {
        State.McpAllowFullWrites = !State.McpAllowFullWrites;
        SaveNow();
        RefreshSettingsRegions("labs.mcp");
    }

    private void ToggleMcpDeletes()
    {
        State.McpAllowDeletes = !State.McpAllowDeletes;
        SaveNow();
        RefreshSettingsRegions("labs.mcp");
    }

    internal bool TryCommitMcpMutation()
    {
        MarkDirty();
        return TrySaveNow(sync: true);
    }

    internal void RunMcpPostCommitUi(Action update)
    {
        try
        {
            update();
            return;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"PaperTodo post-commit UI refresh failed; retrying at ContextIdle: {ex}");
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || IsExiting)
        {
            return;
        }

        try
        {
            _ = dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    if (IsExiting) return;
                    try
                    {
                        update();
                    }
                    catch (Exception ex)
                    {
                        // The business mutation is already persisted. Keep that success result,
                        // but do not turn an arbitrary UI exception into a global capsule/tray
                        // rebuild that can hide the real failing surface.
                        Trace.WriteLine($"PaperTodo post-commit UI retry failed: {ex}");
                    }
                }),
                DispatcherPriority.ContextIdle);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"PaperTodo could not schedule post-commit UI retry: {ex}");
        }
    }

    internal void RollbackMcpCreatedPaper(PaperData paper)
    {
        State.Papers.Remove(paper);
        if (_windows.TryGetValue(paper.Id, out var window))
        {
            TryExitCleanup(() => window.CloseForReal());
            _windows.Remove(paper.Id);
        }
        _visibilityAnimationVersions.Remove(paper.Id);
        TryExitCleanup(NotifyTodoReminderCollectionChanged);
        TryExitCleanup(() => ArrangeDeepCapsules(animate: false));
        TryExitCleanup(RefreshTrayMenu);
    }

    internal void FinalizeMcpPaperCreated(PaperData paper, bool show)
    {
        paper.IsVisible = show;
        RefreshTrayMenu();
        if (show) ShowPaper(paper);
        else ArrangeDeepCapsules(animate: false);

        if (paper.Type == PaperTypes.Todo)
        {
            RefreshCapsuleEligibilityForLinkedPapers(
                paper.Items.Select(item => item.LinkedPaperId));
        }
    }

    internal void RefreshMcpTodoPaper(PaperData paper)
    {
        if (_windows.TryGetValue(paper.Id, out var window))
        {
            window.RefreshTodoRowsForExternalMutation();
        }
        NotifyTodoReminderCollectionChanged();
        RefreshCapsuleEligibilityForLinkedPapers(
            paper.Items.Select(item => item.LinkedPaperId));
    }

    internal void RefreshTodoPapersForExternalLinkMutation(
        IReadOnlyCollection<string> paperIds)
    {
        if (paperIds.Count == 0)
        {
            return;
        }

        foreach (var paperId in paperIds)
        {
            if (_windows.TryGetValue(paperId, out var window))
            {
                window.RefreshTodoRowsForExternalMutation();
            }
        }
        RefreshCapsuleEligibilityForLinkedPapers();
    }

    internal void RefreshMcpNotePaper(PaperData paper)
    {
        if (_windows.TryGetValue(paper.Id, out var window))
        {
            window.RefreshNoteForExternalChange();
        }
        // Note content can change whether a linked paper behaves as a script capsule, so linked
        // Todo rows still need reconciliation even though the tray title itself does not.
        RefreshTodoRowsForLinkedPaper(paper.Id);
    }

    internal void FinalizeMcpPaperDeletion(
        PaperData deleted,
        PaperData? replacement,
        bool refreshLinkedTodos)
    {
        deleted.IsVisible = false;
        NextVisibilityAnimationVersion(deleted.Id);
        if (_windows.TryGetValue(deleted.Id, out var window))
        {
            RestoreExperimentalPassiveForWindow(window);
            window.CloseForReal();
            _windows.Remove(deleted.Id);
        }
        _visibilityAnimationVersions.Remove(deleted.Id);
        NotifyTodoReminderCollectionChanged();

        if (refreshLinkedTodos)
        {
            foreach (var todo in State.Papers.Where(paper => paper.Type == PaperTypes.Todo))
            {
                if (_windows.TryGetValue(todo.Id, out var todoWindow))
                {
                    todoWindow.RefreshTodoRowsForExternalChange();
                }
            }
            RefreshCapsuleEligibilityForLinkedPapers();
        }

        if (replacement != null)
        {
            replacement.IsVisible = true;
            ShowPaper(replacement);
        }
        ArrangeDeepCapsules(animate: false);
        RefreshTrayMenu();
    }

    internal void RefreshMcpAfterRollback()
    {
        TryExitCleanup(() => ArrangeDeepCapsules(animate: false));
        TryExitCleanup(RefreshTrayMenu);
    }

    private static string BuildAiMcpSkill()
    {
        return string.Join(
            Environment.NewLine,
            "# PaperTodo MCP Skill",
            "",
            "Use the MCP server named `papertodo` as the user's lightweight PaperTodo workspace.",
            "",
            "## Workflow",
            "- Call `list_papers` first when a paper id is unknown.",
            "- Call `get_paper` before replacing existing todo text, completion state, or note content.",
            "- Prefer additive operations (`create_todo_paper`, `create_note`, `add_todos`) when they satisfy the request.",
            "- Use `update_todo`, `write_note`, `set_todo_reminder`, and delete tools only when the requested mutation requires them.",
            "- Preserve the user's existing paper structure unless the user explicitly asks to reorganize it.",
            "- Treat permission errors as PaperTodo policy, not as transport failures; do not retry a rejected mutation with a more destructive tool.",
            "- For reminders, use an explicit future ISO 8601 time with UTC offset.",
            "",
            "## Available tools",
            "`list_papers`, `get_paper`, `create_todo_paper`, `create_note`, `add_todos`,",
            "`update_todo`, `set_todo_reminder`, `write_note`, `delete_paper`, `delete_todo`.",
            "",
            "Connection details are intentionally separate. Use PaperTodo's “Copy JSON config” button to configure the MCP client.");
    }

    private static string BuildJsonMcpConfiguration()
    {
        var executable = Environment.ProcessPath ??
            Path.Combine(AppContext.BaseDirectory, "PaperTodo.exe");
        return JsonSerializer.Serialize(
            new
            {
                mcpServers = new
                {
                    papertodo = new
                    {
                        command = executable,
                        args = new[] { McpBridge.CommandLineSwitch }
                    }
                }
            },
            new JsonSerializerOptions { WriteIndented = true });
    }
}
