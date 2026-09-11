using System.Globalization;
using System.IO;
using System.Resources;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static class SkinChecks
{
    private static readonly string[] Decorated = PaperSkins.All.Where(PaperSkins.IsDecorated).ToArray();
    internal static void Run(AppController controller)
    {
        Program.Assert(PaperSkins.All.Distinct().Count() == 8 && Decorated.Length == 4, "unique skin choices");
        Program.Assert(PaperSkins.Resolve(null, "mica", "clearAcrylic") == PaperSkins.ClearAcrylic, "legacy clear Acrylic");
        Program.Assert(PaperSkins.Resolve(null, "mica", "micaAlt") == PaperSkins.Mica, "retired material migration");
        Program.Assert(PaperSkins.Resolve(null, "forest", "acrylic") == PaperSkins.Paper, "ordinary legacy palette");
        foreach (var id in new[] { PaperSkins.Paper, "future", "ceramic", "" })
            Program.Assert(PaperSkins.Resolve(id, "mica", "acrylic") == PaperSkins.Paper, "explicit choice wins over legacy palette");
        foreach (var id in PaperSkins.All)
            Program.Assert(PaperSkins.IsValid(id) && PaperSkins.Normalize(id) == id && !PaperSkins.Decorate(id, true), "valid IDs / high contrast");
        foreach (var id in new[] { PaperSkins.TracingPaper })
            Program.Assert(PaperSkins.UsesNativeBackdrop(id) && PaperSkins.NativeBackdrop(id) == MicaBackdropTypes.Acrylic, "supported Acrylic recipe");
        Program.Assert(PaperSkins.NativeBackdrop(PaperSkins.LiquidGlass) == NativeMicaBackdrop.ClearGlassMaterial,
            "liquid glass no longer maps to frosted Acrylic");
        Program.Assert(PaperSkins.NativeBackdrop(PaperSkins.Aero) == NativeMicaBackdrop.AeroGlassMaterial, "Aero selects its clear native glass recipe");
        CheckPersistence();
        var resources = new ResourceManager("PaperTodo.Resources.Strings", typeof(Strings).Assembly);
        foreach (var culture in new[] { "", "en", "ja", "ko" })
        {
            var set = resources.GetResourceSet(CultureInfo.GetCultureInfo(culture), true, false)!;
            foreach (var key in PaperSkins.All.Select(PaperSkins.LabelKey).Append("SettingsPaperSkin").Append("SkinRestartRequired").Append("SkinSystemPalette").Append("SettingsLiveRefraction").Append("TipLiveRefraction").Append("SettingsMatchAuxiliaryMaterial").Append("TipMatchAuxiliaryMaterial"))
                Program.Assert(!string.IsNullOrWhiteSpace(set.GetString(key)), $"localized {culture}/{key}");
        }
        var before = (controller.State.PaperSkin, controller.State.ColorScheme, controller.State.Theme, controller.State.EnableAnimations);
        try
        {
            CheckOriginalNativeRendering(controller); CheckAuxiliaryMaterials(controller);
            controller.State.ColorScheme = ColorSchemes.Warm;
            controller.State.EnableAnimations = false;
            var samples = 0;
            foreach (var mode in new[] { "light", "dark" })
            foreach (var capsule in new[] { false, true })
            foreach (var scale in new[] { 1.0, 1.25, 1.5 })
            {
                var hashes = new HashSet<string>();
                foreach (var skin in Decorated)
                {
                    controller.State.PaperSkin = skin; controller.State.Theme = mode; Theme.Invalidate();
                    var border = new SkinBorder
                    {
                        Width = 240, Height = capsule ? 40 : 160, IsCapsule = capsule,
                        CornerRadius = new CornerRadius(capsule ? 20 : 16), Background = Theme.PaperBrush,
                        BorderBrush = Theme.PaperBorderBrush, BorderThickness = new Thickness(1)
                    };
                    var image = Render(border, scale); var bytes = Pixels(image);
                    var center = (image.PixelHeight / 2 * image.PixelWidth + image.PixelWidth / 2) * 4;
                    Program.Assert(bytes[center + 3] == 255, $"opaque fallback {skin}/{mode}/{scale}");
                    Program.Assert(bytes[3] == 0, "corner does not paint transparent capacity");
                    var readable = (SolidColorBrush)Theme.WeakTextBrush;
                    // Text occupies the central capsule band, not its specular outer edge.
                    var textHeight = capsule ? .5 : .12;
                    var litPixel = ((int)(image.PixelHeight * textHeight) * image.PixelWidth + image.PixelWidth / 3) * 4;
                    var litColor = Color.FromRgb(bytes[litPixel + 2], bytes[litPixel + 1], bytes[litPixel]);
                    Program.Assert(Contrast(readable.Color, litColor) >= 3,
                        $"surface glare must not wash out secondary text: {skin}/{mode}/{scale}, text={readable.Color}, surface={litColor}");
                    hashes.Add(Convert.ToHexString(SHA256.HashData(bytes)));
                    Save(image, $"{skin}-{mode}-{(capsule ? "capsule" : "paper")}-{scale:0.##}");
                    if (PaperSkins.UsesNativeBackdrop(skin))
                    {
                        border.Background = NativeMicaBackdrop.GetActiveSurfaceBrush(PaperSkins.NativeBackdrop(skin), mode == "dark");
                        Program.Assert(Pixels(Render(border, scale))[center + 3] is > 0 and < 255, "native fill allows compositor through");
                    }
                    samples++;
                }
                Program.Assert(hashes.Count == Decorated.Length, "different surfaces, not renamed presets");
            }
            CheckPixelGeometry(); CheckDockedOutline(controller); CheckLiveSwitch(controller); CheckGlassAndAero(controller);
            Console.WriteLine($"PASS skins: persistence, four locales, {samples} raster/contrast cases, original native pixels, open-edge focus borders and editor identity.");
        }
        finally
        {
            (controller.State.PaperSkin, controller.State.ColorScheme, controller.State.Theme, controller.State.EnableAnimations) = before;
            Theme.Invalidate();
        }
    }
    private static void CheckOriginalNativeRendering(AppController controller)
    {
        controller.State.ColorScheme = ColorSchemes.Neutral;
        foreach (var mode in new[] { "light", "dark" })
        foreach (var skin in new[] { PaperSkins.Mica, PaperSkins.Acrylic, PaperSkins.ClearAcrylic })
        {
            controller.State.Theme = mode; controller.State.PaperSkin = skin; Theme.Invalidate();
            var expected = mode == "light" ? Color.FromRgb(243, 243, 243) : Color.FromRgb(32, 32, 32);
            Program.Assert(((SolidColorBrush)Theme.PaperBrush).Color == expected, "native palette matches the original Mica branch");
            foreach (var background in new[] { Theme.PaperBrush, NativeMicaBackdrop.GetActiveSurfaceBrush(skin, mode == "dark") })
            {
                var original = new Border { Width = 180, Height = 100, CornerRadius = new CornerRadius(8),
                    Background = background, BorderBrush = Theme.PaperBorderBrush, BorderThickness = new Thickness(1) };
                var current = new SkinBorder { Width = original.Width, Height = original.Height, CornerRadius = original.CornerRadius,
                    Background = background, BorderBrush = original.BorderBrush, BorderThickness = original.BorderThickness };
                Program.Assert(Pixels(Render(original, 1)).SequenceEqual(Pixels(Render(current, 1))),
                    "native skins add no decorative wash or replacement border pixels");
            }
        }
        foreach (var scheme in ColorSchemes.All)
        foreach (var mode in new[] { "light", "dark" })
        {
            controller.State.PaperSkin = PaperSkins.Paper; controller.State.ColorScheme = scheme;
            controller.State.Theme = mode; Theme.Invalidate();
            var paperColor = ((SolidColorBrush)Theme.PaperBrush).Color;
            var textColor = ((SolidColorBrush)Theme.TextBrush).Color;
            foreach (var skin in new[] { PaperSkins.Mica, PaperSkins.Acrylic, PaperSkins.ClearAcrylic })
            {
                controller.State.PaperSkin = skin; Theme.Invalidate();
                Program.Assert(((SolidColorBrush)Theme.PaperBrush).Color == paperColor &&
                    ((SolidColorBrush)Theme.TextBrush).Color == textColor && textColor.A == 255,
                    "native material honors the independent saved palette with opaque semantic text");
                var tint = ((SolidColorBrush)Theme.NativeMaterialTint).Color;
                Program.Assert(scheme == ColorSchemes.Neutral ? tint.A == 0 : tint.A is > 0 and < 32,
                    "neutral leaves native pixels alone; selected colors add a light tint, not an opaque replacement");
                var selector = (UIElement)typeof(AppController).GetMethod("CreateColorSchemeSegmentSelector", Program.Private)!.Invoke(controller, null)!;
                Program.Assert(selector.IsEnabled, "native material palette selector remains enabled");
            }
        }
        controller.State.ColorScheme = ColorSchemes.Warm;
        controller.State.PaperSkin = PaperSkins.Paper; controller.State.Theme = "light"; Theme.Invalidate();
        Program.Assert(controller.State.ColorScheme == ColorSchemes.Warm &&
            ((SolidColorBrush)Theme.PaperBrush).Color == Color.FromRgb(255, 249, 234),
            "leaving a native skin restores the independent saved color choice");
    }
    private static void CheckAuxiliaryMaterials(AppController controller)
    {
        var old = controller.State.MatchAuxiliaryMaterialStrength;
        try
        {
            foreach (var mode in new[] { "light", "dark" })
            foreach (var skin in PaperSkins.All.Where(s => s != PaperSkins.Paper))
            {
                controller.State.PaperSkin = skin; controller.State.Theme = mode;
                controller.State.ColorScheme = ColorSchemes.Warm; Theme.Invalidate();
                foreach (var capsule in new[] { false, true })
                {
                    var surface = new SkinBorder { Width = 240, Height = 80,
                        IsCapsule = capsule, IsMenu = !capsule, CornerRadius = new CornerRadius(12),
                        Background = Theme.PaperBrush, BorderBrush = Brushes.Red, BorderThickness = new Thickness(1),
                        Child = new Border { Width = 12, Height = 12, Background = Brushes.Lime,
                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                    controller.State.MatchAuxiliaryMaterialStrength = false; surface.RefreshSkin();
                    var quiet = Pixels(Render(surface, 1));
                    Program.Assert(surface.MaterialStrength == .4 && surface.Opacity == 1, "quiet material does not dim the entire surface");
                    controller.State.MatchAuxiliaryMaterialStrength = true; surface.RefreshSkin();
                    var full = Pixels(Render(surface, 1));
                    Program.Assert(surface.MaterialStrength == 1 && !quiet.SequenceEqual(full), "switch visibly changes auxiliary material intensity");
                    var center = (40*240+120)*4;
                    Program.Assert(quiet.AsSpan(center, 4).SequenceEqual(full.AsSpan(center, 4)) && full[center+1] == 255,
                        "foreground marker remains fully opaque and unchanged");
                    Program.Assert(surface.IsHitTestVisible && surface.Child.IsHitTestVisible &&
                        VisualTreeHelper.HitTest(surface, new Point(120,40)) != null && !surface.HasRefractionWorker,
                        "unattached auxiliary content remains hit-testable without starting a source-less worker");
                    var edge = (40*240)*4;
                    Program.Assert(quiet.AsSpan(edge,4).SequenceEqual(full.AsSpan(edge,4)), "host stroke does not fade with material strength");
                }
                var main = new SkinBorder { Width = 240, Height = 160, Background = Theme.PaperBrush,
                    BorderBrush = Theme.PaperBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
                controller.State.MatchAuxiliaryMaterialStrength = false; main.RefreshSkin(); var a = Pixels(Render(main, 1));
                controller.State.MatchAuxiliaryMaterialStrength = true; main.RefreshSkin(); var b = Pixels(Render(main, 1));
                Program.Assert(a.SequenceEqual(b), "auxiliary preference never changes the main paper material");
            }
            foreach (var (owner, method) in new[] { (typeof(PaperWindow), "BuildContextMenuTemplate"), (typeof(AppController), "BuildTrayMenuTemplate") })
            {
                var template = (ControlTemplate)owner.GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null)!;
                var menu = new ContextMenu { Width = 240, Height = 80, Template = template, Background = Theme.PaperBrush };
                menu.Items.Add(new MenuItem { Header = "Material menu" });
                Render(menu, 1);
                var root = VisualTreeHelper.GetChild(menu, 0);
                Program.Assert(root is SkinBorder { IsMenu: true }, "actual right-click template instantiates a material surface");
            }
            Console.WriteLine("PASS auxiliary materials: main stable, capsule/menu strength, opaque foreground, hit tests and real menu templates.");
        }
        finally { controller.State.MatchAuxiliaryMaterialStrength = old; }
    }
    private static void CheckDockedOutline(AppController controller)
    {
        foreach (var skin in Decorated)
        foreach (var left in new[] { true, false })
        {
            controller.State.PaperSkin = skin; Theme.Invalidate();
            var outline = new SkinBorder { IsOutline = true, IsCapsule = true, Width = 120, Height = 40,
                CornerRadius = left ? new CornerRadius(0, 16, 16, 0) : new CornerRadius(16, 0, 0, 16),
                BorderThickness = left ? new Thickness(0, 2, 2, 2) : new Thickness(2, 2, 0, 2), BorderBrush = Brushes.Red };
            var image = Render(outline, 1); var pixels = Pixels(image);
            var open = (20 * image.PixelWidth + (left ? 0 : image.PixelWidth - 1)) * 4;
            var closed = (20 * image.PixelWidth + (left ? image.PixelWidth - 1 : 0)) * 4;
            Program.Assert(pixels[open + 3] == 0 && pixels[closed + 2] == 255 && pixels[closed + 3] == 255,
                "the host owns focus color and the docked zero-width edge stays open");
            outline.BorderThickness = new Thickness();
            Program.Assert(Pixels(Render(outline, 1)).All(b => b == 0), "zero-width outline paints nothing");
        }
    }
    private static double Contrast(Color a, Color b)
    {
        static double Linear(byte v) => v <= 10 ? v / 3294.6 : Math.Pow((v / 255.0 + .055) / 1.055, 2.4);
        static double Light(Color c) => .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        var x = Light(a); var y = Light(b);
        return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
    private static void CheckPersistence()
    {
        var temp = Path.Combine(Path.GetTempPath(), "PaperTodo.SkinChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var store = new StateStore(temp, DurableAtomicFileWriter.Shared); long version = 0;
            foreach (var skin in PaperSkins.All)
            foreach (var match in new[] { false, true })
            {
                var state = new AppState { MatchAuxiliaryMaterialStrength = match, PaperSkin = skin, ColorScheme = ColorSchemes.Neutral, MicaAlwaysActive = true, LiquidGlassRefraction = skin != PaperSkins.LiquidGlass };
                state.Papers.Add(new PaperData { Type = PaperTypes.Note, Content = "# 换肤不丢正文\n原文 **保留**" });
                store.SaveJsonSync(store.SerializeState(state), ++version);
                var restored = store.Load();
                Program.Assert(restored.PaperSkin == skin && restored.ColorScheme == state.ColorScheme && restored.MicaAlwaysActive && restored.LiquidGlassRefraction == state.LiquidGlassRefraction && restored.MatchAuxiliaryMaterialStrength == match, "independent preferences persist");
                Program.Assert(restored.Papers.Single().Content == state.Papers.Single().Content, "note payload preserved");
            }
            store.SaveJsonSync("""{"colorScheme":"mica","micaBackdropType":"acrylic","papers":[]}""", ++version);
            var legacy = store.Load();
            Program.Assert(legacy.PaperSkin == PaperSkins.Acrylic && legacy.ColorScheme == ColorSchemes.Neutral && !legacy.MatchAuxiliaryMaterialStrength, "old file appearance preserved");
            store.SaveJsonSync(store.SerializeState(legacy), ++version);
            Program.Assert(store.Load().PaperSkin == PaperSkins.Acrylic, "migration remains stable after resave");
        }
        finally { Directory.Delete(temp, true); }
    }
    private static void CheckPixelGeometry()
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5 })
        foreach (var corners in new[] { new CornerRadius(16), new CornerRadius(0, 16, 16, 0), new CornerRadius(16, 0, 0, 16), new CornerRadius(0) })
        {
            var size = new Size(199.3, 38.7);
            var shape = SkinBorder.CreateShape(size, corners, 0, true, new DpiScale(scale, scale));
            Program.Assert(new Rect(size).Contains(shape.Bounds), "pixel shape stays within existing bounds");
            foreach (var figure in shape.GetFlattenedPathGeometry().Figures)
            {
                var previous = figure.StartPoint;
                foreach (var segment in figure.Segments)
                foreach (var point in segment is PolyLineSegment poly ? poly.Points.ToArray() : new[] { ((LineSegment)segment).Point })
                {
                    Program.Assert(Math.Abs(point.X * scale - Math.Round(point.X * scale)) < .001 && Math.Abs(point.Y * scale - Math.Round(point.Y * scale)) < .001, "device pixel alignment");
                    Program.Assert(Math.Abs(previous.X - point.X) < .001 || Math.Abs(previous.Y - point.Y) < .001, "hard stair step");
                    previous = point;
                }
            }
        }
    }
    private static void CheckLiveSwitch(AppController controller)
    {
        var paper = new PaperData { Type = PaperTypes.Note, Title = "材质切换 · 同一张纸", Content = "# 今日待办\n\n保持正文清晰，**不要重建编辑器**。\n\n- 拖动纸片观察反光\n- 收起为胶囊\n- 切换深浅色", Width = 360, Height = 320, X = 40, Y = 40 };
        controller.State.Papers.Add(paper);
        var window = new PaperWindow(paper, controller);
        try
        {
            window.Show(); Program.Pump();
            var handle = new WindowInteropHelper(window).Handle;
            var noteProperty = typeof(PaperWindow).GetProperty("_noteBox", Program.Private)!;
            var editor = noteProperty.GetValue(window);
            Program.Assert(editor != null, "real note editor mounted");
            foreach (var mode in new[] { "light", "dark" })
            foreach (var skin in PaperSkins.All)
            {
                controller.State.Theme = mode; controller.State.PaperSkin = skin;
                Theme.Invalidate(); window.UpdateTheme(); Program.Pump();
                Program.Assert(ReferenceEquals(editor, noteProperty.GetValue(window)) && new WindowInteropHelper(window).Handle == handle, "actual editor and HWND preserved");
                var header = (Border)typeof(PaperWindow).GetField("_topBarHost", Program.Private)!.GetValue(window)!;
                if (skin == PaperSkins.Paper)
                    Program.Assert(header.Background is SolidColorBrush { Color.A: 255 }, "default header keeps main behavior");
                else
                {
                    Program.Assert(header.Background is SolidColorBrush { Color.A: 0 } &&
                        header.BorderThickness == new Thickness() && header.Margin.Bottom == 0,
                        "one material continues under the header with no separator or white gap");
                    if (skin != PaperSkins.Pixel) CheckHeaderSeam(window, header);
                }
                Save(Render(window, 1), $"window-{skin}-{mode}");
                window.SetCollapsedState(true, animate: false, saveGeometry: false); Program.Pump();
                Program.Assert(!window.IsNativeMicaEffective, "capsule remains on the solid path");
                window.SetCollapsedState(false, animate: false, saveGeometry: false); Program.Pump();
            }
        }
        finally { window.CloseForReal(); controller.State.Papers.Remove(paper); }
        var todo = new PaperData { Type = PaperTypes.Todo, Title = "材质与控件", Width = 360, Height = 260 };
        todo.Items.Add(new PaperItem { Text = "普通中文保持清晰", Done = false });
        todo.Items.Add(new PaperItem { Text = "完成项目", Done = true });
        var todoWindow = new PaperWindow(todo, controller);
        try
        {
            todoWindow.Show(); Program.Pump();
            foreach (var mode in new[] { "light", "dark" })
            foreach (var skin in Decorated)
            {
                controller.State.Theme = mode; controller.State.PaperSkin = skin; Theme.Invalidate();
                todoWindow.UpdateTheme(); Program.Pump();
                Save(Render(todoWindow, 1), $"todo-{skin}-{mode}");
            }
        }
        finally { todoWindow.CloseForReal(); }
    }
    private static void CheckHeaderSeam(Window window, Border header)
    {
        var image = Render(window, 1); var bytes = Pixels(image);
        var edge = header.TransformToAncestor(window).Transform(new Point(header.ActualWidth / 2, header.ActualHeight));
        var x = (int)Math.Round(edge.X); var y = (int)Math.Round(edge.Y);
        if (x < 0 || x >= image.PixelWidth || y < 2 || y + 2 >= image.PixelHeight) return;
        var above = ((y - 2) * image.PixelWidth + x) * 4;
        for (var row = y - 1; row <= y + 1; row++)
        for (var c = 0; c < 4; c++)
            Program.Assert(Math.Abs(bytes[(row * image.PixelWidth + x) * 4 + c] - bytes[above + c]) < 18,
                "rendered header boundary has no bright strip or restarted texture");
    }
    private static void CheckGlassAndAero(AppController controller)
    {
        foreach (var mode in new[] { "light", "dark" })
        {
            controller.State.Theme = mode; controller.State.PaperSkin = PaperSkins.LiquidGlass; Theme.Invalidate();
            var lens = new SkinBorder { Width = 240, Height = 160, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent };
            var image = Render(lens, 1); var bytes = Pixels(image);
            var i = (80 * image.PixelWidth + 120) * 4;
            var alpha = bytes[i + 3];
            // This is the requested clear variant, not the old opaque reading wash.
            // Universal black/white-backdrop contrast is incompatible with clear glass;
            // opaque/high-contrast fallback is still tested above in every palette.
            Program.Assert(alpha >= (mode == "dark" ? 48 : 18) && alpha <= 100, "clear lens has a light visible veil, not bare alpha or a frosted sheet");
            foreach (var rear in mode == "dark" ? new byte[] { 0, 48 } : new byte[] { 200, 255 })
            {
                byte Channel(int c) => (byte)Math.Min(255, bytes[i + c] + rear * (255 - alpha) / 255);
                var background = Color.FromRgb(Channel(2), Channel(1), Channel(0));
                Program.Assert(Contrast(((SolidColorBrush)Theme.TextBrush).Color, background) >= 4.5,
                    "primary clear-lens text remains readable on its theme reference backdrops");
                Program.Assert(Contrast(((SolidColorBrush)Theme.WeakTextBrush).Color, background) >= 3,
                    "secondary clear-lens text remains readable on its theme reference backdrops");
            }
        }
    }
    private static RenderTargetBitmap Render(FrameworkElement element, double scale)
    {
        if (element is not Window) { element.Measure(new Size(element.Width, element.Height)); element.Arrange(new Rect(0, 0, element.Width, element.Height)); }
        element.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(element); return image;
    }
    private static byte[] Pixels(BitmapSource image)
    {
        var bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes;
    }
    private static void Save(BitmapSource image, string name)
    {
        var directory = Environment.GetEnvironmentVariable("PAPER_SKIN_CAPTURE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
    }
}
