from pathlib import Path
from xml.sax.saxutils import escape
strings = {
'ColorSchemeNeutral':['素灰','Neutral','ニュートラル','중성'],
'SettingsPaperSkin':['皮肤（实验）','Skin (experimental)','スキン（実験）','스킨 (실험)'],
'TipPaperSkin':['皮肤决定表面材质，配色决定正文和控件颜色。换肤不重建编辑器；高对比度时隐藏装饰。','Skins change the surface; color schemes control text and controls. Switching preserves editors. High contrast disables decoration.','スキンは素材、配色は文字と操作部の色を決めます。変更時にエディターは作り直しません。ハイコントラストでは装飾を隠します。','스킨은 표면 재질을, 색상 테마는 텍스트와 컨트롤 색을 바꿉니다. 편집기는 유지되며 고대비에서는 장식이 꺼집니다.'],
'SkinPaper':['默认纸片','Default paper','標準の紙','기본 메모'],
'SkinPearl':['珠光／镭射膜','Pearl / holographic foil','パール／ホログラム','진주 / 홀로그램'],
'SkinTracingPaper':['半透明描图纸','Tracing paper','半透明トレーシングペーパー','반투명 트레이싱지'],
'SkinLiquidGlass':['仿液态玻璃','Liquid glass (approximation)','リキッドガラス風','리퀴드 글래스 스타일'],
'SkinCeramic':['陶瓷／奶油釉面','Ceramic / cream glaze','陶器／クリーム釉','도자기 / 크림 유약'],
'SkinAero':['Aero 玻璃','Aero glass','Aero ガラス','Aero 유리'],
'SkinPixel':['像素风','Pixel','ピクセル','픽셀'],
'TipSkinPearl':['淡粉、青、紫偏光。拖动时反光缓慢变化；关闭动画后保持静态，不在空闲时持续刷新。','Pink, cyan and violet sheen shifts with movement. Disabling animations freezes it; there is no idle animation loop.','淡いピンク・シアン・紫の光沢が移動に合わせて変化します。アニメーション無効時は静止し、待機中の連続描画は行いません。','분홍·청록·보라 광택이 창 이동에 따라 변합니다. 애니메이션을 끄면 정지하며 유휴 상태에서는 계속 갱신하지 않습니다.'],
'TipSkinTracingPaper':['乳白底色和轻微纤维纹理，背景颜色隐约透入；正文保持清晰。','A milky wash and fine fibers let background colors softly show through while keeping text readable.','乳白色と細かな繊維模様で背景色をほのかに透かし、文字の読みやすさを保ちます。','유백색 바탕과 미세한 섬유 무늬 사이로 배경색이 은은하게 비치며 글자는 선명하게 유지됩니다.'],
'TipSkinLiquidGlass':['高光、厚边和透镜感的外观近似，胶囊更有水滴感；不包含真实背景折射。','A visual approximation with highlights and thick lens-like edges. Capsules resemble water droplets. No real background refraction.','ハイライトと厚い縁でレンズのような見た目を再現し、カプセルは水滴風になります。背景の実際の屈折は行いません。','하이라이트와 두꺼운 테두리로 렌즈 느낌을 내고 캡슐은 물방울처럼 표현합니다. 실제 배경 굴절은 지원하지 않습니다.'],
'TipSkinCeramic':['不透明的温润底色、柔和渐变和釉面高光，跟随当前配色。','Opaque, softly shaded ceramic with a glazed highlight, following your chosen palette.','選択した配色に合わせた不透明な陶器風の表面に、柔らかな陰影と釉薬の光沢を加えます。','선택한 색상에 맞춘 불투명한 도자기 표면에 부드러운 명암과 유약 광택을 더합니다.'],
'TipSkinAero':['亮边、淡蓝光泽和斜向反光的怀旧玻璃；使用现有模糊背景，不是 Win7 系统主题。','Bright rims, a cool sheen and diagonal reflections over the existing blurred backdrop. Not a Windows 7 system theme.','既存のぼかし背景に明るい縁、青い光沢、斜めの反射を重ねます。Windows 7 のシステムテーマではありません。','기존 흐린 배경에 밝은 테두리와 푸른 광택, 사선 반사를 더합니다. Windows 7 시스템 테마는 아닙니다.'],
'TipSkinPixel':['阶梯边框、硬边阴影和像素按钮；正文继续使用正常字体，避免中文难读。','Stepped borders, hard shadows and pixel buttons. Body text keeps its normal font for readability.','階段状の縁、くっきりした影、ピクセル風ボタン。本文は読みやすい通常のフォントを使います。','계단형 테두리, 선명한 그림자와 픽셀 버튼을 적용합니다. 본문은 가독성을 위해 일반 글꼴을 유지합니다.'],
'SkinRestartRequired':['皮肤已保存。请从托盘退出并重新打开，启用原生背景；当前先显示实色外观，不重建编辑内容或撤销记录。','Skin saved. Exit from the tray and reopen to enable the native backdrop. Existing windows use a solid approximation without rebuilding editors or undo history.','スキンを保存しました。トレイから終了し再起動するとネイティブ背景が有効になります。それまでは単色で表示し、編集内容や取り消し履歴は作り直しません。','스킨이 저장되었습니다. 트레이에서 종료한 뒤 다시 실행하면 네이티브 배경이 켜집니다. 현재 창은 단색으로 표시하며 편집 내용과 실행 취소 기록은 유지합니다.'],
'SkinNativeUnsupported':['原生背景需要 Windows 11 22H2（22621）及以上；当前使用不透明外观。','Native backdrops require Windows 11 22H2 (22621) or later. This system uses an opaque approximation.','ネイティブ背景には Windows 11 22H2（22621）以降が必要です。この環境では不透明な外観を使用します。','네이티브 배경에는 Windows 11 22H2 (22621) 이상이 필요합니다. 현재 환경에서는 불투명하게 표시됩니다.'],
'SkinNativeScope':['原生背景用于展开纸片和设置窗口。胶囊、形态动画和半透明状态使用实色底；关闭系统透明效果时也会回退。','Native backdrops apply to expanded papers and Settings. Capsules, shape transitions and partial opacity use solid bases; disabling system transparency also falls back.','ネイティブ背景は展開した紙と設定に適用されます。カプセル・形状アニメーション・半透明状態では単色の下地を使い、システムの透明効果が無効な場合も単色に戻ります。','네이티브 배경은 펼친 메모와 설정에 적용됩니다. 캡슐, 형태 전환, 부분 투명 상태에서는 단색 바탕을 사용하며 시스템 투명 효과가 꺼져도 단색으로 전환됩니다.']
}
for i, locale in enumerate(['','en','ja','ko']):
    p = Path('Resources') / ('Strings' + ('.' + locale if locale else '') + '.resx')
    s = p.read_text(encoding='utf-8-sig')
    assert 'name="SettingsPaperSkin"' not in s
    s = s.replace('</root>', '\n' + ''.join(f'  <data name="{key}" xml:space="preserve">\n    <value>{escape(values[i])}</value>\n  </data>\n' for key, values in strings.items()) + '</root>')
    p.write_text(s, encoding='utf-8')
for path, marker, text in [
('CHANGELOG.md', '- **原生云母皮肤**', '''- **皮肤实验**：在「设置 → 外观 → 皮肤」独立选择默认纸片、标准云母、标准亚克力、透色亚克力，以及珠光／镭射膜、半透明描图纸、仿液态玻璃、陶瓷／奶油釉面、Aero 玻璃和像素风；配色独立选择，旧云母配置自动保留。珠光随拖动轻微偏光，关闭动画后静止；像素风使用阶梯边框、硬边阴影和像素按钮，正文保留正常字体。仿液态玻璃仅近似高光与厚边，不包含真实折射。纸片、普通和贴边胶囊、拖动胶囊、主胶囊与系绳胶囊共用外壳绘制，不改变停靠或动画结构。
- **原生背景**：云母、两种亚克力、描图纸、Aero 和仿液态玻璃在展开纸片与设置窗口使用原生背景，可保持材质的激活外观。不读取或模拟壁纸。需要 Windows 11 22H2 及以上，首次启用需退出并重新打开；胶囊、形态动画、部分透明以及系统不支持或关闭透明效果时保留实色底，高对比度隐藏装饰。换肤不重建编辑器和撤销记录。'''),
('doc/CHANGELOG.en.md', '- **Native Mica skin**', '''- **Experimental skins**: Settings → Appearance → Skin separates surface material from color scheme. Keep default paper, native Mica and the two Acrylic options, or try pearl/holographic foil, tracing paper, liquid-glass approximation, ceramic glaze, Aero glass and pixel styling. Legacy Mica settings retain their appearance. Pearl sheen follows movement without an idle loop; pixel borders, shadows and buttons keep the normal body font. Liquid glass does not perform real refraction. Paper and capsule shells share painting without changing docking or transition ownership.
- **Native backdrops**: Expanded papers and Settings use native backdrops for Mica, Acrylic, tracing paper, Aero and liquid glass, with an optional always-active appearance. First activation requires an application restart on Windows 11 22H2 or later. Capsules, shape transitions, partial opacity and unsupported/disabled transparency retain solid bases; high contrast disables decoration. Switching preserves editors and undo history. No wallpaper sampling or runtime desktop capture.''')]:
    p = Path(path)
    s = p.read_text(encoding='utf-8-sig')
    a = s.index(marker)
    b = s.index('\n\n', a)
    p.write_text(s[:a] + text + s[b:], encoding='utf-8')
p = Path('doc/ARCHITECTURE.md')
s = p.read_text(encoding='utf-8-sig')
needle = '配色由 `Theme` 提供实色语义；'
assert needle in s
s = s.replace(needle, '''`AppState.PaperSkin` 保存表面材质，与 `ColorScheme` 的不透明语义配色分开。缺失字段时 `PaperSkins.Resolve` 从旧 `colorScheme=mica` 与 `MicaBackdropType` 迁移；显式未知皮肤回退默认纸片，旧配色 ID `mica` 保留为「素灰」，不再单独决定是否开启原生背景。`StateStore` 的既有规范化与保存路径持有新字段，不新增状态文件。

六种装饰皮肤由 `SkinBorder` 在已有 Border 的背景绘制中实现：描图纸纤维是固定小块平铺纹理；珠光仅在已加载窗口发生位置变化时量化更新高光，关闭动画或卸载时解绑；像素形状在当前 DPI 内像素对齐。它不管理尺寸、命中、窗口或形态动画；各 Edge surface 的原 shape/layout authority 和 DComp translation-only 边界不变。普通、边缘、跨边拖动、主与系绳胶囊使用不透明基底上的相应外观，不能看成原生半透明胶囊。仿液态玻璃只提供高光和厚边，无实时折射或背景采样。高对比度禁用装饰。

配色由 `Theme` 提供实色语义；''', 1)
s = s.replace('根据已保存的云母选择和系统支持','根据已保存的皮肤选择和系统支持',1)
s = s.replace('云母只让成功启用原生背景的窗口外壳透明','原生皮肤只让成功启用原生背景的窗口外壳透明',1)
s = s.replace('标准云母与标准亚克力使用 full glass','描图纸、Aero 与仿液态玻璃复用标准 Acrylic 原生背景，并在 WPF 外壳叠加各自外观；失败时保留不透明底，不进入透色亚克力的 accent 接法。标准云母与标准亚克力使用 full glass',1)
p.write_text(s, encoding='utf-8')
