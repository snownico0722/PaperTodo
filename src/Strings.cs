using System.Globalization;
using System.Resources;
using System.Collections.Generic;

namespace PaperTodo;

public static class Strings
{
    private static readonly ResourceManager Manager = new("PaperTodo.Resources.Strings", typeof(Strings).Assembly);

    private static readonly IReadOnlyDictionary<string, string[]> Supplemental =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["SettingsUiLanguage"] = ["界面语言", "Interface language", "表示言語", "인터페이스 언어"],
            ["TipSettingsUiLanguage"] = ["选择界面语言；重启 PaperTodo 后生效。", "Choose the interface language; restart PaperTodo to apply.", "表示言語を選択します。PaperTodo の再起動後に反映されます。", "인터페이스 언어를 선택합니다. PaperTodo를 다시 시작하면 적용됩니다."],
            ["UiLanguageSystem"] = ["跟随系统", "Follow system", "システムに従う", "시스템 설정 따름"],
            ["UiLanguageZhHans"] = ["简体中文", "简体中文", "简体中文", "简体中文"],
            ["UiLanguageEnglish"] = ["English", "English", "English", "English"],
            ["UiLanguageJapanese"] = ["日本語", "日本語", "日本語", "日本語"],
            ["UiLanguageKorean"] = ["한국어", "한국어", "한국어", "한국어"],
            ["SettingsDistinguishNumpadShortcutDigits"] = ["区分小键盘数字键", "Distinguish numpad digits", "テンキー数字を区別", "숫자 키패드 숫자 구분"],
            ["TipSettingsDistinguishNumpadShortcutDigits"] = ["开启后数字键与小键盘数字键可分别注册；关闭后两者混合响应，但不会修改已保存的快捷键。快速启动侧边胶囊不受影响。", "When enabled, number-row and numpad digits can be registered separately. When disabled, either key triggers the stored binding without rewriting it. Edge quick-launch sequences are unchanged.", "オンでは数字列とテンキーを別々に登録できます。オフでは保存値を書き換えず両方で反応します。端のクイック起動シーケンスには影響しません。", "켜면 숫자열과 숫자 키패드를 따로 등록할 수 있습니다. 끄면 저장된 값을 바꾸지 않고 둘 다 반응합니다. 가장자리 빠른 실행 시퀀스에는 영향을 주지 않습니다."],
            ["ShortcutNumpadModeConflictTitle"] = ["小键盘快捷键冲突", "Numpad shortcut conflict", "テンキーショートカットの競合", "숫자 키패드 단축키 충돌"],
            ["ShortcutNumpadModeConflictMessage"] = ["无法切换小键盘模式：现有快捷键存在数字键/小键盘冲突，或混合响应所需的组合已被其他程序占用。现有快捷键不会被修改。", "The numpad mode could not be changed because existing bindings conflict across number-row/numpad digits, or a required mixed-mode combination is already owned by another app. Existing bindings were not changed.", "既存の数字列/テンキー割り当てが競合しているか、混合応答に必要な組み合わせを他のアプリが使用しているため切り替えできません。既存の割り当ては変更されません。", "기존 숫자열/숫자 키패드 바인딩이 충돌하거나 혼합 응답에 필요한 조합을 다른 앱이 사용 중이라 모드를 변경할 수 없습니다. 기존 바인딩은 변경되지 않습니다."],
            ["LabsAdvancedShortcuts"] = ["高级快捷键", "Advanced shortcuts", "高度なショートカット", "고급 바로 가기"],
            ["LabsFocusInactiveGroup"] = ["失焦", "Inactive", "非アクティブ", "비활성"],
            ["LabsFocusRestingGroup"] = ["静置", "Resting", "静置", "유휴"],
            ["LabsDockedCapsuleBehavior"] = ["贴边胶囊", "Docked capsules", "端のカプセル", "가장자리 캡슐"],
            ["LabsFocusRestingOpacity"] = ["静置时自动半透明", "Fade while resting", "静置時に自動で半透明", "유휴 시 자동 반투명"],
            ["TipLabsFocusRestingOpacity"] = ["仅影响普通胶囊和贴边胶囊的静置状态；悬停、激活或拖动时恢复为不透明。默认不影响主胶囊，可在下方选择同时应用。", "Affects ordinary and docked capsules while resting; hover, activation, or dragging restores full opacity. The master capsule is excluded by default and can be included below.", "通常カプセルと端のカプセルの静置時だけ半透明にし、ホバー・アクティブ化・ドラッグで不透明に戻します。マスターカプセルは既定では対象外で、下の項目から含められます。", "일반 캡슐과 가장자리 캡슐의 유휴 상태에만 적용되며, 호버·활성화·드래그 시 불투명하게 돌아옵니다. 마스터 캡슐은 기본적으로 제외되며 아래에서 포함할 수 있습니다."],
            ["LabsFocusRestingIncludeMaster"] = ["主胶囊也透明", "Include master capsule", "マスターカプセルにも適用", "마스터 캡슐도 투명"],
            ["LabsFocusRestingAlways"] = ["激活时也保持透明", "Keep transparent while active", "操作中も透明を維持", "활성 상태에서도 투명 유지"],
            ["TipLabsWindowTetheringFixed"] = ["展开纸片后，把顶栏的窗口绑定按钮拖到其他软件窗口并松手，纸片会贴边并跟随移动；折叠与重新展开会保持同一绑定。目标窗口最小化或隐藏时纸片会暂时隐藏并等待恢复；等待期间手动显示纸片会解除绑定。", "After expanding a paper, drag its top-bar window-binding button onto another app window and release to attach and follow it. Folding and expanding keep the same binding. If the target is minimized or hidden, the paper waits hidden and returns with the target; explicitly showing it while waiting detaches the binding.", "紙を展開した後、上部バーのウィンドウ連携ボタンを別アプリのウィンドウへドラッグして離すと、そのウィンドウに沿って追従します。折りたたみと再展開でも同じ連携を維持します。対象が最小化または非表示になると紙も一時的に隠れて復帰を待ち、待機中に手動で表示すると連携を解除します。", "메모를 펼친 뒤 상단 바의 창 연결 버튼을 다른 앱 창으로 드래그해 놓으면 해당 창 가장자리에 붙어 따라갑니다. 접고 다시 펼쳐도 같은 연결을 유지합니다. 대상 창이 최소화되거나 숨겨지면 메모도 임시로 숨은 채 복원을 기다리며, 대기 중 수동으로 표시하면 연결을 해제합니다."],
            ["LabsInteractionLock"] = ["交互锁定", "Interaction lock", "操作ロック", "상호 작용 잠금"],
            ["LabsLockAllPapers"] = ["锁定全部便签", "Lock all papers", "すべての紙をロック", "모든 메모 잠금"],
            ["TipLabsLockAllPapers"] = ["切换全部普通与插件便签的交互锁定。", "Toggle interaction lock for all regular and plugin papers.", "通常およびプラグインの紙をすべてロックします。", "일반 및 플러그인 메모를 모두 잠급니다."],
            ["LabsAllowLockIconUnlock"] = ["允许点击锁头解锁", "Allow lock icon to unlock", "ロックアイコンで解除を許可", "잠금 아이콘으로 해제 허용"],
            ["TipLabsAllowLockIconUnlock"] = ["关闭后锁头仅作提示，只能通过快捷键解锁。", "When off, the lock is only an indicator and the shortcut is required to unlock.", "オフの場合、ロックは表示のみで解除にはショートカットが必要です。", "끄면 잠금은 표시만 하며 단축키로만 해제할 수 있습니다."],
            ["LabsUnlockAllPapers"] = ["解锁全部便签", "Unlock all papers", "すべての紙のロックを解除", "모든 메모 잠금 해제"],
            ["LabsShortcutTransparency"] = ["快捷透明度", "Shortcut transparency", "ショートカット透明度", "단축키 투명도"],
            ["LabsShortcutOpacityLevel"] = ["透明度值", "Opacity level", "透明度", "투명도 값"],
            ["LabsAllPapersTransparent"] = ["切换全部纸片透明", "Toggle all papers transparent", "すべての紙の透明を切替", "모든 메모 투명 전환"],
            ["TipLabsAllPapersTransparent"] = ["部分透明时会先统一设为透明；全部已透明时再次按下才取消。", "If only some are transparent, all become transparent; press again only when all are transparent to cancel.", "一部だけ透明な場合はすべて透明にし、全て透明な場合のみ再度押すと解除します。", "일부만 투명하면 모두 투명하게 만들고, 모두 투명할 때 다시 눌러 해제합니다."],
            ["LabsAllCapsulesTransparent"] = ["切换全部胶囊透明", "Toggle all capsules transparent", "すべてのカプセルの透明を切替", "모든 캡슐 투명 전환"],
            ["TipLabsAllCapsulesTransparent"] = ["显式透明优先于空闲半透明，并统一作用于全部胶囊。", "Explicit transparency overrides idle transparency and applies to all capsules.", "明示的な透明度はアイドル透明度より優先され、全カプセルに適用されます。", "명시적 투명도는 유휴 투명도보다 우선하며 모든 캡슐에 적용됩니다."],
            ["LabsCurrentPaperTransparent"] = ["切换当前焦点纸片透明", "Toggle focused paper transparent", "フォーカス中の紙の透明を切替", "현재 포커스 메모 투명 전환"],
            ["TipLabsCurrentPaperTransparent"] = ["只作用于快捷键触发时拥有焦点的普通或插件纸片。", "Affects only the regular or plugin paper focused when the shortcut fires.", "ショートカット実行時にフォーカス中の通常またはプラグインの紙だけに作用します。", "단축키 실행 시 포커스된 일반 또는 플러그인 메모에만 적용됩니다."],
            ["LabsHideInactiveTopBarButtons"] = ["失焦隐藏顶栏按钮", "Hide inactive top-bar buttons", "非アクティブ時に上部ボタンを隠す", "비활성 상단 버튼 숨기기"],
            ["TipLabsHideInactiveTopBarButtons"] = ["纸片失去焦点时隐藏顶栏操作按钮；悬停或重新激活时显示，并保留原布局空间。", "Hide top-bar action buttons while the paper is inactive; reveal them on hover or activation without changing layout.", "紙が非アクティブな間は上部の操作ボタンを隠し、ホバーまたは再アクティブ化で表示します。レイアウト幅は保持します。", "메모가 비활성일 때 상단 작업 버튼을 숨기고, 마우스를 올리거나 다시 활성화하면 표시합니다. 레이아웃 공간은 유지합니다."],
            ["LabsHideInactiveTitleBar"] = ["失焦隐藏标题栏", "Hide inactive title bar", "非アクティブ時にタイトルバーを隠す", "비활성 제목 표시줄 숨기기"],
            ["TipLabsHideInactiveTitleBar"] = ["普通浮动纸片失焦时从顶部真实收短窗口，正文与底边位置不动；重新激活时向上恢复。最大化、Snap 和深胶囊槽位保持完整标题栏。", "When an ordinary floating paper becomes inactive, physically shorten the window from the top while keeping the body and bottom edge fixed; activation restores it upward. Maximized, snapped, and deep-slot papers keep the full title bar.", "通常のフローティング紙が非アクティブになると、本文と下端の位置を保ったまま上側から実際にウィンドウを縮め、再アクティブ化で上方向に復元します。最大化、スナップ、深いカプセルのスロットでは完全なタイトルバーを維持します。", "일반 플로팅 메모가 비활성화되면 본문과 아래쪽 위치를 유지한 채 위쪽에서 실제 창 높이를 줄이고, 다시 활성화하면 위로 복원합니다. 최대화, 스냅 및 딥 캡슐 슬롯에서는 전체 제목 표시줄을 유지합니다."],
            ["LabsDockedCapsulesNonTopmost"] = ["允许贴边胶囊非置顶", "Allow docked capsules below topmost", "端に固定したカプセルの非最前面を許可", "가장자리 캡슐 비고정 허용"],
            ["TipLabsDockedCapsulesNonTopmost"] = ["开启后贴边胶囊和主胶囊不再保持置顶；展开纸片仍按自身置顶设置。", "When enabled, docked and master capsules no longer stay topmost; expanded papers keep their own topmost setting.", "有効にすると端のカプセルとマスターカプセルは最前面を維持せず、展開した紙は個別設定に従います。", "켜면 가장자리 및 마스터 캡슐이 항상 위를 유지하지 않으며 펼친 메모는 자체 설정을 따릅니다."],
            ["LabsFocusOpacity"] = ["失焦与静止透明", "Inactive and resting transparency", "非アクティブ・静止時の透明度", "비활성·정지 투명도"],
            ["LabsRestingCapsuleOpacityIncludeMaster"] = ["覆盖主胶囊", "Include master capsule", "マスターカプセルにも適用", "마스터 캡슐에도 적용"],
            ["LabsRestingCapsuleOpacityAlways"] = ["无论是否激活都透明", "Keep transparent while active", "操作中も透明を維持", "활성 상태에서도 투명 유지"],
            ["LabsMcpCopyAiSkill"] = ["复制 AI Skill", "Copy AI skill", "AI Skill をコピー", "AI Skill 복사"],
            ["SettingsAutoMoveCompletedTodosToBottom"] = ["已完成待办自动置底", "Move completed todos to bottom", "完了したToDoを下へ移動", "완료된 할 일을 아래로 이동"],
            ["TipAutoMoveCompletedTodosToBottom"] = ["完成待办时移到已完成区域末尾；取消完成时移到未完成区域末尾。开启“自动清除已完成待办”时暂时禁用，但会保留此设置。", "Move a completed todo to the end of the completed group; restoring it moves it to the end of the active group. This is temporarily disabled while auto-clear is on, without forgetting the setting.", "完了時は完了グループの末尾へ、未完了に戻すと未完了グループの末尾へ移動します。完了項目の自動削除中は無効になりますが、設定値は保持されます。", "완료하면 완료 그룹의 끝으로, 완료를 취소하면 미완료 그룹의 끝으로 이동합니다. 완료 항목 자동 삭제가 켜져 있으면 잠시 비활성화되지만 설정은 유지됩니다."],
            ["LabsTodoReminderSoundEnabled"] = ["允许提醒声音", "Play reminder sound", "リマインダー音を鳴らす", "미리 알림 소리 허용"],
            ["TipLabsTodoReminderSoundEnabled"] = ["开启后提醒触发时播放声音。程序目录中的有效 papertodo.wav 会优先于所选 Windows 系统声音。", "Play a sound when a reminder fires. A valid papertodo.wav beside the app takes priority over the selected Windows system sound.", "有効にするとリマインダー時に音を鳴らします。アプリと同じフォルダーの有効な papertodo.wav が、選択した Windows システム音より優先されます。", "켜면 미리 알림이 울릴 때 소리를 재생합니다. 프로그램 폴더의 유효한 papertodo.wav가 선택한 Windows 시스템 소리보다 우선합니다."],
            ["LabsTodoReminderSound"] = ["提醒声音", "Reminder sound", "リマインダー音", "미리 알림 소리"],
            ["TipLabsTodoReminderSound"] = ["选择 Windows 系统声音。若程序目录存在可读取的 papertodo.wav，则自动优先使用；文件无效或播放失败时回退到这里的选择。", "Choose a Windows system sound. A readable papertodo.wav beside the app is used first; invalid or failed custom audio falls back to this choice.", "Windows のシステム音を選びます。アプリと同じフォルダーに読み取り可能な papertodo.wav があれば優先し、無効または再生失敗時はこの音へ戻ります。", "Windows 시스템 소리를 선택합니다. 프로그램 폴더에 읽을 수 있는 papertodo.wav가 있으면 우선 사용하며, 유효하지 않거나 재생에 실패하면 이 선택으로 돌아갑니다."],
            ["TodoReminderSoundAsterisk"] = ["提示音", "Asterisk", "通知", "알림"],
            ["TodoReminderSoundBeep"] = ["蜂鸣", "Beep", "ビープ", "비프"],
            ["TodoReminderSoundExclamation"] = ["感叹", "Exclamation", "警告", "경고"],
            ["TodoReminderSoundHand"] = ["严重警告", "Critical stop", "重大な警告", "심각한 경고"],
            ["TodoReminderSoundQuestion"] = ["询问", "Question", "質問", "질문"],
            ["TodoBacklogButton"] = ["晚点说", "Later", "あとで", "나중에"],
            ["TodoBacklogToolTip"] = ["晚点说：把这条待办暂存进全局待办篮子，不删除，随时可提取回列表。", "Say it later: park this todo in the global backlog basket without deleting it. You can pull it back anytime.", "あとで：このToDoを削除せずにグローバル保留ボックスへ退避します。いつでもリストへ戻せます。", "나중에: 이 할 일을 삭제하지 않고 전역 보류함에 보관합니다. 언제든 목록으로 되돌릴 수 있습니다."],
            ["MenuTodoItemToBacklog"] = ["晚点说（进待办篮子）", "Say it later (backlog)", "あとで（保留ボックスへ）", "나중에 하기(보류함으로)"],
            ["TodoBacklogCount"] = ["待办篮子({0})", "Backlog basket ({0})", "保留ボックス ({0})", "보류함 ({0})"],
            ["TodoBacklogEmpty"] = ["没有晚点说的任务。", "Nothing parked here yet.", "保留した項目はありません。", "보관한 항목이 없습니다."],
            ["TodoBacklogSource"] = ["来自：{0}", "From: {0}", "元：{0}", "출처: {0}"],
            ["TodoBacklogExtract"] = ["回到列表", "Back to list", "リストへ戻す", "목록으로"],
            ["TodoBacklogExtractToolTip"] = ["提取这条到所选待办纸的列表末尾。", "Extract this item to the end of the chosen todo paper.", "選択したToDo紙の末尾へ戻します。", "선택한 할 일 메모의 끝으로 되돌립니다."],
            ["TodoBacklogDelete"] = ["删除", "Delete", "削除", "삭제"],
            ["TodoBacklogDeleteToolTip"] = ["彻底从篮子删除这条。", "Permanently remove this item from the basket.", "この項目をかごから完全に削除します。", "이 항목을 보류함에서 영구 삭제합니다."],
            ["TodoBacklogNoTarget"] = ["没有可提取的待办纸", "No todo paper available", "対象のToDo紙がありません", "추출할 할 일 메모가 없습니다"]
        };

    public static string Get(string key)
    {
        var uiCulture = UiLanguages.EffectiveUiCulture;
        var resource = Manager.GetString(key, uiCulture);
        if (resource != null)
        {
            return resource;
        }

        if (!Supplemental.TryGetValue(key, out var values))
        {
            return key;
        }

        return uiCulture.TwoLetterISOLanguageName switch
        {
            "en" => values[1],
            "ja" => values[2],
            "ko" => values[3],
            _ => values[0]
        };
    }

    public static string Format(string key, params object[] args)
    {
        return string.Format(UiLanguages.EffectiveCulture, Get(key), args);
    }
}
