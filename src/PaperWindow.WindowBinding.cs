using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private const double WindowBindingTargetSampleMilliseconds = 32;

    private sealed record WindowBindingDragFeedback(
        Border Chrome,
        VectorPrimitiveIconElement Icon,
        TextBlock Label);

    private Button? _windowBindingButton;
    private ExternalWindowSnapshot? _windowBindingDragTarget;
    private DeviceScreenPoint? _windowBindingPressStart;
    private long _windowBindingTargetSampleTimestamp;

    private void ConfigureWindowBindingButton(Button button)
    {
        _windowBindingButton = button;
        button.Width = 24;
        button.FontSize = AppTypography.Scale(13);
        button.Cursor = Cursors.Cross;
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _windowBindingPressStart = HasExperimentalWindowTether
                ? DeviceScreenPoint.FromPoint(
                    PointToScreen(e.GetPosition(this)))
                : null;
        };
        button.PreviewMouseLeftButtonUp += (_, _) =>
        {
            var start = _windowBindingPressStart;
            _windowBindingPressStart = null;
            if (!start.HasValue ||
                !HasExperimentalWindowTether)
            {
                return;
            }

            var current = WindowNative.TryGetCursorScreenPosition(
                    out var cursor)
                ? cursor
                : start.Value;
            if (WindowWorkAreaHelper.ExceedsDragThreshold(
                    start.Value,
                    current,
                    this))
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(
                (Action)(() => ShowWindowBindingButtonMenu(button)),
                DispatcherPriority.Input);
        };
        button.PreviewMouseRightButtonUp +=
            (_, e) => OpenWindowBindingButtonMenu(button, e);
        ConfigureTopBarDragGesture(
            button,
            new TopBarDragBehavior
            {
                Kind = TopBarDragKind.WindowBinding,
                CanBegin = CanBeginAssociationDrag,
                Started = () =>
                {
                    _windowBindingDragTarget = null;
                    _windowBindingTargetSampleTimestamp = 0;
                    ExitNoteEditor();
                    if (CanBeginPaperLinkDrag())
                    {
                        _controller.BeginPaperLinkDrag(_paper);
                    }
                },
                CreateFeedback = CreateWindowBindingDragFeedback,
                Moved = UpdateAssociationDragTarget,
                Completed = CompleteAssociationDrag,
                GhostPlacement = TopBarDragGhostPlacement.PointerOffset,
                DraggingOpacity = 0.72
            });
        RefreshWindowBindingButton();
    }

    internal void RefreshAssociationButton()
    {
        RefreshWindowBindingButton();
    }

    private bool CanBeginAssociationDrag() =>
        !_paper.IsCollapsed &&
        !IsPaperFormTransitioning &&
        (CanBeginPaperLinkDrag() || CanBeginWindowBindingDrag());

    private bool CanBeginPaperLinkDrag() =>
        _controller.State.EnableTodoPaperLinks;

    private bool CanBeginWindowBindingDrag() =>
        _controller.State.ExperimentalWindowTethering &&
        WindowState == System.Windows.WindowState.Normal &&
        !_isSnappedPresentation;

    private void RefreshWindowBindingButton()
    {
        if (_windowBindingButton == null)
        {
            return;
        }

        var enabled =
            _controller.State.EnableTodoPaperLinks ||
            _controller.State.ExperimentalWindowTethering;
        var isWindowBound = HasExperimentalWindowTether;
        var isTodoLinked =
            _controller.State.EnableTodoPaperLinks &&
            _controller.IsPaperLinkedToAnyTodo(_paper);
        var isAssociated = isWindowBound || isTodoLinked;
        _windowBindingButton.Visibility =
            enabled ? Visibility.Visible : Visibility.Collapsed;
        _windowBindingButton.Content = CreateTopBarAssociationIcon(_windowBindingButton, isAssociated);
        _windowBindingButton.Cursor =
            isWindowBound ? Cursors.Hand : Cursors.Cross;
        _windowBindingButton.FontWeight =
            isAssociated ? FontWeights.Bold : FontWeights.SemiBold;
        if (isAssociated)
        {
            _windowBindingButton.Foreground = Theme.ActiveBrush;
        }
        else
        {
            _windowBindingButton.ClearValue(Control.ForegroundProperty);
        }
        _windowBindingButton.ToolTip = isWindowBound &&
            _experimentalWindowAttachment is { } session
                ? Strings.Format(
                    "ToolTipWindowBindingActiveFormat",
                    session.TargetTitle)
                : AssociationDragHint();

        UpdateTopBarResponsiveLayout();
    }

    private string AssociationDragHint()
    {
        var todoEnabled = _controller.State.EnableTodoPaperLinks;
        var windowEnabled = CanBeginWindowBindingDrag();
        if (todoEnabled && windowEnabled)
        {
            return Strings.Get("ToolTipDragPaperToAssociation");
        }
        return todoEnabled
            ? Strings.Get("ToolTipDragPaperToTodo")
            : Strings.Get("ToolTipDragPaperToWindow");
    }

    private void UpdateAssociationDragTarget(
        TopBarDragFeedback feedback,
        DeviceScreenPoint point)
    {
        _controller.UpdatePaperLinkDrag(_paper, point.ToPoint());
        if (_controller.HasPaperLinkDropTarget)
        {
            _windowBindingDragTarget = null;
            if (feedback.Context is WindowBindingDragFeedback todoVisual)
            {
                todoVisual.Chrome.BorderBrush = PaperLinkTargetBorderBrush;
                todoVisual.Chrome.Background = PaperLinkTargetBgBrush;
                todoVisual.Icon.Kind = VectorPrimitiveIconKind.TargetLocked;
                todoVisual.Label.Foreground = TextBrush;
                todoVisual.Label.Text = Strings.Get("AssociationDropTodo");
            }
            return;
        }

        if (!CanBeginWindowBindingDrag())
        {
            _windowBindingDragTarget = null;
            ResetAssociationDragFeedback(feedback);
            return;
        }

        UpdateWindowBindingDragTarget(feedback, point);
    }

    private void CompleteAssociationDrag(bool commit)
    {
        if (commit && WindowNative.TryGetCursorScreenPosition(out var cursor))
        {
            _controller.UpdatePaperLinkDrag(_paper, cursor.ToPoint());
        }

        var linkedTodo = _controller.EndPaperLinkDrag(_paper, commit);
        if (linkedTodo)
        {
            _windowBindingDragTarget = null;
            _windowBindingTargetSampleTimestamp = 0;
            if (_controller.State.EnableAnimations && _windowBindingButton != null)
            {
                AnimationHelper.QuickBounce(
                    _windowBindingButton,
                    scale: 1.16,
                    duration: 90);
            }
            RefreshWindowBindingButton();
            return;
        }

        CompleteWindowBindingDrag(commit);
    }

    private void OpenWindowBindingButtonMenu(
        FrameworkElement placementTarget,
        MouseButtonEventArgs e)
    {
        if (!HasExperimentalWindowTether)
        {
            return;
        }

        ShowWindowBindingButtonMenu(placementTarget);
        e.Handled = true;
    }

    private void ShowWindowBindingButtonMenu(
        FrameworkElement placementTarget)
    {
        if (!HasExperimentalWindowTether)
        {
            return;
        }

        var menu = CreateContextMenu();
        menu.Items.Add(MenuItem(
            Strings.Get("LabsWindowTetherDetach"),
            (_, _) => DetachExperimentalWindowAttachment(
                savePosition: true)));
        var previousContextMenu = placementTarget.ContextMenu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(placementTarget.ContextMenu, menu))
            {
                placementTarget.ContextMenu = previousContextMenu;
            }
        };
        placementTarget.ContextMenu = menu;
        menu.PlacementTarget = placementTarget;
        menu.Placement =
            System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void UpdateWindowBindingDragTarget(
        TopBarDragFeedback feedback,
        DeviceScreenPoint point)
    {
        var now = Stopwatch.GetTimestamp();
        if (_windowBindingTargetSampleTimestamp != 0 &&
            (now - _windowBindingTargetSampleTimestamp) * 1000.0 /
                Stopwatch.Frequency <
            WindowBindingTargetSampleMilliseconds)
        {
            return;
        }
        _windowBindingTargetSampleTimestamp = now;

        _windowBindingDragTarget =
            ExternalWindowNative.TryGetTargetAtPoint(
                point,
                out var target)
                ? target
                : null;
        if (feedback.Context is not WindowBindingDragFeedback visual)
        {
            return;
        }

        if (_windowBindingDragTarget is { } selected)
        {
            visual.Chrome.BorderBrush = Theme.ActiveBrush;
            visual.Chrome.Background = Theme.Tint(
                (byte)(Theme.IsDark ? 52 : 34));
            visual.Icon.Kind = VectorPrimitiveIconKind.Target;
            visual.Label.Foreground = TextBrush;
            visual.Label.Text = Strings.Format(
                "WindowBindingDropTargetFormat",
                EllipsizeWindowBindingTarget(selected.Title));
            return;
        }

        ResetAssociationDragFeedback(feedback);
    }

    private void ResetAssociationDragFeedback(TopBarDragFeedback feedback)
    {
        if (feedback.Context is not WindowBindingDragFeedback visual)
        {
            return;
        }
        visual.Chrome.BorderBrush = PaperBorderBrush;
        visual.Chrome.Background = PaperBrush;
        visual.Icon.Kind = VectorPrimitiveIconKind.Target;
        visual.Label.Foreground = WeakTextBrush;
        visual.Label.Text = AssociationDragHint();
    }

    private void CompleteWindowBindingDrag(bool commit)
    {
        if (commit &&
            WindowNative.TryGetCursorScreenPosition(out var cursor))
        {
            _windowBindingDragTarget =
                ExternalWindowNative.TryGetTargetAtPoint(
                    cursor,
                    out var finalTarget)
                    ? finalTarget
                    : null;
        }

        var target = commit ? _windowBindingDragTarget : null;
        _windowBindingDragTarget = null;
        _windowBindingTargetSampleTimestamp = 0;
        if (target is { } selected)
        {
            var attached =
                AttachExperimentalWindowTether(selected.Identity);
            if (attached &&
                _controller.State.EnableAnimations &&
                _windowBindingButton != null)
            {
                AnimationHelper.QuickBounce(
                    _windowBindingButton,
                    scale: 1.16,
                    duration: 90);
            }
        }
        RefreshWindowBindingButton();
    }

    private TopBarDragFeedback CreateWindowBindingDragFeedback()
    {
        var label = new TextBlock
        {
            Text = AssociationDragHint(),
            Foreground = WeakTextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(12),
            FontWeight = FontWeights.SemiBold,
            MaxWidth = AppTypography.Scale(240),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var icon = new VectorPrimitiveIconElement(VectorPrimitiveIconKind.Target)
        {
            Foreground = Theme.ActiveBrush,
            IconSize = AppTypography.Scale(13),
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            IsHitTestVisible = false
        };
        content.Children.Add(icon);
        content.Children.Add(label);

        var chrome = new Border
        {
            Padding = new Thickness(10, 6, 11, 6),
            CornerRadius = new CornerRadius(RadiusControl),
            Background = PaperBrush,
            BorderBrush = PaperBorderBrush,
            BorderThickness = new Thickness(1.2),
            Opacity = 0.94,
            Child = content,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 2,
                Opacity = 0.22
            }
        };
        return new TopBarDragFeedback(
            CreateTopBarDragFeedbackWindow(chrome),
            new WindowBindingDragFeedback(chrome, icon, label));
    }

    private static string EllipsizeWindowBindingTarget(string title) =>
        title.Length <= 52 ? title : title[..49] + "…";
}
