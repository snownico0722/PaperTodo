using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    private bool _textLimitNoticeShown;

    private bool CanApplyTextReplacementWithNotice(string replacement)
    {
        var inputAllowed = CanApplyTextReplacement(replacement);
        UpdateTextLimitNotice(inputAllowed);
        return inputAllowed;
    }

    private void UpdateTextLimitNotice(bool inputAllowed)
    {
        if (inputAllowed)
        {
            _textLimitNoticeShown = false;
            return;
        }

        if (_textLimitNoticeShown)
        {
            return;
        }

        _textLimitNoticeShown = true;
        var maximumCharacters = MaxLength;
        _ = Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (Window.GetWindow(this) is not PaperWindow owner)
                {
                    return;
                }

                PaperNoticeDialog.Show(
                    owner,
                    Strings.Get("NoteInputLimitTitle"),
                    Strings.Format(
                        "NoteInputLimitMessage",
                        maximumCharacters));
            }),
            DispatcherPriority.Background);
    }
}
