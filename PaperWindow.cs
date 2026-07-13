using System.Diagnostics;
using System.Globalization;
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
using WpfPath = System.Windows.Shapes.Path;

namespace PaperTodo;

public sealed partial class PaperWindow : Window
{
    [GeneratedRegex(@"^\s*[-*+]\s+\[(?: |x|X)\]\s*")]
    private static partial Regex TodoCheckboxCleanRegex();

    [GeneratedRegex(@"^\s*[-*+]\s+")]
    private static partial Regex TodoBulletCleanRegex();

    [GeneratedRegex(@"^\s*\d+[\.)、．]\s*")]
    private static partial Regex TodoNumberCleanRegex();

    [GeneratedRegex(@"^\s*[☐☑✓✔]\s*")]
    private static partial Regex TodoGlyphCleanRegex();
    private readonly PaperData _paper;
    private readonly AppController _controller;

    private Grid _windowHost = null!;
    private Border _paperChrome = null!;
    private readonly Grid _containerGrid = new();
    private readonly Grid _shell = new();
    private readonly ScaleTransform _shellScale = new(1.0, 1.0);
    private Canvas? _dragLayer;
    private StackPanel? _todoPanel;
    private Button? _paperIconButton;
    private Button? _newTodoButton;
    private Button? _newNoteButton;
    private Button? _openMarkdownButton;
    private Button? _linkNoteButton;
    private TextBlock? _titleText;
    private TextBox? _titleEditBox;
    private TextBlock? _textZoomIndicator;
    private UIElement? _noteBodyElement;
    private Border? _capsuleLeftArea;
    private Border? _activeDropRow;
    private Border? _dropIndicatorLine;
    private Border? _appendArea;
    private Border? _linkedNoteDropRow;
    private bool _closeForReal;
    // Tracks only the hidden owner that PaperTodo applies for window-switcher hiding.
    private bool _windowSwitcherHiddenOwnerApplied;
    private string? _pendingFocusItemId;
    private readonly Dictionary<string, TodoTextBox> _todoEditors = new();
    private readonly List<Border> _todoRows = new();
    private TodoDragState? _todoDrag;
    private NoteLinkDragState? _noteLinkDrag;
    private MarkdownTextBox? _noteBox;
    private readonly List<WeakReference<ContextMenu>> _themedContextMenus = new();
    private Action? _showNotePreview;
    private readonly List<List<PaperItem>> _undoStack = new();
    private readonly List<List<PaperItem>> _redoStack = new();
    private const int MaxUndoDepth = 100;
    private string? _activeOriginalItemId;
    private string? _activeOriginalText;
    private bool _suppressTodoBackspaceUntilKeyUp;
    private Button? _closeButton;
    private Grid _capsuleShell = null!;
    private EdgeCapsuleHost? _edgeCapsuleHost;
    // Cross-edge dragging owns a separate top-level window. The docked slot host never changes
    // into a floating pill, so its edge columns/corners cannot leak across a drag transition.
    private EdgeCapsuleDragWindow? _deepCapsuleFloatingDragHost;
    private ContextMenu? _deepCapsuleSlotContextMenu;
    private IntPtr _deepCapsuleForegroundHook;
    private IntPtr _deepCapsuleMouseHook;
    private WinEventDelegate? _deepCapsuleForegroundHookProc;
    private LowLevelMouseProc? _deepCapsuleMouseHookProc;
    private Border? _capsuleCloseArea;
    private TextBlock? _capsuleIconText;
    private TextBlock? _capsuleCloseGlyph;
    private TranslateTransform? _capsuleCloseGlyphOffset;
    private TextBlock _capsuleLabelText = null!;
    private bool _suppressGeometrySave;
    private int _collapseTransitionGeneration;
    private double _startTransitionWidth;
    private double _startTransitionHeight;
    private double _targetTransitionWidth;
    private double _targetTransitionHeight;
    private double _transitionBaseWidth;
    private double _transitionBaseHeight;
    private bool _isEditingTitle;
    private bool _suppressTitleEditFromCurrentClick;
    private Rect? _snappedPresentationBoundsForRestore;
    private bool _collapsedFromMaximized;
    private int _themeAnimationGeneration;
    private int _clearDoneGeneration;
    private int _todoRowsGeneration;
    private const double DeepCapsuleExpandedEdgeInset = EdgeCapsuleLayout.ExpandedEdgeInset;
    private const double DeepCapsuleTopMargin = EdgeCapsuleLayout.TopMargin;
    private const double DeepCapsuleStartTopMargin = EdgeCapsuleLayout.StartTopMargin;
    private const double DeepCapsuleGap = EdgeCapsuleLayout.Gap;
    private const double WindowChromeMargin = EdgeCapsuleLayout.WindowChromeMargin;
    private const double WindowChromeInset = WindowChromeMargin * 2;
    private const double TitleBarHeight = PaperLayoutDefaults.TopBarHeight;
    private const int CollapseShellFadeMilliseconds = 70;
    private const int CollapseResizeMilliseconds = 150;
    private const int ExpandAnimationMilliseconds = 220;
    // Expand cross-fade: the capsule pill fades out first, then the paper shell fades in after it.
    private const int ExpandCapsuleFadeOutMilliseconds = 80;
    private const int ExpandShellFadeInMilliseconds = 140;
    private const double ExpandedChromeCornerRadius = RadiusShell;
    private const double CapsuleChromeCornerRadius = EdgeCapsuleLayout.CornerRadius; // 胶囊圆角，自成一套，不纳入圆角阶梯
    private const double CapsuleInnerCornerRadius = EdgeCapsuleLayout.CornerRadius;   // 左区 / 关闭按钮的内圆角，与药丸外圆角同档

    // 胶囊态内部度量。布局（leftStack/标签）与宽度计算（CapsuleShellWidth）共用同一组值，
    // 否则二者不一致会让壳体与内容错位。整体偏紧凑，减少图标/文字四周的死白。
    private const double CapsuleNormalMinWidth = 76;
    private const double CapsuleLeftPadding = 6;
    private const double CapsuleIconGap = 4;
    private const double CapsuleCloseWidth = 28;
    private const double CapsuleNormalCloseWidth = 21;
    private const double CapsuleRightPadding = 6;
    private const double CapsuleIconFontSize = 13;
    private const double CapsuleLabelFontSize = 12;
    private const double TitleFontSize = 12;
    private const double TitleLineHeight = 14;
    private const double TitleBarDragThreshold = 1.0;
    private const double TodoPaperIconFontSize = 14;
    private const double NotePaperIconFontSize = 16;
    private const double CapsuleCloseGlyphNormalOffset = -1;
    private const double DeepCapsuleSlotOutlineThickness = 2;
    private const double DeepCapsuleSlotOutlineOverlap = 1;
    private const double DeepCapsuleReorderDragExtraThreshold = 4;
    private const double DeepCapsuleCrossQueueDragUnlockDistance = 56;
    private const double DeepCapsuleCrossQueueDragScaleFrom = 0.97;
    private const int DeepCapsuleCrossQueueDragMorphMilliseconds = 90;
    // 圆角阶梯：所有元素只从这四档取值，避免散落的随手圆角。
    // 小元素（勾选框）/ 控件（按钮、徽标、行）/ 块（菜单、面板）/ 外壳（纸片、顶栏）。
    private const double RadiusSmall = 4;
    private const double RadiusControl = 8;
    private const double RadiusBlock = 12;
    private const double RadiusShell = 16;
    private static readonly object NoteRenderTraceLock = new();

    public bool IsDeepCapsulePlaced => _paper.IsCollapsed && HasDeepCapsuleSlotPlacement;
    public bool IsDeepCapsuleSlotVisible => _edgeCapsuleHost?.IsVisible == true;
    public bool HasVisibleSurface =>
        (IsVisible && WindowState != WindowState.Minimized) ||
        IsDeepCapsuleSlotVisible;
    public bool HasExpandedPaperSurface =>
        IsVisible &&
        WindowState != WindowState.Minimized &&
        !_paper.IsCollapsed;
    public bool IsCollapseAllRetracted => IsDeepCapsuleRetractedIntoMaster;
    public bool HasExpandedDeepCapsuleSlotReservation => EdgeCapsuleSlot is
        EdgeCapsuleSlotState.ExpandedReserved or
        EdgeCapsuleSlotState.RetractedExpanded or
        EdgeCapsuleSlotState.RetractingExpanded;
    public bool OccupiesDeepCapsuleSlot => _paper.IsVisible && HasDeepCapsuleSlotPlacement;
    public bool IsDeepCapsuleReorderDragInProgress => IsDeepCapsuleReordering;
    public bool SuppressGeometrySave => _suppressGeometrySave;
    // Ordinary collapsed capsules are the main PaperWindow and should still save X/Y.
    // Deep capsules use the slot-host window for docked geometry, so the hidden/parked
    // main window must not overwrite ordinary paper geometry.
    public bool UsesNonPaperGeometry => _paper.IsCollapsed && HasDeepCapsuleSlotPlacement;
    public bool ShouldSaveDeepCapsuleExpandedGeometry => ExpandedFromDeepCapsuleEdge && !_paper.IsCollapsed && _paper.IsVisible;
    public double DesiredCapsuleWindowWidth => CapsuleWindowWidth();
    public double DeepCapsuleRestingVisibleWidth => HoldsDeepCapsuleSlotWhileExpanded
        ? ExpandedDeepCapsuleVisibleWidth()
        : DeepCapsuleVisibleWidth();

    private enum TodoFocusPlacement
    {
        End,
        Start
    }

    private void ClearCapsuleInteractionKeyboardFocus()
    {
        WindowNative.ClearCurrentThreadKeyboardFocus();
        Dispatcher.BeginInvoke(
            (Action)WindowNative.ClearCurrentThreadKeyboardFocus,
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private sealed class TodoDragState
    {
        public TodoDragState(string itemId, Border sourceRow, FrameworkElement handle, Point startPoint)
        {
            ItemId = itemId;
            SourceRow = sourceRow;
            Handle = handle;
            StartPoint = startPoint;
        }

        public string ItemId { get; }
        public Border SourceRow { get; }
        public FrameworkElement Handle { get; }
        public Point StartPoint { get; }
        public bool IsDragging { get; set; }
        public string? TargetId { get; set; }
        public DropPlacement TargetPlacement { get; set; } = DropPlacement.After;
        public bool DropAtEnd { get; set; }

        public Border? Ghost { get; set; }
        public Point MouseOffsetInRow { get; set; }
    }

    private sealed class NoteLinkDragState
    {
        public NoteLinkDragState(FrameworkElement handle, Point startScreenPoint)
        {
            Handle = handle;
            StartScreenPoint = startScreenPoint;
        }

        public FrameworkElement Handle { get; }
        public Point StartScreenPoint { get; }
        public bool IsDragging { get; set; }
        public Window? Ghost { get; set; }
        // Show() of the top-level ghost can steal capture and re-enter LostMouseCapture → End.
        public bool SuppressCaptureLossEnd { get; set; }
    }

    private enum DropPlacement
    {
        Before,
        After
    }

    private static Brush PaperBrush => Theme.PaperBrush;
    private static Brush PaperBorderBrush => Theme.PaperBorderBrush;
    private static Brush TextBrush => Theme.TextBrush;
    private static Brush WeakTextBrush => Theme.WeakTextBrush;
    private static Brush BrightWeakTextBrush => Theme.BrightWeakTextBrush;
    private static Brush HoverBrush => Theme.HoverBrush;
    private static Brush MenuHoverBrush => Theme.HoverBrush;

    // 以下半透明叠加色全部从当前主题的 Tint / Danger 基色派生，
    // 切换配色族（暖纸 / 墨 / 林 / 霞）时自动跟随，无需各自维护 Light/Dark 对。
    private static Brush DropIndicatorBgBrush => Theme.Tint(12);
    private static Brush DropIndicatorBrush => Theme.Tint(180);
    private static Brush AppendDropBrush => Theme.Tint(34);
    private static Brush AppendBorderBrush => Theme.Tint(45);
    private static Brush AppendBgBrush => Theme.Tint(12);
    private static Brush AppendHoverBgBrush => Theme.Tint(26);
    private static Brush NoteLinkTargetBgBrush => Theme.Tint((byte)(Theme.IsDark ? 36 : 28));
    private static Brush NoteLinkTargetBorderBrush => Theme.Tint(150);
    private static Brush LinkedNoteNormalBgBrush => Theme.Tint((byte)(Theme.IsDark ? 28 : 18));
    private static Brush LinkedNoteLightBgBrush => Theme.Tint((byte)(Theme.IsDark ? 48 : 34));
    private static Brush LinkedNoteMediumBgBrush => Theme.Tint((byte)(Theme.IsDark ? 78 : 58));
    private static Brush LinkedNoteActiveTextBrush => Theme.TextBrush;

    private static Brush CheckBoxBorderBrush => Theme.CheckBoxBorderBrush;

    private static Brush TrashBgBrush => Theme.Danger((byte)(Theme.IsDark ? 16 : 12));
    private static Brush TrashBorderBrush => Theme.Danger(50);
    private static Brush TrashTextBrush => Theme.DangerBrush;
    private static Brush TrashHoverBgBrush => Theme.Danger((byte)(Theme.IsDark ? 32 : 26));
    private static Brush TrashHoverBorderBrush => Theme.DangerBrush;

    private static Brush TitleBarBrush => Theme.Tint((byte)(Theme.IsDark ? 18 : 12));
    private static Brush TitleBarDividerBrush => Theme.Tint((byte)(Theme.IsDark ? 34 : 28));
    private const string PinOutlineHeadPathData = "M 7.5,4.25 H 16.5 V 5.75 H 15.5 V 12.05 L 17.6,14.15 V 15.35 H 6.4 V 14.15 L 8.5,12.05 V 5.75 H 7.5 Z";
    private const string PinNeedlePathData = "M 10.85,15.35 H 13.15 V 22.1 L 12,23.25 L 10.85,22.1 Z";
    private const int TodoMoveAnimationMilliseconds = 150;

    private static readonly ControlTemplate SharedContextMenuTemplate = BuildContextMenuTemplate();
    private static readonly Style SharedCompactMenuItemStyle = BuildCompactMenuItemStyle();
    private static readonly Style SharedIconButtonStyle = BuildIconButtonStyle();
    private static readonly Style SharedCheckBoxStyle = BuildCustomCheckBoxStyle();

    private static ControlTemplate BuildContextMenuTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(RadiusBlock));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        border.AppendChild(presenter);

        return new ControlTemplate(typeof(ContextMenu))
        {
            VisualTree = border
        };
    }

    private static Style BuildCompactMenuItemStyle()
    {
        var style = new Style(typeof(MenuItem));

        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 10, 4)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextBrushKey")));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(RadiusControl));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(MenuItem))
        {
            VisualTree = border
        };

        var hover = new Trigger
        {
            Property = WpfMenuItem.IsHighlightedProperty,
            Value = true
        };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("HoverBrushKey"), "Bd"));

        var disabled = new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false
        };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.72));

        template.Triggers.Add(hover);
        template.Triggers.Add(disabled);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Style BuildIconButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("WeakTextBrushKey")));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, AppTypography.SymbolFontFamily));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
        style.Setters.Add(new Setter(Control.FocusableProperty, false));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(RadiusControl));
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

        var mouseOver = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        mouseOver.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("HoverBrushKey")));
        mouseOver.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextBrushKey")));

        var pressed = new Trigger
        {
            Property = ButtonBase.IsPressedProperty,
            Value = true
        };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.7));

        template.Triggers.Add(mouseOver);
        template.Triggers.Add(pressed);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));

        return style;
    }

    private static Style BuildCustomCheckBoxStyle()
    {
        var style = new Style(typeof(CheckBox));

        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 16.0));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 16.0));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
        style.Setters.Add(new Setter(FrameworkElement.UseLayoutRoundingProperty, true));

        var grid = new FrameworkElementFactory(typeof(Grid));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "CheckBorder";
        border.SetValue(FrameworkElement.WidthProperty, 16.0);
        border.SetValue(FrameworkElement.HeightProperty, 16.0);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(RadiusSmall));
        border.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("CheckBoxBorderBrushKey"));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        grid.AppendChild(border);

        var path = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        path.Name = "CheckMark";
        path.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 3,7.5 L 6.5,11 L 13,4"));
        path.SetValue(System.Windows.Shapes.Path.StrokeProperty, new DynamicResourceExtension("PaperBrushKey"));
        path.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 2.0);
        path.SetValue(System.Windows.Shapes.Path.StrokeStartLineCapProperty, PenLineCap.Round);
        path.SetValue(System.Windows.Shapes.Path.StrokeEndLineCapProperty, PenLineCap.Round);
        path.SetValue(System.Windows.Shapes.Path.StrokeLineJoinProperty, PenLineJoin.Round);
        path.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        path.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        path.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        path.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        grid.AppendChild(path);

        var template = new ControlTemplate(typeof(CheckBox))
        {
            VisualTree = grid
        };

        var checkedTrigger = new Trigger
        {
            Property = ToggleButton.IsCheckedProperty,
            Value = true
        };
        checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("CheckBoxActiveBrushKey"), "CheckBorder"));
        checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.Transparent, "CheckBorder"));
        checkedTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(0), "CheckBorder"));
        checkedTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CheckMark"));

        var hoverTrigger = new MultiTrigger();
        hoverTrigger.Conditions.Add(new Condition { Property = UIElement.IsMouseOverProperty, Value = true });
        hoverTrigger.Conditions.Add(new Condition { Property = ToggleButton.IsCheckedProperty, Value = false });
        hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("CheckBoxUncheckedHoverBorderBrushKey"), "CheckBorder"));
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("CheckBoxUncheckedHoverBgKey"), "CheckBorder"));

        var hoverCheckedTrigger = new MultiTrigger();
        hoverCheckedTrigger.Conditions.Add(new Condition { Property = UIElement.IsMouseOverProperty, Value = true });
        hoverCheckedTrigger.Conditions.Add(new Condition { Property = ToggleButton.IsCheckedProperty, Value = true });
        hoverCheckedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("CheckBoxActiveHoverBrushKey"), "CheckBorder"));
        hoverCheckedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.Transparent, "CheckBorder"));
        hoverCheckedTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(0), "CheckBorder"));

        template.Triggers.Add(checkedTrigger);
        template.Triggers.Add(hoverTrigger);
        template.Triggers.Add(hoverCheckedTrigger);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    public PaperWindow(PaperData paper, AppController controller)
    {
        _paper = paper;
        _controller = controller;
        InitializePaperPresentationState();

        ConfigureWindow();
        BuildShell();
        UpdateToolTipSetting();

        Loaded += (_, _) =>
        {
            SaveGeometryIfAllowed();
            // Finish taskbar / Alt+Tab shell registration before ShowPaper's Render-priority
            // reveal. Reapplying it at Background priority after the fade has started makes
            // Windows rebuild each visible paper frame, which appears as a startup flash.
            Dispatcher.BeginInvoke(
                (Action)ApplyDeferredStartupSystemVisibility,
                System.Windows.Threading.DispatcherPriority.Normal);
        };
        LocationChanged += (_, _) => SaveGeometryIfAllowed();
        SizeChanged += (_, _) => SaveGeometryIfAllowed();
        StateChanged += (_, _) => RefreshSnappedPresentation(forceApply: true);
        PreviewMouseMove += OnWindowPreviewMouseMove;
        PreviewMouseWheel += OnWindowPreviewMouseWheel;
        PreviewMouseLeftButtonUp += OnWindowPreviewMouseLeftButtonUp;
        LostMouseCapture += OnLostMouseCapture;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        PreviewKeyUp += OnWindowPreviewKeyUp;
        SourceInitialized += (_, _) =>
        {
            ApplySystemVisibility(reapplyTaskbarShellState: true);
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(OnWindowMessage);
            }
        };
        Activated += (_, _) => _controller.RefreshFloatingSurfaceZOrder();
        Deactivated += (_, _) => AbortAllInteractions(InteractionAbortReason.Deactivated);
        Closing += OnClosing;
        Closed += (_, _) => CompletePaperWindowClose();

        if (_paper.Type == PaperTypes.Note)
        {
            PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (_noteBox != null && _noteBox.IsFocused)
                {
                    var clicked = e.OriginalSource as DependencyObject;
                    if (!IsDescendantOf(clicked, _noteBox))
                    {
                        ExitNoteEditor();
                    }
                }
            };
        }
    }

    public void CloseForReal(bool saveBeforeClose = true)
    {
        if (IsClosed)
        {
            return;
        }

        BeginPaperWindowClose();
        CloseExpandedDeepCapsuleSlotHostForReal();

        if (saveBeforeClose)
        {
            // Force save before closing to prevent data loss if user edited within the last 450ms.
            _controller.SaveNow();
        }

        _closeForReal = true;
        Close();
    }

    public void UpdateToolTipSetting()
    {
        ToolTipPreferences.Apply(this, _controller.State.EnableToolTips);
        _edgeCapsuleHost?.ApplyToolTipSetting(_controller.State.EnableToolTips);
    }

    public void UpdateWindowSwitcherVisibility()
    {
        if (_controller.State.HidePapersFromWindowSwitcher)
        {
            WindowNative.ApplyWindowSwitcherVisibility(this, visible: false);
            _windowSwitcherHiddenOwnerApplied = true;
            return;
        }

        if (_windowSwitcherHiddenOwnerApplied)
        {
            WindowNative.ApplyWindowSwitcherVisibility(this, visible: true);
            _windowSwitcherHiddenOwnerApplied = false;
        }
    }

    public void UpdateTaskbarVisibility(bool reapplyShellState = false)
    {
        var shouldShow = ShouldShowInTaskbar();
        if (reapplyShellState && !shouldShow && !ShowInTaskbar)
        {
            ShowInTaskbar = true;
        }

        ShowInTaskbar = shouldShow;
    }

    public void ApplySystemVisibility(bool reapplyTaskbarShellState = false)
    {
        if (_controller.State.HidePapersFromWindowSwitcher)
        {
            UpdateTaskbarVisibility(reapplyTaskbarShellState);
            UpdateWindowSwitcherVisibility();
            return;
        }

        UpdateWindowSwitcherVisibility();
        UpdateTaskbarVisibility(reapplyTaskbarShellState);
    }

    private void ApplyDeferredStartupSystemVisibility()
    {
        var shouldShowInTaskbar = ShouldShowInTaskbar();
        ApplySystemVisibility(reapplyTaskbarShellState: ShowInTaskbar != shouldShowInTaskbar || !shouldShowInTaskbar);
    }

    private bool ShouldShowInTaskbar()
    {
        return !_controller.State.HidePapersFromWindowSwitcher &&
            !_controller.State.HidePapersFromTaskbar &&
            !_paper.IsCollapsed;
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // PreviewKeyDown needs a WPF keyboard-focus target. Note preview deliberately
        // makes the editor non-focusable, so an active paper can receive WM_KEYDOWN
        // without producing a routed key event. Handle Escape at the HWND boundary and
        // reuse the same collapse path as focused editors.
        if (msg == WmKeyDown &&
            wParam.ToInt32() == VkEscape &&
            Keyboard.Modifiers == ModifierKeys.None &&
            TryCollapseExpandedPaperFromEscape())
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (msg is WmDpiChanged or WmDisplayChange or WmSettingChange)
        {
            if (IsDeepCapsuleReordering)
            {
                _controller.DeferDisplayMetricsRefreshUntilDeepCapsuleDragEnds();
            }
            else
            {
                _controller.ScheduleDisplayMetricsRefresh();
            }
        }

        if (msg == WmWindowPosChanged)
        {
            RefreshSnappedPresentation();
        }

        if (msg == WmGetMinMaxInfo)
        {
            try
            {
                var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                var dpi = GetDpiForWindow(hwnd);
                var dpiScale = dpi > 0 ? dpi / 96.0 : 1.0;
                mmi.MinTrackSize = new MutablePoint
                {
                    X = Math.Max(mmi.MinTrackSize.X, (int)Math.Ceiling(MinWidth * dpiScale)),
                    Y = Math.Max(mmi.MinTrackSize.Y, (int)Math.Ceiling(MinHeight * dpiScale))
                };

                // Clamp maximize bounds to the work area so the window doesn't cover the taskbar.
                // This hook must also preserve WPF's minimum tracking size because handled=true
                // prevents the framework from applying MinWidth/MinHeight afterward.
                var monitor = MonitorFromWindow(hwnd, 2);
                if (monitor != IntPtr.Zero)
                {
                    var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        var wa = info.WorkArea;
                        var mon = info.Monitor;

                        // If work area equals monitor (auto-hide taskbar), use full screen to avoid gap
                        bool isAutoHide = (wa.Left == mon.Left && wa.Top == mon.Top &&
                                           wa.Right == mon.Right && wa.Bottom == mon.Bottom);

                        var rect = isAutoHide ? mon : wa;
                        mmi.MaxSize = new MutablePoint { X = rect.Right - rect.Left, Y = rect.Bottom - rect.Top };
                        mmi.MaxPosition = new MutablePoint { X = rect.Left - mon.Left, Y = rect.Top - mon.Top };
                    }
                }

                Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
            }
            catch
            {
                // Ignore malformed lParam or marshal failures; WPF's default behavior is acceptable.
            }
        }

        return IntPtr.Zero;
    }

    // Snap-completed presentation patch:
    // When the OS docks the window to a monitor tile (drag-to-edge or Win+arrow), make the
    // paper fill the tile edge-to-edge: suppress the drop shadow AND collapse the 8px
    // transparent chrome margin + corner radius. Leaving the margin in place reads as a
    // dark seam around the snapped paper (the shadow lives inside that margin, and the
    // rounded corners leak the wallpaper behind). Maximize is different: the system
    // over-expands a maximized window by its resize border, which already pushes the
    // margin off-screen — keep it as the offset.
    private bool _isSnappedPresentation;


    private void RefreshSnappedPresentation(bool forceApply = false)
    {
        if (_paperChrome == null)
        {
            return;
        }

        var snapped = !_paper.IsCollapsed &&
            !IsPaperFormTransitioning &&
            LooksSnappedNow();

        if (snapped == _isSnappedPresentation)
        {
            // Callers at transition boundaries force a re-apply because the transition
            // itself rewrites Margin/CornerRadius/Effect while the guards were up.
            if (forceApply)
            {
                ApplyPaperChromePresentation();
            }

            return;
        }

        if (snapped)
        {
            if (TryGetCurrentSnapTileBounds(out var bounds))
            {
                _snappedPresentationBoundsForRestore = bounds;
            }

            _isSnappedPresentation = true;
            ApplyPaperChromePresentation();
            SaveGeometryIfAllowed();
            return;
        }

        if (IsVisible && !_paper.IsCollapsed && !IsPaperFormTransitioning)
        {
            _snappedPresentationBoundsForRestore = null;
        }

        _isSnappedPresentation = false;
        ApplyPaperChromePresentation();
    }

    // Central authority for the paper chrome's snap/form presentation: Effect, Margin and
    // CornerRadius. Any code that changes these based on paper form (expanded vs capsule)
    // or snap state should go through here so the inputs can't disagree with each other.
    // Historical bug: two call sites (SetCollapsedState) mutated an existing DropShadowEffect
    // in-place via `if (Effect is DropShadowEffect s)`. Once snap can set Effect=null,
    // that check silently no-ops and the capsule ends up with expanded-shape shadow (or none).
    private void ApplyPaperChromePresentation()
    {
        if (_paperChrome == null)
        {
            return;
        }

        var snappedExpanded = _isSnappedPresentation && !_paper.IsCollapsed;

        // Snapped presentation: make the paper fill the tile edge-to-edge (no shadow, no
        // margin, square corners). Works for Normal-state tiles (half/quarter) and Maximized.
        if (snappedExpanded)
        {
            _paperChrome.Effect = null;
            _paperChrome.BeginAnimation(Border.MarginProperty, null);
            _paperChrome.Margin = new Thickness(0);
            _paperChrome.CornerRadius = new CornerRadius(0);
            return;
        }

        // Floating or capsule: restore shadow, margin, and form-appropriate corner radius.
        var isCapsule = _paper.IsCollapsed && _controller.State.UseCapsuleMode;
        var targetCorner = PaperChromeCornerRadiusForState(isCapsule);
        _paperChrome.BeginAnimation(Border.MarginProperty, null);
        _paperChrome.Margin = new Thickness(WindowChromeMargin);
        _paperChrome.CornerRadius = targetCorner;
        _paperChrome.Effect = isCapsule
            ? CreatePaperChromeShadow(blurRadius: 8, opacity: 0.08)
            : CreatePaperChromeShadow();
    }

    private bool LooksSnappedNow()
    {
        if (WindowState == WindowState.Minimized)
        {
            return false;
        }

        if (WindowState == WindowState.Maximized)
        {
            return true;
        }

        var workArea = WindowWorkAreaHelper.WorkAreaFor(this);
        if (workArea.IsEmpty)
        {
            return false;
        }

        if (WindowNative.TryGetVisibleFrameScreenBounds(this, out var visibleFrameRect) &&
            TryGetSnapTileBounds(visibleFrameRect, workArea, out _))
        {
            return true;
        }

        // WM_WINDOWPOSCHANGED arrives before WPF syncs Left/Top/Width/Height (that happens in
        // the WM_MOVE/WM_SIZE that DefWindowProc raises afterwards, and never while maximized),
        // so the DPs still hold the pre-snap rect here. Half/quarter snaps are a single
        // SetWindowPos with no follow-up message, so judging by the DPs stays one move behind
        // forever — read the live hwnd rect instead.
        if (!TryGetWindowRectDip(out var windowRect))
        {
            return false;
        }

        var chromeRect = new Rect(
            windowRect.Left + WindowChromeMargin,
            windowRect.Top + WindowChromeMargin,
            Math.Max(0, windowRect.Width - WindowChromeInset),
            Math.Max(0, windowRect.Height - WindowChromeInset));

        return MatchesSnapTile(windowRect, workArea) || MatchesSnapTile(chromeRect, workArea);
    }

    // The current hwnd rect (GetWindowRect, physical pixels) converted into this window's DIP
    // space via the same per-monitor transform WorkAreaFor uses, so both rects stay comparable
    // on mixed-DPI monitor setups.
    private bool TryGetWindowRectDip(out Rect rect)
    {
        rect = Rect.Empty;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var native))
        {
            return false;
        }

        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
        {
            return false;
        }

        var transform = target.TransformFromDevice;
        rect = new Rect(
            transform.Transform(new Point(native.Left, native.Top)),
            transform.Transform(new Point(native.Right, native.Bottom)));
        return true;
    }

    private static bool MatchesSnapTile(Rect rect, Rect workArea)
    {
        return TryGetSnapTileBounds(rect, workArea, out _);
    }

    private bool TryGetCurrentSnapTileBounds(out Rect bounds)
    {
        bounds = Rect.Empty;
        if (WindowState == WindowState.Minimized)
        {
            return false;
        }

        var workArea = WindowWorkAreaHelper.WorkAreaFor(this);
        if (workArea.IsEmpty)
        {
            return false;
        }

        if (WindowState == WindowState.Maximized)
        {
            bounds = workArea;
            return true;
        }

        if (WindowNative.TryGetVisibleFrameScreenBounds(this, out var visibleFrameRect) &&
            TryGetSnapTileBounds(visibleFrameRect, workArea, out bounds))
        {
            return true;
        }

        if (!TryGetWindowRectDip(out var windowRect))
        {
            return false;
        }

        if (TryGetSnapTileBounds(windowRect, workArea, out bounds))
        {
            return true;
        }

        var chromeRect = new Rect(
            windowRect.Left + WindowChromeMargin,
            windowRect.Top + WindowChromeMargin,
            Math.Max(0, windowRect.Width - WindowChromeInset),
            Math.Max(0, windowRect.Height - WindowChromeInset));

        return TryGetSnapTileBounds(chromeRect, workArea, out bounds);
    }

    internal bool TryGetSnappedPresentationBoundsForGeometrySave(out Rect bounds)
    {
        bounds = Rect.Empty;
        if (!_paper.IsCollapsed &&
            (_isSnappedPresentation || WindowState == WindowState.Maximized) &&
            TryGetCurrentSnapTileBounds(out bounds))
        {
            _snappedPresentationBoundsForRestore = bounds;
            return true;
        }

        return false;
    }

    internal bool TryGetRememberedSnapTileBoundsForRestore(out Rect bounds)
    {
        bounds = Rect.Empty;
        if (_paper.IsCollapsed || _snappedPresentationBoundsForRestore is not Rect remembered)
        {
            return false;
        }

        // The remembered snap tile was captured in this window's DIP space. Keep the
        // restore-time validation in the same coordinate space; re-resolving from the rect
        // uses system-DPI coordinates and can reject valid tiles on mixed-DPI monitors.
        var workArea = WindowWorkAreaHelper.WorkAreaFor(this);
        return TryGetSnapTileBounds(remembered, workArea, out bounds);
    }

    internal void RestoreSnapTilePresentation(Rect visibleTarget)
    {
        if (_paper.IsCollapsed)
        {
            return;
        }

        _isSnappedPresentation = true;
        _snappedPresentationBoundsForRestore = visibleTarget;
        ApplyPaperChromePresentation();
        MoveWindowWithoutGeometrySave(() =>
        {
            Left = RoundToDevicePixelX(visibleTarget.Left);
            Top = RoundToDevicePixelY(visibleTarget.Top);
            Width = RoundToDevicePixelX(Math.Max(MinWidth, visibleTarget.Width));
            Height = RoundToDevicePixelY(Math.Max(MinHeight, visibleTarget.Height));
        });
        UpdateLayout();
        AlignVisibleFrameToBounds(visibleTarget);
        _isSnappedPresentation = true;
        ApplyPaperChromePresentation();
    }

    internal void AlignVisibleFrameToBounds(Rect visibleTarget)
    {
        if (!WindowNative.TryGetVisibleFrameScreenBounds(this, out var visibleBounds) ||
            visibleBounds.IsEmpty)
        {
            return;
        }

        var dx = visibleTarget.Left - visibleBounds.Left;
        var dy = visibleTarget.Top - visibleBounds.Top;
        var dw = visibleTarget.Width - visibleBounds.Width;
        var dh = visibleTarget.Height - visibleBounds.Height;
        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5 && Math.Abs(dw) < 0.5 && Math.Abs(dh) < 0.5)
        {
            return;
        }

        MoveWindowWithoutGeometrySave(() =>
        {
            Left = RoundToDevicePixelX(Left + dx);
            Top = RoundToDevicePixelY(Top + dy);
            Width = RoundToDevicePixelX(Math.Max(MinWidth, Width + dw));
            Height = RoundToDevicePixelY(Math.Max(MinHeight, Height + dh));
        });
        RefreshSnappedPresentation(forceApply: true);
    }

    private static bool TryGetSnapTileBounds(Rect rect, Rect workArea, out Rect bounds)
    {
        bounds = Rect.Empty;
        if (rect.IsEmpty || workArea.IsEmpty)
        {
            return false;
        }

        const double tolerance = WindowChromeInset + 4.0;
        // A window must occupy at least this fraction of a work-area dimension before a mere
        // edge graze counts as a snapped tile — keeps small free-floating windows out.
        const double minSpanRatio = 0.20;
        var wa = workArea;
        bool nearLeft = Math.Abs(rect.Left - wa.Left) <= tolerance;
        bool nearTop = Math.Abs(rect.Top - wa.Top) <= tolerance;
        bool nearRight = Math.Abs(rect.Right - wa.Right) <= tolerance;
        bool nearBottom = Math.Abs(rect.Bottom - wa.Bottom) <= tolerance;

        bool fullWidth = nearLeft && nearRight && Math.Abs(rect.Width - wa.Width) <= tolerance;
        bool fullHeight = nearTop && nearBottom && Math.Abs(rect.Height - wa.Height) <= tolerance;
        bool horizontallyContained = rect.Left >= wa.Left - tolerance && rect.Right <= wa.Right + tolerance;
        bool centerThirdColumn =
            !nearLeft &&
            !nearRight &&
            horizontallyContained &&
            Math.Abs(rect.Left - (wa.Left + wa.Width / 3)) <= tolerance &&
            Math.Abs(rect.Right - (wa.Left + wa.Width * 2 / 3)) <= tolerance;
        bool centerHalfColumn =
            !nearLeft &&
            !nearRight &&
            horizontallyContained &&
            Math.Abs(rect.Left - (wa.Left + wa.Width / 4)) <= tolerance &&
            Math.Abs(rect.Right - (wa.Left + wa.Width * 3 / 4)) <= tolerance;

        // Maximized / full-area tile.
        if (fullWidth && fullHeight)
        {
            bounds = wa;
            return true;
        }

        // Edge columns may use any width after the divider is dragged. Interior columns are
        // limited to Windows 11's standard equal-thirds and 25/50/25 center tiles so an
        // arbitrary full-height floating window is not mistaken for a Snap Layout member.
        if (fullHeight &&
            ((nearLeft ^ nearRight) || centerThirdColumn || centerHalfColumn) &&
            rect.Width >= wa.Width * minSpanRatio)
        {
            var left = nearLeft ? wa.Left : Math.Clamp(rect.Left, wa.Left, wa.Right);
            var right = nearRight ? wa.Right : Math.Clamp(rect.Right, wa.Left, wa.Right);
            if (right - left >= tolerance)
            {
                bounds = new Rect(new Point(left, wa.Top), new Point(right, wa.Bottom));
                return true;
            }
        }

        // Row tile: spans the full width and is docked to exactly one horizontal edge, at any
        // height. Capture the actual occupied height.
        if (fullWidth && (nearTop ^ nearBottom) && rect.Height >= wa.Height * minSpanRatio)
        {
            var top = nearTop ? wa.Top : Math.Clamp(rect.Top, wa.Top, wa.Bottom);
            var bottom = nearBottom ? wa.Bottom : Math.Clamp(rect.Bottom, wa.Top, wa.Bottom);
            if (bottom - top >= tolerance)
            {
                bounds = new Rect(new Point(wa.Left, top), new Point(wa.Right, bottom));
                return true;
            }
        }

        // Standard stacked corner tiles are half-height and either half-width or one-third
        // width (the latter appears beside a 2/3 main column). Keep the ratio gates instead of
        // accepting any corner-adjacent floating window.
        bool halfWidth = Math.Abs(rect.Width - wa.Width / 2) <= tolerance;
        bool thirdWidth = Math.Abs(rect.Width - wa.Width / 3) <= tolerance;
        bool halfHeight = Math.Abs(rect.Height - wa.Height / 2) <= tolerance;
        if ((halfWidth || thirdWidth) && halfHeight && (nearLeft ^ nearRight) && (nearTop ^ nearBottom))
        {
            var left = nearLeft ? wa.Left : Math.Clamp(rect.Left, wa.Left, wa.Right);
            var right = nearRight ? wa.Right : Math.Clamp(rect.Right, wa.Left, wa.Right);
            var top = nearTop ? wa.Top : Math.Clamp(rect.Top, wa.Top, wa.Bottom);
            var bottom = nearBottom ? wa.Bottom : Math.Clamp(rect.Bottom, wa.Top, wa.Bottom);
            if (right - left >= tolerance && bottom - top >= tolerance)
            {
                bounds = new Rect(new Point(left, top), new Point(right, bottom));
                return true;
            }
        }

        return false;
    }

    private static DropShadowEffect CreatePaperChromeShadow(double blurRadius = 14, double opacity = 0.18)
    {
        return new DropShadowEffect
        {
            BlurRadius = blurRadius,
            ShadowDepth = 2,
            Opacity = opacity
        };
    }

    public void CancelPendingVisibilityTransitions()
    {
        BeginAnimation(Window.OpacityProperty, null);
        Opacity = 1.0;

        _edgeCapsule.CancelTransition();

        if (IsDeepCapsuleSlotRetracting)
        {
            SetEdgeCapsuleSlotState(EdgeCapsuleSlotState.None);
            if (!_paper.IsCollapsed &&
                _controller.State.UseCapsuleMode &&
                _controller.State.UseDeepCapsuleMode &&
                _edgeCapsuleHost?.IsVisible == true)
            {
                SetEdgeCapsuleSlotState(EdgeCapsuleSlotState.ExpandedReserved);
            }
        }

        ReconcileDeepCapsuleHostPresentation();
    }

    private void ConfigureWindow()
    {
        InitializeThemeResources();
        Title = _controller.PaperTitleText(_paper);
        ShowInTaskbar = ShouldShowInTaskbar();
        WindowStartupLocation = WindowStartupLocation.Manual;

        Left = _paper.X;
        Top = _paper.Y;

        if (_paper.IsCollapsed && _controller.State.UseCapsuleMode)
        {
            Width = CapsuleWindowWidth();
            Height = PaperLayoutDefaults.CapsuleHeight;
            MinWidth = CapsuleWindowWidth();
            MinHeight = PaperLayoutDefaults.CapsuleHeight;
            ResizeMode = ResizeMode.NoResize;
        }
        else
        {
            Width = _paper.Width;
            Height = _paper.Height;
            MinWidth = PaperLayoutDefaults.MinWidth;
            MinHeight = PaperLayoutDefaults.MinHeight;
            ResizeMode = ResizeMode.CanResizeWithGrip;
        }

        RefreshEffectiveTopmost();
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        FontFamily = AppTypography.UiFontFamily;
        Language = AppTypography.Language;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    private void InitializeThemeResources()
    {
        Resources["PaperBrushKey"] = PaperBrush;
        Resources["PaperBorderBrushKey"] = PaperBorderBrush;
        Resources["TextBrushKey"] = TextBrush;
        Resources["WeakTextBrushKey"] = WeakTextBrush;
        Resources["HoverBrushKey"] = HoverBrush;
        Resources["DropIndicatorBrushKey"] = DropIndicatorBrush;
        Resources["AppendDropBrushKey"] = AppendDropBrush;
        Resources["MenuHoverBrushKey"] = MenuHoverBrush;
        Resources["TitleBarBrushKey"] = TitleBarBrush;
        Resources["TitleBarDividerBrushKey"] = TitleBarDividerBrush;

        Resources["CheckBoxBorderBrushKey"] = CheckBoxBorderBrush;
        Resources["CheckBoxActiveBrushKey"] = Theme.ActiveBrush;
        Resources["CheckBoxUncheckedHoverBorderBrushKey"] = Theme.CheckBoxHoverBorderBrush;
        Resources["CheckBoxUncheckedHoverBgKey"] = Theme.CheckBoxUncheckedHoverBgBrush;
        Resources["CheckBoxActiveHoverBrushKey"] = Theme.CheckBoxActiveHoverBrush;
    }

    public void UpdateTheme()
    {
        var oldPaperColor = TryGetSolidColor(_paperChrome?.Background, out var capturedPaperColor)
            ? capturedPaperColor
            : (Color?)null;
        var oldBorderColor = TryGetSolidColor(_paperChrome?.BorderBrush, out var capturedBorderColor)
            ? capturedBorderColor
            : (Color?)null;

        _themeAnimationGeneration++;
        var themeAnimationGeneration = _themeAnimationGeneration;

        InitializeThemeResources();
        RefreshThemedContextMenus();

        var canAnimateTheme = _controller.State.EnableAnimations &&
            _paperChrome != null &&
            oldPaperColor.HasValue &&
            oldBorderColor.HasValue &&
            TryGetSolidColor(Resources["PaperBrushKey"] as Brush, out var newPaperColor) &&
            TryGetSolidColor(Resources["PaperBorderBrushKey"] as Brush, out var newBorderColor);

        // 主题动画只能使用临时本地画刷；完成后必须恢复动态资源绑定。
        if (_controller.State.EnableAnimations && _paperChrome != null)
        {
            if (canAnimateTheme)
            {
                var pendingAnimations = 0;

                void MarkThemeAnimationComplete()
                {
                    pendingAnimations--;
                    if (pendingAnimations <= 0 && themeAnimationGeneration == _themeAnimationGeneration)
                    {
                        RestorePaperChromeThemeReferences();
                    }
                }

                pendingAnimations++;
                AnimatePaperChromeBrush(
                    oldPaperColor!.Value,
                    newPaperColor,
                    brush => _paperChrome.Background = brush,
                    MarkThemeAnimationComplete);

                pendingAnimations++;
                AnimatePaperChromeBrush(
                    oldBorderColor!.Value,
                    newBorderColor,
                    brush => _paperChrome.BorderBrush = brush,
                    MarkThemeAnimationComplete);
            }
            else
            {
                RestorePaperChromeThemeReferences();
            }
        }
        else
        {
            RestorePaperChromeThemeReferences();
        }

        RefreshPaperTitle();
        RefreshPaperIconButton();
        UpdateTextZoom();
        UpdateDeepCapsuleSlotHostTheme();

        if (_paper.Type == PaperTypes.Note)
        {
            if (_noteBox != null)
            {
                _noteBox.RefreshVisualStyle();
            }

        }
        else
        {
            RebuildTodoRows(CurrentFocusedTodoItemId());
        }
    }

    public void UpdateTypography()
    {
        FontFamily = AppTypography.UiFontFamily;
        Language = AppTypography.Language;

        if (_titleText != null)
        {
            _titleText.FontFamily = AppTypography.UiFontFamily;
        }

        if (_titleEditBox != null)
        {
            _titleEditBox.FontFamily = AppTypography.UiFontFamily;
        }

        if (_openMarkdownButton != null)
        {
            _openMarkdownButton.FontFamily = AppTypography.UiFontFamily;
        }

        if (_noteBox != null)
        {
            _noteBox.RefreshTypography();
        }

        if (_paper.Type == PaperTypes.Todo)
        {
            RebuildTodoRows(CurrentFocusedTodoItemId());
        }

        if (_capsuleIconText != null)
        {
            _capsuleIconText.FontFamily = AppTypography.SymbolFontFamily;
        }

        if (_capsuleLabelText != null)
        {
            _capsuleLabelText.FontFamily = AppTypography.UiFontFamily;
        }

        _edgeCapsuleHost?.UpdateTypography(
            AppTypography.UiFontFamily,
            AppTypography.SymbolFontFamily,
            AppTypography.Language);

        RefreshPaperTitle();
    }

    private static bool TryGetSolidColor(Brush? brush, out Color color)
    {
        if (brush is SolidColorBrush solidBrush)
        {
            color = solidBrush.Color;
            return true;
        }

        color = default;
        return false;
    }

    private void AnimatePaperChromeBrush(Color from, Color to, Action<SolidColorBrush> assignBrush, Action onComplete)
    {
        var transitionBrush = new SolidColorBrush(from);
        assignBrush(transitionBrush);

        var animation = new System.Windows.Media.Animation.ColorAnimation(to, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = AnimationHelper.SmoothEase
        };
        animation.Completed += (_, _) => onComplete();
        transitionBrush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    private void RestorePaperChromeThemeReferences()
    {
        if (_paperChrome == null)
        {
            return;
        }

        _paperChrome.SetResourceReference(Border.BackgroundProperty, "PaperBrushKey");
        _paperChrome.SetResourceReference(Border.BorderBrushProperty, "PaperBorderBrushKey");
    }

    private void BuildShell()
    {
        _windowHost = new Grid
        {
            Background = Brushes.Transparent,
            ClipToBounds = false
        };
        Content = _windowHost;

        _paperChrome = new Border
        {
            Margin = new Thickness(WindowChromeMargin),
            CornerRadius = PaperChromeCornerRadiusForState(_paper.IsCollapsed && _controller.State.UseCapsuleMode),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            Effect = CreatePaperChromeShadow()
        };
        _paperChrome.SetResourceReference(Border.BackgroundProperty, "PaperBrushKey");
        _paperChrome.SetResourceReference(Border.BorderBrushProperty, "PaperBorderBrushKey");

        // Chrome-level drag gesture: when users click the chrome background itself (top margin
        // area in snapped state, or transparent margin in floating state), initiate title bar
        // drag. This covers the gap where top.Margin creates a dead zone at the window's edge.
        _paperChrome.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource == _paperChrome || e.OriginalSource == _windowHost || e.OriginalSource == _shell)
            {
                BeginTitleBarDragGesture(_paperChrome, e);
            }
        };
        _paperChrome.PreviewMouseMove += (_, e) =>
        {
            if (e.OriginalSource == _paperChrome || e.OriginalSource == _windowHost || e.OriginalSource == _shell)
            {
                UpdateTitleBarDragGesture(_paperChrome, e);
            }
        };
        _paperChrome.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource == _paperChrome || e.OriginalSource == _windowHost || e.OriginalSource == _shell)
            {
                EndTitleBarDragGesture(_paperChrome);
            }
        };
        _paperChrome.LostMouseCapture += (_, _) => EndTitleBarDragGesture(_paperChrome);

        _windowHost.Children.Add(_paperChrome);

        _containerGrid.Background = Brushes.Transparent;
        _containerGrid.ClipToBounds = false;
        _containerGrid.RenderTransform = _shellScale;
        _containerGrid.RenderTransformOrigin = new Point(0, 0);
        _paperChrome.Child = _containerGrid;

        _shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _containerGrid.Children.Add(_shell);

        BuildTopBar();
        BuildBody();
        BuildDragLayer();

        BuildCapsuleShell();
        AttachCapsuleShellToWindowHost();

        if (_paper.IsCollapsed && _controller.State.UseCapsuleMode)
        {
            _shell.Visibility = Visibility.Collapsed;
            _shell.Opacity = 0;
            _capsuleShell.Visibility = Visibility.Visible;
            _capsuleShell.Opacity = 1;
        }
        else
        {
            _shell.Visibility = Visibility.Visible;
            _shell.Opacity = 1;
            _capsuleShell.Visibility = Visibility.Collapsed;
            _capsuleShell.Opacity = 0;
        }

        _paperChrome.ContextMenu = BuildPaperContextMenu();
        UpdateTextZoom();
        ApplyPaperChromePresentation();
    }

    private void AttachCapsuleShellToWindowHost()
    {
        _capsuleShell.BeginAnimation(UIElement.OpacityProperty, null);
        _capsuleShell.Margin = new Thickness(WindowChromeMargin);
        _capsuleShell.HorizontalAlignment = HorizontalAlignment.Left;
        _capsuleShell.VerticalAlignment = VerticalAlignment.Top;
        Panel.SetZIndex(_capsuleShell, 10);
        if (!_windowHost.Children.Contains(_capsuleShell))
        {
            _windowHost.Children.Add(_capsuleShell);
        }
    }

    private void BuildDragLayer()
    {
        _dragLayer = new Canvas
        {
            IsHitTestVisible = false,
            Background = Brushes.Transparent,
            ClipToBounds = false
        };

        Grid.SetRowSpan(_dragLayer, 3);
        Panel.SetZIndex(_dragLayer, 1000);
        _shell.Children.Add(_dragLayer);
    }

    private void BeginTitleBarDragGesture(FrameworkElement dragSource, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        EndTitleBarDragGesture();
        _titleBarDragSession = new TitleBarDragSession(dragSource, e.GetPosition(this));
        dragSource.CaptureMouse();
        e.Handled = true;
    }

    private void UpdateTitleBarDragGesture(FrameworkElement dragSource, MouseEventArgs e)
    {
        var session = _titleBarDragSession;
        if (session == null || !ReferenceEquals(session.Source, dragSource))
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndTitleBarDragGesture(dragSource);
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - session.StartPosition.X) < TitleBarDragThreshold &&
            Math.Abs(current.Y - session.StartPosition.Y) < TitleBarDragThreshold)
        {
            return;
        }

        EndTitleBarDragGesture(dragSource);
        WindowNative.BeginWindowCaptionDrag(this);
        e.Handled = true;
    }

    private void EndTitleBarDragGesture(FrameworkElement dragSource)
    {
        if (_titleBarDragSession == null || !ReferenceEquals(_titleBarDragSession.Source, dragSource))
        {
            return;
        }

        EndTitleBarDragGesture();
    }

    private void EndTitleBarDragGesture()
    {
        var session = _titleBarDragSession;
        _titleBarDragSession = null;
        if (session?.Source.IsMouseCaptured == true)
        {
            session.Source.ReleaseMouseCapture();
        }
    }

    private void BuildTopBar()
    {
        var top = new Grid
        {
            Height = TitleBarHeight,
            Margin = new Thickness(3, 3, 6, 0),
            Background = Brushes.Transparent
        };

        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        top.PreviewMouseLeftButtonDown += (_, e) =>
        {
            var source = e.OriginalSource as DependencyObject;
            if (_isEditingTitle && !IsTitleEditBoxEventSource(source))
            {
                CommitTitleEdit();
                _suppressTitleEditFromCurrentClick = true;
                Dispatcher.BeginInvoke(
                    (Action)(() => _suppressTitleEditFromCurrentClick = false),
                    System.Windows.Threading.DispatcherPriority.Input);
            }

            if (!IsTitleEditBoxEventSource(source))
            {
                ExitNoteEditor();
            }
        };
        top.MouseLeftButtonDown += (_, e) =>
        {
            if (IsTitleEditBoxEventSource(e.OriginalSource as DependencyObject))
            {
                return;
            }

            if (e.ChangedButton == MouseButton.Left && e.ClickCount == 1)
            {
                BeginTitleBarDragGesture(top, e);
            }
        };
        top.PreviewMouseMove += (_, e) => UpdateTitleBarDragGesture(top, e);
        top.PreviewMouseLeftButtonUp += (_, _) => EndTitleBarDragGesture(top);
        top.LostMouseCapture += (_, _) => EndTitleBarDragGesture(top);

        var titleArea = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        titleArea.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _paperIconButton = IconButton("", _paper.AlwaysOnTop ? Strings.Get("Unpin") : Strings.Get("Pin"));
        _paperIconButton.Width = 23;
        _paperIconButton.HorizontalAlignment = HorizontalAlignment.Left;
        _paperIconButton.VerticalAlignment = VerticalAlignment.Center;
        _paperIconButton.Click += (_, _) => ToggleTopmost();
        _paperIconButton.MouseEnter += (_, _) => _paperIconButton.Opacity = 1.0;
        _paperIconButton.MouseLeave += (_, _) => RefreshPaperIconButton();
        RefreshPaperIconButton();

        Grid.SetColumn(_paperIconButton, 0);
        titleArea.Children.Add(_paperIconButton);

        var titleHost = new Border
        {
            Margin = new Thickness(0, 1, 8, 1),
            Padding = new Thickness(4, 0, 5, 0),
            CornerRadius = new CornerRadius(RadiusControl),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = Brushes.Transparent,
            Cursor = Cursors.IBeam,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            MinWidth = 38,
            MaxWidth = 86,
            ToolTip = Strings.Get("ToolTipEditTitle")
        };
        titleHost.SetResourceReference(Border.BorderBrushProperty, "TitleBarDividerBrushKey");

        var titleEditLayer = new Grid
        {
            MinWidth = 30,
            MaxWidth = 76,
            Height = TitleLineHeight + 1,
            VerticalAlignment = VerticalAlignment.Center
        };

        _titleText = new TextBlock
        {
            Foreground = TextBrush,
            FontSize = TitleFontSize,
            Height = TitleLineHeight + 1,
            LineHeight = TitleLineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.IBeam
        };

        _titleEditBox = new TextBox
        {
            Visibility = Visibility.Collapsed,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = TextBrush,
            CaretBrush = TextBrush,
            FontSize = TitleFontSize,
            Height = TitleLineHeight + 1,
            FontWeight = FontWeights.SemiBold,
            MaxLength = PaperTitles.MaxTitleLength,
            // MaxLength is only a coarse UTF-16 guard; the real title limit is applied on commit
            // so IME composition is never interrupted by rewriting TextBox.Text mid-edit.
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            FocusVisualStyle = null
        };
        _titleEditBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitTitleEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                EndTitleEdit(commit: false);
                e.Handled = true;
            }
        };
        _titleEditBox.LostKeyboardFocus += (_, _) =>
        {
            if (_isEditingTitle)
            {
                CommitTitleEdit();
            }
        };

        titleEditLayer.Children.Add(_titleText);
        titleEditLayer.Children.Add(_titleEditBox);
        titleHost.Child = titleEditLayer;
        titleHost.MouseEnter += (_, _) => titleHost.Background = HoverBrush;
        titleHost.MouseLeave += (_, _) => titleHost.Background = Brushes.Transparent;
        titleHost.MouseLeftButtonDown += (_, e) =>
        {
            if (_suppressTitleEditFromCurrentClick)
            {
                _suppressTitleEditFromCurrentClick = false;
                e.Handled = true;
                return;
            }

            if (_isEditingTitle && IsTitleEditBoxEventSource(e.OriginalSource as DependencyObject))
            {
                return;
            }

            BeginTitleEdit();
            e.Handled = true;
        };

        Grid.SetColumn(titleHost, 1);
        titleArea.Children.Add(titleHost);

        RefreshPaperTitle();

        Grid.SetColumn(titleArea, 0);
        top.Children.Add(titleArea);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        _newTodoButton = IconButton("＋✓", Strings.Get("ToolTipNewTodoPaper"));
        _newTodoButton.Click += (_, _) => _controller.CreatePaper(PaperTypes.Todo, show: true, _paper);

        _newNoteButton = IconButton("＋✎", Strings.Get("ToolTipNewNotePaper"));
        _newNoteButton.Click += (_, _) => _controller.CreatePaper(PaperTypes.Note, show: true, _paper);

        if (_paper.Type == PaperTypes.Note)
        {
            _linkNoteButton = IconButton("⌖", Strings.Get("ToolTipDragNoteToTodo"));
            _linkNoteButton.Width = 24;
            _linkNoteButton.FontSize = 13;
            _linkNoteButton.Cursor = Cursors.Cross;
            _linkNoteButton.Visibility = _controller.State.EnableTodoNoteLinks ? Visibility.Visible : Visibility.Collapsed;
            _linkNoteButton.PreviewMouseLeftButtonDown += (_, e) => BeginNoteLinkMouseGesture(_linkNoteButton, e);
            _linkNoteButton.PreviewMouseMove += (_, e) => UpdateNoteLinkMouseGesture(e);
            _linkNoteButton.PreviewMouseLeftButtonUp += (_, e) => EndNoteLinkMouseGestureFromMouseUp(e);
            _linkNoteButton.LostMouseCapture += (_, _) =>
            {
                var state = _noteLinkDrag;
                if (state?.SuppressCaptureLossEnd == true)
                {
                    return;
                }

                // Ghost Show() may steal capture while the button is still down; re-capture so
                // the drag does not tear down mid-gesture (same class of bug as capsule floating drag).
                if (state != null &&
                    Mouse.LeftButton == MouseButtonState.Pressed &&
                    state.Handle.IsVisible &&
                    state.Handle.IsEnabled)
                {
                    state.Handle.CaptureMouse();
                    return;
                }

                EndNoteLinkMouseGesture(commit: false);
            };
            buttons.Children.Add(_linkNoteButton);

            _openMarkdownButton = IconButton(ExternalOpenButtonLabel(), OpenMarkdownEditorToolTip());
            _openMarkdownButton.FontFamily = AppTypography.UiFontFamily;
            _openMarkdownButton.FontSize = 10.5;
            _openMarkdownButton.Click += (_, _) => OpenMarkdownInDefaultEditor();
            buttons.Children.Add(_openMarkdownButton);
        }

        _closeButton = IconButton("×", Strings.Get("ToolTipHideThisPaper"));
        _closeButton.FontSize = 16;
        _closeButton.Click += (_, _) =>
        {
            if (CanDisplayAsCapsule())
            {
                SetCollapsedState(true);
            }
            else
            {
                _controller.HidePaper(_paper);
            }
        };
        RefreshCloseButton();

        buttons.Children.Add(_newTodoButton);
        buttons.Children.Add(_newNoteButton);
        buttons.Children.Add(_closeButton);
        UpdateTopBarNewPaperButtons();

        Grid.SetColumn(buttons, 1);
        top.Children.Add(buttons);

        var topHost = new Border
        {
            Margin = new Thickness(0, 0, 0, 1.5),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(RadiusShell, RadiusShell, 0, 0),
            Child = top
        };
        topHost.SetResourceReference(Border.BackgroundProperty, "TitleBarBrushKey");
        topHost.SetResourceReference(Border.BorderBrushProperty, "TitleBarDividerBrushKey");
        topHost.MouseLeftButtonDown += (_, e) => BeginTitleBarDragGesture(topHost, e);
        topHost.PreviewMouseMove += (_, e) => UpdateTitleBarDragGesture(topHost, e);
        topHost.PreviewMouseLeftButtonUp += (_, _) => EndTitleBarDragGesture(topHost);
        topHost.LostMouseCapture += (_, _) => EndTitleBarDragGesture(topHost);

        Grid.SetRow(topHost, 0);
        _shell.Children.Add(topHost);
    }

    private void BeginNoteLinkMouseGesture(FrameworkElement handle, MouseButtonEventArgs e)
    {
        if (!_controller.State.EnableTodoNoteLinks || _paper.Type != PaperTypes.Note)
        {
            return;
        }

        _noteLinkDrag = new NoteLinkDragState(handle, PointToScreen(e.GetPosition(this)));
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void UpdateNoteLinkMouseGesture(MouseEventArgs e)
    {
        var state = _noteLinkDrag;
        if (state == null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // Only the explicit MouseUp handler may commit a link.
            EndNoteLinkMouseGesture(commit: false);
            e.Handled = true;
            return;
        }

        var currentScreenPoint = PointToScreen(e.GetPosition(this));
        if (!state.IsDragging)
        {
            var movedEnough =
                Math.Abs(currentScreenPoint.X - state.StartScreenPoint.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(currentScreenPoint.Y - state.StartScreenPoint.Y) >= SystemParameters.MinimumVerticalDragDistance;

            if (!movedEnough)
            {
                return;
            }

            state.IsDragging = true;
            state.Handle.Opacity = 0.82;
            Mouse.OverrideCursor = Cursors.Cross;
            ExitNoteEditor();
            _controller.BeginNoteLinkDrag(_paper);

            // Showing a top-level ghost can steal capture and re-enter LostMouseCapture → End,
            // which nulls Ghost on the same state object. Suppress teardown while establishing it.
            state.SuppressCaptureLossEnd = true;
            try
            {
                var ghost = CreateNoteLinkDragGhost();
                state.Ghost = ghost;
                ghost.Show();
                ghost.UpdateLayout();
                if (Mouse.LeftButton == MouseButtonState.Pressed &&
                    !state.Handle.IsMouseCaptured)
                {
                    state.Handle.CaptureMouse();
                }
            }
            catch
            {
                CloseNoteLinkDragGhost(state);
                EndNoteLinkMouseGesture(commit: false);
                e.Handled = true;
                return;
            }
            finally
            {
                state.SuppressCaptureLossEnd = false;
                if (_noteLinkDrag == state &&
                    Mouse.LeftButton == MouseButtonState.Pressed &&
                    !state.Handle.IsMouseCaptured)
                {
                    state.Handle.CaptureMouse();
                }
            }

            if (_noteLinkDrag != state || state.Ghost == null)
            {
                e.Handled = true;
                return;
            }
        }

        MoveNoteLinkDragGhost(state, currentScreenPoint);
        if (_noteLinkDrag == state)
        {
            _controller.UpdateNoteLinkDrag(_paper, currentScreenPoint);
        }
        e.Handled = true;
    }

    private void EndNoteLinkMouseGestureFromMouseUp(MouseButtonEventArgs e)
    {
        var state = _noteLinkDrag;
        if (state == null)
        {
            return;
        }

        EndNoteLinkMouseGesture(commit: state.IsDragging);
        e.Handled = true;
    }

    private void EndNoteLinkMouseGesture(bool commit)
    {
        var state = _noteLinkDrag;
        if (state == null)
        {
            return;
        }

        if (state.SuppressCaptureLossEnd)
        {
            return;
        }

        _noteLinkDrag = null;

        if (state.Handle.IsMouseCaptured)
        {
            state.Handle.ReleaseMouseCapture();
        }

        CloseNoteLinkDragGhost(state);
        state.Handle.Opacity = 1.0;
        Mouse.OverrideCursor = null;
        _controller.EndNoteLinkDrag(_paper, commit && state.IsDragging);
    }

    private Window CreateNoteLinkDragGhost()
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        stack.Children.Add(new TextBlock
        {
            Text = "✎",
            Foreground = TextBrush,
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        stack.Children.Add(new TextBlock
        {
            Text = _controller.PaperCapsuleTitle(_paper),
            Foreground = TextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });

        var root = new Border
        {
            Padding = new Thickness(9, 5, 10, 5),
            CornerRadius = new CornerRadius(RadiusControl),
            Background = PaperBrush,
            BorderBrush = NoteLinkTargetBorderBrush,
            BorderThickness = new Thickness(1),
            Opacity = 0.86,
            Child = stack,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 2,
                Opacity = 0.22
            }
        };

        return new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            IsHitTestVisible = false,
            Content = root
        };
    }

    private static void MoveNoteLinkDragGhost(NoteLinkDragState state, Point screenPoint)
    {
        // Capture a local reference: EndNoteLinkMouseGesture may null state.Ghost mid-call if a
        // nested capture-loss teardown runs while we are positioning the window.
        var ghost = state.Ghost;
        if (ghost == null)
        {
            return;
        }

        var mousePoint = screenPoint;
        var handle = state.Handle;
        if (handle.IsLoaded)
        {
            var source = PresentationSource.FromVisual(handle);
            if (source?.CompositionTarget != null)
            {
                mousePoint = source.CompositionTarget.TransformFromDevice.Transform(screenPoint);
            }
            else
            {
                var dpi = VisualTreeHelper.GetDpi(handle);
                if (dpi.DpiScaleX > 0 && dpi.DpiScaleY > 0)
                {
                    mousePoint = new Point(screenPoint.X / dpi.DpiScaleX, screenPoint.Y / dpi.DpiScaleY);
                }
            }
        }

        var width = ghost.ActualWidth > 1 ? ghost.ActualWidth : ghost.Width;
        var height = ghost.ActualHeight > 1 ? ghost.ActualHeight : ghost.Height;
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 1)
        {
            width = 120;
        }
        if (double.IsNaN(height) || double.IsInfinity(height) || height <= 1)
        {
            height = 28;
        }

        try
        {
            ghost.Left = mousePoint.X - (width / 2);
            ghost.Top = mousePoint.Y - (height / 2);
        }
        catch
        {
            // Ghost may have been closed by a nested gesture teardown.
        }
    }

    private static void CloseNoteLinkDragGhost(NoteLinkDragState state)
    {
        if (state.Ghost == null)
        {
            return;
        }

        try
        {
            state.Ghost.Close();
        }
        catch
        {
            // Drag feedback is disposable UI.
        }

        state.Ghost = null;
    }

    private void BuildBody()
    {
        UIElement body = _paper.Type == PaperTypes.Note ? BuildNoteBody() : BuildTodoBody();
        Grid.SetRow(body, 1);
        if (_paper.Type == PaperTypes.Note)
        {
            _noteBodyElement = body;
        }
        _shell.Children.Add(body);

        if (_paper.Type == PaperTypes.Note)
        {
            BuildTextZoomOverlay();
        }
    }

    private void BuildTextZoomOverlay()
    {
        var zoomHost = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 12, 7),
            Padding = new Thickness(6, 1, 6, 1),
            CornerRadius = new CornerRadius(RadiusControl),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = Strings.Get("ToolTipResetTextZoom"),
            Visibility = Visibility.Collapsed
        };

        _textZoomIndicator = new TextBlock
        {
            Foreground = WeakTextBrush,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center
        };

        zoomHost.Child = _textZoomIndicator;
        zoomHost.MouseEnter += (_, _) =>
        {
            zoomHost.Background = HoverBrush;
            _textZoomIndicator.Foreground = TextBrush;
            _textZoomIndicator.Opacity = 1.0;
        };
        zoomHost.MouseLeave += (_, _) =>
        {
            zoomHost.Background = Brushes.Transparent;
            _textZoomIndicator.Foreground = WeakTextBrush;
            _textZoomIndicator.Opacity = 0.55;
        };
        zoomHost.MouseLeftButtonUp += (_, e) =>
        {
            _controller.SetPaperTextZoom(_paper, 1.0);
            e.Handled = true;
        };

        Grid.SetRow(zoomHost, 1);
        Panel.SetZIndex(zoomHost, 20);
        _shell.Children.Add(zoomHost);
    }

    private ContextMenu BuildPaperContextMenu(bool forDeepCapsuleSlot = false)
    {
        var menu = CreateContextMenu();

        menu.Items.Add(MenuHeader(Strings.Get("MenuNew")));
        menu.Items.Add(MenuItem(Strings.Get("MenuNewTodoPaper"), (_, _) => _controller.CreatePaper(PaperTypes.Todo, show: true, _paper)));
        menu.Items.Add(MenuItem(Strings.Get("MenuNewNotePaper"), (_, _) => _controller.CreatePaper(PaperTypes.Note, show: true, _paper)));

        if (_paper.Type == PaperTypes.Todo)
        {
            menu.Items.Add(MenuSeparator());
            menu.Items.Add(MenuHeader(Strings.Get("MenuTodo")));
            menu.Items.Add(MenuItem(Strings.Get("MenuClearDone"), (_, _) => ClearDoneItems()));
        }

        menu.Items.Add(MenuSeparator());
        menu.Items.Add(MenuHeader(_controller.PaperCapsuleTitle(_paper)));

        if (CanDisplayAsCapsule())
        {
            var isScriptCapsule = IsScriptCapsule();
            if (isScriptCapsule && (_paper.IsCollapsed || forDeepCapsuleSlot))
            {
                menu.Items.Add(MenuItem(Strings.Get("ScriptCapsuleEditMenu"), (_, _) => OpenCapsuleForEditing()));
            }
            else if (_paper.IsCollapsed)
            {
                if (!forDeepCapsuleSlot)
                {
                    menu.Items.Add(MenuItem(
                        Strings.Get("MenuRestoreWindow"),
                        (_, _) => SetCollapsedState(false, activateOnExpand: true)));
                }
            }
            else
            {
                menu.Items.Add(MenuItem(Strings.Get("MenuCollapseToCapsule"), (_, _) => SetCollapsedState(true)));
            }
        }

        menu.Items.Add(MenuItem(Strings.Get("MenuHide"), (_, _) => _controller.HidePaper(_paper)));
        menu.Items.Add(MenuItem(Strings.Get("MenuDelete"), (_, _) => DeletePaperFromPaperMenu()));

        return menu;
    }

    private void ToggleTopmost()
    {
        _paper.AlwaysOnTop = !_paper.AlwaysOnTop;
        RefreshEffectiveTopmost();
        RefreshPaperIconButton();
        _controller.MarkDirty();
    }

    internal void RefreshEffectiveTopmost()
    {
        var shouldBeTopmost = _paper.AlwaysOnTop || (_controller.State.UseCapsuleMode && _paper.IsCollapsed);
        var effectiveTopmost = shouldBeTopmost && !_controller.SuppressTopmostForFullscreenForeground;
        Topmost = effectiveTopmost;
        if (IsVisible && (shouldBeTopmost || WindowNative.IsTopmost(this)))
        {
            WindowNative.ApplyTopmostZOrder(this, effectiveTopmost, _controller.FullscreenAvoidanceWindow);
        }

        RefreshDeepCapsuleSlotTopmost();
        if (_noteLinkDrag?.Ghost is { } noteLinkGhost)
        {
            var ghostTopmost = !_controller.SuppressTopmostForFullscreenForeground;
            noteLinkGhost.Topmost = ghostTopmost;
            if (noteLinkGhost.IsVisible)
            {
                WindowNative.ApplyTopmostZOrder(
                    noteLinkGhost,
                    ghostTopmost,
                    _controller.FullscreenAvoidanceWindow);
            }
        }
    }

    internal void RefreshDeepCapsuleSlotTopmost()
    {
        var slotShouldBeTopmost = !_controller.SuppressDeepCapsuleTopmostForContextMenu &&
            !_controller.SuppressTopmostForFullscreenForeground;
        _edgeCapsuleHost?.SetTopmost(
            slotShouldBeTopmost,
            _controller.FullscreenAvoidanceWindow);

        if (_deepCapsuleFloatingDragHost != null)
        {
            _deepCapsuleFloatingDragHost.Topmost = slotShouldBeTopmost;
            if (_deepCapsuleFloatingDragHost.IsVisible)
            {
                WindowNative.ApplyTopmostZOrder(_deepCapsuleFloatingDragHost, slotShouldBeTopmost, _controller.FullscreenAvoidanceWindow);
            }
        }
    }

    private void RefreshPaperIconButton()
    {
        if (_paperIconButton == null)
        {
            return;
        }

        _paperIconButton.ToolTip = _paper.AlwaysOnTop ? Strings.Get("Unpin") : Strings.Get("Pin");
        _paperIconButton.Content = CreateTopmostPinIcon(_paperIconButton, _paper.AlwaysOnTop);
        _paperIconButton.Opacity = 1.0;
        _paperIconButton.Foreground = _paper.AlwaysOnTop ? Theme.ActiveBrush : WeakTextBrush;
    }

    public void RefreshPaperTitle()
    {
        var title = _controller.PaperTitleText(_paper);
        Title = title;

        if (_titleText != null)
        {
            _titleText.Text = title;
            _titleText.ToolTip = Strings.Get("ToolTipEditTitle");
            _titleText.Foreground = TextBrush;
        }

        if (_titleEditBox != null)
        {
            _titleEditBox.Foreground = TextBrush;
            _titleEditBox.CaretBrush = TextBrush;
        }

        RefreshCapsuleLabel();
        RefreshPaperContextMenus();
    }

    private void BeginTitleEdit()
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            !_paper.IsVisible ||
            _titleText == null ||
            _titleEditBox == null)
        {
            return;
        }

        if (_isEditingTitle)
        {
            _titleEditBox.Focus();
            return;
        }

        if (!CanBeginTitleEditNow())
        {
            QueueTitleEditAfterWindowIsExpanded();
            return;
        }

        ExitNoteEditor();
        _isEditingTitle = true;
        _titleEditBox.Text = _controller.PaperTitleText(_paper);
        _titleText.Visibility = Visibility.Collapsed;
        _titleEditBox.Visibility = Visibility.Visible;
        _titleEditBox.Focus();
        _titleEditBox.SelectAll();
    }

    private bool IsTitleEditBoxEventSource(DependencyObject? source)
    {
        return _titleEditBox != null && IsDescendantOf(source, _titleEditBox);
    }

    private void CommitTitleEdit()
    {
        EndTitleEdit(commit: true);
    }

    private void EndTitleEdit(bool commit)
    {
        if (_titleText == null || _titleEditBox == null)
        {
            return;
        }

        if (!_isEditingTitle)
        {
            return;
        }

        var editedTitle = _titleEditBox.Text;
        _isEditingTitle = false;
        _titleEditBox.Visibility = Visibility.Collapsed;
        _titleText.Visibility = Visibility.Visible;

        if (commit)
        {
            _controller.UpdatePaperTitle(_paper, editedTitle);
        }
        else
        {
            RefreshPaperTitle();
        }
    }

    private bool CanBeginTitleEditNow()
    {
        return IsVisible &&
            !_paper.IsCollapsed &&
            !IsPaperFormTransitioning &&
            Width > DesiredCapsuleWindowWidth + 8 &&
            Height > PaperLayoutDefaults.CapsuleHeight + 8;
    }

    private void QueueTitleEditAfterWindowIsExpanded()
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive || !_paper.IsVisible)
        {
            return;
        }

        var generation = ++_titleEditIntentGeneration;
        if (_paper.IsCollapsed)
        {
            ExpandForProgrammaticOpen();
        }
        else
        {
            EnsureExpandedSurfaceGeometry(alignToDockedEdge: true);
        }

        var delay = Math.Max(ExpandAnimationMilliseconds, CollapseResizeMilliseconds) + 30;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(delay)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (generation != _titleEditIntentGeneration ||
                _windowLifecycle != PaperWindowLifecycleState.Alive ||
                IsClosed ||
                !_paper.IsVisible ||
                !CanBeginTitleEditNow())
            {
                return;
            }

            Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    if (generation == _titleEditIntentGeneration &&
                        _windowLifecycle == PaperWindowLifecycleState.Alive &&
                        _paper.IsVisible &&
                        CanBeginTitleEditNow())
                    {
                        BeginTitleEdit();
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Input);
        };
        timer.Start();
    }

    public void UpdateTopBarNewPaperButtons()
    {
        if (_newTodoButton != null)
        {
            _newTodoButton.Visibility = _controller.State.ShowTopBarNewTodoButton ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_newNoteButton != null)
        {
            _newNoteButton.Visibility = _controller.State.ShowTopBarNewNoteButton ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_openMarkdownButton != null)
        {
            _openMarkdownButton.Visibility = _controller.State.ShowTopBarExternalOpenButton ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ConfirmAndDeletePaper()
    {
        if (ShowDeletePaperDialog())
        {
            _controller.DeletePaper(_paper);
        }
    }

    private void DeletePaperFromPaperMenu()
    {
        if (_controller.IsPaperEmpty(_paper))
        {
            _controller.DeletePaper(_paper);
            return;
        }

        ConfirmAndDeletePaper();
    }

    private bool ShowDeletePaperDialog()
    {
        var dialog = new Window
        {
            Owner = this,
            Title = Strings.Get("DeletePaperTitle"),
            Width = 300,
            Height = 178,
            MinWidth = 300,
            MinHeight = 178,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = Topmost
        };

        var root = new Border
        {
            CornerRadius = new CornerRadius(RadiusShell),
            BorderBrush = PaperBorderBrush,
            BorderThickness = new Thickness(1),
            Background = PaperBrush,
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
            Text = Strings.Get("DeletePaperQuestion"),
            Foreground = TextBrush,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var message = new TextBlock
        {
            Text = Strings.Get("DeletePaperBody"),
            Foreground = WeakTextBrush,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var delete = DialogButton(Strings.Get("MenuDelete"), isDanger: true);
        delete.Click += (_, _) => dialog.DialogResult = true;

        buttons.Children.Add(delete);

        var cancel = DialogButton(Strings.Get("CommonCancel"), isDanger: false);
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(8, 0, 0, 0);
        cancel.Click += (_, _) => dialog.DialogResult = false;

        buttons.Children.Add(cancel);

        Grid.SetRow(title, 0);
        Grid.SetRow(message, 1);
        Grid.SetRow(buttons, 2);

        layout.Children.Add(title);
        layout.Children.Add(message);
        layout.Children.Add(buttons);

        root.Child = layout;
        dialog.Content = root;

        return dialog.ShowDialog() == true;
    }

    private static Button DialogButton(string text, bool isDanger)
    {
        var background = isDanger
            ? Theme.DangerBrush
            : Theme.Tint(28);

        var foreground = isDanger ? PaperBrush : TextBrush;
        var hover = isDanger
            ? Theme.DangerHoverBrush
            : Theme.Tint(46);

        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 7, 16, 7)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, background));
        style.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 72.0));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(RadiusControl));
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

        var mouseOver = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        mouseOver.Setters.Add(new Setter(Control.BackgroundProperty, hover));

        var pressed = new Trigger
        {
            Property = ButtonBase.IsPressedProperty,
            Value = true
        };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.82));

        template.Triggers.Add(mouseOver);
        template.Triggers.Add(pressed);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));

        return new Button
        {
            Content = text,
            Style = style
        };
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        EndNoteLinkMouseGesture(commit: false);
        if (_closeForReal)
        {
            CloseExpandedDeepCapsuleSlotHostForReal();
            return;
        }

        e.Cancel = true;
        _controller.HidePaper(_paper);
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_todoDrag != null && Mouse.LeftButton != MouseButtonState.Pressed)
        {
            EndTodoMouseDrag(commit: false);
        }
    }

    private static DependencyObject? GetSafeParent(DependencyObject current)
    {
        if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
        {
            return VisualTreeHelper.GetParent(current);
        }

        if (current is FrameworkContentElement fce)
        {
            return fce.Parent;
        }

        if (current is ContentElement ce)
        {
            return ContentOperations.GetParent(ce);
        }

        return null;
    }

    private static Button IconButton(string text, string tooltip)
    {
        return new Button
        {
            Content = text,
            ToolTip = tooltip,
            Width = 28,
            Height = 24,
            Margin = new Thickness(1, 0, 1, 0),
            Style = SharedIconButtonStyle
        };
    }

    private static FrameworkElement CreateTopmostPinIcon(Button owner, bool pinned)
    {
        var canvas = new Canvas
        {
            Width = 24,
            Height = 24,
            SnapsToDevicePixels = true
        };

        if (pinned)
        {
            var head = new WpfPath
            {
                Data = Geometry.Parse(PinOutlineHeadPathData),
                StrokeThickness = 2.15,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                SnapsToDevicePixels = true
            };
            head.SetBinding(System.Windows.Shapes.Shape.FillProperty, CreateForegroundBinding(owner));
            head.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, CreateForegroundBinding(owner));
            canvas.Children.Add(head);

            var needle = new WpfPath
            {
                Data = Geometry.Parse(PinNeedlePathData),
                SnapsToDevicePixels = true
            };
            needle.SetBinding(System.Windows.Shapes.Shape.FillProperty, CreateForegroundBinding(owner));
            canvas.Children.Add(needle);
        }
        else
        {
            var head = new WpfPath
            {
                Data = Geometry.Parse(PinOutlineHeadPathData),
                StrokeThickness = 2.15,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                SnapsToDevicePixels = true
            };
            head.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, CreateForegroundBinding(owner));
            canvas.Children.Add(head);

            var needle = new WpfPath
            {
                Data = Geometry.Parse(PinNeedlePathData),
                SnapsToDevicePixels = true
            };
            needle.SetBinding(System.Windows.Shapes.Shape.FillProperty, CreateForegroundBinding(owner));
            canvas.Children.Add(needle);
        }

        return new Viewbox
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Child = canvas
        };
    }

    private static System.Windows.Data.Binding CreateForegroundBinding(Button owner)
    {
        return new System.Windows.Data.Binding(nameof(Control.Foreground))
        {
            Source = owner
        };
    }

    private ContextMenu CreateContextMenu()
    {
        var menu = new ContextMenu
        {
            Padding = new Thickness(4, 4, 4, 4),
            FontFamily = AppTypography.UiFontFamily,
            Language = AppTypography.Language,
            FontSize = 13,
            HasDropShadow = true,
            Template = SharedContextMenuTemplate
        };
        UpdateContextMenuTheme(menu);
        menu.Opened += (_, _) =>
        {
            if (_controller.State.Theme == "system")
            {
                Theme.Invalidate();
            }

            UpdateContextMenuTheme(menu);
        };

        menu.Resources.Add(typeof(MenuItem), SharedCompactMenuItemStyle);
        RegisterThemedContextMenu(menu);
        return menu;
    }

    private void RegisterThemedContextMenu(ContextMenu menu)
    {
        for (var i = _themedContextMenus.Count - 1; i >= 0; i--)
        {
            if (!_themedContextMenus[i].TryGetTarget(out _))
            {
                _themedContextMenus.RemoveAt(i);
            }
        }

        _themedContextMenus.Add(new WeakReference<ContextMenu>(menu));
    }

    private void RefreshThemedContextMenus()
    {
        for (var i = _themedContextMenus.Count - 1; i >= 0; i--)
        {
            if (_themedContextMenus[i].TryGetTarget(out var menu))
            {
                UpdateContextMenuTheme(menu);
            }
            else
            {
                _themedContextMenus.RemoveAt(i);
            }
        }
    }

    private static void UpdateContextMenuTheme(ContextMenu menu)
    {
        menu.Resources["PaperBrushKey"] = PaperBrush;
        menu.Resources["PaperBorderBrushKey"] = PaperBorderBrush;
        menu.Resources["TextBrushKey"] = TextBrush;
        menu.Resources["WeakTextBrushKey"] = WeakTextBrush;
        menu.Resources["HoverBrushKey"] = HoverBrush;
        menu.Resources["MenuHoverBrushKey"] = MenuHoverBrush;
        menu.Background = PaperBrush;
        menu.BorderBrush = PaperBorderBrush;
        menu.Foreground = TextBrush;
    }

    private static Separator MenuSeparator()
    {
        return new Separator
        {
            Margin = new Thickness(8, 3, 8, 3),
            Opacity = 0.38
        };
    }

    private static MenuItem MenuHeader(string header)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = false,
            Padding = new Thickness(8, 2, 10, 2),
            Background = Brushes.Transparent,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        };
        item.SetResourceReference(Control.ForegroundProperty, "WeakTextBrushKey");
        return item;
    }

    private static MenuItem MenuItem(string header, RoutedEventHandler click)
    {
        var item = new MenuItem
        {
            Header = header,
            Padding = new Thickness(8, 4, 10, 4),
            Background = Brushes.Transparent
        };
        item.SetResourceReference(Control.ForegroundProperty, "TextBrushKey");
        item.Click += click;
        return item;
    }

    public static readonly DependencyProperty TransitionProgressProperty =
        DependencyProperty.Register(
            nameof(TransitionProgress),
            typeof(double),
            typeof(PaperWindow),
            new PropertyMetadata(0.0, OnTransitionProgressChanged));

    public double TransitionProgress
    {
        get => (double)GetValue(TransitionProgressProperty);
        set => SetValue(TransitionProgressProperty, value);
    }

    private static void OnTransitionProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PaperWindow window)
        {
            window.UpdateTransitionVisuals((double)e.NewValue);
        }
    }

    private void UpdateTransitionVisuals(double progress)
    {
        if (!IsPaperFormTransitioning)
        {
            return;
        }

        var currentProgress = double.IsNaN(progress) || double.IsInfinity(progress)
            ? 0.0
            : Math.Clamp(progress, 0.0, 1.0);

        var visualWidth = _startTransitionWidth + (_targetTransitionWidth - _startTransitionWidth) * currentProgress;
        var visualHeight = _startTransitionHeight + (_targetTransitionHeight - _startTransitionHeight) * currentProgress;
        var visualChromeWidth = Math.Max(1.0, visualWidth - WindowChromeInset);
        var visualChromeHeight = Math.Max(1.0, visualHeight - WindowChromeInset);
        var baseChromeWidth = Math.Max(1.0, _transitionBaseWidth - WindowChromeInset);
        var baseChromeHeight = Math.Max(1.0, _transitionBaseHeight - WindowChromeInset);

        _paperChrome.HorizontalAlignment = HorizontalAlignment.Left;
        _paperChrome.VerticalAlignment = VerticalAlignment.Top;
        _paperChrome.Width = visualChromeWidth;
        _paperChrome.Height = visualChromeHeight;
        _shellScale.ScaleX = Math.Max(0.01, visualChromeWidth / baseChromeWidth);
        _shellScale.ScaleY = Math.Max(0.01, visualChromeHeight / baseChromeHeight);
        UpdateTransitionCornerRadius(visualChromeWidth, visualChromeHeight, baseChromeWidth, baseChromeHeight);
    }

    private void ResetTransitionVisuals()
    {
        _paperChrome.Width = double.NaN;
        _paperChrome.Height = double.NaN;
        _paperChrome.HorizontalAlignment = HorizontalAlignment.Stretch;
        _paperChrome.VerticalAlignment = VerticalAlignment.Stretch;
        _shellScale.ScaleX = 1.0;
        _shellScale.ScaleY = 1.0;
        // Restore the full chrome presentation (corner radius, margin, shadow) for the
        // current form + snap state instead of just the corner radius.
        ApplyPaperChromePresentation();
    }

    private void UpdateTransitionCornerRadius(
        double visualChromeWidth,
        double visualChromeHeight,
        double baseChromeWidth,
        double baseChromeHeight)
    {
        var visualChromeMin = Math.Min(visualChromeWidth, visualChromeHeight);
        var expandedChromeMin = Math.Max(1.0, Math.Min(baseChromeWidth, baseChromeHeight));
        var capsuleChromeMin = Math.Max(
            1.0,
            Math.Min(
                PaperLayoutDefaults.CapsuleWidth - WindowChromeInset,
                PaperLayoutDefaults.CapsuleHeight - WindowChromeInset));
        var compactRange = Math.Max(1.0, expandedChromeMin - capsuleChromeMin);
        var compactness = Math.Clamp((expandedChromeMin - visualChromeMin) / compactRange, 0.0, 1.0);
        var compactVisualRadius = Math.Min(CapsuleChromeCornerRadius, visualChromeMin / 2.0);
        var desiredVisualRadius = ExpandedChromeCornerRadius + (compactVisualRadius - ExpandedChromeCornerRadius) * compactness;

        _paperChrome.CornerRadius = new CornerRadius(desiredVisualRadius);
    }

    private static CornerRadius PaperChromeCornerRadiusForState(bool collapsed)
    {
        return new CornerRadius(collapsed ? CapsuleChromeCornerRadius : ExpandedChromeCornerRadius);
    }

    private double CapsuleWindowWidth()
    {
        return Math.Max(CapsuleNormalMinWidth, CapsuleShellWidth() + WindowChromeInset);
    }

    private double CapsuleShellWidth()
    {
        return Math.Ceiling(
            CapsuleLeftPadding +
            MeasureCapsuleIconWidth() +
            CapsuleIconGap +
            MeasureCapsuleTitleWidth() +
            CapsuleNormalCloseWidth +
            CapsuleRightPadding);
    }

    // The pill window clamps to a minimum width (CapsuleWidth), so for short titles the pill is
    // wider than the raw content. The shell must always fill the pill interior, otherwise it is
    // left-aligned inside the pill and the close button's rounded right corner floats off the
    // pill's actual curve. Pill interior = window width minus the chrome margin on both sides.
    private double CapsuleShellLayoutWidth()
    {
        return Math.Max(CapsuleShellWidth(), CapsuleWindowWidth() - WindowChromeInset);
    }

    private double MeasureCapsuleTitleWidth(bool limitForDeepCapsule = false, double? pixelsPerDip = null)
    {
        var title = _controller.PaperCapsuleTitle(_paper);
        if (limitForDeepCapsule)
        {
            title = LimitTextElements(title, _controller.State.DeepCapsuleTitleMeasureCharacterLimit);
        }

        return MeasureCapsuleTextWidth(
            title,
            CapsuleLabelFontSize,
            FontWeights.Normal,
            AppTypography.UiFontFamily,
            pixelsPerDip);
    }

    private static string LimitTextElements(string text, int limit)
    {
        if (limit <= 0 || string.IsNullOrEmpty(text))
        {
            return text;
        }

        var indexes = StringInfo.ParseCombiningCharacters(text);
        return indexes.Length <= limit ? text : text[..indexes[limit]];
    }

    // The capsule icon glyph (✓ / ✎) is not a fixed box — its rendered advance width depends
    // on the font and weight. Measure it with the same SemiBold weight it renders at.
    private double MeasureCapsuleIconWidth(double? pixelsPerDip = null)
    {
        return MeasureCapsuleTextWidth(
            CapsuleIconText(),
            CapsuleIconFontSizeForCurrentPaper(),
            FontWeights.SemiBold,
            AppTypography.SymbolFontFamily,
            pixelsPerDip);
    }

    // Single source of truth for "how wide does this text actually render". Uses the same
    // font family and weight the capsule icon/label are bound to, so
    // measurement and rendering never disagree — digits and halfwidth chars get their true
    // advance width.
    private double MeasureCapsuleTextWidth(
        string text,
        double fontSize,
        FontWeight weight,
        FontFamily fontFamily,
        double? pixelsPerDip = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        try
        {
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(fontFamily, FontStyles.Normal, weight, FontStretches.Normal),
                fontSize,
                WeakTextBrush,
                pixelsPerDip ?? VisualTreeHelper.GetDpi(this).PixelsPerDip);
            return formatted.WidthIncludingTrailingWhitespace;
        }
        catch
        {
            return text.Length * fontSize;
        }
    }

    private double RoundToDevicePixelX(double value)
    {
        return RoundToDevicePixel(value, VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    private double RoundToDevicePixelY(double value)
    {
        return RoundToDevicePixel(value, VisualTreeHelper.GetDpi(this).DpiScaleY);
    }

    private static double RoundToDevicePixel(double value, double scale)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || scale <= 0)
        {
            return value;
        }

        return Math.Round(value * scale, MidpointRounding.AwayFromZero) / scale;
    }

    private void SaveGeometryIfAllowed()
    {
        if (IsPaperFormTransitioning || SuppressGeometrySave)
        {
            return;
        }

        if (_isSnappedPresentation && !_paper.IsCollapsed)
        {
            return;
        }

        _controller.UpdateGeometry(_paper, this);
    }

    internal void SaveGeometryForCurrentPresentation()
    {
        SaveGeometryIfAllowed();
    }

    internal void HideWithoutGeometrySave()
    {
        MoveWindowWithoutGeometrySave(Hide);
    }

    private void MoveWindowWithoutGeometrySave(Action move)
    {
        var wasSuppressing = _suppressGeometrySave;
        _suppressGeometrySave = true;
        try
        {
            move();
        }
        finally
        {
            _suppressGeometrySave = wasSuppressing;
        }
    }

}
