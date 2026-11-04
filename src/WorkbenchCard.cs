using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PaperTodo.Plugin;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace PaperTodo;

/// <summary>
/// One paper on a workbench canvas. A live card edits its paper through the shared
/// <see cref="PaperCommandService"/> (the same path MCP and plugins use), so the real paper
/// window is refreshed by the existing external-change reconcile and keeps its single
/// Markdown surface. A preview card (inactive workspace) is read-only.
/// </summary>
internal sealed class WorkbenchCard : Border
{
    private const int PreviewTextLimit = 1200;

    private readonly AppController _controller;
    private readonly Action<string> _report;
    private readonly Grid _layout;
    private readonly Grid _header;
    private readonly TextBlock _title;
    private readonly Border _linkHandle;
    private readonly Border _openButton;
    private readonly ContentControl _body = new() { Focusable = false };
    private readonly StackPanel _footer = new() { Orientation = Orientation.Horizontal };
    private TextBox? _titleEditor;
    private bool _linkTarget;

    public WorkbenchCard(
        AppController controller,
        PaperData paper,
        bool live,
        WorkbenchCardKind kind,
        string title,
        Action<string> report)
    {
        _controller = controller;
        _report = report;
        Paper = paper;
        Live = live;
        Kind = kind;

        Width = WorkbenchLayout.CardWidth;
        Height = WorkbenchLayout.CardHeight;
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1.2);
        Background = Theme.PaperBrush;
        // No per-card shader effect: a canvas may hold up to 200 cards at any zoom.
        SnapsToDevicePixels = true;
        Focusable = true;
        FocusVisualStyle = null;

        _layout = new Grid();
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _header = new Grid
        {
            Background = Theme.TitleBarBrush(opaque: true),
            Cursor = live ? Cursors.SizeAll : Cursors.Arrow,
            MinHeight = 30
        };
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = new TextBlock
        {
            Text = KindGlyph(kind),
            Foreground = Theme.WeakTextBrush,
            FontSize = AppTypography.Scale(12),
            Margin = new Thickness(10, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _title = new TextBlock
        {
            Text = title,
            Foreground = Theme.TextBrush,
            FontWeight = FontWeights.SemiBold,
            FontSize = AppTypography.Scale(13),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = title
        };
        _linkHandle = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(2),
            BorderBrush = Theme.ActiveBrush,
            Background = Theme.PaperBrush,
            Margin = new Thickness(6, 0, 4, 0),
            Cursor = Cursors.Cross,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = Strings.Get("WorkbenchLinkHandleTip"),
            Visibility = live ? Visibility.Visible : Visibility.Collapsed
        };
        _openButton = new Border
        {
            Width = 24,
            Height = 22,
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = Strings.Get("WorkbenchOpenPaper"),
            Visibility = live ? Visibility.Visible : Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "↗",
                Foreground = Theme.WeakTextBrush,
                FontSize = AppTypography.Scale(13),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        _openButton.MouseEnter += (_, _) => _openButton.Background = Theme.HoverBrush;
        _openButton.MouseLeave += (_, _) => _openButton.Background = Brushes.Transparent;
        _openButton.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _openButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _controller.OpenPaperFromWorkbench(Paper);
        };

        Grid.SetColumn(glyph, 0);
        Grid.SetColumn(_title, 1);
        Grid.SetColumn(_linkHandle, 2);
        Grid.SetColumn(_openButton, 3);
        _header.Children.Add(glyph);
        _header.Children.Add(_title);
        _header.Children.Add(_linkHandle);
        _header.Children.Add(_openButton);

        _footer.Margin = new Thickness(10, 0, 10, 6);
        Grid.SetRow(_header, 0);
        Grid.SetRow(_body, 1);
        Grid.SetRow(_footer, 2);
        _layout.Children.Add(_header);
        _layout.Children.Add(_body);
        _layout.Children.Add(_footer);
        Child = new Border
        {
            CornerRadius = new CornerRadius(9),
            ClipToBounds = true,
            Child = _layout
        };

        IsKeyboardFocusWithinChanged += (_, _) => RefreshBorder();
        RefreshBorder();
        BuildBody();
        BuildFooter();
    }

    public PaperData Paper { get; }
    public bool Live { get; }
    public WorkbenchCardKind Kind { get; }
    public FrameworkElement DragArea => _header;
    public FrameworkElement LinkHandle => _linkHandle;
    public bool IsEditingTitle => _titleEditor != null;

    public void SetLinkTarget(bool target)
    {
        if (_linkTarget == target) return;
        _linkTarget = target;
        RefreshBorder();
    }

    public void SetCrossing(bool crossing) => Opacity = crossing ? 0.55 : 1;

    public void RefreshContent()
    {
        BuildBody();
        BuildFooter();
    }

    public void BeginTitleEdit()
    {
        if (!Live || _titleEditor != null) return;
        var editor = new TextBox
        {
            Text = Paper.Title is { Length: > 0 } custom ? custom : _title.Text,
            FontWeight = FontWeights.SemiBold,
            FontSize = AppTypography.Scale(13),
            BorderThickness = new Thickness(0),
            Background = Theme.PaperBrush,
            Foreground = Theme.TextBrush,
            CaretBrush = Theme.TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0)
        };
        var committed = false;
        void Finish(bool commit)
        {
            if (committed) return;
            committed = true;
            if (commit && _controller.IsRunning)
            {
                var text = editor.Text.Trim();
                if (text.Length > 0)
                {
                    if (PaperTitles.ExceedsTitleLengthLimit(text, _controller.State.MaxTitleLength))
                        _report(Strings.Get("WorkbenchTitleTooLong"));
                    else
                        _controller.UpdatePaperTitle(Paper, text);
                }
            }
            _header.Children.Remove(editor);
            _titleEditor = null;
            _title.Visibility = Visibility.Visible;
            _title.Text = _controller.WorkbenchTitle(Paper, live: true, _controller.State.Papers);
            _title.ToolTip = _title.Text;
        }
        editor.LostKeyboardFocus += (_, _) => Finish(commit: true);
        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Finish(commit: true); e.Handled = true; Focus(); }
            else if (e.Key == Key.Escape) { Finish(commit: false); e.Handled = true; Focus(); }
        };
        Grid.SetColumn(editor, 1);
        _title.Visibility = Visibility.Collapsed;
        _header.Children.Add(editor);
        _titleEditor = editor;
        editor.Focus();
        editor.SelectAll();
    }

    private void RefreshBorder()
    {
        BorderBrush = _linkTarget || IsKeyboardFocusWithin
            ? Theme.ActiveBrush
            : Theme.PaperBorderBrush;
        BorderThickness = new Thickness(_linkTarget ? 2.4 : 1.2);
    }

    private static string KindGlyph(WorkbenchCardKind kind) => kind switch
    {
        WorkbenchCardKind.Todo => "☑",
        WorkbenchCardKind.Note => "✎",
        WorkbenchCardKind.Tethered => "⛓",
        _ => "◇"
    };

    private void BuildFooter()
    {
        _footer.Children.Clear();
        var badges = new List<string>();
        if (Live)
        {
            if (_controller.WorkbenchTetherTitle(Paper) is { } tether)
            {
                badges.Add(tether.Length == 0
                    ? Strings.Get("WorkbenchBoundToProgram")
                    : Strings.Format("WorkbenchBoundToProgramNamed", tether));
            }
            if (!_controller.IsWorkbenchPaperShown(Paper)) badges.Add(Strings.Get("WorkbenchHiddenOnDesktop"));
            else if (Paper.IsCollapsed) badges.Add(Strings.Get("WorkbenchCollapsedOnDesktop"));
        }
        foreach (var badge in badges)
        {
            _footer.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = Theme.Tint((byte)(Theme.IsDark ? 40 : 28)),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(0, 0, 4, 0),
                Child = new TextBlock
                {
                    Text = badge,
                    Foreground = Theme.WeakTextBrush,
                    FontSize = AppTypography.Scale(10.5),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = WorkbenchLayout.CardWidth - 40
                }
            });
        }
        _footer.Visibility = badges.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------ bodies

    private void BuildBody()
    {
        _body.Content = Kind switch
        {
            WorkbenchCardKind.Todo => BuildTodoBody(),
            WorkbenchCardKind.Plugin => BuildPluginBody(),
            _ when Paper.Type == PaperTypes.Todo => BuildTodoBody(),
            _ when !string.Equals(Paper.BodyProviderId, PaperBodyProviderIds.Markdown, StringComparison.Ordinal)
                => BuildPluginBody(),
            _ => BuildNoteBody()
        };
    }

    private UIElement BuildPluginBody()
    {
        var text = !string.IsNullOrWhiteSpace(Paper.BodyHeaderText)
            ? Paper.BodyHeaderText
            : Paper.BodyCapsuleText;
        var panel = new StackPanel { Margin = new Thickness(12, 10, 12, 8) };
        panel.Children.Add(WeakText(_controller.WorkbenchPluginName(Paper), 11));
        if (!string.IsNullOrWhiteSpace(text))
        {
            panel.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = Theme.TextBrush,
                FontSize = AppTypography.Scale(12.5),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
        }
        if (Live) panel.Children.Add(WeakText(Strings.Get("WorkbenchPluginPaperHint"), 11, top: 8));
        return panel;
    }

    private UIElement BuildNoteBody()
    {
        var content = _controller.WorkbenchNoteText(Paper, Live);
        if (!Live)
        {
            return new TextBlock
            {
                Text = content.Length > PreviewTextLimit ? content[..PreviewTextLimit] + "…" : content,
                Foreground = Theme.TextBrush,
                FontSize = AppTypography.Scale(12.5),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(12, 8, 12, 8)
            };
        }

        var editor = new TextBox
        {
            Text = content,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Theme.TextBrush,
            CaretBrush = Theme.TextBrush,
            SelectionBrush = Theme.ActiveBrush,
            FontSize = AppTypography.Scale(12.5),
            Padding = new Thickness(8, 6, 8, 6),
            MaxLength = PaperWindow.NoteTextMaxLength
        };
        var baseline = content;
        editor.LostKeyboardFocus += (_, _) =>
        {
            if (string.Equals(editor.Text, baseline, StringComparison.Ordinal) || !_controller.IsRunning) return;
            if (Run(() => _controller.PaperCommands.WriteNote(
                    new WriteNoteRequest { PaperId = Paper.Id, Content = editor.Text, Mode = NoteWriteMode.Replace },
                    PaperOperationContext.User())))
            {
                baseline = editor.Text;
            }
            else
            {
                editor.Text = baseline = _controller.WorkbenchNoteText(Paper, live: true);
            }
        };
        return editor;
    }

    private UIElement BuildTodoBody()
    {
        var rows = new StackPanel { Margin = new Thickness(6, 6, 6, 4) };
        var items = (Paper.Items ?? new List<PaperItem>())
            .Where(item => item != null && TodoRules.HasMeaningfulContent(item))
            .OrderBy(item => item.Order)
            .ToList();
        var editors = new List<TextBox>();
        foreach (var item in items)
        {
            rows.Children.Add(Live ? LiveTodoRow(item, editors) : PreviewTodoRow(item));
        }
        if (items.Count == 0 && !Live)
        {
            rows.Children.Add(WeakText(Strings.Get("WorkbenchEmptyTodo"), 12, left: 6));
        }
        if (Live) rows.Children.Add(AddTodoRow(editors));
        return new ScrollViewer
        {
            Content = rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false
        };
    }

    private UIElement PreviewTodoRow(PaperItem item)
    {
        var row = new DockPanel { Margin = new Thickness(4, 2, 4, 2) };
        var mark = new TextBlock
        {
            Text = item.Done ? "☑" : "☐",
            Foreground = Theme.WeakTextBrush,
            FontSize = AppTypography.Scale(12.5),
            Margin = new Thickness(0, 0, 6, 0)
        };
        DockPanel.SetDock(mark, Dock.Left);
        row.Children.Add(mark);
        row.Children.Add(new TextBlock
        {
            Text = item.Text,
            Foreground = item.Done ? Theme.WeakTextBrush : Theme.TextBrush,
            TextDecorations = item.Done ? TextDecorations.Strikethrough : null,
            FontSize = AppTypography.Scale(12.5),
            TextWrapping = TextWrapping.Wrap
        });
        return row;
    }

    private UIElement LiveTodoRow(PaperItem item, List<TextBox> editors)
    {
        var row = new DockPanel { Margin = new Thickness(2, 1, 2, 1) };
        var check = new System.Windows.Controls.CheckBox
        {
            IsChecked = item.Done,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 4, 4, 0),
            Focusable = false
        };
        check.Click += (_, _) =>
        {
            var done = check.IsChecked == true;
            Run(() => _controller.PaperCommands.UpdateTodo(
                new UpdateTodoRequest { PaperId = Paper.Id, TodoId = item.Id, Done = done },
                PaperOperationContext.User()));
            // Completion ordering and auto-clear are owned by the command; show its result.
            Dispatcher.InvokeAsync(RefreshContent, System.Windows.Threading.DispatcherPriority.Background);
        };
        DockPanel.SetDock(check, Dock.Left);
        row.Children.Add(check);

        var editor = TodoEditor(item.Text, item.Done);
        var baseline = item.Text;
        editor.LostKeyboardFocus += (_, _) =>
        {
            if (string.Equals(editor.Text, baseline, StringComparison.Ordinal) || !_controller.IsRunning) return;
            if (Run(() => _controller.PaperCommands.UpdateTodo(
                    new UpdateTodoRequest { PaperId = Paper.Id, TodoId = item.Id, Text = editor.Text },
                    PaperOperationContext.User())))
            {
                baseline = editor.Text;
            }
            else
            {
                editor.Text = baseline;
            }
        };
        var index = editors.Count;
        editor.PreviewKeyDown += (_, e) => MoveBetweenTodoEditors(e, editor, editors, index);
        editors.Add(editor);
        row.Children.Add(editor);
        return row;
    }

    private UIElement AddTodoRow(List<TextBox> editors)
    {
        var row = new DockPanel { Margin = new Thickness(2, 1, 2, 1) };
        var plus = new TextBlock
        {
            Text = "+",
            Foreground = Theme.WeakTextBrush,
            FontSize = AppTypography.Scale(14),
            Margin = new Thickness(7, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(plus, Dock.Left);
        row.Children.Add(plus);
        var editor = TodoEditor("", done: false);
        var placeholder = new TextBlock
        {
            Text = Strings.Get("WorkbenchAddTodo"),
            Foreground = Theme.WeakTextBrush,
            FontSize = AppTypography.Scale(12.5),
            IsHitTestVisible = false,
            Margin = new Thickness(4, 3, 0, 0)
        };
        var host = new Grid();
        host.Children.Add(editor);
        host.Children.Add(placeholder);
        editor.TextChanged += (_, _) =>
            placeholder.Visibility = editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Up && editors.Count > 0 && editor.CaretIndex == 0)
            {
                editors[^1].Focus();
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var text = editor.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            if (Run(() => _controller.PaperCommands.AppendTodos(
                    new AppendTodosRequest
                    {
                        PaperId = Paper.Id,
                        Todos = new[] { new TodoCreateItem { Text = text } }
                    },
                    PaperOperationContext.User())))
            {
                RefreshContent();
                FocusAddTodo();
            }
        };
        row.Children.Add(host);
        return row;
    }

    private void FocusAddTodo()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_body.Content is ScrollViewer { Content: StackPanel rows } viewer &&
                rows.Children.Count > 0 &&
                rows.Children[^1] is DockPanel { Children.Count: > 1 } row &&
                row.Children[1] is Grid { Children.Count: > 0 } host &&
                host.Children[0] is TextBox editor)
            {
                editor.Focus();
                viewer.ScrollToEnd();
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void MoveBetweenTodoEditors(KeyEventArgs e, TextBox editor, List<TextBox> editors, int index)
    {
        if (e.Key == Key.Enter || (e.Key == Key.Down && editor.CaretIndex == editor.Text.Length))
        {
            e.Handled = true;
            if (index + 1 < editors.Count) editors[index + 1].Focus();
            else FocusAddTodo();
        }
        else if (e.Key == Key.Up && index > 0 && editor.CaretIndex == 0)
        {
            e.Handled = true;
            editors[index - 1].Focus();
        }
    }

    private TextBox TodoEditor(string text, bool done) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Foreground = done ? Theme.WeakTextBrush : Theme.TextBrush,
        CaretBrush = Theme.TextBrush,
        SelectionBrush = Theme.ActiveBrush,
        TextDecorations = done ? TextDecorations.Strikethrough : null,
        FontSize = AppTypography.Scale(12.5),
        Padding = new Thickness(2, 2, 2, 2),
        MaxLength = PaperWindow.TodoTextMaxLength
    };

    private static TextBlock WeakText(string text, double size, double left = 0, double top = 0) => new()
    {
        Text = text,
        Foreground = Theme.WeakTextBrush,
        FontSize = AppTypography.Scale(size),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(left, top, 0, 0)
    };

    private bool Run(Action command)
    {
        try
        {
            command();
            return true;
        }
        catch (PaperCommandException ex)
        {
            _report(Strings.Format("WorkbenchEditFailed", ex.Message));
            return false;
        }
    }
}
