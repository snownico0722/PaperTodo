using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

public sealed class WorkbenchWindow : Window
{
    private readonly AppController _controller;
    private readonly ListBox _scenes = new();
    private readonly ListBox _papers = new();
    private readonly ListBox _relations = new();
    private readonly TextBlock _detail = new();
    private bool _refreshing;
    private string _view = "objects";

    public WorkbenchWindow(AppController controller)
    {
        _controller = controller;
        Title = Label("title", controller.State.UiLanguage);
        Width = 1080; Height = 720; MinWidth = 800; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#F4F1EA"); Foreground = Brush("#282720");
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        Content = Build();
        RefreshAll();
    }

    internal static string Label(string key, string? language)
    {
        var l = (language ?? "").ToLowerInvariant();
        var i = l.StartsWith("en") ? 1 : l.StartsWith("ja") ? 2 : l.StartsWith("ko") ? 3 : 0;
        string[] v = key switch
        {
            "title" => new[] { "桌面工作台", "Desktop Workbench", "デスクトップワークベンチ", "데스크톱 작업대" },
            "workbench" => new[] { "打开桌面工作台", "Open desktop workbench", "デスクトップワークベンチを開く", "데스크톱 작업대 열기" },
            "all" => new[] { "全部纸片", "All papers", "すべての紙片", "모든 페이퍼" },
            "scenes" => new[] { "场景", "Scenes", "シーン", "장면" },
            "new" => new[] { "新建场景", "New scene", "新しいシーン", "새 장면" },
            "add" => new[] { "加入场景", "Add to scene", "シーンに追加", "장면에 추가" },
            "objects" => new[] { "对象", "Objects", "オブジェクト", "객체" },
            "tasks" => new[] { "任务", "Tasks", "タスク", "작업" },
            "relations" => new[] { "关系", "Relations", "関係", "관계" },
            "relate" => new[] { "关联所选两张纸", "Relate selected papers", "選択した紙片を関連付け", "선택한 페이퍼 연결" },
            "remove" => new[] { "删除关系", "Remove relation", "関係を削除", "관계 삭제" },
            "open" => new[] { "打开原纸片", "Open original paper", "元の紙片を開く", "원본 페이퍼 열기" },
            "empty" => new[] { "选择纸片查看内容与关系。", "Select a paper to inspect it.", "紙片を選択してください。", "페이퍼를 선택하세요." },
            "name" => new[] { "输入场景名称", "Scene name", "シーン名", "장면 이름" },
            "selectTwo" => new[] { "请在列表中选择两张纸片。", "Select two papers in the list.", "紙片を2つ選択してください。", "페이퍼 두 개를 선택하세요." },
            _ => new[] { key, key, key, key }
        };
        return v[i];
    }

    private UIElement Build()
    {
        var root = new DockPanel { Margin = new Thickness(16) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var title = new TextBlock { Text = Title, FontSize = 23, FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(title, Dock.Left); head.Children.Add(title);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        ButtonTo(tabs, "objects", () => SetView("objects"));
        ButtonTo(tabs, "tasks", () => SetView("tasks"));
        ButtonTo(tabs, "relations", () => SetView("relations"));
        DockPanel.SetDock(tabs, Dock.Right); head.Children.Add(tabs);
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 230 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });

        var left = new DockPanel { Margin = new Thickness(0, 0, 10, 0) };
        Header(left, "scenes");
        _scenes.BorderThickness = new Thickness(0); _scenes.Background = Brushes.Transparent;
        _scenes.SelectionChanged += (_, _) => { if (!_refreshing) { _controller.State.WorkbenchActiveSceneId = (_scenes.SelectedItem as SceneChoice)?.Id ?? ""; _controller.SaveWorkbenchMetadata(); RefreshPapers(); } };
        left.Children.Add(_scenes);
        var sb = new StackPanel(); ButtonTo(sb, "new", CreateScene); ButtonTo(sb, "add", AddToScene);
        DockPanel.SetDock(sb, Dock.Bottom); left.Children.Add(sb); Grid.SetColumn(left, 0); grid.Children.Add(left);

        var center = new DockPanel { Margin = new Thickness(6, 0, 10, 0) };
        Header(center, "objects");
        _papers.SelectionMode = SelectionMode.Extended; _papers.BorderThickness = new Thickness(0); _papers.Background = Brushes.Transparent;
        _papers.SelectionChanged += (_, _) => RefreshDetail(); _papers.MouseDoubleClick += (_, _) => OpenSelected();
        center.Children.Add(_papers);
        var pb = new WrapPanel(); ButtonTo(pb, "open", OpenSelected); ButtonTo(pb, "relate", AddRelation);
        DockPanel.SetDock(pb, Dock.Bottom); center.Children.Add(pb); Grid.SetColumn(center, 1); grid.Children.Add(center);

        var right = new DockPanel { Margin = new Thickness(10, 0, 0, 0) };
        _detail.TextWrapping = TextWrapping.Wrap; _detail.Margin = new Thickness(0, 0, 0, 12);
        DockPanel.SetDock(_detail, Dock.Top); right.Children.Add(_detail);
        var rh = new TextBlock { Text = Label("relations", _controller.State.UiLanguage), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 6) };
        DockPanel.SetDock(rh, Dock.Top); right.Children.Add(rh);
        _relations.BorderThickness = new Thickness(0); _relations.Background = Brushes.Transparent; right.Children.Add(_relations);
        var rb = MakeButton(Label("remove", _controller.State.UiLanguage), RemoveRelation);
        DockPanel.SetDock(rb, Dock.Bottom); right.Children.Add(rb); Grid.SetColumn(right, 2); grid.Children.Add(right);
        root.Children.Add(grid);
        return root;
    }

    private void Header(DockPanel p, string key)
    {
        var t = new TextBlock { Text = Label(key, _controller.State.UiLanguage), FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 8) };
        DockPanel.SetDock(t, Dock.Top); p.Children.Add(t);
    }
    private void ButtonTo(Panel p, string key, Action action) => p.Children.Add(MakeButton(Label(key, _controller.State.UiLanguage), action));
    private Button MakeButton(string text, Action action)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 5, 5), MinHeight = 29, Background = Brush("#E7E1D6"), Foreground = Foreground, BorderBrush = Brush("#D1C9BC"), Cursor = Cursors.Hand };
        b.Click += (_, _) => action(); return b;
    }
    private void SetView(string view) { _view = view; RefreshPapers(); }
    private void RefreshAll()
    {
        _controller.State.WorkbenchScenes ??= new();
        _controller.State.WorkbenchRelations ??= new();
        _refreshing = true;
        _scenes.Items.Clear(); _scenes.Items.Add(new SceneChoice("", Label("all", _controller.State.UiLanguage)));
        foreach (var s in _controller.State.WorkbenchScenes) _scenes.Items.Add(new SceneChoice(s.Id, s.Name));
        _scenes.SelectedItem = _scenes.Items.Cast<SceneChoice>().FirstOrDefault(x => x.Id == _controller.State.WorkbenchActiveSceneId) ?? _scenes.Items[0];
        _refreshing = false; RefreshPapers();
    }
    private void RefreshPapers()
    {
        var keep = _papers.SelectedItems.Cast<PaperChoice>().Select(x => x.Paper.Id).ToHashSet();
        var id = (_scenes.SelectedItem as SceneChoice)?.Id ?? "";
        var scene = _controller.State.WorkbenchScenes?.FirstOrDefault(x => x.Id == id);
        var list = _controller.State.Papers.Where(p => scene == null || scene.PaperIds.Contains(p.Id));
        if (_view == "tasks") list = list.Where(p => p.Type == PaperTypes.Todo);
        if (_view == "relations") { var ids = (_controller.State.WorkbenchRelations ?? new()).SelectMany(r => new[] { r.SourcePaperId, r.TargetPaperId }).ToHashSet(); list = list.Where(p => ids.Contains(p.Id)); }
        _refreshing = true; _papers.Items.Clear();
        foreach (var p in list) { var x = new PaperChoice(p, $"{(p.Type == PaperTypes.Todo ? "☑" : "▤")}  {Name(p)}"); _papers.Items.Add(x); if (keep.Contains(p.Id)) _papers.SelectedItems.Add(x); }
        _refreshing = false; RefreshDetail();
    }
    private void RefreshDetail()
    {
        var p = (_papers.SelectedItem as PaperChoice)?.Paper;
        if (p == null) { _detail.Text = Label("empty", _controller.State.UiLanguage); _relations.Items.Clear(); return; }
        _detail.Text = Name(p) + Environment.NewLine + Environment.NewLine +
            (p.Type == PaperTypes.Todo ? $"{p.Items.Count(x => !x.Done)} / {p.Items.Count} 未完成事项" : (p.Content.Length > 400 ? p.Content[..400] + "…" : p.Content));
        _relations.Items.Clear();
        foreach (var r in (_controller.State.WorkbenchRelations ?? new()).Where(r => r.SourcePaperId == p.Id || r.TargetPaperId == p.Id))
        {
            var otherId = r.SourcePaperId == p.Id ? r.TargetPaperId : r.SourcePaperId;
            var other = _controller.State.Papers.FirstOrDefault(x => x.Id == otherId);
            if (other != null) _relations.Items.Add(new RelationChoice(r, $"{r.Label} · {Name(other)}"));
        }
    }
    private static string Name(PaperData p) => string.IsNullOrWhiteSpace(p.Title) ? (p.Type == PaperTypes.Todo ? "待办" : "笔记") : p.Title;
    private void CreateScene()
    {
        var box = new TextBox { Width = 260, Margin = new Thickness(12) };
        var dialog = new Window { Title = Label("new", _controller.State.UiLanguage), Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Background = Background };
        var form = new StackPanel();
        form.Children.Add(new TextBlock { Text = Label("name", _controller.State.UiLanguage), Margin = new Thickness(12, 12, 12, 0) });
        form.Children.Add(box);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 12, 12) };
        var create = MakeButton("OK", () => dialog.DialogResult = true);
        var cancel = MakeButton("Cancel", () => dialog.DialogResult = false);
        actions.Children.Add(create); actions.Children.Add(cancel); form.Children.Add(actions);
        dialog.Content = form;
        dialog.Loaded += (_, _) => box.Focus();
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(box.Text))
        {
            var s = new WorkbenchScene { Name = box.Text.Trim() };
            _controller.State.WorkbenchScenes ??= new(); _controller.State.WorkbenchScenes.Add(s);
            _controller.State.WorkbenchActiveSceneId = s.Id; _controller.SaveWorkbenchMetadata(); RefreshAll();
        }
    }
    private void AddToScene()
    {
        var s = _controller.State.WorkbenchScenes?.FirstOrDefault(x => x.Id == ((_scenes.SelectedItem as SceneChoice)?.Id ?? ""));
        if (s == null) return;
        foreach (var p in _papers.SelectedItems.Cast<PaperChoice>().Select(x => x.Paper)) if (!s.PaperIds.Contains(p.Id)) s.PaperIds.Add(p.Id);
        _controller.SaveWorkbenchMetadata(); RefreshAll();
    }
    private void AddRelation()
    {
        var ps = _papers.SelectedItems.Cast<PaperChoice>().Select(x => x.Paper).Take(2).ToList();
        if (ps.Count != 2) { MessageBox.Show(this, Label("selectTwo", _controller.State.UiLanguage), Title); return; }
        _controller.State.WorkbenchRelations ??= new();
        if (_controller.State.WorkbenchRelations.Any(r => (r.SourcePaperId == ps[0].Id && r.TargetPaperId == ps[1].Id) || (r.SourcePaperId == ps[1].Id && r.TargetPaperId == ps[0].Id))) return;
        _controller.State.WorkbenchRelations.Add(new WorkbenchRelation { SourcePaperId = ps[0].Id, TargetPaperId = ps[1].Id, Label = "related" });
        _controller.SaveWorkbenchMetadata(); RefreshPapers();
    }
    private void RemoveRelation()
    {
        if (_relations.SelectedItem is not RelationChoice choice) return;
        _controller.State.WorkbenchRelations?.RemoveAll(r => r.Id == choice.Relation.Id);
        _controller.SaveWorkbenchMetadata(); RefreshPapers();
    }
    private void OpenSelected()
    {
        if (_papers.SelectedItem is PaperChoice p) { _controller.ShowPaper(p.Paper); }
    }
    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    private sealed record SceneChoice(string Id, string Name) { public override string ToString() => Name; }
    private sealed record PaperChoice(PaperData Paper, string Display) { public override string ToString() => Display; }
    private sealed record RelationChoice(WorkbenchRelation Relation, string Display) { public override string ToString() => Display; }
}
