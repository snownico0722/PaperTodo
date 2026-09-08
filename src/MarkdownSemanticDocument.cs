namespace PaperTodo;

/// <summary>
/// 一次文本编辑引起的语义变化。仅当本次走了局部增量解析（≥阈值且未被引用/围栏等拒收）才产出；
/// desc==null 表示整篇解析 / 不可局部 rebase，折叠表应回退全量重建。
/// </summary>
internal readonly record struct MarkdownSourceChange(
    MarkdownSemanticSnapshot OldSnapshot,
    MarkdownSemanticSnapshot NewSnapshot,
    MarkdownSemanticIncrementalWindow Window);

/// <summary>
/// Per-editor semantic cache owned by the same thread as AvalonEdit's TextDocument. Opening a note
/// always publishes one exact full-document Markdig snapshot. Completed edits below 2K characters
/// are also parsed in full; larger notes use the lightweight local reparse path and synchronously
/// fall back to a full parse only for global reference, fence or math-delimiter changes it declines.
/// There is no worker, semaphore, pending queue, stale generation or concurrent publication path.
/// </summary>
internal sealed class MarkdownSemanticDocument : IDisposable
{
    internal const int FullParseThresholdChars = 2000;

    private readonly ICSharpCode.AvalonEdit.Document.TextDocument _document;
    private MarkdownSemanticSnapshot _snapshot;
    private string _snapshotSource;
    private bool _disposed;

    public MarkdownSemanticDocument(ICSharpCode.AvalonEdit.Document.TextDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _snapshotSource = _document.Text;
        _snapshot = MarkdownSemanticSnapshot.Parse(_snapshotSource);
        _document.TextChanged += OnDocumentTextChanged;
    }

    /// <summary>
    /// Raised synchronously after semantics for the completed TextDocument change are published.
    /// Consumers may still defer visual invalidation to their normal WPF render priority.
    /// 参数为 null 表示该次未能局部解析（见 <see cref="MarkdownSourceChange"/>）。
    /// </summary>
    public event Action<MarkdownSourceChange?>? SnapshotChanged;

    public bool TryGetCurrent(out MarkdownSemanticSnapshot snapshot)
    {
        if (!_disposed)
        {
            snapshot = _snapshot;
            return true;
        }

        snapshot = null!;
        return false;
    }

    private void OnDocumentTextChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var previous = _snapshot;
        var source = _document.Text;
        MarkdownSemanticSnapshot next;
        MarkdownSourceChange? change = null;
        if (source.Length < FullParseThresholdChars ||
            MarkdownMathIncremental.ChangeMayAffectDelimiterState(_snapshotSource, source))
        {
            next = MarkdownSemanticSnapshot.Parse(source);
        }
        else if (MarkdownSemanticSnapshot.TryParseIncrementalLocal(
                     _snapshotSource,
                     previous,
                     source,
                     out var incremental,
                     out var window))
        {
            next = incremental;
            change = new MarkdownSourceChange(previous, incremental, window);
        }
        else
        {
            next = MarkdownSemanticSnapshot.Parse(source);
        }

        _snapshotSource = source;
        _snapshot = next;
        SnapshotChanged?.Invoke(change);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _document.TextChanged -= OnDocumentTextChanged;
        _snapshotSource = string.Empty;
        _snapshot = MarkdownSemanticSnapshot.Empty;
        SnapshotChanged = null;
    }
}
