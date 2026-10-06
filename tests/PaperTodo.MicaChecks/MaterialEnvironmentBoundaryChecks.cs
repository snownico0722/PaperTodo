using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using PaperTodo;

internal static class MaterialEnvironmentBoundaryChecks
{
    internal static void Run(AppController controller)
    {
        var saved = (controller.State.PaperSkin, controller.State.MatchAuxiliaryMaterialStrength);
        var failures = new List<string>();
        try
        {
            controller.State.PaperSkin = PaperSkins.Acrylic;
            controller.State.MatchAuxiliaryMaterialStrength = true;
            Theme.Invalidate();
            foreach (var cachedCapability in new[] { "_transparencyEnabled", "_compositionEnabled" })
            foreach (var role in new[] { "menu", "drag" })
            {
                try { CheckLoss(cachedCapability, role); Console.WriteLine("PASS material environment: " + cachedCapability + "/" + role); }
                catch (Exception ex) { failures.Add(cachedCapability + "/" + role + ": " + ex.Message); }
                finally { DwmMicaApi.Instance.InvalidateEnvironment(); }
            }
        }
        finally
        {
            DwmMicaApi.Instance.InvalidateEnvironment();
            (controller.State.PaperSkin, controller.State.MatchAuxiliaryMaterialStrength) = saved;
            Theme.Invalidate();
        }
        foreach (var failure in failures) Console.WriteLine("FAIL material environment: " + failure);
        Program.Assert(failures.Count == 0, string.Join("; ", failures));
    }

    private static void CheckLoss(string capability, string role)
    {
        DwmMicaApi.Instance.InvalidateEnvironment();
        Program.Assert(DwmMicaApi.Instance.EffectsEnabled, "initial material environment is available");
        var surface = new SkinBorder { IsMenu = role == "menu", IsCapsule = role == "drag", Width = 180, Height = 90 };
        var window = new Window { Width = 180, Height = 90, Left = 300, Top = 200,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            Content = surface, ShowInTaskbar = false, ShowActivated = false };
        var layout = new BackgroundCaptureLayout.Scene(new Int32Rect(0, 0, 64, 32), 64, 32);
        var frame = new DesktopBackgroundCapture.Frame(layout, new byte[64 * 32 * 4]);
        var late = new DesktopBackgroundCapture.Snapshot(layout, frame.Bitmap, true);
        try
        {
            if (role == "menu") surface.PrepareMenuBackground(frame, false);
            window.Show();
            Until(() => surface.IsBackgroundActive && !surface.HasBackgroundCapture, "initial material scene is presented");
            // Change only the API's cached observation in this process. Do not modify the user's
            // registry, accessibility settings or the runner's real desktop composition.
            typeof(DwmMicaApi).GetField(capability, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(DwmMicaApi.Instance, (bool?)false);
            surface.RefreshSkin();
            if (role == "drag") surface.UseDragBackground(late);
            Program.Assert(!surface.IsBackgroundActive && !surface.HasBackgroundCapture,
                role == "menu" ? "an open menu must release its prepared scene when system material effects are unavailable" :
                    "a late drag result must not reactivate material after system effects became unavailable");
            DwmMicaApi.Instance.InvalidateEnvironment();
            surface.RefreshSkin();
            if (role == "menu") surface.PrepareMenuBackground(frame, false);
            else surface.UseDragBackground(late);
            Program.Assert(surface.IsBackgroundActive, "a later valid material request remains usable");
        }
        finally
        {
            DwmMicaApi.Instance.InvalidateEnvironment();
            surface.EndDragBackground();
            surface.PrepareMenuBackground(null, false);
            window.Close(); Program.Pump();
        }
    }
    private static void Until(Func<bool> predicate, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(3)) { Program.Pump(); Thread.Sleep(1); }
        Program.Assert(predicate(), message);
    }
}
