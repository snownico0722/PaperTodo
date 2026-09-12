using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PaperTodo;

internal static partial class Program
{
    private static void ArtifactSurfaceChecks()
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 200, 40));
        drawing.Freeze();
        var artifact = new MarkdownPreviewArtifact(drawing, 200, 40, false, new[]
        {
            new MarkdownPreviewArtifactLink(new Rect(12, 7, 64, 18), "https://example.com/a"),
            new MarkdownPreviewArtifactLink(new Rect(90, 7, 64, 18), "https://example.com/b")
        });
        var opened = new List<string>();
        var surface = new MarkdownPreviewArtifactSurface(artifact, opened.Add);
        var inherited = new Style(typeof(Button));
        inherited.Setters.Add(new Setter(ButtonBase.ClickModeProperty, ClickMode.Press));
        surface.Resources[typeof(Button)] = inherited;
        var window = new Window { Content = surface, Width = 240, Height = 120, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump();
            var buttons = surface.Children.OfType<Button>().ToArray();
            Require(buttons.Length == 2 && surface.Children.Count == 2 && artifact.Drawing.IsFrozen,
                "one immutable drawing plus native link hits replaces the WPF block tree");
            Require(surface.DesiredSize == new Size(200, 40), "artifact surface keeps prepared geometry");
            Button? Hit(Point point)
            {
                for (var node = surface.InputHitTest(point) as DependencyObject;
                     node != null && node != surface; node = VisualTreeHelper.GetParent(node))
                    if (node is Button button) return button;
                return null;
            }
            Require(Hit(new Point(20, 12)) == buttons[0] && Hit(new Point(100, 12)) == buttons[1],
                "artifact links are independent native input targets");
            Require(surface.InputHitTest(new Point(82, 12)) == null &&
                    surface.InputHitTest(new Point(150, 30)) == null,
                "gaps and background remain transparent to the paper-open gesture");
            surface.Clip = new RectangleGeometry(new Rect(0, 0, 80, 40));
            surface.UpdateLayout();
            Require(Hit(new Point(100, 12)) == null && !buttons[1].IsEnabled,
                "clipped links cannot receive pointer or keyboard input");
            // Rect.IntersectsWith includes touching edges. Zero visible pixels are not an
            // accessible link, including while the shared clip geometry is being changed.
            var clip = new RectangleGeometry(new Rect(0, 0, 200, 7));
            surface.Clip = clip;
            surface.UpdateLayout();
            Require(buttons.All(button => !button.IsEnabled), "links touching the clip edge have no visible pixels");
            clip.Rect = new Rect(0, 0, 200, 8);
            surface.UpdateLayout();
            Require(buttons.All(button => button.IsEnabled), "partially visible links regain native input");
            clip.Rect = new Rect(0, 0, 200, 0);
            surface.UpdateLayout();
            Require(buttons.All(button => !button.IsEnabled), "zero-height clips expose no keyboard targets");
            surface.Clip = null;
            surface.UpdateLayout();
            Require(buttons.All(b => b.ClickMode == ClickMode.Release), "inherited styles cannot make links activate on press");
            foreach (var button in buttons)
                button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Require(opened.Count == 0, "unpaired artifact releases never open links");
            void Key(Button button, Key key, RoutedEvent route) => button.RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice, PresentationSource.FromVisual(button)!, Environment.TickCount, key) { RoutedEvent = route });
            Require(buttons[0].Focus(), "artifact link accepts keyboard focus");
            Key(buttons[0], System.Windows.Input.Key.Space, Keyboard.KeyDownEvent);
            Require(opened.Count == 0 && buttons[0].IsPressed, "Space down alone does not activate an artifact link");
            Key(buttons[0], System.Windows.Input.Key.Space, Keyboard.KeyUpEvent);
            Require(opened.SequenceEqual(new[] { "https://example.com/a" }), "Space release activates the focused link exactly once");
            opened.Clear();
            Key(buttons[0], System.Windows.Input.Key.Space, Keyboard.KeyDownEvent);
            Require(buttons[0].MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)) && buttons[1].IsKeyboardFocused,
                "native focus traversal reaches the next artifact link");
            Key(buttons[0], System.Windows.Input.Key.Space, Keyboard.KeyUpEvent);
            Require(opened.Count == 0 && !buttons[0].IsPressed, "focus loss cancels a pressed artifact link");
            Key(buttons[1], System.Windows.Input.Key.Enter, Keyboard.KeyDownEvent);
            Require(opened.SequenceEqual(new[] { "https://example.com/b" }), "Enter opens the second focused artifact link");
            Console.WriteLine("PASS artifact native links: hit regions, clipping, background, release, focus traversal and keyboard cancellation");
        }
        finally { Mouse.Capture(null); window.Close(); Pump(); }
    }
}
