using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
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
        var check = (TextBlock)item.Template.FindName("CheckMark", item);
        var arrow = (TextBlock)item.Template.FindName("SubMenuArrow", item);
        var host = (Border)item.Template.FindName("CheckHost", item);
        Assert(check.FontSize == AppTypography.Scale(11), "check glyph retained its old font size");
        Assert(arrow.FontSize == AppTypography.Scale(14), "submenu arrow retained its old font size");
        Assert(host.Width == AppTypography.Scale(13), "check column retained its old width");
    }
}
