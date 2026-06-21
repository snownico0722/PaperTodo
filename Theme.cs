using System;
using System.Collections.Generic;
using System.Globalization;
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
    public const string Ocean = "ocean";
    public const string Lavender = "lavender";
    public const string Dune = "dune";
    public const string Custom = "custom";

    public static readonly string[] BuiltIn = { Warm, Ink, Forest, Rose, Ocean, Lavender, Dune };
    public static readonly string[] All = { Warm, Ink, Forest, Rose, Ocean, Lavender, Dune, Custom };

    public static bool IsValid(string? id)
    {
        return id is Warm or Ink or Forest or Rose or Ocean or Lavender or Dune or Custom;
    }

    public static bool IsBuiltIn(string? id)
    {
        return id is Warm or Ink or Forest or Rose or Ocean or Lavender or Dune;
    }

    public static string Normalize(string? id) => IsValid(id) ? id! : Warm;

    public static string NormalizeBuiltIn(string? id) => IsBuiltIn(id) ? id! : Warm;
}

public readonly record struct ThemeColorSlot(string Key, string ResourceKey);

public static class Theme
{
    public const string SlotPaper = "paper";
    public const string SlotPaperBorder = "paperBorder";
    public const string SlotText = "text";
    public const string SlotWeakText = "weakText";
    public const string SlotActive = "active";
    public const string SlotCode = "code";
    public const string SlotQuoteBorder = "quoteBorder";
    public const string SlotLink = "link";
    public const string SlotCheckBox = "checkBox";
    public const string SlotTint = "tint";
    public const string SlotDanger = "danger";

    public static readonly ThemeColorSlot[] CustomColorSlots =
    {
        new(SlotPaper, "CustomColorPaper"),
        new(SlotPaperBorder, "CustomColorPaperBorder"),
        new(SlotText, "CustomColorText"),
        new(SlotWeakText, "CustomColorWeakText"),
        new(SlotActive, "CustomColorActive"),
        new(SlotCode, "CustomColorCode"),
        new(SlotQuoteBorder, "CustomColorQuoteBorder"),
        new(SlotLink, "CustomColorLink"),
        new(SlotCheckBox, "CustomColorCheckBox"),
        new(SlotTint, "CustomColorTint"),
        new(SlotDanger, "CustomColorDanger")
    };

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

            if (CurrentScheme == ColorSchemes.Custom)
            {
                var normalized = NormalizeCustomPalette(AppController.Current?.State?.CustomColorPalette, ColorSchemes.Warm);
                var fallbackPair = Schemes[ColorSchemes.Warm];
                _paletteCache = ToPalette(IsDark ? normalized.Dark : normalized.Light, IsDark ? fallbackPair.Dark : fallbackPair.Light);
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

    public static Brush BrushForHex(string? hex)
    {
        return TryParseHexColor(hex, out var color) ? Solid(color) : PaperBorderBrush;
    }

    public static CustomColorPalette CreateDefaultCustomPalette()
    {
        return CreateCustomPaletteFromScheme(ColorSchemes.Warm);
    }

    public static CustomColorPalette CreateCustomPaletteFromScheme(string? scheme)
    {
        var builtIn = ColorSchemes.NormalizeBuiltIn(scheme);
        var pair = Schemes.TryGetValue(builtIn, out var palettes) ? palettes : Schemes[ColorSchemes.Warm];
        return new CustomColorPalette
        {
            Light = ToPaletteColors(pair.Light),
            Dark = ToPaletteColors(pair.Dark)
        };
    }

    public static CustomColorPalette NormalizeCustomPalette(CustomColorPalette? palette, string? fallbackScheme = null)
    {
        var fallback = CreateCustomPaletteFromScheme(ColorSchemes.NormalizeBuiltIn(fallbackScheme));
        return new CustomColorPalette
        {
            Light = NormalizePaletteColors(palette?.Light, fallback.Light),
            Dark = NormalizePaletteColors(palette?.Dark, fallback.Dark)
        };
    }

    public static string GetCustomPaletteHex(CustomColorPalette? palette, bool dark, string slot)
    {
        var normalized = NormalizeCustomPalette(palette, ColorSchemes.Warm);
        var colors = dark ? normalized.Dark : normalized.Light;
        return GetSlot(colors, slot);
    }

    public static bool SetCustomPaletteHex(CustomColorPalette palette, bool dark, string slot, string? value)
    {
        if (!TryNormalizeHex(value, out var hex))
        {
            return false;
        }

        palette.Light ??= CreateDefaultCustomPalette().Light;
        palette.Dark ??= CreateDefaultCustomPalette().Dark;
        SetSlot(dark ? palette.Dark : palette.Light, slot, hex);
        return true;
    }

    public static bool TryNormalizeHex(string? value, out string hex)
    {
        hex = "";
        var text = (value ?? "").Trim();
        if (text.StartsWith("#", StringComparison.Ordinal))
        {
            text = text[1..];
        }

        if (text.Length != 6)
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (!Uri.IsHexDigit(text[i]))
            {
                return false;
            }
        }

        hex = "#" + text.ToUpperInvariant();
        return true;
    }

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

    private static bool TryParseHexColor(string? value, out Color color)
    {
        color = default;
        if (!TryNormalizeHex(value, out var hex))
        {
            return false;
        }

        color = Color.FromRgb(
            byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return true;
    }

    private static string Hex(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static string NormalizeHexOrFallback(string? value, string? fallback)
    {
        if (TryNormalizeHex(value, out var normalized))
        {
            return normalized;
        }

        return TryNormalizeHex(fallback, out var fallbackNormalized) ? fallbackNormalized : "#000000";
    }

    private static ThemePaletteColors NormalizePaletteColors(ThemePaletteColors? colors, ThemePaletteColors? fallback)
    {
        fallback ??= ToPaletteColors(Schemes[ColorSchemes.Warm].Light);
        return new ThemePaletteColors
        {
            Paper = NormalizeHexOrFallback(colors?.Paper, fallback.Paper),
            PaperBorder = NormalizeHexOrFallback(colors?.PaperBorder, fallback.PaperBorder),
            Text = NormalizeHexOrFallback(colors?.Text, fallback.Text),
            WeakText = NormalizeHexOrFallback(colors?.WeakText, fallback.WeakText),
            Active = NormalizeHexOrFallback(colors?.Active, fallback.Active),
            Code = NormalizeHexOrFallback(colors?.Code, fallback.Code),
            QuoteBorder = NormalizeHexOrFallback(colors?.QuoteBorder, fallback.QuoteBorder),
            Link = NormalizeHexOrFallback(colors?.Link, fallback.Link),
            CheckBox = NormalizeHexOrFallback(colors?.CheckBox, fallback.CheckBox),
            Tint = NormalizeHexOrFallback(colors?.Tint, fallback.Tint),
            Danger = NormalizeHexOrFallback(colors?.Danger, fallback.Danger)
        };
    }

    private static ThemePaletteColors ToPaletteColors(Palette palette)
    {
        return new ThemePaletteColors
        {
            Paper = Hex(palette.Paper),
            PaperBorder = Hex(palette.PaperBorder),
            Text = Hex(palette.Text),
            WeakText = Hex(palette.WeakText),
            Active = Hex(palette.Active),
            Code = Hex(palette.Code),
            QuoteBorder = Hex(palette.QuoteBorder),
            Link = Hex(palette.Link),
            CheckBox = Hex(palette.CheckBox),
            Tint = Hex(palette.Tint),
            Danger = Hex(palette.Danger)
        };
    }

    private static Palette ToPalette(ThemePaletteColors? colors, Palette fallback)
    {
        var normalized = NormalizePaletteColors(colors, ToPaletteColors(fallback));
        return new Palette
        {
            Paper = ColorFromHexOrFallback(normalized.Paper, fallback.Paper),
            PaperBorder = ColorFromHexOrFallback(normalized.PaperBorder, fallback.PaperBorder),
            Text = ColorFromHexOrFallback(normalized.Text, fallback.Text),
            WeakText = ColorFromHexOrFallback(normalized.WeakText, fallback.WeakText),
            Active = ColorFromHexOrFallback(normalized.Active, fallback.Active),
            Code = ColorFromHexOrFallback(normalized.Code, fallback.Code),
            QuoteBorder = ColorFromHexOrFallback(normalized.QuoteBorder, fallback.QuoteBorder),
            Link = ColorFromHexOrFallback(normalized.Link, fallback.Link),
            CheckBox = ColorFromHexOrFallback(normalized.CheckBox, fallback.CheckBox),
            Tint = ColorFromHexOrFallback(normalized.Tint, fallback.Tint),
            Danger = ColorFromHexOrFallback(normalized.Danger, fallback.Danger)
        };
    }

    private static Color ColorFromHexOrFallback(string? hex, Color fallback)
    {
        return TryParseHexColor(hex, out var color) ? color : fallback;
    }

    private static string GetSlot(ThemePaletteColors? colors, string slot)
    {
        if (colors == null)
        {
            return GetSlot(CreateDefaultCustomPalette().Light, slot);
        }

        return slot switch
        {
            SlotPaper => colors.Paper ?? "#000000",
            SlotPaperBorder => colors.PaperBorder ?? "#000000",
            SlotText => colors.Text ?? "#000000",
            SlotWeakText => colors.WeakText ?? "#000000",
            SlotActive => colors.Active ?? "#000000",
            SlotCode => colors.Code ?? "#000000",
            SlotQuoteBorder => colors.QuoteBorder ?? "#000000",
            SlotLink => colors.Link ?? "#000000",
            SlotCheckBox => colors.CheckBox ?? "#000000",
            SlotTint => colors.Tint ?? "#000000",
            SlotDanger => colors.Danger ?? "#000000",
            _ => colors.Paper ?? "#000000"
        };
    }

    private static void SetSlot(ThemePaletteColors? colors, string slot, string hex)
    {
        if (colors == null)
        {
            return;
        }

        switch (slot)
        {
            case SlotPaper:
                colors.Paper = hex;
                break;
            case SlotPaperBorder:
                colors.PaperBorder = hex;
                break;
            case SlotText:
                colors.Text = hex;
                break;
            case SlotWeakText:
                colors.WeakText = hex;
                break;
            case SlotActive:
                colors.Active = hex;
                break;
            case SlotCode:
                colors.Code = hex;
                break;
            case SlotQuoteBorder:
                colors.QuoteBorder = hex;
                break;
            case SlotLink:
                colors.Link = hex;
                break;
            case SlotCheckBox:
                colors.CheckBox = hex;
                break;
            case SlotTint:
                colors.Tint = hex;
                break;
            case SlotDanger:
                colors.Danger = hex;
                break;
        }
    }

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

            // 海盐 — 干净的蓝绿纸面，适合长时间阅读和清爽桌面。
            [ColorSchemes.Ocean] = (
                new Palette
                {
                    Paper = Color.FromRgb(240, 250, 251),
                    PaperBorder = Color.FromRgb(184, 218, 224),
                    Text = Color.FromRgb(31, 53, 60),
                    WeakText = Color.FromRgb(94, 125, 132),
                    Active = Color.FromRgb(62, 142, 155),
                    Code = Color.FromRgb(226, 243, 246),
                    QuoteBorder = Color.FromRgb(166, 207, 216),
                    Link = Color.FromRgb(42, 121, 155),
                    CheckBox = Color.FromRgb(144, 190, 200),
                    Tint = Color.FromRgb(40, 122, 140),
                    Danger = Color.FromRgb(190, 88, 76),
                },
                new Palette
                {
                    Paper = Color.FromRgb(22, 33, 38),
                    PaperBorder = Color.FromRgb(48, 78, 86),
                    Text = Color.FromRgb(218, 236, 239),
                    WeakText = Color.FromRgb(128, 158, 164),
                    Active = Color.FromRgb(92, 178, 190),
                    Code = Color.FromRgb(31, 47, 53),
                    QuoteBorder = Color.FromRgb(62, 98, 108),
                    Link = Color.FromRgb(116, 200, 222),
                    CheckBox = Color.FromRgb(78, 112, 120),
                    Tint = Color.FromRgb(170, 225, 235),
                    Danger = Color.FromRgb(230, 118, 102),
                }),

            // 雾紫 — 轻柔紫灰纸面，保留低饱和度，避免刺眼。
            [ColorSchemes.Lavender] = (
                new Palette
                {
                    Paper = Color.FromRgb(248, 245, 253),
                    PaperBorder = Color.FromRgb(213, 202, 228),
                    Text = Color.FromRgb(47, 39, 58),
                    WeakText = Color.FromRgb(122, 110, 142),
                    Active = Color.FromRgb(128, 104, 170),
                    Code = Color.FromRgb(239, 233, 248),
                    QuoteBorder = Color.FromRgb(204, 190, 224),
                    Link = Color.FromRgb(112, 92, 178),
                    CheckBox = Color.FromRgb(190, 176, 214),
                    Tint = Color.FromRgb(112, 88, 160),
                    Danger = Color.FromRgb(184, 82, 100),
                },
                new Palette
                {
                    Paper = Color.FromRgb(30, 27, 38),
                    PaperBorder = Color.FromRgb(68, 58, 88),
                    Text = Color.FromRgb(229, 222, 238),
                    WeakText = Color.FromRgb(146, 134, 164),
                    Active = Color.FromRgb(168, 142, 214),
                    Code = Color.FromRgb(41, 36, 52),
                    QuoteBorder = Color.FromRgb(84, 72, 108),
                    Link = Color.FromRgb(190, 170, 238),
                    CheckBox = Color.FromRgb(92, 78, 116),
                    Tint = Color.FromRgb(210, 190, 245),
                    Danger = Color.FromRgb(226, 112, 132),
                }),

            // 沙丘 — 比暖纸更现代的沙色和琥珀强调，适合温和高对比桌面。
            [ColorSchemes.Dune] = (
                new Palette
                {
                    Paper = Color.FromRgb(251, 245, 232),
                    PaperBorder = Color.FromRgb(219, 197, 158),
                    Text = Color.FromRgb(55, 45, 32),
                    WeakText = Color.FromRgb(132, 114, 86),
                    Active = Color.FromRgb(178, 121, 50),
                    Code = Color.FromRgb(243, 232, 207),
                    QuoteBorder = Color.FromRgb(210, 184, 138),
                    Link = Color.FromRgb(168, 96, 36),
                    CheckBox = Color.FromRgb(188, 160, 110),
                    Tint = Color.FromRgb(150, 102, 42),
                    Danger = Color.FromRgb(184, 84, 64),
                },
                new Palette
                {
                    Paper = Color.FromRgb(36, 31, 24),
                    PaperBorder = Color.FromRgb(84, 68, 46),
                    Text = Color.FromRgb(236, 225, 206),
                    WeakText = Color.FromRgb(154, 136, 108),
                    Active = Color.FromRgb(216, 154, 72),
                    Code = Color.FromRgb(48, 41, 31),
                    QuoteBorder = Color.FromRgb(100, 80, 54),
                    Link = Color.FromRgb(232, 174, 92),
                    CheckBox = Color.FromRgb(112, 92, 62),
                    Tint = Color.FromRgb(232, 196, 130),
                    Danger = Color.FromRgb(228, 112, 92),
                }),
        };
    }
}

/*
=== 修改记录 ===
[修改编号]: 1
[修改日期]: 2026-06-21
[修改类型]: 新增功能
[主要内容]:
- 新增海盐、雾紫、沙丘三个内置配色方案。
- 新增自定义色盘配色方案和 HEX 颜色规范化、复制、读取、写入逻辑。
- 保留原有暖纸、墨、林、霞配色值不变。

[修改目的]:
- 支持用户在不破坏现有主题的前提下，选择更多外观风格并自定义纸面、边框、文字和强调色。

[影响范围]:
- 主题配色解析、纸片窗口画刷、托盘菜单画刷、设置窗口配色选择和 data.json 自定义色盘兼容。
*/
