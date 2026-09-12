using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PaperTodo;

internal static partial class Program
{
    private static void ReviewLinkGestureChecks()
    {
        var panel = new StackPanel();
        // Link activation is a product contract, not a theme choice. This inherited style
        // deliberately asks normal buttons to activate on press; links must still use Release.
        var inherited = new Style(typeof(Button));
        inherited.Setters.Add(new Setter(ButtonBase.ClickModeProperty, ClickMode.Press));
        panel.Resources[typeof(Button)] = inherited;
        var opened = new List<string>();
        RenderForCheck(panel,
            "[first](https://example.com/a) [second](https://example.com/b) " + new string('文', 300),
            opened.Add, MarkdownRenderModes.Full, new Size(360, 200));
        var window = new Window { Content = panel, Width = 390, Height = 240,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        try
        {
            window.Show(); Pump();
            var buttons = Elements(panel).OfType<Button>().ToArray();
            Require(buttons.Length == 2 && buttons.All(b => b.ClickMode == ClickMode.Release),
                "link gesture cannot be replaced by an inherited press-to-click style");
            void Key(Button button, Key key, RoutedEvent route)
            {
                button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(button)!, Environment.TickCount, key) { RoutedEvent = route });
            }
            Require(buttons[0].Focus(), "link target accepts keyboard focus");
            Key(buttons[0], System.Windows.Input.Key.Space, Keyboard.KeyDownEvent);
            Require(opened.Count == 0 && buttons[0].IsPressed, "Space down alone does not activate a link");
            Key(buttons[0], System.Windows.Input.Key.Space, Keyboard.KeyUpEvent);
            Require(opened.SequenceEqual(new[] { "https://example.com/a" }),
                "WPF Space press/release activates the matching link once");
            opened.Clear();
            Key(buttons[0], System.Windows.Input.Key.Space, Keyboard.KeyDownEvent);
            Require(buttons[1].Focus(), "move keyboard focus away during a pressed gesture");
            Key(buttons[0], System.Windows.Input.Key.Space, Keyboard.KeyUpEvent);
            Require(opened.Count == 0 && !buttons[0].IsPressed,
                "losing focus cancels the old pressed link instead of activating it later");
            Key(buttons[1], System.Windows.Input.Key.Enter, Keyboard.KeyDownEvent);
            Require(opened.SequenceEqual(new[] { "https://example.com/b" }), "Enter opens the currently focused link");
            Console.WriteLine("PASS link inherited-style isolation, real Button keyboard press/release, focus cancellation and Enter");
        }
        finally { Mouse.Capture(null); window.Close(); Pump(); }
    }
}
