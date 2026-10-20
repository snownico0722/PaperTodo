using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PaperTodo;

public static class ClipboardHelper
{
    public static void SetImage(BitmapSource bitmap)
    {
        var data = new DataObject();
        AddImageFormats(data, bitmap);
        // Flush both formats so pasting also works after this process exits.
        Clipboard.SetDataObject(data, copy: true);
    }

    internal static void AddImageFormats(DataObject data, BitmapSource bitmap)
    {
        // The DataObject retains this managed stream until the clipboard consumes it.
        var png = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(png);
        png.Position = 0;

        data.SetData("PNG", png, autoConvert: false);
        data.SetImage(bitmap);
    }

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
