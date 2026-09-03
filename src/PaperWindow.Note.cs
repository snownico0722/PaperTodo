using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal const int NoteTextMaxLength = 100000;
    private static readonly object PersistentScriptProcessLock = new();
    private static readonly Dictionary<string, Process> PersistentScriptProcesses = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ActiveScriptProcessLock = new();
    private static readonly Dictionary<Guid, Process> ActiveScriptProcesses = new();

    // 由 BuildNoteBody 闭包内的 RebuildFullRenderPanelForZoom 局部函数注入:
    // Ctrl+滚轮缩放走 UpdateTextZoom → 本字段,原地重建 fullRenderPanel(无淡入淡出)。
    // presenter 取消(隐藏/关闭/重建)时清空,避免对旧闭包触发重建。
    private Action? _rebuildFullRenderPanelForZoom;

    internal void RefreshNoteForExternalChange()
    {
        if (_paper.Type == PaperTypes.Note)
        {
            RefreshCurrentPaperBodyFromModel();
        }
    }

    private int BeginNotePresenterSession()
    {
        CancelNotePresenterDeferredWork();
        _cancelNotePresenterInteractions = null;
        _showNotePreview = null;
        _notePreviewContextMenu = null;
        return ++_notePresenterGeneration;
    }

    private bool IsCurrentNotePresenter(int presenterGeneration, MarkdownTextBox box)
    {
        return presenterGeneration == _notePresenterGeneration &&
            ReferenceEquals(_noteBox, box) &&
            _windowLifecycle == PaperWindowLifecycleState.Alive &&
            !IsClosed;
    }

    private bool IsCurrentNoteDeferredWork(
        int presenterGeneration,
        int deferredWorkGeneration,
        MarkdownTextBox box)
    {
        return deferredWorkGeneration == _noteDeferredWorkGeneration &&
            IsCurrentNotePresenter(presenterGeneration, box);
    }

    // Window lifecycle code calls this before hiding/closing so queued focus/image work from
    // the old interaction cannot run against a later presenter generation.
    private void CancelNotePresenterDeferredWork()
    {
        _rebuildFullRenderPanelForZoom = null;
        _markdownBodySession?.CancelPresenterDeferredWork();
    }

    public void UpdateMarkdownRenderMode()
    {
        if (_paper.Type == PaperTypes.Note && _noteBox != null)
        {
            var mode = _controller.State.MarkdownRenderMode;
            TraceNoteRender($"UpdateMarkdownRenderMode mode={mode}");
            _noteBox.SetMarkdownRenderMode(mode);
        }
    }

    public void UpdateImageReferenceTextMode()
    {
        if (_paper.Type == PaperTypes.Note && _noteBox != null)
        {
            _noteBox.SetImageReferenceTextMode(_controller.State.ImageReferenceTextMode);
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
        var presenterGeneration = BeginNotePresenterSession();
        var host = new Grid();

        _noteBox = new MarkdownTextBox
        {
            MaxLength = NoteTextMaxLength,
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
        var box = _noteBox;
        box.ImageContextMenuFactory = CreateContextMenu;
        box.SetMarkdownRenderMode(_controller.State.MarkdownRenderMode);
        box.SetImageReferenceTextMode(_controller.State.ImageReferenceTextMode);
        box.SetTextZoom(CurrentTextZoom());
        box.ConfigureNoteImages(_paper.Id, _controller.ImageStore);
        _noteContentDirty = box.Document.TextLength != (_paper.Content?.Length ?? 0);
        _liveIsScriptCapsule = IsScriptCapsuleDocument(box);
        box.ImageImportFailed += ShowNoteImageImportFailure;
        box.PasteRejected += ShowNotePasteRejected;
        // New MarkdownTextBox defaults to rendering images; re-apply hide/collapse/minimize policy.
        SyncNoteImagePresentationState();

        host.Children.Add(box);
        var isPreviewing = false;
        var isEnteringEditorFromPreview = false;
        var isInteractingWithImage = false;
        var isOpeningImagePicker = false;
        int? pendingImageReferenceOffset = null;
        string? pendingImageId = null;
        var editorEntryGeneration = 0;
        var imageInteractionGeneration = 0;

        // Full render mode state
        ScrollViewer? fullRenderPanel = null;
        // 水平偏移与光标位置在渲染面板内无对等概念,按原值保留;
        // 垂直位置由 SyncEditorScrollToRenderPanel 按比例同步。
        double savedHorizontalOffset = 0;
        int savedCaretIndex = 0;

        // 切换动画进行中标记,防止 ShowPreview/ShowEditor/缩放重建互相叠加。
        bool isRenderModeTransitioning = false;
        int renderModeTransitionGeneration = 0;

        // 上一次渲染快照:与 ShowPreview.needsRebuild 比对以决定复用还是重建,
        // 避免失焦/缩放每次都重解码 BitmapImage 与重新 layout。
        string _lastRenderedText = "";
        double _lastRenderedZoom = -1;
        double _lastRenderedTargetWidthDip = -1;
        // 供 ScrollChanged / Unloaded / panelExit 时维护视口保护。
        NoteImageStore? _fullRenderImageStore = null;
        // 与 box 端的 owner key 隔离,避免 panel 与 box 视口保护互相覆盖。
        const string FullRenderViewportOwnerSuffix = "#full";

        // 拖动窗口/纸片期间就地更新图片尺寸,不动 BitmapImage——重建路径
        // 会因重新解码产生 50-300ms 的空白闪烁,体感"图片不跟手"。
        // 阈值与 ShowPreview.needsRebuild 保持一致,避免无意义更新。
        DispatcherTimer? _fullRenderPanelResizeSettleTimer = null;
        bool _isFullRenderResizePreview = false;
        const double FullRenderPanelResizeSettleMs = 150;
        const double FullRenderPanelResizeThresholdDip = 0.5;

        // 按内容百分比把编辑器滚动位置同步到渲染面板,避免切换后被置顶。
        void SyncEditorScrollToRenderPanel()
        {
            // 强制 layout 以拿到当前 ExtentHeight/FontSize 下的真实比例;
            // zoom 改 FontSize 后若不 UpdateLayout,比例会按旧值算,导致面板跳位。
            // 调用方保证 fullRenderPanel 非空,这里仅做防御性空检查。
            if (fullRenderPanel == null)
            {
                return;
            }
            box.UpdateLayout();
            fullRenderPanel.UpdateLayout();
            var boxScrollable = Math.Max(0.001, box.ExtentHeight - box.ViewportHeight);
            var panelScrollable = Math.Max(0, fullRenderPanel.ExtentHeight - fullRenderPanel.ViewportHeight);
            var ratio = Math.Clamp(box.VerticalOffset / boxScrollable, 0, 1);
            fullRenderPanel.ScrollToVerticalOffset(ratio * panelScrollable);
            TraceNoteRender($"SyncEditorScroll->Panel ratio={ratio:F3} " +
                $"boxV={box.VerticalOffset:F1}/{boxScrollable:F1} " +
                $"panelScrollable={panelScrollable:F1}");
        }

        bool IsCurrentPresenter()
        {
            return IsCurrentNotePresenter(presenterGeneration, box);
        }

        _cancelNotePresenterInteractions = () =>
        {
            if (!IsCurrentPresenter())
            {
                return;
            }

            editorEntryGeneration++;
            imageInteractionGeneration++;
            isEnteringEditorFromPreview = false;
            isInteractingWithImage = false;
            pendingImageReferenceOffset = null;
            pendingImageId = null;
        };

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
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuInsertImage"), (_, _) =>
        {
            isOpeningImagePicker = true;
            try
            {
                ShowEditor(focus: false);
                var imagePaths = SelectImagesFromFilePicker();
                if (imagePaths.Length == 0 || !IsCurrentPresenter())
                {
                    return;
                }

                // The modal picker can run deactivation/focus callbacks before returning.
                // Reassert edit mode only after confirming this presenter is still current.
                ShowEditor(focus: false);
                InsertImageFiles(box, imagePaths);
            }
            finally
            {
                isOpeningImagePicker = false;
                ShowEditor();
            }
        }));
        editorMenu.Items.Add(MenuSeparator());
        editorMenu.Items.Add(MenuHeader(Strings.Get("MenuText")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuCopy"), (_, _) => box.Copy()));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuPaste"), (_, _) => box.Paste()));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuSelectAll"), (_, _) => box.SelectAll()));

        // 挂载:必须在所有被 OnHostSizeChanged 捕获的闭包变量声明之后订阅,
        // 否则 C# 编译器会报 CS0165(变量声明顺序问题)。
        host.SizeChanged += OnHostSizeChanged;

        _notePreviewContextMenu = BuildPaperContextMenu();
        void ShowPreview()
        {
            if (!IsCurrentPresenter())
            {
                return;
            }

            var alreadyPreviewing = isPreviewing && box.IsPreviewMode;
            TraceNoteRender($"ShowPreview before isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode} already={alreadyPreviewing}");
            box.ClearImageSelection();
            box.SelectionLength = 0;

            // Full render mode: switch to rendered panel instead of just fading markers
            if (box.IsFullRenderMode && !alreadyPreviewing)
            {
                // 切换动画中再次触发直接吞掉,防止多动画叠加。
                if (isRenderModeTransitioning)
                {
                    TraceNoteRender("ShowPreview skipped: render-mode transition in flight");
                    return;
                }
                isRenderModeTransitioning = true;
                var generation = ++renderModeTransitionGeneration;

                savedHorizontalOffset = box.HorizontalOffset;
                savedCaretIndex = box.CaretIndex;

                var currentText = box.PersistentText;
                var currentZoom = CurrentTextZoom();
                var currentTargetWidth = ComputeFullRenderTargetWidth(host);
                // 文本/缩放/宽度全部命中上次快照时复用现有 panel,
                // 跳过重新解析 markdown 与重新解码 BitmapImage。
                var needsRebuild =
                    fullRenderPanel == null ||
                    !string.Equals(_lastRenderedText, currentText, StringComparison.Ordinal) ||
                    Math.Abs(_lastRenderedZoom - currentZoom) > 0.001 ||
                    Math.Abs(_lastRenderedTargetWidthDip - currentTargetWidth) > 0.5;

                if (needsRebuild)
                {
                    if (fullRenderPanel != null)
                    {
                        // 显式解绑 Unloaded handler:WPF 的 Unloaded 由 BroadcastEventHelper
                        // 异步派发,旧 panel 的回调可能在新建之后才触发,误清新 panel 的图。
                        fullRenderPanel.Unloaded -= OnFullRenderPanelUnloaded;
                        host.Children.Remove(fullRenderPanel);
                        DisposeFullRenderPanelImages(fullRenderPanel);
                        fullRenderPanel = null;
                    }
                    fullRenderPanel = CreateFullRenderPanel(
                        currentText,
                        _controller.ImageStore,
                        _paper.Id);
                }
                else
                {
                    // 复用:把上一轮 ShowEditor 留下的 Collapsed/Opacity=0 复位,跳过整次创建。
                    var existingPanel = fullRenderPanel!;
                    existingPanel.Visibility = Visibility.Visible;
                    existingPanel.IsHitTestVisible = false;
                    existingPanel.Opacity = 0;
                    existingPanel.RenderTransform = null;
                    if (!host.Children.Contains(existingPanel))
                    {
                        host.Children.Add(existingPanel);
                    }
                    RefreshFullRenderPanelViewportProtection();
                }

                // 把 panel 挂到 host 但保持不可点,为淡入做准备(与 ShowEditor 对称:仅淡入淡出)。
                // 复用分支可能已把 panel 留在 host.Children,这里只切 Visibility/Opacity,避免重复 Add。
                var activePanel = fullRenderPanel!;
                if (!host.Children.Contains(activePanel))
                {
                    host.Children.Add(activePanel);
                }
                activePanel.Visibility = Visibility.Visible;
                activePanel.Opacity = 0;
                activePanel.IsHitTestVisible = false;
                // 清掉旧 RenderTransform,防止下次淡入被 Y 起点干扰。
                activePanel.RenderTransform = null;

                // 提前同步滚动位置:此时 panel 仍 Opacity=0,设 VerticalOffset 不影响视觉,
                // 但保证 220ms 淡入动画期间显示的就是与编辑器对应的位置,避免"先置顶再闪回"。
                SyncEditorScrollToRenderPanel();

                // 动画期间编辑器也不可点,避免与入场面板抢命中。
                box.IsHitTestVisible = false;

                isPreviewing = true;
                TraceNoteRender($"ShowPreview Full mode active, render panel visible");

                EventHandler panelEnterOnComplete = (_, _) =>
                {
                    if (generation != renderModeTransitionGeneration) return;
                    activePanel.IsHitTestVisible = true;
                    box.IsHitTestVisible = true;
                    isRenderModeTransitioning = false;
                    TraceNoteRender("ShowPreview Full mode transition complete");
                };

                EventHandler boxExitOnComplete = (_, _) =>
                {
                    if (generation != renderModeTransitionGeneration) return;
                    box.Visibility = Visibility.Collapsed;
                    box.Opacity = 1; // 重置为下次进入渲染前的初始值
                };

                AnimationHelper.FadeTo(box, 0, 180, AnimationHelper.QuickEase, boxExitOnComplete);
                // Render 优先级启动入场动画,确保 panel 已完成首帧布局,避免起步闪烁。
                Dispatcher.BeginInvoke(() =>
                {
                    if (generation != renderModeTransitionGeneration) return;
                    AnimationHelper.FadeTo(activePanel, 1, 220, AnimationHelper.SmoothEase, panelEnterOnComplete);
                }, System.Windows.Threading.DispatcherPriority.Render);
                return;
            }

            if (!alreadyPreviewing)
            {
                box.SetPreviewMode(true);
                box.ContextMenu = _notePreviewContextMenu ??= BuildPaperContextMenu();
                isPreviewing = true;
                // Focus can be cleared by the caller before preview mode is entered. Defer the
                // decision until WPF has finished the current focus transition, then park focus
                // on the active window only when no child control has claimed it. This keeps the
                // window-level ESC handler available without stealing focus from title editing.
                var deferredWorkGeneration = _noteDeferredWorkGeneration;
                Dispatcher.BeginInvoke(
                    (Action)(() =>
                    {
                        if (!IsCurrentNoteDeferredWork(
                                presenterGeneration,
                                deferredWorkGeneration,
                                box))
                        {
                            return;
                        }

                        if (isPreviewing &&
                            IsActive &&
                            !IsKeyboardFocusWithin &&
                            !IsPaperContextMenuInteractionActive)
                        {
                            Focus();
                        }
                    }),
                    System.Windows.Threading.DispatcherPriority.Input);
            }
            TraceNoteRender($"ShowPreview after isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode} already={alreadyPreviewing}");
        }

        _showNotePreview = ShowPreview;

        void ShowEditor(bool focus = true)
        {
            if (!IsCurrentPresenter())
            {
                return;
            }

            TraceNoteRender($"ShowEditor before focus={focus} isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");

            // Full render mode: switch back to editor
            if (box.IsFullRenderMode && isPreviewing && fullRenderPanel != null)
            {
                // 动画进行中直接吞掉,防止连点导致动画叠加
                if (isRenderModeTransitioning)
                {
                    TraceNoteRender("ShowEditor skipped: render-mode transition in flight");
                    return;
                }
                isRenderModeTransitioning = true;
                var generation = ++renderModeTransitionGeneration;

                // box 先现身,但保持不可点,准备播放淡入动画。
                box.Visibility = Visibility.Visible;
                box.Opacity = 0;
                box.IsHitTestVisible = false;

                EventHandler panelExitOnComplete = (_, _) =>
                {
                    if (generation != renderModeTransitionGeneration) return;
                    fullRenderPanel.Visibility = Visibility.Collapsed;
                    fullRenderPanel.IsHitTestVisible = false;
                    // 释放 panel 的视口保护,让 LRU 自然管理冷门图片;
                    // 下次 ShowPreview 会在 ScrollChanged 中重新登记。
                    if (_fullRenderImageStore != null)
                    {
                        _fullRenderImageStore.SetViewportProtectedBitmapIds(
                            $"{_paper.Id}{FullRenderViewportOwnerSuffix}",
                            Array.Empty<string>());
                    }
                };

                EventHandler boxEnterOnComplete = (_, _) =>
                {
                    if (generation != renderModeTransitionGeneration) return;
                    box.IsHitTestVisible = true;
                    isRenderModeTransitioning = false;
                    TraceNoteRender("ShowEditor Full mode transition complete");
                };

                AnimationHelper.FadeTo(fullRenderPanel, 0, 160, AnimationHelper.QuickEase, panelExitOnComplete);

                // 按渲染面板的滚动比例同步回编辑器,保留"用户在渲染里滚过的位置"；
                // 水平偏移和光标位置无 panel 对等概念,直接还原。
                fullRenderPanel.UpdateLayout();
                var panelScrollable = Math.Max(0.001, fullRenderPanel.ExtentHeight - fullRenderPanel.ViewportHeight);
                var boxScrollable = Math.Max(0, box.ExtentHeight - box.ViewportHeight);
                var scrollRatio = Math.Clamp(fullRenderPanel.VerticalOffset / panelScrollable, 0, 1);
                var targetBoxVerticalOffset = scrollRatio * boxScrollable;
                TraceNoteRender($"ShowEditor Full mode scroll sync ratio={scrollRatio:F3} " +
                    $"panelV={fullRenderPanel.VerticalOffset:F1}/{panelScrollable:F1} -> " +
                    $"boxV={targetBoxVerticalOffset:F1}/{boxScrollable:F1}");

                // 滚动与光标一次性复原,避免在动画过程中再修改造成视觉抖动。
                box.SetPreviewMode(false);
                box.ContextMenu = editorMenu;
                box.ScrollToVerticalOffset(targetBoxVerticalOffset);
                box.ScrollToHorizontalOffset(savedHorizontalOffset);
                box.CaretIndex = Math.Clamp(savedCaretIndex, 0, box.Text.Length);

                isPreviewing = false;
                AnimationHelper.FadeTo(box, 1, 220, AnimationHelper.SmoothEase, boxEnterOnComplete);
                TraceNoteRender($"ShowEditor Full mode editor fade-in started");
            }
            else
            {
                box.SetPreviewMode(false);
                box.ContextMenu = editorMenu;
                isPreviewing = false;
            }

            if (focus && !box.IsKeyboardFocusWithin)
            {
                box.Focus();
            }
            TraceNoteRender($"ShowEditor after focus={focus} isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode} focused={box.IsKeyboardFocusWithin}");
        }

        ScrollViewer CreateFullRenderPanel(string markdownText, NoteImageStore? imageStore, string noteId)
        {
            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = Brushes.Transparent,
                // App.xaml 的 ScrollViewer ControlTemplate 不消费 Padding,这里用 Margin
                // 让滚动条与编辑模式的 box 占据同一内缩区,否则渲染面板的滚动条会贴
                // cell 边缘,与编辑器滚动条位置不一致。
                Margin = NoteTypography.ContentPadding,
                IsTabStop = false,
                Focusable = false
            };

            var contentStack = new StackPanel { Background = Brushes.Transparent };
            contentStack.SetResourceReference(Control.ForegroundProperty, "TextBrushKey");

            // 滚动时把当前可见 imageId 推到 NoteImageStore 的视口保护集合,
            // 避免来回滚动时被 LRU 误驱逐。Unloaded 时清掉保护并清空 Source 以释放 BitmapImage。
            scrollViewer.ScrollChanged += OnFullRenderPanelScrollChanged;
            scrollViewer.Unloaded += OnFullRenderPanelUnloaded;

            // 点击 ScrollViewer 空白区域切回编辑模式;滚动条自身处理自己的点击/拖动。
            scrollViewer.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler((s, e) =>
                {
                    if (IsScrollBarInteractionSource(e.OriginalSource as DependencyObject, scrollViewer))
                    {
                        return;
                    }
                    if (IsHyperlinkInteractionSource(e.Source as DependencyObject))
                    {
                        // Hyperlink 自己处理 click + RequestNavigate,不要 e.Handled = true。
                        return;
                    }
                    e.Handled = true;
                    ShowEditor(focus: true);
                }), true);

            contentStack.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler((s, e) =>
                {
                    if (IsHyperlinkInteractionSource(e.Source as DependencyObject))
                    {
                        return;
                    }
                    e.Handled = true;
                    ShowEditor(focus: true);
                }), true);

            // Ctrl+滚轮缩放走 Window 根的 OnWindowPreviewMouseWheel,经 controller
            // (SetPaperTextZoom) → UpdateTextZoom → _rebuildFullRenderPanelForZoom
            // 这条原地路径处理,全程不经这里,所以不再挂 PreviewMouseWheel handler。

            var zoom = CurrentTextZoom();
            var targetWidthDip = ComputeFullRenderTargetWidth(host);
            var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            // 记录本次渲染快照,供 ShowPreview 复用判定。
            _fullRenderImageStore = imageStore;
            _lastRenderedText = markdownText;
            _lastRenderedZoom = zoom;
            _lastRenderedTargetWidthDip = targetWidthDip;

            var imageContext = new FullRenderImageContext(
                imageStore,
                noteId,
                targetWidthDip,
                dpiScale);
            // 复用下面的静态 OpenMarkdownLink,避免重复实现 URL 启动 + try/catch。
            MarkdownEdgeCapsulePreviewRenderer.RenderFullMarkdownToStackPanel(
                contentStack,
                markdownText,
                OpenMarkdownLink,
                zoom,
                imageContext);

            // 首次创建后立即推一次视口保护,让首屏图片立刻受 LRU 保护。
            RefreshFullRenderPanelViewportProtection();

            scrollViewer.Content = contentStack;
            return scrollViewer;
        }

        // 对齐 MarkdownTextBox.ImageTargetWidth:host 可视宽度减内边距,下限 80,无法获取时兜底 240。
        double ComputeFullRenderTargetWidth(FrameworkElement host)
        {
            var w = host.ActualWidth
                - NoteTypography.ContentPadding.Left
                - NoteTypography.ContentPadding.Right
                - 4;
            if (double.IsNaN(w) || double.IsInfinity(w) || w < 80)
            {
                return 240;
            }
            return Math.Max(80, w);
        }

        void OnFullRenderPanelScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // 动画中或非 Full 模式时跳过,避免与其它重建路径相互干扰。
            if (fullRenderPanel == null ||
                _fullRenderImageStore == null ||
                !isPreviewing ||
                isRenderModeTransitioning)
            {
                return;
            }
            RefreshFullRenderPanelViewportProtection();
        }

        void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 只在 full render preview 状态下关心宽度变化(忽略单纯高度变化,
            // 纸片高度变化不直接影响图片宽度)
            if (!e.WidthChanged) return;
            if (!isPreviewing ||
                fullRenderPanel == null ||
                isRenderModeTransitioning)
            {
                return;
            }

            // 复用 needsRebuild 的阈值,避免无意义的就地更新。
            var newTargetWidth = ComputeFullRenderTargetWidth(host);
            if (Math.Abs(_lastRenderedTargetWidthDip - newTargetWidth)
                <= FullRenderPanelResizeThresholdDip)
            {
                return;
            }

            // 拖动期间就地更新,BitmapScalingMode 临时 LowQuality;settle 后由
            // CompleteFullRenderResizePreview 升级回 HighQuality。不重建 panel,
            // 不重解码 BitmapImage,所以拖动期间无延迟、无闪烁。
            UpdateFullRenderPanelImageWidthsInPlace(newTargetWidth);

            if (_fullRenderPanelResizeSettleTimer == null)
            {
                _fullRenderPanelResizeSettleTimer = new DispatcherTimer(
                    DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(FullRenderPanelResizeSettleMs)
                };
                _fullRenderPanelResizeSettleTimer.Tick += (_, _) =>
                {
                    _fullRenderPanelResizeSettleTimer!.Stop();
                    // 终结时再校验一次:拖动期间可能已失焦/折叠/关闭。
                    if (!isPreviewing ||
                        fullRenderPanel == null ||
                        isRenderModeTransitioning)
                    {
                        return;
                    }
                    CompleteFullRenderResizePreview();
                };
            }
            else
            {
                _fullRenderPanelResizeSettleTimer.Stop();
            }
            _fullRenderPanelResizeSettleTimer.Start();
        }

        // 对应编辑模式 MarkdownTextBox.ImageBlockReuse.TryUpdateReusableImageBlock:
        // 就地更新 panel 内图片的 Width/Height,不重建 panel、不重新解码 BitmapImage。
        void UpdateFullRenderPanelImageWidthsInPlace(double newTargetWidth)
        {
            if (fullRenderPanel == null) return;
            _isFullRenderResizePreview = true;
            if (fullRenderPanel.Content is Panel content)
            {
                ApplyWidthsToPanel(content, newTargetWidth);
            }
            _lastRenderedTargetWidthDip = newTargetWidth;
        }

        // 递归遍历 panel 子树,就地更新所有 FullRenderImageBlockTag 的 Border/Image。
        // 对应 MarkdownTextBox.ImageResize.ApplyImageViewportPreviewLayout。
        void ApplyWidthsToPanel(DependencyObject node, double targetWidth)
        {
            int count;
            try
            {
                count = VisualTreeHelper.GetChildrenCount(node);
            }
            catch (InvalidOperationException)
            {
                return;
            }
            for (var i = 0; i < count; i++)
            {
                DependencyObject child;
                try
                {
                    child = VisualTreeHelper.GetChild(node, i);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
                if (child is Border { Tag: FullRenderImageBlockTag tag } border)
                {
                    border.Width = targetWidth;
                    var displayWidth = MarkdownTextBox.ResolveImageDisplayWidth(
                        tag.DisplayOptions,
                        tag.NaturalWidth,
                        targetWidth);
                    if (border.Child is System.Windows.Controls.Image img)
                    {
                        img.Width = displayWidth;
                        // 用现有 bitmap 的宽高比就地重算 Height。
                        if (img.Source is BitmapSource bitmap && bitmap.PixelWidth > 0)
                        {
                            img.Height = Math.Round(
                                displayWidth * (double)bitmap.PixelHeight / bitmap.PixelWidth,
                                1);
                        }
                        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.LowQuality);
                    }
                }
                ApplyWidthsToPanel(child, targetWidth);
            }
        }

        // settle timer 触发:把所有图片 BitmapScalingMode 改回 HighQuality。
        // 对应编辑模式 CompleteImageResizePreview。bitmap 重解码路径先不上,
        // 后续如在低 DPI 下明显模糊再加 _imageStore.GetBitmapSource(allowDecodeUpgrade: true)。
        void CompleteFullRenderResizePreview()
        {
            if (!_isFullRenderResizePreview) return;
            _isFullRenderResizePreview = false;
            if (fullRenderPanel == null) return;
            if (fullRenderPanel.Content is Panel content)
            {
                UpgradeBitmapQualityInPanel(content);
            }
        }

        void UpgradeBitmapQualityInPanel(DependencyObject node)
        {
            int count;
            try
            {
                count = VisualTreeHelper.GetChildrenCount(node);
            }
            catch (InvalidOperationException)
            {
                return;
            }
            for (var i = 0; i < count; i++)
            {
                DependencyObject child;
                try
                {
                    child = VisualTreeHelper.GetChild(node, i);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
                if (child is Border { Child: System.Windows.Controls.Image img })
                {
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                }
                UpgradeBitmapQualityInPanel(child);
            }
        }

        void OnFullRenderPanelUnloaded(object sender, RoutedEventArgs e)
        {
            // panel 真正销毁时:清掉视口保护并把 Image.Source 置 null,让 GC 立刻回收 BitmapImage。
            // 必须用 sender 而非闭包变量 fullRenderPanel——WPF 的 Unloaded 由 BroadcastEventHelper
            // 异步派发,闭包变量此时可能已被 ShowPreview/Rebuild 指向新建的 panel,误清新 panel。
            //
            // deep capsule 折叠/展开触发的 Window.Hide/Window.Show,以及普通 capsule 折叠
            // 期间 _shell.Visibility 反复切换,都会让 panel 在仍挂在 host.Children 时被
            // 异步派发 Unloaded。这种 Unloaded 是临时性的,清掉图片会让展开后图片永久空白,
            // 直到编辑模式修改尺寸强制 needsRebuild 才能恢复。这里用"panel 不再挂在 host"判断
            // 真销毁;折叠期临时 Unloaded 跳过清理,展开后 WPF 自动重新展现。
            var actualPanel = sender as DependencyObject;
            if (actualPanel == null)
            {
                return;
            }
            if (_suppressFullRenderPanelUnloadCleanup)
            {
                return;
            }

            // 真销毁检测: panel 不再挂在 host.Children 时说明是显式 Remove 的销毁。
            var panelStillAttached = false;
            if (actualPanel is UIElement asElement)
            {
                try
                {
                    if (host.Children.Contains(asElement))
                    {
                        panelStillAttached = true;
                    }
                }
                catch (InvalidOperationException)
                {
                    panelStillAttached = false;
                }
            }
            if (panelStillAttached)
            {
                return;
            }

            DisposeFullRenderPanelImages(actualPanel);

            // 视口保护集合:只在 sender 确实是当前 fullRenderPanel 时清,
            // 防止已被替换/重建时误清新 panel 的图。
            if (_fullRenderImageStore != null &&
                ReferenceEquals(actualPanel, fullRenderPanel))
            {
                _fullRenderImageStore.SetViewportProtectedBitmapIds(
                    $"{_paper.Id}{FullRenderViewportOwnerSuffix}",
                    Array.Empty<string>());
            }
        }

        void RefreshFullRenderPanelViewportProtection()
        {
            if (fullRenderPanel == null || _fullRenderImageStore == null)
            {
                return;
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            CollectVisibleImageIds(fullRenderPanel, ids);
            _fullRenderImageStore.SetViewportProtectedBitmapIds(
                $"{_paper.Id}{FullRenderViewportOwnerSuffix}",
                ids);
        }

        // 递归扫描 panel 子树,收集所有合法图片 ID。
        // 首版采用 panel 整树保护:覆盖所有滚动场景,且不会超过 MaxBitmapCacheEntries。
        static void CollectVisibleImageIds(
            DependencyObject node,
            ISet<string> imageIds)
        {
            if (node is FrameworkElement fe &&
                fe.Tag is FullRenderImageBlockTag tag &&
                MarkdownImageReferences.IsValidImageId(tag.ImageId))
            {
                imageIds.Add(tag.ImageId);
            }
            int count;
            try
            {
                count = VisualTreeHelper.GetChildrenCount(node);
            }
            catch (InvalidOperationException)
            {
                return;
            }
            for (var i = 0; i < count; i++)
            {
                DependencyObject child;
                try
                {
                    child = VisualTreeHelper.GetChild(node, i);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
                CollectVisibleImageIds(child, imageIds);
            }
        }

        // 显式清空所有 Image.Source 让 BitmapImage 不再被 WPF 端 ImageBrush 强引用。
        // NoteImageStore 的 _bitmapCache 字典本身保留(下次解码可复用)。
        static void DisposeFullRenderPanelImages(DependencyObject? root)
        {
            if (root == null)
            {
                return;
            }
            if (root is System.Windows.Controls.Image image)
            {
                image.Source = null;
            }
            int count;
            try
            {
                count = VisualTreeHelper.GetChildrenCount(root);
            }
            catch (InvalidOperationException)
            {
                return;
            }
            for (var i = 0; i < count; i++)
            {
                DependencyObject child;
                try
                {
                    child = VisualTreeHelper.GetChild(root, i);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
                DisposeFullRenderPanelImages(child);
            }
        }

        // Ctrl+滚轮缩放时原地同步重建 fullRenderPanel,不触发 fade 动画;
        // 替代走 RebuildNoteBodyForMarkdownMode 会引起 box↔panel 闪烁的旧路径。
        void RebuildFullRenderPanelForZoom()
        {
            if (!IsCurrentPresenter()) return;             // presenter 被 RebuildNoteBodyForMarkdownMode 取代
            if (fullRenderPanel == null) return;          // Enhanced 模式 / 未进入预览
            if (!isPreviewing) return;                    // 防御:Full 模式但未预览
            if (isRenderModeTransitioning) return;        // ShowPreview/ShowEditor 淡入淡出中

            // 显式解绑 Unloaded handler,避免 WPF 异步派发的旧 panel Unloaded 误清新 panel。
            fullRenderPanel.Unloaded -= OnFullRenderPanelUnloaded;
            host.Children.Remove(fullRenderPanel);
            // 缩放一定改变 zoom + targetWidth,显式清图片让 GC 回收。
            DisposeFullRenderPanelImages(fullRenderPanel);
            fullRenderPanel = CreateFullRenderPanel(
                box.PersistentText,
                _controller.ImageStore,
                _paper.Id);
            host.Children.Add(fullRenderPanel);
            fullRenderPanel.Visibility = Visibility.Visible;
            fullRenderPanel.IsHitTestVisible = true;
            fullRenderPanel.Opacity = 1;
            SyncEditorScrollToRenderPanel();
        }
        _rebuildFullRenderPanelForZoom = RebuildFullRenderPanelForZoom;

        void ShowEditorAtPreviewPoint(
            Point previewPoint,
            DependencyObject? originalSource = null,
            bool selectImage = true)
        {
            if (!IsCurrentPresenter())
            {
                return;
            }

            var entryGeneration = ++editorEntryGeneration;
            TraceNoteRender($"ShowEditorAtPreviewPoint x={previewPoint.X:F1} y={previewPoint.Y:F1}");
            var hasImageSelection = false;
            var imageReferenceOffset = 0;
            var imageId = "";
            var hasImageCaret = false;
            var caretIndex = 0;
            if (selectImage)
            {
                hasImageSelection = box.TryGetImageReferenceFromSource(
                    originalSource,
                    out imageReferenceOffset,
                    out imageId);
                if (!hasImageSelection)
                {
                    hasImageSelection = box.TryGetImageReferenceFromPoint(
                        previewPoint,
                        out imageReferenceOffset,
                        out imageId);
                }
            }
            else
            {
                hasImageCaret = box.TryGetImageCaretFromSource(originalSource, out caretIndex);
                if (!hasImageCaret)
                {
                    hasImageCaret = box.TryGetImageCaretFromPoint(previewPoint, out caretIndex);
                }
            }
            var hasPreviewPosition = hasImageSelection || hasImageCaret;
            if (!hasPreviewPosition)
            {
                hasPreviewPosition = box.TryGetCharacterIndexFromPoint(previewPoint, out caretIndex);
            }

            isEnteringEditorFromPreview = true;
            ShowEditor(focus: false);

            if (!box.IsKeyboardFocusWithin)
            {
                box.Focus();
            }

            if (hasImageSelection)
            {
                MarkImageInteraction();
                pendingImageReferenceOffset = imageReferenceOffset;
                pendingImageId = imageId;
                box.SelectImageReference(imageReferenceOffset, imageId);
            }
            else if (hasImageCaret)
            {
                box.ClearImageSelection();
                box.PlaceCaretAfterImage(caretIndex);
            }
            else if (hasPreviewPosition)
            {
                box.ClearImageSelection();
                box.CaretIndex = Math.Clamp(caretIndex, 0, box.Text.Length);
                box.SelectionLength = 0;
            }
            TraceNoteRender($"ShowEditorAtPreviewPoint after hasPosition={hasPreviewPosition} caret={box.CaretIndex}");
            var deferredWorkGeneration = _noteDeferredWorkGeneration;
            Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    if (!IsCurrentNoteDeferredWork(
                            presenterGeneration,
                            deferredWorkGeneration,
                            box) ||
                        entryGeneration != editorEntryGeneration)
                    {
                        return;
                    }

                    isEnteringEditorFromPreview = false;
                    TraceNoteRender($"ShowEditorAtPreviewPoint release focused={box.IsKeyboardFocusWithin} isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
                }),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        void MarkImageInteraction()
        {
            imageInteractionGeneration++;
            isInteractingWithImage = true;
        }

        void FinishImageInteraction(int referenceOffset, string imageId)
        {
            if (!IsCurrentPresenter())
            {
                return;
            }

            if (isPreviewing)
            {
                ShowEditor(focus: false);
            }

            if (!box.IsKeyboardFocusWithin)
            {
                box.Focus();
            }
            box.SelectImageReference(referenceOffset, imageId);

            var finishingInteractionGeneration = imageInteractionGeneration;
            var deferredWorkGeneration = _noteDeferredWorkGeneration;
            Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    if (!IsCurrentNoteDeferredWork(
                            presenterGeneration,
                            deferredWorkGeneration,
                            box) ||
                        finishingInteractionGeneration != imageInteractionGeneration)
                    {
                        return;
                    }

                    if (isPreviewing)
                    {
                        ShowEditor(focus: false);
                    }
                    if (!box.IsKeyboardFocusWithin)
                    {
                        box.Focus();
                    }

                    box.SelectImageReference(referenceOffset, imageId);
                    pendingImageReferenceOffset = null;
                    pendingImageId = null;
                    isInteractingWithImage = false;
                }),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        bool TrySelectImage(Point point, DependencyObject? originalSource)
        {
            if (!IsCurrentPresenter())
            {
                return false;
            }

            var hasImageReference = box.TryGetImageReferenceFromSource(
                originalSource,
                out var referenceOffset,
                out var imageId);
            if (!hasImageReference)
            {
                hasImageReference = box.TryGetImageReferenceFromPoint(
                    point,
                    out referenceOffset,
                    out imageId);
            }

            if (!hasImageReference)
            {
                return false;
            }

            MarkImageInteraction();
            pendingImageReferenceOffset = referenceOffset;
            pendingImageId = imageId;
            if (!box.IsKeyboardFocusWithin)
            {
                box.Focus();
            }
            box.SelectImageReference(referenceOffset, imageId);
            return true;
        }

        bool TryPlaceCaretOnImageForDrop(Point point, DependencyObject? originalSource)
        {
            if (!IsCurrentPresenter())
            {
                return false;
            }

            var hasImageCaret = box.TryGetImageCaretFromSource(originalSource, out var caretIndex);
            if (!hasImageCaret)
            {
                hasImageCaret = box.TryGetImageCaretFromPoint(point, out caretIndex);
            }

            if (!hasImageCaret)
            {
                return false;
            }

            if (!box.IsKeyboardFocusWithin)
            {
                box.Focus();
            }
            box.ClearImageSelection();
            box.PlaceCaretAfterImage(caretIndex);

            return true;
        }

        box.AllowDrop = true;
        box.PreviewDragOver += (_, e) =>
        {
            if (!box.CanInsertImagesFromDataObject(e.Data))
            {
                return;
            }

            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        };
        box.PreviewDrop += (_, e) =>
        {
            if (!box.CanInsertImagesFromDataObject(e.Data))
            {
                if (!box.ValidateTextDrop(e.Data))
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                }
                return;
            }

            var point = e.GetPosition(box);
            if (isPreviewing)
            {
                ShowEditorAtPreviewPoint(
                    point,
                    e.OriginalSource as DependencyObject,
                    selectImage: false);
            }
            else if (!TryPlaceCaretOnImageForDrop(point, e.OriginalSource as DependencyObject) &&
                     box.TryGetCharacterIndexFromPoint(point, out var dropCaret))
            {
                box.CaretIndex = dropCaret;
                box.Select(dropCaret, 0);
            }

            box.TryInsertImagesFromDataObject(e.Data);
            e.Handled = true;
        };

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
            if (_applyingExternalNoteChange)
            {
                return;
            }

            _noteContentDirty = true;
            InvalidateEdgeCapsulePreviewContent();
            var wasScriptCapsule = _liveIsScriptCapsule;
            var isScriptCapsule = IsScriptCapsuleDocument(box);
            _liveIsScriptCapsule = isScriptCapsule;
            if (wasScriptCapsule != isScriptCapsule)
            {
                RefreshCapsuleLabel();
                RefreshPaperContextMenus();
                _controller.RefreshTodoRowsForLinkedPaper(_paper.Id);
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
            if (box.IsImageContextMenuOpen)
            {
                TraceNoteRender("LostKeyboardFocus ignored: image context menu open");
                return;
            }
            if (isEnteringEditorFromPreview)
            {
                TraceNoteRender($"LostKeyboardFocus ignored: entering editor isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
                return;
            }
            if (isOpeningImagePicker)
            {
                TraceNoteRender("LostKeyboardFocus ignored: image picker open");
                return;
            }
            if (isInteractingWithImage)
            {
                TraceNoteRender($"LostKeyboardFocus ignored: image interaction isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
                return;
            }
            TraceNoteRender($"LostKeyboardFocus isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
            ShowPreview();
        };

        box.ImageContextMenuClosed += () =>
        {
            // The image menu steals keyboard focus while open. WPF restores focus
            // asynchronously after Closed, so defer the decision: if focus hasn't
            // come back but the window is still active (menu item clicked / Esc),
            // hand focus back to the editor; only fall back to preview when the
            // user actually left the window.
            var deferredWorkGeneration = _noteDeferredWorkGeneration;
            Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    if (!IsCurrentNoteDeferredWork(
                            presenterGeneration,
                            deferredWorkGeneration,
                            box))
                    {
                        return;
                    }

                    if (isPreviewing || box.IsKeyboardFocusWithin)
                    {
                        return;
                    }
                    if (IsActive)
                    {
                        TraceNoteRender("ImageContextMenuClosed: refocus editor");
                        box.Focus();
                    }
                    else
                    {
                        TraceNoteRender("ImageContextMenuClosed: window inactive -> ShowPreview");
                        ShowPreview();
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Background);
        };

        MouseButtonEventHandler noteMouseDown = (_, e) =>
        {
            if (IsScrollBarInteractionSource(e.OriginalSource as DependencyObject, box))
            {
                TraceNoteRender($"PreviewMouseLeftButtonDown ignored: scrollbar isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
                return;
            }

            var textViewPoint = e.GetPosition(box.TextArea.TextView);
            var point = e.GetPosition(box);
            var originalSource = e.OriginalSource as DependencyObject;
            imageInteractionGeneration++;
            pendingImageReferenceOffset = null;
            pendingImageId = null;
            isInteractingWithImage = false;
            TraceNoteRender($"PreviewMouseLeftButtonDown isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode} handled={e.Handled}");
            if (!isPreviewing)
            {
                if (TrySelectImage(point, originalSource))
                {
                    e.Handled = true;
                    return;
                }

                box.ClearImageSelection();
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                    box.TryGetOpenableLinkFromTextViewPoint(textViewPoint, out var editUrl))
                {
                    OpenMarkdownLink(editUrl);
                    e.Handled = true;
                }
                return;
            }

            if (box.TryGetOpenableLinkFromTextViewPoint(textViewPoint, out var url))
            {
                OpenMarkdownLink(url);
                e.Handled = true;
                return;
            }

            ShowEditorAtPreviewPoint(point, originalSource);
            e.Handled = true;
        };
        box.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, noteMouseDown, true);
        box.AddHandler(
            UIElement.MouseLeftButtonUpEvent,
            new MouseButtonEventHandler((_, e) =>
            {
                if (!pendingImageReferenceOffset.HasValue ||
                    string.IsNullOrWhiteSpace(pendingImageId))
                {
                    return;
                }

                FinishImageInteraction(
                    pendingImageReferenceOffset.Value,
                    pendingImageId);
                e.Handled = true;
            }),
            true);

        box.MouseMove += (sender, e) =>
        {
            if (!isPreviewing &&
                (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
            {
                box.SetInteractionCursor(Cursors.IBeam);
                return;
            }

            var isOverLink = box.TryGetOpenableLinkFromTextViewPointFast(
                e.GetPosition(box.TextArea.TextView),
                out _);
            if (isPreviewing)
            {
                box.SetInteractionCursor(isOverLink ? Cursors.Hand : Cursors.Arrow);
            }
            else
            {
                box.SetInteractionCursor(isOverLink ? Cursors.Hand : Cursors.IBeam);
            }
        };

        box.MouseLeave += (_, _) =>
        {
            box.SetInteractionCursor(isPreviewing ? Cursors.Arrow : Cursors.IBeam);
        };

        editorMenu.Closed += (_, _) =>
        {
            if (!isOpeningImagePicker &&
                !isPreviewing &&
                !box.IsFocused &&
                !box.IsKeyboardFocusWithin)
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

    private string[] SelectImagesFromFilePicker()
    {
        var dialog = new OpenFileDialog
        {
            Filter = Strings.Get("ImageFileDialogFilter"),
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return Array.Empty<string>();
        }

        return dialog.FileNames;
    }

    private void InsertImageFiles(MarkdownTextBox box, IEnumerable<string> paths)
    {
        try
        {
            box.InsertImagesFromFiles(paths);
        }
        catch (Exception ex)
        {
            ShowNoteImageImportFailure(ex);
        }
    }

    private void ShowNoteImageImportFailure(Exception ex)
    {
        PaperNoticeDialog.Show(
            this,
            Strings.Get("ImageImportFailureTitle"),
            Strings.Format("ImageImportFailureMessage", ex.Message));
    }

    private void ShowNotePasteRejected()
    {
        PaperNoticeDialog.Show(
            this,
            Strings.Get("NotePasteRejectedTitle"),
            Strings.Get("NotePasteRejectedMessage"));
    }


    public void UpdateTextZoom()
    {
        if (_paper.Type != PaperTypes.Note ||
            !BodySupports(PaperBodyCapabilities.TextZoom))
        {
            return;
        }

        var zoom = CurrentTextZoom();
        if (_noteBox != null)
        {
            // zoom 是字号变化,不是渲染模式变化。走原地路径:
            // SetTextZoom 改 box.FontSize 不重建,_rebuildFullRenderPanelForZoom
            // 同步替换 fullRenderPanel child,无 fade 动画。
            _noteBox.SetTextZoom(zoom);
            _rebuildFullRenderPanelForZoom?.Invoke();
        }
        else
        {
            NotifyCurrentPaperBodyTypographyChanged();
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
        if (_paper.Type != PaperTypes.Note ||
            !BodySupports(PaperBodyCapabilities.TextZoom))
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
        if (_paper.Type != PaperTypes.Note ||
            !IsCurrentBodyProviderMarkdown)
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
            _openMarkdownButton.Visibility =
                _controller.State.ShowTopBarExternalOpenButton &&
                IsCurrentBodyProviderMarkdown
                    ? Visibility.Visible
                    : Visibility.Collapsed;
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
        CommitPendingNoteContent();
        var directory = Path.Combine(Path.GetTempPath(), "PaperTodo");
        Directory.CreateDirectory(directory);

        var fileStem = ExternalMarkdownFileStem();
        var path = Path.Combine(directory, fileStem + CurrentExternalMarkdownExtension());
        var text = _paper.Content ?? "";
        text = _controller.ImageStore.ConvertMarkdownForExternalEditor(
            _paper.Id,
            text,
            Path.Combine(directory, fileStem + "-images"),
            directory);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private string ExternalMarkdownFileStem()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(_paper.Id ?? ""));
        return "paper-" + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
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
        return IsScriptCapsule() ? AppTypography.Scale(15) : CapsuleIconFontSize;
    }

    private bool IsScriptCapsule()
    {
        return IsCurrentScriptCapsule();
    }

    internal bool IsCurrentScriptCapsule()
    {
        if (_paper.Type != PaperTypes.Note ||
            !IsCurrentBodyProviderMarkdown)
        {
            return false;
        }

        return _noteBox != null
            ? _liveIsScriptCapsule
            : IsScriptCapsuleContent(_paper.Content);
    }

    internal bool IsCurrentNoteEmpty()
    {
        if (_paper.Type != PaperTypes.Note ||
            !IsCurrentBodyProviderMarkdown)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(_noteBox?.PersistentText ?? _paper.Content);
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

        SetCollapsedState(false, activateOnExpand: true);
    }

    private void OpenCapsuleForEditing()
    {
        if (_paper.IsCollapsed)
        {
            if (HasDeepCapsuleSlotPlacement)
            {
                ShowMainWindowForDeepCapsuleActivation();
                SetCollapsedState(false, alignExpandedToDockedEdge: true, activateOnExpand: true);
            }
            else
            {
                SetCollapsedState(false, activateOnExpand: true);
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
        if (_paper.Type != PaperTypes.Note ||
            !IsCurrentBodyProviderMarkdown)
        {
            return false;
        }

        CommitPendingNoteContent();
        var text = _paper.Content ?? "";
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

    private static bool IsScriptCapsuleDocument(MarkdownTextBox box)
    {
        if (box.Document.TextLength <= 0)
        {
            return false;
        }

        var firstLine = box.Document.GetLineByNumber(1);
        return TryParseScriptCapsuleMarker(box.Document.GetText(firstLine), out _);
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
        var executionId = Guid.NewGuid();
        var registeredProcess = false;
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

            lock (ActiveScriptProcessLock)
            {
                ActiveScriptProcesses[executionId] = process;
                registeredProcess = true;
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
            if (registeredProcess)
            {
                lock (ActiveScriptProcessLock)
                {
                    ActiveScriptProcesses.Remove(executionId);
                }
            }

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
                var ownsProcess = false;
                lock (PersistentScriptProcessLock)
                {
                    if (PersistentScriptProcesses.TryGetValue(key, out var current) && ReferenceEquals(current, process))
                    {
                        PersistentScriptProcesses.Remove(key);
                        ownsProcess = true;
                    }
                }

                if (ownsProcess)
                {
                    process.Dispose();
                }
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
        if (_windowLifecycle != PaperWindowLifecycleState.Alive || IsClosed || !_paper.IsVisible)
        {
            return;
        }

        MessageBox.Show(
            message,
            Strings.Get("ScriptCapsuleFailureTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }


    internal static void StopPersistentScriptProcesses()
    {
        List<Process> processes;
        lock (PersistentScriptProcessLock)
        {
            processes = PersistentScriptProcesses.Values.ToList();
            PersistentScriptProcesses.Clear();
        }

        foreach (var process in processes)
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
    }

    internal static void StopAllScriptProcesses()
    {
        StopPersistentScriptProcesses();

        List<Process> activeProcesses;
        lock (ActiveScriptProcessLock)
        {
            activeProcesses = ActiveScriptProcesses.Values.Distinct().ToList();
            ActiveScriptProcesses.Clear();
        }

        foreach (var process in activeProcesses)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1000);
                }
            }
            catch
            {
                // The execution task owns disposal and temporary-file cleanup in its finally.
            }
        }
    }
}