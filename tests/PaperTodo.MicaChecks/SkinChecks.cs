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
        Program.Assert(PaperSkins.All.Distinct().Count() == 10 && Decorated.Length == 6, "unique skin choices");
        Program.Assert(PaperSkins.Resolve(null, "mica", "clearAcrylic") == PaperSkins.ClearAcrylic, "legacy clear Acrylic");
        Program.Assert(PaperSkins.Resolve(null, "mica", "micaAlt") == PaperSkins.Mica, "retired material migration");
        Program.Assert(PaperSkins.Resolve(null, "forest", "acrylic") == PaperSkins.Paper, "ordinary legacy palette");
        foreach (var id in new[] { PaperSkins.Paper, "future", "" })
            Program.Assert(PaperSkins.Resolve(id, "mica", "acrylic") == PaperSkins.Paper, "explicit choice wins over legacy palette");
        foreach (var id in PaperSkins.All)
            Program.Assert(PaperSkins.IsValid(id) && PaperSkins.Normalize(id) == id && !PaperSkins.Decorate(id, true), "valid IDs / high contrast");
        foreach (var id in new[] { PaperSkins.TracingPaper, PaperSkins.Aero, PaperSkins.LiquidGlass })
            Program.Assert(PaperSkins.UsesNativeBackdrop(id) && PaperSkins.NativeBackdrop(id) == MicaBackdropTypes.Acrylic, "supported Acrylic recipe");
        CheckPersistence();
        var resources = new ResourceManager("PaperTodo.Resources.Strings", typeof(Strings).Assembly);
        foreach (var culture in new[] { "", "en", "ja", "ko" })
        {
            var set = resources.GetResourceSet(CultureInfo.GetCultureInfo(culture), true, false)!;
            foreach (var key in PaperSkins.All.Select(PaperSkins.LabelKey).Append("SettingsPaperSkin").Append("SkinRestartRequired"))
                Program.Assert(!string.IsNullOrWhiteSpace(set.GetString(key)), $"localized {culture}/{key}");
        }
        var before = (controller.State.PaperSkin, controller.State.ColorScheme, controller.State.Theme, controller.State.EnableAnimations);
        try
        {
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
                    hashes.Add(Convert.ToHexString(SHA256.HashData(bytes)));
                    Save(image, $"{skin}-{mode}-{(capsule ? "capsule" : "paper")}-{scale:0.##}");
                    if (PaperSkins.UsesNativeBackdrop(skin))
                    {
                        border.Background = NativeMicaBackdrop.GetActiveSurfaceBrush(MicaBackdropTypes.Acrylic, mode == "dark");
                        Program.Assert(Pixels(Render(border, scale))[center + 3] is > 0 and < 255, "native fill allows compositor through");
                    }
                    samples++;
                }
                Program.Assert(hashes.Count == 6, "six different surfaces, not renamed presets");
            }
            CheckPixelGeometry(); CheckLiveSwitch(controller);
            Console.WriteLine($"PASS skins: persistence, four locales, {samples} raster cases, DPI geometry and editor identity.");
        }
        finally
        {
            (controller.State.PaperSkin, controller.State.ColorScheme, controller.State.Theme, controller.State.EnableAnimations) = before;
            Theme.Invalidate();
        }
    }
    private static void CheckPersistence()
    {
        var temp = Path.Combine(Path.GetTempPath(), "PaperTodo.SkinChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var store = new StateStore(temp, DurableAtomicFileWriter.Shared); long version = 0;
            foreach (var skin in PaperSkins.All)
            {
                var state = new AppState { PaperSkin = skin, ColorScheme = ColorSchemes.Neutral, MicaAlwaysActive = true };
                state.Papers.Add(new PaperData { Type = PaperTypes.Note, Content = "# 换肤不丢正文\n原文 **保留**" });
                store.SaveJsonSync(store.SerializeState(state), ++version);
                var restored = store.Load();
                Program.Assert(restored.PaperSkin == skin && restored.ColorScheme == state.ColorScheme && restored.MicaAlwaysActive, "independent preferences persist");
                Program.Assert(restored.Papers.Single().Content == state.Papers.Single().Content, "note payload preserved");
            }
            store.SaveJsonSync("""{"colorScheme":"mica","micaBackdropType":"acrylic","papers":[]}""", ++version);
            var legacy = store.Load();
            Program.Assert(legacy.PaperSkin == PaperSkins.Acrylic && legacy.ColorScheme == ColorSchemes.Neutral, "old file appearance preserved");
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
        var paper = new PaperData { Type = PaperTypes.Note, Title = "六种材质 · 同一张纸", Content = "# 今日待办\n\n保持正文清晰，**不要重建编辑器**。\n\n- 拖动纸片观察反光\n- 收起为胶囊\n- 切换深浅色", Width = 360, Height = 320, X = 40, Y = 40 };
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
            foreach (var skin in Decorated.Append(PaperSkins.Paper))
            {
                controller.State.Theme = mode; controller.State.PaperSkin = skin;
                Theme.Invalidate(); window.UpdateTheme(); Program.Pump();
                Program.Assert(ReferenceEquals(editor, noteProperty.GetValue(window)) && new WindowInteropHelper(window).Handle == handle, "actual editor and HWND preserved");
                Save(Render(window, 1), $"window-{skin}-{mode}");
                window.SetCollapsedState(true, animate: false, saveGeometry: false); Program.Pump();
                Program.Assert(!window.IsNativeMicaEffective, "capsule remains on the solid path");
                window.SetCollapsedState(false, animate: false, saveGeometry: false); Program.Pump();
            }
        }
        finally { window.CloseForReal(); controller.State.Papers.Remove(paper); }
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
