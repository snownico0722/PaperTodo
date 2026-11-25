using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

public sealed partial class AppController
{
    private UIElement BuildPaperBackgroundSettingsSection()
    {
        var section = new StackPanel
        {
            Margin = new Thickness(2, 0, 4, 4)
        };
        var imageFound = PaperBackground.IsAvailable;
        var loadError = imageFound ? PaperBackground.LoadError : null;
        var imageLoaded = imageFound && string.IsNullOrWhiteSpace(loadError);

        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        heading.Children.Add(new TextBlock
        {
            Text = SettingsSidebarLocalized("纸片背景", "Paper background", "紙面背景", "종이 배경"),
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(12),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        heading.Children.Add(CreateSettingsHintGlyph(
            BuildSettingsHintTooltip(SettingsSidebarLocalized(
                "将图片命名为 papertodo.png、papertodo.jpg 或 papertodo.jpeg，放到 PaperTodo.exe 所在文件夹，重启后即可作为待办纸和笔记纸的背景。",
                "Name the image papertodo.png, papertodo.jpg, or papertodo.jpeg, place it beside PaperTodo.exe, and restart PaperTodo to use it as the background for todos and notes.",
                "画像を papertodo.png、papertodo.jpg、または papertodo.jpeg という名前で PaperTodo.exe と同じフォルダーに置き、再起動すると ToDo とノートの背景として使用できます。",
                "이미지 이름을 papertodo.png, papertodo.jpg 또는 papertodo.jpeg로 지정해 PaperTodo.exe와 같은 폴더에 넣고 다시 시작하면 할 일과 노트의 배경으로 사용할 수 있습니다.")),
            new Thickness(5, 0, 0, 0)));
        if (!imageFound)
        {
            heading.Children.Add(new TextBlock
            {
                Text = SettingsSidebarLocalized("未启用", "Not enabled", "未有効", "사용 안 함"),
                Foreground = TrayWeakTextBrush,
                FontSize = AppTypography.Scale(10.5),
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        section.Children.Add(heading);

        if (!imageFound)
            return section;

        if (!imageLoaded)
        {
            section.Children.Add(new TextBlock
            {
                Text = SettingsSidebarLocalized(
                    "背景图片加载失败，请检查图片文件。",
                    "The background image could not be loaded. Check the image file.",
                    "背景画像を読み込めません。画像ファイルを確認してください。",
                    "배경 이미지를 불러오지 못했습니다. 이미지 파일을 확인하세요."),
                Foreground = Theme.DangerBrush,
                FontSize = AppTypography.Scale(11.5),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
                ToolTip = BuildSettingsHintTooltip(loadError ?? string.Empty)
            });
            return section;
        }

        section.Children.Add(WrapWithHint(
            SettingsToggle(
                SettingsSidebarLocalized(
                    "启用背景色混合",
                    "Enable background color blending",
                    "背景色のブレンドを有効化",
                    "배경색 혼합 사용"),
                PaperBackground.BlendWithTheme,
                TogglePaperBackgroundBlend),
            BuildSettingsHintTooltip(SettingsSidebarLocalized(
                "开启后，背景图与当前纸片配色混合；关闭后显示原图。",
                "When enabled, the image is blended with the current paper colors; when disabled, the original image is shown.",
                "オンにすると背景画像を現在の紙面配色と混合し、オフにすると元画像を表示します。",
                "켜면 배경 이미지가 현재 종이 색상과 혼합되고, 끄면 원본 이미지가 표시됩니다."))));
        section.Children.Add(SettingsToggle(
            SettingsSidebarLocalized("拉伸", "Stretch", "ストレッチ", "늘이기"),
            PaperBackground.StretchImage,
            TogglePaperBackgroundStretch));
        section.Children.Add(BuildPaperBackgroundLayoutRow());
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
                (PaperBackgroundLayouts.Center,
                    SettingsSidebarLocalized("居中", "Center", "中央", "가운데")),
                (PaperBackgroundLayouts.BottomLeft,
                    SettingsSidebarLocalized("左下", "Bottom left", "左下", "왼쪽 아래")),
                (PaperBackgroundLayouts.BottomCenter,
                    SettingsSidebarLocalized("正下", "Bottom center", "下中央", "아래 가운데")),
                (PaperBackgroundLayouts.BottomRight,
                    SettingsSidebarLocalized("右下", "Bottom right", "右下", "오른쪽 아래"))
            ],
            PaperBackground.Layout,
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

    private void TogglePaperBackgroundBlend() =>
        SetSettingFromUi("appearance.background_blend", !PaperBackground.BlendWithTheme);

    private void TogglePaperBackgroundStretch() =>
        SetSettingFromUi("appearance.background_stretch", !PaperBackground.StretchImage);

    private void SetPaperBackgroundLayout(string layout) =>
        SetSettingFromUi("appearance.background_layout", layout);



    private bool TryResetPaperBackgroundPreferences()
    {
        if (!TryUpdatePaperBackgroundSetting(PaperBackground.ResetPreferences))
        {
            return false;
        }

        RefreshPaperBackgroundSurfaces();
        return true;
    }

    private void RefreshPaperBackgroundSurfaces()
    {
        foreach (var window in _windows.Values)
        {
            window.RefreshPaperBackground();
        }
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
            ShowPaperBackgroundSaveFailure(ex);
            return false;
        }
    }

    private void ShowPaperBackgroundSaveFailure(Exception ex)
    {
        if (_settingsWindow != null)
        {
            PaperNoticeDialog.Show(
                _settingsWindow,
                SettingsSidebarLocalized(
                    "纸片背景", "Paper background", "紙面背景", "종이 배경"),
                SettingsSidebarLocalized(
                    "无法保存纸片背景设置。请检查 PaperTodo.exe 所在目录的写入权限。",
                    "Could not save the paper background setting. Check write access to the folder containing PaperTodo.exe.",
                    "紙面背景の設定を保存できませんでした。PaperTodo.exe があるフォルダーへの書き込み権限を確認してください。",
                    "종이 배경 설정을 저장하지 못했습니다. PaperTodo.exe가 있는 폴더의 쓰기 권한을 확인하세요.") +
                Environment.NewLine + ex.Message);
        }
    }
}
