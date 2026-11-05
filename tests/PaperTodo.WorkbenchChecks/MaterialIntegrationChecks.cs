using System.IO;
using System.Windows;
using System.Windows.Controls;
using PaperTodo;
using PaperTodo.Plugin;

internal static partial class Program
{
    private static async Task CheckMaterialImageClipboardAndExports(AppController controller, PaperWindow ownerWindow)
    {
        var owner = WorkspaceMaterials.FindOwner(controller.State)!;
        var imagePath = Path.Combine(AppContext.BaseDirectory, "clipboard-material.png");
        WriteMaterialFixtureImage(imagePath);
        var original = controller.ImageStore.ImportImageFile("note-a", imagePath);
        var sourceEditor = (MarkdownTextBox)Member(controller.WorkbenchPaperWindow("note-a"), "_noteBox");
        var sourceText = "Copied from an ordinary note\n\n" + MarkdownImageReferences.CreateReference(original.Id) + "\n";
        sourceEditor.Text = sourceText;
        sourceEditor.SelectAll();
        sourceEditor.Copy();

        var listId = controller.CreateWorkbenchMaterial(WorkspaceMaterialKinds.Text, "Clipboard list", "", "");
        var editors = (Dictionary<string, TextBox>)Member(ownerWindow, "_workspaceMaterialEditors");
        await Until(() => editors.ContainsKey(listId + "/content"), "The new material has no collection editor");
        var listEditor = editors[listId + "/content"];
        ownerWindow.Activate();
        listEditor.BringIntoView();
        listEditor.Focus();
        await Until(() => listEditor.IsKeyboardFocusWithin, "The materials collection editor did not accept focus");
        listEditor.Paste();
        var pasted = listEditor.Text;
        AssertMaterialOwnedImage(controller, owner.Id, original.Id, pasted);
        Assert(controller.FindWorkbenchMaterial(listId)!.Content == pasted,
            "The collection editor retained a foreign image ID after paste");
        listEditor.Undo();
        Assert(listEditor.Text == "" && controller.FindWorkbenchMaterial(listId)!.Content == "",
            "One native undo did not remove the complete image-aware material paste");
        listEditor.Redo();
        Assert(listEditor.Text == pasted && controller.FindWorkbenchMaterial(listId)!.Content == pasted,
            "Native redo cloned the image again or restored the foreign source ID");
        listEditor.CaretIndex = listEditor.Text.Length;
        listEditor.SelectedText = "Further editing";
        Assert(MarkdownImageReferences.CollectImageIds(listEditor.Text).SetEquals(
            MarkdownImageReferences.CollectImageIds(pasted)), "Ordinary material typing cloned the pasted image again");

        var drop = new DataObject();
        drop.SetData(DataFormats.UnicodeText, sourceText);
        var imported = controller.ImportWorkbenchData(drop, new WorkbenchPoint(4800, 1200)).Single();
        AssertMaterialOwnedImage(controller, owner.Id, original.Id, controller.FindWorkbenchMaterial(imported)!.Content);

        var canvasId = controller.CreateWorkbenchMaterial(WorkspaceMaterialKinds.Text, "Clipboard canvas", "", "");
        controller.ToggleWorkbench();
        var desk = (WorkbenchWindow)Member(controller, "_workbenchWindow");
        await Until(() => CardReady(desk, canvasId), "The clipboard canvas material was not mounted");
        var card = ActiveCards(desk)[canvasId]!;
        var canvasEditor = (TextBox)Member(card, "MaterialEditor");
        InvokeWorkbench(desk, "FocusPaper", card);
        canvasEditor.Focus();
        await Until(() => canvasEditor.IsKeyboardFocusWithin, "The canvas material editor did not accept focus");
        canvasEditor.Paste();
        var canvasPasted = canvasEditor.Text;
        AssertMaterialOwnedImage(controller, owner.Id, original.Id, canvasPasted);
        Assert(controller.FindWorkbenchMaterial(canvasId)!.Content == canvasPasted,
            "The canvas editor and material disagree about the cloned image ID");
        canvasEditor.Undo();
        Assert(canvasEditor.Text == "" && controller.FindWorkbenchMaterial(canvasId)!.Content == "",
            "Canvas paste stopped participating in native text undo");
        canvasEditor.Redo();
        Assert(canvasEditor.Text == canvasPasted && controller.FindWorkbenchMaterial(canvasId)!.Content == canvasPasted,
            "Canvas redo did not retain the already cloned image ID");
        controller.ToggleWorkbench();
        await Until(() => ownerWindow.IsVisible, "The materials note did not return after clipboard checks");

        var projection = WorkspaceMaterials.GetReadableContent(owner);
        var exportRoot = Path.Combine(AppContext.BaseDirectory, "material-exports");
        Directory.CreateDirectory(exportRoot);
        var document = new NoteExportDocument(owner.Id, owner.Title, projection);
        foreach (var format in new[] { NoteExportFormat.Markdown, NoteExportFormat.Html })
        {
            var destination = Path.Combine(exportRoot, format == NoteExportFormat.Markdown ? "materials.md" : "materials.html");
            var result = NotePortableExport.Export(destination, [document], format, false, controller.ImageStore, "missing-image");
            var exported = File.ReadAllText(destination);
            Assert(result.Exported == 1 && result.MissingImages == 0 && result.FailedNotes.Count == 0 &&
                !exported.Contains("missing-image") && !exported.Contains("(i:"),
                "The materials projection lost an image during " + format + " export");
            if (format == NoteExportFormat.Html)
                Assert(exported.Contains("data:image/"), "HTML export omitted the material image bytes");
        }
        var external = controller.ImageStore.ConvertMarkdownForExternalEditor(owner.Id, projection,
            Path.Combine(exportRoot, "external-images"), exportRoot);
        Assert(!external.Contains("(i:") && Directory.EnumerateFiles(Path.Combine(exportRoot, "external-images")).Any(),
            "External Markdown conversion rejected or omitted the cloned material images");
        Assert(controller.ImageStore.TryGetAsset(original.Id, out var unchanged) && unchanged.NoteId == "note-a",
            "Material import reassigned the source note's image ownership");
    }

    private static void AssertMaterialOwnedImage(AppController controller, string ownerId, string sourceId, string content)
    {
        var imageId = MarkdownImageReferences.CollectImageIds(content).Single();
        Assert(imageId != sourceId && controller.ImageStore.TryGetAsset(imageId, out var asset) && asset.NoteId == ownerId,
            "Material text retained a foreign note's image reference");
    }

    private static async Task CheckMaterialNoteEvents(AppController controller, PaperWindow ownerWindow, string materialId)
    {
        var owner = WorkspaceMaterials.FindOwner(controller.State)!;
        using var api = new PaperPluginRuntimeWorkspaceApi(controller, "material-observer",
            [PaperTodoPermissionNames.NotesRead], () => true);
        var events = new List<NoteChangedEvent>();
        var observedContent = new List<string>();
        using var subscription = api.Subscribe(new()
        {
            Kinds = new HashSet<PaperTodoEventKind> { PaperTodoEventKind.NoteChanged },
            PaperIds = new HashSet<string> { owner.Id }
        }, value =>
        {
            events.Add((NoteChangedEvent)value);
            observedContent.Add(api.GetNote(owner.Id)!.Content);
        });
        var before = api.GetNote(owner.Id)!.Content;
        var editors = (Dictionary<string, TextBox>)Member(ownerWindow, "_workspaceMaterialEditors");
        editors[materialId + "/content"].Text = "Material event from the collection editor";
        await Until(() => events.Count == 1, "Editing material content did not emit note.changed through the plugin subscription");
        Assert(events[0].PreviousLength == before.Length && events[0].CurrentLength == observedContent[0].Length &&
            observedContent[0].Contains("Material event from the collection editor"),
            "Material note.changed lengths did not describe the readable note projection");

        var added = controller.CreateWorkbenchMaterial(WorkspaceMaterialKinds.Text, "Event addition", "Added body", "");
        await Until(() => events.Count == 2, "Adding material content did not emit note.changed");
        controller.DeleteWorkbenchMaterial(added);
        await Until(() => events.Count == 3, "Deleting material content did not emit note.changed");
        var layout = controller.WorkbenchLayout.Copy();
        layout.Cards[materialId].X += 20;
        controller.StoreWorkbenchLayout(layout);
        controller.PaperBodyPluginEvents.ScanNow(PaperOperationContext.User());
        Assert(events.Count == 3, "A layout-only change emitted a false material note.changed");
    }
}
