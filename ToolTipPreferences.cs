using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo;

public static class ToolTipPreferences
{
    public static readonly DependencyProperty AlwaysEnabledProperty =
        DependencyProperty.RegisterAttached(
            "AlwaysEnabled",
            typeof(bool),
            typeof(ToolTipPreferences),
            new PropertyMetadata(false));

    private static bool _isRegistered;
    private static Func<bool>? _isEnabledProvider;

    public static void SetAlwaysEnabled(DependencyObject element, bool value)
    {
        element.SetValue(AlwaysEnabledProperty, value);
    }

    public static bool GetAlwaysEnabled(DependencyObject element)
    {
        return (bool)element.GetValue(AlwaysEnabledProperty);
    }

    public static void Register(Func<bool> isEnabledProvider)
    {
        _isEnabledProvider = isEnabledProvider;
        if (_isRegistered)
        {
            return;
        }

        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.ToolTipOpeningEvent,
            new ToolTipEventHandler(OnToolTipOpening),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(ToolTip),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnToolTipLoaded),
            handledEventsToo: true);
        _isRegistered = true;
    }

    public static void Apply(DependencyObject? root, bool enabled)
    {
        if (root == null)
        {
            return;
        }

        ApplyCore(root, enabled, forceEnabled: false, new HashSet<DependencyObject>());
    }

    private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (_isEnabledProvider?.Invoke() == false &&
            sender is DependencyObject owner &&
            !IsAlwaysEnabled(owner))
        {
            e.Handled = true;
        }
    }

    private static void OnToolTipLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject toolTip)
        {
            AppTypography.ApplyTextRendering(toolTip);
        }
    }

    private static void ApplyCore(DependencyObject root, bool enabled, bool forceEnabled, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(root))
        {
            return;
        }

        var childForceEnabled = forceEnabled || GetAlwaysEnabled(root);
        ToolTipService.SetIsEnabled(root, enabled || childForceEnabled);

        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject logicalChild)
            {
                ApplyCore(logicalChild, enabled, childForceEnabled, visited);
            }
        }

        try
        {
            var visualChildCount = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < visualChildCount; i++)
            {
                ApplyCore(VisualTreeHelper.GetChild(root, i), enabled, childForceEnabled, visited);
            }
        }
        catch (InvalidOperationException)
        {
            // Some DependencyObjects are logical-only and have no visual children.
        }
    }

    private static bool IsAlwaysEnabled(DependencyObject? element)
    {
        while (element != null)
        {
            if (GetAlwaysEnabled(element))
            {
                return true;
            }

            element = LogicalTreeHelper.GetParent(element) ?? TryGetVisualParent(element);
        }

        return false;
    }

    private static DependencyObject? TryGetVisualParent(DependencyObject element)
    {
        try
        {
            return VisualTreeHelper.GetParent(element);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
