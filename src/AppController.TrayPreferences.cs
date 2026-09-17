using System.Windows;

namespace PaperTodo;

public sealed partial class AppController
{
    private void ApplyTrayIconVisibility()
    {
        if (_trayIcon == null)
        {
            return;
        }

        _trayIcon.Visibility = State.HideTrayIcon
            ? Visibility.Hidden
            : Visibility.Visible;
    }

    private void ToggleHideTrayIcon()
    {
        State.HideTrayIcon = !State.HideTrayIcon;
        ApplyTrayIconVisibility();
        SaveNow();
    }

    private void ExecuteGlobalShortcutAction(GlobalShortcutActionKind actionKind)
    {
        if (actionKind == GlobalShortcutActionKind.TrayMenu)
        {
            ShowTrayMenuAtCursor();
        }
    }

    private void ShowTrayMenuAtCursor()
    {
        if (IsExiting || _trayIcon == null)
        {
            return;
        }

        _trayIcon.ShowContextMenuAtCurrentMousePosition();
    }

    private string HideTrayIconSettingLabel() =>
        SettingsSidebarLocalized(
            "隐藏托盘图标",
            "Hide tray icon",
            "トレイアイコンを隠す",
            "트레이 아이콘 숨기기");

    private string HideTrayIconSettingTip() =>
        SettingsSidebarLocalized(
            "隐藏 Windows 通知区域中的 PaperTodo 图标。建议先在快捷键页设置并启用「打开托盘菜单」；隐藏后该快捷键仍会在鼠标位置打开同一个菜单。",
            "Hide the PaperTodo icon from the Windows notification area. Configure and enable Open tray menu in Shortcuts first; while hidden, that shortcut still opens the same menu at the mouse pointer.",
            "Windows の通知領域から PaperTodo アイコンを隠します。先にショートカット設定で「トレイメニューを開く」を設定して有効にしてください。アイコンを隠しても、そのショートカットでマウスポインター位置に同じメニューを開けます。",
            "Windows 알림 영역에서 PaperTodo 아이콘을 숨깁니다. 먼저 바로 가기 설정에서 '트레이 메뉴 열기'를 지정하고 활성화하세요. 아이콘을 숨긴 뒤에도 해당 바로 가기로 마우스 위치에 같은 메뉴를 열 수 있습니다.");

    private string TrayMenuShortcutLabel() =>
        SettingsSidebarLocalized(
            "打开托盘菜单",
            "Open tray menu",
            "トレイメニューを開く",
            "트레이 메뉴 열기");
}
