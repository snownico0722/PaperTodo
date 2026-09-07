using System.Windows;
using System.Windows.Controls;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.ExtensionApi;

public sealed class ExtensionApiPlugin : IPaperBodyPlugin, IPaperPluginRuntimeProvider
{
    public IPaperBodySession Create(PaperBodyContext context) => new Body(context);
    public IPaperPluginRuntime CreatePluginRuntime(PaperPluginRuntimeContext context) => new Runtime(context);

    private sealed class Body : IPaperBodySession
    {
        internal Body(PaperBodyContext context)
        {
            context.Paper.SetCapsulePresentation(new PaperCapsulePresentation
            {
                PreferredWidth = PaperCapsulePresentation.AutomaticWidth,
                PlainText = "扩展 API", Components = [new() { Kind = PaperCapsuleComponentKind.Text, Text = "扩展 API", Fill = true }]
            });
            View = new TextBlock
            {
                Text = "保持这张示例纸片存在。Markdown 纸片会出现“查看笔记”和“笔记浮层”操作。\n" +
                    "浮层里的下拉框复用宿主样式；输入 i: 后面的图片 ID 可以测试 NoteAssets。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16)
            };
        }
        public FrameworkElement View { get; }
        public void Dispose() { }
    }

    private sealed class Runtime : IPaperPluginRuntime
    {
        private readonly PaperPluginRuntimeContext _context;
        private readonly IDisposable _subscription;
        internal Runtime(PaperPluginRuntimeContext context)
        {
            _context = context;
            context.PaperActions.SetActionHandler(Invoke);
            foreach (var paper in context.Workspace.ListPapers()) Configure(paper);
            _subscription = context.Workspace.Subscribe(new PaperTodoEventFilter
            {
                Kinds = new HashSet<PaperTodoEventKind> { PaperTodoEventKind.PaperCreated, PaperTodoEventKind.PaperChanged }
            }, value =>
            {
                if (value is PaperCreatedEvent created) Configure(created.Paper);
                else if (value is PaperChangedEvent changed && changed.ChangedFields.HasFlag(PaperChangedFields.BodyProvider)) Configure(changed.After);
            });
        }
        private void Configure(PaperSnapshot paper)
        {
            if (paper.Type != "note" || paper.BodyProviderId != "builtin.markdown")
            { _context.PaperActions.Clear(paper.Id); return; }
            _context.PaperActions.SetActions(paper.Id,
            [
                new() { Id = "window", Text = "查看笔记", Icon = PaperTopBarIcon.Character("i"),
                    Placement = PaperActionPlacement.ContextMenu, BodyProviderId = "builtin.markdown" },
                new() { Id = "popup", Text = "笔记浮层", Icon = PaperTopBarIcon.Character("⋯"),
                    Placement = PaperActionPlacement.TopBar | PaperActionPlacement.ContextMenu, BodyProviderId = "builtin.markdown" }
            ]);
        }
        private void Invoke(PaperActionInvocation action)
        {
            var note = _context.Workspace.GetNote(action.Paper.Id);
            if (note?.ContentAvailable != true) return;
            IPaperPluginSurfaceContent Create(PaperPluginSurfaceContext ui) => new Inspector(_context, ui, note);
            if (action.ActionId == "popup" && action.Anchor is { } anchor)
                _context.Surfaces.OpenPopup(anchor, new() { Id = "inspector-popup", Width = 360, Height = 300 }, Create);
            else
                _context.Surfaces.OpenWindow(new() { Id = "note-" + note.PaperId, Title = note.PaperTitle,
                    Width = 520, Height = 340, Anchor = action.Anchor }, Create);
        }
        // The host has already revoked the lease when it calls Dispose; don't call Clear here.
        public void Dispose() => _subscription.Dispose();
    }

    private sealed class Inspector : IPaperPluginSurfaceContent
    {
        private readonly PaperPluginSurfaceContext _ui;
        private readonly ComboBox _units = new() { ItemsSource = new[] { "字节", "KiB" }, SelectedIndex = 0, Margin = new Thickness(0, 8, 0, 8) };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        private int _length;
        private string _mime = "";
        private bool _disposed;
        internal Inspector(PaperPluginRuntimeContext runtime, PaperPluginSurfaceContext ui, NoteSnapshot note)
        {
            _ui = ui;
            var imageId = new TextBox { ToolTip = "输入 i: 后面的图片 ID", Margin = new Thickness(0, 4, 0, 8) };
            var read = new Button { Content = "读取图片信息", Padding = new Thickness(10, 4, 10, 4) };
            var close = new Button { Content = "关闭", Margin = new Thickness(0, 12, 0, 0) };
            var root = new StackPanel();
            root.Children.Add(new TextBlock { Text = note.PaperTitle, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
            root.Children.Add(new TextBlock { Text = $"正文：{note.Content.Length} 字符。图片只读取编码数据，不解码。", TextWrapping = TextWrapping.Wrap });
            root.Children.Add(_units); root.Children.Add(imageId); root.Children.Add(read); root.Children.Add(_status); root.Children.Add(close);
            _units.SelectionChanged += (_, _) => ShowSize();
            read.Click += (_, _) =>
            {
                if (_disposed) return;
                try
                {
                    var id = imageId.Text.Trim();
                    if (id.StartsWith("i:", StringComparison.Ordinal)) id = id[2..];
                    var image = runtime.NoteAssets.ReadImage(note.PaperId, id);
                    _length = image.Bytes.Length; _mime = image.Mime;
                    ShowSize();
                }
                catch (PaperTodoPluginException ex) { _status.Text = $"{ex.Code}: {ex.Message}"; }
            };
            close.Click += (_, _) => ui.Close();
            View = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        private void ShowSize()
        {
            if (_length == 0 || _disposed) return;
            _status.Text = _units.SelectedIndex == 1 ? $"{_mime} · {_length / 1024.0:F1} KiB" : $"{_mime} · {_length} 字节";
        }
        public FrameworkElement View { get; }
        public void OnThemeChanged(PaperBodyTheme theme) => _ui.Controls.ApplySelectStyle(_units, 12 * theme.FontScale);
        public void Dispose() => _disposed = true;
    }
}
