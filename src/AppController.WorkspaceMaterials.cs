using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PaperTodo;

public sealed partial class AppController
{
    internal PaperData EnsureWorkspaceMaterialsOwner()
    {
        if (WorkspaceMaterials.FindOwner(State) is { } existing) return existing;
        var owner = WorkspaceMaterials.CreateOwner(State, Strings.Get("WorkbenchMaterialNote"));
        RefreshTrayMenu();
        MarkDirty();
        return owner;
    }

    internal WorkspaceMaterialItem? FindWorkbenchMaterial(string id) =>
        WorkspaceMaterials.FindOwner(State)?.WorkspaceMaterials?.Items.GetValueOrDefault(id);

    internal string CreateWorkbenchMaterial(string kind, string title, string content,
        string source, WorkbenchPoint? point = null)
    {
        if (IsExiting || IsDataReloading) return "";
        if (content.Length > PaperWindow.NoteTextMaxLength)
            throw new InvalidOperationException(Strings.Format("NoteInputLimitMessage", PaperWindow.NoteTextMaxLength));
        var owner = EnsureWorkspaceMaterialsOwner();
        content = ImageStore.CloneForeignImageReferencesForNote(owner.Id, content);
        if (content.Length > PaperWindow.NoteTextMaxLength)
            throw new InvalidOperationException(Strings.Format("NoteInputLimitMessage", PaperWindow.NoteTextMaxLength));
        var document = owner.WorkspaceMaterials!;
        var item = new WorkspaceMaterialItem
        {
            Kind = WorkspaceMaterialKinds.Normalize(kind), Title = title, Content = content, Source = source
        };
        document.Items.Add(item.Id, item);
        if (point is { } p)
            document.Layout.Cards[item.Id] = new WorkbenchCardState
            {
                X = p.X, Y = p.Y,
                Width = kind is WorkspaceMaterialKinds.File or WorkspaceMaterialKinds.Link ? 120
                    : kind == WorkspaceMaterialKinds.Node ? 220 : 320,
                Height = kind is WorkspaceMaterialKinds.File or WorkspaceMaterialKinds.Link ? 128
                    : kind == WorkspaceMaterialKinds.Node ? 120 : 260,
                Preferred = true
            };
        MarkDirty();
        return item.Id;
    }

    internal void UpdateWorkbenchMaterial(string id, string title, string content)
    {
        if (IsExiting || IsDataReloading || FindWorkbenchMaterial(id) is not { } item) return;
        if (content.Length > PaperWindow.NoteTextMaxLength)
            throw new InvalidOperationException(Strings.Format("NoteInputLimitMessage", PaperWindow.NoteTextMaxLength));
        if (item.Title == title && item.Content == content) return;
        item.Title = title;
        item.Content = content;
        MarkDirty();
    }

    internal void AttachWorkspaceMaterialImagePaste(TextBox editor, Action<Exception> reportFailure)
    {
        DataObject.AddPastingHandler(editor, (_, e) =>
        {
            if (IsExiting || IsDataReloading)
            {
                e.CancelCommand();
                return;
            }
            try
            {
                var text = e.DataObject.GetData(DataFormats.UnicodeText) as string ??
                    e.DataObject.GetData(DataFormats.Text) as string;
                if (string.IsNullOrEmpty(text)) return;
                var references = MarkdownImageReferences.Enumerate(text).ToArray();
                if (references.Length == 0) return;

                // Native TextBox paste clips at MaxLength. Image references must remain whole,
                // including when cloning expands a short source ID to an eight-digit ID.
                var maximumLength = (long)editor.Text.Length - editor.SelectionLength + text.Length +
                    references.Sum(reference => 8 - reference.ImageId.Length);
                if (maximumLength > PaperWindow.NoteTextMaxLength)
                    throw new InvalidOperationException(Strings.Format("NoteInputLimitMessage", PaperWindow.NoteTextMaxLength));
                var owner = EnsureWorkspaceMaterialsOwner();
                var cloned = ImageStore.CloneForeignImageReferencesForNote(owner.Id, text);
                if (cloned == text) return;

                // Rewrite this paste, not TextChanged: the focused editor and its native undo
                // stack must retain the owned IDs instead of repeatedly cloning the same source.
                var replacement = new DataObject();
                replacement.SetData(DataFormats.UnicodeText, cloned);
                replacement.SetData(DataFormats.Text, cloned);
                e.DataObject = replacement;
                e.FormatToApply = DataFormats.UnicodeText;
            }
            catch (Exception ex)
            {
                e.CancelCommand();
                reportFailure(ex);
            }
        });
    }

    internal void SetWorkbenchMaterialCollapsed(string id, bool collapsed)
    {
        if (IsExiting || IsDataReloading || FindWorkbenchMaterial(id) is not { } item || item.Collapsed == collapsed) return;
        item.Collapsed = collapsed;
        MarkDirty();
    }

    internal void UpdateWorkbenchMaterialSource(string id, string source)
    {
        if (IsExiting || IsDataReloading || FindWorkbenchMaterial(id) is not { } item || item.Source == source) return;
        item.Source = source;
        MarkDirty();
    }

    internal void DeleteWorkbenchMaterial(string id)
    {
        if (IsExiting || IsDataReloading || WorkspaceMaterials.FindOwner(State)?.WorkspaceMaterials is not { } document ||
            !document.Items.Remove(id)) return;
        // Removing a file entry never deletes its source file. Image retirement remains
        // owned by the normal image-reference/undo protection and StateStore lifecycle.
        document.Normalize(State.Papers.Where(p => p.WorkspaceMaterials == null).Select(p => p.Id));
        MarkDirty();
    }

    internal ImageSource? ReadWorkbenchMaterialImage(string id)
    {
        if (FindWorkbenchMaterial(id) is not { Kind: WorkspaceMaterialKinds.Image } item) return null;
        var imageId = MarkdownImageReferences.CollectImageIds(item.Content).FirstOrDefault();
        return imageId == null ? null : ImageStore.GetBitmapSource(imageId, targetPixelWidth: 1024);
    }

    internal IReadOnlyList<string> ImportWorkbenchData(IDataObject data, WorkbenchPoint point)
    {
        if (IsExiting || IsDataReloading) return [];
        var imported = new List<string>();
        var owner = EnsureWorkspaceMaterialsOwner();
        var prepared = new List<(string Kind, string Title, string Content, string Source)>();
        // Prepare the whole drop before publishing cards. Failed imports do not leave a
        // partially moved source or a half-created group of cards in the user's layout.
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var path in files)
            {
                var title = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                if (File.Exists(path) && NoteImageStore.IsSupportedImageFile(path))
                {
                    var image = ImageStore.ImportImageFile(owner.Id, path);
                    prepared.Add((WorkspaceMaterialKinds.Image, title, MarkdownImageReferences.CreateReference(image.Id), ""));
                }
                else
                {
                    if (!File.Exists(path) && !Directory.Exists(path))
                        throw new FileNotFoundException(Strings.Get("WorkbenchMaterialMissing"), path);
                    var absolutePath = Path.GetFullPath(path);
                    prepared.Add((WorkspaceMaterialKinds.File, title, "", absolutePath));
                }
            }
        }
        else if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            var image = ImageStore.ImportBitmapSource(owner.Id, bitmap);
            prepared.Add((WorkspaceMaterialKinds.Image, Strings.Get("WorkbenchImageMaterial"),
                MarkdownImageReferences.CreateReference(image.Id), ""));
        }
        else
        {
            var text = data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string
                ?? data.GetData("UniformResourceLocatorW") as string ?? data.GetData("UniformResourceLocator") as string;
            if (string.IsNullOrWhiteSpace(text)) return [];
            if (text.Length > PaperWindow.NoteTextMaxLength)
                throw new InvalidOperationException(Strings.Format("NoteInputLimitMessage", PaperWindow.NoteTextMaxLength));
            var trimmed = text.Trim().TrimEnd('\0');
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                prepared.Add((WorkspaceMaterialKinds.Link, uri.Host, "", trimmed));
            else
                prepared.Add((WorkspaceMaterialKinds.Text, "", text, ""));
        }
        foreach (var value in prepared)
        {
            var at = new WorkbenchPoint(point.X + (imported.Count % 3) * 340,
                point.Y + (imported.Count / 3) * 280);
            imported.Add(CreateWorkbenchMaterial(value.Kind, value.Title, value.Content, value.Source, at));
        }
        return imported;
    }

    internal void OpenWorkbenchMaterial(string id)
    {
        if (FindWorkbenchMaterial(id) is not { } item || string.IsNullOrWhiteSpace(item.Source)) return;
        if (item.Kind == WorkspaceMaterialKinds.Link)
        {
            if (!Uri.TryCreate(item.Source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException(Strings.Get("WorkbenchMaterialMissing"));
        }
        else if (item.Kind != WorkspaceMaterialKinds.File || (!File.Exists(item.Source) && !Directory.Exists(item.Source)))
            throw new FileNotFoundException(Strings.Get("WorkbenchMaterialMissing"), item.Source);
        Process.Start(new ProcessStartInfo(item.Source) { UseShellExecute = true });
    }

    internal void OpenWorkspaceMaterialsNote()
    {
        var owner = EnsureWorkspaceMaterialsOwner();
        CloseWorkbenchForDesktopAction();
        SetPaperCollapsedRuntime(owner, collapsed: false, animate: false, saveGeometry: false);
        ShowPaper(owner);
    }
}
