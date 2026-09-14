using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

public sealed partial class AppController
{
    private UIElement BuildNoteBackgroundSettingsSection()
    {
        var section = new StackPanel
        {
            Margin = new Thickness(2, 0, 4, 4)
        };
        section.Children.Add(SettingsSectionLabel(SettingsSidebarLocalized(
            "笔记背景", "Note background", "ノート背景", "노트 배경")));
        section.Children.Add(WrapWithHint(
            SettingsToggle(
                SettingsSidebarLocalized(
                    "启用自定义笔记背景",
                    "Enable custom note background",
                    "カスタムノート背景を有効にする",
                    "사용자 지정 노트 배경 사용"),
                NoteBackground.IsEnabled,
                ToggleNoteBackground),
            BuildSettingsHintTooltip(SettingsSidebarLocalized(
                "已检测到 custom/note/background.png（也支持 .jpg/.jpeg）。背景会自动与当前浅色/深色纸张主题混合；替换文件后重启 PaperTodo 或重新切换此开关即可刷新。",
                "Detected custom/note/background.png (.jpg/.jpeg are also supported). The image is blended with the current light/dark paper theme. After replacing the file, restart PaperTodo or toggle this setting to refresh it.",
                "custom/note/background.png を検出しました（.jpg/.jpeg も対応）。現在のライト／ダーク紙面テーマに合わせて自動的に合成します。画像を差し替えた後は PaperTodo を再起動するか、この設定を切り替えて更新してください。",
                "custom/note/background.png을 감지했습니다(.jpg/.jpeg도 지원). 현재 밝은/어두운 종이 테마와 자동으로 혼합됩니다. 파일을 교체한 뒤 PaperTodo를 다시 시작하거나 이 설정을 전환하면 새로 고쳐집니다."))));
        return section;
    }

    private void ToggleNoteBackground()
    {
        TrySetNoteBackgroundEnabled(!NoteBackground.IsEnabled);
        foreach (var window in _windows.Values)
        {
            window.RefreshNoteBackground();
        }
        // Re-read the marker after either outcome so paper content and the clicked toggle reflect
        // the same final on-disk state even when a failed write had a partial filesystem effect.
        RefreshSettingsWindowContent();
    }

    private bool TrySetNoteBackgroundEnabled(bool enabled)
    {
        try
        {
            NoteBackground.SetEnabled(enabled);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_settingsWindow != null)
            {
                PaperNoticeDialog.Show(
                    _settingsWindow,
                    SettingsSidebarLocalized("笔记背景", "Note background", "ノート背景", "노트 배경"),
                    SettingsSidebarLocalized(
                        "无法保存笔记背景开关。请检查 custom/note 目录的写入权限。",
                        "Could not save the note background setting. Check write access to custom/note.",
                        "ノート背景の設定を保存できませんでした。custom/note の書き込み権限を確認してください。",
                        "노트 배경 설정을 저장하지 못했습니다. custom/note의 쓰기 권한을 확인하세요.") +
                    Environment.NewLine + ex.Message);
            }
            return false;
        }
    }
}
