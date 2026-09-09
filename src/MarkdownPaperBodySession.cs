using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PaperTodo.Plugin;

namespace PaperTodo;

/// <summary>
/// Built-in Markdown body session. The stable root stays attached to PaperWindow while Markdown
/// presenters can be rebuilt inside it. All mutable editor/presenter state belongs to this session;
/// PaperWindow.Note.cs accesses it through narrow forwarding properties during the staged extraction.
/// </summary>
internal sealed class MarkdownPaperBodySession : IPaperBodySession
{
    private readonly PaperWindow _owner;
    private readonly PaperData _paper;
    private readonly NoteImageStore _imageStore;
    private readonly Grid _root = new();
    private MarkdownTextBox? _noteBox;
    private MarkdownSemanticDocument? _semanticDocument;
    private MarkdownSemanticPresentation? _semanticPresentation;
    private bool _disposed;

    internal MarkdownPaperBodySession(
        PaperWindow owner,
        PaperData paper,
        NoteImageStore imageStore)
    {
        _owner = owner;
        _paper = paper;
        _imageStore = imageStore;
        _root.AddHandler(
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(OnPreviewKeyDown),
            handledEventsToo: true);
        owner.AttachMarkdownBodySession(this);
        try
        {
            var presenter = owner.CreateMarkdownBodyView();
            AddPresenter(presenter);
        }
        catch
        {
            owner.DetachMarkdownBodySession(this);
            throw;
        }
    }

    public FrameworkElement View => _root;

    internal MarkdownTextBox? NoteBox
    {
        get => _noteBox;
        set
        {
            if (ReferenceEquals(_noteBox, value))
            {
                return;
            }

            _semanticPresentation?.Dispose();
            _semanticPresentation = null;
            _noteBox?.SetSemanticDocument(null);
            _semanticDocument?.Dispose();
            _semanticDocument = null;

            _noteBox = value;
            if (value != null)
            {
                _semanticDocument = new MarkdownSemanticDocument(value.Document);
                value.SetSemanticDocument(_semanticDocument);
                _semanticPresentation = new MarkdownSemanticPresentation(
                    value,
                    _semanticDocument);
            }
        }
    }

    internal UIElement? CurrentPresenter { get; set; }
    internal ContextMenu? PreviewContextMenu { get; set; }
    internal Action? ShowPreview { get; set; }
    internal int PresenterGeneration { get; set; }
    internal int DeferredWorkGeneration { get; set; }
    internal Action? CancelPresenterInteractions { get; set; }
    internal bool ContentDirty { get; set; }
    internal bool ApplyingExternalChange { get; set; }
    internal bool LiveIsScriptCapsule { get; set; }

    internal IReadOnlyList<UIElement> PresenterElements =>
        _root.Children.Cast<UIElement>().ToArray();

    internal void SetTransientFindReveal(int? absoluteOffset, int length) =>
        _semanticPresentation?.SetTransientFindReveal(absoluteOffset, length);

    internal void AddPresenter(UIElement presenter)
    {
        if (!_root.Children.Contains(presenter))
        {
            _root.Children.Add(presenter);
        }
        CurrentPresenter = presenter;
    }

    internal void RemovePresenter(UIElement presenter)
    {
        _root.Children.Remove(presenter);
        if (ReferenceEquals(CurrentPresenter, presenter))
        {
            CurrentPresenter = _root.Children.OfType<UIElement>().LastOrDefault();
        }
    }

    internal void ResetPresenterState()
    {
        PresenterGeneration++;
        DeferredWorkGeneration++;
        NoteBox = null;
        CurrentPresenter = null;
        PreviewContextMenu = null;
        ShowPreview = null;
        CancelPresenterInteractions = null;
        ContentDirty = false;
        ApplyingExternalChange = false;
        LiveIsScriptCapsule = false;
        _root.Children.Clear();
    }

    public void Commit()
    {
        if (NoteBox == null || !ContentDirty)
        {
            return;
        }
        _paper.Content = NoteBox.PersistentText;
        ContentDirty = false;
    }

    public void RefreshFromModel() => _owner.RefreshLegacyMarkdownFromModel();

    public void CancelInteractions() => CancelPresenterDeferredWork();

    internal void CancelPresenterDeferredWork()
    {
        DeferredWorkGeneration++;
        CancelPresenterInteractions?.Invoke();
    }

    public void OnThemeChanged(PaperBodyTheme theme) =>
        NoteBox?.RefreshVisualStyle();

    public void OnTypographyChanged(PaperBodyTheme theme) =>
        NoteBox?.RefreshTypography();

    public void OnDpiChanged() =>
        NoteBox?.RefreshImageDecodeForCurrentDpi();

    public void OnVisibilityChanged(bool visible)
    {
        NoteBox?.SetImageRenderingSuspended(!visible);
        if (!visible)
        {
            _imageStore.ReleaseNoteBitmapCache(_paper.Id);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || NoteBox == null)
        {
            return;
        }

        var handled = e.Key switch
        {
            Key.Enter => NoteBox.TryHandleSemanticEnter(),
            Key.Back => NoteBox.TryHandleSemanticBackspace(),
            _ => false
        };
        if (handled)
        {
            e.Handled = true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Commit();
        CancelInteractions();
        OnVisibilityChanged(false);
        ResetPresenterState();
        _owner.DetachMarkdownBodySession(this);
    }
}
