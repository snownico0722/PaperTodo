namespace PaperTodo;

internal static partial class UserInjectionText
{
    static partial void AddCurrentLayout(Dictionary<string, string[]> entries)
    {
        entries["note.editor.lineSpacingFactor.title"] = ["Note line spacing factor", "ノートの行間倍率", "노트 줄 간격 배율"];
        entries["note.editor.lineSpacingFactor.description"] =
            ["Override the line spacing factor selected for note editing and reading.", "編集・閲覧時の行間倍率を上書きします。", "편집과 읽기에서 사용하는 줄 간격 배율을 덮어씁니다."];
        entries["note.editor.lineSpacingFactor.defaultDescription"] =
            ["Computed from the current note line spacing preset", "現在のノート行間設定から計算", "현재 노트 줄 간격 설정으로 계산"];
        entries["todo.layout.itemVerticalMarginDip.title"] = ["Todo item vertical margin", "ToDo 項目の上下余白", "할 일 항목의 상하 여백"];
        entries["todo.layout.itemVerticalMarginDip.description"] =
            ["Override each vertical outer margin while keeping line layout within an item.", "項目内の行レイアウトを保ったまま上下の外側余白を上書きします。", "항목 내부의 줄 배치는 유지하고 위아래 바깥 여백을 덮어씁니다."];
        entries["todo.layout.itemVerticalMarginDip.defaultDescription"] =
            ["Computed from the current todo item spacing preset", "現在の ToDo 間隔設定から計算", "현재 할 일 간격 설정으로 계산"];
        entries["capsule.edge.iconOnlyMinWidthDip.title"] = ["Icon-only edge capsule minimum width", "アイコンのみの端カプセルの最小幅", "아이콘 전용 가장자리 캡슐 최소 너비"];
        entries["capsule.edge.iconOnlyMinWidthDip.description"] =
            ["Minimum built-in edge capsule width with zero title characters, including the inner shadow margin.", "タイトルがゼロ文字のときの最小幅です。内側の影の余白を含みます。", "제목이 0자일 때의 최소 너비로 내부 그림자 여백을 포함합니다."];
        entries["paper.bottomBar.horizontalInsetDip.title"] = ["Bottom bar side inset", "下部バーの左右余白", "하단 표시줄 좌우 여백"];
        entries["paper.bottomBar.horizontalInsetDip.description"] =
            ["Inset from each side of the paper to the plugin bottom bar.", "紙の左右端からプラグイン下部バーまでの距離です。", "종이 양쪽 가장자리에서 플러그인 하단 표시줄까지의 거리입니다."];
        entries["paper.bottomBar.bottomInsetDip.title"] = ["Bottom bar bottom inset", "下部バーの下端余白", "하단 표시줄 아래 여백"];
        entries["paper.bottomBar.bottomInsetDip.description"] =
            ["Inset from the bottom of the paper to the plugin bottom bar.", "紙の下端からプラグイン下部バーまでの距離です。", "종이 아래쪽에서 플러그인 하단 표시줄까지의 거리입니다."];
        entries["paper.bottomBar.itemGapDip.title"] = ["Bottom bar item gap", "下部バーの項目間隔", "하단 표시줄 항목 간격"];
        entries["paper.bottomBar.itemGapDip.description"] =
            ["Space between adjacent bottom bar items in the same group.", "同じグループ内の隣接する項目の間隔です。", "같은 그룹 안에 인접한 두 항목 사이의 간격입니다."];
        entries["paper.bottomBar.groupGapDip.title"] = ["Bottom bar group gap", "下部バーのグループ間隔", "하단 표시줄 그룹 간격"];
        entries["paper.bottomBar.groupGapDip.description"] =
            ["Minimum space between the left and right bottom bar groups.", "左右グループを分けて表示する場合の最小間隔です。", "왼쪽과 오른쪽 그룹을 나누어 표시할 때의 최소 간격입니다."];
        entries["paper.bottomBar.fontDip.title"] = ["Bottom bar base font size", "下部バーの基準文字サイズ", "하단 표시줄 기본 글자 크기"];
        entries["paper.bottomBar.fontDip.description"] =
            ["Base size of bottom bar text and icons before overall font scaling.", "全体のフォント倍率を適用する前の文字とアイコンの基準サイズです。", "전체 글자 배율을 적용하기 전의 글자와 아이콘 기본 크기입니다."];
    }
}
