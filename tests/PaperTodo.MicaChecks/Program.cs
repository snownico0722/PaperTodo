using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static class Program
{
    private static int _passed;

    [STAThread]
    private static void Main()
    {
        var app = new App();
        app.InitializeComponent();
        var temp = Path.Combine(Path.GetTempPath(), "PaperTodo.MicaChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Check("skin normalization preserves old choices and defaults", () =>
            {
                foreach (var scheme in new[] { "warm", "ink", "forest", "rose", "mica" })
                    Assert(ColorSchemes.IsValid(scheme) && ColorSchemes.Normalize(scheme) == scheme, scheme);
                Assert(ColorSchemes.All.Distinct().Count() == 5, "unique schemes");
                Assert(ColorSchemes.Normalize(null) == ColorSchemes.Warm, "null defaults to paper");
                Assert(ColorSchemes.Normalize("future") == ColorSchemes.Warm, "unknown defaults to paper");
                Assert(new AppState().ColorScheme == ColorSchemes.Warm, "existing default is unchanged");
            });

            Check("real StateStore round-trips Mica without losing notes", () =>
            {
                var store = new StateStore(temp, DurableAtomicFileWriter.Shared);
                foreach (var mode in new[] { "light", "dark", "system" })
                {
                    var state = new AppState { ColorScheme = ColorSchemes.Mica, Theme = mode };
                    state.Papers.Add(new PaperData { Type = PaperTypes.Note, Content = "保留笔记\n**Mica**" });
                    store.SaveJsonSync(store.SerializeState(state), version: 1);
                    var restored = store.Load();
                    Assert(restored.ColorScheme == ColorSchemes.Mica && restored.Theme == mode, "stored selection");
                    Assert(restored.Papers[0].Content == state.Papers[0].Content, "note content");
                }
            });

            var sample = Sample();
            var material = Task.Run(() => MicaMaterial.FromBitmap(sample)).GetAwaiter().GetResult();
            Check("worker-created brushes and images are frozen and usable by UI", () =>
            {
                foreach (var brush in new[] { material.Light, material.Dark })
                {
                    Assert(brush.IsFrozen && brush.ImageSource.IsFrozen, "frozen graph");
                    Assert(brush.Opacity == 1, "no whole-surface translucency");
                    var image = (BitmapSource)brush.ImageSource;
                    Assert(image.PixelWidth == 64 && image.PixelHeight == 64, "bounded texture");
                    var pixels = Pixels(image);
                    Assert(Enumerable.Range(0, 4096).All(i => pixels[i * 4 + 3] == 255), "opaque material");
                    Assert(pixels.Where((_, i) => i % 4 == 0).Distinct().Count() > 4, "wallpaper variation retained");
                }
            });

            Check("local wallpaper decode closes the stream and bounds portrait images", () =>
            {
                var path = Path.Combine(temp, "portrait.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(sample));
                using (var stream = File.Create(path)) encoder.Save(stream);
                Assert(MicaMaterial.LoadFile(path) != null, "load a real local image");
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert(exclusive.Length > 0, "decoder released file handle");
            });

            Check("missing, corrupt, empty and non-local wallpaper fall back", () =>
            {
                var path = Path.Combine(temp, "bad-image.png");
                File.WriteAllText(path, "This is not an image.");
                foreach (var candidate in new[] { "", "https://example.invalid/wallpaper.png", @"\\server\image.png",
                    Path.Combine(temp, "missing.png"), path })
                    Assert(MicaMaterial.LoadFile(candidate) == null, "fallback for " + candidate);
                File.WriteAllBytes(path, []);
                Assert(MicaMaterial.LoadFile(path) == null, "empty image fallback");
            });

            Check("cache loads only when selected and reuses the same pair", () =>
            {
                var calls = 0;
                var cache = new MicaMaterialCache(() => { calls++; return Task.FromResult<MicaMaterial?>(material); });
                cache.RefreshAsync(false).GetAwaiter().GetResult();
                Assert(calls == 0, "no wallpaper read in default skin");
                cache.RefreshAsync(true).GetAwaiter().GetResult();
                for (var i = 0; i < 100; i++) cache.RefreshAsync(true).GetAwaiter().GetResult();
                Assert(calls == 1 && ReferenceEquals(cache.Current, material), "shared cache");
                cache.RefreshAsync(true, invalidate: true).GetAwaiter().GetResult();
                Assert(calls == 2, "wallpaper event refresh");
                cache.RefreshAsync(false).GetAwaiter().GetResult();
                Assert(cache.Current == null, "disabled skin releases cached pair");
            });

            Check("late load cannot resurrect a disabled skin", () =>
            {
                var completion = new TaskCompletionSource<MicaMaterial?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var cache = new MicaMaterialCache(() => completion.Task);
                var pending = cache.RefreshAsync(true);
                cache.RefreshAsync(false).GetAwaiter().GetResult();
                completion.SetResult(material);
                Assert(!pending.GetAwaiter().GetResult() && cache.Current == null, "stale load discarded");
            });

            Check("newer wallpaper wins out-of-order completion", () =>
            {
                var first = new TaskCompletionSource<MicaMaterial?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var second = new TaskCompletionSource<MicaMaterial?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var calls = 0;
                var cache = new MicaMaterialCache(() => ++calls == 1 ? first.Task : second.Task);
                var older = cache.RefreshAsync(true);
                var newer = cache.RefreshAsync(true, invalidate: true);
                second.SetResult(null); // Current wallpaper was removed / became a solid desktop.
                Assert(newer.GetAwaiter().GetResult() && cache.Current == null, "latest fallback published");
                first.SetResult(material);
                Assert(!older.GetAwaiter().GetResult() && cache.Current == null, "old wallpaper cannot return");
            });

            Check("unreadable wallpaper is not retried by every surface", () =>
            {
                var calls = 0;
                var cache = new MicaMaterialCache(() => { calls++; return Task.FromResult<MicaMaterial?>(null); });
                for (var i = 0; i < 20; i++) cache.RefreshAsync(true).GetAwaiter().GetResult();
                Assert(calls == 1 && cache.Current == null, "failure cached");
                cache.RefreshAsync(false).GetAwaiter().GetResult();
                cache.RefreshAsync(true).GetAwaiter().GetResult();
                Assert(calls == 2, "reselect allows retry");
            });

            using var controller = new AppController();
            controller.State.EnableAnimations = false;
            Check("Mica palettes preserve solid plugin colors and readable text", () =>
            {
                foreach (var mode in new[] { "light", "dark" })
                {
                    controller.State.ColorScheme = ColorSchemes.Mica;
                    controller.State.Theme = mode;
                    Theme.Invalidate();
                    Assert(Theme.PaperBrush is SolidColorBrush { IsFrozen: true }, "semantic color stays solid");
                    Assert(ReferenceEquals(Theme.SurfaceBrush, Theme.PaperBrush), "unloaded wallpaper uses palette fallback");
                    var extremes = new[] { Solid(Colors.Black), Solid(Colors.White), sample };
                    foreach (var source in extremes)
                    {
                        var surface = MicaMaterial.FromBitmap(source);
                        var image = (BitmapSource)(mode == "dark" ? surface.Dark : surface.Light).ImageSource;
                        var pixels = Pixels(image);
                        for (var i = 0; i < pixels.Length; i += 4)
                        {
                            var background = Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]);
                            foreach (var foreground in new[] { Theme.TextBrush, Theme.WeakTextBrush, Theme.LinkBrush })
                                Assert(Contrast(((SolidColorBrush)foreground).Color, background) >= 4.5, "readable text in " + mode);
                        }
                    }
                }
            });

            Check("actual Todo/Note shells switch skins without changing content or geometry", () =>
            {
                foreach (var type in new[] { PaperTypes.Todo, PaperTypes.Note })
                {
                    var paper = new PaperData { Type = type, Width = 360, Height = 280, Content = "# Mica\n保留正文" };
                    var window = new PaperWindow(paper, controller);
                    try
                    {
                        foreach (var scheme in new[] { ColorSchemes.Mica, ColorSchemes.Warm, ColorSchemes.Mica })
                        {
                            controller.State.ColorScheme = scheme;
                            controller.State.Theme = scheme == ColorSchemes.Warm ? "light" : "dark";
                            Theme.Invalidate();
                            window.UpdateTheme();
                            window.Measure(new Size(360, 280));
                            window.Arrange(new Rect(0, 0, 360, 280));
                            window.UpdateLayout();
                            Assert(ReferenceEquals(window.Resources["PaperSurfaceBrushKey"], Theme.SurfaceBrush), "surface refreshed");
                            Assert(window.AllowsTransparency && window.WindowStyle == WindowStyle.None, "window shape policy unchanged");
                            Assert(paper.Width == 360 && paper.Height == 280 && paper.Content == "# Mica\n保留正文", "geometry and content unchanged");
                        }
                    }
                    finally { window.CloseForReal(); }
                }
            });

            Check("WPF renders rounded material without painting outside its shape", () =>
            {
                foreach (var dark in new[] { false, true })
                {
                    var border = new Border
                    {
                        Width = 300, Height = 200, CornerRadius = new CornerRadius(16),
                        Background = dark ? material.Dark : material.Light,
                        Child = new TextBlock
                        {
                            Text = "Mica\nWallpaper-tinted paper\n壁纸融合 · 云母质感", Margin = new Thickness(20),
                            Foreground = dark ? Brushes.White : Brushes.Black, FontSize = 20
                        }
                    };
                    border.Measure(new Size(300, 200));
                    border.Arrange(new Rect(0, 0, 300, 200));
                    var bitmap = new RenderTargetBitmap(300, 200, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(border);
                    var pixels = Pixels(bitmap);
                    Assert(pixels[3] == 0, "rounded corner stays transparent");
                    Assert(pixels[(190 * 300 + 150) * 4 + 3] == 255, "material remains opaque inside");
                }
            });
            Console.WriteLine($"Mica checks passed: {_passed}/{_passed}");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static void Check(string name, Action action)
    {
        action();
        _passed++;
        Console.WriteLine("PASS " + name);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static BitmapSource Solid(Color color)
    {
        var source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { color.B, color.G, color.R, 255 }, 4);
        source.Freeze();
        return source;
    }

    private static BitmapSource Sample()
    {
        const int width = 80, height = 160;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            pixels[i] = (byte)(255 * x / width);
            pixels[i + 1] = (byte)(255 * y / height);
            pixels[i + 2] = (byte)(255 - pixels[i]);
            pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color c) => Channel(c.R) * 0.2126 + Channel(c.G) * 0.7152 + Channel(c.B) * 0.0722;
        var la = Luminance(a); var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
