using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

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
            "纸片背景", "Paper background", "紙面背景", "종이 배경")));
        section.Children.Add(WrapWithHint(
            SettingsToggle(
                SettingsSidebarLocalized(
                    "启用和配色混合",
                    "Blend with paper colors",
                    "紙面カラーと混合する",
                    "종이 색상과 혼합"),
                NoteBackground.BlendWithTheme,
                TogglePaperBackgroundBlend),
            BuildSettingsHintTooltip(SettingsSidebarLocalized(
                "已检测到 PaperTodo.exe 同目录下的 papertodo.png（也支持 .jpg/.jpeg）。图片会同时用于笔记和待办；关闭此项显示原图，开启后与当前纸片配色混合。替换图片后切换此项、切换位置或重启 PaperTodo 即可刷新。",
                "Detected papertodo.png beside PaperTodo.exe (.jpg/.jpeg are also supported). The image is shared by note and todo papers. Turn this off to show the original image, or on to blend it with the current paper colors. After replacing the image, toggle this option, change its position, or restart PaperTodo to refresh it.",
                "PaperTodo.exe と同じフォルダーの papertodo.png を検出しました（.jpg/.jpeg も対応）。画像はノートと ToDo の両方で共有されます。オフでは元画像をそのまま表示し、オンでは現在の紙面カラーと混合します。画像を差し替えた後は、この設定か位置を切り替えるか PaperTodo を再起動すると更新されます。",
                "PaperTodo.exe와 같은 폴더의 papertodo.png을 감지했습니다(.jpg/.jpeg도 지원). 이미지는 노트와 할 일 종이에 함께 사용됩니다. 끄면 원본 이미지를 표시하고, 켜면 현재 종이 색상과 혼합합니다. 이미지를 교체한 뒤 이 옵션이나 위치를 바꾸거나 PaperTodo를 다시 시작하면 새로 고쳐집니다."))));
        section.Children.Add(BuildPaperBackgroundLayoutRow());

        // BuildVisualSettingsPage historically inserts this optional section above both columns.
        // Move it into the already-built right column after attachment so detecting an image does
        // not make the entire visual settings page taller.
        section.Loaded += (_, _) => section.Dispatcher.BeginInvoke(
            (Action)(() => MovePaperBackgroundSectionIntoVisualRightColumn(section)),
            DispatcherPriority.Loaded);
        return section;
    }

    private UIElement BuildPaperBackgroundLayoutRow()
    {
        var row = new Grid
        {
            Margin = new Thickness(0, 5, 0, 2)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = SettingsSidebarLocalized(
                "位置", "Position", "位置", "위치"),
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(12),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var selector = CreateSettingsSelect(
            [
                (PaperBackgroundLayouts.Stretch,
                    SettingsSidebarLocalized("拉伸", "Stretch", "ストレッチ", "늘이기")),
                (PaperBackgroundLayouts.Center,
                    SettingsSidebarLocalized("居中", "Center", "中央", "가운데")),
                (PaperBackgroundLayouts.BottomLeft,
                    SettingsSidebarLocalized("左下", "Bottom left", "左下", "왼쪽 아래")),
                (PaperBackgroundLayouts.BottomCenter,
                    SettingsSidebarLocalized("正下", "Bottom center", "下中央", "아래 가운데")),
                (PaperBackgroundLayouts.BottomRight,
                    SettingsSidebarLocalized("右下", "Bottom right", "右下", "오른쪽 아래"))
            ],
            NoteBackground.Layout,
            SetPaperBackgroundLayout);
        if (selector is FrameworkElement selectorElement)
        {
            selectorElement.Width = 132;
            selectorElement.Margin = new Thickness(8, 0, 0, 0);
            selectorElement.HorizontalAlignment = HorizontalAlignment.Right;
        }
        Grid.SetColumn(selector, 1);
        row.Children.Add(selector);
        return row;
    }

    private static void MovePaperBackgroundSectionIntoVisualRightColumn(
        StackPanel section)
    {
        if (section.Parent is not StackPanel wrapper)
        {
            return;
        }

        Grid? columns = null;
        foreach (UIElement child in wrapper.Children)
        {
            if (child is Grid grid && grid.ColumnDefinitions.Count >= 3)
            {
                columns = grid;
                break;
            }
        }
        if (columns == null)
        {
            return;
        }

        StackPanel? rightColumn = null;
        foreach (UIElement child in columns.Children)
        {
            if (child is StackPanel candidate && Grid.GetColumn(candidate) == 2)
            {
                rightColumn = candidate;
                break;
            }
        }
        if (rightColumn == null)
        {
            return;
        }

        wrapper.Children.Remove(section);
        section.Margin = new Thickness(0, 12, 0, 4);
        rightColumn.Children.Add(section);
    }

    private void TogglePaperBackgroundBlend()
    {
        ApplyPaperBackgroundSetting(() =>
            NoteBackground.SetBlendWithTheme(!NoteBackground.BlendWithTheme));
    }

    private void SetPaperBackgroundLayout(string layout)
    {
        ApplyPaperBackgroundSetting(() => NoteBackground.SetLayout(layout));
    }

    private void ApplyPaperBackgroundSetting(Action update)
    {
        var changed = TryUpdatePaperBackgroundSetting(update);
        if (changed)
        {
            foreach (var window in _windows.Values)
            {
                window.RefreshPaperBackground();
            }
        }

        // Rebuild even on a failed write so the control reflects the value that actually persisted.
        RefreshSettingsWindowContent();
    }

    private bool TryUpdatePaperBackgroundSetting(Action update)
    {
        try
        {
            update();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_settingsWindow != null)
            {
                PaperNoticeDialog.Show(
                    _settingsWindow,
                    SettingsSidebarLocalized(
                        "纸片背景", "Paper background", "紙面背景", "종이 배경"),
                    SettingsSidebarLocalized(
                        "无法保存纸片背景设置。请检查 PaperTodo 本地设置目录的写入权限。",
                        "Could not save the paper background setting. Check write access to PaperTodo's local settings folder.",
                        "紙面背景の設定を保存できませんでした。PaperTodo のローカル設定フォルダーへの書き込み権限を確認してください。",
                        "종이 배경 설정을 저장하지 못했습니다. PaperTodo 로컬 설정 폴더의 쓰기 권한을 확인하세요.") +
                    Environment.NewLine + ex.Message);
            }
            return false;
        }
    }
}
