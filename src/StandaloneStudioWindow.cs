using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Search;
using Microsoft.Win32;

namespace PaperTodo;

// One file, one editor and one HWND. No PaperData, hidden PaperWindow, workspace or
// AppController is created to host it. The same editor and toolbar serve normal Studio.
internal sealed class StandaloneStudioWindow : Window
{
    private readonly AppState _settings;
    private readonly StandaloneStudioRecovery _recovery;
    private readonly StandaloneStudioImages _images;
    private readonly MarkdownTextBox _editor;
    private readonly Border _frame;
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = new() { Margin = new Thickness(14, 4, 14, 7), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _markdownTools;
    private readonly DispatcherTimer _recoveryTimer;
    private readonly Queue<string> _pendingFiles = new();
    private StandaloneStudioDocument _document = StandaloneStudioDocument.New();
    private MarkdownSemanticDocument? _semantics;
    private MarkdownSemanticPresentation? _presentation;
    private bool _loading;
    private bool _busy;
    private bool _closed;
    private WindowState _lastShownState = WindowState.Normal;
    private Button? _maximizeButton;

    internal StandaloneStudioWindow(AppState settings, StandaloneStudioRecovery? recovery = null)
    {
        _settings = settings;
        _recovery = recovery ?? new StandaloneStudioRecovery();
        _images = new StandaloneStudioImages(_recovery.AssetsDirectory);
        StandaloneStudioSettings.Apply(settings);
        ToolTipPreferences.Register(() => settings.EnableToolTips);
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) _lastShownState = WindowState;
            if (_maximizeButton != null) _maximizeButton.Content = StudioIcons.Create(_maximizeButton,
                WindowState == WindowState.Maximized ? StudioIcon.Restore : StudioIcon.Maximize);
        };
        Width = 1000;
        Height = 760;
        MinWidth = 600;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        Background = Theme.PaperBrush;
        FontFamily = AppTypography.UiFontFamily;
        FontSize = AppTypography.Scale(13);
        Language = AppTypography.Language;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(10), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false
        });
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = root };
        Content = _frame;
        root.Children.Add(CreateTitleBar());
        _editor = new MarkdownTextBox
        {
            MaxLength = PaperWindow.NoteTextMaxLength, Margin = new Thickness(18, 10, 12, 8),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            ShowLineNumbers = true, AllowDrop = true, FocusVisualStyle = null
        };
        _editor.TextChanged += (_, _) =>
        {
            if (_loading || _closed) return;
            _document.Text = _editor.Text;
            UpdateTitle();
            _recoveryTimer?.Stop();
            _recoveryTimer?.Start();
        };
        _editor.ImageImportFailed += ShowFailure;
        _editor.PasteRejected += () => PaperNoticeDialog.Show(this, Strings.Get("NoteInputLimitTitle"),
            Strings.Format("NoteInputLimitMessage", _editor.MaxLength));
        _editor.PreviewMouseLeftButtonDown += (_, _) => _editor.BeginCaretRevealGesture();
        _editor.PreviewMouseLeftButtonUp += (_, _) => _editor.EndCaretRevealGesture();
        _editor.LostMouseCapture += (_, _) => _editor.EndCaretRevealGesture();
        _editor.ContextMenu = CreateEditorMenu();
        SearchPanel.Install(_editor.TextArea);
        Grid.SetRow(_editor, 2);
        root.Children.Add(_editor);
        _markdownTools = StudioMarkdownToolbar.Create(() => _editor, EnterEdit, ChooseImage);
        var toolsHost = new ScrollViewer
        {
            Content = _markdownTools, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 3, 0, 3)
        };
        Grid.SetRow(toolsHost, 1);
        root.Children.Add(toolsHost);
        Grid.SetRow(_status, 3);
        root.Children.Add(_status);
        _recoveryTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) => { _recoveryTimer?.Stop(); SaveRecovery(); }, Dispatcher);
        _recoveryTimer.Stop();
        PreviewKeyDown += OnKeyDown;
        PreviewDragOver += OnDragOver;
        PreviewDrop += OnDrop;
        AllowDrop = true;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _closed = true;
            _recoveryTimer.Stop();
            _pendingFiles.Clear();
            DisposePresentation();
        };
        InstallDocument(_document);
        RefreshAppearance();
    }

    internal MarkdownTextBox Editor => _editor;
    internal StandaloneStudioDocument Document => _document;

    internal void Initialize(string? filePath)
    {
        RunDocumentAction(() =>
        {
            if (_recovery.HasSnapshot)
            {
                var recover = PaperNoticeDialog.ShowDecision(this, Strings.Get("StudioRecoveryTitle"),
                    Strings.Get("StudioRecoveryMessage"), Strings.Get("StudioDiscard"), Strings.Get("StudioRecover"));
                if (recover == null)
                {
                    // Closing the recovery choice leaves the previous scene untouched and
                    // exits this startup; opening an empty document would overwrite it.
                    _closed = true;
                    Close();
                    return;
                }
                try
                {
                    if (recover.Value)
                    {
                        var recovered = _recovery.Load();
                        if (recovered != null) InstallDocument(recovered);
                    }
                    else _recovery.Discard();
                }
                catch (Exception ex)
                {
                    ShowFailure(ex);
                    _closed = true;
                    Close();
                    return;
                }
            }
            if (filePath != null) OpenCore(filePath);
            else SaveRecovery();
            _editor.Focus();
        });
    }

    internal void ActivateDocument(string? path)
    {
        if (_closed) return;
        if (WindowState == WindowState.Minimized) WindowState = _lastShownState;
        Show();
        Activate();
        if (!string.IsNullOrWhiteSpace(path)) _pendingFiles.Enqueue(path);
        DrainFiles();
    }

    internal void StopRecovery() => _recoveryTimer.Stop();
    internal void PreserveForSessionEnding() { _recoveryTimer.Stop(); SaveRecovery(); }

    private UIElement CreateTitleBar()
    {
        var header = new Grid { Height = 42, Background = Brushes.Transparent, Margin = new Thickness(7, 3, 7, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject origin && FindButton(origin) != null) return;
            if (e.ClickCount == 2) ToggleMaximize();
            else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        };
        var fileButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        void Add(Panel panel, StudioIcon icon, string resource, Action action)
        {
            var button = StudioIcons.Button(icon, Strings.Get(resource));
            button.Click += (_, _) => action();
            panel.Children.Add(button);
        }
        Add(fileButtons, StudioIcon.NewNote, "StudioNew", () => RunDocumentAction(() =>
        {
            if (!ConfirmReplace(false)) return;
            InstallDocument(StandaloneStudioDocument.New());
            SaveRecovery();
        }));
        Add(fileButtons, StudioIcon.Open, "StudioOpen", () => RunDocumentAction(ChooseDocument));
        Add(fileButtons, StudioIcon.Save, "StudioSave", () => RunDocumentAction(() => SaveDocument()));
        header.Children.Add(fileButtons);
        _title.Margin = new Thickness(14, 0, 10, 0);
        Grid.SetColumn(_title, 1);
        header.Children.Add(_title);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Add(controls, StudioIcon.Settings, "TraySettings", () => RunDocumentAction(() => StandaloneStudioSettings.Show(this, _settings, RefreshAppearance)));
        Add(controls, StudioIcon.Minimize, "EditorMinimize", () => WindowState = WindowState.Minimized);
        Add(controls, StudioIcon.Maximize, "StudioMaximize", ToggleMaximize);
        _maximizeButton = (Button)controls.Children[^1];
        Add(controls, StudioIcon.Close, "StudioClose", Close);
        Grid.SetColumn(controls, 2);
        header.Children.Add(controls);
        return header;
    }

    private static Button? FindButton(DependencyObject? current)
    {
        while (current != null)
        {
            if (current is Button button) return button;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : null;
        }
        return null;
    }

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void EnterEdit() { _editor.SetPreviewMode(false); _editor.Focus(); }

    private void InstallDocument(StandaloneStudioDocument document)
    {
        _recoveryTimer?.Stop();
        _loading = true;
        try
        {
            DisposePresentation();
            _document = document;
            _editor.PreferredNewLine = document.NewLine;
            _images.DocumentPath = document.FilePath;
            _editor.ClearImageSelection();
            _editor.ConfigureImageSource(StandaloneStudioImages.OwnerId,
                document.IsMarkdown ? _images : null, preserveFileDocumentText: true);
            _editor.Text = document.Text;
            _editor.Document.UndoStack.ClearAll();
            if (document.IsMarkdown)
            {
                _semantics = new MarkdownSemanticDocument(_editor.Document);
                _editor.SetSemanticDocument(_semantics);
                _presentation = new MarkdownSemanticPresentation(_editor, _semantics);
            }
            _editor.SetPreviewMode(false);
            _editor.SetMarkdownRenderMode(document.IsMarkdown ? _settings.MarkdownRenderMode : MarkdownRenderModes.Off);
            // Undo and redo remain available for source/text documents; Markdown-only
            // formatting commands are hidden, never applied to HTML or TXT.
            for (var index = 2; index < _markdownTools.Children.Count; index++)
                _markdownTools.Children[index].Visibility = document.IsMarkdown ? Visibility.Visible : Visibility.Collapsed;
            _editor.CaretOffset = 0;
            _editor.ScrollToHome();
            _status.Text = "";
            UpdateTitle();
        }
        finally { _loading = false; }
    }

    private void DisposePresentation()
    {
        _presentation?.Dispose();
        _presentation = null;
        _editor?.SetSemanticDocument(null);
        _semantics?.Dispose();
        _semantics = null;
    }

    private void OpenCore(string path)
    {
        try
        {
            if (!StandaloneStudioDocument.IsSupportedPath(path))
                throw new NotSupportedException(Strings.Get("StudioSupportedFiles"));
            // Read the entire candidate before prompting or changing the current document.
            var candidate = StandaloneStudioDocument.Load(path);
            if (!ConfirmReplace(true)) return;
            // Save-and-open can save into this very path, including a new document's
            // first save. Do not reinstall the stale bytes read before that save.
            if (string.Equals(candidate.FilePath, _document.FilePath, StringComparison.OrdinalIgnoreCase))
                candidate = StandaloneStudioDocument.Load(path);
            InstallDocument(candidate);
            SaveRecovery();
            _editor.Focus();
        }
        catch (Exception ex) { ShowFailure(ex); }
    }

    private void ChooseDocument()
    {
        var picker = new OpenFileDialog { Filter = Strings.Get("StudioFileFilter"), CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) == true) OpenCore(picker.FileName);
    }

    private bool ConfirmReplace(bool opening)
    {
        _document.Text = _editor.Text;
        if (!_document.IsDirty)
        {
            if (_document.FilePath == null && _document.Text.Length == 0) return true;
            return PaperNoticeDialog.ShowChoice(this, Strings.Get("StudioReplaceTitle"), Strings.Get("StudioReplaceMessage"),
                Strings.Get("CommonCancel"), Strings.Get(opening ? "StudioOpen" : "StudioNew"));
        }
        var choice = PaperNoticeDialog.ShowOptions(this, Strings.Get("StudioUnsavedTitle"), Strings.Get("StudioUnsavedMessage"),
            [Strings.Get(opening ? "StudioSaveAndOpen" : "StudioSaveAndNew"),
             Strings.Get(opening ? "StudioDiscardAndOpen" : "StudioDiscardAndNew"), Strings.Get("CommonCancel")]);
        return choice == 1 || choice == 0 && SaveDocument();
    }

    private bool SaveDocument()
    {
        _document.Text = _editor.Text;
        var path = _document.FilePath;
        if (path == null)
        {
            var picker = new SaveFileDialog { Filter = Strings.Get("StudioFileFilter"), DefaultExt = ".md", AddExtension = true,
                FileName = Strings.Get("StudioUntitled") + ".md", OverwritePrompt = true };
            if (picker.ShowDialog(this) != true) return false;
            path = picker.FileName;
        }
        try
        {
            using var prepared = _document.IsMarkdown ? _images.PrepareSave(_editor.Text, path) : null;
            var savedText = prepared?.Text ?? _editor.Text;
            var wasMarkdown = _document.IsMarkdown;
            _document.Save(path, savedText);
            if (prepared != null) prepared.Committed = true;
            _loading = true;
            try
            {
                if (!string.Equals(savedText, _editor.Text, StringComparison.Ordinal))
                {
                    var caret = _editor.CaretOffset;
                    // Keep the same editor and its undo stack. Undo can still reach the
                    // temporary URLs because images stay until this document is closed.
                    var undo = _editor.Document.UndoStack;
                    undo.StartContinuedUndoGroup(undo.LastGroupDescriptor);
                    try { _editor.Document.Replace(0, _editor.Document.TextLength, savedText); }
                    finally { undo.EndUndoGroup(); }
                    _editor.CaretOffset = Math.Min(caret, _editor.Document.TextLength);
                }
                _images.DocumentPath = _document.FilePath;
                if (wasMarkdown != _document.IsMarkdown) InstallDocument(_document);
                else _editor.RefreshVisualStyle();
            }
            finally { _loading = false; }
            _status.Text = Strings.Get("StudioSaved");
            UpdateTitle();
            SaveRecovery();
            return true;
        }
        catch (Exception ex) { ShowFailure(ex); return false; }
    }

    private void ChooseImage()
    {
        if (!_document.IsMarkdown) return;
        RunDocumentAction(() =>
        {
            var picker = new OpenFileDialog { Filter = Strings.Get("StudioImageFilter"), Multiselect = true };
            if (picker.ShowDialog(this) != true) return;
            try { _editor.InsertImagesFromFiles(picker.FileNames); }
            catch (Exception ex) { ShowFailure(ex); }
        });
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.S: e.Handled = true; RunDocumentAction(() => SaveDocument()); return;
                case Key.O: e.Handled = true; RunDocumentAction(ChooseDocument); return;
                case Key.N: e.Handled = true; RunDocumentAction(() => { if (ConfirmReplace(false)) { InstallDocument(StandaloneStudioDocument.New()); SaveRecovery(); } }); return;
                case Key.B when _document.IsMarkdown: _editor.WrapSelection("**", "**"); e.Handled = true; return;
                case Key.I when _document.IsMarkdown: _editor.WrapSelection("*", "*"); e.Handled = true; return;
            }
        }
        if (_document.IsMarkdown && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (e.Key == Key.Enter) e.Handled = _editor.TryHandleSemanticEnter();
            else if (e.Key == Key.Back) e.Handled = _editor.TryHandleSemanticBackspace();
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        var canOpen = files.Any(StandaloneStudioDocument.IsSupportedPath);
        e.Effects = !_busy && (canOpen || _document.IsMarkdown && _editor.CanInsertImagesFromDataObject(e.Data))
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        var document = files.FirstOrDefault(StandaloneStudioDocument.IsSupportedPath);
        if (document != null) { ActivateDocument(document); return; }
        if (!_busy && _document.IsMarkdown)
            RunDocumentAction(() => _editor.TryInsertImagesFromDataObject(e.Data));
    }

    private ContextMenu CreateEditorMenu()
    {
        var menu = new ContextMenu();
        void Add(string key, ICommand command)
        {
            menu.Items.Add(new MenuItem { Header = Strings.Get(key), Command = command, CommandTarget = _editor });
        }
        Add("EditorUndo", ApplicationCommands.Undo);
        Add("EditorRedo", ApplicationCommands.Redo);
        menu.Items.Add(new Separator());
        Add("StudioCut", ApplicationCommands.Cut);
        Add("MenuCopy", ApplicationCommands.Copy);
        Add("MenuPaste", ApplicationCommands.Paste);
        Add("MenuSelectAll", ApplicationCommands.SelectAll);
        return menu;
    }

    private void RunDocumentAction(Action action)
    {
        if (_busy || _closed) return;
        _busy = true;
        try { action(); }
        finally { _busy = false; DrainFiles(); }
    }

    private void DrainFiles()
    {
        if (_busy || _closed) return;
        _busy = true;
        try { while (_pendingFiles.TryDequeue(out var path) && !_closed) OpenCore(path); }
        finally { _busy = false; }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        if (_busy) { e.Cancel = true; return; }
        _busy = true;
        try
        {
            _document.Text = _editor.Text;
            if (_document.IsDirty)
            {
                var choice = PaperNoticeDialog.ShowOptions(this, Strings.Get("StudioUnsavedTitle"), Strings.Get("StudioUnsavedMessage"),
                    [Strings.Get("StudioSave"), Strings.Get("StudioDiscard"), Strings.Get("CommonCancel")]);
                if (choice is null or 2 || choice == 0 && !SaveDocument()) { e.Cancel = true; return; }
            }
            _recoveryTimer.Stop();
            try { _recovery.Discard(); }
            catch (Exception ex) { ShowFailure(ex); e.Cancel = true; return; }
            _closed = true;
        }
        finally
        {
            _busy = false;
            if (e.Cancel) _ = Dispatcher.BeginInvoke((Action)DrainFiles);
        }
    }

    private void SaveRecovery()
    {
        if (_closed) return;
        _document.Text = _editor.Text;
        try { _recovery.Save(_document); }
        catch (Exception ex) { _status.Text = Strings.Format("StudioRecoveryFailed", ex.Message); }
    }

    private void RefreshAppearance()
    {
        StandaloneStudioSettings.Apply(_settings);
        Resources["WeakTextBrushKey"] = Theme.WeakTextBrush;
        Resources["TextBrushKey"] = Theme.TextBrush;
        Resources["HoverBrushKey"] = Theme.HoverBrush;
        Background = _frame.Background = Theme.PaperBrush;
        _frame.BorderBrush = Theme.PaperBorderBrush;
        _title.Foreground = Theme.TextBrush;
        _status.Foreground = Theme.WeakTextBrush;
        _editor.Foreground = Theme.TextBrush;
        _editor.CaretBrush = Theme.TextBrush;
        _editor.LineNumbersForeground = Theme.WeakTextBrush;
        _editor.SetMarkdownRenderMode(_document.IsMarkdown ? _settings.MarkdownRenderMode : MarkdownRenderModes.Off);
        _editor.SetImageReferenceTextMode(_settings.ImageReferenceTextMode);
        _editor.SetMarkdownEditAnimationEnabled(_settings.MarkdownEditAnimationEnabled);
        _editor.RefreshTypography();
    }

    private void UpdateTitle()
    {
        var name = _document.FilePath == null ? Strings.Get("StudioUntitled") : Path.GetFileName(_document.FilePath);
        _title.Text = name + (_document.IsDirty ? " *" : "");
        _title.ToolTip = _document.FilePath;
        Title = _title.Text + " — " + Strings.Get("StudioStandaloneTitle");
    }

    private void ShowFailure(Exception ex) => PaperNoticeDialog.Show(this,
        Strings.Get("StudioFileErrorTitle"), Strings.Format("StudioFileError", ex.GetBaseException().Message));
}
