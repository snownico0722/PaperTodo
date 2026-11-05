using System.Windows;
using System.Windows.Controls;
using PaperTodo;
using PaperTodo.Plugin;

internal static partial class Program
{
    private static void PopupThemeNotificationsFollowAppearance(AppController controller)
    {
        Check(ReadField<Window?>(controller, "_settingsWindow") == null,
            "Popup refresh fixture must not depend on an open settings window.");
        var previousTheme = controller.State.Theme;
        var previousZoom = controller.State.Zoom;
        var refreshes = 0;
        void Changed() => refreshes++;
        controller.PluginPopupThemeChanged += Changed;
        try
        {
            Invoke(controller, "RefreshSettingsWindowContent");
            Check(refreshes == 0, "Rebuilding settings must not dispatch unrelated plugin popup theme callbacks.");
            controller.PublicSettings.Set("appearance.theme", Json(previousTheme == "dark" ? "light" : "dark"));
            Check(refreshes == 1, "Theme changes must notify popups exactly once without an open settings window.");
            controller.PublicSettings.Set("appearance.font_scale", Json(previousZoom == 1.05 ? 1.0 : 1.05));
            Check(refreshes == 2, "Font changes must notify popups exactly once without an open settings window.");
            Invoke(controller, "RefreshSettingsWindowContent");
            Check(refreshes == 2, "An unrelated settings rebuild must not repeat popup theme delivery.");
        }
        finally
        {
            controller.PluginPopupThemeChanged -= Changed;
            controller.PublicSettings.Set("appearance.theme", Json(previousTheme));
            controller.PublicSettings.Set("appearance.font_scale", Json(previousZoom));
        }
    }

    private static void VisualBroadcastSurvivesPaperCreation(AppController controller)
    {
        var windows = ReadField<Dictionary<string, PaperWindow>>(controller, "_windows");
        var notes = windows.Values.Where(window => controller.State.Papers.Any(paper =>
            paper.Id == window.PaperId && paper.Type == PaperTypes.Note)).Take(2).ToArray();
        Check(notes.Length == 2, "Visual broadcast needs two existing note bodies.");
        var firstHost = ReadField<PaperBodyHost>(notes[0], "_paperBodyHost");
        var secondHost = ReadField<PaperBodyHost>(notes[1], "_paperBodyHost");
        var firstBody = firstHost.Current!;
        var secondBody = secondHost.Current!;
        var first = new VisualBroadcastBodyProbe();
        var second = new VisualBroadcastBodyProbe();
        var created = new List<string>();
        var oldTheme = controller.State.Theme;
        var oldZoom = controller.State.Zoom;
        Exception? callbackFailure = null;
        void CreateFromCallback()
        {
            try
            {
                var result = controller.PaperCommands.CreatePaper(
                    new CreatePaperRequest { Type = PaperTypes.Todo, Show = true },
                    PaperOperationContext.Plugin("tests.visual-broadcast"));
                created.Add(result.PaperId);
            }
            catch (Exception ex) { callbackFailure = ex; }
        }
        Field(firstHost, "<Current>k__BackingField", first);
        Field(secondHost, "<Current>k__BackingField", second);
        try
        {
            first.ThemeChanged = CreateFromCallback;
            var result = controller.PublicSettings.Set("appearance.theme", Json(oldTheme == "dark" ? "light" : "dark"));
            first.ThemeChanged = null;
            Check(result.Changed && callbackFailure == null && created.Count == 1 && windows.ContainsKey(created[0]),
                "A legitimate synchronous theme callback could not create a visible paper.");
            Check(first.Themes == 1 && second.Themes == 1,
                "Creating a paper in a theme callback stopped the theme broadcast to another existing paper.");

            first.TypographyChanged = CreateFromCallback;
            result = controller.PublicSettings.Set("appearance.font_scale", Json(oldZoom == 1.05 ? 1.0 : 1.05));
            first.TypographyChanged = null;
            Check(result.Changed && callbackFailure == null && created.Count == 2 && windows.ContainsKey(created[1]),
                "A legitimate synchronous typography callback could not create a visible paper.");
            Check(first.Typography == 1 && second.Typography == 1,
                "Creating a paper in a typography callback stopped the font broadcast to another existing paper.");
        }
        finally
        {
            Field(firstHost, "<Current>k__BackingField", firstBody);
            Field(secondHost, "<Current>k__BackingField", secondBody);
            foreach (var id in created)
                controller.PaperCommands.DeletePaper(id, PaperOperationContext.Plugin("tests.visual-broadcast"));
            controller.PublicSettings.Set("appearance.theme", Json(oldTheme));
            controller.PublicSettings.Set("appearance.font_scale", Json(oldZoom));
        }
    }

    private sealed class VisualBroadcastBodyProbe : IPaperBodySession
    {
        public FrameworkElement View { get; } = new TextBlock();
        public Action? ThemeChanged, TypographyChanged;
        public int Themes, Typography;
        public void OnThemeChanged(PaperBodyTheme theme) { Themes++; ThemeChanged?.Invoke(); }
        public void OnTypographyChanged(PaperBodyTheme theme) { Typography++; TypographyChanged?.Invoke(); }
        public void Dispose() { }
    }
}
