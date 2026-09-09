using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Documents;
using System.Windows.Threading;

namespace PaperTodo.ThreadingChecks;

internal static partial class Program
{
    private static readonly string[] PaperResources = ["SharedContextMenuTemplate", "SharedCompactMenuItemStyle"];
    private static readonly string[] TrayResources =
    [
        "SharedTrayMenuTemplate", "SharedSeparatorTemplate", "SharedTrayMenuItemTemplate",
        "SharedSegmentMenuItemTemplate", "SharedTrayContentMenuItemTemplate",
        "SharedTrayMenuItemStyle", "SharedTrayContentMenuItemStyle", "SharedTrayToolbarItemStyle"
    ];

    private static object[] ReadAndApplyResources(Type owner, string[] names)
    {
        return names.Select(name =>
        {
            var resource = ReadStatic<DispatcherObject>(owner, name);
            resource.VerifyAccess();
            Assert(ReferenceEquals(resource, ReadStatic<DispatcherObject>(owner, name)), "same-thread cache not reused");
            Control control;
            if (resource is ControlTemplate template)
            {
                control = (Control)Activator.CreateInstance(template.TargetType)!;
                control.Template = template;
            }
            else
            {
                var style = (Style)resource;
                control = new MenuItem { Header = "Thread test", IsCheckable = true, IsChecked = true, Style = style };
            }
            Assert(control.ApplyTemplate(), $"{name} did not apply its real WPF template");
            control.Measure(new Size(300, 300));
            control.Arrange(new Rect(0, 0, 300, 300));
            return (object)resource;
        }).ToArray();
    }

    private static void CheckResources(Type owner, string[] names)
    {
        Task.Run(() => RuntimeHelpers.RunClassConstructor(owner.TypeHandle)).GetAwaiter().GetResult();
        _ = ReadAndApplyResources(owner, names);
    }

    private static void CheckSeparateUiThreads()
    {
        var first = ReadAndApplyResources(typeof(PaperWindow), PaperResources)
            .Concat(ReadAndApplyResources(typeof(AppController), TrayResources)).ToArray();
        object[]? second = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                second = ReadAndApplyResources(typeof(PaperWindow), PaperResources)
                    .Concat(ReadAndApplyResources(typeof(AppController), TrayResources)).ToArray();
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert(thread.Join(TimeSpan.FromSeconds(5)), "second STA thread timed out");
        if (failure != null) throw new InvalidOperationException("second STA failed", failure);
        Assert(second != null && first.Length == second.Length, "resource count changed between threads");
        for (var index = 0; index < first.Length; index++)
            Assert(!ReferenceEquals(first[index], second![index]), "dispatcher-owned resource shared across UI threads");
    }

    private static void CheckFrozenEasings()
    {
        var easings = Task.Run(() => new[] { AnimationHelper.SmoothEase, AnimationHelper.QuickEase, AnimationHelper.SnapEase })
            .GetAwaiter().GetResult();
        foreach (var easing in easings)
        {
            Assert(easing is Freezable { IsFrozen: true, Dispatcher: null }, "global easing is not frozen");
            Assert(double.IsFinite(easing.Ease(0.5)), "invalid easing curve");
            var target = new Border();
            target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1))
            {
                EasingFunction = easing
            });
            target.BeginAnimation(UIElement.OpacityProperty, null);
        }
    }

    private static void CheckVectorIcons()
    {
        // First initialization off the UI thread must not publish dispatcher-bound paths.
        Task.Run(() => RuntimeHelpers.RunClassConstructor(typeof(VectorPrimitiveIconElement).TypeHandle))
            .GetAwaiter().GetResult();
        foreach (var kind in Enum.GetValues<VectorPrimitiveIconKind>())
        {
            var icon = new VectorPrimitiveIconElement(kind);
            var host = new Border { Child = icon };
            TextElement.SetForeground(host, Brushes.Blue);
            host.Measure(new Size(48, 48));
            host.Arrange(new Rect(0, 0, 48, 48));
            Assert(ReferenceEquals(icon.Foreground, Brushes.Blue), $"{kind}: inherited foreground lost");
            var bitmap = new RenderTargetBitmap(48, 48, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var pixels = new byte[48 * 48 * 4];
            bitmap.CopyPixels(pixels, 48 * 4, 0);
            Assert(pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0),
                $"{kind}: empty vector drawing");
            icon.IconSize = 24;
            TextElement.SetForeground(host, Brushes.Red);
            host.Measure(new Size(48, 48));
            host.Arrange(new Rect(0, 0, 48, 48));
            Assert(icon.DesiredSize == new Size(24, 24), $"{kind}: scale change did not resize icon");
            Assert(ReferenceEquals(icon.Foreground, Brushes.Red), $"{kind}: theme change lost");
        }

        CheckSmallVectorIcons();

        var label = new TextBlock { FontSize = 14, Foreground = Brushes.Blue };
        VectorPrimitiveIconElement.SetInlineIcon(label, VectorPrimitiveIconKind.Close);
        label.Measure(new Size(100, 40));
        label.Arrange(new Rect(0, 0, 100, 40));
        var inlineIcon = (VectorPrimitiveIconElement)((InlineUIContainer)label.Inlines.FirstInline).Child;
        Assert(inlineIcon.IconSize == 14 && ReferenceEquals(inlineIcon.Foreground, Brushes.Blue),
            "inline operation icon lost label typography or foreground");
        label.Text = "Cancel";
        Assert(!label.Inlines.OfType<InlineUIContainer>().Any(), "text state retained the operation icon");
    }

    private static void CheckSmallVectorIcons()
    {
        foreach (var (kind, width, height) in new[]
        {
            (VectorPrimitiveIconKind.Plus, 8, 8),
            (VectorPrimitiveIconKind.Plus, 9, 9),
            (VectorPrimitiveIconKind.Minus, 16, 10)
        })
        foreach (var explicitSize in new[] { true, false })
        {
            var size = new Size(width, height);
            var name = $"{kind} {size} ({(explicitSize ? "explicit size" : "available space")})";
            var icon = new VectorPrimitiveIconElement(kind)
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            if (explicitSize) { icon.Width = width; icon.Height = height; }
            var host = new Border { Child = icon };
            var hostSize = explicitSize ? new Size(48, 48) : size;
            host.Measure(hostSize);
            host.Arrange(new Rect(hostSize));
            Assert(icon.DesiredSize == size && icon.RenderSize == size,
                $"{name}: expected {size}, desired {icon.DesiredSize}, rendered {icon.RenderSize}");

            var bitmap = new RenderTargetBitmap(48, 48, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var pixels = new byte[48 * 48 * 4];
            bitmap.CopyPixels(pixels, 48 * 4, 0);
            bool HasPixel(int x, int y) => pixels[(y * 48 + x) * 4 + 3] > 0;
            var centerX = width / 2;
            var centerY = height / 2;
            Assert(HasPixel(centerX, centerY) && HasPixel(centerX - 2, centerY) && HasPixel(centerX + 2, centerY),
                $"{name}: horizontal stroke is clipped or off center");
            if (kind == VectorPrimitiveIconKind.Plus)
                Assert(HasPixel(centerX, centerY - 2) && HasPixel(centerX, centerY + 2)
                    && !HasPixel(centerX - 2, centerY - 2) && !HasPixel(centerX + 2, centerY + 2),
                    $"{name}: plus is incomplete");
            else
                Assert(!HasPixel(centerX, centerY - 1) && !HasPixel(centerX, centerY + 1),
                    $"{name}: minus is not a single centered stroke");
            for (var y = 0; y < 48; y++)
                for (var x = 0; x < 48; x++)
                    if (x == 0 || y == 0 || x >= width - 1 || y >= height - 1)
                        Assert(!HasPixel(x, y), $"{name}: drawing touches or exceeds the layout edge");
        }
    }

    private static void CheckMenuScaleRefresh()
    {
        AppTypography.Configure(null, 1.0);
        var menu = new ContextMenu();
        // Reuse an existing, implicitly styled item. Assigning a new Style directly to a
        // fresh item would not check whether menu resource replacement reaches live items.
        var item = new MenuItem { Header = "Scale", IsCheckable = true, IsChecked = true };
        item.Items.Add(new MenuItem { Header = "Child" });
        menu.Items.Add(item);
        var refresh = typeof(PaperWindow).GetMethod("RefreshContextMenuTypography", PrivateStatic)!;
        var original = ReadStatic<Style>(typeof(PaperWindow), "SharedCompactMenuItemStyle");
        menu.Resources[typeof(MenuItem)] = original;
        CheckGlyphSizes(item);
        AppTypography.Configure(null, 1.5);
        Assert(AppTypography.ScaleFactor != 1.0, "test scale was normalized to the original value");
        refresh.Invoke(null, [menu]);
        var updated = (Style)menu.Resources[typeof(MenuItem)];
        Assert(!ReferenceEquals(original, updated), "existing menu still holds the old scale's style");
        Assert(ReferenceEquals(updated, ReadStatic<Style>(typeof(PaperWindow), "SharedCompactMenuItemStyle")),
            "new and existing menus do not share the current style");
        CheckGlyphSizes(item);
        AppTypography.Configure(null, 1.0);
        refresh.Invoke(null, [menu]);
        CheckGlyphSizes(item);
    }

    private static void CheckGlyphSizes(MenuItem item)
    {
        item.ApplyTemplate();
        var check = (VectorPrimitiveIconElement)item.Template.FindName("CheckMark", item);
        var arrow = (VectorPrimitiveIconElement)item.Template.FindName("SubMenuArrow", item);
        var host = (Border)item.Template.FindName("CheckHost", item);
        Assert(check.IconSize == AppTypography.Scale(11), "check glyph retained its old icon size");
        Assert(arrow.IconSize == AppTypography.Scale(14), "submenu arrow retained its old icon size");
        Assert(host.Width == AppTypography.Scale(13), "check column retained its old width");
    }
}
