using System;
using System.Windows;

namespace PaperTodo;

public static class ClipboardHelper
{
    public static bool TrySetText(string text)
    {
        try
        {
            Clipboard.SetText(text ?? "");
            return true;
        }
        catch
        {
            // OLE/COM clipboard exception or locked by another process
            return false;
        }
    }

}
