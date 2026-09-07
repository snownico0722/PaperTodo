using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private void ExportNoteHtml()
    {
        if (_paper.Type != PaperTypes.Note || !IsCurrentBodyProviderMarkdown) return;
        try
        {
            _controller.PrepareExternalPaperOperation();
            var title = _controller.PaperCapsuleTitle(_paper);
            var name = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (name.Length == 0) name = "Note";
            if (name.Length > 80) name = name[..80];
            var dialog = new SaveFileDialog
            {
                Title = Strings.Get("NoteHtmlExport"),
                Filter = "HTML (*.html)|*.html",
                DefaultExt = ".html",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = DateTime.Now.ToString("yyyy-MM-dd") + "_" + name + ".html"
            };
            if (dialog.ShowDialog(this) != true) return;
            var html = NoteHtmlExport.Render(title, _paper.Content ?? "", _controller.ImageStore);
            NoteHtmlExport.Save(dialog.FileName, html);
            MessageBox.Show(this, Strings.Format("NoteHtmlSaved", dialog.FileName), Strings.Get("NoteHtmlExport"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Strings.Get("NoteHtmlExport"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
