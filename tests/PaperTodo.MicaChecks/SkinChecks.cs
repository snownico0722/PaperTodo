using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Reflection;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static class SkinChecks
{
    private static readonly string[] Decorated = PaperSkins.All.Where(PaperSkins.IsDecorated).ToArray();
    internal static void Run(AppController controller)
    {
        Program.Assert(PaperSkins.All.Distinct().Count() == 7 && Decorated.Length == 3, "unique skin choices");
        Program.Assert(!new AppState().HideSurfaceOutline, "ordinary outer border is visible by default");
        Program.Assert(PaperSkins.Resolve(null, "mica", "clearAcrylic") == PaperSkins.ClearAcrylic, "legacy clear Acrylic");
        Program.Assert(PaperSkins.Resolve(null, "mica", "micaAlt") == PaperSkins.Mica, "retired material migration");
        Program.Assert(PaperSkins.Resolve(null, "forest", "acrylic") == PaperSkins.Paper, "ordinary legacy palette");
        foreach (var id in new[] { PaperSkins.Paper, "future", "ceramic", "" })
            Program.Assert(PaperSkins.Resolve(id, "mica", "acrylic") == PaperSkins.Paper, "explicit choice wins over legacy palette");
        foreach (var id in PaperSkins.All)
            Program.Assert(PaperSkins.IsValid(id) && PaperSkins.Normalize(id) == id && !PaperSkins.Decorate(id, true), "valid IDs / high contrast");
        foreach (var id in new[] { PaperSkins.TracingPaper })
            Program.Assert(PaperSkins.UsesNativeBackdrop(id) && PaperSkins.NativeBackdrop(id) == MicaBackdropTypes.Acrylic, "supported Acrylic recipe");
        Program.Assert(PaperSkins.NativeBackdrop(PaperSkins.Aero) == NativeMicaBackdrop.AeroGlassMaterial, "Aero selects its clear native glass recipe");
        CheckPersistence();
        var resources = new ResourceManager("PaperTodo.Resources.Strings", typeof(Strings).Assembly);
        foreach (var culture in new[] { "", "en", "ja", "ko" })
        {
            var set = resources.GetResourceSet(CultureInfo.GetCultureInfo(culture), true, false)!;
            foreach (var key in PaperSkins.All.Select(PaperSkins.LabelKey).Append("SettingsPaperSkin").Append("SkinRestartRequired").Append("SkinCaptureNotice").Append("SettingsMatchAuxiliaryMaterial").Append("TipMatchAuxiliaryMaterial"))
                Program.Assert(!string.IsNullOrWhiteSpace(set.GetString(key)), $"localized {culture}/{key}");
        }
        var before = (controller.State.PaperSkin, controller.State.ColorScheme, controller.State.Theme,
            controller.State.EnableAnimations, controller.State.HideSurfaceOutline,
            controller.State.MatchAuxiliaryMaterialStrength);
        try
        {
            CheckOriginalNativeRendering(controller); CheckAuxiliaryMaterials(controller);
            controller.State.ColorScheme = ColorSchemes.Warm;
            controller.State.EnableAnimations = false;
            controller.State.MatchAuxiliaryMaterialStrength = true;
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
            CheckPixelGeometry(); CheckDockedOutline(controller); CheckLiveSwitch(controller);
            Console.WriteLine($"PASS skins: persistence, four locales, {samples} raster/contrast cases, original native pixels, open-edge focus borders and editor identity.");
        }
        finally
        {
            (controller.State.PaperSkin, controller.State.ColorScheme, controller.State.Theme,
                controller.State.EnableAnimations, controller.State.HideSurfaceOutline,
                controller.State.MatchAuxiliaryMaterialStrength) = before;
            Theme.Invalidate();
        }
    }
    private static void CheckOriginalNativeRendering(AppController controller)
    {
        // This comparison intentionally enables the ordinary border; hide-outline behavior
        // and the independent active/focus capsule outline are checked separately.
        controller.State.HideSurfaceOutline = false;
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
        controller.State.HideSurfaceOutline = true;
        Theme.Invalidate();
    }
    private static void CheckAuxiliaryMaterials(AppController controller)
    {
        var old = (controller.State.MatchAuxiliaryMaterialStrength, controller.State.HideSurfaceOutline);
        try
        {
            controller.State.HideSurfaceOutline = false;
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
                    Program.Assert(surface.MaterialStrength == 1 && !quiet.SequenceEqual(full), $"{skin}/{mode}/{capsule}: switch visibly changes auxiliary material intensity");
                    var center = (40*240+120)*4;
                    Program.Assert(quiet.AsSpan(center, 4).SequenceEqual(full.AsSpan(center, 4)) && full[center+1] == 255,
                        "foreground marker remains fully opaque and unchanged");
                    Program.Assert(surface.IsHitTestVisible && surface.Child.IsHitTestVisible &&
                        VisualTreeHelper.HitTest(surface, new Point(120,40)) != null && !surface.HasBackgroundCapture,
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
        finally
        {
            (controller.State.MatchAuxiliaryMaterialStrength, controller.State.HideSurfaceOutline) = old;
        }
    }
    private static void CheckDockedOutline(AppController controller)
    {
        // Active/focus outline is a distinct semantic layer and remains visible even
        // while the ordinary outer border is hidden.
        controller.State.HideSurfaceOutline = true;
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
                var state = new AppState { MatchAuxiliaryMaterialStrength = match, PaperSkin = skin, ColorScheme = ColorSchemes.Neutral, MicaAlwaysActive = true, MaterialTransparency = MaterialTransparencyLevels.High };
                state.Papers.Add(new PaperData { Type = PaperTypes.Note, Content = "# 换肤不丢正文\n原文 **保留**" });
                store.SaveJsonSync(store.SerializeState(state), ++version);
                var restored = store.Load();
                Program.Assert(restored.PaperSkin == skin && restored.ColorScheme == state.ColorScheme && restored.MicaAlwaysActive && restored.MaterialTransparency == MaterialTransparencyLevels.High && restored.MatchAuxiliaryMaterialStrength == match, "independent preferences persist");
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
        var previousGripMode = controller.State.ResizeGripMode;
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
                foreach (var grip in new[] { ResizeGripModes.Standard, ResizeGripModes.Soft, ResizeGripModes.Hidden })
                {
                    controller.State.ResizeGripMode = grip;
                    CheckResizeHitTests(window, $"native {skin}/{mode}/{grip}");
                    if (mode == "light" && skin == PaperSkins.Mica && grip == ResizeGripModes.Hidden)
                        DragActualResizeGrip(window, "native Mica after skin change, hidden grip");
                }
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
        finally { controller.State.ResizeGripMode = previousGripMode; window.CloseForReal(); controller.State.Papers.Remove(paper); }
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

    internal static void CheckLayeredResize(AppController controller)
    {
        var previousSkin = controller.State.PaperSkin;
        var previousGripMode = controller.State.ResizeGripMode;
        var settingsField = typeof(AppController).GetField("_publicSettings", Program.Private)!;
        var previousSettings = settingsField.GetValue(controller);
        var definitions = (IEnumerable<PaperSettingDefinition>)typeof(AppController)
            .GetMethod("CreatePublicSettingsCatalog", Program.Private)!.Invoke(controller, null)!;
        // Exercise the real UI entry point + settings publication, but keep this test away
        // from the user's StateStore. SettingsUiChecks uses the same commit substitution.
        settingsField.SetValue(controller, new PaperSettingsService(definitions,
            () => true, () => true, publish => publish()));
        var setMode = typeof(AppController).GetMethod("SetResizeGripMode", Program.Private)!;
        var window = new PaperWindow(new PaperData
        {
            Type = PaperTypes.Note, Width = 360, Height = 320, X = 60, Y = 60
        }, controller);
        try
        {
            Program.Assert(window.AllowsTransparency, "layered resize check requires a layered paper");
            window.Show(); Program.Pump();
            foreach (var skin in PaperSkins.All)
            {
                controller.State.PaperSkin = skin;
                Theme.Invalidate();
                window.UpdateTheme();
                Program.Pump();
                var gripSnapshots = new List<byte[]>();
                foreach (var grip in new[] { ResizeGripModes.Standard, ResizeGripModes.Soft, ResizeGripModes.Hidden })
                {
                    setMode.Invoke(controller, [grip]);
                    Program.Pump();
                    Program.Assert(controller.State.ResizeGripMode == grip,
                        $"UI settings entry point applied {grip}");
                    var brush = (SolidColorBrush)Application.Current.Resources["PaperResizeGripBrush"];
                    var expectedAlpha = grip switch
                    {
                        ResizeGripModes.Standard => 255,
                        ResizeGripModes.Soft => 128,
                        _ => 0
                    };
                    Program.Assert(brush.Color.A == expectedAlpha,
                        $"UI settings published {skin}/{grip} grip alpha");
                    CheckResizeHitTests(window, $"layered {skin}/{grip}");
                    if (skin == PaperSkins.Paper)
                    {
                        CheckGripTemplateBrush(window, grip, expectedAlpha);
                        gripSnapshots.Add(CaptureActualGripPixels(window));
                    }
                }
                if (skin == PaperSkins.Paper)
                {
                    var standardDelta = GripPixelDelta(gripSnapshots[0], gripSnapshots[2]);
                    var softDelta = GripPixelDelta(gripSnapshots[1], gripSnapshots[2]);
                    Program.Assert(standardDelta > softDelta && softDelta > 0,
                        "actual window pixels must show stronger standard than soft grip " +
                        $"(standard-hidden={standardDelta}, soft-hidden={softDelta})");
                    setMode.Invoke(controller, [ResizeGripModes.Standard]);
                    Program.Pump();
                    DragActualResizeGrip(window, "layered paper/standard after skin change");
                }
                else if (skin == PaperSkins.Aero)
                    DragActualResizeGrip(window, "layered Aero/hidden after skin change");
            }
        }
        finally
        {
            setMode.Invoke(controller, [previousGripMode]);
            window.CloseForReal();
            controller.State.PaperSkin = previousSkin;
            controller.State.ResizeGripMode = previousGripMode;
            settingsField.SetValue(controller, previousSettings);
            Theme.Invalidate();
        }
    }

    private static void CheckResizeHitTests(PaperWindow window, string scenario)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        Program.Assert(window.ResizeMode == ResizeMode.CanResizeWithGrip,
            $"{scenario}: expanded paper must allow resizing");
        Program.Assert(GetWindowRect(hwnd, out var rect), $"{scenario}: HWND rectangle");

        var chrome = (PaperChromeBorder)typeof(PaperWindow)
            .GetField("_paperChrome", Program.Private)!.GetValue(window)!;
        var dpi = VisualTreeHelper.GetDpi(window);
        var leftInset = (int)Math.Round((chrome.Margin.Left + 2) * dpi.DpiScaleX);
        var rightInset = (int)Math.Round((chrome.Margin.Right + 2) * dpi.DpiScaleX);
        var topInset = (int)Math.Round((chrome.Margin.Top + 2) * dpi.DpiScaleY);
        var bottomInset = (int)Math.Round((chrome.Margin.Bottom + 2) * dpi.DpiScaleY);
        var centerX = (rect.Left + rect.Right) / 2;
        var centerY = (rect.Top + rect.Bottom) / 2;
        Program.Assert(ResizeHit(hwnd, rect.Left + leftInset, centerY) == 10,
            $"{scenario}: painted left edge must resize");
        Program.Assert(ResizeHit(hwnd, rect.Right - rightInset, centerY) == 11,
            $"{scenario}: painted right edge must resize");
        Program.Assert(ResizeHit(hwnd, centerX, rect.Bottom - bottomInset) == 15,
            $"{scenario}: painted bottom edge must resize");
        Program.Assert(ResizeHit(hwnd, rect.Right - rightInset, rect.Bottom - bottomInset) == 17,
            $"{scenario}: painted bottom-right corner must resize");
        // Locate the WPF control in the actual window tree instead of repeating
        // PaperWindow's geometric approximation of where the grip should be.
        var grip = FindResizeGrip(window);
        var center = grip.PointToScreen(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2));
        Program.Assert(ResizeHit(hwnd, (int)Math.Round(center.X), (int)Math.Round(center.Y)) == 17,
            $"{scenario}: actual WPF resize grip center must resize");
        var neighbor = grip.PointToScreen(new Point(-8, 1));
        var neighborHit = ResizeHit(hwnd, (int)Math.Round(neighbor.X), (int)Math.Round(neighbor.Y));
        Program.Assert(neighborHit == 1,
            $"{scenario}: body area beside grip remains an interactive client " +
            $"(hit={neighborHit}, grip={grip.ActualWidth:0.#}x{grip.ActualHeight:0.#}, " +
            $"pointer={neighbor.X:0},{neighbor.Y:0}, hwnd={rect.Right}x{rect.Bottom})");
        if (chrome.HeaderOpacity != 0)
            Program.Assert(ResizeHit(hwnd, centerX, rect.Top + topInset) == 12,
                $"{scenario}: painted top edge must resize");
    }

    private static ResizeGrip FindResizeGrip(Window window)
    {
        static IEnumerable<ResizeGrip> Grips(DependencyObject root)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ResizeGrip grip) yield return grip;
                foreach (var descendant in Grips(child)) yield return descendant;
            }
        }

        var grips = Grips(window).Where(g => g.IsVisible &&
            g.ActualWidth >= 8 && g.ActualHeight >= 8).ToArray();
        Program.Assert(grips.Length == 1, $"expected one visible WPF resize grip, found {grips.Length}");
        return grips[0];
    }

    private static T? FirstVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FirstVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static void CheckGripTemplateBrush(PaperWindow window, string mode, int expectedAlpha)
    {
        var grip = FindResizeGrip(window);
        var path = FirstVisualChild<System.Windows.Shapes.Path>(grip);
        var painted = ((path?.Fill as DrawingBrush)?.Drawing as GeometryDrawing)?.Brush as SolidColorBrush;
        var alpha = painted?.Color.A ?? -1;
        var styleMatches = ReferenceEquals(grip.Style, Application.Current.Resources[typeof(ResizeGrip)]);
        Program.Assert(alpha == expectedAlpha,
            $"real WPF grip template follows {mode}: brush alpha {alpha} expected {expectedAlpha}; " +
            $"styleMatches={styleMatches}, gripOpacity={grip.Opacity}, children={VisualTreeHelper.GetChildrenCount(grip)}");
    }

    private static byte[] CaptureActualGripPixels(PaperWindow window)
    {
        // Render the attached complete Window. Render(grip) alone can produce empty
        // pixels because the stock Window template owns/clips the resize control.
        var grip = FindResizeGrip(window);
        var bounds = grip.TransformToAncestor(window).TransformBounds(
            new Rect(0, 0, grip.ActualWidth, grip.ActualHeight));
        var bitmap = Render(window, 1);
        var bytes = Pixels(bitmap);
        var left = Math.Clamp((int)Math.Floor(bounds.Left), 0, bitmap.PixelWidth - 1);
        var top = Math.Clamp((int)Math.Floor(bounds.Top), 0, bitmap.PixelHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling(bounds.Right), left + 1, bitmap.PixelWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom), top + 1, bitmap.PixelHeight);
        var pixels = new byte[(right - left) * (bottom - top) * 4];
        for (var y = top; y < bottom; y++)
            Array.Copy(bytes, (y * bitmap.PixelWidth + left) * 4,
                pixels, (y - top) * (right - left) * 4, (right - left) * 4);
        return pixels;
    }

    private static long GripPixelDelta(byte[] a, byte[] b)
    {
        Program.Assert(a.Length == b.Length, "comparison uses stable grip bounds");
        long sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum;
    }

    private static void DragActualResizeGrip(PaperWindow window, string scenario)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var grip = FindResizeGrip(window);
        var center = grip.PointToScreen(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2));
        var origin = new NativePoint((int)Math.Round(center.X), (int)Math.Round(center.Y));
        Program.Assert(GetWindowRect(hwnd, out var before), $"{scenario}: original bounds");
        Program.Assert(GetCursorPos(out var priorCursor), $"{scenario}: original cursor");
        window.Activate();
        Program.Pump();
        var nativeTarget = WindowFromPoint(origin);
        Program.Assert(nativeTarget == hwnd,
            $"{scenario}: actual grip must receive native input " +
            $"(target={nativeTarget}, paper={hwnd}, point={origin.X},{origin.Y})");

        try
        {
            // Win32 enters a modal sizing loop on mouse-down; inject the entire
            // press/move/release from another thread while WPF continues pumping.
            var drag = Task.Run(() =>
            {
                if (!SetCursorPos(origin.X, origin.Y)) throw new InvalidOperationException("Move to grip failed");
                Thread.Sleep(80);
                try
                {
                    MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero); // LEFTDOWN
                    Thread.Sleep(100);
                    for (var step = 1; step <= 4; step++)
                    {
                        if (!SetCursorPos(origin.X + step * 18, origin.Y + step * 15))
                            throw new InvalidOperationException("Resize drag pointer move failed");
                        Thread.Sleep(55);
                    }
                }
                finally { MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero); } // LEFTUP
            });
            var elapsed = Stopwatch.StartNew();
            while (!drag.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(8))
                Program.Pump();
            Program.Assert(drag.IsCompleted, $"{scenario}: native resize gesture timed out");
            drag.GetAwaiter().GetResult();
            Program.Pump();
            Program.Assert(GetWindowRect(hwnd, out var after), $"{scenario}: final bounds");
            var oldW = before.Right - before.Left;
            var oldH = before.Bottom - before.Top;
            var newW = after.Right - after.Left;
            var newH = after.Bottom - after.Top;
            Program.Assert(newW >= oldW + 25 && newH >= oldH + 20,
                $"{scenario}: real mouse drag must increase BOTH dimensions " +
                $"({oldW}x{oldH} -> {newW}x{newH})");
        }
        finally
        {
            MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
            _ = SetCursorPos(priorCursor.X, priorCursor.Y);
            Program.Pump();
        }
    }

    private static int ResizeHit(IntPtr hwnd, int x, int y)
    {
        var packed = new IntPtr(unchecked((int)((uint)(ushort)x | ((uint)(ushort)y << 16))));
        return SendMessage(hwnd, 0x0084 /* WM_NCHITTEST */, IntPtr.Zero, packed).ToInt32();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
        public NativePoint(int x, int y) => (X, Y) = (x, y);
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

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
