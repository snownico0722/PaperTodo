using System.Collections;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static async Task RunMaterials()
    {
        var initial = FixtureState();
        initial.UseCapsuleMode = false;
        initial.UseDeepCapsuleMode = false;
        var store = new StateStore();
        store.SaveJsonSync(store.SerializeState(initial), 1);
        using var controller = new AppController();
        await controller.StartAsync(createDefaultPaper: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var desktop = controller.State.Papers.ToDictionary(p => p.Id, Geometry);

        controller.ToggleWorkbench();
        var desk = (WorkbenchWindow)Member(controller, "_workbenchWindow");
        await Until(() => desk.HostedPaperCount == 3, "Source paper editors were not mounted");

        const string excerpt = "第一段摘录\n第二段保留换行 — material-search-needle";
        var textData = new DataObject();
        textData.SetData(DataFormats.UnicodeText, excerpt);
        var textIds = controller.ImportWorkbenchData(textData, new WorkbenchPoint(1700, 120));
        Assert(textIds.Count == 1, "A multiline excerpt was split into unrelated materials");
        var textId = textIds[0];
        Assert(controller.FindWorkbenchMaterial(textId)?.Content == excerpt,
            "Imported excerpt lost its content or line breaks");

        var owner = WorkspaceMaterials.FindOwner(controller.State)!;
        Assert(owner != null && owner.Type == PaperTypes.Note && !owner.IsVisible,
            "Materials owner is not a hidden note");
        Assert(controller.State.Papers.Count(p => p.WorkspaceMaterials != null) == 1,
            "Import created more than one materials owner");
        Assert(string.IsNullOrEmpty(owner.Content),
            "Materials note persisted a second writable copy of its item content");
        Assert(controller.SnapshotCurrentWorkbench().Papers.All(p => p.Id != owner.Id),
            "Materials note recursively appeared as a card in its own canvas");

        // Renaming the note changes its presentation, never how later imports find it.
        var ownerId = owner.Id;
        controller.UpdatePaperTitle(owner, "Collected references");
        var linkData = new DataObject();
        const string url = "https://example.com/reference?q=paper#section";
        linkData.SetData(DataFormats.UnicodeText, url);
        var linkIds = controller.ImportWorkbenchData(linkData, new WorkbenchPoint(2100, 120));
        Assert(linkIds.Count == 1 && controller.FindWorkbenchMaterial(linkIds[0])?.Kind == WorkspaceMaterialKinds.Link,
            "A dropped URL did not become a link material");
        Assert(controller.FindWorkbenchMaterial(linkIds[0])?.Source == url,
            "Link material changed the source URL");
        Assert(WorkspaceMaterials.FindOwner(controller.State)?.Id == ownerId &&
               controller.State.Papers.Count(p => p.WorkspaceMaterials != null) == 1,
            "Renaming the collection created a second materials note");

        var externalFile = Path.Combine(AppContext.BaseDirectory, "external-reference.txt");
        File.WriteAllText(externalFile, "This file remains owned by the user.");
        var externalDirectory = Path.Combine(AppContext.BaseDirectory, "external-folder");
        Directory.CreateDirectory(externalDirectory);
        var imagePath = Path.Combine(AppContext.BaseDirectory, "reference.png");
        WriteMaterialFixtureImage(imagePath);
        var files = new DataObject();
        files.SetFileDropList(new StringCollection { imagePath, externalFile, externalDirectory });
        var fileIds = controller.ImportWorkbenchData(files, new WorkbenchPoint(1700, 700));
        Assert(fileIds.Count == 3, "Mixed image/file/folder drop lost a material");
        var imageId = fileIds.Single(id => controller.FindWorkbenchMaterial(id)?.Kind == WorkspaceMaterialKinds.Image);
        var fileId = fileIds.Single(id => controller.FindWorkbenchMaterial(id)?.Source == externalFile);
        var directoryId = fileIds.Single(id => controller.FindWorkbenchMaterial(id)?.Source == externalDirectory);
        Assert(controller.FindWorkbenchMaterial(fileId)?.Kind == WorkspaceMaterialKinds.File &&
               controller.FindWorkbenchMaterial(directoryId)?.Kind == WorkspaceMaterialKinds.File,
            "Ordinary files and folders were not retained as external references");
        Assert(controller.ReadWorkbenchMaterialImage(imageId) != null,
            "Imported image has no readable stored image asset");
        File.Delete(imagePath);
        Assert(controller.ReadWorkbenchMaterialImage(imageId) != null,
            "Imported image still depends on the original source file");

        var nodeId = controller.CreateWorkbenchMaterial(WorkspaceMaterialKinds.Node,
            "A compact idea", "A compact idea", "", new WorkbenchPoint(2500, 700));
        await Until(() => ActiveCards(desk).Contains(textId) && ActiveCards(desk).Contains(nodeId),
            "New material cards were not added to the live canvas");
        Assert(desk.HostedPaperCount == 3,
            "Material cards created extra ordinary PaperWindow editor sessions");

        // Edit the actual canvas control, then observe the authoritative collection item.
        var textCard = ActiveCards(desk)[textId]!;
        var body = (Border)Member(textCard, "Body");
        var textEditor = VisualDescendants<TextBox>(body).FirstOrDefault(t => t.Text == excerpt);
        Assert(textEditor != null, "Excerpt has no direct editable canvas body");
        const string updated = "从画布直接编辑\nmaterial-search-updated";
        InvokeWorkbench(desk, "FocusPaper", textCard);
        textEditor!.Focus();
        await Until(() => textEditor.IsKeyboardFocusWithin, "Material editor did not accept keyboard focus");
        textEditor.Text = updated;
        await Until(() => controller.FindWorkbenchMaterial(textId)?.Content == updated,
            "Canvas edits did not update the material's only authoritative content");
        Assert(WorkspaceMaterials.RenderMarkdown(owner.WorkspaceMaterials!).Contains(updated),
            "Collection reading/export projection did not reflect canvas edits");

        var snapshot = controller.SnapshotCurrentWorkbench();
        var entry = snapshot.Papers.Single(p => p.Id == textId);
        Assert(entry.SearchText.Contains("material-search-updated"),
            "Materials content is absent from full workbench search");
        Assert(controller.State.Papers.Where(p => p.WorkspaceMaterials == null)
            .All(p => desktop[p.Id] == Geometry(p)), "Material imports changed the desktop paper layout");

        await Until(() => controller.WorkbenchLayout.Cards.ContainsKey(textId) &&
                          controller.WorkbenchLayout.Cards.ContainsKey(nodeId),
            "Material coordinates were not stored");
        var savedLayout = controller.WorkbenchLayout.Copy();
        savedLayout.Cards[textId].X = 4200;
        savedLayout.Cards[textId].Y = 1500;
        savedLayout.Cards[textId].Pinned = true;
        controller.StoreWorkbenchLayout(savedLayout);
        controller.SaveNow(sync: true);
        var persisted = StateStore.ReadWorkbenchOverview(store.FilePath)!;
        var persistedOwner = WorkspaceMaterials.FindOwner(persisted)!;
        Assert(persistedOwner.Id == ownerId && persisted.Workbench == null,
            "Material persistence kept a parallel legacy layout or changed the owner");
        Assert(persistedOwner.WorkspaceMaterials!.Items[textId].Content == updated &&
               persistedOwner.WorkspaceMaterials.Layout.Cards[textId].X == 4200 &&
               persistedOwner.WorkspaceMaterials.Layout.Cards[textId].Y == 1500,
            "Material content and position did not round-trip together");

        controller.ToggleWorkbench();
        Assert(controller.State.Papers.Where(p => p.WorkspaceMaterials == null)
            .All(p => desktop[p.Id] == Geometry(p)), "Exiting materials canvas changed desktop geometry");
        controller.ToggleWorkbench();
        desk = (WorkbenchWindow)Member(controller, "_workbenchWindow");
        await Until(() => desk.HostedPaperCount == 3 && ActiveCards(desk).Contains(textId) &&
                          controller.WorkbenchLayout.Cards[textId].Pinned,
            "Reopening the canvas lost a saved material");
        Assert(controller.WorkbenchLayout.Cards[textId].X == 4200 &&
               controller.WorkbenchLayout.Cards[textId].Y == 1500,
            "Reopening the canvas moved a pinned material");
        Assert(controller.ReadWorkbenchMaterialImage(imageId) != null,
            "Saved material image became unavailable after reopening");

        controller.DeleteWorkbenchMaterial(fileId);
        controller.DeleteWorkbenchMaterial(directoryId);
        Assert(File.Exists(externalFile) && Directory.Exists(externalDirectory),
            "Deleting a material entry deleted the user's external source");
        Assert(controller.FindWorkbenchMaterial(fileId) == null &&
               controller.FindWorkbenchMaterial(directoryId) == null,
            "Deleting a material left its record in the collection");

        // The collection can also be opened as its special note. Entering the
        // canvas temporarily hides that presentation and restores it on exit.
        controller.OpenWorkspaceMaterialsNote();
        var ownerWindow = controller.WorkbenchPaperWindow(ownerId);
        await Until(() => !controller.HasWorkbench && ownerWindow.IsVisible,
            "Opening the materials note did not leave the canvas and show its collection");
        var ownerEditors = (Dictionary<string, TextBox>)Member(ownerWindow, "_workspaceMaterialEditors");
        Assert(ownerEditors[textId + "/content"].Text == updated,
            "The collection note did not show the content edited on the canvas");
        const string fromNote = "Edited from the collection note";
        ownerEditors[textId + "/content"].Text = fromNote;
        Assert(controller.FindWorkbenchMaterial(textId)?.Content == fromNote &&
               WorkspaceMaterials.RenderMarkdown(owner.WorkspaceMaterials!).Contains(fromNote),
            "Editing the collection note did not update the material and export projection");
        var ownerGeometry = Geometry(owner);
        controller.ToggleWorkbench();
        desk = (WorkbenchWindow)Member(controller, "_workbenchWindow");
        await Until(() => desk.HostedPaperCount == 3, "Reentering the canvas lost the ordinary paper editors");
        Assert(!ownerWindow.IsVisible && !ownerWindow.IsWorkbenchHosted &&
               !ActiveCards(desk).Contains(ownerId),
            "An open materials note remained visible or recursively entered its own canvas");
        Assert(((TextBox)Member(ActiveCards(desk)[textId]!, "MaterialEditor")).Text == fromNote,
            "The canvas did not show material content edited from the collection note");
        const string whileNoteHidden = "Updated while the collection presentation is hidden";
        controller.UpdateWorkbenchMaterial(textId, controller.FindWorkbenchMaterial(textId)!.Title, whileNoteHidden);
        controller.ToggleWorkbench();
        await Until(() => ownerWindow.IsVisible, "Leaving the canvas did not restore the previously open materials note");
        await Until(() => ownerEditors[textId + "/content"].Text == whileNoteHidden,
            "Restoring the collection note displayed stale material content");
        Assert(Geometry(owner) == ownerGeometry,
            "Temporarily hiding the materials note changed its saved desktop geometry or visibility");
        controller.SaveNow(sync: true);
    }

    private static IDictionary ActiveCards(WorkbenchWindow desk) =>
        (IDictionary)Member(Member(desk, "ActiveScene"), "Cards");

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in VisualDescendants<T>(VisualTreeHelper.GetChild(root, index)))
                yield return child;
    }

    private static void WriteMaterialFixtureImage(string path)
    {
        var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 }, 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
