using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PaperTodo;

internal static class PaperNoticeDialog
{
    public static void Show(Window? owner, string titleText, string messageText)
    {
        _ = ShowCore(
            owner,
            titleText,
            messageText,
            [Strings.Get("CommonOk")], primaryIndex: 0);
    }

    public static bool ShowChoice(
        Window? owner,
        string titleText,
        string messageText,
        string secondaryText,
        string primaryText)
    {
        return ShowDecision(
            owner,
            titleText,
            messageText,
            secondaryText,
            primaryText) == true;
    }

    public static bool? ShowDecision(
        Window? owner, string titleText, string messageText, string secondaryText, string primaryText) =>
        ShowCore(owner, titleText, messageText, [secondaryText, primaryText], primaryIndex: 1) switch
        {
            0 => false,
            1 => true,
            _ => null
        };

    public static bool ShowDeleteConfirmation(Window owner)
    {
        // Startup-restored edge capsules can own an unshown PaperWindow shell.
        var dialogOwner = owner.IsVisible && PresentationSource.FromVisual(owner) is HwndSource
            ? owner : null;
        return ShowCore(dialogOwner, Strings.Get("DeletePaperQuestion"), Strings.Get("DeletePaperBody"),
            [Strings.Get("MenuDelete"), Strings.Get("CommonCancel")], primaryIndex: -1,
            width: 300, minHeight: 178, defaultIndex: 1, dangerIndex: 0,
            topmost: owner.Topmost, showInTaskbar: false, windowTitle: Strings.Get("DeletePaperTitle")) == 0;
    }

    public static void ShowPaperLimit() =>
        ShowCore(null, Strings.Get("PaperLimitTitle"), Strings.Get("PaperLimitMessage"),
            [Strings.Get("CommonOk")], primaryIndex: -1, width: 340, minHeight: 176,
            topmost: true, showInTaskbar: false, closeOnEscape: false);

    public static void ShowSaveFailure(string reason, Action<bool> onChoice) =>
        ShowCore(null, Strings.Get("SaveFailureTitle"), Strings.Format("SaveFailureMessage", reason),
            [Strings.Get("CommonOk"), Strings.Get("SaveFailureIgnore")], primaryIndex: -1,
            minHeight: 180, topmost: true, closeOnEscape: false,
            onSelected: index => onChoice(index == 1));

    public static WorkspaceRecoveryChoice ShowHistoryRecovery(Window? owner, int number, string backupTime) =>
        ShowCore(owner, Strings.Get("HistoryRecoveryTitle"),
            Strings.Format("HistoryRecoveryMessage", number, backupTime),
            [Strings.Get("HistoryRecoveryCreate"), Strings.Get("HistoryRecoveryRestore"),
                Strings.Get("WorkspaceFailureExit")], primaryIndex: 1, width: 560) switch
        {
            0 => WorkspaceRecoveryChoice.CreateEmpty,
            1 => WorkspaceRecoveryChoice.Restore,
            2 => WorkspaceRecoveryChoice.Exit,
            _ => WorkspaceRecoveryChoice.Cancel
        };

    public static WorkspaceRecoveryChoice ShowBackupReadFailure(Window? owner, int number, string reason) =>
        ShowCore(owner, Strings.Get("HistoryRecoveryTitle"),
            Strings.Format("WorkspaceBackupReadFailedMessage", number, reason),
            [Strings.Get("HistoryRecoveryCreate"), Strings.Get("WorkspaceFailureRetry")],
            primaryIndex: 1, width: 560) switch
        {
            0 => WorkspaceRecoveryChoice.CreateEmpty,
            1 => WorkspaceRecoveryChoice.Retry,
            _ => WorkspaceRecoveryChoice.Cancel
        };

    public static WorkspaceRecoveryChoice ShowUnavailableWorkspace(Window? owner, int number, int? fallbackNumber)
    {
        var actions = new List<string> { Strings.Get("HistoryRecoveryCreate") };
        if (fallbackNumber is { } fallback)
            actions.Add(Strings.Format("WorkspaceRecoveryFallback", fallback));
        actions.Add(Strings.Get("WorkspaceFailureExit"));
        return ShowCore(owner, Strings.Get("HistoryRecoveryTitle"),
            Strings.Format("WorkspaceRecoveryUnavailableMessage", number), actions.ToArray(), primaryIndex: 1, width: 560) switch
        {
            0 => WorkspaceRecoveryChoice.CreateEmpty,
            1 when fallbackNumber.HasValue => WorkspaceRecoveryChoice.Fallback,
            null => WorkspaceRecoveryChoice.Cancel,
            _ => WorkspaceRecoveryChoice.Exit
        };
    }

    private static int? ShowCore(
        Window? owner,
        string titleText,
        string messageText,
        string[] actions,
        int primaryIndex,
        double width = 360,
        double minHeight = 164,
        int? defaultIndex = null,
        int dangerIndex = -1,
        bool? topmost = null,
        bool? showInTaskbar = null,
        bool closeOnEscape = true,
        string? windowTitle = null,
        Action<int>? onSelected = null)
    {
        int? selected = null;
        var dialog = new Window
        {
            Owner = owner,
            Title = windowTitle ?? titleText,
            Width = width,
            MinHeight = minHeight,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = showInTaskbar ?? (owner == null),
            Topmost = topmost ?? owner?.Topmost ?? false,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(12),
            Language = AppTypography.Language,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        AppTypography.ApplyTextRendering(dialog);

        var root = new Border
        {
            Background = Theme.PaperBrush,
            BorderBrush = Theme.PaperBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18),
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 2,
                Opacity = 0.22
            }
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = titleText,
            Foreground = Theme.TextBrush,
            FontSize = AppTypography.Scale(16),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var message = new TextBlock
        {
            Text = messageText,
            Foreground = Theme.WeakTextBrush,
            FontSize = AppTypography.Scale(13),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            MaxWidth = width - 38,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var buttonRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        for (var index = 0; index < actions.Length; index++)
        {
            var action = index;
            var button = CreateButton(actions[index], primary: index == primaryIndex && actions.Length > 1,
                danger: index == dangerIndex);
            button.IsDefault = index == (defaultIndex ?? primaryIndex);
            if (index > 0) button.Margin = new Thickness(8, 0, 0, 0);
            button.Click += (_, _) =>
            {
                selected = action;
                onSelected?.Invoke(action);
                dialog.Close();
            };
            buttonRow.Children.Add(button);
        }

        dialog.PreviewKeyDown += (_, e) =>
        {
            if (!closeOnEscape || e.Key != Key.Escape)
            {
                return;
            }

            e.Handled = true;
            dialog.Close();
        };

        Grid.SetRow(title, 0);
        Grid.SetRow(message, 1);
        Grid.SetRow(buttonRow, 2);
        layout.Children.Add(title);
        layout.Children.Add(message);
        layout.Children.Add(buttonRow);

        root.Child = layout;
        dialog.Content = root;
        dialog.ShowDialog();
        return selected;
    }

    internal static Button CreateButton(string text, bool primary, bool danger = false, bool compact = false)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.PaddingProperty,
            compact ? new Thickness(10, 4, 10, 4) : new Thickness(16, 7, 16, 7)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(
            Control.BackgroundProperty,
            danger ? Theme.DangerBrush : compact ? Brushes.Transparent : primary ? Theme.Tint(44) : Theme.Tint(28)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, danger ? Theme.PaperBrush : Theme.TextBrush));
        style.Setters.Add(new Setter(Control.FontSizeProperty, AppTypography.Scale(13)));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 72.0));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(compact ? 6 : 8));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button))
        {
            VisualTree = border
        };

        var hover = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        hover.Setters.Add(new Setter(Control.BackgroundProperty,
            danger ? Theme.DangerHoverBrush : compact ? Theme.HoverBrush : Theme.Tint(46)));

        var pressed = new Trigger
        {
            Property = ButtonBase.IsPressedProperty,
            Value = true
        };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, compact ? 0.7 : 0.82));

        template.Triggers.Add(hover);
        template.Triggers.Add(pressed);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));

        return new Button
        {
            Content = text,
            Style = style
        };
    }
}
