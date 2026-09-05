(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.StarI18n = api;
})(globalThis, function () {
  'use strict';
  // Each row is zh / en / ja / ko. Keep all four variants together when adding UI copy.
  const rows = {
    name: ['星笺', 'Starpaper', '星のノート', '별빛 노트'],
    tagline: ['让灵感相连，让行动生长。', 'Connect ideas. Give plans a little life.', 'ひらめきをつなぎ、行動を育てる。', '생각을 잇고, 계획을 키워요.'],
    map: ['知识星图', 'Constellation', '知識の星図', '지식 별자리'],
    cards: ['待办图鉴', 'Illustrated tasks', 'タスク図鑑', '할 일 도감'],
    local: ['离线 · 本地', 'Offline · local', 'オフライン · ローカル', '오프라인 · 로컬'],
    demo: ['演示数据 · 不会修改 PaperTodo', 'Demo · no PaperTodo data is changed', 'デモ · PaperTodo のデータは変更されません', '데모 · PaperTodo 데이터는 변경되지 않습니다'],
    sources: ['选择纸片', 'Choose papers', '用紙を選択', '용지 선택'],
    sourceHint: ['只读取选中纸片的正文；标记完成会修改原待办。取消来源选择不会删除原始数据。', 'Only selected papers are read. Completing a task changes the original. Deselecting never deletes it.', '選択した用紙だけを読み取ります。完了操作は元のタスクに反映されます。選択を外しても削除されません。', '선택한 용지만 읽습니다. 완료 상태는 원본에 반영됩니다. 선택을 해제해도 삭제되지 않습니다.'],
    newNote: ['新建知识', 'New idea', '知識を追加', '지식 추가'],
    newTodo: ['新建待办', 'New task', 'タスクを追加', '할 일 추가'],
    search: ['搜索标题与内容…', 'Search titles and text…', 'タイトル・内容を検索…', '제목과 내용 검색…'],
    all: ['全部', 'All', 'すべて', '전체'], open: ['未完成', 'Open', '未完了', '미완료'], done: ['已完成', 'Done', '完了', '완료'],
    undo: ['撤销星图编辑（不撤销原待办）', 'Undo board edit (not host tasks)', '星図の編集を元に戻す（元のタスクは対象外）', '별자리 편집 실행 취소 (원본 할 일 제외)'],
    redo: ['重做星图编辑', 'Redo board edit', '星図の編集をやり直す', '별자리 편집 다시 실행'],
    more: ['更多', 'More', 'その他', '더 보기'], fit: ['全图', 'Fit all', '全体表示', '전체 보기'],
    arrange: ['整理布局', 'Arrange', '整列', '정렬'],
    arrangeHint: ['恢复自动布局？知识和连线都会保留，布局可撤销。', 'Restore the automatic layout? Ideas and links stay intact. You can undo this.', '自動配置に戻しますか？知識と関連は残り、操作を元に戻せます。', '자동 배치로 되돌릴까요? 지식과 연결은 유지되며 실행 취소할 수 있습니다.'],
    export: ['导出备份', 'Export backup', 'バックアップを保存', '백업 내보내기'],
    import: ['导入备份', 'Import backup', 'バックアップを読み込む', '백업 가져오기'],
    exportSvg: ['导出星图 SVG', 'Export map as SVG', '星図を SVG で保存', '별자리를 SVG로 저장'],
    exportCard: ['导出插画卡片', 'Export illustrated card', 'イラストカードを保存', '일러스트 카드 저장'],
    help: ['使用说明', 'Guide', '使い方', '사용 안내'],
    helpText: ['拖动节点摆放，拖动画布平移，滚轮缩放。选中节点后 Shift+单击另一节点可快速连线，也可用“关联”填写关系。正文中的 [[知识标题]] 会生成虚线链接；同名知识不自动猜测。双击空白处新建知识。Ctrl+Z / Ctrl+Y 只处理星图本地编辑。插画显示在本插件，不改变原待办行。', 'Drag nodes to arrange; drag the canvas to pan; use the wheel to zoom. Shift-click another node to connect, or use Link to name the relationship. [[Idea title]] creates a dashed reference; duplicate titles are not guessed. Double-click the canvas to add an idea. Ctrl+Z / Ctrl+Y only affect local board edits. Illustrations live here, not in the original task rows.', 'ノードのドラッグで配置、余白のドラッグで移動、ホイールで拡大縮小。別のノードを Shift+クリックすると関連を追加できます。「関連」で名前も付けられます。[[知識のタイトル]] は点線の参照になります。同名の場合は推測しません。余白をダブルクリックで知識を追加。Ctrl+Z / Ctrl+Y は星図のみが対象です。イラストはこのプラグイン内に表示されます。', '노드를 끌어 배치하고, 배경을 끌어 이동하며, 휠로 확대합니다. 다른 노드를 Shift+클릭하면 연결할 수 있고, 연결 메뉴에서 관계 이름을 지정합니다. [[지식 제목]]은 점선 참조를 만듭니다. 중복 제목은 추측하지 않습니다. 배경을 두 번 클릭하면 지식을 추가합니다. Ctrl+Z / Ctrl+Y는 별자리 편집만 되돌립니다. 일러스트는 이 플러그인 안에만 표시됩니다.'],
    knowledge: ['知识', 'Ideas', '知識', '지식'], tasks: ['待办', 'Tasks', 'タスク', '할 일'], papers: ['纸片', 'Papers', '用紙', '용지'],
    link: ['关联', 'Link', '関連', '연결'], relation: ['关系说明（可选）', 'Relationship (optional)', '関連の説明（任意）', '관계 설명 (선택)'],
    target: ['目标节点', 'Target node', '関連先', '연결 대상'], noTarget: ['先再创建一个知识节点或选择待办纸。', 'Add another idea or choose a task paper first.', '別の知識を追加するか、タスクの用紙を選択してください。', '다른 지식을 추가하거나 할 일 용지를 먼저 선택하세요.'],
    related: ['相邻节点', 'Connections', '関連するノード', '연결된 노드'],
    focus: ['只看相邻节点', 'Focus on connections', '関連ノードに絞る', '연결된 노드만 보기'],
    unfocus: ['退出聚焦', 'Show all nodes', '絞り込みを解除', '모든 노드 보기'],
    wiki: ['[[标题]] 自动关联', 'Link [[titles]] automatically', '[[タイトル]] を自動参照', '[[제목]] 자동 연결'],
    manual: ['手动关联', 'Manual link', '手動の関連', '수동 연결'], reference: ['正文引用', 'Text reference', '本文の参照', '본문 참조'],
    contains: ['所属纸片', 'Paper membership', '所属する用紙', '소속 용지'],
    unlink: ['移除连线', 'Remove link', '関連を解除', '연결 제거'],
    title: ['标题', 'Title', 'タイトル', '제목'], body: ['内容 · 支持 [[知识标题]]', 'Text · supports [[Idea title]]', '内容 · [[知識のタイトル]] に対応', '내용 · [[지식 제목]] 지원'],
    edit: ['编辑', 'Edit', '編集', '편집'], save: ['保存', 'Save', '保存', '저장'], cancel: ['取消', 'Cancel', 'キャンセル', '취소'], close: ['关闭', 'Close', '閉じる', '닫기'],
    remove: ['删除知识', 'Delete idea', '知識を削除', '지식 삭제'],
    removeHint: ['仅删除这个知识节点及其连线，不删除任何原始待办。可以撤销。', 'Delete this idea and its links only. Original tasks are not deleted. You can undo this.', 'この知識と関連のみ削除します。元のタスクは削除されず、操作を元に戻せます。', '이 지식과 연결만 삭제합니다. 원본 할 일은 삭제하지 않으며 실행 취소할 수 있습니다.'],
    complete: ['标记完成', 'Mark done', '完了にする', '완료로 표시'], reopen: ['恢复未完成', 'Mark open', '未完了に戻す', '미완료로 표시'],
    illustration: ['插画', 'Illustration', 'イラスト', '일러스트'],
    image: ['使用本地图片', 'Use local image', 'ローカル画像を使う', '로컬 이미지 사용'],
    imageHint: ['支持 PNG / JPEG / WebP / GIF。也可粘贴或拖入图片；自动缩小并保存副本，不依赖原文件。', 'PNG / JPEG / WebP / GIF. You can also paste or drop an image. A resized copy is stored, independent of the original file.', 'PNG / JPEG / WebP / GIF に対応。貼り付け・ドロップも可能。縮小したコピーを保存するため元ファイルは不要です。', 'PNG / JPEG / WebP / GIF 지원. 붙여넣기나 끌어넣기도 가능합니다. 축소한 사본을 저장하므로 원본 파일이 필요하지 않습니다.'],
    resetCover: ['恢复自动插画', 'Restore automatic art', '自動イラストに戻す', '자동 일러스트 복원'],
    orbit: ['星轨', 'Orbit', '星の軌道', '별의 궤도'], read: ['书页', 'Reading', '読書', '독서'], code: ['代码', 'Code', 'コード', '코드'],
    grow: ['生长', 'Growth', '成長', '성장'], travel: ['远行', 'Travel', '旅', '여행'], health: ['活力', 'Wellbeing', '健康', '활력'], create: ['创作', 'Create', '創作', '창작'], focusScene: ['专注', 'Focus', '集中', '집중'],
    emptyTitle: ['一颗想法，就能点亮星图。', 'One idea starts a constellation.', 'ひとつの知識から、星図が始まる。', '생각 하나가 별자리의 시작입니다.'],
    emptyText: ['创建一个知识节点，或选取现有待办、笔记纸。没有数据会被自动导入或修改。', 'Add an idea or choose existing task or note papers. Nothing is imported or changed automatically.', '知識を追加するか、既存の用紙を選択してください。自動で読み込んだり変更したりしません。', '지식을 추가하거나 기존 할 일 용지를 선택하세요. 자동으로 가져오거나 수정하지 않습니다.'],
    noTasks: ['这里还没有待办', 'No tasks here yet', 'タスクはまだありません', '아직 할 일이 없습니다'],
    noMatch: ['没有匹配的节点', 'No matching nodes', '一致するノードがありません', '일치하는 노드가 없습니다'],
    noPaper: ['请先在 PaperTodo 创建一张待办纸，再点刷新。', 'Create a task paper in PaperTodo, then refresh.', 'PaperTodo でタスク用紙を作成してから更新してください。', 'PaperTodo에서 할 일 용지를 만든 후 새로고침하세요.'],
    refresh: ['刷新', 'Refresh', '更新', '새로고침'], loading: ['正在读取…', 'Loading…', '読み込み中…', '불러오는 중…'],
    stale: ['读取失败；保留上次成功读取的内容。请刷新后再操作。', 'Read failed; the last successful snapshot is kept. Refresh before editing host tasks.', '読み込み失敗。前回の内容を保持しています。更新してから元のタスクを操作してください。', '읽기 실패. 마지막으로 읽은 내용을 유지합니다. 새로고침 후 원본 할 일을 수정하세요.'],
    unavailable: ['引用暂不可用', 'Reference unavailable', '参照を利用できません', '참조를 사용할 수 없음'],
    missingHint: ['原始对象可能已删除、自动清理或不再可读取。连线与插图不会被自动抹掉；这不是一条可编辑的原始待办。', 'The original may be deleted, auto-cleared, or unreadable. Links and art are kept. This is not an editable original task.', '元の項目が削除・自動整理されたか、読み取れない可能性があります。関連と画像は保持されます。この参照から元のタスクは編集できません。', '원본이 삭제·자동 정리되었거나 읽을 수 없을 수 있습니다. 연결과 그림은 유지됩니다. 이 참조는 편집 가능한 원본 할 일이 아닙니다.'],
    waiting: ['请在 PaperTodo 中加载插件；浏览器预览请打开 preview.html。', 'Load this plugin in PaperTodo. For a browser demo, open preview.html.', 'PaperTodo で読み込んでください。ブラウザーのデモは preview.html を開きます。', 'PaperTodo에서 플러그인을 불러오세요. 브라우저 데모는 preview.html을 여세요.'],
    stateVersion: ['数据版本不兼容，已停止写入。请先导出原始数据。', 'Incompatible data version; writes are blocked. Export the original data first.', 'データのバージョンが非対応のため書き込みを停止しました。まず元データを保存してください。', '호환되지 않는 데이터 버전으로 쓰기를 중단했습니다. 원본 데이터를 먼저 내보내세요.'],
    invalidState: ['数据格式异常，未覆盖原始数据。', 'Invalid document; the original has not been overwritten.', 'データ形式が不正です。元データは上書きしていません。', '데이터 형식이 잘못되었습니다. 원본은 덮어쓰지 않았습니다.'],
    invalidImage: ['不支持这份图片数据。请选择可解码的本地位图。', 'Unsupported image data. Choose a decodable local raster image.', 'この画像には対応していません。読み込めるローカル画像を選択してください。', '지원하지 않는 이미지입니다. 읽을 수 있는 로컬 비트맵을 선택하세요.'],
    capacity: ['超出宿主每张纸 10 MiB 的状态容量，未提交这次修改。可导出备份后移除部分图片。', 'This exceeds the host’s 10 MiB per-paper state limit. The change was not submitted. Back up, then remove some images.', '宿主の用紙あたり 10 MiB 制限を超えるため変更していません。バックアップ後、画像を減らしてください。', '호스트의 용지당 10 MiB 제한을 초과하여 변경하지 않았습니다. 백업한 뒤 일부 이미지를 제거하세요.'],
    importHint: ['将替换这张星图的知识、连线和插图；不会创建、删除或覆盖原始待办。其他设备上的原始待办 ID 可能无法对应。可撤销。', 'Replace this board’s ideas, links and art. Original tasks are not created, deleted or overwritten. Task IDs may not resolve on another installation. Undo is available.', 'この星図の知識・関連・画像を置き換えます。元のタスクを作成・削除・上書きしません。別の環境ではタスク ID が一致しない場合があります。元に戻せます。', '이 별자리의 지식·연결·그림을 교체합니다. 원본 할 일은 생성·삭제·덮어쓰지 않습니다. 다른 환경에서는 할 일 ID가 일치하지 않을 수 있습니다. 실행 취소가 가능합니다.'],
    operationFailed: ['操作未确认。请先刷新核对原待办，再决定是否重试；不会自动重发。', 'Operation not confirmed. Refresh and check the original before retrying. It will not be resent automatically.', '操作を確認できませんでした。更新して元のタスクを確認してから再試行してください。自動再送はしません。', '작업 결과를 확인하지 못했습니다. 새로고침하여 원본을 확인한 뒤 재시도하세요. 자동으로 재전송하지 않습니다.'],
    conflict: ['内容已在其他位置变化。请重新打开编辑器，避免覆盖更新。', 'Content changed elsewhere. Reopen the editor to avoid overwriting it.', '他の場所で内容が変更されました。上書きを避けるため編集画面を開き直してください。', '다른 위치에서 내용이 변경되었습니다. 덮어쓰지 않도록 편집기를 다시 여세요.'],
    selfLink: ['请选择另一个节点。', 'Choose a different node.', '別のノードを選択してください。', '다른 노드를 선택하세요.'],
    selectedImage: ['先选中一个待办或知识节点，再放入图片。', 'Select a task or idea before adding an image.', 'タスクまたは知識を選択してから画像を追加してください。', '이미지를 추가하기 전에 할 일이나 지식을 선택하세요.'],
    imageTooLarge: ['图片过大，请先缩小到 20 MiB / 3200 万像素以内。', 'Resize the image below 20 MiB / 32 megapixels first.', '画像を 20 MiB / 3200 万画素以下に縮小してください。', '먼저 이미지를 20 MiB / 3200만 화소 이하로 줄이세요.'],
    savedByHost: ['编辑会立即提交宿主保存；备份不包含原始待办或引用笔记正文。', 'Edits are submitted to the host immediately. Backups do not contain original task or referenced note text.', '編集はすぐに宿主へ保存要求します。バックアップに元のタスクや参照メモの本文は含まれません。', '편집은 즉시 호스트에 저장 요청됩니다. 백업에는 원본 할 일이나 참조 메모 본문이 포함되지 않습니다.'],
    previewOnly: ['演示只在当前页面内存中运行，刷新会重置。', 'The demo is memory-only and resets when reloaded.', 'デモはメモリ内のみで動作し、再読み込みすると初期化されます。', '데모는 메모리에서만 동작하며 새로고침하면 초기화됩니다.'],
    hint: ['拖动 · 滚轮缩放 · Shift+单击连线', 'Drag · wheel to zoom · Shift-click to link', 'ドラッグ · ホイールで拡大 · Shift+クリックで関連', '끌기 · 휠로 확대 · Shift+클릭으로 연결'],
    selected: ['已选', 'Selected', '選択済み', '선택됨'],
    untitled: ['未命名纸片', 'Untitled paper', '無題の用紙', '제목 없는 용지'],
    clearFilter: ['清除筛选', 'Clear filters', '絞り込みを解除', '필터 해제'],
    readOnlyNote: ['实时引用的笔记正文。请在原笔记纸编辑；这里可配图和连线。', 'Live note reference. Edit the original note; add art and links here.', '元のメモの参照です。本文は元の用紙で編集し、ここでは画像や関連を追加します。', '실시간 메모 참조입니다. 원본 메모에서 편집하고 여기에서는 그림과 연결을 추가하세요.'],
    zoomIn: ['放大', 'Zoom in', '拡大', '확대'], zoomOut: ['缩小', 'Zoom out', '縮小', '축소'],
    protocolData: ['宿主返回的数据格式不符合 API 2.1。', 'The host response does not match API 2.1.', '宿主の応答が API 2.1 の形式と一致しません。', '호스트 응답이 API 2.1 형식과 다릅니다.']
  };
  const languages = ['zh', 'en', 'ja', 'ko'];
  function language(value, system = 'zh') {
    const requested = !value || value === 'auto' ? system : value;
    const prefix = String(requested).slice(0, 2).toLowerCase();
    return languages.includes(prefix) ? prefix : 'en';
  }
  function translator(locale) {
    const index = languages.indexOf(language(locale));
    return key => rows[key]?.[index] ?? key;
  }
  return Object.freeze({ rows, languages, language, translator });
});
