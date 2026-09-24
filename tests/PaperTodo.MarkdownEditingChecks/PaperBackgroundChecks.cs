using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static partial class Program
{
    private static void CheckPaperBackgroundToggle()
    {
        var paths = new[] { "papertodo.png", "papertodo.jpg", "papertodo.jpeg" }
            .Select(name => Path.Combine(AppContext.BaseDirectory, name)).ToArray();
        Require(paths.All(path => !File.Exists(path)), "background check must not replace an existing image");
        var path = paths[0];
        try
        {
            var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgr24, null, new byte[16 * 16 * 3], 16 * 3);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            var brush = PaperBackground.CreateBrush(false, PaperBackgroundLayouts.Center, false);
            Require(brush?.ImageSource is BitmapSource image && image.PixelWidth == 16 && image.PixelHeight == 16,
                "a valid small background loads without enlarging its decoded image");
            var host = new Grid(); PaperBackground.Apply(host);
            Require(host.Background is ImageBrush && PaperBackground.LoadError == null, "valid background is applied");
            File.WriteAllText(path, "not an image");
            Require(PaperBackground.CreateBrush(false, PaperBackgroundLayouts.Center, false) == null &&
                !string.IsNullOrWhiteSpace(PaperBackground.LoadError), "corrupt input falls back and reports a load error");
            PaperBackground.Apply(host);
            Require(host.Background is SolidColorBrush plain && plain.Color.A == 0, "corrupt input does not leave the old image mounted");
        }
        finally { File.Delete(path); }
    }
}
