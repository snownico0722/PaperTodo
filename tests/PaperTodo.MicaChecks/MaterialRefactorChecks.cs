using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

// Keep the native-surface boundary and byte-identical Aero lighting regressions.
internal static class MaterialRefactorChecks
{
    internal static void Run(AppController controller)
    {
        var saved = (controller.State.PaperSkin, controller.State.Theme,
            controller.State.ColorScheme, controller.State.EnableAnimations);
        try
        {
            controller.State.Theme = "light";
            controller.State.ColorScheme = ColorSchemes.Neutral;
            controller.State.EnableAnimations = false;
            CheckNativeIsolation(controller);
            CheckReliefReuse();
        }
        finally
        {
            (controller.State.PaperSkin, controller.State.Theme,
                controller.State.ColorScheme, controller.State.EnableAnimations) = saved;
            Theme.Invalidate();
        }
    }

    private static void CheckNativeIsolation(AppController controller)
    {
        controller.State.EnableAnimations = false;
        foreach (var skin in new[] { PaperSkins.Paper, PaperSkins.Mica, PaperSkins.Acrylic, PaperSkins.ClearAcrylic, PaperSkins.TracingPaper })
        {
            controller.State.PaperSkin = skin; Theme.Invalidate();
            var surface = new SkinBorder { Background = Brushes.White, CornerRadius = new CornerRadius(8) };
            var window = new Window { Content = surface, Width = 280, Height = 190, Left = 60, Top = 60, ShowInTaskbar = false };
            try
            {
                window.Show(); Program.Pump();
                window.Left += 10; window.Width += 12; Program.Pump();
                surface.RefreshBackground();
                Program.Assert(!surface.HasMaterialHostSubscription,
                    skin + ": ordinary and native surfaces need no auxiliary observer");
            }
            finally { window.Close(); }
        }
    }

    private static void CheckReliefReuse()
    {
        var cases = 0;
        foreach (var size in new[] { new Size(92, 46), new Size(280, 220), new Size(700, 240) })
        foreach (var dpi in new[] { 1d, 1.25, 1.5, 2d })
        foreach (var dark in new[] { false, true })
        foreach (var border in new[] { new Thickness(1), new Thickness(0, 1, 1, 1) })
        {
            var corners = new CornerRadius(8, 17, 12, 0);
            var cached = MaterialRelief.Create(size, corners, border, new DpiScale(dpi, dpi), dark, out var metrics);
            var reference = MaterialRelief.Create(size, corners, border, new DpiScale(dpi, dpi), dark, out var baseline, reuseStraightEdges: false);
            var a = cached.Children.Cast<ImageDrawing>().ToArray();
            var b = reference.Children.Cast<ImageDrawing>().ToArray();
            Program.Assert(a.Length == b.Length && metrics.Pixels == baseline.Pixels, "relief caching does not change raster geometry");
            for (var i = 0; i < a.Length; i++)
                Program.Assert(a[i].Rect == b[i].Rect && Pixels((BitmapSource)a[i].ImageSource).SequenceEqual(Pixels((BitmapSource)b[i].ImageSource)),
                    "cached Aero lighting is byte-identical, including asymmetric corners/open edges/fractional DPI");
            cases++;
        }
        Console.WriteLine($"AERO: {cases} byte-identical lighting cases.");
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(data, bitmap.PixelWidth * 4, 0);
        return data;
    }
}
