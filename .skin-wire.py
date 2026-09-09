from pathlib import Path
import re
root = Path('.')
def edit(path, old, new, count=1):
    p = root / path
    s = p.read_text(encoding='utf-8-sig')
    assert s.count(old) >= count, (path, old)
    p.write_text(s.replace(old, new, count), encoding='utf-8')

edit('src/Models.cs','    public string MicaBackdropType','    // Null is the legacy migration sentinel; an explicit "paper" never implies Mica.\n    public string? PaperSkin { get; set; }\n    public string MicaBackdropType')
edit('src/StateStore.cs','        state.ColorScheme = ColorSchemes.Normalize(state.ColorScheme);','        state.PaperSkin = PaperSkins.Resolve(state);\n        state.ColorScheme = ColorSchemes.Normalize(state.ColorScheme);')
edit('src/Theme.cs','    public const string Mica = "mica";','    // Keep the stored ID so older Mica settings retain their original neutral palette.\n    public const string Neutral = "mica";\n    public const string Mica = Neutral;')
edit('src/Theme.cs','            if (IsMica && SystemParameters.HighContrast)','            if (SystemParameters.HighContrast && (Skin != PaperSkins.Paper || CurrentScheme == ColorSchemes.Neutral))')
edit('src/Theme.cs','    public static bool IsMica => CurrentScheme == ColorSchemes.Mica;','    public static string Skin => PaperSkins.Resolve(AppController.Current?.State);\n    public static bool UsesNativeBackdrop => PaperSkins.UsesNativeBackdrop(Skin);\n    public static bool IsPixelSkin => Skin == PaperSkins.Pixel && !SystemParameters.HighContrast;')
edit('src/AppController.cs','        UsesNativeMicaWindows = State.ColorScheme == ColorSchemes.Mica && NativeMicaBackdrop.IsSupported;','        State.PaperSkin = PaperSkins.Resolve(State);\n        UsesNativeMicaWindows = PaperSkins.UsesNativeBackdrop(State.PaperSkin) && NativeMicaBackdrop.IsSupported;')
for name in ['src/PaperWindow.cs','src/AppController.Settings.cs','src/AppController.SettingsSidebar.cs']:
    p = root / name
    s = p.read_text(encoding='utf-8-sig').replace('Theme.IsMica','Theme.UsesNativeBackdrop')
    s = s.replace('_controller.State.MicaBackdropType, Theme.IsDark)', 'PaperSkins.NativeBackdrop(Theme.Skin), Theme.IsDark)')
    s = s.replace('Theme.IsDark, _controller.State.MicaBackdropType,', 'Theme.IsDark, PaperSkins.NativeBackdrop(Theme.Skin),')
    s = s.replace('Theme.IsDark, State.MicaBackdropType,', 'Theme.IsDark, PaperSkins.NativeBackdrop(Theme.Skin),')
    p.write_text(s, encoding='utf-8')
p = root / 'src/AppController.Settings.cs'
s = p.read_text(encoding='utf-8')
a = s.index('    private void SetMicaBackdrop(')
b = s.index('    private void ToggleMicaAlwaysActive()', a)
s = s[:a] + s[b:]
a = s.index('    private UIElement CreateMicaBackdropSegmentSelector()')
b = s.index('    private void SetUiFontPreset(', a)
s = s[:a] + s[b:]
s = s.replace('(ColorSchemes.Mica, Strings.Get("ColorSchemeMica"))', '(ColorSchemes.Neutral, Strings.Get("ColorSchemeNeutral"))')
a = s.index('        leftColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsColorScheme")),')
b = s.index('        leftColumn.Children.Add(WrapWithHint(\n            SettingsFieldLabel(Strings.Get("SettingsResizeGripMode"))', a)
s = s[:a] + '''        leftColumn.Children.Add(WrapWithHint(SettingsFieldLabel(Strings.Get("SettingsColorScheme")), "TipColorScheme"));
        leftColumn.Children.Add(CreateColorSchemeSegmentSelector());
        leftColumn.Children.Add(CreateSkinSettings());
''' + s[b:]
s = s.replace('        State.ColorScheme = ColorSchemes.Warm;', '        State.ColorScheme = ColorSchemes.Warm;\n        State.PaperSkin = PaperSkins.Paper;')
a = s.index('        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)\n', s.index('QueueNativeMicaPreferenceRefresh();'))
b = s.index('\n    private void ToggleStartup()', a)
s = s[:a] + '    }\n' + s[b:]
a = s.index('    private void ToggleAnimations()')
b = s.index('\n    private ', a + 10)
block = s[a:b]
assert 'SaveNow();' in block
s = s[:a] + block.replace('        SaveNow();', '        SaveNow();\n        RefreshSkinSurfaces();') + s[b:]
s = s.replace('            QueueNativeMicaPreferenceRefresh();\n        }\n\n    }','            QueueNativeMicaPreferenceRefresh();\n        }\n    }')
p.write_text(s, encoding='utf-8')
p = root / 'src/AppController.Mica.cs'
s = p.read_text(encoding='utf-8').replace('IsExiting || State.ColorScheme != ColorSchemes.Mica || _nativeMicaPreferenceRefreshQueued','IsExiting || _nativeMicaPreferenceRefreshQueued').replace('IsExiting || State.ColorScheme != ColorSchemes.Mica','IsExiting')
s = s.replace('    private void QueueNativeMicaPreferenceRefresh()', '    // Also refresh decorated skins in fixed light/dark mode when accessibility changes.\n    private void QueueNativeMicaPreferenceRefresh()')
p.write_text(s, encoding='utf-8')
for name, old, new in [
    ('src/PaperWindow.cs','_paperChrome = new Border','_paperChrome = new SkinBorder'),
    ('src/EdgeCapsuleHost.cs','var chrome = new Border\n        {','var chrome = new SkinBorder\n        {\n            IsCapsule = true,'),
    ('src/EdgeCapsuleHost.cs','var outline = new Border\n        {','var outline = new SkinBorder\n        {\n            IsOutline = true, IsCapsule = true,'),
    ('src/EdgeCapsuleDragWindow.cs','_paperBackground = new Border\n        {','_paperBackground = new SkinBorder\n        {\n            IsCapsule = true,'),
    ('src/EdgeCapsuleDragWindow.cs','_outline = new Border\n        {','_outline = new SkinBorder\n        {\n            IsOutline = true, IsCapsule = true,'),
    ('src/MasterCapsuleWindow.cs','_pill = new Border\n        {','_pill = new SkinBorder\n        {\n            IsCapsule = true,'),
    ('src/ExperimentalTetherCapsuleWindow.cs','_pill = new Border\n        {','_pill = new SkinBorder\n        {\n            IsCapsule = true,'),
    ('src/AppController.SettingsSidebar.cs','var frame = new Border\n        {','var frame = new SkinBorder\n        {')]:
    edit(name, old, new)
edit('src/PaperWindow.cs','        var snappedExpanded = _isSnappedPresentation && !_paper.IsCollapsed;', '        if (_paperChrome is SkinBorder skin) skin.IsCapsule = _paper.IsCollapsed && _controller.State.UseCapsuleMode;\n        var snappedExpanded = _isSnappedPresentation && !_paper.IsCollapsed;')
edit('src/PaperWindow.cs','    private static Brush TitleBarBrush => Theme.Tint((byte)(Theme.IsDark ? 18 : 12));','    private static Brush TitleBarBrush => PaperSkins.IsDecorated(Theme.Skin) && !SystemParameters.HighContrast\n        ? Brushes.Transparent : Theme.Tint((byte)(Theme.IsDark ? 18 : 12));')
edit('src/PaperWindow.cs','    public void UpdateTheme()\n    {','    public void UpdateTheme()\n    {\n        RefreshSkin();')
edit('src/PaperWindow.cs','        var canAnimateTheme = _nativeMica == null && _controller.State.EnableAnimations &&','        var canAnimateTheme = !PaperSkins.IsDecorated(Theme.Skin) && _nativeMica == null && _controller.State.EnableAnimations &&')
edit('src/PaperWindow.cs','        Resources["PaperBrushKey"] = PaperBrush;', '        Resources["SkinButtonRadiusKey"] = new CornerRadius(Theme.IsPixelSkin ? 0 : RadiusControl);\n        Resources["SkinButtonPressedOffsetKey"] = new TranslateTransform(0, Theme.IsPixelSkin ? 1 : 0);\n        Resources["PaperBrushKey"] = PaperBrush;')
p = root / 'src/PaperWindow.cs'
s = p.read_text(encoding='utf-8')
a = s.index('    private static Style BuildIconButtonStyle()')
b = s.index('    private Style CurrentTodoCheckBoxStyle()', a)
block = s[a:b].replace('new CornerRadius(RadiusControl)', 'new DynamicResourceExtension("SkinButtonRadiusKey")')
block = block.replace('        template.Triggers.Add(mouseOver);', '        pressed.Setters.Add(new Setter(UIElement.RenderTransformProperty, new DynamicResourceExtension("SkinButtonPressedOffsetKey"), "Bd"));\n        template.Triggers.Add(mouseOver);')
s = s[:a] + block + s[b:]
a = s.index('    private static Style BuildCustomCheckBoxStyle()')
b = s.index('    public PaperWindow(', a)
block = s[a:b].replace('new CornerRadius(AppTypography.Scale(RadiusSmall))','new CornerRadius(Theme.IsPixelSkin ? 0 : AppTypography.Scale(RadiusSmall))')
block = block.replace('Geometry.Parse("M 3,7.5 L 6.5,11 L 13,4")','Geometry.Parse(Theme.IsPixelSkin ? "M 3,7 L 3,9 L 5,9 L 5,11 L 7,11 L 7,9 L 9,9 L 9,7 L 11,7 L 11,5 L 13,5 L 13,3" : "M 3,7.5 L 6.5,11 L 13,4")')
block = block.replace('PenLineCap.Round', 'Theme.IsPixelSkin ? PenLineCap.Square : PenLineCap.Round').replace('PenLineJoin.Round','Theme.IsPixelSkin ? PenLineJoin.Miter : PenLineJoin.Round')
block = block.replace('        path.Name = "CheckMark";', '        path.Name = "CheckMark";\n        path.SetValue(RenderOptions.EdgeModeProperty, Theme.IsPixelSkin ? EdgeMode.Aliased : EdgeMode.Unspecified);')
s = s[:a] + block + s[b:]
p.write_text(s, encoding='utf-8')
edit('src/EdgeCapsuleHost.cs','        Chrome.Background = paperBrush;', '        SkinBorder.Refresh(Chrome);\n        SkinBorder.Refresh(Outline);\n        Chrome.Background = paperBrush;')
edit('src/EdgeCapsuleDragWindow.cs','        _paperBackground.Background = options.PaperBrush;', '        SkinBorder.Refresh(_paperBackground);\n        SkinBorder.Refresh(_outline);\n        _paperBackground.Background = options.PaperBrush;')
for name in ['src/MasterCapsuleWindow.cs','src/ExperimentalTetherCapsuleWindow.cs']:
    edit(name, '        _pill.Background = Theme.PaperBrush;', '        SkinBorder.Refresh(_pill);\n        _pill.Background = Theme.PaperBrush;')
edit('src/EdgeCapsuleHost.cs','    public void UpdateTheme(\n','    internal void RefreshSkin()\n    {\n        if (_disposed) return;\n        SkinBorder.Refresh(Chrome);\n        Chrome.Effect = SkinBorder.CreateShadow(4, 0, 0.1);\n        SkinBorder.Refresh(Outline);\n    }\n\n    public void UpdateTheme(\n')
edit('src/PaperWindow.cs','''        return new DropShadowEffect
        {
            BlurRadius = blurRadius,
            ShadowDepth = shadowDepth,
            Opacity = opacity
        };''','        return SkinBorder.CreateShadow(blurRadius, shadowDepth, opacity);')
edit('src/PaperWindow.cs','        RefreshNativeMica(force: true);\n        RefreshPaperTitle();','        RefreshNativeMica(force: true);\n        if (!IsPaperFormTransitioning) ApplyPaperChromePresentation();\n        RefreshPaperTitle();')
for name, var, blur, depth, opacity in [('src/EdgeCapsuleHost.cs','Chrome',4,0,.1),('src/EdgeCapsuleDragWindow.cs','_paperBackground',8,1,.12),('src/MasterCapsuleWindow.cs','_pill',4,0,.1)]:
    p = root / name
    s = p.read_text(encoding='utf-8')
    s, n = re.subn(r'Effect = new DropShadowEffect\s*\{\s*BlurRadius = \d+,\s*ShadowDepth = \d+,\s*Opacity = [\d.]+\s*\}', f'Effect = SkinBorder.CreateShadow({blur}, {depth}, {opacity})', s)
    assert n == 1, (name, n)
    if var != 'Chrome':
        needle = f'        SkinBorder.Refresh({var});'
        assert needle in s
        s = s.replace(needle, needle + f'\n        {var}.Effect = SkinBorder.CreateShadow({blur}, {depth}, {opacity});', 1)
    p.write_text(s, encoding='utf-8')
edit('src/PaperWindow.TopBarVectorIcons.cs','    private static FrameworkElement CreateTopBarNewTodoIcon(Button owner)\n    {','    private static FrameworkElement CreateTopBarNewTodoIcon(Button owner)\n    {\n        if (Theme.IsPixelSkin) return CreatePixelIcon(owner,\n            "..#.........#", "..#........##", "#####.....##.", "..#...#..##..", "..#...####...", ".......##....");')
edit('src/PaperWindow.TopBarVectorIcons.cs','    private static FrameworkElement CreateTopBarNewNoteIcon(Button owner)\n    {','    private static FrameworkElement CreateTopBarNewNoteIcon(Button owner)\n    {\n        if (Theme.IsPixelSkin) return CreatePixelIcon(owner,\n            "..#.......##.", "..#......####", "#####...####.", "..#....####..", "..#....###...", ".......#.....");')
edit('src/PaperWindow.TopBarVectorIcons.cs','        bool collapse)\n    {','        bool collapse)\n    {\n        if (Theme.IsPixelSkin) return collapse\n            ? CreatePixelIcon(owner, "########", "########")\n            : CreatePixelIcon(owner, "##....##", ".##..##.", "..####..", "...##...", "..####..", ".##..##.", "##....##");')
edit('src/PaperWindow.cs','    private static FrameworkElement CreateTopmostPinIcon(Button owner, bool pinned)\n    {','    private static FrameworkElement CreateTopmostPinIcon(Button owner, bool pinned)\n    {\n        if (Theme.IsPixelSkin) return pinned\n            ? CreatePixelIcon(owner, "..####..", "...##...", "..####..", ".######.", "...##...", "...##...", "...#....")\n            : CreatePixelIcon(owner, "..####..", "..#..#..", "..#..#..", ".######.", "...##...", "...##...", "...#....");')
edit('tests/PaperTodo.MicaChecks/Program.cs','                controller.State.EnableAnimations = false;','                controller.State.PaperSkin = null; // Exercise the pre-skin Mica settings format.\n                controller.State.EnableAnimations = false;')
edit('tests/PaperTodo.MicaChecks/VisualChecks.cs','        controller.State.ColorScheme = ColorSchemes.Mica;','        controller.State.PaperSkin = null; // Material permutations below cover legacy settings.\n        controller.State.ColorScheme = ColorSchemes.Mica;')
edit('tests/PaperTodo.MicaChecks/Program.cs','            }\n            Console.WriteLine($"Native Mica behavior checks passed: {_passed}.");','                Check("experimental skins", () => SkinChecks.Run(controller));\n            }\n            Console.WriteLine($"Native Mica behavior checks passed: {_passed}.");')
