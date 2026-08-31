using System;
using System.Windows;

namespace PaperTodo;

public static class ClipboardHelper
{
    public static bool TryGetText(out string? text)
    {
        text = null;
        try
        {
            var dataObject = Clipboard.GetDataObject();
            if (dataObject == null)
            {
                return false;
            }

            if (!dataObject.GetDataPresent(DataFormats.UnicodeText))
            {
                return false;
            }

            text = dataObject.GetData(DataFormats.UnicodeText) as string;
            return true;
        }
        catch
        {
            // OLE/COM clipboard exception or locked by another process
            return false;
        }
    }
}
