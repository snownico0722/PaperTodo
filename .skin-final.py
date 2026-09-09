from pathlib import Path


def replace(path, old, new):
    file = Path(path)
    text = file.read_text(encoding='utf-8-sig')
    assert text.count(old) == 1, (path, old)
    file.write_text(text.replace(old, new), encoding='utf-8', newline='\n')


# Reflective colored surfaces need stronger secondary text, not weaker material.
# Keep original/default and native Mica/Acrylic semantic colors unchanged.
replace('src/Theme.cs',
    '    public static Brush WeakTextBrush => Solid(Current.WeakText);\n    public static Brush BrightWeakTextBrush => Solid(IsDark ? Lighten(Current.WeakText, 0.22) : Current.WeakText);',
    '''    private static Color SurfaceWeakText => PaperSkins.Decorate(Skin, SystemParameters.HighContrast)
        ? Mix(Current.WeakText, Current.Text, 0.36) : Current.WeakText;
    public static Brush WeakTextBrush => Solid(SurfaceWeakText);
    public static Brush BrightWeakTextBrush => Solid(IsDark ? Lighten(SurfaceWeakText, 0.22) : SurfaceWeakText);''')

# Actual WPF frames at their real interval, not synthetic interpolated art.
replace('tests/PaperTodo.MicaChecks/SkinChecks.cs',
    '''            for (var frame = 0; frame < 4; frame++)
            {
                Save(Render(window, 1), $"pearl-motion-{frame}");
                Wait(2100);
            }''',
    '''            for (var frame = 0; frame < 64; frame++)
            {
                Save(Render(window, 1), $"pearl-motion-{frame:D3}");
                Wait(125);
            }''')
replace('tests/PaperTodo.MicaChecks/SkinChecks.cs',
    '''                        $"surface glare must not wash out secondary text: {skin}/{mode}/{scale}");''',
    '''                        $"surface glare must not wash out secondary text: {skin}/{mode}/{scale}, text={readable.Color}, surface={litColor}");''')

# The old 12%-height sample landed at y=4 in a 40px capsule: inside the 5px
# empty lens rim, not behind a glyph. Expanded headers still sample their text zone.
replace('tests/PaperTodo.MicaChecks/SkinChecks.cs',
    '                    var litPixel = ((int)(image.PixelHeight * .12) * image.PixelWidth + image.PixelWidth / 3) * 4;',
    '''                    // Text occupies the central capsule band, not its specular outer edge.
                    var textHeight = capsule ? .5 : .12;
                    var litPixel = ((int)(image.PixelHeight * textHeight) * image.PixelWidth + image.PixelWidth / 3) * 4;''')
