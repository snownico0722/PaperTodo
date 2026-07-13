using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Control = System.Windows.Controls.Control;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using Separator = System.Windows.Controls.Separator;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private EdgeCapsuleHost EnsureDeepCapsuleSlotHost()
    {
        if (_edgeCapsuleHost != null)
        {
            return _edgeCapsuleHost;
        }

        _edgeCapsuleHost = EdgeCapsuleHost.Create(new EdgeCapsuleHostOptions(
            WindowChromeMargin,
            CapsuleChromeCornerRadius,
            CapsuleInnerCornerRadius,
            DeepCapsuleSlotOutlineThickness,
            DeepCapsuleSlotOutlineOverlap,
            Math.Max(0, PaperLayoutDefaults.CapsuleHeight - WindowChromeInset),
            CapsuleLeftPadding,
            CapsuleIconGap,
            CapsuleIconText(),
            CapsuleIconFontSizeForCurrentPaper(),
            CapsuleLabelFontSize,
            Strings.Get("ToolTipHideThisPaper"),
            PaperBrush,
            PaperBorderBrush,
            Theme.CapsuleFocusBorderBrush,
            HoverBrush,
            BrightWeakTextBrush,
            TextBrush,
            WeakTextBrush,
            AppTypography.UiFontFamily,
            AppTypography.SymbolFontFamily,
            AppTypography.Language,
            !_controller.SuppressTopmostForFullscreenForeground));
        var host = _edgeCapsuleHost;
        AttachDeepCapsuleSlotHostInput();
        host.AttachNativeHooks(
            OnDeepCapsuleSlotHostMessage,
            CloseDeepCapsuleSlotContextMenu);
        UpdateDeepCapsuleSlotHostTheme();
        return host;
    }

    private IntPtr OnDeepCapsuleSlotHostMessage(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        // The slot host is not a paper window. In particular it must not inherit the paper's
        // snap/maximize WM_GETMINMAXINFO handling or minimum tracking size. It only participates
        // in display-metric refresh and then lets WPF process the native message normally.
        if (msg is WmDpiChanged or WmDisplayChange or WmSettingChange)
        {
            WindowWorkAreaHelper.InvalidateMonitorGeometryCache();
            _edgeCapsule.RequestPresentation(EdgeCapsuleMotion.Preserve(
                EdgeCapsuleTransitionReason.DisplayMetrics));
            InvalidateEdgeCapsule(EdgeCapsuleDirty.Presentation);
            if (IsDeepCapsuleReordering)
            {
                _controller.DeferDisplayMetricsRefreshUntilDeepCapsuleDragEnds();
            }
            else
            {
                _controller.ScheduleDisplayMetricsRefresh();
            }
        }

        return IntPtr.Zero;
    }

    private void AttachDeepCapsuleSlotHostInput()
    {
        if (_edgeCapsuleHost == null)
        {
            return;
        }
        _edgeCapsuleHost.AttachInput(new EdgeCapsuleHostCallbacks(
            SetDeepCapsuleHover,
            OnEdgeCapsulePointerPressed,
            OnEdgeCapsulePointerMoved,
            OnEdgeCapsulePointerReleased,
            OnEdgeCapsuleCaptureLost,
            OnEdgeCapsuleCloseInvoked));
        _edgeCapsuleHost.SetContextMenu(BuildDeepCapsuleSlotContextMenu());
        RefreshDeepCapsuleSlotLabel();
    }

    private void OnEdgeCapsulePointerPressed(DeviceScreenPoint screenPosition) =>
        BeginEdgeCapsulePointerInteraction(screenPosition);

    private bool OnEdgeCapsulePointerMoved(
        DeviceScreenPoint currentScreenPosition,
        bool leftButtonPressed)
    {
        if ((IsDeepCapsuleReordering || IsDeepCapsuleSlotPendingClick) && !leftButtonPressed)
        {
            if (IsDeepCapsuleReordering)
            {
                EndDeepCapsuleReorderDrag(commit: false);
                ClearCapsuleInteractionKeyboardFocus();
            }
            else
            {
                FinishEdgeCapsulePointerInteraction();
            }
            _edgeCapsuleHost?.ReleaseContentPointer();
            return true;
        }

        if (IsDeepCapsuleReordering)
        {
            UpdateDeepCapsuleReorderDrag(currentScreenPosition);
            return true;
        }
        if (!IsDeepCapsuleSlotPendingClick)
        {
            return false;
        }
        if (!TryGetEdgeCapsuleDragSession(out var session))
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            _edgeCapsuleHost?.ReleaseContentPointer();
            return true;
        }

        var deltaX = Math.Abs(currentScreenPosition.X - session.PointerDownScreenPosition.X);
        var deltaY = Math.Abs(currentScreenPosition.Y - session.PointerDownScreenPosition.Y);
        if (CanReorderDeepCapsuleSlot())
        {
            if (deltaY >= SystemParameters.MinimumVerticalDragDistance + DeepCapsuleReorderDragExtraThreshold ||
                deltaX >= SystemParameters.MinimumHorizontalDragDistance + DeepCapsuleReorderDragExtraThreshold)
            {
                StartDeepCapsuleReorderDrag(currentScreenPosition);
                return true;
            }
            return false;
        }

        if (deltaX >= SystemParameters.MinimumHorizontalDragDistance ||
            deltaY >= SystemParameters.MinimumVerticalDragDistance)
        {
            FinishEdgeCapsulePointerInteraction();
            _edgeCapsuleHost?.ReleaseContentPointer();
        }
        return false;
    }

    private bool OnEdgeCapsulePointerReleased(DeviceScreenPoint _)
    {
        if (IsDeepCapsuleReordering)
        {
            EndDeepCapsuleReorderDrag(commit: true);
            ClearCapsuleInteractionKeyboardFocus();
            return true;
        }
        if (!IsDeepCapsuleSlotPendingClick)
        {
            return false;
        }

        FinishEdgeCapsulePointerInteraction();
        try
        {
            ActivateFromDeepCapsuleSlot();
        }
        finally
        {
            ClearCapsuleInteractionKeyboardFocus();
        }
        return true;
    }

    private EdgeCapsuleCaptureAction OnEdgeCapsuleCaptureLost(bool leftButtonPressed)
    {
        var action = _edgeCapsule.HandleCaptureLost(leftButtonPressed);
        if (action == EdgeCapsuleCaptureAction.CancelDrag)
        {
            EndDeepCapsuleReorderDrag(commit: false);
            ClearCapsuleInteractionKeyboardFocus();
        }
        return action;
    }

    private void OnEdgeCapsuleCloseInvoked()
    {
        _controller.HidePaper(_paper);
        ClearCapsuleInteractionKeyboardFocus();
    }

    private void ActivateFromDeepCapsuleSlot()
    {
        CloseDeepCapsuleSlotContextMenu();
        if (TryRunScriptCapsule())
        {
            return;
        }

        if (_paper.IsCollapsed)
        {
            ShowMainWindowForDeepCapsuleActivation();
            SetCollapsedState(false, alignExpandedToDockedEdge: true, activateOnExpand: true);
        }
        else if (_controller.State.CollapseExpandedDeepCapsuleOnClick &&
            (HoldsDeepCapsuleSlotWhileExpanded || HasExpandedDeepCapsuleSlotReservation))
        {
            SetCollapsedState(true, alignExpandedToDockedEdge: true);
        }
        else
        {
            EnsureExpandedSurfaceGeometry(alignToDockedEdge: true);
            _controller.BringPaperToFront(_paper);
        }
    }

    public bool TryHandleLinkedNoteRepeatedOpenAsDeepCapsuleToggle()
    {
        if (_paper.IsCollapsed ||
            !_paper.IsVisible ||
            !_controller.State.UseCapsuleMode ||
            !_controller.State.UseDeepCapsuleMode ||
            !_controller.State.ShowDeepCapsuleWhileExpanded ||
            !CanDisplayAsCapsule())
        {
            return false;
        }

        if (!HoldsDeepCapsuleSlotWhileExpanded && !HasExpandedDeepCapsuleSlotReservation)
        {
            SetEdgeCapsuleOpenOrigin(EdgeCapsuleOpenOrigin.EdgeSlot);
        }

        SetCollapsedState(true, alignExpandedToDockedEdge: true);
        return true;
    }

    private void ShowMainWindowForDeepCapsuleActivation()
    {
        if (IsVisible)
        {
            BeginAnimation(Window.OpacityProperty, null);
            Opacity = 1.0;
            return;
        }

        BeginAnimation(Window.OpacityProperty, null);
        Opacity = 1.0;
        MoveWindowWithoutGeometrySave(() =>
        {
            Width = DesiredCapsuleWindowWidth;
            Height = PaperLayoutDefaults.CapsuleHeight;
            if (_edgeCapsuleHost != null)
            {
                // PointToScreen is physical pixels; convert through the app's global screen-DIP
                // coordinate space. Rounding with this hidden paper HWND's old monitor DPI is the
                // exact mixed-DPI bug this hand-off must avoid.
                var slotOrigin = WindowWorkAreaHelper.DeviceScreenPointToDip(
                    _edgeCapsuleHost.ScreenOrigin());
                Left = slotOrigin.X;
                Top = slotOrigin.Y;
            }
            else
            {
                Left = _paper.X;
                Top = _paper.Y;
            }
            Show();
        });
    }

    private void HideMainWindowForDeepCapsuleRest()
    {
        if (!_paper.IsCollapsed || !_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            return;
        }

        if (!IsVisible)
        {
            return;
        }

        BeginAnimation(Window.OpacityProperty, null);
        Opacity = 1.0;
        HideWithoutGeometrySave();
    }

    internal void HideMainWindowForDeepCapsuleMode()
    {
        HideMainWindowForDeepCapsuleRest();
    }

    public void EnsureExpandedSurfaceGeometry(bool alignToDockedEdge = false)
    {
        if (_paper.IsCollapsed)
        {
            return;
        }

        var needsRestore =
            !IsVisible ||
            IsPaperFormTransitioning ||
            Width <= DesiredCapsuleWindowWidth + 8 ||
            Height <= PaperLayoutDefaults.CapsuleHeight + 8 ||
            _shell.Visibility != Visibility.Visible ||
            _capsuleShell.Visibility == Visibility.Visible;
        if (!needsRestore)
        {
            return;
        }

        _collapseTransitionGeneration++;
        BeginAnimation(TransitionProgressProperty, null);
        _shell.BeginAnimation(UIElement.OpacityProperty, null);
        _capsuleShell.BeginAnimation(UIElement.OpacityProperty, null);
        ResetTransitionVisuals();

        CompletePaperFormTransition(collapsed: false);
        _shell.Width = double.NaN;
        _shell.Height = double.NaN;
        _shell.Visibility = Visibility.Visible;
        _shell.Opacity = 1.0;
        _capsuleShell.Visibility = Visibility.Collapsed;
        _capsuleShell.Opacity = 0.0;
        MinWidth = PaperLayoutDefaults.MinWidth;
        MinHeight = PaperLayoutDefaults.MinHeight;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        var rawTargetWidth = Math.Max(_paper.Width, PaperLayoutDefaults.MinWidth);
        var rawTargetHeight = Math.Max(_paper.Height, PaperLayoutDefaults.MinHeight);
        Rect? rememberedDeepCapsuleExpandedGeometry = null;
        if (alignToDockedEdge &&
            ExpandedFromDeepCapsuleEdge &&
            _controller.TryGetRememberedDeepCapsuleExpandedGeometry(_paper, rawTargetWidth, rawTargetHeight, out var rememberedGeometry))
        {
            rememberedDeepCapsuleExpandedGeometry = rememberedGeometry;
            rawTargetWidth = rememberedGeometry.Width;
            rawTargetHeight = rememberedGeometry.Height;
        }

        var targetWidth = RoundToDevicePixelX(rawTargetWidth);
        var targetHeight = RoundToDevicePixelY(rawTargetHeight);
        MoveWindowWithoutGeometrySave(() =>
        {
            Width = targetWidth;
            Height = targetHeight;
            if (alignToDockedEdge)
            {
                if (rememberedDeepCapsuleExpandedGeometry is Rect rememberedRect)
                {
                    Left = RoundToDevicePixelX(rememberedRect.Left);
                    Top = RoundToDevicePixelY(rememberedRect.Top);
                }
                else
                {
                    var requiredEdgeInset = _controller.State.ShowDeepCapsuleWhileExpanded && _controller.CanPaperDisplayAsCapsule(_paper)
                        ? ExpandedDeepCapsuleVisibleWidth() + DeepCapsuleGap
                        : 0;
                    AlignExpandedToDockedEdge(targetWidth, targetHeight, requiredEdgeInset);
                }
            }
        });

        if (!IsVisible)
        {
            Opacity = 1.0;
            Show();
        }

        RefreshEffectiveTopmost();
    }

    public bool TryRestoreRememberedDeepCapsuleExpandedGeometry()
    {
        if (_paper.IsCollapsed ||
            !_controller.State.UseCapsuleMode ||
            !_controller.State.UseDeepCapsuleMode ||
            !_controller.State.ShowDeepCapsuleWhileExpanded ||
            !CanDisplayAsCapsule())
        {
            return false;
        }

        var fallbackWidth = Math.Max(_paper.Width, PaperLayoutDefaults.MinWidth);
        var fallbackHeight = Math.Max(_paper.Height, PaperLayoutDefaults.MinHeight);
        if (!_controller.TryGetRememberedDeepCapsuleExpandedGeometry(_paper, fallbackWidth, fallbackHeight, out var rememberedGeometry))
        {
            return false;
        }

        SetEdgeCapsuleOpenOrigin(EdgeCapsuleOpenOrigin.EdgeSlot);
        MoveWindowWithoutGeometrySave(() =>
        {
            Left = RoundToDevicePixelX(rememberedGeometry.Left);
            Top = RoundToDevicePixelY(rememberedGeometry.Top);
            Width = RoundToDevicePixelX(rememberedGeometry.Width);
            Height = RoundToDevicePixelY(rememberedGeometry.Height);
        });
        return true;
    }

    public void ExpandForProgrammaticOpen()
    {
        if (!_paper.IsCollapsed)
        {
            EnsureExpandedSurfaceGeometry(alignToDockedEdge: true);
            return;
        }

        if (_controller.State.UseCapsuleMode &&
            _controller.State.UseDeepCapsuleMode &&
            HasDeepCapsuleSlotPlacement)
        {
            ShowMainWindowForDeepCapsuleActivation();
            SetCollapsedState(false);
            return;
        }

        if (!IsVisible)
        {
            BeginAnimation(Window.OpacityProperty, null);
            Opacity = 1.0;
            Left = _paper.X;
            Top = _paper.Y;
            Width = DesiredCapsuleWindowWidth;
            Height = PaperLayoutDefaults.CapsuleHeight;
            Show();
        }

        SetCollapsedState(false);
    }

    private void UpdateDeepCapsuleSlotHostTheme()
    {
        _edgeCapsuleHost?.UpdateTheme(
            PaperBrush,
            PaperBorderBrush,
            Theme.CapsuleFocusBorderBrush,
            HoverBrush,
            BrightWeakTextBrush,
            TextBrush,
            WeakTextBrush,
            CapsuleIconText(),
            CapsuleIconFontSizeForCurrentPaper());
    }

    private void RequestEdgeCapsulePresentation(
        bool animate,
        EdgeCapsuleTransitionReason reason,
        int durationMilliseconds = EdgeCapsuleLayout.SlotMoveMilliseconds,
        bool immediate = false)
    {
        animate = animate && _controller.State.EnableAnimations;
        _edgeCapsule.RequestPresentation(animate
            ? EdgeCapsuleMotion.Animate(reason, durationMilliseconds)
            : EdgeCapsuleMotion.Snap(reason));
        if (immediate)
        {
            _ = ReconcileEdgeCapsule(EdgeCapsuleDirty.Presentation);
            return;
        }
        InvalidateEdgeCapsule(EdgeCapsuleDirty.Presentation);
    }

    private EdgeCapsuleLayoutSnapshot CaptureEdgeCapsuleLayoutSnapshot()
    {
        var monitor = DeepCapsuleMonitorGeometry();
        var placement = _edgeCapsule.Placement;
        var normalTop = placement.IsPlaced
            ? MyTopForIndex(placement.VisualIndex, placement.SlotCount)
            : 0;
        var masterTop = placement.IsPlaced
            ? MyTopForIndex(0, placement.SlotCount)
            : normalTop;
        return new EdgeCapsuleLayoutSnapshot(
            monitor,
            MyDeepCapsuleEdge,
            normalTop,
            masterTop,
            DeepCapsuleVisibleWidth(monitor.DpiScaleY),
            CapsuleCloseWidth,
            PaperLayoutDefaults.CapsuleHeight);
    }

    private bool ApplyEdgeCapsulePresentationFrame(EdgeCapsulePresentationFrame frame)
    {
        if (!frame.Visible)
        {
            return _edgeCapsuleHost?.Apply(frame) ?? true;
        }

        EnsureDeepCapsuleSlotHost();
        return _edgeCapsuleHost?.Apply(frame) == true;
    }

    private bool ReconcileEdgeCapsulePointerIntent()
    {
        if (!HasDeepCapsuleSlotPlacement ||
            !_paper.IsCollapsed ||
            !_controller.State.UseDeepCapsuleMode ||
            IsDeepCapsuleReordering ||
            IsDeepCapsuleSlotActive)
        {
            return false;
        }

        var hovering = _edgeCapsule.ContextMenuOpen;
        if (!hovering &&
            WindowNative.TryGetCursorScreenPosition(out var pointer) &&
            _edgeCapsuleHost != null)
        {
            hovering = _edgeCapsuleHost.ContainsScreenPoint(pointer);
        }
        var target = hovering
            ? EdgeCapsuleVisualState.Hovered
            : EdgeCapsuleVisualState.Resting;
        if (target == EdgeCapsuleVisual)
        {
            return false;
        }

        SetEdgeCapsuleVisualState(target);
        _edgeCapsule.RequestPresentation(EdgeCapsuleMotion.Animate(
            EdgeCapsuleTransitionReason.Pointer));
        return true;
    }

    private void ScheduleDeepCapsuleSlotMeasureRefresh()
    {
        if (_edgeCapsuleHost?.IsVisible == true && HasDeepCapsuleSlotPlacement)
        {
            InvalidateEdgeCapsule(EdgeCapsuleDirty.Measure);
        }
    }

    private void InvalidateEdgeCapsule(EdgeCapsuleDirty dirty)
    {
        var dispatcher = _edgeCapsuleHost?.Dispatcher ?? Dispatcher;
        _edgeCapsule.Invalidate(dirty, dispatcher, ReconcileEdgeCapsule);
    }

    private void WakeEdgeCapsuleReconcile()
    {
        var dispatcher = _edgeCapsuleHost?.Dispatcher ?? Dispatcher;
        _edgeCapsule.Wake(dispatcher, ReconcileEdgeCapsule);
    }

    private EdgeCapsuleDirty ReconcileEdgeCapsule(EdgeCapsuleDirty dirty)
    {
        var remaining = EdgeCapsuleDirty.None;
        if ((dirty & EdgeCapsuleDirty.Pointer) != 0)
        {
            if (ReconcileEdgeCapsulePointerIntent())
            {
                dirty |= EdgeCapsuleDirty.Presentation;
            }
        }
        if ((dirty & EdgeCapsuleDirty.Measure) != 0)
        {
            if (_deepCapsuleFloatingDragHost != null || IsDeepCapsuleReordering)
            {
                remaining |= EdgeCapsuleDirty.Measure;
            }
            else
            {
                _edgeCapsule.RequestPresentation(EdgeCapsuleMotion.Preserve(
                    EdgeCapsuleTransitionReason.Measure));
                dirty |= EdgeCapsuleDirty.Presentation;
            }
        }

        if ((dirty & (EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Frame)) != 0)
        {
            var result = _edgeCapsule.ReconcilePresentation(
                CaptureEdgeCapsuleLayoutSnapshot(),
                ApplyEdgeCapsulePresentationFrame);
            if (!result.Applied)
            {
                remaining |= EdgeCapsuleDirty.Presentation;
            }
            if (result.NeedsNextFrame)
            {
                remaining |= EdgeCapsuleDirty.Frame;
            }
            if (result.RetractionCompleted)
            {
                UpdateDeepCapsuleSlotHostTheme();
                UpdateCapsuleClosePlacement();
            }
        }

        return remaining;
    }

    private void CloseExpandedDeepCapsuleSlotHostForReal()
    {
        CancelDeepCapsuleReorderDrag();
        CloseDeepCapsuleFloatingDragHost(restoreDockedRoot: false);
        CloseDeepCapsuleSlotContextMenu();
        _edgeCapsule.Reset();
        _edgeCapsuleHost?.Dispose();
        _edgeCapsuleHost = null;
    }

    private ContextMenu BuildDeepCapsuleSlotContextMenu()
    {
        var menu = BuildPaperContextMenu(forDeepCapsuleSlot: true);

        menu.Opened += (_, _) =>
        {
            if (_deepCapsuleSlotContextMenu != null && !ReferenceEquals(_deepCapsuleSlotContextMenu, menu))
            {
                _deepCapsuleSlotContextMenu.IsOpen = false;
            }

            _deepCapsuleSlotContextMenu = menu;
            SetDeepCapsuleSlotContextMenuOpen(true);
            SetDeepCapsuleHover(true);
            StartDeepCapsuleContextMenuGuards();
            PromoteDeepCapsuleContextMenu(menu);
            _ = menu.Dispatcher.BeginInvoke(
                () => PromoteDeepCapsuleContextMenu(menu),
                System.Windows.Threading.DispatcherPriority.Input);
        };

        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_deepCapsuleSlotContextMenu, menu))
            {
                _deepCapsuleSlotContextMenu = null;
                SetDeepCapsuleSlotContextMenuOpen(false);
                StopDeepCapsuleContextMenuGuards();
                SetDeepCapsuleHover(_edgeCapsuleHost?.IsSurfaceMouseOver == true);
            }
        };

        return menu;
    }

    private static void PromoteDeepCapsuleContextMenu(ContextMenu menu)
    {
        if (menu.IsOpen && PresentationSource.FromVisual(menu) is HwndSource source)
        {
            WindowNative.ApplyTopmostZOrder(source.Handle, topmost: true, insertAfter: IntPtr.Zero);
        }
    }

    private void CloseDeepCapsuleSlotContextMenu()
    {
        var menu = _deepCapsuleSlotContextMenu;
        if (menu != null)
        {
            menu.IsOpen = false;
        }

        _deepCapsuleSlotContextMenu = null;
        SetDeepCapsuleSlotContextMenuOpen(false);
        StopDeepCapsuleContextMenuGuards();
    }

    private void SetDeepCapsuleSlotContextMenuOpen(bool open)
    {
        if (_edgeCapsule.ContextMenuOpen == open)
        {
            RefreshDeepCapsuleSlotTopmost();
            return;
        }

        SetEdgeCapsuleContextMenuOpen(open);
        _controller.SetDeepCapsuleContextMenuOpen(_paper.Id, open);

        RefreshDeepCapsuleSlotTopmost();
    }

    private void StartDeepCapsuleContextMenuGuards()
    {
        if (_deepCapsuleForegroundHook == IntPtr.Zero)
        {
            _deepCapsuleForegroundHookProc = OnDeepCapsuleForegroundChanged;
            _deepCapsuleForegroundHook = SetWinEventHook(
                EventSystemForeground,
                EventSystemForeground,
                IntPtr.Zero,
                _deepCapsuleForegroundHookProc,
                0,
                0,
                WineventOutOfContext);
        }

        if (_deepCapsuleMouseHook == IntPtr.Zero)
        {
            _deepCapsuleMouseHookProc = OnDeepCapsuleMouseHook;
            _deepCapsuleMouseHook = SetWindowsHookEx(WhMouseLl, _deepCapsuleMouseHookProc, GetModuleHandle(null), 0);
        }
    }

    private void StopDeepCapsuleContextMenuGuards()
    {
        if (_deepCapsuleForegroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_deepCapsuleForegroundHook);
            _deepCapsuleForegroundHook = IntPtr.Zero;
        }

        _deepCapsuleForegroundHookProc = null;

        if (_deepCapsuleMouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_deepCapsuleMouseHook);
            _deepCapsuleMouseHook = IntPtr.Zero;
        }

        _deepCapsuleMouseHookProc = null;
    }

    private void OnDeepCapsuleForegroundChanged(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (_deepCapsuleSlotContextMenu?.IsOpen != true || hwnd == IntPtr.Zero || IsWindowFromCurrentProcess(hwnd))
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(CloseDeepCapsuleSlotContextMenu));
    }

    private IntPtr OnDeepCapsuleMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsMouseButtonDownMessage(wParam) && _deepCapsuleSlotContextMenu?.IsOpen == true)
        {
            var hook = Marshal.PtrToStructure<MouseHookStruct>(lParam);
            var screenPoint = new Point(hook.Point.X, hook.Point.Y);
            if (!IsPointInsideDeepCapsuleContextSurface(screenPoint))
            {
                Dispatcher.BeginInvoke(new Action(CloseDeepCapsuleSlotContextMenu));
            }
        }

        return CallNextHookEx(_deepCapsuleMouseHook, nCode, wParam, lParam);
    }

    private bool IsPointInsideDeepCapsuleContextSurface(Point screenPoint)
    {
        if (IsPointInsideElement(_deepCapsuleSlotContextMenu, screenPoint))
        {
            return true;
        }

        return _edgeCapsuleHost?.ContainsWindowScreenPoint(screenPoint) == true;
    }

    private static bool IsPointInsideElement(FrameworkElement? element, Point screenPoint)
    {
        if (element == null || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var localPoint = element.PointFromScreen(screenPoint);
            return localPoint.X >= 0 &&
                localPoint.Y >= 0 &&
                localPoint.X <= element.ActualWidth &&
                localPoint.Y <= element.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsMouseButtonDownMessage(IntPtr message)
    {
        var value = message.ToInt32();
        return value == WmLButtonDown ||
            value == WmRButtonDown ||
            value == WmMButtonDown ||
            value == WmXButtonDown;
    }

    private static bool IsWindowFromCurrentProcess(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var processId);
        return processId == Environment.ProcessId;
    }

    // ── This window's OWN queue identity. A queue is (monitor, edge); each docked capsule
    // resolves its geometry against its own queue, not a single global anchor. This is what
    // lets one capsule sit on the left edge of monitor A while another sits on the right of B.
    private EdgeCapsuleEdge MyDeepCapsuleEdge =>
        _paper.CapsuleSide == DeepCapsuleSides.Left ? EdgeCapsuleEdge.Left : EdgeCapsuleEdge.Right;

    private bool MyDeepCapsuleIsLeftEdge => MyDeepCapsuleEdge == EdgeCapsuleEdge.Left;

    private Rect DeepCapsuleWorkArea()
    {
        return EdgeCapsuleLayout.WorkAreaForQueue(_paper.CapsuleMonitorDeviceName);
    }

    private MonitorGeometry DeepCapsuleMonitorGeometry()
    {
        if (_edgeCapsuleHost?.TryGetMonitorGeometry(
                _paper.CapsuleMonitorDeviceName,
                out var geometry) == true ||
            _edgeCapsuleHost == null && WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                _paper.CapsuleMonitorDeviceName,
                out geometry))
        {
            return geometry;
        }

        var dpi = _edgeCapsuleHost?.Dpi ?? VisualTreeHelper.GetDpi(this);
        var area = SystemParameters.WorkArea;
        return new MonitorGeometry(
            "",
            new DeviceScreenRect(
                (int)Math.Round(area.Left * dpi.DpiScaleX),
                (int)Math.Round(area.Top * dpi.DpiScaleY),
                (int)Math.Round(area.Right * dpi.DpiScaleX),
                (int)Math.Round(area.Bottom * dpi.DpiScaleY)),
            Math.Max(1, dpi.DpiScaleX),
            Math.Max(1, dpi.DpiScaleY));
    }

    private DpiScale DeepCapsuleSlotDpi()
    {
        var geometry = DeepCapsuleMonitorGeometry();
        return new DpiScale(geometry.DpiScaleX, geometry.DpiScaleY);
    }

    private double MyTopForIndex(int index, int slotCount)
    {
        return EdgeCapsuleLayout.TopForIndex(
            index,
            _controller.DeepCapsuleStartTopMarginFor(_paper),
            EdgeCapsuleLayout.LocalWorkAreaForQueue(_paper.CapsuleMonitorDeviceName),
            slotCount);
    }

    private double DeepCapsuleFloatingDragWidth()
    {
        // The detached drag surface is a complete pill with a transparent margin on both sides.
        // It is never reused as the docked edge tag.
        return Math.Max(
            PaperLayoutDefaults.CapsuleWidth,
            ExpandedDeepCapsuleVisibleWidth() + WindowChromeMargin);
    }

    private EdgeCapsuleDragWindow CreateDeepCapsuleFloatingDragHost(DeviceScreenPoint pointer)
    {
        CloseDeepCapsuleFloatingDragHost(restoreDockedRoot: false);

        var width = DeepCapsuleFloatingDragWidth();
        var height = PaperLayoutDefaults.CapsuleHeight;
        var bodyHeight = Math.Max(1, height - WindowChromeInset);
        var bodyRadius = bodyHeight / 2.0;
        var outlineMargin = WindowChromeMargin - DeepCapsuleSlotOutlineThickness + DeepCapsuleSlotOutlineOverlap;
        var host = new EdgeCapsuleDragWindow(new EdgeCapsuleDragWindowOptions
        {
            Width = width,
            Height = height,
            BodyHeight = bodyHeight,
            BodyRadius = bodyRadius,
            WindowChromeMargin = WindowChromeMargin,
            OutlineMargin = outlineMargin,
            OutlineThickness = DeepCapsuleSlotOutlineThickness,
            OutlineOverlap = DeepCapsuleSlotOutlineOverlap,
            CloseWidth = CapsuleCloseWidth,
            LeftPadding = CapsuleLeftPadding,
            IconGap = CapsuleIconGap,
            RightPadding = CapsuleRightPadding,
            Icon = CapsuleIconText(),
            Label = _controller.PaperCapsuleTitle(_paper),
            IconFontSize = CapsuleIconFontSizeForCurrentPaper(),
            LabelFontSize = CapsuleLabelFontSize,
            UiFontFamily = AppTypography.UiFontFamily,
            SymbolFontFamily = AppTypography.SymbolFontFamily,
            Language = AppTypography.Language,
            PaperBrush = PaperBrush,
            PaperBorderBrush = PaperBorderBrush,
            IconBrush = BrightWeakTextBrush,
            LabelBrush = WeakTextBrush,
            OutlineBrush = Theme.CapsuleFocusBorderBrush,
            ShowOutline = IsDeepCapsuleSlotActive,
            Topmost = _edgeCapsuleHost?.IsTopmost == true
        });
        host.UnexpectedlyClosed += OnDeepCapsuleFloatingDragHostUnexpectedlyClosed;

        _deepCapsuleFloatingDragHost = host;
        try
        {
            host.ShowWithEntrance(
                pointer,
                _controller.State.EnableAnimations,
                DeepCapsuleCrossQueueDragScaleFrom,
                DeepCapsuleCrossQueueDragMorphMilliseconds);
            return host;
        }
        catch
        {
            _deepCapsuleFloatingDragHost = null;
            host.UnexpectedlyClosed -= OnDeepCapsuleFloatingDragHostUnexpectedlyClosed;
            try
            {
                host.CloseFromOwner();
            }
            catch
            {
                // Preserve the original Show failure; the interaction caller owns rollback.
            }
            throw;
        }
    }

    private void MoveDeepCapsuleFloatingDragHost(DeviceScreenPoint pointer)
    {
        if (_deepCapsuleFloatingDragHost == null)
        {
            return;
        }

        _deepCapsuleFloatingDragHost.MoveCenteredAt(pointer);
    }

    private void SetDeepCapsuleDockedRootSuppressedForFloatingDrag(bool suppressed)
    {
        // Suppression is derived from FloatingTransfer/FloatingReordering in the desired model.
        // The bool remains only as an assertion-friendly call-site description.
        Debug.Assert(
            !suppressed || IsDeepCapsuleFloatingTransfer || IsDeepCapsuleFloatingReordering,
            "Only a floating gesture may suppress the docked edge surface.");
        RequestEdgeCapsulePresentation(
            animate: false,
            EdgeCapsuleTransitionReason.FloatingTransfer,
            immediate: true);
    }

    private void ReconcileDeepCapsuleHostPresentation()
    {
        RequestEdgeCapsulePresentation(
            animate: false,
            EdgeCapsuleTransitionReason.State,
            immediate: true);
    }

    private void CloseDeepCapsuleFloatingDragHost(bool restoreDockedRoot)
    {
        var host = _deepCapsuleFloatingDragHost;
        _deepCapsuleFloatingDragHost = null;
        if (host != null)
        {
            host.UnexpectedlyClosed -= OnDeepCapsuleFloatingDragHostUnexpectedlyClosed;
            host.CloseFromOwner();
        }

        if (restoreDockedRoot)
        {
            SetDeepCapsuleDockedRootSuppressedForFloatingDrag(false);
        }
    }

    private void OnDeepCapsuleFloatingDragHostUnexpectedlyClosed(object? sender, EventArgs e)
    {
        if (sender is not EdgeCapsuleDragWindow host ||
            !ReferenceEquals(host, _deepCapsuleFloatingDragHost))
        {
            return;
        }

        host.UnexpectedlyClosed -= OnDeepCapsuleFloatingDragHostUnexpectedlyClosed;
        _deepCapsuleFloatingDragHost = null;
        SetDeepCapsuleDockedRootSuppressedForFloatingDrag(false);
        if (!IsDeepCapsuleFloatingReordering)
        {
            return;
        }

        CancelDeepCapsuleReorderDrag(restoreLayout: true);
    }

    private double DeepCapsuleTopForIndex(int index)
    {
        return MyTopForIndex(index, _edgeCapsule.Placement.SlotCount);
    }

    private double DeepCapsuleVisibleWidth()
    {
        return DeepCapsuleVisibleWidth(DeepCapsuleSlotDpi().PixelsPerDip);
    }

    private double DeepCapsuleVisibleWidth(double pixelsPerDip)
    {
        // A resting edge tag owns exactly the pixels it renders: one interior shadow margin plus
        // icon/title content and its padding. There is no hidden full-width pill behind it.
        var bodyWidth = Math.Ceiling(
            CapsuleLeftPadding +
            MeasureCapsuleIconWidth(pixelsPerDip) +
            CapsuleIconGap +
            MeasureCapsuleTitleWidth(
                limitForDeepCapsule: true,
                pixelsPerDip: pixelsPerDip) +
            CapsuleRightPadding);
        return Math.Max(34, bodyWidth + WindowChromeMargin);
    }

    private double ExpandedDeepCapsuleVisibleWidth()
    {
        return DeepCapsuleVisibleWidth() + CapsuleCloseWidth;
    }

    // Slide this capsule up to the master's slot and fade it out. The window stays shown
    // (so it keeps counting as a deep-capsule member) but, being a per-pixel transparent
    // window at Opacity 0, it is fully click-through and never blocks the master pill.
    internal void RetractIntoMaster(EdgeCapsulePlacement placement, bool animate)
    {
        if (!_controller.State.UseCapsuleMode ||
            !_controller.State.UseDeepCapsuleMode ||
            !_paper.IsVisible ||
            !_controller.CanPaperDisplayAsCapsule(_paper))
        {
            ClearDeepCapsulePlacement();
            return;
        }

        SetEdgeCapsuleVisualState(EdgeCapsuleVisualState.Resting);
        SetEdgeCapsulePlacement(placement);
        SetEdgeCapsuleSlotState(_paper.IsCollapsed
            ? EdgeCapsuleSlotState.RetractedCollapsed
            : EdgeCapsuleSlotState.RetractedExpanded);
        UpdateDeepCapsuleSlotHostTheme();
        RefreshEffectiveTopmost();

        RequestEdgeCapsulePresentation(
            animate,
            EdgeCapsuleTransitionReason.Retraction,
            EdgeCapsuleLayout.SlotRetractMoveMilliseconds);
        if (_paper.IsCollapsed)
        {
            HideMainWindowForDeepCapsuleRest();
        }
    }

    private void SetDeepCapsuleHover(bool hovering)
    {
        if (IsDeepCapsuleReordering || !HasDeepCapsuleSlotPlacement || !_paper.IsCollapsed || !_controller.State.UseDeepCapsuleMode)
        {
            return;
        }

        if (IsDeepCapsuleSlotActive)
        {
            return;
        }

        if (_edgeCapsule.ContextMenuOpen)
        {
            hovering = true;
        }
        else if (WindowNative.TryGetCursorScreenPosition(out var pointer) &&
            _edgeCapsuleHost != null)
        {
            // Enter/leave raised while the right-edge HWND is resizing is only a hint. Test the
            // actual pointer against the current rendered shell so a false leave cannot lock the
            // desired state, while a real movement can retarget an in-flight transition.
            hovering = _edgeCapsuleHost.ContainsScreenPoint(pointer);
        }

        var targetState = hovering
            ? EdgeCapsuleVisualState.Hovered
            : EdgeCapsuleVisualState.Resting;
        if (EdgeCapsuleVisual == targetState)
        {
            return;
        }

        SetEdgeCapsuleVisualState(targetState);
        RequestEdgeCapsulePresentation(
            animate: _controller.State.EnableAnimations,
            EdgeCapsuleTransitionReason.Pointer);
    }

    private void CompleteDeepCapsuleFloatingDragDrop()
    {
        if (_deepCapsuleFloatingDragHost == null)
        {
            SetDeepCapsuleDockedRootSuppressedForFloatingDrag(false);
            return;
        }

        if (!HasDeepCapsuleSlotPlacement || _edgeCapsuleHost == null)
        {
            CloseDeepCapsuleFloatingDragHost(restoreDockedRoot: true);
            return;
        }

        // The controller has already resolved the destination queue. Snap the permanently docked
        // host while hidden, then destroy the floating HWND before revealing the docked tree.
        // Keeping this hand-off synchronous avoids a third, mixed-DPI transition state.
        RequestEdgeCapsulePresentation(
            animate: false,
            EdgeCapsuleTransitionReason.FloatingTransfer,
            immediate: true);
        CloseDeepCapsuleFloatingDragHost(restoreDockedRoot: true);
        _edgeCapsule.RequestPresentation(EdgeCapsuleMotion.Snap(
            EdgeCapsuleTransitionReason.FloatingTransfer));
        InvalidateEdgeCapsule(EdgeCapsuleDirty.Presentation);
    }

    internal void ApplyDeepCapsulePlacement(EdgeCapsulePlacement placement, bool animate = false)
    {
        if (!_paper.IsCollapsed || !_paper.IsVisible || !_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            ClearDeepCapsulePlacement();
            return;
        }

        SetEdgeCapsuleSlotState(EdgeCapsuleSlotState.CollapsedDocked);
        // Semantic state changes immediately. The geometry animator already snapshots the current
        // width, so it can retract from Active without keeping a false Active target alive.
        SetEdgeCapsuleVisualState(EdgeCapsuleVisualState.Resting);
        SetEdgeCapsulePlacement(placement);
        RefreshCapsuleLabel();
        RequestEdgeCapsulePresentation(
            animate,
            EdgeCapsuleTransitionReason.Placement,
            EdgeCapsuleLayout.SlotMoveMilliseconds);
        if (!IsPaperFormTransitioning)
        {
            HideMainWindowForDeepCapsuleRest();
        }
        RefreshEffectiveTopmost();
    }

    internal void PreviewDeepCapsulePlacement(EdgeCapsulePlacement placement)
    {
        if (!HasDeepCapsuleSlotPlacement ||
            _edgeCapsuleHost?.IsVisible != true ||
            IsDeepCapsuleReordering ||
            IsDeepCapsuleRetractedIntoMaster)
        {
            return;
        }

        SetEdgeCapsulePlacement(placement);
        RequestEdgeCapsulePresentation(
            animate: true,
            EdgeCapsuleTransitionReason.Placement,
            EdgeCapsuleLayout.SlotMoveMilliseconds);
    }

    internal void ApplyExpandedDeepCapsuleSlotPlacement(EdgeCapsulePlacement placement, bool animate = false)
    {
        var shouldReserveWhileExpanded = _controller.State.ShowDeepCapsuleWhileExpanded &&
            _controller.CanPaperDisplayAsCapsule(_paper);
        if (_paper.IsCollapsed ||
            !shouldReserveWhileExpanded ||
            !_controller.State.UseCapsuleMode ||
            !_controller.State.UseDeepCapsuleMode ||
            !_paper.IsVisible)
        {
            ClearExpandedDeepCapsuleSlotPlacement();
            return;
        }

        var shouldSaveExpandedGeometry = ShouldSaveDeepCapsuleExpandedGeometry;
        SetEdgeCapsuleOpenOrigin(EdgeCapsuleOpenOrigin.EdgeSlot);
        SetEdgeCapsuleSlotState(EdgeCapsuleSlotState.ExpandedReserved);
        SetEdgeCapsuleVisualState(EdgeCapsuleVisualState.Active);
        SetEdgeCapsulePlacement(placement);
        RefreshCapsuleLabel();
        UpdateDeepCapsuleSlotHostTheme();

        RefreshDeepCapsuleSlotLabel();

        var firstShow = _edgeCapsuleHost?.IsVisible != true;
        RequestEdgeCapsulePresentation(
            animate: !firstShow && animate,
            EdgeCapsuleTransitionReason.Placement,
            EdgeCapsuleLayout.SlotMoveMilliseconds,
            immediate: firstShow);
        RefreshEffectiveTopmost();
        UpdateToolTipSetting();
        if (!IsPaperFormTransitioning && shouldSaveExpandedGeometry)
        {
            _controller.UpdateGeometry(_paper, this);
        }
    }

    public void ClearExpandedDeepCapsuleSlotPlacement(bool animate = false)
    {
        if (EdgeCapsuleSlot == EdgeCapsuleSlotState.ExpandedReserved)
        {
            SetEdgeCapsuleSlotState(_paper.IsCollapsed ? EdgeCapsuleSlotState.CollapsedDocked : EdgeCapsuleSlotState.None);
        }
        SetEdgeCapsuleVisualState(EdgeCapsuleVisualState.Resting);
        if (IsDeepCapsuleSlotRetracting)
        {
            SetEdgeCapsuleSlotState(_paper.IsCollapsed ? EdgeCapsuleSlotState.CollapsedDocked : EdgeCapsuleSlotState.None);
        }
        if (!_paper.IsCollapsed && EdgeCapsuleSlot == EdgeCapsuleSlotState.None)
        {
            SetEdgeCapsulePlacement(EdgeCapsulePlacement.None);
        }
        UpdateDeepCapsuleSlotHostTheme();
        if (_paper.IsCollapsed && HasDeepCapsuleSlotPlacement)
        {
            RequestEdgeCapsulePresentation(
                animate,
                EdgeCapsuleTransitionReason.State,
                EdgeCapsuleLayout.SlotMoveMilliseconds);
        }
    }

    private void HideExpandedDeepCapsuleSlotHost(bool animate)
    {
        animate = animate && _controller.State.EnableAnimations;
        if (animate &&
            _edgeCapsuleHost?.IsVisible == true &&
            HasDeepCapsuleSlotPlacement &&
            _edgeCapsule.Placement.IsPlaced &&
            !IsDeepCapsuleSlotRetracting)
        {
            BeginEdgeCapsuleSlotRetraction();
            SetEdgeCapsuleVisualState(EdgeCapsuleVisualState.Resting);
            RequestEdgeCapsulePresentation(
                animate: true,
                EdgeCapsuleTransitionReason.Retraction,
                EdgeCapsuleLayout.SlotRetractMoveMilliseconds);
            return;
        }

        if (HasDeepCapsuleSlotPlacement)
        {
            SetEdgeCapsuleSlotState(EdgeCapsuleSlotState.None);
        }
        SetEdgeCapsuleVisualState(EdgeCapsuleVisualState.Resting);
        SetEdgeCapsulePlacement(EdgeCapsulePlacement.None);
        RequestEdgeCapsulePresentation(
            animate: false,
            EdgeCapsuleTransitionReason.Retraction,
            immediate: true);
    }

    private void RetractAndHideDeepCapsuleSlotHost(bool animate)
    {
        HideExpandedDeepCapsuleSlotHost(animate);
    }

    public void ClearDeepCapsulePlacement(bool restoreCollapsedPosition = true, bool animate = false)
    {
        CancelDeepCapsuleReorderDrag();
        animate = animate && _controller.State.EnableAnimations;
        CloseDeepCapsuleFloatingDragHost(restoreDockedRoot: true);

        var shouldRetractBeforeHide = animate &&
            _edgeCapsuleHost?.IsVisible == true &&
            HasDeepCapsuleSlotPlacement &&
            !IsDeepCapsuleRetractedIntoMaster;

        if (shouldRetractBeforeHide)
        {
            RetractAndHideDeepCapsuleSlotHost(animate: true);
        }
        else
        {
            SetEdgeCapsuleVisualState(EdgeCapsuleVisualState.Resting);
            SetEdgeCapsulePlacement(EdgeCapsulePlacement.None);
            UpdateCapsuleClosePlacement();
            HideExpandedDeepCapsuleSlotHost(animate);
        }

        // A capsule may have been faded out while retracted behind the master; never leave
        // a live (expanded or free-floating) window invisible.
        if (Math.Abs(Opacity - 1.0) > 0.001)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1.0;
        }

        if (!_paper.IsVisible || !_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            SetEdgeCapsuleOpenOrigin(EdgeCapsuleOpenOrigin.Normal);
        }
    }

    public void ClearDeepCapsuleSlotReservation(bool animate = false)
    {
        if (EdgeCapsuleSlot == EdgeCapsuleSlotState.ExpandedReserved)
        {
            SetEdgeCapsuleSlotState(_paper.IsCollapsed ? EdgeCapsuleSlotState.CollapsedDocked : EdgeCapsuleSlotState.None);
        }
        ClearExpandedDeepCapsuleSlotPlacement(animate);
    }

    // Fully remove this window from the deep-capsule stack: tears down the docked/collapsed slot
    // AND any expanded reservation, and hides the slot-host window. This is the single correct
    // call for "this paper is leaving the stack" (hidden, mode off, surface restored). Callers
    // must NOT also call ClearDeepCapsuleSlotReservation afterward — ClearDeepCapsulePlacement
    // already resets the reservation, so the pair was a redundant no-op and an easy mis-pairing.
    public void DetachFromDeepCapsuleStack(bool animate = false)
    {
        ClearDeepCapsulePlacement(animate: animate);
    }

    public void UpdateDeepCapsuleMode()
    {
        if (!_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            if (EdgeCapsuleSlot == EdgeCapsuleSlotState.ExpandedReserved)
            {
                SetEdgeCapsuleSlotState(EdgeCapsuleSlotState.None);
            }
            ClearDeepCapsulePlacement();
        }
        else if (!_paper.IsCollapsed)
        {
            ClearDeepCapsulePlacement(restoreCollapsedPosition: false);
        }
        else
        {
            RequestEdgeCapsulePresentation(
                animate: false,
                EdgeCapsuleTransitionReason.State);
        }

        RefreshEffectiveTopmost();
    }

    public void UpdateDeepCapsuleExpandedSlotMode()
    {
        if (_paper.IsCollapsed)
        {
            return;
        }

        if (!_paper.IsVisible || !_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            if (EdgeCapsuleSlot == EdgeCapsuleSlotState.ExpandedReserved)
            {
                SetEdgeCapsuleSlotState(EdgeCapsuleSlotState.None);
            }
            return;
        }

        if (_controller.State.ShowDeepCapsuleWhileExpanded && _controller.CanPaperDisplayAsCapsule(_paper))
        {
            SetEdgeCapsuleSlotState(EdgeCapsuleSlotState.ExpandedReserved);
            SetEdgeCapsuleVisualState(EdgeCapsuleVisualState.Active);
            RefreshCapsuleLabel();
            UpdateDeepCapsuleSlotHostTheme();
            RequestEdgeCapsulePresentation(
                animate: _controller.State.EnableAnimations,
                EdgeCapsuleTransitionReason.State);
            return;
        }

        if (!_controller.State.ShowDeepCapsuleWhileExpanded && HoldsDeepCapsuleSlotWhileExpanded)
        {
            SetEdgeCapsuleSlotState(EdgeCapsuleSlotState.None);
            SetEdgeCapsuleVisualState(EdgeCapsuleVisualState.Resting);
            ClearDeepCapsulePlacement(restoreCollapsedPosition: false, animate: _controller.State.EnableAnimations);
        }
    }

    private void StartDeepCapsuleReorderDrag(DeviceScreenPoint currentScreenPos)
    {
        if (!CanReorderDeepCapsuleSlot() ||
            _edgeCapsuleHost == null ||
            !_edgeCapsule.AppliedPresentation.Visible ||
            _edgeCapsule.AppliedPresentation.Bounds.IsEmpty)
        {
            return;
        }

        if (!TryGetEdgeCapsuleDragSession(out var session))
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }
        SetEdgeCapsuleGestureState(EdgeCapsuleGestureState.DockedReordering);
        SetEdgeCapsuleVisualState(EdgeCapsuleVisualState.Hovered);
        session.LastScreenPosition = currentScreenPos;
        session.StartMonitorDeviceName = WindowWorkAreaHelper
            .MonitorAtDeviceScreenPoint(session.PointerDownScreenPosition)?.DeviceName ?? "";
        session.PreviewIndex = -1;
        var appliedBounds = _edgeCapsule.AppliedPresentation.Bounds;
        session.DockedPointerOffsetY = currentScreenPos.Y - appliedBounds.Top;

        CloseDeepCapsuleFloatingDragHost(restoreDockedRoot: true);
        SetEdgeCapsuleDockedDragTop(
            DeepCapsuleMonitorGeometry().DeviceYToLocalDip(appliedBounds.Top));
        RequestEdgeCapsulePresentation(
            animate: false,
            EdgeCapsuleTransitionReason.Drag,
            immediate: true);
        _edgeCapsuleHost.BringToFrontNoActivate();

        Mouse.OverrideCursor = Cursors.SizeAll;
        UpdateDeepCapsuleReorderDrag(currentScreenPos);
    }

    private void UpdateDeepCapsuleReorderDrag(DeviceScreenPoint currentScreenPos)
    {
        if (!IsDeepCapsuleReordering)
        {
            return;
        }

        if (!TryGetEdgeCapsuleDragSession(out var session))
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }
        session.LastScreenPosition = currentScreenPos;

        if (_edgeCapsuleHost == null)
        {
            return;
        }

        if (IsDeepCapsuleDockedReordering &&
            ShouldUnlockDeepCapsuleCrossQueueDrag(currentScreenPos))
        {
            BeginDeepCapsuleFloatingReorder(currentScreenPos);
            return;
        }

        if (IsDeepCapsuleFloatingReordering)
        {
            MoveDeepCapsuleFloatingDragHost(currentScreenPos);
            return;
        }

        var geometry = DeepCapsuleMonitorGeometry();
        var targetDeviceTop = currentScreenPos.Y - session.DockedPointerOffsetY;
        SetEdgeCapsuleDockedDragTop(geometry.DeviceYToLocalDip(targetDeviceTop));
        RequestEdgeCapsulePresentation(
            animate: false,
            EdgeCapsuleTransitionReason.Drag,
            immediate: true);
        PreviewDeepCapsuleReorderForCurrentPosition();
    }

    private void PreviewDeepCapsuleReorderForCurrentPosition()
    {
        if (!TryGetEdgeCapsuleDragSession(out var session))
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }
        var dropIndex = DeepCapsuleDropIndexForCurrentPosition();
        if (dropIndex == session.PreviewIndex)
        {
            return;
        }

        session.PreviewIndex = dropIndex;
        _controller.PreviewDeepCapsuleReorder(_paper, dropIndex);
    }

    private void BeginDeepCapsuleFloatingReorder(DeviceScreenPoint currentScreenPos)
    {
        if (_edgeCapsuleHost == null || !IsDeepCapsuleDockedReordering)
        {
            return;
        }

        if (!TryGetEdgeCapsuleDragSession(out var session))
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }
        session.LastScreenPosition = currentScreenPos;
        var edgeHost = _edgeCapsuleHost;
        SetEdgeCapsuleGestureState(EdgeCapsuleGestureState.FloatingTransfer);
        try
        {
            var floatingHost = CreateDeepCapsuleFloatingDragHost(currentScreenPos);
            SetDeepCapsuleDockedRootSuppressedForFloatingDrag(true);
            SetEdgeCapsuleGestureState(EdgeCapsuleGestureState.FloatingReordering);
            WindowNative.BringToFrontNoActivate(floatingHost);
            RefreshDeepCapsuleSlotTopmost();
            Mouse.OverrideCursor = Cursors.SizeAll;
            // Show() of the floating top-level window steals capture from the docked content.
            // Re-capture so subsequent move/up keep driving the floating host.
            if (Mouse.LeftButton == MouseButtonState.Pressed &&
                !edgeHost.IsContentPointerCaptured)
            {
                edgeHost.CaptureContentPointer();
            }
        }
        catch
        {
            CloseDeepCapsuleFloatingDragHost(restoreDockedRoot: true);
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            _controller.ArrangeDeepCapsules(animate: true);
        }
        finally
        {
            if (IsDeepCapsuleFloatingReordering &&
                Mouse.LeftButton == MouseButtonState.Pressed &&
                !edgeHost.IsContentPointerCaptured)
            {
                edgeHost.CaptureContentPointer();
            }
        }
    }

    private bool ShouldUnlockDeepCapsuleCrossQueueDrag(DeviceScreenPoint currentScreenPos)
    {
        if (!TryGetEdgeCapsuleDragSession(out var session))
        {
            return false;
        }
        var scaleX = WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
            session.PointerDownScreenPosition,
            out var startGeometry)
                ? startGeometry.DpiScaleX
                : 1.0;
        var horizontalDelta = Math.Abs(
            currentScreenPos.X - session.PointerDownScreenPosition.X);
        if (horizontalDelta >= DeepCapsuleCrossQueueDragUnlockDistance * scaleX)
        {
            return true;
        }

        return HasDeepCapsuleDragEnteredAnotherMonitor(currentScreenPos);
    }
    private bool HasDeepCapsuleDragEnteredAnotherMonitor(DeviceScreenPoint currentScreenPos)
    {
        if (!TryGetEdgeCapsuleDragSession(out var session))
        {
            return false;
        }
        if (string.IsNullOrEmpty(session.StartMonitorDeviceName))
        {
            return false;
        }

        var currentMonitor = WindowWorkAreaHelper.MonitorAtDeviceScreenPoint(currentScreenPos);
        return currentMonitor.HasValue &&
            !string.IsNullOrEmpty(currentMonitor.Value.DeviceName) &&
            !string.Equals(
                currentMonitor.Value.DeviceName,
                session.StartMonitorDeviceName,
                StringComparison.Ordinal);
    }

    private void EndDeepCapsuleReorderDrag(bool commit)
    {
        if (!IsDeepCapsuleReordering)
        {
            return;
        }

        if (!TryGetEdgeCapsuleDragSession(out var session))
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }
        var wasFloatingDrag = IsDeepCapsuleFloatingReordering;
        try
        {
            Mouse.OverrideCursor = null;
            SetEdgeCapsuleVisualState(
                _edgeCapsuleHost?.IsSurfaceMouseOver == true
                    ? EdgeCapsuleVisualState.Hovered
                    : EdgeCapsuleVisualState.Resting);

            if (commit)
            {
                if (!wasFloatingDrag)
                {
                    _controller.ReorderDeepCapsule(_paper, DeepCapsuleDropIndexForCurrentPosition());
                    return;
                }

                // Resolve the (monitor, edge) queue under the drop point. If it differs from this
                // paper's current queue, reassign it (cross-edge / cross-monitor move). Otherwise it's
                // a plain vertical reorder within the same queue.
                var targetGeometry = WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
                    session.LastScreenPosition,
                    out var resolvedGeometry)
                        ? resolvedGeometry
                        : DeepCapsuleMonitorGeometry();
                var targetMonitor = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(
                    targetGeometry.DeviceName);

                // Choose the nearer physical wall of the target monitor.
                var targetSide = session.LastScreenPosition.X <
                    targetGeometry.WorkArea.Left + targetGeometry.WorkArea.Width / 2.0
                    ? DeepCapsuleSides.Left
                    : DeepCapsuleSides.Right;

                var queueChanged = targetSide != _paper.CapsuleSide ||
                    !string.Equals(targetMonitor, _paper.CapsuleMonitorDeviceName, StringComparison.Ordinal);

                if (queueChanged)
                {
                    _controller.MoveCapsuleToQueue(
                        _paper,
                        targetMonitor,
                        targetSide,
                        session.LastScreenPosition);
                }
                else
                {
                    _controller.ReorderDeepCapsule(_paper, DeepCapsuleDropIndexForCurrentPosition());
                }
                return;
            }

            _controller.ArrangeDeepCapsules(animate: true);
        }
        finally
        {
            if (wasFloatingDrag || _deepCapsuleFloatingDragHost != null)
            {
                CompleteDeepCapsuleFloatingDragDrop();
            }
            else
            {
                SetDeepCapsuleDockedRootSuppressedForFloatingDrag(false);
            }
            // Keep the gesture state alive through the controller operation. Queue arrange calls
            // are then deferred instead of racing the drag session, and a floating docked host
            // remains suppressed until its replacement HWND has been destroyed.
            FinishEdgeCapsulePointerInteraction();
            _controller.CompleteDeepCapsuleReorderDrag();
            _controller.RefreshFloatingSurfaceZOrder();
            RequestEdgeCapsulePresentation(
                animate: false,
                EdgeCapsuleTransitionReason.FloatingTransfer);
            WakeEdgeCapsuleReconcile();
        }
    }

    private void CancelDeepCapsuleReorderDrag(bool restoreLayout = false)
    {
        var wasReordering = IsDeepCapsuleReordering;
        if (!wasReordering &&
            !IsDeepCapsuleSlotPendingClick &&
            _deepCapsuleFloatingDragHost == null)
        {
            return;
        }

        try
        {
            CloseDeepCapsuleFloatingDragHost(restoreDockedRoot: true);
            Mouse.OverrideCursor = null;
            if (wasReordering && restoreLayout &&
                _windowLifecycle == PaperWindowLifecycleState.Alive)
            {
                _controller.ArrangeDeepCapsules(animate: true);
            }
        }
        finally
        {
            FinishEdgeCapsulePointerInteraction();
            _edgeCapsuleHost?.ReleaseContentPointer();
            if (wasReordering)
            {
                _controller.CompleteDeepCapsuleReorderDrag();
                _controller.RefreshFloatingSurfaceZOrder();
            }

            RequestEdgeCapsulePresentation(
                animate: false,
                EdgeCapsuleTransitionReason.FloatingTransfer);
            WakeEdgeCapsuleReconcile();
        }
    }

    private bool CanReorderDeepCapsuleSlot()
    {
        return HasDeepCapsuleSlotPlacement &&
            _edgeCapsuleHost?.IsVisible == true &&
            (_paper.IsCollapsed || (_controller.State.ShowDeepCapsuleWhileExpanded && IsDeepCapsuleSlotActive));
    }

    private int DeepCapsuleDropIndexForCurrentPosition()
    {
        var count = _controller.VisibleDeepCapsuleCountForQueue(_paper);
        if (count <= 1)
        {
            return 0;
        }

        var dragBounds = _deepCapsuleFloatingDragHost != null &&
            WindowNative.TryGetWindowDeviceBounds(_deepCapsuleFloatingDragHost, out var floatingBounds)
                ? floatingBounds
                : _edgeCapsule.AppliedPresentation.Bounds;
        if (dragBounds.IsEmpty)
        {
            return Math.Clamp(_edgeCapsule.Placement.Index, 0, count - 1);
        }

        var geometry = DeepCapsuleMonitorGeometry();
        var centerY = geometry.DeviceYToLocalDip(dragBounds.Top + dragBounds.Height / 2.0);
        // Real capsules start after slot 0 when the master capsule occupies that slot.
        var firstCenterY = DeepCapsuleTopForIndex(_edgeCapsule.Placement.VisualOffset) +
            (PaperLayoutDefaults.CapsuleHeight / 2);
        var slotHeight = PaperLayoutDefaults.CapsuleHeight + DeepCapsuleGap;
        var originalIndex = Math.Clamp(_edgeCapsule.Placement.Index, 0, count - 1);
        var rawIndex = (centerY - firstCenterY) / slotHeight;
        var index = rawIndex >= originalIndex
            ? (int)Math.Floor(rawIndex)
            : (int)Math.Ceiling(rawIndex);
        return Math.Clamp(index, 0, count - 1);
    }

    private void AlignExpandedToDockedEdge(double targetWidth, double targetHeight, double requiredEdgeInset = 0)
    {
        var area = DeepCapsuleWorkArea();
        var width = Math.Max(targetWidth, PaperLayoutDefaults.MinWidth);
        var height = Math.Max(targetHeight, PaperLayoutDefaults.MinHeight);
        // "rightInset" is really the gap between the expanded window and the docked edge; it
        // reserves room for the resting capsule strip. On the left edge it pushes the window
        // rightward from area.Left; on the right edge it pulls it leftward from area.Right.
        var edgeInset = Math.Min(
            Math.Max(
                Math.Max(DeepCapsuleExpandedEdgeInset, requiredEdgeInset),
                _controller.VisibleDeepCapsuleRestingWidthForQueue(_paper) + DeepCapsuleGap),
            Math.Max(0, area.Width - width));
        var targetTop = Math.Clamp(Top, area.Top + DeepCapsuleTopMargin, Math.Max(area.Top + DeepCapsuleTopMargin, area.Bottom - height - DeepCapsuleTopMargin));

        Left = RoundToDevicePixelX(MyDeepCapsuleIsLeftEdge
            ? area.Left + edgeInset
            : area.Right - width - edgeInset);
        Top = RoundToDevicePixelY(targetTop);
    }

    internal void ReconcileExpandedDeepCapsuleInset()
    {
        if (_paper.IsCollapsed ||
            !HoldsDeepCapsuleSlotWhileExpanded ||
            !_paper.IsVisible ||
            !IsVisible)
        {
            return;
        }

        var width = Math.Max(ActualWidth > 0 ? ActualWidth : Width, PaperLayoutDefaults.MinWidth);
        var height = Math.Max(ActualHeight > 0 ? ActualHeight : Height, PaperLayoutDefaults.MinHeight);
        MoveWindowWithoutGeometrySave(() => AlignExpandedToDockedEdge(
            width,
            height,
            ExpandedDeepCapsuleVisibleWidth() + DeepCapsuleGap));
        _controller.UpdateGeometry(_paper, this);
    }

    private void RegisterNameSafe(string name, object scopedElement)
    {
        try
        {
            UnregisterName(name);
        }
        catch
        {
            // Name may not exist yet.
        }

        try
        {
            RegisterName(name, scopedElement);
        }
        catch
        {
            // Duplicate names are non-fatal for this small UI.
        }
    }

    private static bool IsDescendantOf(DependencyObject? current, DependencyObject target)
    {
        while (current != null)
        {
            if (ReferenceEquals(current, target))
            {
                return true;
            }
            current = GetSafeParent(current);
        }
        return false;
    }

    private static bool IsScrollBarInteractionSource(DependencyObject? current, DependencyObject scope)
    {
        while (current != null)
        {
            if (current is ScrollBar or Thumb or Track or RepeatButton)
            {
                return true;
            }

            if (ReferenceEquals(current, scope))
            {
                return false;
            }

            current = GetSafeParent(current);
        }

        return false;
    }

}
