using System.Collections.Generic;

namespace PaperTodo;

internal static partial class UserInjectionText
{
    static partial void AddContent(Dictionary<string, string[]> entries)
    {
        void Add(string id, string[] title, string[] description, bool dynamic = false)
        {
            entries[id + ".title"] = title;
            entries[id + ".description"] = description;
            if (dynamic)
                entries[id + ".defaultDescription"] = [
                    "Computed from current settings and layout",
                    "現在の設定とレイアウトから計算",
                    "현재 설정과 레이아웃으로 계산"];
        }

        // Markdown scheduling, parsing and editing.
        Add("markdown.layout.maxLinesPerTurn",
            ["Layout lines per turn", "レイアウト処理の1回あたりの行数", "회차당 레이아웃 처리 줄 수"],
            ["Maximum lines processed in one scheduled turn of the background layout worker.",
             "バックグラウンドのレイアウト処理が1回の実行で扱う最大行数です。",
             "백그라운드 레이아웃 작업이 한 번의 실행에서 처리하는 최대 줄 수입니다."]);
        Add("markdown.layout.maxTurnMs",
            ["Layout time per turn", "レイアウト処理の1回あたりの時間", "회차당 레이아웃 처리 시간"],
            ["Time budget for one scheduled turn of the background layout worker.",
             "バックグラウンドのレイアウト処理が1回の実行に使える時間です。",
             "백그라운드 레이아웃 작업이 한 번의 실행에 사용할 수 있는 시간입니다."]);
        Add("markdown.parse.fullThresholdChars",
            ["Full parse threshold", "全文解析のしきい値", "전체 구문 분석 임계값"],
            ["Text shorter than this is fully parsed on every edit; longer text uses the existing local parsing path when possible.",
             "本文がこの文字数未満なら編集ごとに全文を解析し、それ以外は可能な範囲で既存の部分解析を使います。",
             "본문이 이 글자 수보다 짧으면 편집할 때마다 전체를 분석하고, 그 외에는 가능한 경우 기존 부분 분석을 사용합니다."]);
        Add("markdown.parse.localWindowChars",
            ["Local parse window", "部分解析の初期範囲", "부분 구문 분석 범위"],
            ["Initial character window for local parsing; the existing semantic boundary rules may extend it.",
             "部分解析を始める文字範囲です。既存の意味的な境界規則に従って範囲を広げます。",
             "부분 분석의 초기 문자 범위입니다. 기존 의미 경계 규칙에 따라 범위를 확장합니다."]);
        Add("markdown.reveal.fadeMs",
            ["Markdown marker fade-in duration", "Markdown 記号のフェードイン時間", "Markdown 기호 페이드인 시간"],
            ["Base duration of the animation that reveals Markdown syntax markers while editing.",
             "編集中に Markdown の構文記号を表示するアニメーションの基準時間です。",
             "편집 중 Markdown 구문 기호를 표시하는 애니메이션의 기본 시간입니다."]);
        Add("markdown.reveal.tickMs",
            ["Markdown marker animation interval", "Markdown 記号のアニメーション間隔", "Markdown 기호 애니메이션 주기"],
            ["Timer interval for fading in Markdown syntax markers.",
             "Markdown の構文記号をフェードインするタイマーの実行間隔です。",
             "Markdown 구문 기호의 페이드인 타이머 실행 간격입니다."]);
        Add("markdown.tasks.hitPaddingDip",
            ["Markdown checkbox hit padding", "Markdown チェックボックスのクリック余白", "Markdown 체크박스 클릭 여백"],
            ["Extra clickable distance around each rendered checkbox.",
             "描画したチェックボックスの周囲に追加するクリック可能な幅です。",
             "표시된 체크박스 둘레에 추가되는 클릭 가능 거리입니다."]);

        // Note image layout, clipboard export and storage budgets.
        Add("note.images.resizeToleranceDip",
            ["Image reflow width tolerance", "画像の再配置を始める幅の変化量", "이미지 재배치 너비 허용 차이"],
            ["Reflow images once their viewport width changes by this amount.",
             "画像の表示領域の幅がこの距離だけ変わると、再配置を始めます。",
             "이미지 표시 영역의 너비가 이 거리만큼 바뀌면 재배치를 시작합니다."]);
        Add("note.images.resizeSettleMs",
            ["Image resize settling delay", "画像サイズ変更後の待機時間", "이미지 크기 변경 후 대기 시간"],
            ["Wait after image viewport resizing stops before resuming high-quality decoding.",
             "画像の表示領域の幅変更が止まってから、高品質のデコードを再開するまでの待機時間です。",
             "이미지 표시 영역의 너비 변경이 멈춘 뒤 고품질 디코딩을 재개하기까지 기다리는 시간입니다."]);
        Add("note.images.cachePruneMs",
            ["Image block cache cleanup delay", "画像ブロックキャッシュの整理待機時間", "이미지 블록 캐시 정리 대기 시간"],
            ["Debounce delay before cleaning up cached image controls after the visible lines change.",
             "表示行が変わってから画像コントロールのキャッシュを整理するまで、連続した変更をまとめる待機時間です。",
             "표시되는 줄이 바뀐 후 연속 변경을 모아 이미지 컨트롤 캐시를 정리하기까지 기다리는 시간입니다."]);
        Add("note.clipboard.maxImageBytes",
            ["Clipboard image input limit", "クリップボード画像の入力上限", "클립보드 이미지 입력 제한"],
            ["Byte limit when reading encoded image data from the clipboard.",
             "クリップボードから読み取るエンコード済み画像データのバイト数上限です。",
             "클립보드에서 인코딩된 이미지 데이터를 읽을 때의 바이트 수 제한입니다."]);
        Add("note.images.verticalPaddingDip",
            ["Note image vertical padding", "ノート画像の上下余白", "노트 이미지 위아래 여백"],
            ["Base vertical padding used by image block layout.",
             "画像ブロックのレイアウトに使う上下余白の基準値です。",
             "이미지 블록 레이아웃에 사용하는 기본 세로 여백입니다."]);
        Add("note.images.horizontalPaddingDip",
            ["Note image horizontal padding", "ノート画像の左右余白", "노트 이미지 좌우 여백"],
            ["Space reserved on each side when calculating the available image width.",
             "画像に使える幅を計算するとき、左右それぞれに確保する余白です。",
             "이미지에 사용할 수 있는 너비를 계산할 때 양쪽에 확보하는 여백입니다."]);
        Add("note.editor.indentationSize",
            ["Note indentation width", "ノートのインデント幅", "노트 들여쓰기 너비"],
            ["Tab and indentation display width in AvalonEdit.",
             "AvalonEdit でのタブとインデントの表示幅です。",
             "AvalonEdit의 탭 및 들여쓰기 표시 너비입니다."]);
        Add("note.clipboard.maxHtmlImageBytes",
            ["HTML clipboard image budget", "HTML クリップボードの画像容量上限", "HTML 클립보드 이미지 용량 한도"],
            ["Source image byte budget for mixed HTML export, counting each image occurrence separately.",
             "画像を含む HTML の出力で使う元画像の合計バイト数上限です。同じ画像でも出現するたびに加算します。",
             "이미지가 포함된 HTML을 내보낼 때의 원본 이미지 바이트 수 한도입니다. 같은 이미지도 나타날 때마다 합산합니다."]);
        Add("note.clipboard.maxRtfImageBytes",
            ["RTF clipboard image budget", "RTF クリップボードの画像容量上限", "RTF 클립보드 이미지 용량 한도"],
            ["Source image byte budget for mixed RTF export, counting each image occurrence separately.",
             "画像を含む RTF の出力で使う元画像の合計バイト数上限です。同じ画像でも出現するたびに加算します。",
             "이미지가 포함된 RTF를 내보낼 때의 원본 이미지 바이트 수 한도입니다. 같은 이미지도 나타날 때마다 합산합니다."]);
        Add("note.clipboard.imageMaxWidthDip",
            ["Clipboard image maximum width", "クリップボード画像の最大幅", "클립보드 이미지 최대 너비"],
            ["Default and maximum display width of images exported as HTML or RTF.",
             "HTML または RTF に出力する画像の既定の表示幅と最大表示幅です。",
             "HTML 또는 RTF로 내보내는 이미지의 기본 표시 너비이자 최대 표시 너비입니다."]);
        Add("note.clipboard.imageMinWidthDip",
            ["Clipboard image minimum width", "クリップボード画像の最小幅", "클립보드 이미지 최소 너비"],
            ["Minimum display width of images exported as HTML or RTF.",
             "HTML または RTF に出力する画像の最小表示幅です。",
             "HTML 또는 RTF로 내보내는 이미지의 최소 표시 너비입니다."]);
        Add("note.maxTextChars",
            ["Note text length limit", "ノートの最大文字数", "노트 최대 글자 수"],
            ["Character limit shared by the note editor and the host write APIs.",
             "ノートエディターとホストの書き込み API が共通して使う文字数上限です。",
             "노트 편집기와 호스트 쓰기 API가 공통으로 사용하는 글자 수 제한입니다."]);
        Add("note.images.maxStoredDimensionPx",
            ["Stored image maximum dimension", "保存画像の最大辺長", "저장 이미지 최대 변 길이"],
            ["Maximum pixel dimension accepted when importing or loading image assets.",
             "画像アセットの取り込みや読み込みで受け入れる、各辺の最大ピクセル数です。",
             "이미지 자산을 가져오거나 불러올 때 허용되는 각 변의 최대 픽셀 수입니다."]);
        Add("note.images.autoCompressedDimensionPx",
            ["Automatic compression target dimension", "自動圧縮後の目標辺長", "자동 압축 목표 변 길이"],
            ["Maximum dimension after resizing large images when automatic compression is enabled.",
             "自動圧縮が有効なとき、大きな画像を縮小した後の最大辺長です。",
             "자동 압축이 켜져 있을 때 큰 이미지를 축소한 후의 최대 변 길이입니다."]);
        Add("note.images.maxStoredBytes",
            ["Single image storage budget", "画像1枚の保存容量上限", "개별 이미지 저장 용량 한도"],
            ["Maximum encoded byte size of one image asset.",
             "画像アセット1枚をエンコードした後のバイト数上限です。",
             "이미지 자산 하나를 인코딩한 후의 최대 바이트 수입니다."]);
        Add("note.images.maxInputBytes",
            ["Image import input budget", "画像取り込みの入力容量上限", "이미지 가져오기 입력 용량 한도"],
            ["Byte limit for original image data supplied during import.",
             "画像を取り込むときの元データのバイト数上限です。",
             "이미지를 가져올 때 원본 입력 데이터의 바이트 수 제한입니다."]);
        Add("note.images.maxTotalBytes",
            ["Total image asset budget", "画像アセットの合計容量上限", "이미지 자산 총용량 한도"],
            ["Total encoded byte limit for all images in the current image store.",
             "現在の画像ストレージにある、すべてのエンコード済み画像の合計バイト数上限です。",
             "현재 이미지 저장소에 있는 모든 인코딩된 이미지의 총 바이트 수 제한입니다."]);
        Add("note.images.maxDecodedBytes",
            ["Decoded image cache budget", "デコード済み画像キャッシュの容量上限", "디코딩된 이미지 캐시 용량 한도"],
            ["Byte budget for the LRU decoded-image cache, excluding images currently visible.",
             "現在表示中の画像を除く、デコード済み画像の LRU キャッシュのバイト数上限です。",
             "현재 보이는 이미지를 제외한 디코딩된 이미지 LRU 캐시의 바이트 수 한도입니다."]);
        Add("note.images.maxCacheEntries",
            ["Decoded image cache count", "デコード済み画像キャッシュの件数上限", "디코딩된 이미지 캐시 개수"],
            ["Entry budget for the LRU decoded-image cache, excluding images currently visible.",
             "現在表示中の画像を除く、デコード済み画像の LRU キャッシュの件数上限です。",
             "현재 보이는 이미지를 제외한 디코딩된 이미지 LRU 캐시의 항목 수 한도입니다."]);

        // Todo text, animation, dragging and selection.
        Add("todo.fold.thresholdLines",
            ["Todo folding line threshold", "ToDo の折りたたみ行数しきい値", "할 일 접기 줄 수 임계값"],
            ["Allow folding when the todo's displayed line count after layout exceeds this value.",
             "レイアウト後の ToDo の表示行数がこの値を超えると、折りたたみを許可します。",
             "레이아웃 후 할 일의 표시 줄 수가 이 값을 넘으면 접기를 허용합니다."]);
        Add("todo.fold.visibleLines",
            ["Folded todo visible lines", "折りたたんだ ToDo の表示行数", "접힌 할 일 표시 줄 수"],
            ["Number of text lines kept visible in a folded todo.",
             "ToDo を折りたたんだときに表示を残す本文の行数です。",
             "할 일을 접었을 때 계속 표시할 본문 줄 수입니다."]);
        Add("todo.maxTextChars",
            ["Todo text length limit", "ToDo の最大文字数", "할 일 최대 글자 수"],
            ["Character limit shared by the todo editor and the host write APIs.",
             "ToDo エディターとホストの書き込み API が共通して使う文字数上限です。",
             "할 일 편집기와 호스트 쓰기 API가 공통으로 사용하는 글자 수 제한입니다."]);
        Add("todo.paste.maxItems",
            ["Todo batch input limit", "ToDo の一括入力件数上限", "할 일 일괄 입력 제한"],
            ["Maximum todos in one multiline paste or one host batch-create operation.",
             "1回の複数行貼り付け、またはホストの一括作成で追加できる ToDo の最大件数です。",
             "여러 줄 붙여넣기 한 번 또는 호스트 일괄 생성 한 번으로 추가할 수 있는 최대 할 일 수입니다."]);
        Add("todo.strike.insetDip",
            ["Todo strikethrough inset", "ToDo の取り消し線の左右余白", "할 일 취소선 좌우 여백"],
            ["Inset at each end of a completed todo's strikethrough.",
             "完了した ToDo の取り消し線を両端から内側へ短くする距離です。",
             "완료된 할 일의 취소선을 양 끝에서 안쪽으로 줄이는 거리입니다."]);
        Add("todo.strike.thicknessDip",
            ["Todo strikethrough thickness", "ToDo の取り消し線の太さ", "할 일 취소선 두께"],
            ["Stroke thickness of the strikethrough on completed todos.",
             "完了した ToDo に描く取り消し線の太さです。",
             "완료된 할 일에 그리는 취소선의 두께입니다."]);
        Add("todo.strike.verticalRatio",
            ["Todo strikethrough height ratio", "ToDo の取り消し線の高さ比率", "할 일 취소선 높이 비율"],
            ["Vertical position of the strikethrough as a fraction of each text line's rectangle.",
             "各テキスト行の矩形内で、取り消し線を置く縦位置の比率です。",
             "각 텍스트 줄의 사각 영역 안에서 취소선이 놓이는 세로 위치 비율입니다."]);
        Add("todo.paste.animatedItems",
            ["Animated pasted todo count", "貼り付け時にアニメーションする ToDo 数", "붙여넣기 애니메이션 할 일 수"],
            ["Maximum todos included in staggered entrance animations during multiline paste.",
             "複数行の貼り付け時に、時間差で表示するアニメーションの対象となる ToDo の最大件数です。",
             "여러 줄을 붙여넣을 때 순차 등장 애니메이션을 적용하는 최대 할 일 수입니다."]);
        Add("todo.paste.staggerMs",
            ["Paste animation stagger interval", "貼り付けアニメーションの開始間隔", "붙여넣기 애니메이션 시작 간격"],
            ["Time between the start of adjacent todos' entrance animations.",
             "隣り合う ToDo の表示アニメーションを開始する時間差です。",
             "인접한 할 일의 등장 애니메이션이 시작되는 시간 차이입니다."]);
        Add("todo.paste.startOffsetDip",
            ["Paste animation starting offset", "貼り付けアニメーションの初期位置差", "붙여넣기 애니메이션 초기 위치 차이"],
            ["Vertical displacement at the start of a todo's entrance animation.",
             "ToDo の表示アニメーション開始時の縦方向の移動量です。",
             "할 일 등장 애니메이션 시작 시 세로 방향 위치 차이입니다."]);
        Add("todo.paste.fadeMs",
            ["Paste fade-in duration", "貼り付け時のフェードイン時間", "붙여넣기 페이드인 시간"],
            ["Base opacity transition duration for todos added by multiline paste.",
             "複数行の貼り付けで追加する ToDo の不透明度が変化する基準時間です。",
             "여러 줄 붙여넣기로 추가한 할 일의 불투명도 전환 기본 시간입니다."]);
        Add("todo.paste.moveMs",
            ["Paste movement duration", "貼り付け時の移動時間", "붙여넣기 이동 시간"],
            ["Base vertical transition duration for todos added by multiline paste.",
             "複数行の貼り付けで追加する ToDo の縦方向の移動に使う基準時間です。",
             "여러 줄 붙여넣기로 추가한 할 일의 세로 이동 기본 시간입니다."]);
        Add("todo.delete.animationMs",
            ["Todo deletion animation duration", "ToDo 削除アニメーションの時間", "할 일 삭제 애니메이션 시간"],
            ["Base fade-out and horizontal movement duration when deleting one todo.",
             "ToDo を1件削除するときのフェードアウトと横移動に使う基準時間です。",
             "할 일 하나를 삭제할 때 페이드아웃과 가로 이동에 사용하는 기본 시간입니다."]);
        Add("todo.delete.offsetDip",
            ["Todo deletion animation offset", "ToDo 削除アニメーションの移動量", "할 일 삭제 애니메이션 이동 거리"],
            ["Target horizontal displacement of a single todo's deletion animation.",
             "ToDo を1件削除するアニメーションの横方向の目標移動量です。",
             "할 일 하나의 삭제 애니메이션에서 목표로 하는 가로 이동 거리입니다."]);
        Add("todo.drag.ghostMinWidthDip",
            ["Todo drag preview minimum width", "ToDo ドラッグプレビューの最小幅", "할 일 드래그 미리보기 최소 너비"],
            ["Minimum width of the todo preview that follows the pointer while dragging between positions.",
             "ToDo を別の位置へドラッグするとき、ポインターに追従するプレビューの最小幅です。",
             "할 일을 다른 위치로 드래그할 때 포인터를 따라가는 미리보기의 최소 너비입니다."]);
        Add("todo.drag.ghostMinHeightDip",
            ["Todo drag preview minimum height", "ToDo ドラッグプレビューの最小高さ", "할 일 드래그 미리보기 최소 높이"],
            ["Minimum height of the todo preview that follows the pointer.",
             "ポインターに追従する ToDo プレビューの最小高さです。",
             "포인터를 따라가는 할 일 미리보기의 최소 높이입니다."]);
        Add("todo.drag.indicatorHeightDip",
            ["Todo drop indicator height", "ToDo 挿入位置線の高さ", "할 일 삽입 위치 표시선 높이"],
            ["Height of the insertion line shown while reordering todos by dragging.",
             "ToDo をドラッグして並べ替えるときに表示する挿入位置線の高さです。",
             "할 일을 드래그해 순서를 바꿀 때 표시하는 삽입 위치선의 높이입니다."]);
        Add("todo.drag.indicatorRadiusDip",
            ["Todo drop indicator corner radius", "ToDo 挿入位置線の角丸半径", "할 일 삽입 위치 표시선 모서리 반경"],
            ["Corner radius of the todo reordering indicator.",
             "ToDo の並べ替え位置を示す線の角丸半径です。",
             "할 일 순서 변경 위치를 나타내는 표시선의 모서리 반경입니다."]);
        Add("todo.drag.indicatorMinWidthDip",
            ["Todo drop indicator minimum width", "ToDo 挿入位置線の最小幅", "할 일 삽입 위치 표시선 최소 너비"],
            ["Minimum width of the insertion position indicator.",
             "挿入位置を示す線の最小幅です。",
             "삽입 위치를 나타내는 표시선의 최소 너비입니다."]);
        Add("todo.drag.indicatorWidthInsetDip",
            ["Todo drop indicator width reduction", "ToDo 挿入位置線の幅の減少量", "할 일 삽입 위치 표시선 너비 감소량"],
            ["Total width subtracted from the todo row width for its insertion indicator.",
             "挿入位置線の幅を求めるとき、ToDo 行の幅から差し引く合計幅です。",
             "삽입 위치 표시선의 너비를 구할 때 할 일 행 너비에서 빼는 총너비입니다."]);
        Add("todo.selection.tickMs",
            ["Todo multiselection tracking interval", "ToDo 複数選択の追跡間隔", "할 일 다중 선택 추적 주기"],
            ["Pointer tracking timer interval during box selection or sweep selection.",
             "範囲選択やなぞり選択でポインターを追跡するタイマーの間隔です。",
             "영역 선택 또는 쓸어 선택 중 포인터를 추적하는 타이머 간격입니다."]);
        Add("todo.selection.edgeDip",
            ["Todo multiselection scroll zone", "ToDo 複数選択のスクロール領域", "할 일 다중 선택 스크롤 영역"],
            ["Base size of the sweep-selection auto-scroll zone, before text scaling.",
             "なぞり選択で自動スクロールする領域の基準サイズです。この後に文字の拡大率を掛けます。",
             "쓸어 선택의 자동 스크롤 영역 기본 크기입니다. 이후 텍스트 배율을 곱합니다."]);
        Add("todo.selection.edgeHeightRatio",
            ["Todo multiselection scroll zone ratio", "ToDo 複数選択の領域高さ比率", "할 일 다중 선택 스크롤 영역 비율"],
            ["Maximum fraction of the visible list height occupied by the sweep-selection scroll zone.",
             "なぞり選択のスクロール領域が、一覧の表示高さに占める最大比率です。",
             "쓸어 선택 스크롤 영역이 목록의 표시 높이에서 차지하는 최대 비율입니다."]);
        Add("todo.selection.effectiveEdgeDip",
            ["Final todo multiselection scroll zone", "ToDo 複数選択の最終スクロール領域", "할 일 다중 선택 최종 스크롤 영역"],
            ["Override the sweep-selection scroll zone after size and ratio calculations.",
             "サイズと比率から計算した、なぞり選択のスクロール領域を上書きします。",
             "크기와 비율로 계산한 쓸어 선택 스크롤 영역을 재정의합니다."], dynamic: true);
        Add("todo.selection.stepDip",
            ["Todo multiselection acceleration distance", "ToDo 複数選択の加速距離", "할 일 다중 선택 가속 거리"],
            ["Base minimum distance required to add one scroll step.",
             "スクロールのステップ数を1つ増やすために必要な距離の、基準となる下限です。",
             "스크롤 단계를 하나 늘리는 데 필요한 거리의 기본 최솟값입니다."]);
        Add("todo.selection.stepEdgeRatio",
            ["Todo multiselection acceleration ratio", "ToDo 複数選択の加速距離比率", "할 일 다중 선택 가속 거리 비율"],
            ["Distance required to add a scroll step, as a fraction of the scroll zone size.",
             "スクロールのステップ数を増やす距離を、スクロール領域のサイズに対する比率で指定します。",
             "스크롤 단계를 늘리는 데 필요한 거리를 스크롤 영역 크기에 대한 비율로 지정합니다."]);
        Add("todo.selection.effectiveStepDip",
            ["Final todo multiselection acceleration distance", "ToDo 複数選択の最終加速距離", "할 일 다중 선택 최종 가속 거리"],
            ["Override the calculated distance required to add one scroll step.",
             "スクロールのステップ数を1つ増やすために必要な、計算後の距離を上書きします。",
             "스크롤 단계를 하나 늘리는 데 필요한 계산된 거리를 재정의합니다."], dynamic: true);
        Add("todo.selection.maxSteps",
            ["Todo multiselection maximum scroll steps", "ToDo 複数選択の最大スクロール段数", "할 일 다중 선택 최대 스크롤 단계"],
            ["Maximum line-scroll steps per tracking tick during sweep-selection auto-scroll.",
             "なぞり選択の自動スクロールで、1回の追跡時に実行する行スクロールの最大回数です。",
             "쓸어 선택 자동 스크롤에서 추적 주기마다 실행하는 최대 줄 스크롤 횟수입니다."]);

        // Backgrounds and reminder scheduling.
        Add("background.maxDecodeDimensionPx",
            ["Background image decode dimension", "紙片背景のデコード辺長", "메모 배경 디코딩 변 길이"],
            ["Maximum pixel dimension used when decoding a background image.",
             "背景画像をデコードするときの各辺の最大ピクセル数です。",
             "배경 이미지를 디코딩할 때 각 변의 최대 픽셀 수입니다."]);
        Add("background.blendedImageOpacity",
            ["Blended background image opacity", "背景色と混合する画像の不透明度", "배경색 혼합 이미지 불투명도"],
            ["Image brush opacity when blending with the background color is enabled.",
             "背景色との混合を有効にしたときの、画像ブラシの不透明度です。",
             "배경색과 혼합하기가 켜져 있을 때 이미지 브러시의 불투명도입니다."]);
        Add("reminder.maxCustomSoundBytes",
            ["Custom reminder sound budget", "カスタム通知音の容量上限", "사용자 지정 알림음 용량 한도"],
            ["Maximum file size accepted when loading a custom reminder sound.",
             "カスタム通知音を読み込むときに許可する最大ファイルサイズです。",
             "사용자 지정 알림음을 불러올 때 허용하는 최대 파일 크기입니다."]);
        Add("reminder.maxTimerIntervalMs",
            ["Maximum reminder check interval", "リマインダー確認の最長間隔", "알림 확인 최대 간격"],
            ["Longest allowed timer interval before the next reminder check.",
             "次のリマインダー確認までのタイマーに許可する最長間隔です。",
             "다음 알림 확인 타이머에 허용되는 최대 간격입니다."]);
        Add("reminder.minTimerIntervalMs",
            ["Minimum reminder check interval", "リマインダー確認の最短間隔", "알림 확인 최소 간격"],
            ["Shortest allowed interval for reminder scheduling and retry timers.",
             "リマインダーのスケジュール用・再試行用タイマーに許可する最短間隔です。",
             "알림 예약 및 재시도 타이머에 허용되는 최소 간격입니다."]);
        Add("reminder.advanceNoticeMs",
            ["Reminder advance notice time", "リマインダーの事前通知時間", "알림 사전 통지 시간"],
            ["How far ahead of the due time the reminder preview event is sent to plugins.",
             "リマインダーの期限よりどれだけ前に、プラグインへ予告イベントを送るかを指定します。",
             "알림 예정 시각보다 얼마나 일찍 플러그인에 사전 통지 이벤트를 보낼지 지정합니다."]);
        Add("reminder.retryMs",
            ["Reminder failure retry interval", "リマインダー失敗時の再試行間隔", "알림 실패 재시도 간격"],
            ["Default retry delay after some or all reminder deliveries fail.",
             "リマインダーの配信が一部またはすべて失敗した後、再試行するまでの既定の待機時間です。",
             "알림 전달이 일부 또는 모두 실패한 뒤 재시도하기까지의 기본 대기 시간입니다."]);
        Add("reminder.summaryChars",
            ["Reminder summary length", "リマインダー要約の長さ", "알림 요약 길이"],
            ["Maximum characters in a system tray reminder summary.",
             "システムトレイのリマインダー要約に表示する最大文字数です。",
             "시스템 트레이 알림 요약의 최대 글자 수입니다."]);

        // Window binding, following and tethering.
        Add("window.binding.sampleMs",
            ["Window binding target sample interval", "関連付け対象ウィンドウの検出間隔", "연결 대상 창 감지 주기"],
            ["Interval between target window detection while dragging the binding button.",
             "関連付けボタンをドラッグしている間、対象ウィンドウを再検出する間隔です。",
             "연결 버튼을 드래그하는 동안 대상 창을 다시 감지하는 간격입니다."]);
        Add("window.binding.dragOpacity",
            ["Window binding drag opacity", "ウィンドウ関連付けのドラッグ時不透明度", "창 연결 드래그 불투명도"],
            ["Binding button opacity during the window binding drag gesture.",
             "ウィンドウを関連付けるドラッグ操作中の、関連付けボタンの不透明度です。",
             "창 연결 드래그 동작 중 연결 버튼의 불투명도입니다."]);
        Add("window.opacity.transitionMs",
            ["Unfocused opacity transition duration", "非アクティブ時の不透明度の遷移時間", "포커스 해제 불투명도 전환 시간"],
            ["Base opacity transition duration for unfocused papers and idle capsules.",
             "非アクティブな紙片と待機中のカプセルの不透明度が変化する基準時間です。",
             "포커스가 없는 메모와 대기 중인 캡슐의 불투명도 전환 기본 시간입니다."]);
        Add("window.autoCollapse.delayMs",
            ["Auto-collapse delay after focus loss", "フォーカスを失った後の自動収納待機時間", "포커스 해제 후 자동 접기 지연"],
            ["Wait after focus is lost before automatically collapsing the paper.",
             "フォーカスを失ってから紙片を自動的に折りたたむまでの待機時間です。",
             "포커스를 잃은 뒤 메모를 자동으로 접기까지 기다리는 시간입니다."]);
        Add("window.follow.slideMs",
            ["Following capsule slide duration", "追従カプセルのスライド時間", "추적 캡슐 슬라이드 시간"],
            ["Slide duration when a window-bound capsule appears or retracts.",
             "関連付けたウィンドウのカプセルが表示・収納されるときのスライド時間です。",
             "창에 연결된 캡슐이 나타나거나 들어갈 때의 슬라이드 시간입니다."]);
        Add("window.follow.retractDelayMs",
            ["Following capsule retraction delay", "追従カプセルの収納待機時間", "추적 캡슐 접기 지연"],
            ["Wait before retracting a following capsule after the pointer leaves it.",
             "追従カプセルからポインターが離れた後、収納するまでの待機時間です。",
             "추적 캡슐에서 포인터가 벗어난 뒤 캡슐을 접기까지 기다리는 시간입니다."]);
        Add("window.tether.detachDip",
            ["Window tether detach distance", "ウィンドウ連結の解除距離", "창 연결 해제 거리"],
            ["Detach the tether once dragging reaches this distance.",
             "ドラッグ距離がこの値に達すると連結を解除します。",
             "드래그 거리가 이 값에 도달하면 연결을 해제합니다."]);
        Add("window.tether.minHandleWidthDip",
            ["Window tether visible handle width", "ウィンドウ連結の可視ハンドル幅", "창 연결 핸들 표시 너비"],
            ["Minimum visible width kept available as a draggable handle.",
             "ドラッグ可能なハンドルを残すために確保する最小表示幅です。",
             "드래그 가능한 핸들을 유지하기 위해 확보하는 최소 표시 너비입니다."]);
        Add("window.tether.minHandleHeightDip",
            ["Window tether visible handle height", "ウィンドウ連結の可視ハンドル高さ", "창 연결 핸들 표시 높이"],
            ["Minimum visible height kept available as a draggable handle.",
             "ドラッグ可能なハンドルを残すために確保する最小表示高さです。",
             "드래그 가능한 핸들을 유지하기 위해 확보하는 최소 표시 높이입니다."]);
        Add("window.follow.stableSamples",
            ["Window following stable sample count", "ウィンドウ追従の安定サンプル数", "창 추적 안정 샘플 수"],
            ["Number of stable position samples required before stopping continuous following frames.",
             "連続した追従フレームを止めるまでに必要な、位置が安定したサンプル数です。",
             "연속 추적 프레임을 중지하기 전에 필요한 안정된 위치 샘플 수입니다."]);
        Add("window.follow.stableMs",
            ["Window following stable duration", "ウィンドウ追従の安定待機時間", "창 추적 안정 유지 시간"],
            ["Time the target window must stay still before following frames stop.",
             "追従フレームを止めるために、対象ウィンドウが静止している必要のある時間です。",
             "추적 프레임을 중지하기 위해 대상 창이 정지 상태를 유지해야 하는 시간입니다."]);
        Add("window.follow.movingIdleMs",
            ["Window movement following idle timeout", "移動操作中の追従休止時間", "창 이동 추적 유휴 시간"],
            ["Idle time required to stop following frames while the move gesture is still active.",
             "移動ジェスチャーが続いている間に追従フレームを止めるための、動きのない待機時間です。",
             "이동 동작이 아직 진행 중일 때 추적 프레임을 중지하기 위해 필요한 움직임 없는 대기 시간입니다."]);

        // Plugin capsules and notification coalescing.
        Add("plugin.capsule.minContentWidthDip",
            ["Plugin capsule minimum content width", "プラグインカプセルの最小内容幅", "플러그인 캡슐 콘텐츠 최소 너비"],
            ["Lower bound for a plugin capsule's desired content width.",
             "プラグインカプセルが要求する内容幅の下限です。",
             "플러그인 캡슐이 요청하는 콘텐츠 너비의 하한입니다."]);
        Add("plugin.capsule.maxContentWidthDip",
            ["Plugin capsule maximum content width", "プラグインカプセルの最大内容幅", "플러그인 캡슐 콘텐츠 최대 너비"],
            ["Upper bound for a plugin capsule's desired content width.",
             "プラグインカプセルが要求する内容幅の上限です。",
             "플러그인 캡슐이 요청하는 콘텐츠 너비의 상한입니다."]);
        Add("plugin.capsule.componentGapDip",
            ["Plugin capsule component gap", "プラグインカプセルの部品間隔", "플러그인 캡슐 구성 요소 간격"],
            ["Spacing between adjacent components in the plugin template.",
             "プラグインのテンプレート内で隣り合う部品同士の間隔です。",
             "플러그인 템플릿 안에서 인접한 구성 요소 사이의 간격입니다."]);
        Add("plugin.capsule.statusDotSizeDip",
            ["Plugin capsule status dot size", "プラグインカプセルの状態ドットサイズ", "플러그인 캡슐 상태 점 크기"],
            ["Diameter of a plugin capsule's status dot.",
             "プラグインカプセルの状態を示す丸い点の直径です。",
             "플러그인 캡슐의 상태를 나타내는 원형 점의 지름입니다."]);
        Add("plugin.capsule.progressRingSizeDip",
            ["Plugin capsule progress ring size", "プラグインカプセルの進捗リングサイズ", "플러그인 캡슐 진행률 링 크기"],
            ["Progress ring diameter when the plugin does not specify a size.",
             "プラグインがサイズを指定していない場合の、進捗リングの直径です。",
             "플러그인이 크기를 지정하지 않았을 때 진행률 링의 지름입니다."]);
        Add("plugin.capsule.progressBarWidthDip",
            ["Plugin capsule progress bar width", "プラグインカプセルの進捗バー幅", "플러그인 캡슐 진행률 막대 너비"],
            ["Progress bar width when the plugin does not specify a size.",
             "プラグインがサイズを指定していない場合の、進捗バーの幅です。",
             "플러그인이 크기를 지정하지 않았을 때 진행률 막대의 너비입니다."]);
        Add("plugin.capsule.fillProgressBarMinWidthDip",
            ["Plugin capsule filling progress bar minimum width", "プラグインカプセルの可変進捗バー最小幅", "플러그인 캡슐 채움 진행률 막대 최소 너비"],
            ["Minimum width of a progress bar that fills the available space.",
             "利用可能な空間を埋める進捗バーの最小幅です。",
             "사용 가능한 공간을 채우는 진행률 막대의 최소 너비입니다."]);
        Add("plugin.events.userChangeDebounceMs",
            ["Plugin user-change event debounce", "プラグインへの編集通知の待機時間", "플러그인 사용자 변경 이벤트 대기 시간"],
            ["Wait to combine consecutive user edits before creating plugin notifications.",
             "連続したユーザー編集をまとめてからプラグイン通知を作るまでの待機時間です。",
             "연속된 사용자 편집을 모아 플러그인 알림을 만들기까지 기다리는 시간입니다."]);
        Add("plugin.settings.textPropagationMs",
            ["Plugin text setting propagation delay", "プラグイン文字設定の反映待機時間", "플러그인 텍스트 설정 반영 지연"],
            ["Wait to coalesce changes to plugin string and numeric settings.",
             "プラグインの文字列設定と数値設定の連続した変更をまとめる待機時間です。",
             "플러그인 문자열 및 숫자 설정의 연속 변경을 모으는 대기 시간입니다."]);

        // These metrics share the same point in the preset/scaling lifecycle.
        string[] todoMetricDescription = [
            "Override this measurement after the todo preset and global text scaling. Used when a todo row is next built.",
            "ToDo プリセットと全体の文字拡大率を適用した後の寸法を上書きします。次に ToDo 行を構築するときに使います。",
            "할 일 프리셋과 전체 텍스트 배율을 적용한 후의 치수를 재정의합니다. 다음에 할 일 행을 구성할 때 사용합니다."];
        Add("todo.metrics.textFontSize",
            ["Todo text font size", "ToDo 本文の文字サイズ", "할 일 본문 글꼴 크기"], todoMetricDescription, dynamic: true);
        Add("todo.metrics.textVerticalPadding",
            ["Todo text vertical padding", "ToDo 本文の上下余白", "할 일 본문 위아래 여백"], todoMetricDescription, dynamic: true);
        Add("todo.metrics.appendMinHeight",
            ["Todo add-row height", "ToDo 追加行の高さ", "할 일 추가 행 높이"], todoMetricDescription, dynamic: true);
        Add("todo.metrics.appendGlyphFontSize",
            ["Todo add symbol font size", "ToDo 追加記号の文字サイズ", "할 일 추가 기호 글꼴 크기"], todoMetricDescription, dynamic: true);
        Add("todo.metrics.trashGlyphFontSize",
            ["Todo trash icon font size", "ToDo のごみ箱アイコンの文字サイズ", "할 일 휴지통 아이콘 글꼴 크기"], todoMetricDescription, dynamic: true);
        Add("todo.metrics.linkedPaperNameFontSize",
            ["Todo linked paper name font size", "ToDo のリンク先紙片名の文字サイズ", "할 일 연결 메모 이름 글꼴 크기"], todoMetricDescription, dynamic: true);
        Add("todo.metrics.linkedPaperIconFontSize",
            ["Todo linked paper icon font size", "ToDo のリンク先紙片アイコンの文字サイズ", "할 일 연결 메모 아이콘 글꼴 크기"], todoMetricDescription, dynamic: true);
        Add("todo.metrics.checkColumnWidth",
            ["Todo checkbox column width", "ToDo チェック列の幅", "할 일 체크박스 열 너비"], todoMetricDescription, dynamic: true);
        Add("todo.metrics.ghostTextFontSize",
            ["Todo drag preview font size", "ToDo ドラッグプレビューの文字サイズ", "할 일 드래그 미리보기 글꼴 크기"], todoMetricDescription, dynamic: true);
        Add("todo.metrics.rowMinHeight",
            ["Todo row minimum height", "ToDo 行の最小高さ", "할 일 행 최소 높이"], todoMetricDescription, dynamic: true);

        // Base note sizes precede presets; computed sizes precede per-note zoom.
        string[] baseNoteFontDescription = [
            "Font size before the font preset adjustment and global scaling.",
            "文字サイズプリセットの補正と全体の拡大率を適用する前の文字サイズです。",
            "글꼴 크기 프리셋 조정과 전체 배율을 적용하기 전의 글꼴 크기입니다."];
        string[] computedNoteFontDescription = [
            "Override the font size after the preset and global scaling; per-note zoom is still applied afterward.",
            "プリセットと全体の拡大率を適用した後の文字サイズを上書きします。ノートごとの拡大率は引き続きその後に適用します。",
            "프리셋과 전체 배율을 적용한 후의 글꼴 크기를 재정의합니다. 개별 노트 확대 배율은 계속 그 이후에 적용됩니다."];
        Add("note.typography.baseFontSize",
            ["Note text base font size", "ノート本文の基本文字サイズ", "노트 본문 기본 글꼴 크기"], baseNoteFontDescription);
        Add("note.typography.fontSize",
            ["Note text computed font size", "ノート本文の計算後の文字サイズ", "노트 본문 계산된 글꼴 크기"], computedNoteFontDescription, dynamic: true);
        Add("note.typography.baseCodeFontSize",
            ["Note code base font size", "ノートのコードの基本文字サイズ", "노트 코드 기본 글꼴 크기"], baseNoteFontDescription);
        Add("note.typography.codeFontSize",
            ["Note code computed font size", "ノートのコードの計算後の文字サイズ", "노트 코드 계산된 글꼴 크기"], computedNoteFontDescription, dynamic: true);
        Add("note.typography.baseHeading1FontSize",
            ["Note heading 1 base font size", "ノート見出し1の基本文字サイズ", "노트 제목 1 기본 글꼴 크기"], baseNoteFontDescription);
        Add("note.typography.heading1FontSize",
            ["Note heading 1 computed font size", "ノート見出し1の計算後の文字サイズ", "노트 제목 1 계산된 글꼴 크기"], computedNoteFontDescription, dynamic: true);
        Add("note.typography.baseHeading2FontSize",
            ["Note heading 2 base font size", "ノート見出し2の基本文字サイズ", "노트 제목 2 기본 글꼴 크기"], baseNoteFontDescription);
        Add("note.typography.heading2FontSize",
            ["Note heading 2 computed font size", "ノート見出し2の計算後の文字サイズ", "노트 제목 2 계산된 글꼴 크기"], computedNoteFontDescription, dynamic: true);
        Add("note.typography.baseHeading3FontSize",
            ["Note heading 3 base font size", "ノート見出し3の基本文字サイズ", "노트 제목 3 기본 글꼴 크기"], baseNoteFontDescription);
        Add("note.typography.heading3FontSize",
            ["Note heading 3 computed font size", "ノート見出し3の計算後の文字サイズ", "노트 제목 3 계산된 글꼴 크기"], computedNoteFontDescription, dynamic: true);
        string[] notePaddingDescription = [
            "Inner padding of the note content area.",
            "ノートの本文領域の内側余白です。",
            "노트 본문 영역의 안쪽 여백입니다."];
        Add("note.padding.leftDip",
            ["Note content left padding", "ノート本文の左余白", "노트 본문 왼쪽 여백"], notePaddingDescription);
        Add("note.padding.topDip",
            ["Note content top padding", "ノート本文の上余白", "노트 본문 위쪽 여백"], notePaddingDescription);
        Add("note.padding.rightDip",
            ["Note content right padding", "ノート本文の右余白", "노트 본문 오른쪽 여백"], notePaddingDescription);
        Add("note.padding.bottomDip",
            ["Note content bottom padding", "ノート本文の下余白", "노트 본문 아래쪽 여백"], notePaddingDescription);
        Add("note.images.viewportReserveDip",
            ["Note image viewport reserve", "ノート画像の表示領域の追加余白", "노트 이미지 표시 영역 추가 여백"],
            ["Extra space subtracted from image width in addition to left and right padding.",
             "画像の幅を計算するとき、左右の余白とは別に追加で差し引く空間です。",
             "이미지 너비를 계산할 때 좌우 여백 외에 추가로 빼는 공간입니다."]);
        Add("note.images.targetWidthDip",
            ["Final note image available width", "ノート画像の最終使用可能幅", "노트 이미지 최종 가용 너비"],
            ["Override the target image width calculated from the content viewport.",
             "本文の表示領域から計算した画像の目標幅を上書きします。",
             "본문 표시 영역으로 계산한 이미지 목표 너비를 재정의합니다."], dynamic: true);
        Add("note.images.fallbackWidthDip",
            ["Note image width before layout", "配置前のノート画像幅", "레이아웃 전 노트 이미지 너비"],
            ["Target width used while the image viewport has no valid width yet.",
             "画像の表示領域の幅がまだ確定していないときに使う目標幅です。",
             "이미지 표시 영역에 아직 유효한 너비가 없을 때 사용하는 목표 너비입니다."]);
        Add("note.images.minTargetWidthDip",
            ["Note image minimum target width", "ノート画像の最小目標幅", "노트 이미지 최소 목표 너비"],
            ["Built-in lower bound for the image target width after viewport calculations.",
             "表示領域から計算した後の画像目標幅に適用する、既存の下限です。",
             "표시 영역 계산 후 이미지 목표 너비에 적용하는 기존 하한입니다."]);

        // Find panel sizing and placement.
        Add("find.baseWidthDip",
            ["Find panel base width", "検索パネルの基本幅", "찾기 패널 기본 너비"],
            ["Find panel width before global text scaling.",
             "全体の文字拡大率を掛ける前の検索パネルの幅です。",
             "전체 텍스트 배율을 적용하기 전의 찾기 패널 너비입니다."]);
        Add("find.minWidthDip",
            ["Find panel minimum width", "検索パネルの最小幅", "찾기 패널 최소 너비"],
            ["Lower width bound when the find panel follows text scaling.",
             "文字の拡大率に合わせて検索パネルの幅を変えるときの下限です。",
             "텍스트 배율에 맞춰 찾기 패널 너비를 조정할 때의 하한입니다."]);
        Add("find.maxWidthDip",
            ["Find panel maximum width", "検索パネルの最大幅", "찾기 패널 최대 너비"],
            ["Upper width bound when the find panel follows text scaling.",
             "文字の拡大率に合わせて検索パネルの幅を変えるときの上限です。",
             "텍스트 배율에 맞춰 찾기 패널 너비를 조정할 때의 상한입니다."]);
        Add("find.widthDip",
            ["Final find panel width", "検索パネルの最終幅", "찾기 패널 최종 너비"],
            ["Override the find panel width after text scaling, limiting and rounding.",
             "文字拡大率、上下限、丸めを適用した後の検索パネルの幅を上書きします。",
             "텍스트 배율, 상하한 제한 및 반올림을 적용한 후의 찾기 패널 너비를 재정의합니다."], dynamic: true);
        Add("find.inputMinWidthDip",
            ["Find input minimum width", "検索入力欄の最小幅", "찾기 입력란 최소 너비"],
            ["Minimum width of the column containing the find input field.",
             "検索の入力欄を置く列の最小幅です。",
             "찾기 입력란이 배치되는 열의 최소 너비입니다."]);
        Add("find.inputFontSizeDip",
            ["Find input font size", "検索入力の文字サイズ", "찾기 입력 글꼴 크기"],
            ["Font size for find input and button text before global scaling.",
             "全体の拡大率を掛ける前の、検索入力とボタン文字のサイズです。",
             "전체 배율을 적용하기 전의 찾기 입력 및 버튼 텍스트 글꼴 크기입니다."]);
        Add("find.counterFontSizeDip",
            ["Find counter font size", "検索件数の文字サイズ", "찾기 개수 글꼴 크기"],
            ["Match counter font size before global scaling.",
             "全体の拡大率を掛ける前の、一致件数を示す文字のサイズです。",
             "전체 배율을 적용하기 전의 일치 개수 표시 글꼴 크기입니다."]);
        Add("find.controlSizeDip",
            ["Find control base size", "検索コントロールの基本サイズ", "찾기 컨트롤 기본 크기"],
            ["Minimum input height and button side length, subsequently adjusted by text scaling.",
             "入力欄の最小高さとボタンの一辺の長さです。この後に文字の拡大率で調整します。",
             "입력란의 최소 높이와 버튼의 한 변 길이입니다. 이후 텍스트 배율에 맞춰 조정합니다."]);
        Add("find.counterMinWidthDip",
            ["Find counter minimum width", "検索件数領域の最小幅", "찾기 개수 영역 최소 너비"],
            ["Base minimum width of the match count area.",
             "一致件数を表示する領域の、基準となる最小幅です。",
             "일치 개수를 표시하는 영역의 기본 최소 너비입니다."]);
        Add("find.iconSizeDip",
            ["Find button icon size", "検索ボタンのアイコンサイズ", "찾기 버튼 아이콘 크기"],
            ["Icon side length for previous match, next match and close buttons.",
             "前の一致、次の一致、閉じるボタンのアイコンの一辺の長さです。",
             "이전 일치 항목, 다음 일치 항목 및 닫기 버튼 아이콘의 한 변 길이입니다."]);
        Add("find.topOffsetDip",
            ["Find panel vertical offset", "検索パネルの縦位置差", "찾기 패널 세로 위치 차이"],
            ["Default position offset from the bottom edge of the title bar.",
             "検索パネルの既定位置とタイトルバー下端との縦方向の距離です。",
             "제목 표시줄 아래쪽 가장자리에서 찾기 패널 기본 위치까지의 세로 거리입니다."]);
        Add("find.minLeftDip",
            ["Find panel minimum left position", "検索パネルの最小左位置", "찾기 패널 최소 왼쪽 위치"],
            ["Minimum distance of the default find panel position from the paper's left edge.",
             "検索パネルの既定位置を決めるときに、紙片の左端から確保する最小距離です。",
             "찾기 패널 기본 위치와 메모 왼쪽 가장자리 사이의 최소 거리입니다."]);
        Add("find.rightOverhangDip",
            ["Find panel right overhang", "検索パネルの右側への張り出し", "찾기 패널 오른쪽 돌출 거리"],
            ["How far the default find panel extends beyond the paper's right edge.",
             "検索パネルの既定位置が紙片の右端から外側へ張り出す距離です。",
             "기본 찾기 패널이 메모 오른쪽 가장자리 밖으로 나오는 거리입니다."]);

        // Settings navigation and window geometry.
        Add("settings.sidebar.baseWidthDip",
            ["Settings sidebar base width", "設定サイドバーの基本幅", "설정 사이드바 기본 너비"],
            ["Base lower bound used when calculating the settings navigation width.",
             "設定ナビゲーションの幅を計算するときの基準となる下限です。",
             "설정 탐색 영역의 너비를 계산할 때 사용하는 기본 하한입니다."]);
        Add("settings.sidebar.maxWidthRatio",
            ["Settings sidebar maximum expansion ratio", "設定サイドバーの最大拡張比率", "설정 사이드바 최대 확장 비율"],
            ["Maximum sidebar width relative to its base width when expanding to fit measured text.",
             "測定した文字に合わせてサイドバーを広げるときの、基本幅に対する最大比率です。",
             "측정한 텍스트에 맞춰 사이드바를 넓힐 때 기본 너비에 대한 최대 비율입니다."]);
        Add("settings.sidebar.widthDip",
            ["Final settings sidebar width", "設定サイドバーの最終幅", "설정 사이드바 최종 너비"],
            ["Override the settings sidebar width after measuring translated text and applying limits.",
             "翻訳済みの文字を測定し、上下限を適用して得た設定サイドバーの幅を上書きします。",
             "번역된 텍스트를 측정하고 범위를 제한한 후의 설정 사이드바 너비를 재정의합니다."], dynamic: true);
        Add("settings.sidebar.rowHeightDip",
            ["Settings navigation row height", "設定ナビゲーション行の高さ", "설정 탐색 행 높이"],
            ["Fixed height of navigation items on the left side of the settings window.",
             "設定ウィンドウ左側のナビゲーション項目に使う固定の高さです。",
             "설정 창 왼쪽 탐색 항목의 고정 높이입니다."]);
        Add("settings.window.baseWidthDip",
            ["Settings window base width", "設定ウィンドウの基本幅", "설정 창 기본 너비"],
            ["Target settings window width before adding the sidebar's extra width.",
             "サイドバーの拡張分を加える前の、設定ウィンドウの目標幅です。",
             "사이드바 확장분을 더하기 전의 설정 창 목표 너비입니다."]);
        Add("settings.window.baseHeightDip",
            ["Settings window base height", "設定ウィンドウの基本高さ", "설정 창 기본 높이"],
            ["Normal target height of the settings window.",
             "設定ウィンドウの通常の目標高さです。",
             "설정 창의 일반적인 목표 높이입니다."]);
        Add("settings.window.minWidthDip",
            ["Settings window reference minimum width", "設定ウィンドウの基準最小幅", "설정 창 기준 최소 너비"],
            ["Reference lower bound when calculating a settings window width that fits the screen.",
             "画面内に収まる設定ウィンドウの幅を計算するときに参照する下限です。",
             "화면 안에 들어가는 설정 창 너비를 계산할 때 참조하는 하한입니다."]);
        Add("settings.window.minHeightDip",
            ["Settings window reference minimum height", "設定ウィンドウの基準最小高さ", "설정 창 기준 최소 높이"],
            ["Reference lower bound when calculating a settings window height that fits the screen.",
             "画面内に収まる設定ウィンドウの高さを計算するときに参照する下限です。",
             "화면 안에 들어가는 설정 창 높이를 계산할 때 참조하는 하한입니다."]);
        Add("settings.window.preferredMarginDip",
            ["Settings window preferred space reserve", "設定ウィンドウの目標余白", "설정 창 권장 여유 공간"],
            ["Total space subtracted from the available screen area when calculating the target settings window size.",
             "設定ウィンドウの目標サイズを計算するとき、画面の使用可能領域から差し引く余白の合計です。",
             "설정 창 목표 크기를 계산할 때 화면의 사용 가능 영역에서 빼는 총여백입니다."]);
        Add("settings.window.boundaryMarginDip",
            ["Settings window boundary space reserve", "設定ウィンドウの境界余白", "설정 창 경계 여유 공간"],
            ["Total space subtracted from the available screen area when applying the final screen bounds.",
             "最終的に画面内へ収めるとき、画面の使用可能領域から差し引く余白の合計です。",
             "최종적으로 화면 안에 맞출 때 사용 가능 영역에서 빼는 총여백입니다."]);

        // Todo path buttons and reminder controls.
        Add("todo.pathLink.multilineMinWidthDip",
            ["Multiline todo path button minimum width", "複数行 ToDo のパスボタンの最小幅", "여러 줄 할 일 경로 버튼 최소 너비"],
            ["Base minimum width of the path-name button in a multiline todo.",
             "複数行の ToDo に表示するパス名ボタンの、基準となる最小幅です。",
             "여러 줄 할 일의 경로 이름 버튼에 사용하는 기본 최소 너비입니다."]);
        Add("todo.pathLink.singleLineMinWidthDip",
            ["Single-line todo path button minimum width", "1行 ToDo のパスボタンの最小幅", "한 줄 할 일 경로 버튼 최소 너비"],
            ["Base minimum width of the path-name button in a single-line todo.",
             "1行の ToDo に表示するパス名ボタンの、基準となる最小幅です。",
             "한 줄 할 일의 경로 이름 버튼에 사용하는 기본 최소 너비입니다."]);
        Add("todo.pathLink.multilineWidthRatio",
            ["Multiline todo path button width ratio", "複数行 ToDo のパスボタンの幅比率", "여러 줄 할 일 경로 버튼 너비 비율"],
            ["Path button width factor relative to the checkbox column width in multiline todos.",
             "複数行 ToDo のパスボタン幅を、チェック列の幅に対する倍率で指定します。",
             "여러 줄 할 일에서 체크박스 열 너비에 대한 경로 버튼 너비의 배율입니다."]);
        Add("todo.pathLink.singleLineWidthRatio",
            ["Single-line todo path button width ratio", "1行 ToDo のパスボタンの幅比率", "한 줄 할 일 경로 버튼 너비 비율"],
            ["Path button width factor relative to the checkbox column width in single-line todos.",
             "1行 ToDo のパスボタン幅を、チェック列の幅に対する倍率で指定します。",
             "한 줄 할 일에서 체크박스 열 너비에 대한 경로 버튼 너비의 배율입니다."]);
        Add("todo.pathLink.namePaddingDip",
            ["Path name measurement padding", "パス名測定時の追加余白", "경로 이름 측정 여백"],
            ["Total space added to the measured text width when the full path name may be shown.",
             "パス名の全体表示を許可するとき、測定した文字幅に加える余白の合計です。",
             "경로 이름 전체 표시를 허용할 때 측정한 텍스트 너비에 더하는 총여백입니다."]);
        Add("todo.pathLink.widthDip",
            ["Final todo path button width", "ToDo パスボタンの最終幅", "할 일 경로 버튼 최종 너비"],
            ["Override the path button width calculated from name, icon and todo row width rules.",
             "名前、アイコン、ToDo 行の幅の規則から計算したパスボタン幅を上書きします。",
             "이름, 아이콘 및 할 일 행 너비 규칙으로 계산한 경로 버튼 너비를 재정의합니다."], dynamic: true);
        Add("reminder.ui.iconOpacity",
            ["Reminder icon idle opacity", "リマインダーアイコンの通常時不透明度", "알림 아이콘 대기 불투명도"],
            ["Reminder settings icon opacity when the pointer is not over it.",
             "ポインターが重なっていないときの、リマインダー設定アイコンの不透明度です。",
             "포인터가 위에 없을 때 알림 설정 아이콘의 불투명도입니다."]);
        Add("reminder.ui.pressedOpacity",
            ["Reminder icon pressed opacity", "リマインダーアイコンの押下時不透明度", "알림 아이콘 누름 불투명도"],
            ["Reminder settings icon opacity while pressed.",
             "リマインダー設定アイコンを押しているときの不透明度です。",
             "알림 설정 아이콘을 누르고 있을 때의 불투명도입니다."]);
        Add("reminder.ui.minIconFontDip",
            ["Reminder icon minimum font size", "リマインダーアイコンの最小文字サイズ", "알림 아이콘 최소 글꼴 크기"],
            ["Base lower bound when calculating the reminder icon font size from the text size.",
             "本文の文字サイズからリマインダーアイコンの文字サイズを求めるときの、基準となる下限です。",
             "본문 글꼴 크기로 알림 아이콘 글꼴 크기를 계산할 때의 기본 하한입니다."]);
        Add("reminder.ui.iconFontOffsetDip",
            ["Reminder icon font size reduction", "リマインダーアイコンの文字縮小量", "알림 아이콘 글꼴 크기 감소량"],
            ["Base amount subtracted from the todo text font size for the reminder icon.",
             "リマインダーアイコンの文字サイズを求めるとき、ToDo 本文の文字サイズから差し引く基準値です。",
             "알림 아이콘 글꼴 크기를 구할 때 할 일 본문 글꼴 크기에서 빼는 기본값입니다."]);
        Add("reminder.ui.minIconWidthDip",
            ["Reminder icon minimum width", "リマインダーアイコンの最小幅", "알림 아이콘 최소 너비"],
            ["Base minimum width of the reminder settings button.",
             "リマインダー設定ボタンの、基準となる最小幅です。",
             "알림 설정 버튼의 기본 최소 너비입니다."]);
        Add("reminder.ui.iconWidthOffsetDip",
            ["Reminder icon width reduction", "リマインダーアイコンの幅縮小量", "알림 아이콘 너비 감소량"],
            ["Base amount subtracted from the checkbox column width for the reminder settings button.",
             "リマインダー設定ボタンの幅を求めるとき、チェック列の幅から差し引く基準値です。",
             "알림 설정 버튼 너비를 구할 때 체크박스 열 너비에서 빼는 기본값입니다."]);
        Add("reminder.ui.minCountdownFontDip",
            ["Reminder countdown minimum font size", "リマインダーカウントダウンの最小文字サイズ", "알림 카운트다운 최소 글꼴 크기"],
            ["Base lower bound when calculating the countdown font size from the text size.",
             "本文の文字サイズからカウントダウンの文字サイズを求めるときの、基準となる下限です。",
             "본문 글꼴 크기로 카운트다운 글꼴 크기를 계산할 때의 기본 하한입니다."]);
        Add("reminder.ui.countdownFontOffsetDip",
            ["Reminder countdown font size reduction", "リマインダーカウントダウンの文字縮小量", "알림 카운트다운 글꼴 크기 감소량"],
            ["Base amount subtracted from the text font size for the countdown.",
             "カウントダウンの文字サイズを求めるとき、本文の文字サイズから差し引く基準値です。",
             "카운트다운 글꼴 크기를 구할 때 본문 글꼴 크기에서 빼는 기본값입니다."]);
        Add("reminder.ui.minCountdownWidthDip",
            ["Reminder countdown minimum width", "リマインダーカウントダウンの最小幅", "알림 카운트다운 최소 너비"],
            ["Base minimum width of the countdown area.",
             "カウントダウン領域の、基準となる最小幅です。",
             "카운트다운 영역의 기본 최소 너비입니다."]);
    }
}
