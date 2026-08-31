using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace PaperTodo;

public static class ColorSchemes
{
    public const string Warm = "warm";
    public const string Ink = "ink";
    public const string Forest = "forest";
    public const string Rose = "rose";

    public static readonly string[] All = { Warm, Ink, Forest, Rose };

    public static bool IsValid(string? id) => id is Warm or Ink or Forest or Rose;

    public static string Normalize(string? id) => IsValid(id) ? id! : Warm;
}

public static class Theme
{
    // 一套主题的全部基色。半透明叠加色（hover / 拖放 / 标题栏 / 删除区）
    // 不进表，统一用 Tint / Danger 在基色上派生，保证一处定义、整体一致。
    private sealed class Palette
    {
        public Color Paper;        // 纸面背景
        public Color PaperBorder;  // 纸面描边
        public Color Text;         // 正文
        public Color WeakText;     // 弱化文字（完成项、提示、次要信息）
        public Color Active;       // 强调（置顶、选中、勾选）
        public Color Code;         // 行内代码 / 代码块背景
        public Color QuoteBorder;  // 引用左轨
        public Color Link;         // 超链接
        public Color CheckBox;     // 待办勾选框描边
        public Color Tint;         // 暖色叠加基（hover、拖放、标题栏底纹的来源）
        public Color Danger;       // 删除 / 警示
    }

    private static readonly Dictionary<string, (Palette Light, Palette Dark)> Schemes = BuildSchemes();

    // 颜色 → frozen 画刷的全局缓存。颜色到画刷是恒定映射，跨主题切换无需清空。
    private static readonly Dictionary<uint, SolidColorBrush> BrushCache = new();

    private static bool? _isDarkCache;
    private static string? _schemeCache;
    private static Palette? _paletteCache;

    /// <summary>主题或配色族变化后调用，使下一次取色重新解析。</summary>
    public static void Invalidate()
    {
        _isDarkCache = null;
        _schemeCache = null;
        _paletteCache = null;
    }

    public static bool IsDark
    {
        get
        {
            if (_isDarkCache is bool cached)
            {
                return cached;
            }

            var theme = AppController.Current?.State?.Theme;
            bool dark = theme == "system" ? IsSystemDark() : theme == "dark";
            _isDarkCache = dark;
            return dark;
        }
    }

    private static string CurrentScheme => _schemeCache ??= ColorSchemes.Normalize(AppController.Current?.State?.ColorScheme);

    private static Palette Current
    {
        get
        {
            if (_paletteCache != null)
            {
                return _paletteCache;
            }

            var pair = Schemes.TryGetValue(CurrentScheme, out var s) ? s : Schemes[ColorSchemes.Warm];
            _paletteCache = IsDark ? pair.Dark : pair.Light;
            return _paletteCache;
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null && key.GetValue("AppsUseLightTheme") is int i)
            {
                return i == 0;
            }
        }
        catch
        {
            // Fallback to light
        }
        return false;
    }

    // ---- 基色画刷 ----
    public static Brush PaperBrush => Solid(Current.Paper);
    public static Brush PaperBorderBrush => Solid(Current.PaperBorder);
    public static Brush TextBrush => Solid(Current.Text);
    public static Brush WeakTextBrush => Solid(Current.WeakText);
    public static Brush BrightWeakTextBrush => Solid(IsDark ? Lighten(Current.WeakText, 0.22) : Current.WeakText);
    public static Brush ActiveBrush => Solid(Current.Active);
    public static Brush CodeBrush => Solid(Current.Code);
    public static Brush QuoteBorderBrush => Solid(Current.QuoteBorder);
    public static Brush LinkBrush => Solid(Current.Link);
    public static Brush CheckBoxBorderBrush => Solid(Current.CheckBox);
    public static Brush DangerBrush => Solid(Current.Danger);
    public static Brush ScrollThumbBrush => WeakTextBrush;
    public static Brush ScrollThumbHoverBrush => ActiveBrush;

    // Stock ResizeGrip geometry.
    // Base tone = Windows ControlDark (system light/dark inverse) + light scheme Tint mix.
    // Mode only changes tile alpha (hidden = 0, still hit-testable for corner resize).
    private const double ResizeGripSchemeTintMix = 0.14;
    // 50% transparency => alpha 128.
    private const byte ResizeGripSoftAlpha = 128;

    public static Brush ResizeGripBrush(string? mode)
    {
        var tone = Mix(SystemColors.ControlDarkColor, Current.Tint, ResizeGripSchemeTintMix);
        return ResizeGripModes.Normalize(mode) switch
        {
            ResizeGripModes.Hidden => Solid(WithAlpha(tone, 0)),
            ResizeGripModes.Soft => Solid(WithAlpha(tone, ResizeGripSoftAlpha)),
            _ => Solid(tone)
        };
    }

    public static Brush HoverBrush => Tint((byte)(IsDark ? 48 : 32));
    public static Brush CapsuleFocusBorderBrush => Solid(Mix(Current.Active, Current.Text, IsDark ? 0.38 : 0.08));

    // ---- 派生画刷 ----
    /// <summary>在当前主题的暖色叠加基上按 alpha 取一层半透明画刷。</summary>
    public static Brush Tint(byte alpha) => Solid(WithAlpha(Current.Tint, alpha));

    /// <summary>在当前主题的警示色上按 alpha 取一层半透明画刷。</summary>
    public static Brush Danger(byte alpha) => Solid(WithAlpha(Current.Danger, alpha));

    /// <summary>警示按钮的悬停态：在警示色上提亮，作为实色悬停反馈。</summary>
    public static Brush DangerHoverBrush => Solid(Lighten(Current.Danger, 0.14));

    /// <summary>Markdown 浏览态淡化标记用的弱前景（正文色的低透明版）。</summary>
    public static Brush SyntaxFadeBrush => Solid(WithAlpha(Current.Text, (byte)(IsDark ? 78 : 72)));

    // 勾选框三态：hover 描边朝正文色靠（浅色变深、深色变亮，方向自适应）；
    // active hover 在强调色上压暗；未选 hover 底用极淡叠加。
    public static Brush CheckBoxHoverBorderBrush => Solid(Mix(Current.CheckBox, Current.Text, 0.35));
    public static Brush CheckBoxActiveHoverBrush => Solid(Darken(Current.Active, 0.12));
    public static Brush CheckBoxUncheckedHoverBgBrush => Tint(20);

    private static SolidColorBrush Solid(Color c)
    {
        uint key = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        if (BrushCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var brush = new SolidColorBrush(c);
        brush.Freeze();
        BrushCache[key] = brush;
        return brush;
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private static Color Darken(Color c, double t) => Mix(c, Color.FromRgb(0, 0, 0), t);

    private static Color Lighten(Color c, double t) => Mix(c, Color.FromRgb(255, 255, 255), t);

    private static Dictionary<string, (Palette Light, Palette Dark)> BuildSchemes()
    {
        return new Dictionary<string, (Palette, Palette)>
        {
            // 暖纸 — 经典奶白纸张焕新：去掉旧版偏冷的紫蓝链接，换暖陶土；弱文字略降饱和。
            [ColorSchemes.Warm] = (
                new Palette
                {
                    Paper = Color.FromRgb(255, 249, 234),
                    PaperBorder = Color.FromRgb(224, 206, 167),
                    Text = Color.FromRgb(51, 41, 30),
                    WeakText = Color.FromRgb(138, 122, 99),
                    Active = Color.FromRgb(140, 115, 80),
                    Code = Color.FromRgb(247, 237, 210),
                    QuoteBorder = Color.FromRgb(212, 190, 146),
                    Link = Color.FromRgb(176, 98, 66),
                    CheckBox = Color.FromRgb(180, 160, 120),
                    Tint = Color.FromRgb(120, 92, 48),
                    Danger = Color.FromRgb(176, 90, 70),
                },
                new Palette
                {
                    Paper = Color.FromRgb(33, 31, 28),
                    PaperBorder = Color.FromRgb(76, 69, 61),
                    Text = Color.FromRgb(231, 224, 212),
                    WeakText = Color.FromRgb(146, 137, 123),
                    Active = Color.FromRgb(168, 142, 106),
                    Code = Color.FromRgb(45, 42, 38),
                    QuoteBorder = Color.FromRgb(94, 86, 75),
                    Link = Color.FromRgb(214, 150, 120),
                    CheckBox = Color.FromRgb(110, 100, 85),
                    Tint = Color.FromRgb(230, 223, 211),
                    Danger = Color.FromRgb(230, 110, 90),
                }),

            // 墨 — 冷调中性灰白，沉静专业，链接用经典墨蓝。
            [ColorSchemes.Ink] = (
                new Palette
                {
                    Paper = Color.FromRgb(246, 247, 249),
                    PaperBorder = Color.FromRgb(208, 214, 222),
                    Text = Color.FromRgb(38, 44, 54),
                    WeakText = Color.FromRgb(118, 126, 138),
                    Active = Color.FromRgb(90, 108, 134),
                    Code = Color.FromRgb(236, 239, 243),
                    QuoteBorder = Color.FromRgb(198, 206, 216),
                    Link = Color.FromRgb(66, 104, 156),
                    CheckBox = Color.FromRgb(170, 180, 194),
                    Tint = Color.FromRgb(70, 90, 120),
                    Danger = Color.FromRgb(188, 84, 80),
                },
                new Palette
                {
                    Paper = Color.FromRgb(26, 28, 32),
                    PaperBorder = Color.FromRgb(60, 66, 76),
                    Text = Color.FromRgb(222, 227, 234),
                    WeakText = Color.FromRgb(138, 146, 158),
                    Active = Color.FromRgb(132, 156, 188),
                    Code = Color.FromRgb(38, 41, 47),
                    QuoteBorder = Color.FromRgb(78, 86, 98),
                    Link = Color.FromRgb(132, 170, 214),
                    CheckBox = Color.FromRgb(96, 106, 120),
                    Tint = Color.FromRgb(180, 200, 228),
                    Danger = Color.FromRgb(224, 116, 108),
                }),

            // 林 — 柔和草木绿，纸面带极淡绿调，链接用森绿。
            [ColorSchemes.Forest] = (
                new Palette
                {
                    Paper = Color.FromRgb(243, 248, 241),
                    PaperBorder = Color.FromRgb(200, 218, 198),
                    Text = Color.FromRgb(38, 50, 42),
                    WeakText = Color.FromRgb(110, 128, 112),
                    Active = Color.FromRgb(88, 130, 96),
                    Code = Color.FromRgb(233, 242, 231),
                    QuoteBorder = Color.FromRgb(192, 214, 192),
                    Link = Color.FromRgb(60, 130, 96),
                    CheckBox = Color.FromRgb(168, 192, 168),
                    Tint = Color.FromRgb(70, 110, 80),
                    Danger = Color.FromRgb(188, 96, 76),
                },
                new Palette
                {
                    Paper = Color.FromRgb(26, 30, 27),
                    PaperBorder = Color.FromRgb(58, 70, 60),
                    Text = Color.FromRgb(220, 228, 220),
                    WeakText = Color.FromRgb(134, 148, 136),
                    Active = Color.FromRgb(124, 168, 134),
                    Code = Color.FromRgb(37, 42, 38),
                    QuoteBorder = Color.FromRgb(74, 90, 76),
                    Link = Color.FromRgb(128, 190, 150),
                    CheckBox = Color.FromRgb(92, 110, 94),
                    Tint = Color.FromRgb(180, 208, 186),
                    Danger = Color.FromRgb(222, 124, 104),
                }),

            // 霞 — 暖玫瑰胭脂，纸面带血色，链接用玫瑰红。
            [ColorSchemes.Rose] = (
                new Palette
                {
                    Paper = Color.FromRgb(253, 245, 246),
                    PaperBorder = Color.FromRgb(228, 205, 210),
                    Text = Color.FromRgb(54, 38, 42),
                    WeakText = Color.FromRgb(140, 114, 120),
                    Active = Color.FromRgb(158, 104, 118),
                    Code = Color.FromRgb(248, 236, 238),
                    QuoteBorder = Color.FromRgb(224, 198, 204),
                    Link = Color.FromRgb(178, 84, 110),
                    CheckBox = Color.FromRgb(216, 184, 192),
                    Tint = Color.FromRgb(150, 80, 96),
                    Danger = Color.FromRgb(188, 82, 78),
                },
                new Palette
                {
                    Paper = Color.FromRgb(33, 28, 30),
                    PaperBorder = Color.FromRgb(78, 64, 68),
                    Text = Color.FromRgb(232, 220, 223),
                    WeakText = Color.FromRgb(152, 132, 137),
                    Active = Color.FromRgb(190, 134, 148),
                    Code = Color.FromRgb(44, 38, 40),
                    QuoteBorder = Color.FromRgb(92, 76, 80),
                    Link = Color.FromRgb(224, 148, 170),
                    CheckBox = Color.FromRgb(96, 78, 82),
                    Tint = Color.FromRgb(224, 180, 190),
                    Danger = Color.FromRgb(230, 114, 100),
                }),
        };
    }
}
