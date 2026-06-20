using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private static readonly object PersistentScriptProcessLock = new();
    private static readonly Dictionary<string, Process> PersistentScriptProcesses = new(StringComparer.OrdinalIgnoreCase);

    public void UpdateMarkdownRenderMode()
    {
        if (_paper.Type == PaperTypes.Note && _noteBox != null)
        {
            var mode = _controller.State.MarkdownRenderMode;
            TraceNoteRender($"UpdateMarkdownRenderMode rebuild mode={mode}");
            RebuildNoteBodyForMarkdownMode();
        }
    }

    private void TraceNoteRender(string message)
    {
#if DEBUG
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "md-render-trace.log");
            var line = $"{DateTime.Now:HH:mm:ss.fff} paper={_paper.Id[..Math.Min(6, _paper.Id.Length)]} {message}{Environment.NewLine}";
            lock (NoteRenderTraceLock)
            {
                System.IO.File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Test-only diagnostics must never affect note interaction.
        }
#endif
    }

    private void RebuildNoteBodyForMarkdownMode()
    {
        if (_paper.Type != PaperTypes.Note)
        {
            return;
        }

        var oldBox = _noteBox;
        var text = oldBox?.Text ?? _paper.Content ?? "";
        var caret = oldBox?.CaretIndex ?? 0;
        var verticalOffset = oldBox?.VerticalOffset ?? 0;
        var horizontalOffset = oldBox?.HorizontalOffset ?? 0;
        _paper.Content = text;

        TraceNoteRender($"RebuildNoteBody start textLength={text.Length} caret={caret} v={verticalOffset:F1} h={horizontalOffset:F1}");

        var oldBodies = new List<UIElement>();
        if (_noteBodyElement != null)
        {
            oldBodies.Add(_noteBodyElement);
        }
        else
        {
            var zoomHost = _textZoomIndicator?.Parent as UIElement;
            foreach (UIElement child in _shell.Children)
            {
                if (Grid.GetRow(child) == 1 && !ReferenceEquals(child, zoomHost))
                {
                    oldBodies.Add(child);
                }
            }
        }

        _noteBox = null;
        _showNotePreview = null;

        var body = BuildNoteBody();
        body.Opacity = 0;
        body.IsHitTestVisible = false;
        Grid.SetRow(body, 1);
        Panel.SetZIndex(body, 1);
        _noteBodyElement = body;
        _shell.Children.Add(body);

        if (_noteBox == null)
        {
            TraceNoteRender("RebuildNoteBody end: no note box");
            return;
        }

        _noteBox.CaretIndex = Math.Clamp(caret, 0, _noteBox.Text.Length);
        _showNotePreview?.Invoke();
        body.UpdateLayout();

        Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (_noteBox == null)
                {
                    return;
                }

                foreach (var oldBody in oldBodies)
                {
                    _shell.Children.Remove(oldBody);
                }

                body.Opacity = 1;
                body.IsHitTestVisible = true;
                _noteBox.ScrollToHorizontalOffset(horizontalOffset);
                _noteBox.ScrollToVerticalOffset(verticalOffset);
                _showNotePreview?.Invoke();
                TraceNoteRender($"RebuildNoteBody restored caret={_noteBox.CaretIndex} v={verticalOffset:F1} h={horizontalOffset:F1}");
            }),
            System.Windows.Threading.DispatcherPriority.Render);
    }

    private void ExitNoteEditor()
    {
        if (_paper.Type != PaperTypes.Note || _noteBox == null)
        {
            return;
        }

        if (_noteBox.ContextMenu?.IsOpen == true)
        {
            return;
        }

        Keyboard.ClearFocus();
        _showNotePreview?.Invoke();
    }


    private UIElement BuildNoteBody()
    {
        var host = new Grid();

        _noteBox = new MarkdownTextBox
        {
            MaxLength = 100000,
            Text = _paper.Content ?? "",
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = TextBrush,
            CaretBrush = TextBrush,
            FontFamily = NoteTypography.FontFamily,
            FontSize = NoteTypography.FontSize,
            FontStyle = NoteTypography.FontStyle,
            FontWeight = NoteTypography.FontWeight,
            FontStretch = NoteTypography.FontStretch,
            Language = NoteTypography.Language,
            Margin = NoteTypography.ContentPadding,
            FocusVisualStyle = null
        };
        NoteTypography.ApplyTextRendering(_noteBox);
        var box = _noteBox;
        box.SetMarkdownRenderMode(_controller.State.MarkdownRenderMode);
        box.SetTextZoom(CurrentTextZoom());

        host.Children.Add(box);
        var editorMenu = CreateContextMenu();
        editorMenu.Items.Add(MenuHeader(Strings.Get("MenuFormat")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuBold"), (_, _) => box.WrapSelection("**", "**")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuItalic"), (_, _) => box.WrapSelection("*", "*")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuStrikethrough"), (_, _) => box.WrapSelection("~~", "~~")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuHeading"), (_, _) => box.InsertLinePrefix("# ")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuQuote"), (_, _) => box.InsertLinePrefix("> ")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuList"), (_, _) => box.InsertLinePrefix("- ")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuCodeBlock"), (_, _) => box.WrapSelection("```\n", "\n```")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuInsertLink"), (_, _) => box.InsertMarkdownLink()));
        editorMenu.Items.Add(MenuSeparator());
        editorMenu.Items.Add(MenuHeader(Strings.Get("MenuText")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuCopy"), (_, _) => box.Copy()));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuPaste"), (_, _) => box.Paste()));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuSelectAll"), (_, _) => box.SelectAll()));

        var previewMenu = BuildPaperContextMenu();
        var isPreviewing = false;
        var isEnteringEditorFromPreview = false;

        void ShowPreview()
        {
            TraceNoteRender($"ShowPreview before isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
            box.SelectionLength = 0;
            box.SetPreviewMode(true);
            box.ContextMenu = previewMenu;
            isPreviewing = true;
            TraceNoteRender($"ShowPreview after isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
        }

        _showNotePreview = ShowPreview;

        void ShowEditor(bool focus = true)
        {
            TraceNoteRender($"ShowEditor before focus={focus} isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
            box.SetPreviewMode(false);
            box.ContextMenu = editorMenu;
            isPreviewing = false;

            if (focus && !box.IsKeyboardFocusWithin)
            {
                box.Focus();
            }
            TraceNoteRender($"ShowEditor after focus={focus} isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode} focused={box.IsKeyboardFocusWithin}");
        }

        void ShowEditorAtPreviewPoint(Point previewPoint)
        {
            TraceNoteRender($"ShowEditorAtPreviewPoint x={previewPoint.X:F1} y={previewPoint.Y:F1}");
            var hasPreviewCaret = box.TryGetCharacterIndexFromPoint(previewPoint, out var caretIndex);

            isEnteringEditorFromPreview = true;
            ShowEditor(focus: false);

            if (!box.IsKeyboardFocusWithin)
            {
                box.Focus();
            }

            if (hasPreviewCaret)
            {
                box.CaretIndex = Math.Clamp(caretIndex, 0, box.Text.Length);
                box.SelectionLength = 0;
            }
            TraceNoteRender($"ShowEditorAtPreviewPoint after hasCaret={hasPreviewCaret} caret={box.CaretIndex}");
            Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    isEnteringEditorFromPreview = false;
                    TraceNoteRender($"ShowEditorAtPreviewPoint release focused={box.IsKeyboardFocusWithin} isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
                }),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        static void OpenMarkdownLink(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                // Link opening is optional; the note should never crash because a URL handler failed.
            }
        }

        box.TextChanged += (_, _) =>
        {
            var wasScriptCapsule = IsScriptCapsuleText(_paper.Content ?? "");
            _paper.Content = box.Text;
            var isScriptCapsule = IsScriptCapsuleText(_paper.Content ?? "");
            if (wasScriptCapsule != isScriptCapsule)
            {
                RefreshCapsuleLabel();
                RefreshPaperContextMenus();
                _controller.RefreshTodoRowsForLinkedNote(_paper.Id);
            }
            _controller.MarkDirty();
        };

        box.PreviewKeyDown += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
            {
                return;
            }

            if (e.Key == Key.B)
            {
                box.WrapSelection("**", "**");
                e.Handled = true;
            }
            else if (e.Key == Key.I)
            {
                box.WrapSelection("*", "*");
                e.Handled = true;
            }
            else if (e.Key == Key.K)
            {
                box.InsertMarkdownLink();
                e.Handled = true;
            }
        };

        box.GotKeyboardFocus += (_, _) =>
        {
            TraceNoteRender($"GotKeyboardFocus isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
        };

        box.LostKeyboardFocus += (_, _) =>
        {
            if (box.ContextMenu != null && box.ContextMenu.IsOpen)
            {
                TraceNoteRender("LostKeyboardFocus ignored: context menu open");
                return;
            }
            if (isEnteringEditorFromPreview)
            {
                TraceNoteRender($"LostKeyboardFocus ignored: entering editor isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
                return;
            }
            TraceNoteRender($"LostKeyboardFocus isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
            ShowPreview();
        };

        MouseButtonEventHandler noteMouseDown = (_, e) =>
        {
            if (IsScrollBarInteractionSource(e.OriginalSource as DependencyObject, box))
            {
                TraceNoteRender($"PreviewMouseLeftButtonDown ignored: scrollbar isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
                return;
            }

            var textViewPoint = e.GetPosition(box.TextArea.TextView);
            TraceNoteRender($"PreviewMouseLeftButtonDown isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode} handled={e.Handled}");
            if (!isPreviewing)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                    box.TryGetMarkdownLinkFromTextViewPoint(textViewPoint, out var editUrl))
                {
                    OpenMarkdownLink(editUrl);
                    e.Handled = true;
                }
                return;
            }

            var point = e.GetPosition(box);
            if (box.TryGetMarkdownLinkFromTextViewPoint(textViewPoint, out var url))
            {
                OpenMarkdownLink(url);
                e.Handled = true;
                return;
            }

            ShowEditorAtPreviewPoint(point);
            e.Handled = true;
        };
        box.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, noteMouseDown, true);

        box.MouseMove += (sender, e) =>
        {
            var isOverLink = box.TryGetMarkdownLinkFromTextViewPoint(e.GetPosition(box.TextArea.TextView), out _);
            if (isPreviewing)
            {
                box.SetInteractionCursor(isOverLink ? Cursors.Hand : Cursors.Arrow);
            }
            else
            {
                box.SetInteractionCursor(isOverLink && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
                    ? Cursors.Hand
                    : Cursors.IBeam);
            }
        };

        box.MouseLeave += (_, _) =>
        {
            box.SetInteractionCursor(isPreviewing ? Cursors.Arrow : Cursors.IBeam);
        };

        editorMenu.Closed += (_, _) =>
        {
            if (!isPreviewing && !box.IsFocused && !box.IsKeyboardFocusWithin)
            {
                ShowPreview();
            }
        };

        if (box.IsFocused || string.IsNullOrEmpty(box.Text))
        {
            ShowEditor();
        }
        else
        {
            ShowPreview();
        }

        return host;
    }


    public void UpdateTextZoom()
    {
        if (_paper.Type != PaperTypes.Note)
        {
            return;
        }

        var zoom = CurrentTextZoom();
        if (_noteBox != null)
        {
            var expectedFontSize = Math.Round(NoteTypography.FontSize * zoom, 1);
            if (IsLoaded && Math.Abs(_noteBox.FontSize - expectedFontSize) > 0.001)
            {
                RebuildNoteBodyForMarkdownMode();
            }
            else
            {
                _noteBox.SetTextZoom(zoom);
            }
        }

        if (_textZoomIndicator != null)
        {
            _textZoomIndicator.Text = $"{(int)Math.Round(zoom * 100)}%";
            _textZoomIndicator.Foreground = WeakTextBrush;
            _textZoomIndicator.Opacity = 0.55;
            if (_textZoomIndicator.Parent is UIElement host)
            {
                host.Visibility = Math.Abs(zoom - 1.0) < 0.001 ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }

    private double CurrentTextZoom()
    {
        return Math.Clamp(_paper.TextZoom, 0.5, 1.5);
    }

    private void OnWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_paper.Type != PaperTypes.Note)
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        var step = e.Delta > 0 ? 0.1 : -0.1;
        _controller.SetPaperTextZoom(_paper, _paper.TextZoom + step);
        e.Handled = true;
    }

    private void OpenMarkdownInDefaultEditor()
    {
        if (_paper.Type != PaperTypes.Note)
        {
            return;
        }

        try
        {
            var path = WriteExternalMarkdownFile();
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Strings.Format("OpenMarkdownFailureMessage", CurrentExternalMarkdownExtension(), ex.Message),
                Strings.Get("OpenMarkdownFailureTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    public void UpdateExternalMarkdownExtension()
    {
        if (_openMarkdownButton != null)
        {
            _openMarkdownButton.Content = ExternalOpenButtonLabel();
            _openMarkdownButton.ToolTip = OpenMarkdownEditorToolTip();
            _openMarkdownButton.Visibility = _controller.State.ShowTopBarExternalOpenButton ? Visibility.Visible : Visibility.Collapsed;
        }
    }


    private string OpenMarkdownEditorToolTip()
    {
        return Strings.Format("ToolTipOpenMarkdownEditor", CurrentExternalMarkdownExtension());
    }

    private string ExternalOpenButtonLabel()
    {
        var extension = CurrentExternalMarkdownExtension().TrimStart('.');
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ExternalMarkdownFileExtensions.Default.TrimStart('.');
        }

        return extension.Length > 2
            ? extension[..2].ToUpperInvariant()
            : extension.ToUpperInvariant();
    }

    private string CurrentExternalMarkdownExtension()
    {
        return ExternalMarkdownFileExtensions.Normalize(_controller.State.ExternalMarkdownExtension);
    }

    private string WriteExternalMarkdownFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaperTodo");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"paper-{_paper.Id}{CurrentExternalMarkdownExtension()}");
        var text = _noteBox?.Text ?? _paper.Content ?? "";
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private readonly record struct ScriptCapsuleSpec(string Engine, string Script, bool UsePersistentProcess);
    private readonly record struct ScriptCapsuleMarkerSpec(string Engine, bool UsePersistentProcess);

    private string CapsuleIconText()
    {
        if (IsScriptCapsule())
        {
            return "⚡";
        }

        return _paper.Type == PaperTypes.Note ? "✎" : "✓";
    }

    private double CapsuleIconFontSizeForCurrentPaper()
    {
        return IsScriptCapsule() ? CapsuleIconFontSize + 2 : CapsuleIconFontSize;
    }

    private bool IsScriptCapsule()
    {
        return TryGetScriptCapsule(out _);
    }

    internal static bool IsScriptCapsuleContent(string? text)
    {
        return IsScriptCapsuleText(text ?? "");
    }

    private void ActivateFromCollapsedCapsule()
    {
        if (TryRunScriptCapsule())
        {
            return;
        }

        SetCollapsedState(false);
    }

    private void OpenCapsuleForEditing()
    {
        if (_paper.IsCollapsed)
        {
            if (HasDeepCapsuleSlotPlacement)
            {
                ShowMainWindowForDeepCapsuleActivation();
                SetCollapsedState(false, alignExpandedToDockedEdge: true);
            }
            else
            {
                SetCollapsedState(false);
            }

            return;
        }

        EnsureExpandedSurfaceGeometry(alignToDockedEdge: HasDeepCapsuleSlotPlacement);
        _controller.BringPaperToFront(_paper);
    }

    internal bool TryRunScriptCapsule()
    {
        if (!TryGetScriptCapsule(out var spec))
        {
            return false;
        }

        _ = RunScriptCapsuleAsync(spec);
        return true;
    }

    private bool TryGetScriptCapsule(out ScriptCapsuleSpec spec)
    {
        spec = default;
        if (_paper.Type != PaperTypes.Note)
        {
            return false;
        }

        var text = _noteBox?.Text ?? _paper.Content ?? "";
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var firstLineEnd = text.IndexOfAny(new[] { '\r', '\n' });
        var firstLine = firstLineEnd >= 0 ? text[..firstLineEnd] : text;
        if (!TryParseScriptCapsuleMarker(firstLine, out var markerSpec))
        {
            return false;
        }

        var scriptStart = firstLineEnd < 0 ? text.Length : firstLineEnd;
        if (scriptStart < text.Length && text[scriptStart] == '\r')
        {
            scriptStart++;
        }
        if (scriptStart < text.Length && text[scriptStart] == '\n')
        {
            scriptStart++;
        }

        spec = new ScriptCapsuleSpec(
            markerSpec.Engine,
            NormalizeScriptCapsuleIndent(text[scriptStart..]),
            markerSpec.UsePersistentProcess);
        return true;
    }

    private static bool IsScriptCapsuleText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var firstLineEnd = text.IndexOfAny(new[] { '\r', '\n' });
        var firstLine = firstLineEnd >= 0 ? text[..firstLineEnd] : text;
        return TryParseScriptCapsuleMarker(firstLine, out _);
    }

    private static bool TryParseScriptCapsuleMarker(string firstLine, out ScriptCapsuleMarkerSpec spec)
    {
        spec = default;
        var marker = firstLine.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        spec = marker switch
        {
            "!pf" or "!powerf" => new ScriptCapsuleMarkerSpec("auto", true),
            "!p" or "!power" => new ScriptCapsuleMarkerSpec("auto", false),
            "!pwsh" or "!ps7" => new ScriptCapsuleMarkerSpec("pwsh", false),
            "!ps5" or "!winps" => new ScriptCapsuleMarkerSpec("powershell", false),
            _ => default
        };
        return !string.IsNullOrEmpty(spec.Engine);
    }

    private async Task RunScriptCapsuleAsync(ScriptCapsuleSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Script))
        {
            ShowScriptCapsuleFailure(Strings.Get("ScriptCapsuleEmptyMessage"));
            return;
        }

        if (spec.UsePersistentProcess && _controller.State.UsePersistentPowerShellProcess)
        {
            RunPersistentScriptCapsule(spec);
            return;
        }

        string? path = null;
        try
        {
            path = WriteScriptCapsuleFile(spec.Script);
            var executable = ResolvePowerShellExecutable(spec.Engine);
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = _controller.State.HideScriptRunWindow,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(EncodedPowerShellLaunchCommand(path));

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                ShowScriptCapsuleFailure(Strings.Get("ScriptCapsuleStartFailureMessage"));
                return;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                var detail = CompactScriptCapsuleOutput(output, error);
                ShowScriptCapsuleFailure(Strings.Format("ScriptCapsuleExitFailureMessage", process.ExitCode, detail));
            }
        }
        catch (Exception ex)
        {
            ShowScriptCapsuleFailure(ex.Message);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Temporary script cleanup must not affect the user's note.
                }
            }
        }
    }

    private void RunPersistentScriptCapsule(ScriptCapsuleSpec spec)
    {
        string? path = null;
        var submitted = false;
        try
        {
            path = WriteScriptCapsuleFile(spec.Script);
            var executable = ResolvePowerShellExecutable(spec.Engine);
            var process = EnsurePersistentScriptProcess(executable, _controller.State.HideScriptRunWindow);
            var escapedPath = path.Replace("'", "''", StringComparison.Ordinal);
            process.StandardInput.WriteLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
            process.StandardInput.WriteLine("$OutputEncoding = [System.Text.Encoding]::UTF8");
            process.StandardInput.WriteLine($"try {{ & '{escapedPath}' }} finally {{ Remove-Item -LiteralPath '{escapedPath}' -ErrorAction SilentlyContinue }}");
            process.StandardInput.Flush();
            submitted = true;
        }
        catch (Exception ex)
        {
            ShowScriptCapsuleFailure(ex.Message);
        }
        finally
        {
            if (!submitted && !string.IsNullOrWhiteSpace(path))
            {
                DeleteScriptCapsuleFile(path);
            }
        }
    }

    private static Process EnsurePersistentScriptProcess(string executable, bool hideWindow)
    {
        var key = $"{executable}|{hideWindow}";
        lock (PersistentScriptProcessLock)
        {
            if (PersistentScriptProcesses.TryGetValue(key, out var existing) && !existing.HasExited)
            {
                return existing;
            }

            if (existing != null)
            {
                existing.Dispose();
                PersistentScriptProcesses.Remove(key);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = hideWindow,
                RedirectStandardInput = true,
                StandardInputEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-NoExit");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("-");

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += (_, _) =>
            {
                lock (PersistentScriptProcessLock)
                {
                    if (PersistentScriptProcesses.TryGetValue(key, out var current) && ReferenceEquals(current, process))
                    {
                        PersistentScriptProcesses.Remove(key);
                    }
                }

                process.Dispose();
            };
            process.Start();
            PersistentScriptProcesses[key] = process;
            return process;
        }
    }

    private static string NormalizeScriptCapsuleIndent(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return script;
        }

        var normalized = script.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var commonIndent = int.MaxValue;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var indent = 0;
            while (indent < line.Length && line[indent] is ' ' or '\t')
            {
                indent++;
            }
            commonIndent = Math.Min(commonIndent, indent);
        }

        if (commonIndent is int.MaxValue or <= 0)
        {
            return script;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var remove = Math.Min(commonIndent, LeadingWhitespaceLength(lines[i]));
            lines[i] = lines[i][remove..];
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static int LeadingWhitespaceLength(string text)
    {
        var length = 0;
        while (length < text.Length && text[length] is ' ' or '\t')
        {
            length++;
        }

        return length;
    }

    private string WriteScriptCapsuleFile(string script)
    {
        var directory = ScriptCapsuleTempDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"script-{_paper.Id}-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(path, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private static string ScriptCapsuleTempDirectory()
    {
        return Path.Combine(Path.GetTempPath(), "PaperTodo", "Scripts");
    }

    private static void DeleteScriptCapsuleFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Temporary script cleanup must not affect the user's note.
        }
    }

    internal static void CleanupOldScriptCapsuleTempFiles()
    {
        try
        {
            var directory = ScriptCapsuleTempDirectory();
            if (!Directory.Exists(directory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (var path in Directory.EnumerateFiles(directory, "script-*.ps1"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private string ResolvePowerShellExecutable(string engine)
    {
        return ResolvePowerShellExecutable(_controller.State, engine);
    }

    internal static void EnsurePersistentScriptProcessForSettings(AppState state)
    {
        if (!state.UsePersistentPowerShellProcess)
        {
            return;
        }

        try
        {
            var executable = ResolvePowerShellExecutable(state, "auto");
            EnsurePersistentScriptProcess(executable, state.HideScriptRunWindow);
        }
        catch
        {
            // Prewarming is best-effort; explicit script execution will report failures.
        }
    }

    private static string ResolvePowerShellExecutable(AppState state, string engine)
    {
        if (engine == "pwsh")
        {
            return FindPowerShellExecutable("pwsh.exe")
                ?? throw new InvalidOperationException(Strings.Get("ScriptCapsulePowerShell7NotFound"));
        }

        if (engine == "powershell")
        {
            return "powershell.exe";
        }

        if (state.PreferPowerShell7)
        {
            var pwsh = FindPowerShellExecutable("pwsh.exe");
            if (!string.IsNullOrWhiteSpace(pwsh))
            {
                return pwsh;
            }
        }

        return "powershell.exe";
    }

    private static string? FindPowerShellExecutable(string fileName)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PATH")))
        {
            candidates.AddRange(
                (Environment.GetEnvironmentVariable("PATH") ?? "")
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Select(path => Path.Combine(path.Trim(), fileName)));
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "PowerShell", "7", fileName));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string EncodedPowerShellLaunchCommand(string path)
    {
        var escapedPath = path.Replace("'", "''", StringComparison.Ordinal);
        var command = string.Join(
            "; ",
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8",
            "$OutputEncoding = [System.Text.Encoding]::UTF8",
            $"& '{escapedPath}'");
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
    }

    private static string CompactScriptCapsuleOutput(string output, string error)
    {
        var text = string.Join(
            Environment.NewLine,
            new[] { error.Trim(), output.Trim() }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(text))
        {
            return Strings.Get("ScriptCapsuleNoOutput");
        }

        const int maxLength = 1800;
        return text.Length <= maxLength ? text : text[^maxLength..];
    }

    private void ShowScriptCapsuleFailure(string message)
    {
        MessageBox.Show(
            message,
            Strings.Get("ScriptCapsuleFailureTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void RefreshPaperContextMenus()
    {
        if (_capsuleLeftArea != null)
        {
            _capsuleLeftArea.ContextMenu = BuildPaperContextMenu();
        }
        if (_deepCapsuleSlotLeftArea != null)
        {
            _deepCapsuleSlotLeftArea.ContextMenu = BuildDeepCapsuleSlotContextMenu();
        }
        if (_paperChrome != null)
        {
            _paperChrome.ContextMenu = BuildPaperContextMenu();
        }
    }

    internal static void StopPersistentScriptProcesses()
    {
        lock (PersistentScriptProcessLock)
        {
            foreach (var process in PersistentScriptProcesses.Values.ToList())
            {
                try
                {
                    if (!process.HasExited)
                    {
                        try
                        {
                            if (process.StartInfo.RedirectStandardInput)
                            {
                                process.StandardInput.Close();
                            }
                        }
                        catch
                        {
                            // The process may already be exiting or the pipe may be broken.
                        }

                        if (!process.WaitForExit(250))
                        {
                            process.Kill(entireProcessTree: true);
                            process.WaitForExit(1000);
                        }
                    }
                }
                catch
                {
                    // Persistent script sessions are optional and disposable.
                }
                finally
                {
                    process.Dispose();
                }
            }

            PersistentScriptProcesses.Clear();
        }
    }

}
