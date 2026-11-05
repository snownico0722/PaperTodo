using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private const string WorkspaceMaterialsDescriptionKey = "$description";
    private readonly Dictionary<string, TextBox> _workspaceMaterialEditors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkspaceMaterialRow> _workspaceMaterialRows = new(StringComparer.Ordinal);
    private StackPanel? _workspaceMaterialsRowsPanel;
    private FrameworkElement? _workspaceMaterialsRoot;
    private TextBlock? _workspaceMaterialsEmpty;
    private bool _workspaceMaterialsApplying;
    private bool _workspaceMaterialsRefreshQueued;

    private sealed record WorkspaceMaterialRow(string Kind, Border View, Image? Image)
    {
        internal string ImageContent { get; set; } = "";
    }

    // The collection and canvas edit the same items. Generated Markdown is reserved
    // for reading/export; it is never a second editable copy of the material content.
    private FrameworkElement BuildWorkspaceMaterialsBody()
    {
        _workspaceMaterialEditors.Clear();
        _workspaceMaterialRows.Clear();
        var stack = new StackPanel { Margin = NoteTypography.ContentPadding };
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var add = WorkspaceMaterialsButton("WorkbenchAddText");
        add.Click += (_, _) =>
        {
            if (!CanEditWorkspaceMaterials()) return;
            var id = _controller.CreateWorkbenchMaterial(WorkspaceMaterialKinds.Text, "", "", "");
            RefreshWorkspaceMaterialsBodyFromModel();
            if (_workspaceMaterialEditors.TryGetValue(id + "/content", out var editor))
            {
                editor.Focus();
                editor.BringIntoView();
            }
        };
        actions.Children.Add(add);
        var canvas = WorkspaceMaterialsButton("WorkbenchTitle");
        canvas.Click += (_, _) => _controller.ToggleWorkbench();
        actions.Children.Add(canvas);
        stack.Children.Add(actions);

        var descriptionLabel = new TextBlock
        {
            Text = Strings.Get("WorkbenchMaterialDescription"),
            Margin = new Thickness(0, 0, 0, 4)
        };
        descriptionLabel.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        stack.Children.Add(descriptionLabel);
        var description = WorkspaceMaterialsEditor(WorkspaceMaterialsDescriptionKey,
            _paper.Content ?? "", "WorkbenchMaterialDescription", multiline: true);
        description.MinHeight = 48;
        description.TextChanged += (_, _) =>
        {
            if (_workspaceMaterialsApplying || !CanEditWorkspaceMaterials()) return;
            _paper.Content = description.Text;
            _controller.MarkDirty();
            QueueBuiltInFindBodyRefresh();
        };
        stack.Children.Add(description);
        _workspaceMaterialsEmpty = new TextBlock
        {
            Text = Strings.Get("WorkbenchMaterialsEmpty"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 8)
        };
        _workspaceMaterialsEmpty.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        stack.Children.Add(_workspaceMaterialsEmpty);
        _workspaceMaterialsRowsPanel = new StackPanel();
        stack.Children.Add(_workspaceMaterialsRowsPanel);

        var root = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Background = Brushes.Transparent
        };
        _workspaceMaterialsRoot = root;
        Action refresh = () =>
        {
            if (ReferenceEquals(_workspaceMaterialsRoot, root)) QueueWorkspaceMaterialsRefresh();
        };
        var subscribed = false;
        root.Loaded += (_, _) =>
        {
            if (!subscribed)
            {
                _controller.WorkbenchModelChanged += refresh;
                subscribed = true;
            }
            RefreshWorkspaceMaterialsBodyFromModel();
        };
        root.Unloaded += (_, _) =>
        {
            if (!subscribed) return;
            _controller.WorkbenchModelChanged -= refresh;
            subscribed = false;
        };
        root.IsVisibleChanged += (_, _) =>
        {
            if (root.IsVisible) RefreshWorkspaceMaterialsBodyFromModel();
        };
        RefreshWorkspaceMaterialsBodyFromModel();
        return root;
    }

    private bool CanEditWorkspaceMaterials() =>
        !IsClosed && _controller.IsRunning && !_controller.IsDataReloading &&
        _paper.WorkspaceMaterials != null && _controller.State.Papers.Contains(_paper);

    private static Button WorkspaceMaterialsButton(string textKey)
    {
        var button = new Button
        {
            Content = Strings.Get(textKey),
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, 0, 6, 0),
            MinHeight = 26,
            Background = Brushes.Transparent
        };
        button.SetResourceReference(Control.ForegroundProperty, "TextBrushKey");
        button.SetResourceReference(Control.BorderBrushProperty, "PaperBorderBrushKey");
        return button;
    }

    private TextBox WorkspaceMaterialsEditor(string key, string text, string label, bool multiline)
    {
        var editor = new TextBox
        {
            Text = text,
            MaxLength = NoteTextMaxLength,
            AcceptsReturn = multiline,
            AcceptsTab = false,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(3, 4, 3, 4),
            Margin = new Thickness(0, 0, 0, 5),
            FontFamily = NoteTypography.FontFamily,
            FontSize = NoteTypography.FontSize * CurrentTextZoom(),
            Language = NoteTypography.Language,
            ToolTip = Strings.Get(label),
            IsInactiveSelectionHighlightEnabled = true
        };
        AutomationProperties.SetName(editor, Strings.Get(label));
        editor.SetResourceReference(Control.ForegroundProperty, "TextBrushKey");
        editor.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextBrushKey");
        editor.SetResourceReference(Control.BorderBrushProperty, "PaperBorderBrushKey");
        if (multiline)
            _controller.AttachWorkspaceMaterialImagePaste(editor, ex =>
                PaperNoticeDialog.Show(InteractionWindow, Strings.Get("WorkbenchTitle"), ex.Message));
        _workspaceMaterialEditors[key] = editor;
        return editor;
    }

    private WorkspaceMaterialRow BuildWorkspaceMaterialRow(WorkspaceMaterialItem item)
    {
        var id = item.Id;
        var stack = new StackPanel();
        var title = WorkspaceMaterialsEditor(id + "/title", item.Title, "WorkbenchMaterialTitle", multiline: false);
        title.FontWeight = FontWeights.SemiBold;
        title.TextChanged += (_, _) =>
        {
            if (_workspaceMaterialsApplying || !CanEditWorkspaceMaterials()) return;
            if (_controller.FindWorkbenchMaterial(id) is { } live)
                _controller.UpdateWorkbenchMaterial(id, title.Text, live.Content);
            QueueBuiltInFindBodyRefresh();
        };
        stack.Children.Add(title);
        Image? image = null;
        if (item.Kind == WorkspaceMaterialKinds.Image)
        {
            image = new Image { Stretch = Stretch.Uniform, MaxHeight = 360, Margin = new Thickness(0, 4, 0, 8) };
            stack.Children.Add(image);
        }
        else
        {
            var content = WorkspaceMaterialsEditor(id + "/content", item.Content, "WorkbenchMaterialContent", multiline: true);
            content.MinHeight = 56;
            content.TextChanged += (_, _) =>
            {
                if (_workspaceMaterialsApplying || !CanEditWorkspaceMaterials()) return;
                if (_controller.FindWorkbenchMaterial(id) is { } live)
                    _controller.UpdateWorkbenchMaterial(id, live.Title, content.Text);
                QueueBuiltInFindBodyRefresh();
            };
            stack.Children.Add(content);
        }
        if (item.Kind is WorkspaceMaterialKinds.File or WorkspaceMaterialKinds.Link)
        {
            var source = WorkspaceMaterialsEditor(id + "/source", item.Source, "WorkbenchMaterialSource", multiline: false);
            source.TextChanged += (_, _) =>
            {
                if (_workspaceMaterialsApplying || !CanEditWorkspaceMaterials()) return;
                _controller.UpdateWorkbenchMaterialSource(id, source.Text);
                QueueBuiltInFindBodyRefresh();
            };
            stack.Children.Add(source);
        }
        var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        if (item.Kind is WorkspaceMaterialKinds.File or WorkspaceMaterialKinds.Link)
        {
            var open = WorkspaceMaterialsButton("WorkbenchOpenMaterial");
            open.Click += (_, _) =>
            {
                try { _controller.OpenWorkbenchMaterial(id); }
                catch (Exception ex)
                {
                    PaperNoticeDialog.Show(InteractionWindow, Strings.Get("WorkbenchOpenMaterial"), ex.Message);
                }
            };
            actions.Children.Add(open);
        }
        var delete = WorkspaceMaterialsButton("WorkbenchDeleteMaterial");
        delete.Click += (_, _) =>
        {
            if (!CanEditWorkspaceMaterials()) return;
            if (MessageBox.Show(InteractionWindow, Strings.Get("WorkbenchDeleteMaterialConfirm"),
                Strings.Get("WorkbenchDeleteMaterial"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _controller.DeleteWorkbenchMaterial(id);
        };
        actions.Children.Add(delete);
        stack.Children.Add(actions);
        var border = new Border
        {
            Child = stack,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(9),
            Margin = new Thickness(0, 10, 0, 0)
        };
        border.SetResourceReference(Border.BorderBrushProperty, "PaperBorderBrushKey");
        return new WorkspaceMaterialRow(item.Kind, border, image);
    }

    private void QueueWorkspaceMaterialsRefresh()
    {
        if (_workspaceMaterialsRefreshQueued || _workspaceMaterialsRoot?.IsVisible != true) return;
        _workspaceMaterialsRefreshQueued = true;
        _ = Dispatcher.BeginInvoke((Action)(() =>
        {
            _workspaceMaterialsRefreshQueued = false;
            if (!IsClosed && _workspaceMaterialsRoot?.IsVisible == true)
                RefreshWorkspaceMaterialsBodyFromModel(force: false);
        }), DispatcherPriority.Background);
    }

    private void RefreshWorkspaceMaterialsBodyFromModel(bool force = true)
    {
        if (_workspaceMaterialsRowsPanel == null || _paper.WorkspaceMaterials is not { } document) return;
        _workspaceMaterialsApplying = true;
        try
        {
            SetWorkspaceMaterialsEditorText(WorkspaceMaterialsDescriptionKey, _paper.Content ?? "", force);
            foreach (var id in _workspaceMaterialRows.Keys.Where(id => !document.Items.ContainsKey(id) ||
                document.Items[id].Kind != _workspaceMaterialRows[id].Kind).ToArray())
            {
                _workspaceMaterialsRowsPanel.Children.Remove(_workspaceMaterialRows[id].View);
                _workspaceMaterialRows.Remove(id);
                foreach (var key in _workspaceMaterialEditors.Keys.Where(key => key.StartsWith(id + "/", StringComparison.Ordinal)).ToArray())
                    _workspaceMaterialEditors.Remove(key);
            }
            foreach (var item in document.Items.Values)
            {
                if (!_workspaceMaterialRows.TryGetValue(item.Id, out var row))
                {
                    row = BuildWorkspaceMaterialRow(item);
                    _workspaceMaterialRows.Add(item.Id, row);
                    _workspaceMaterialsRowsPanel.Children.Add(row.View);
                }
                SetWorkspaceMaterialsEditorText(item.Id + "/title", item.Title, force);
                SetWorkspaceMaterialsEditorText(item.Id + "/content", item.Content, force);
                SetWorkspaceMaterialsEditorText(item.Id + "/source", item.Source, force);
                if (row.Image != null && (row.Image.Source == null || row.ImageContent != item.Content))
                {
                    row.Image.Source = _controller.ReadWorkbenchMaterialImage(item.Id);
                    row.ImageContent = item.Content;
                }
            }
            if (_workspaceMaterialsEmpty != null)
                _workspaceMaterialsEmpty.Visibility = document.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _workspaceMaterialsApplying = false; }
        QueueBuiltInFindBodyRefresh();
    }

    private void SetWorkspaceMaterialsEditorText(string key, string text, bool force)
    {
        if (!_workspaceMaterialEditors.TryGetValue(key, out var editor) || editor.Text == text ||
            (!force && editor.IsKeyboardFocusWithin)) return;
        var selectionStart = Math.Min(editor.SelectionStart, text.Length);
        var selectionLength = Math.Min(editor.SelectionLength, text.Length - selectionStart);
        editor.Text = text;
        editor.Select(selectionStart, selectionLength);
    }

    internal void RefreshWorkspaceMaterialsTypography()
    {
        if (_paper.WorkspaceMaterials == null) return;
        foreach (var editor in _workspaceMaterialEditors.Values)
        {
            editor.FontFamily = NoteTypography.FontFamily;
            editor.FontSize = NoteTypography.FontSize * CurrentTextZoom();
            editor.Language = NoteTypography.Language;
        }
    }

    internal void SetWorkspaceMaterialsVisibility(bool visible)
    {
        if (_paper.WorkspaceMaterials == null) return;
        if (visible)
        {
            RefreshWorkspaceMaterialsBodyFromModel();
            return;
        }
        foreach (var row in _workspaceMaterialRows.Values)
            if (row.Image != null) row.Image.Source = null;
    }

    internal static IEnumerable<(string Key, string Text)> GetWorkspaceMaterialsFindFields(PaperData paper)
    {
        yield return (WorkspaceMaterialsDescriptionKey, paper.Content ?? "");
        if (paper.WorkspaceMaterials == null) yield break;
        foreach (var item in paper.WorkspaceMaterials.Items.Values)
        {
            yield return (item.Id + "/title", item.Title);
            if (item.Kind != WorkspaceMaterialKinds.Image)
                yield return (item.Id + "/content", item.Content);
            if (item.Kind is WorkspaceMaterialKinds.File or WorkspaceMaterialKinds.Link)
                yield return (item.Id + "/source", item.Source);
        }
    }
}
