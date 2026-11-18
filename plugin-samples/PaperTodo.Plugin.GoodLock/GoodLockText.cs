namespace PaperTodo.Plugin.GoodLock;

internal sealed class GoodLockText(string language)
{
    private readonly string _language = language.Split('-')[0].ToLowerInvariant();

    private string Pick(string zh, string en, string ja, string ko) => _language switch
    {
        "zh" => zh,
        "ja" => ja,
        "ko" => ko,
        _ => en
    };

    public string Apply => Pick("应用", "Apply", "適用", "적용");
    public string OpenFile => Pick("打开编辑文件", "Open file", "編集ファイルを開く", "편집 파일 열기");
    public string ResetAll => Pick("全体重置", "Reset all", "すべてリセット", "모두 초기화");
    public string Restart => Pick("快速重启", "Restart", "再起動", "다시 시작");
    public string SaveAs => Pick("另存为", "Save as", "名前を付けて保存", "다른 이름으로 저장");
    public string Default => Pick("默认值", "Default", "既定値", "기본값");
    public string Modified => Pick("修改后", "Modified", "変更後", "변경 후");
    public string Reset => Pick("重置本项", "Reset this value", "この項目をリセット", "이 항목 초기화");
    public string Optimize => Pick("官方优化", "Suggest an adjustment", "調整を提案", "조정 제안");
    public string NoSuggestion => Pick("暂无优化建议", "No adjustment available", "調整案はありません", "조정 제안 없음");
    public string Empty => Pick("暂无修改", "No overrides", "変更はありません", "변경 사항 없음");
    public string Pending => Pick("待应用", "Pending", "未適用", "적용 대기");
    public string Applied => Pick("已应用", "Applied", "適用済み", "적용됨");
    public string RestartRequired => Pick("需要重启", "Restart required", "再起動が必要", "재시작 필요");
    public string Unknown => Pick("未识别的参数", "Unknown parameter", "認識できない項目", "알 수 없는 매개변수");
    public string True => Pick("开启", "On", "オン", "켜짐");
    public string False => Pick("关闭", "Off", "オフ", "꺼짐");
    public string ErrorTitle => Pick("操作未完成", "Operation not completed", "操作を完了できませんでした", "작업을 완료하지 못했습니다");
    public string DifferentFile => Pick("请选择另一份文件。", "Choose a different file.", "別のファイルを選択してください。", "다른 파일을 선택하세요.");
    public string FileChanged => Pick(
        "配置文件已在外部修改，页面已刷新。本页待应用修改仍保留，请检查后再次应用。",
        "The file changed externally and has been refreshed. Your pending edits are preserved. Review them before applying again.",
        "設定ファイルが外部で変更されたため、表示を更新しました。未適用の編集は保持されています。確認してから再度適用してください。",
        "구성 파일이 외부에서 변경되어 새로 고쳤습니다. 대기 중인 편집은 유지됩니다. 확인한 후 다시 적용하세요.");
    public string JsonFilter => Pick("JSON 配置 (*.json)|*.json|所有文件 (*.*)|*.*", "JSON configuration (*.json)|*.json|All files (*.*)|*.*", "JSON 設定 (*.json)|*.json|すべてのファイル (*.*)|*.*", "JSON 구성 (*.json)|*.json|모든 파일 (*.*)|*.*");
}
