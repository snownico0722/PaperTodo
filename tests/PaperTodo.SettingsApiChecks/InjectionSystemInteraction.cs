using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using PaperTodo;

internal static partial class InjectionSystemRunner
{
    private static async Task Typography(AppController c)
    {
        var window = Windows(c)["note"];
        var box = Field<MarkdownPaperBodySession>(window, "_markdownBodySession").NoteBox!;
        var original = box.FontSize;
        var originalText = box.Text;
        // TextEditor.Text is a document-loading API, not a user undoable edit.
        box.Document.Replace(0, box.Document.TextLength, "pending typography draft");
        box.Select(2, 4);
        Check(box.Document.UndoStack.CanUndo && box.SelectionStart == 2 && box.SelectionLength == 4,
            "Typography fixture must contain an undoable edit and selection before applying anything.");
        var document = box.Document;
        var preference = c.State.Zoom;
        Set(c, ("appearance.fontScale", 1.2));
        await Settle();
        Check(c.State.Zoom == preference && AppTypography.ScaleFactor == 1.2,
            "Font override replaced ordinary preferences or failed to reach typography.");
        Check(ReferenceEquals(box, Field<MarkdownPaperBodySession>(window, "_markdownBodySession").NoteBox), "Typography replaced the editor.");
        Check(ReferenceEquals(document, box.Document), "Typography replaced the document.");
        Check(box.Text == "pending typography draft", "Typography replaced the draft: " + box.Text);
        Check(box.SelectionStart == 2 && box.SelectionLength == 4,
            $"Typography changed selection to {box.SelectionStart}+{box.SelectionLength}.");
        Check(box.Document.UndoStack.CanUndo, "Typography cleared the existing undo stack.");
        Check(Math.Abs(box.FontSize - original * 1.2) < 0.11,
            $"Actual note font did not follow its typed scale: {original} -> {box.FontSize}.");
        Remove(c, "appearance.fontScale");
        await Settle();
        Check(Math.Abs(box.FontSize - original) < 0.11 && c.State.Zoom == preference, "Actual font did not recover on removal.");
        box.Undo(); Check(box.Text == originalText, "Real undo no longer restores the original note.");
        box.Redo(); Check(box.Text == "pending typography draft", "Real redo no longer restores the edited note.");
    }

    private static void Conflicts(AppController c)
    {
        Set(c, ("appearance.fontScale", 1.1), ("todo.paste.maxItems", 100));
        using var view = new GoodLockView(c);
        using var dialogs = new FixtureDialogs();
        view.Command("todo.paste.maxItems", "ResetCommand");
        const string external = "{\"appearance.fontScale\":1.05,\"todo.paste.maxItems\":100,\"external.keep\":[1,2,3]}";
        File.WriteAllText(UserInjection.FilePath, external);
        view.Click("ApplyButton");
        Check(dialogs.Count == 1, "Conflict was silently accepted or not reported to the user.");
        Check(File.ReadAllText(UserInjection.FilePath) == external && view.Draft != "{}" && AppTypography.ScaleFactor == 1.1,
            "Conflict changed external bytes, lost the user's draft or adopted unaccepted settings.");
        view.Click("ApplyButton");
        var accepted = UserInjection.Read();
        Check(dialogs.Count == 1, "Explicit retry raised another dialog instead of applying the refreshed revision.");
        Check(!accepted.Overrides.ContainsKey("todo.paste.maxItems") && accepted.Overrides.ContainsKey("external.keep") &&
            AppTypography.ScaleFactor == 1.05 && view.Draft == "{}", "Explicit conflict retry did not combine the fresh file with sparse local edits.");
    }

    // Drive the actual OK button of native MessageBoxes on this fixture's own UI thread.
    // Sending WM_COMMAND directly to a dialog is not equivalent to clicking its button.
    private sealed class FixtureDialogs : IDisposable
    {
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
        private readonly HashSet<IntPtr> _seen = [];
        public int Count => _seen.Count;
        public FixtureDialogs()
        {
            _timer.Tick += (_, _) => EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
            {
                var name = new StringBuilder(64); GetClassName(hwnd, name, name.Capacity);
                if (name.ToString() != "#32770") return true;
                if (_seen.Add(hwnd))
                {
                    var text = new List<string>();
                    EnumChildWindows(hwnd, (child, _) =>
                    {
                        var value = new StringBuilder(2048);
                        GetWindowText(child, value, value.Capacity);
                        if (value.Length > 0) text.Add(value.ToString());
                        return true;
                    }, IntPtr.Zero);
                    Console.WriteLine("FIXTURE_DIALOG " + string.Join(" | ", text));
                }
                var ok = GetDlgItem(hwnd, 1);
                if (ok != IntPtr.Zero) SendMessage(ok, 0x00F5, IntPtr.Zero, IntPtr.Zero);
                else PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
            _timer.Start();
        }
        public void Dispose() => _timer.Stop();
    }
    private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumWindowProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
