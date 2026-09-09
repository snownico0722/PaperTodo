from pathlib import Path
from xml.sax.saxutils import escape
import re


def read(name):
    return Path(name).read_text(encoding='utf-8-sig')

def write(name, s):
    Path(name).write_text(s, encoding='utf-8', newline='\n')

def replace(name, old, new, count=1):
    s = read(name)
    assert s.count(old) == count, (name, old, s.count(old), count)
    write(name, s.replace(old, new))

p = 'src/NativeMicaBackdrop.cs'
s = read(p)
a = s.index('                // Keep WindowChrome in its glass-managed')
b = s.index('                if (LastHResult >= 0 && clear)', a)
s = s[:a] + '''                // First leave the old alpha recipe. Mica/system Acrylic then need a
                // full glass composition surface (-1). Setting this to zero turns WPF's
                // transparent pixels black; a white material wash merely disguises it.
                // Only accent Acrylic and the unblurred lens use zero physical margins.
                LastHResult = _native.DisableAlpha(hwnd);
                if (LastHResult >= 0)
                {
                    // Keep WindowChrome on its glass-managed, no-window-region path.
                    _windowChrome.GlassFrameThickness = new Thickness(-1);
                    LastHResult = _native.ExtendFrame(hwnd, clear || glass ? 0 : -1);
                }
                if (LastHResult >= 0) LastHResult = _native.SetDarkMode(hwnd, dark);
                if (LastHResult >= 0) LastHResult = _native.SetBackdrop(hwnd,
                    glass ? DwmMicaApi.None : MicaBackdropTypes.ToDwmBackdrop(_material));
                if (LastHResult >= 0 && glass) LastHResult = _native.EnableAlpha(hwnd);
''' + s[b:]
s = s.replace('            // With no extended glass and no WS_CAPTION it has no client-area band to paint.\n',
              '            // COLOR_DEFAULT allows the system material to continue below the WPF header.\n')
write(p, s)
replace('src/DwmMicaApi.cs',
'''        // The adapter supplies COLOR_DEFAULT for the caption and zero extended glass.
        // A fixed caption color draws an opaque band even behind a borderless WPF header.''',
'''        // Do not paint a fixed-color native caption underneath the custom material.
        // Frame extent is selected independently for system versus accent/alpha recipes.''')

p = 'src/PaperSkins.cs'
s = read(p).replace('    public const string Pearl = "pearl";\n', '')
s = s.replace('ClearAcrylic, Pearl, TracingPaper', 'ClearAcrylic, TracingPaper')
s = s.replace('Pearl or TracingPaper', 'TracingPaper')
s = s.replace('        Pearl => "SkinPearl", TracingPaper', '        TracingPaper')
write(p, s)
replace('src/AppController.Settings.Skins.cs',
        '                PaperSkins.Pearl => "TipSkinPearl", PaperSkins.TracingPaper',
        '                PaperSkins.TracingPaper')
p = 'src/SkinBorder.cs'
s = read(p).replace('SyncReflectionSubscription', 'SyncLensLight').replace('DetachReflection', 'DetachLensLight')
s = s.replace('        InitializeReflection();\n', '')
s = s.replace('    private Window? _reflectionWindow;\n', '')
write(p, s)
Path('src/SkinBorder.Reflection.cs').unlink()
write('src/SkinBorder.LensLight.cs', '''using System;
using System.Windows;
using System.Windows.Input;

namespace PaperTodo;

internal sealed partial class SkinBorder
{
    private Window? _lensWindow;
    internal bool HasLensLightSubscription => _lensWindow != null;

    // Pointer-only gloss. No idle animation clocks, movement timers or location hooks.
    private void SyncLensLight()
    {
        var window = !IsOutline && IsLoaded && IsVisible && !_highContrast && _animateReflection &&
            Skin == PaperSkins.LiquidGlass ? Window.GetWindow(this) : null;
        if (ReferenceEquals(window, _lensWindow)) return;
        DetachLensLight();
        _lensWindow = window;
        if (window == null) return;
        window.PreviewMouseMove += OnLensPointerMoved;
        window.Closed += OnLensClosed;
    }
    private void DetachLensLight()
    {
        if (_lensWindow != null)
        {
            _lensWindow.PreviewMouseMove -= OnLensPointerMoved;
            _lensWindow.Closed -= OnLensClosed;
        }
        _lensWindow = null;
        _lensLight.Center = _lensLight.GradientOrigin = new Point(.24, .05);
    }
    private void OnLensClosed(object? sender, EventArgs e) => DetachLensLight();
    private void OnLensPointerMoved(object sender, MouseEventArgs e)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || _lensWindow?.WindowState == WindowState.Minimized) return;
        var pointer = e.GetPosition(this);
        var point = new Point(Math.Clamp(pointer.X / ActualWidth, 0, 1), Math.Clamp(pointer.Y / ActualHeight, 0, 1));
        if ((point - _lensLight.Center).LengthSquared < .0004) return;
        _lensLight.Center = _lensLight.GradientOrigin = point;
    }
}
''')

p = 'src/SkinBorder.Materials.cs'
s = read(p)
a = s.index('            case PaperSkins.Pearl:')
b = s.index('            case PaperSkins.Aero:', a)
s = s[:a] + s[b:]
s = s.replace('    private Brush _header = Brushes.Transparent;',
'''    private Brush _header = Brushes.Transparent;
    private Brush _glazeTop = Brushes.Transparent, _glazeBottom = Brushes.Transparent;
    private Brush _glazeLeft = Brushes.Transparent, _glazeRight = Brushes.Transparent;''')
s = s.replace('            _ => (byte)(_dark ? 70 : 44)',
'''            // A visible lens veil, not a near-empty transparent window. The rear
            // detail remains sharp; opacity is independent of background blur.
            _ => (byte)(_dark ? 196 : 152)''')
s = s.replace('                // A clear, neutral lens, not a cream plate over system Acrylic.',
              '                // A translucent, neutral lens with a readable center; no frosted Acrylic.')
s = s.replace('                _fill = Frozen(new SolidColorBrush(WithAlpha(clear, alpha)));',
'''                _fill = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(WithAlpha(clear, opaque ? (byte)255 : (byte)Math.Min(255, alpha + 20)), 0),
                    new(WithAlpha(clear, alpha), .20), new(WithAlpha(clear, alpha), .80),
                    new(WithAlpha(clear, opaque ? (byte)255 : (byte)Math.Min(255, alpha + 12)), 1)
                }, new Point(0, 0), new Point(0, 1)));''')
a = s.index('            case PaperSkins.Ceramic:')
b = s.index('            case PaperSkins.Pixel:', a)
s = s[:a] + '''            case PaperSkins.Ceramic:
                // A warm porcelain body and broad specular shoulder, not a paper tint.
                // Smooth edge shading has no inner outline and cannot make another bezel.
                var glaze = Mix(paper, _dark ? Color.FromRgb(49, 51, 55) : Color.FromRgb(232, 218, 193), .72);
                _fill = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(Mix(glaze, Colors.White, _dark ? .03 : .22), 0),
                    new(glaze, .35), new(Mix(glaze, Colors.Black, _dark ? .10 : .025), 1)
                }, new Point(0, 0), new Point(.25, 1)));
                _shine = Frozen(new RadialGradientBrush(new GradientStopCollection
                {
                    new(White(_dark ? 20 : 165), 0), new(White(_dark ? 18 : 150), .30),
                    new(White(_dark ? 8 : 65), .63), new(Colors.Transparent, 1)
                }) { Center = new Point(.25, .02), GradientOrigin = new Point(.18, -.03), RadiusX = .90, RadiusY = .65 });
                _glazeTop = Gradient(0, White(_dark ? 38 : 195), 1, Colors.Transparent);
                _glazeBottom = Gradient(0, Colors.Transparent, 1, Color.FromArgb(_dark ? (byte)76 : (byte)48, 65, 50, 33));
                _glazeLeft = Frozen(new LinearGradientBrush(White(_dark ? 20 : 100), Colors.Transparent, 0));
                _glazeRight = Frozen(new LinearGradientBrush(Colors.Transparent, Color.FromArgb(_dark ? (byte)56 : (byte)32, 40, 34, 26), 0));
                break;
''' + s[b:]
s = s.replace('''        if (Skin == PaperSkins.Pixel && !IsCapsule && HeaderHeight > 0)''',
'''        if (Skin == PaperSkins.Ceramic)
        {
            // Blend into the one outer surface. No hollow nested rectangle/ring.
            var depth = Math.Min(IsCapsule ? 3 : 6, Math.Min(ActualWidth, ActualHeight) / 2);
            if (BorderThickness.Top > 0) dc.DrawRectangle(_glazeTop, null, new Rect(0, 0, ActualWidth, depth));
            if (BorderThickness.Bottom > 0) dc.DrawRectangle(_glazeBottom, null, new Rect(0, ActualHeight - depth, ActualWidth, depth));
            if (BorderThickness.Left > 0) dc.DrawRectangle(_glazeLeft, null, new Rect(0, 0, depth, ActualHeight));
            if (BorderThickness.Right > 0) dc.DrawRectangle(_glazeRight, null, new Rect(ActualWidth - depth, 0, depth, ActualHeight));
        }
        if (Skin == PaperSkins.Pixel && !IsCapsule && HeaderHeight > 0)''')
write(p, s)
replace('src/Theme.cs',
        '? Mix(Current.WeakText, Current.Text, 0.36) : Current.WeakText;',
        '? Mix(Current.WeakText, Current.Text, Skin == PaperSkins.LiquidGlass ? 0.80 : 0.36) : Current.WeakText;')

p = 'tests/PaperTodo.MicaChecks/Program.cs'
s = read(p)
s = s.replace('CaptionColor == unchecked((int)0xffffffff) && f.Api.FrameTop == 0',
              'CaptionColor == unchecked((int)0xffffffff) && f.Api.FrameTop == -1')
s = s.replace('no solid caption override or extended-glass stripe behind the material header',
              'system material requires full glass but no solid caption override')
s = s.replace('f.Api.Backdrop == 3 && f.Api.FrameTop == 0', 'f.Api.Backdrop == 3 && f.Api.FrameTop == -1')
s = s.replace('Assert(!Alpha && !ClearAcrylic, "system backdrop excludes fallback alpha and accent Acrylic")',
              'Assert(!Alpha && !ClearAcrylic && Glass, "system backdrop needs full glass and excludes alpha/accent")')
write(p, s)
p = 'tests/PaperTodo.MicaChecks/SkinChecks.cs'
s = read(p).replace('Count() == 10 && Decorated.Length == 6', 'Count() == 9 && Decorated.Length == 5')
s = s.replace('hashes.Count == 6, "six different surfaces, not renamed presets"',
              'hashes.Count == Decorated.Length, "different surfaces, not renamed presets"')
s = s.replace(' CheckReflectionLifecycle(controller);', ' CheckLensAndGlaze(controller);')
s = s.replace('六种材质 · 同一张纸', '材质切换 · 同一张纸')
a = s.index('    private static void CheckReflectionLifecycle(')
b = s.index('    private static RenderTargetBitmap Render(', a)
s = s[:a] + '''    private static void CheckLensAndGlaze(AppController controller)
    {
        foreach (var mode in new[] { "light", "dark" })
        {
            controller.State.Theme = mode; controller.State.PaperSkin = PaperSkins.LiquidGlass; Theme.Invalidate();
            var lens = new SkinBorder { Width = 240, Height = 160, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent };
            var image = Render(lens, 1); var bytes = Pixels(image);
            var i = (80 * image.PixelWidth + 120) * 4;
            var alpha = bytes[i + 3];
            Program.Assert(alpha is >= 128 and <= 220, "lens has a visible veil, neither bare clear nor opaque");
            foreach (var rear in new byte[] { 0, 255 })
            {
                byte Channel(int c) => (byte)Math.Min(255, bytes[i + c] + rear * (255 - alpha) / 255);
                var background = Color.FromRgb(Channel(2), Channel(1), Channel(0));
                Program.Assert(Contrast(((SolidColorBrush)Theme.TextBrush).Color, background) >= 4.5,
                    "primary lens text remains readable over black/white rear content");
                Program.Assert(Contrast(((SolidColorBrush)Theme.WeakTextBrush).Color, background) >= 3,
                    "secondary lens text remains readable over black/white rear content");
            }
            controller.State.PaperSkin = PaperSkins.Ceramic; Theme.Invalidate();
            var ceramic = new SkinBorder { Width = 240, Height = 160, CornerRadius = new CornerRadius(8),
                Background = Theme.PaperBrush, BorderBrush = Theme.PaperBorderBrush, BorderThickness = new Thickness(1) };
            var painted = Render(ceramic, 1); var pixels = Pixels(painted);
            var baseColor = ((SolidColorBrush)Theme.PaperBrush).Color;
            var bottom = (120 * painted.PixelWidth + 160) * 4;
            Program.Assert(pixels[bottom + 3] == 255 && Math.Abs(pixels[bottom] - baseColor.B) >= 14,
                "porcelain body differs visibly from flat default paper without adding an inset frame");
            Save(painted, $"porcelain-volume-{mode}");
        }
    }
''' + s[b:]
write(p, s)
p = 'tests/PaperTodo.MicaChecks/NativeSurfaceChecks.cs'
s = read(p).replace('PaperSkins.Aero, PaperSkins.Pearl, PaperSkins.Ceramic', 'PaperSkins.Aero, PaperSkins.Ceramic')
s = s.replace('''                        if (skin is PaperSkins.Mica or PaperSkins.Acrylic or PaperSkins.ClearAcrylic)''',
'''                        if (mode == "light" && skin == PaperSkins.Mica)
                            Program.Assert(Math.Min(b.R, Math.Min(b.G, b.B)) >= 120,
                                $"light Mica must not expose an uncomposited black underlay: {b}");
                        if (skin is PaperSkins.Mica or PaperSkins.Acrylic or PaperSkins.ClearAcrylic)''')
s = s.replace('values.Max() - values.Min() >= 130', 'values.Max() - values.Min() is >= 35 and <= 150')
s = s.replace('glass must reveal sharp rear detail, not an opaque or frosted plate',
              'glass must retain rear detail AND a visible veil, neither opaque nor invisible')
s = s.replace('resizing/collapse does not restore full glass or opaque backing',
              'resizing/collapse preserves the visible translucent lens')
write(p, s)

copy = {
'TipSkinLiquidGlass': [
'可见的半透明镜片底色、细线边缘反光，顶栏与纸面连成一体；不再是一扇近乎全透明的窗口。背景保持清晰，不含真实折射，不等同于苹果 Liquid Glass。',
'A visible translucent lens veil with fine edge reflections, continuous across header and body. Rear detail stays sharp. No real refraction; not equivalent to Apple Liquid Glass.',
'見える半透明のレンズ面と細い縁の反射が、ヘッダーから本文まで続きます。背後の細部は鮮明なままです。実際の屈折はなく、Apple Liquid Glass と同等ではありません。',
'눈에 보이는 반투명 렌즈 표면과 가는 가장자리 반사가 제목과 본문에 이어집니다. 뒤쪽 내용은 선명하게 유지됩니다. 실제 굴절은 없으며 Apple Liquid Glass와 같지 않습니다.'],
'TipSkinCeramic': [
'奶油色瓷面、宽幅釉面亮斑与柔和的凸面明暗；不透明，保留配色倾向，不叠加宽厚内套框。',
'Cream porcelain with a broad glazed highlight and softly rounded light/shade. Opaque, palette-aware, without a thick inset frame.',
'クリーム色の磁器に、広い釉薬のハイライトと柔らかな立体陰影を加えます。不透明で配色に追従し、厚い内枠はありません。',
'크림색 도자기에 넓은 유약 하이라이트와 부드러운 입체 명암을 적용합니다. 불투명하며 선택한 색상을 따르고 두꺼운 안쪽 틀은 없습니다.']}
for i, suffix in enumerate(['', '.en', '.ja', '.ko']):
    p = f'Resources/Strings{suffix}.resx'
    s = read(p)
    for key in ['SkinPearl', 'TipSkinPearl']:
        s, n = re.subn(r'  <data name="' + key + r'"[^>]*>.*?</data>\n', '', s, flags=re.S)
        assert n == 1, (p, key)
    for key, values in copy.items():
        s, n = re.subn(r'(<data name="' + key + r'"[^>]*>\s*<value>).*?(</value>)',
                       lambda m: m[1] + escape(values[i]) + m[2], s, flags=re.S)
        assert n == 1, (p, key)
    write(p, s)
p = 'CHANGELOG.md'
s = read(p)
s = re.sub(r'^- \*\*皮肤实验\*\*：.*$',
'''- **皮肤实验**：在「设置 → 外观 → 皮肤」选择默认纸片、标准云母、标准亚克力、透色亚克力、半透明描图纸、仿液态玻璃、陶瓷／奶油釉面、Aero 玻璃和像素风。五种装饰皮肤可独立配色，标准原生材质保留素灰配色，切换不会覆盖已保存的配色。材质连续延伸到顶栏；描图纸带乳白纤维纹理，Aero 带冷色玻璃光泽，仿液态玻璃保留可见的半透明镜片底色和指针反光，陶瓷有奶油色瓷面、釉面亮斑和凸面明暗，像素风使用紧凑阶梯角、方形控件和物理像素图标。正文保留普通字体，外壳不叠加宽厚内套框。仿液态玻璃不包含真实背景折射。''', s, flags=re.M)
write(p, s)
p = 'doc/CHANGELOG.en.md'
s = read(p)
s = re.sub(r'^- \*\*Experimental skins\*\*:.*$',
'''- **Experimental skins**: Settings → Appearance → Skin offers default paper, Mica, both Acrylic options, tracing paper, liquid-glass styling, ceramic glaze, Aero glass and pixel styling. Five decorative skins support the independently saved color scheme; native system skins retain their neutral palette. Material continues beneath the header. Tracing paper has fine fibers; Aero has cool glass reflections; liquid glass has a visible translucent lens veil and pointer highlights; ceramic has a creamy body, broad glaze and rounded light/shade; pixel styling uses compact stepped corners, square controls and physical-pixel icons. Body text keeps its normal font. No thick inset frames or real background refraction.''', s, flags=re.M)
write(p, s)
p = 'doc/ARCHITECTURE.md'
s = read(p)
a = s.index('六种装饰皮肤由 `SkinBorder`')
b = s.index('\n\n配色由 `Theme`', a)
s = s[:a] + '''五种装饰皮肤由 `SkinBorder` 在已有 Border 的背景绘制中实现：描图纸使用固定纤维纹理，像素形状与 `PixelIconElement` 按物理像素对齐，材质画刷由 `SkinBorder.Materials` 持有。`SkinBorder.LensLight` 仅监听液态皮肤可见窗口的指针以改变局部高光；没有空闲动画、窗口位置监听或逐帧时钟。关闭动画、高对比度、隐藏、卸载或切换皮肤时解除监听。它不管理尺寸、命中、窗口或形态动画；Edge 的既有 shape/layout 和 DComp translation-only 边界不变。普通与各类胶囊保留实色基底，液态皮肤在展开态使用有可见遮色的 alpha 镜片，不做实时背景折射或采样。高对比度隐藏装饰。外轮廓由当前尺寸／圆角／DPI 缓存；焦点描边和贴边开口继续服从 host，不叠加宽内套框。''' + s[b:]
s = s.replace('标准云母与标准亚克力使用 `DWMWA_SYSTEMBACKDROP_TYPE`',
              '标准云母与标准亚克力先关闭 legacy alpha，再恢复 full glass（-1），随后设置 `DWMWA_SYSTEMBACKDROP_TYPE`')
s = s.replace('适配器在原生状态刷新末端将实际 DWM glass margin 归零，不占用顶部一条像素，也不增加第二套 NCCALCSIZE 或形状修改。',
              '只有透色 accent 与 clearGlass alpha recipe 将实际 DWM glass margin 归零，系统 Mica/Acrylic 必须保留 full glass；不增加第二套 NCCALCSIZE 或形状修改。')
s = s.replace('（见 D-035）', '（见 D-036）')
write(p, s)
p = 'doc/DECISIONS.md'
s = read(p)
s = s.replace('| D-035 | 自绘材质顶栏使用零物理 glass，清透皮肤分离 alpha recipe | Experimental |',
              '| D-035 | 自绘材质顶栏使用零物理 glass，清透皮肤分离 alpha recipe | Superseded by D-036 |')
lines = s.splitlines()
last = max(i for i,l in enumerate(lines) if l.startswith('| D-'))
lines.insert(last + 1, '| D-036 | 系统材质保留 full glass，清透接法的零边距不通用 | Accepted | 主题 / Window integration |')
s = '\n'.join(lines) + '\n'
s = s.replace('## D-035 — 自绘材质顶栏不再叠加 native caption，清透玻璃不用磨砂\n',
              '## D-035 — 自绘材质顶栏不再叠加 native caption，清透玻璃不用磨砂\n\n**Status:** Superseded by D-036（仅纠正所有材质统一零 glass 的选择）。\n')
s += '''
---

## D-036 — 系统材质保留 full glass，清透接法的零边距不通用

**Status:** Accepted

**Context / Why:** `d36a4f47` 把全部材质的实际 DWM glass margin 归零，同时关闭 legacy alpha。用户反馈云母纯黑、带半透明画刷的亚克力／描图纸／Aero 为深灰：透明 WPF 像素没有系统材质承接，白色覆盖层只能把黑底混成灰底。原生 API 成功、顶栏与正文同色，都不能证明背景已正确合成。独立探针原本使用 full glass，不能据此推导所有接法都应清零。

**Decision:** 恢复系统 Mica/Acrylic 的 full glass，保持清理旧 alpha → 设置对应 glass → 启用系统 backdrop 的顺序；零实际边距只属于 accent 与清透 alpha 接法。caption 颜色仍用默认值，不恢复实色顶栏遮盖。窗口、编辑器、形态动画和 Edge authority 不变。

**Evidence:** `NativeMicaBackdrop.Refresh` 与 `PaperTodo.MicaChecks` 的 full-glass 互斥检查、浅色云母黑底拒绝检查及最终桌面捕获。液态皮肤同时检查背景细节与可见遮色范围，不能再让一扇近乎隐形的窗口通过“有透明效果”检查。Windows Server 无法显示真实 Acrylic 透色时仍记录 SKIP，不冒充 Windows 11 真机验收。
'''
write(p, s)
print('Applied native surface repair, retired material removal, and lens/porcelain refinements.')
