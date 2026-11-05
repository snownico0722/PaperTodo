using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PaperTodo;

public static class ClipboardHelper
{
    public static void SetImage(BitmapSource bitmap)
    {
        var dataObject = new DataObject();
        AddImageFormats(dataObject, bitmap);
        Clipboard.SetDataObject(dataObject, copy: true);
    }

    internal static void AddImageFormats(DataObject data, BitmapSource bitmap)
    {
        var pngStream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(pngStream);
        pngStream.Position = 0;

        // The DataObject owns this managed stream until clipboard consumers finish.
        // Keep PNG alpha intact while offering Bitmap to older consumers.
        data.SetData("PNG", pngStream, autoConvert: false);
        data.SetImage(bitmap);
    }

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
