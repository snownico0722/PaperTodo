using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private readonly record struct TodoPasteLimitNotice(
        bool TooManyItems,
        bool TextTruncated);

    // Register before any PaperWindow instance is constructed. The window-level lock handler
    // mirrors the lock shield's "consume all keyboard interaction" contract, while the Todo
    // paste preflight only scans enough input to determine whether user-visible truncation occurs.
    private static readonly bool TodoSafetyGuardsRegistered = RegisterTodoSafetyGuards();

    private static bool RegisterTodoSafetyGuards()
    {
        EventManager.RegisterClassHandler(
            typeof(PaperWindow),
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(OnInteractionLockPreviewKeyDown),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(TodoTextBox),
            DataObject.PastingEvent,
            new DataObjectPastingEventHandler(OnTodoSafetyPasting),
            handledEventsToo: true);
        return true;
    }

    private static void OnInteractionLockPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is PaperWindow { _advancedInteractionLocked: true })
        {
            // The lock shield already consumes all child keyboard input. Window-level handlers
            // tunnel before the shield, so consume the same input here before Escape/Undo/etc.
            e.Handled = true;
        }
    }

    private static void OnTodoSafetyPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TodoTextBox editor ||
            Window.GetWindow(editor) is not PaperWindow window ||
            window._paper.Type != PaperTypes.Todo)
        {
            return;
        }

        if (!TryGetTodoPastingText(e.DataObject, out var raw))
        {
            return;
        }

        var notice = AnalyzeTodoPasteLimits(raw, editor);
        if (!notice.TooManyItems && !notice.TextTruncated)
        {
            return;
        }

        _ = window.Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                var messages = new List<string>(2);
                if (notice.TooManyItems)
                {
                    messages.Add(Strings.Format(
                        "TodoPasteItemLimitMessage",
                        MaxPastedTodoLines));
                }
                if (notice.TextTruncated)
                {
                    messages.Add(Strings.Format(
                        "TodoPasteTextLimitMessage",
                        TodoTextMaxLength));
                }

                PaperNoticeDialog.Show(
                    window,
                    Strings.Get("TodoPasteTruncatedTitle"),
                    string.Join(Environment.NewLine, messages));
            }),
            DispatcherPriority.Background);
    }

    private static bool TryGetTodoPastingText(
        IDataObject dataObject,
        out string raw)
    {
        raw = "";
        try
        {
            raw = dataObject.GetDataPresent(DataFormats.UnicodeText)
                ? dataObject.GetData(DataFormats.UnicodeText) as string ?? ""
                : dataObject.GetDataPresent(DataFormats.Text)
                    ? dataObject.GetData(DataFormats.Text) as string ?? ""
                    : "";
        }
        catch
        {
            raw = "";
            return false;
        }

        return raw.Length > 0;
    }

    private static TodoPasteLimitNotice AnalyzeTodoPasteLimits(
        string raw,
        TodoTextBox editor)
    {
        var meaningfulCount = 0;
        var tooManyItems = false;
        var textTruncated = false;
        string? firstInsertedLine = null;
        string? lastInsertedLine = null;

        foreach (var rawLine in EnumerateTodoClipboardLines(raw))
        {
            var cleaned = CleanPastedTodoLine(rawLine);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                continue;
            }

            meaningfulCount++;
            if (meaningfulCount > MaxPastedTodoLines)
            {
                tooManyItems = true;
                break;
            }

            firstInsertedLine ??= cleaned;
            lastInsertedLine = cleaned;
            if (cleaned.Length > TodoTextMaxLength)
            {
                textTruncated = true;
            }
        }

        var originalText = editor.Text ?? "";
        var selectionStart = Math.Clamp(editor.SelectionStart, 0, originalText.Length);
        var selectionLength = Math.Clamp(
            editor.SelectionLength,
            0,
            originalText.Length - selectionStart);

        if (meaningfulCount <= 1)
        {
            // HandleTodoPaste leaves a single logical line to the native TextBox paste path.
            // Warn when MaxLength will keep part of the incoming text out of the editor.
            var projectedLength =
                (long)originalText.Length - selectionLength + raw.Length;
            textTruncated |= projectedLength > TodoTextMaxLength;
            return new TodoPasteLimitNotice(tooManyItems, textTruncated);
        }

        if (firstInsertedLine != null && lastInsertedLine != null)
        {
            var prefixLength = selectionStart;
            var suffixLength = originalText.Length - (selectionStart + selectionLength);
            var firstLength = Math.Min(firstInsertedLine.Length, TodoTextMaxLength);
            var lastLength = Math.Min(lastInsertedLine.Length, TodoTextMaxLength);
            textTruncated |=
                (long)prefixLength + firstLength > TodoTextMaxLength ||
                (long)lastLength + suffixLength > TodoTextMaxLength;
        }

        return new TodoPasteLimitNotice(tooManyItems, textTruncated);
    }

    private static IEnumerable<string> EnumerateTodoClipboardLines(string raw)
    {
        var start = 0;
        for (var index = 0; index < raw.Length; index++)
        {
            if (raw[index] is not ('\r' or '\n'))
            {
                continue;
            }

            yield return raw[start..index];
            if (raw[index] == '\r' &&
                index + 1 < raw.Length &&
                raw[index + 1] == '\n')
            {
                index++;
            }
            start = index + 1;
        }

        yield return raw[start..];
    }

    internal void PreserveTriggeredTodoReminderInHistory(
        string itemId,
        DateTimeOffset reminderAt)
    {
        PreserveTriggeredTodoReminderInHistory(_undoStack, itemId, reminderAt);
        PreserveTriggeredTodoReminderInHistory(_redoStack, itemId, reminderAt);
    }

    private static void PreserveTriggeredTodoReminderInHistory(
        IEnumerable<List<PaperItem>> history,
        string itemId,
        DateTimeOffset reminderAt)
    {
        foreach (var snapshot in history)
        {
            var item = snapshot.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
            if (item?.ReminderAt == reminderAt)
            {
                // Delivery is runtime truth for this exact scheduled reminder. Replaying an older
                // unrelated snapshot must not turn the same already-surfaced reminder pending again.
                item.ReminderTriggered = true;
            }
        }
    }
}
