using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PaperTodo;

/// <summary>
/// data.json 冲突弹窗:展示单个冲突的当前值和外部值,让用户二选一。
/// 默认(关闭/X/Esc)=KeepMemory;用户点 "Keep external" 才会覆盖 memory。
/// 展示框可滚动;冲突行用浅红/浅绿背景高亮(只在一侧存在的行)。
/// </summary>
internal static class DataReloadConflictDialog
{
    /// <summary>
    /// 弹一个 modal dialog,返回用户选择。
    /// </summary>
    public static ConflictChoice Show(Window? owner, StateMergeConflict conflict)
    {
        var dialog = new DataReloadConflictDialogWindow(conflict);
        if (owner != null && PresentationSource.FromVisual(owner) is HwndSource)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        var keepExternal = dialog.ShowDialog() == true;
        dialog.Close();
        return keepExternal ? ConflictChoice.KeepExternal : ConflictChoice.KeepMemory;
    }
}

/// <summary>
/// paper 级冲突弹窗:同一 paper 下的所有字段冲突一次展示,用户二选一整张 paper。
/// 默认(关闭/X/Esc)=KeepMemory;用户点 "全部保留传入" 才会用外部覆盖整张 paper。
/// </summary>
internal static class DataReloadPaperConflictDialog
{
    /// <summary>
    /// 弹一个 modal paper 级 dialog,返回用户选择。
    /// </summary>
    public static PaperConflictChoice Show(Window? owner, PaperBucket bucket)
    {
        var dialog = new DataReloadPaperConflictDialogWindow(bucket);
        if (owner != null && PresentationSource.FromVisual(owner) is HwndSource)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        var keepExternal = dialog.ShowDialog() == true;
        dialog.Close();
        return keepExternal ? PaperConflictChoice.KeepExternal : PaperConflictChoice.KeepMemory;
    }
}

internal sealed class DataReloadConflictDialogWindow : Window
{
    public DataReloadConflictDialogWindow(StateMergeConflict conflict)
    {
        Title = Strings.Get("DataReloadConflictTitle");
        Width = 800;
        Height = 600;
        MinWidth = 560;
        MinHeight = 380;
        SizeToContent = SizeToContent.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        FontFamily = AppTypography.UiFontFamily;
        FontSize = AppTypography.Scale(12);
        Language = AppTypography.Language;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        AppTypography.ApplyTextRendering(this);

        var root = new Border
        {
            Margin = new Thickness(10),
            Padding = new Thickness(18, 16, 18, 16),
            Background = Theme.PaperBrush,
            BorderBrush = Theme.PaperBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Effect = new DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 3,
                Opacity = Theme.IsDark ? 0.34 : 0.2
            }
        };

        var content = new DockPanel { LastChildFill = true };

        // 顶部:标题 + 关闭按钮
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleText = new TextBlock
        {
            Text = Strings.Get("DataReloadConflictTitle"),
            Foreground = Theme.TextBrush,
            FontSize = AppTypography.Scale(14),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        };
        Grid.SetColumn(titleText, 0);
        headerGrid.Children.Add(titleText);

        var closeBtn = new Button
        {
            Content = "×",
            Width = 28,
            Height = 24,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Theme.TextBrush,
            BorderBrush = Brushes.Transparent,
            FontSize = AppTypography.Scale(16),
            IsCancel = true,
            Cursor = Cursors.Hand
        };
        closeBtn.Click += (_, _) => { DialogResult = false; };
        Grid.SetColumn(closeBtn, 1);
        headerGrid.Children.Add(closeBtn);
        DockPanel.SetDock(headerGrid, Dock.Top);
        content.Children.Add(headerGrid);

        // 路径
        var pathText = new TextBlock
        {
            Text = conflict.Reason == "unknown_baseline"
                ? Strings.Get("DataReloadMissingBaseline")
                : Strings.Format("DataReloadConflictPath", conflict.Path),
            Foreground = Theme.WeakTextBrush,
            FontSize = AppTypography.Scale(11),
            Margin = new Thickness(0, 8, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };
        DockPanel.SetDock(pathText, Dock.Top);
        content.Children.Add(pathText);

        // 底部:两个按钮
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var keepMemoryBtn = new Button
        {
            Content = Strings.Get("DataReloadConflictKeepMemory"),
            MinWidth = 140,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true,
            Cursor = Cursors.Hand
        };
        keepMemoryBtn.Click += (_, _) => { DialogResult = false; };
        buttonRow.Children.Add(keepMemoryBtn);

        var keepExternalBtn = new Button
        {
            Content = Strings.Get("DataReloadConflictKeepExternal"),
            MinWidth = 140,
            Padding = new Thickness(14, 6, 14, 6),
            IsDefault = true,
            Cursor = Cursors.Hand
        };
        keepExternalBtn.Click += (_, _) => { DialogResult = true; };
        buttonRow.Children.Add(keepExternalBtn);
        DockPanel.SetDock(buttonRow, Dock.Bottom);
        content.Children.Add(buttonRow);

        // 中间:两个并排的 diff 视图,共享 ScrollSyncGroup 实现双栏同步滚动
        var memoryText = DataReloadConflictUi.FormatConflictValue(conflict.PaperTodoExists, conflict.PaperTodo);
        var externalText = DataReloadConflictUi.FormatConflictValue(conflict.ExternalExists, conflict.External);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sync = new DataReloadConflictUi.ScrollSyncGroup();

        var memoryPanel = DataReloadConflictUi.BuildDiffPanel(
            Strings.Get("DataReloadConflictMemoryHeader"),
            memoryText,
            externalText,
            DataReloadConflictUi.LineMark.OnlyInMemory,
            sync);
        Grid.SetColumn(memoryPanel.root, 0);
        grid.Children.Add(memoryPanel.root);

        var externalPanel = DataReloadConflictUi.BuildDiffPanel(
            Strings.Get("DataReloadConflictExternalHeader"),
            externalText,
            memoryText,
            DataReloadConflictUi.LineMark.OnlyInExternal,
            sync);
        Grid.SetColumn(externalPanel.root, 2);
        grid.Children.Add(externalPanel.root);

        content.Children.Add(grid);

        root.Child = content;
        Content = root;
    }
}

/// <summary>
/// paper 级冲突弹窗:同一 paper 的所有 leaf 冲突一次性展示。
/// 顶部头部一行显示 paper ID 与类型,中间字段列表(每个有差异字段一对左右 diff 面板),
/// 底部"全部保留当前 / 全部保留传入"两个按钮二选一。
/// </summary>
internal sealed class DataReloadPaperConflictDialogWindow : Window
{
    public DataReloadPaperConflictDialogWindow(PaperBucket bucket)
    {
        Title = Strings.Get("DataReloadPaperConflictTitle");
        Width = 880;
        Height = 640;
        MinWidth = 620;
        MinHeight = 420;
        SizeToContent = SizeToContent.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        FontFamily = AppTypography.UiFontFamily;
        FontSize = AppTypography.Scale(12);
        Language = AppTypography.Language;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        AppTypography.ApplyTextRendering(this);

        var root = new Border
        {
            Margin = new Thickness(10),
            Padding = new Thickness(18, 16, 18, 16),
            Background = Theme.PaperBrush,
            BorderBrush = Theme.PaperBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Effect = new DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 3,
                Opacity = Theme.IsDark ? 0.34 : 0.2
            }
        };

        var content = new DockPanel { LastChildFill = true };

        // 顶部:标题 + 关闭按钮
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleText = new TextBlock
        {
            Text = Strings.Get("DataReloadPaperConflictTitle"),
            Foreground = Theme.TextBrush,
            FontSize = AppTypography.Scale(14),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        };
        Grid.SetColumn(titleText, 0);
        headerGrid.Children.Add(titleText);

        var closeBtn = new Button
        {
            Content = "×",
            Width = 28,
            Height = 24,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Theme.TextBrush,
            BorderBrush = Brushes.Transparent,
            FontSize = AppTypography.Scale(16),
            IsCancel = true,
            Cursor = Cursors.Hand
        };
        closeBtn.Click += (_, _) => { DialogResult = false; };
        Grid.SetColumn(closeBtn, 1);
        headerGrid.Children.Add(closeBtn);
        DockPanel.SetDock(headerGrid, Dock.Top);
        content.Children.Add(headerGrid);

        // Paper ID + 类型行
        var paperHeaderText = new TextBlock
        {
            Text = Strings.Format("DataReloadPaperConflictHeader", bucket.PaperId, bucket.PaperType),
            Foreground = Theme.WeakTextBrush,
            FontSize = AppTypography.Scale(11),
            Margin = new Thickness(0, 8, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };
        DockPanel.SetDock(paperHeaderText, Dock.Top);
        content.Children.Add(paperHeaderText);

        // 底部:两个按钮
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var keepMemoryBtn = new Button
        {
            Content = Strings.Get("DataReloadPaperConflictKeepAllMemory"),
            MinWidth = 160,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true,
            Cursor = Cursors.Hand
        };
        keepMemoryBtn.Click += (_, _) => { DialogResult = false; };
        buttonRow.Children.Add(keepMemoryBtn);

        var keepExternalBtn = new Button
        {
            Content = Strings.Get("DataReloadPaperConflictKeepAllExternal"),
            MinWidth = 160,
            Padding = new Thickness(14, 6, 14, 6),
            IsDefault = true,
            Cursor = Cursors.Hand
        };
        keepExternalBtn.Click += (_, _) => { DialogResult = true; };
        buttonRow.Children.Add(keepExternalBtn);
        DockPanel.SetDock(buttonRow, Dock.Bottom);
        content.Children.Add(buttonRow);

        // 中间:字段列表(每个有差异字段一组左右 diff 面板,所有面板共享一个 ScrollSyncGroup)
        var fieldsPanel = new StackPanel();
        var sync = new DataReloadConflictUi.ScrollSyncGroup();

        // 整张 paper 整节点冲突时,bucket.Conflicts 长度为 1 且 path 是 "papers[id=X]"
        // 仍然渲染为单个 "整张 paper" 字段块,内部直接对比两个完整 paper JsonObject
        // 普通多字段:按 conflict 顺序逐条渲染,字段短路径来自 conflict.Path 去掉前缀
        var prefix = $"papers[id={bucket.PaperId}].";
        foreach (var conflict in bucket.Conflicts)
        {
            var fieldLabel = StripPrefix(conflict.Path, prefix);
            var memoryText = DataReloadConflictUi.FormatConflictValue(conflict.PaperTodoExists, conflict.PaperTodo);
            var externalText = DataReloadConflictUi.FormatConflictValue(conflict.ExternalExists, conflict.External);

            var groupBlock = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

            // 字段名分组小标题
            var fieldHeader = new TextBlock
            {
                Text = Strings.Format("DataReloadPaperConflictField", fieldLabel),
                Foreground = Theme.TextBrush,
                FontSize = AppTypography.Scale(12),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            };
            groupBlock.Children.Add(fieldHeader);

            // 左右并排 diff 面板
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var memoryPanel = DataReloadConflictUi.BuildDiffPanel(
                Strings.Get("DataReloadConflictMemoryHeader"),
                memoryText,
                externalText,
                DataReloadConflictUi.LineMark.OnlyInMemory,
                sync);
            Grid.SetColumn(memoryPanel.root, 0);
            grid.Children.Add(memoryPanel.root);

            var externalPanel = DataReloadConflictUi.BuildDiffPanel(
                Strings.Get("DataReloadConflictExternalHeader"),
                externalText,
                memoryText,
                DataReloadConflictUi.LineMark.OnlyInExternal,
                sync);
            Grid.SetColumn(externalPanel.root, 2);
            grid.Children.Add(externalPanel.root);

            groupBlock.Children.Add(grid);
            fieldsPanel.Children.Add(groupBlock);
        }

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = fieldsPanel,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 0, 0, 4)
        };
        content.Children.Add(scrollViewer);

        root.Child = content;
        Content = root;
    }

    /// <summary>
    /// 把 conflict.Path 去掉 "papers[id=X]." 前缀,得到字段展示标签(title / items[id=Y].text / ...)。
    /// 如果 path 本身就是 "papers[id=X]"(整节点),则返回 "(整张 paper)";空前缀返回原 path。
    /// </summary>
    private static string StripPrefix(string path, string prefix)
    {
        if (string.Equals(path, prefix.TrimEnd('.'), StringComparison.Ordinal))
            return Strings.Get("DataReloadPaperConflictWholePaper");
        if (path.StartsWith(prefix, StringComparison.Ordinal))
            return path.Substring(prefix.Length);
        return path;
    }
}

/// <summary>
/// data.json 冲突弹窗共用的 UI 原语(单/双栏 diff 面板、行级同步滚动、JsonNode 文本化)。
/// DataReloadConflictDialog 与 DataReloadPaperConflictDialog 都从这里取构件。
/// </summary>
internal static class DataReloadConflictUi
{
    /// <summary>冲突行可视化标记。</summary>
    public enum LineMark { Common, OnlyInMemory, OnlyInExternal }

    /// <summary>
    /// 构造一个可滚动的 diff 面板:头部 + ScrollViewer + 行集合。
    /// 返回的 ScrollViewer 由 ScrollSyncGroup 协调两侧同步滚动。
    /// </summary>
    public static (Border root, ScrollViewer scrollViewer) BuildDiffPanel(
        string header,
        string thisSideText,
        string otherSideText,
        LineMark thisSideMark,
        ScrollSyncGroup sync)
    {
        // 头部(header) + 内容(scrollViewer),整体套一个 Border 作为面板外壳
        var stack = new DockPanel { LastChildFill = true };

        var headerBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)), // 浅灰头
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x42, 0x42, 0x42)), // 明显深色边框
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 6, 8, 6),
            Child = new TextBlock
            {
                Text = header,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextBrush,
                FontSize = AppTypography.Scale(12)
            }
        };
        DockPanel.SetDock(headerBorder, Dock.Top);
        stack.Children.Add(headerBorder);

        // 用 LineDiff 计算逐行差异
        var diff = LineDiff.DiffLines(otherSideText, thisSideText);

        // 高亮策略:左侧 4px 色条 + 微妙底色 + 加粗 + 彩色前缀,而不是整行深色背景,
        // 保证文本在浅底色上仍清晰可读。
        var stripeBrush = thisSideMark switch
        {
            LineMark.OnlyInMemory => new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),  // Material Red 600
            LineMark.OnlyInExternal => new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47)), // Material Green 600
            _ => Brushes.Transparent
        };
        var subtleBg = thisSideMark switch
        {
            LineMark.OnlyInMemory => new SolidColorBrush(Color.FromRgb(0xFD, 0xF2, 0xF2)),  // 极浅粉
            LineMark.OnlyInExternal => new SolidColorBrush(Color.FromRgb(0xF1, 0xF8, 0xE9)), // 极浅绿
            _ => Brushes.Transparent
        };
        var markerBrush = thisSideMark switch
        {
            LineMark.OnlyInMemory => new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)),  // Material Red 800
            LineMark.OnlyInExternal => new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)), // Material Green 800
            _ => Theme.TextBrush
        };

        var linesPanel = new StackPanel();
        if (diff.Count == 0)
        {
            linesPanel.Children.Add(new TextBlock
            {
                Text = "(empty)",
                Foreground = Theme.WeakTextBrush,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(4)
            });
        }
        else
        {
            foreach (var entry in diff)
            {
                var isThisSideOnly = entry.Kind != LineDiff.ChangeKind.Common;
                var prefix = isThisSideOnly ? "+ " : "  ";
                var fullText = prefix + entry.Text;

                // 单行容器:左侧 4px 色条 + 文本
                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var stripe = new Border
                {
                    Width = 4,
                    Background = isThisSideOnly ? stripeBrush : Brushes.Transparent
                };
                Grid.SetColumn(stripe, 0);
                rowGrid.Children.Add(stripe);

                var tb = new TextBlock
                {
                    Text = fullText,
                    FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                    FontSize = AppTypography.Scale(11.5),
                    Foreground = isThisSideOnly ? markerBrush : Theme.TextBrush,
                    Background = isThisSideOnly ? subtleBg : Brushes.Transparent,
                    Padding = new Thickness(6, 2, 6, 2),
                    TextWrapping = TextWrapping.NoWrap,
                    FontWeight = isThisSideOnly ? FontWeights.SemiBold : FontWeights.Normal,
                    ToolTip = isThisSideOnly
                        ? (thisSideMark == LineMark.OnlyInMemory
                            ? Strings.Get("DataReloadConflictMemoryOnlyTooltip")
                            : Strings.Get("DataReloadConflictExternalOnlyTooltip"))
                        : null
                };
                Grid.SetColumn(tb, 1);
                rowGrid.Children.Add(tb);

                linesPanel.Children.Add(rowGrid);
            }
        }

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 360,
            MinHeight = 160,
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = linesPanel,
            Background = Theme.PaperBrush,
            BorderThickness = new Thickness(0)
        };
        sync.Register(scrollViewer);
        stack.Children.Add(scrollViewer);

        var panelBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x42, 0x42, 0x42)), // 明显深色边框(VS Code style)
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Child = stack
        };
        return (panelBorder, scrollViewer);
    }

    /// <summary>
    /// 把一组 ScrollViewer 的滚动位置互相绑定:任一滚动都同步到其它。
    /// 类似 VS Code conflict editor 的双栏同步行为。
    /// </summary>
    public sealed class ScrollSyncGroup
    {
        private readonly List<ScrollViewer> _members = new();
        private bool _syncing;

        public void Register(ScrollViewer sv)
        {
            _members.Add(sv);
            sv.ScrollChanged += OnScrollChanged;
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_syncing) return;
            // 仅在用户实际滚动(VerticalChange 或 HorizontalChange 非零)时同步
            if (e.VerticalChange == 0 && e.HorizontalChange == 0) return;
            var src = (ScrollViewer)sender;
            _syncing = true;
            try
            {
                foreach (var sv in _members)
                {
                    if (sv == src) continue;
                    if (e.VerticalChange != 0)
                        sv.ScrollToVerticalOffset(Math.Min(src.VerticalOffset, sv.ScrollableHeight));
                    if (e.HorizontalChange != 0)
                        sv.ScrollToHorizontalOffset(Math.Min(src.HorizontalOffset, sv.ScrollableWidth));
                }
            }
            finally
            {
                _syncing = false;
            }
        }
    }

    /// <summary>
    /// 把 JsonNode 格式化为可读文本(不是原始 JSON 语法)。
    /// - JsonValue&lt;string&gt; → 字符串内容(不带引号、去掉转义)
    /// - JsonValue&lt;number/bool&gt; → 直接显示值
    /// - JsonObject → key=value 一行一对
    /// - JsonArray → 一行一项
    /// - null/missing → "(空)" / "(缺失)"
    /// </summary>
    public static string FormatConflictValue(bool exists, JsonNode? node)
    {
        if (!exists) return Strings.Get("DataReloadConflictValueMissing");
        if (node == null) return "(null)";
        var sb = new System.Text.StringBuilder();
        AppendFormatted(sb, node, indent: 0);
        return sb.ToString();
    }

    private static void AppendFormatted(StringBuilder sb, JsonNode? node, int indent)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) sb.Append(s);
            else sb.Append(v.ToString());
            return;
        }
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(kv.Key).Append('=');
                AppendFormattedValue(sb, kv.Value);
            }
            return;
        }
        if (node is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append('[').Append(i).Append("] ");
                AppendFormattedValue(sb, arr[i]);
            }
            return;
        }
        sb.Append(node?.ToString() ?? "(null)");
    }

    private static void AppendFormattedValue(StringBuilder sb, JsonNode? node)
    {
        if (node == null) { sb.Append("(null)"); return; }
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) sb.Append(s);
            else sb.Append(v.ToString());
            return;
        }
        // 嵌套 object/array:多行
        sb.Append('\n');
        AppendFormatted(sb, node, indent: 0);
    }
}

/// <summary>
/// 行级 LCS diff。返回一系列 Added / Removed / Common。
/// Removed 表示只在 old 里有,Added 表示只在 @new 里有,Common 表示两侧都有。
/// </summary>
internal static class LineDiff
{
    public enum ChangeKind { Common, Removed, Added }

    public readonly record struct Change(ChangeKind Kind, string Text);

    public static IReadOnlyList<Change> DiffLines(string oldText, string newText)
    {
        var oldLines = SplitLines(oldText ?? string.Empty);
        var newLines = SplitLines(newText ?? string.Empty);
        // Bound UI-thread LCS allocations for large JSON conflicts.
        if ((long)oldLines.Length * newLines.Length > 250_000)
            return DiffLarge(oldLines, newLines);
        var lcs = LongestCommonSubsequence(oldLines, newLines);
        return BuildChanges(oldLines, newLines, lcs);
    }

    private static IReadOnlyList<Change> DiffLarge(string[] oldLines, string[] newLines)
    {
        // Preserve identical prefix/suffix without allocating a quadratic DP matrix.
        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length &&
               string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal)) prefix++;
        var suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix &&
               string.Equals(oldLines[oldLines.Length - suffix - 1],
                   newLines[newLines.Length - suffix - 1], StringComparison.Ordinal)) suffix++;
        var result = new List<Change>(oldLines.Length + newLines.Length);
        for (var i = 0; i < prefix; i++) result.Add(new(ChangeKind.Common, oldLines[i]));
        for (var i = prefix; i < oldLines.Length - suffix; i++) result.Add(new(ChangeKind.Removed, oldLines[i]));
        for (var i = prefix; i < newLines.Length - suffix; i++) result.Add(new(ChangeKind.Added, newLines[i]));
        for (var i = oldLines.Length - suffix; i < oldLines.Length; i++) result.Add(new(ChangeKind.Common, oldLines[i]));
        return result;
    }

    private static string[] SplitLines(string text) =>
        text.Length == 0 ? Array.Empty<string>() : text.Split('\n');

    private static int[,] LongestCommonSubsequence(string[] a, string[] b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                dp[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }
        return dp;
    }

    private static List<Change> BuildChanges(string[] a, string[] b, int[,] lcs)
    {
        var result = new List<Change>(capacity: a.Length + b.Length);
        var i = 0; var j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (string.Equals(a[i], b[j], StringComparison.Ordinal))
            {
                result.Add(new Change(ChangeKind.Common, a[i]));
                i++; j++;
            }
            else if (lcs[i + 1, j] >= lcs[i, j + 1])
            {
                result.Add(new Change(ChangeKind.Removed, a[i]));
                i++;
            }
            else
            {
                result.Add(new Change(ChangeKind.Added, b[j]));
                j++;
            }
        }
        while (i < a.Length) { result.Add(new Change(ChangeKind.Removed, a[i++])); }
        while (j < b.Length) { result.Add(new Change(ChangeKind.Added, b[j++])); }
        return result;
    }
}