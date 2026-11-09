using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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

    internal static Task<bool> TrySetTextAsync(string text)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // OLE clipboard retries and eager persistence can block. Keep both on a
        // short-lived STA so the settings window stays responsive during a copy.
        var worker = new Thread(() =>
        {
            try
            {
                var data = new System.Windows.Forms.DataObject();
                data.SetText(text, System.Windows.Forms.TextDataFormat.UnicodeText);
                System.Windows.Forms.Clipboard.SetDataObject(data, copy: true, retryTimes: 20, retryDelay: 50);
                completion.SetResult(true);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Clipboard text copy failed (0x{0:X8}): {1}", ex.HResult, ex.Message);
                completion.SetResult(false);
            }
        }) { IsBackground = true, Name = "PaperTodo clipboard copy" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
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
