using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private string? _pendingFocusItemId;
    private readonly Dictionary<string, TodoTextBox> _todoEditors = new();
    private readonly List<Border> _todoRows = new();
    private TodoDragState? _todoDrag;
    private NoteLinkDragState? _noteLinkDrag;
    private MarkdownTextBox? _noteBox;
    private Action? _showNotePreview;
    private readonly List<List<PaperItem>> _undoStack = new();
    private readonly List<List<PaperItem>> _redoStack = new();
    private const int MaxUndoDepth = 100;
    private string? _activeOriginalItemId;
    private string? _activeOriginalText;
    private bool _suppressTodoBackspaceUntilKeyUp;
    private bool _isApplyingCollapsedState;
    private Button? _closeButton;
    private Grid _capsuleShell = null!;
    private Window? _deepCapsuleSlotHost;
    private Grid? _deepCapsuleSlotHostRoot;
    private ScaleTransform? _deepCapsuleSlotDragScale;
    private Border? _deepCapsuleSlotChrome;
    private Border? _deepCapsuleSlotOutline;
    private Grid? _deepCapsuleSlotShell;
    private Border? _deepCapsuleSlotLeftArea;
    private Grid? _deepCapsuleSlotLeftStack;
    private TextBlock? _deepCapsuleSlotIconText;
    private Border? _deepCapsuleSlotCloseArea;
    private TextBlock? _deepCapsuleSlotCloseGlyph;
    private TranslateTransform? _deepCapsuleSlotCloseGlyphOffset;
    private TextBlock? _deepCapsuleSlotLabelText;
    private DeepCapsuleEdge? _appliedSlotEdge;
    private ContextMenu? _deepCapsuleSlotContextMenu;
    private bool _deepCapsuleSlotContextMenuOpen;
    private IntPtr _deepCapsuleForegroundHook;
    private IntPtr _deepCapsuleMouseHook;
    private WinEventDelegate? _deepCapsuleForegroundHookProc;
    private LowLevelMouseProc? _deepCapsuleMouseHookProc;
    private Border? _capsuleCloseArea;
    private TextBlock? _capsuleIconText;
    private TextBlock? _capsuleCloseGlyph;
    private TranslateTransform? _capsuleCloseGlyphOffset;
    private TextBlock _capsuleLabelText = null!;
    private bool _isMaybeDragging;
    private Point _mouseDownScreenPos;
    private bool _suppressGeometrySave;
    private DeepCapsuleSlotState _deepCapsuleSlotState = DeepCapsuleSlotState.None;
    private DeepCapsuleVisualState _deepCapsuleVisualState = DeepCapsuleVisualState.Resting;
    private DeepCapsuleGestureState _deepCapsuleGestureState = DeepCapsuleGestureState.Idle;
    private DeepCapsuleOpenOrigin _deepCapsuleOpenOrigin = DeepCapsuleOpenOrigin.Normal;
    private bool _isCollapseAllRetracted;
    private double _deepCapsuleDragMouseOffsetY;
    private double _deepCapsuleDragLeft;
    private bool _deepCapsuleCrossQueueDragUnlocked;
    private bool _deepCapsuleCrossQueueDragVisualActive;
    private double _deepCapsuleCrossQueueDragWidth = PaperLayoutDefaults.CapsuleHeight;
    private Point _deepCapsuleDragStartDip;
    // Cross-edge/monitor drag: cursor offset inside the pill, last DIP point for layout/drop
    // ordering, and last raw PointToScreen point for Win32 monitor resolution.
    private double _deepCapsuleDragMouseOffsetX;
    private Point _deepCapsuleDragLastDip;
    private Point _deepCapsuleDragLastScreenPos;
    private double _deepCapsuleSlotLeft;
    private double _deepCapsuleSlotTop;
    private int _deepCapsuleIndex = -1;
    // Visual slot shift: when the "collapse-all" master capsule occupies slot 0, real
    // capsules render at slot index+offset while _deepCapsuleIndex stays the paper-list index.
    private int _deepCapsuleVisualOffset;
    // Total visual slots in this capsule's queue, including the optional master slot. Top
    // clamping must use the whole stack; clamping per capsule makes lower slots collapse together.
    private int _deepCapsuleSlotCount = 1;
    // Monotonic tokens guarding superseded animations; a stale Completed handler bails when its
    // captured value no longer matches.
    private int _deepCapsuleSlotMoveGeneration;
    private int _collapseTransitionGeneration;
    private double _deepCapsuleSlotTargetLeft;
    private double _deepCapsuleSlotStartViewportWidth;
    private double _deepCapsuleSlotTargetViewportWidth;
    private Point _deepCapsuleSlotMouseDownScreenPos;
    private double _startTransitionWidth;
    private double _startTransitionHeight;
    private double _targetTransitionWidth;
    private double _targetTransitionHeight;
    private double _transitionBaseWidth;
    private double _transitionBaseHeight;
    private bool _isTransitionVisualsActive;
    private bool _isEditingTitle;
    private bool _pendingTitleEdit;
    private int _themeAnimationGeneration;
    private int _clearDoneGeneration;
    private int _todoRowsGeneration;
    private const double DeepCapsuleHoverOutsideOffset = DeepCapsuleLayout.HoverOutsideOffset;
    private const double DeepCapsuleExpandedEdgeInset = DeepCapsuleLayout.ExpandedEdgeInset;
    private const double DeepCapsuleTopMargin = DeepCapsuleLayout.TopMargin;
    private const double DeepCapsuleStartTopMargin = DeepCapsuleLayout.StartTopMargin;
    private const double DeepCapsuleGap = DeepCapsuleLayout.Gap;
    private const double WindowChromeMargin = 8;
    private const double WindowChromeInset = WindowChromeMargin * 2;
    private const double TitleBarHeight = PaperLayoutDefaults.TopBarHeight;
    private const int CollapseShellFadeMilliseconds = 70;
    private const int CollapseResizeMilliseconds = 150;
    private const int ExpandAnimationMilliseconds = 220;
    // Expand cross-fade: the capsule pill fades out first, then the paper shell fades in after it.
    private const int ExpandCapsuleFadeOutMilliseconds = 80;
    private const int ExpandShellFadeInMilliseconds = 140;
    private const double ExpandedChromeCornerRadius = RadiusShell;
    private const double CapsuleChromeCornerRadius = DeepCapsuleLayout.CornerRadius; // 胶囊圆角，自成一套，不纳入圆角阶梯
    private const double CapsuleInnerCornerRadius = DeepCapsuleLayout.CornerRadius;   // 左区 / 关闭按钮的内圆角，与药丸外圆角同档

    // 胶囊态内部度量。布局（leftStack/标签）与宽度计算（CapsuleShellWidth）共用同一组值，
    // 否则二者不一致会让壳体与内容错位。整体偏紧凑，减少图标/文字四周的死白。
    private const double CapsuleNormalMinWidth = 76;
    private const double CapsuleLeftPadding = 6;
    private const double CapsuleIconGap = 4;
    private const double CapsuleCloseWidth = 30;
    private const double CapsuleNormalCloseWidth = 21;
    private const double CapsuleRightPadding = 6;
    private const double CapsuleIconFontSize = 13;
    private const double CapsuleLabelFontSize = 11;
    private const double CapsuleCloseGlyphDeepOffset = -8;
    private const double CapsuleCloseGlyphNormalOffset = -1;
    private const double DeepCapsuleSlotOutlineThickness = 2;
    private const double DeepCapsuleSlotOutlineOverlap = 1;
    private const double DeepCapsuleReorderDragExtraThreshold = 4;
    private const double DeepCapsuleCrossQueueDragUnlockDistance = 56;
    private const double DeepCapsuleCrossQueueDragScaleFrom = 0.97;
    private const int DeepCapsuleCrossQueueDragMorphMilliseconds = 90;
    // Half-hidden (peek) cut-off, measured from the END of the title. Negative pulls the cut
    // INTO the title so the last glyph is roughly half-covered — the capsule reads as clearly
    // tucked away at the edge, yet enough text shows to identify it. ~half a CJK glyph at 11px,
    // less 1px so a sliver of breathing room shows to the right of the text.
    private const double CapsulePeekRightGap = -5;

    // 圆角阶梯：所有元素只从这四档取值，避免散落的随手圆角。
    // 小元素（勾选框）/ 控件（按钮、徽标、行）/ 块（菜单、面板）/ 外壳（纸片、顶栏）。
    private const double RadiusSmall = 4;
    private const double RadiusControl = 8;
    private const double RadiusBlock = 12;
    private const double RadiusShell = 16;
    private static readonly object NoteRenderTraceLock = new();

    public bool IsDeepCapsulePlaced => _paper.IsCollapsed && HasDeepCapsuleSlotPlacement;
    public bool IsDeepCapsuleSlotVisible => _deepCapsuleSlotHost?.IsVisible == true;
    public bool HasVisibleSurface => IsVisible || IsDeepCapsuleSlotVisible;
    public bool IsCollapseAllRetracted => _isCollapseAllRetracted;
    public bool HasExpandedDeepCapsuleSlotReservation => _deepCapsuleSlotState is DeepCapsuleSlotState.ExpandedReserved or DeepCapsuleSlotState.Retracting;
    public bool OccupiesDeepCapsuleSlot => _paper.IsVisible && (_paper.IsCollapsed || _deepCapsuleSlotState == DeepCapsuleSlotState.ExpandedReserved);
    public bool SuppressGeometrySave => _suppressGeometrySave;
    // Ordinary collapsed capsules are the main PaperWindow and should still save X/Y.
    // Deep capsules use the slot-host window for docked geometry, so the hidden/parked
    // main window must not overwrite ordinary paper geometry.
    public bool UsesNonPaperGeometry => _paper.IsCollapsed && HasDeepCapsuleSlotPlacement;
    public double DesiredCapsuleWindowWidth => CapsuleWindowWidth();
    public double DeepCapsuleRestingVisibleWidth => _deepCapsuleSlotState == DeepCapsuleSlotState.ExpandedReserved
        ? ExpandedDeepCapsuleVisibleWidth()
        : DeepCapsuleVisibleWidth();

    private enum TodoFocusPlacement
    {
        End,
        Start
    }

    // ── Deep-capsule state machine (currently implicit; transitions are scattered across
    // SetCollapsedState, Apply*/Clear*DeepCapsule*, ArrangeDeepCapsules). Documented here until
    // it can be extracted into a dedicated presenter that owns these transitions centrally.
    //
    // Four orthogonal axes track one docked capsule:
    //
    //   SlotState   — does this paper occupy/reserve an edge slot, and is its slot-host window live?
    //       None            : not in the stack; slot-host hidden.
    //       CollapsedDocked : paper is collapsed and shown as an edge pill (slot-host visible).
    //       ExpandedReserved: paper is expanded but still holds its slot (ShowDeepCapsuleWhileExpanded).
    //       Retracting      : transient — slot-host is animating out before going None.
    //     Legal: None⇄CollapsedDocked, None⇄ExpandedReserved, CollapsedDocked⇄ExpandedReserved,
    //            (CollapsedDocked|ExpandedReserved)→Retracting→None.
    //     Invariant: paper not visible ⇒ SlotState must reach None (slot-host hidden). The
    //                single correct teardown is DetachFromDeepCapsuleStack().
    //
    //   VisualState — resting tag / hover-peek / fully-revealed (active). Independent of SlotState.
    //   GestureState— pointer interaction: Idle / PendingClick / Reordering (edge-locked reorder or cross-queue drag).
    //   OpenOrigin  — whether the expanded window came from an edge slot (affects re-dock on collapse).
    private enum DeepCapsuleSlotState
    {
        None,
        CollapsedDocked,
        ExpandedReserved,
        Retracting
    }

    private enum DeepCapsuleVisualState
    {
        Resting,
        Hovered,
        Active
    }

    private enum DeepCapsuleGestureState
    {
        Idle,
        PendingClick,
        Reordering
    }

    private enum DeepCapsuleOpenOrigin
    {
        Normal,
        EdgeSlot
    }

    private bool HasDeepCapsuleSlotPlacement => _deepCapsuleSlotState != DeepCapsuleSlotState.None;
    private bool HoldsDeepCapsuleSlotWhileExpanded => _deepCapsuleSlotState == DeepCapsuleSlotState.ExpandedReserved;
    private bool IsDeepCapsuleSlotRetracting => _deepCapsuleSlotState == DeepCapsuleSlotState.Retracting;
    private bool IsDeepCapsuleHovered => _deepCapsuleVisualState == DeepCapsuleVisualState.Hovered;
    private bool IsDeepCapsuleSlotActive => _deepCapsuleVisualState == DeepCapsuleVisualState.Active;
    private bool IsDeepCapsuleSlotPendingClick => _deepCapsuleGestureState == DeepCapsuleGestureState.PendingClick;
    private bool IsDeepCapsuleReordering => _deepCapsuleGestureState == DeepCapsuleGestureState.Reordering;
    private bool ExpandedFromDeepCapsuleEdge => _deepCapsuleOpenOrigin == DeepCapsuleOpenOrigin.EdgeSlot;

    private void SetDeepCapsuleSlotState(DeepCapsuleSlotState state) => _deepCapsuleSlotState = state;
    private void SetDeepCapsuleVisualState(DeepCapsuleVisualState state) => _deepCapsuleVisualState = state;
    private void SetDeepCapsuleGestureState(DeepCapsuleGestureState state) => _deepCapsuleGestureState = state;
    private void SetDeepCapsuleOpenOrigin(DeepCapsuleOpenOrigin origin) => _deepCapsuleOpenOrigin = origin;

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
    private static Brush LinkedNoteBgBrush => Theme.Tint((byte)(Theme.IsDark ? 28 : 18));
    private static Brush LinkedNoteHoverBgBrush => Theme.Tint((byte)(Theme.IsDark ? 48 : 34));

    private static Brush CheckBoxBorderBrush => Theme.CheckBoxBorderBrush;

    private static Brush TrashBgBrush => Theme.Danger((byte)(Theme.IsDark ? 16 : 12));
    private static Brush TrashBorderBrush => Theme.Danger(50);
    private static Brush TrashTextBrush => Theme.DangerBrush;
    private static Brush TrashHoverBgBrush => Theme.Danger((byte)(Theme.IsDark ? 32 : 26));
    private static Brush TrashHoverBorderBrush => Theme.DangerBrush;

    private static Brush TitleBarBrush => Theme.Tint((byte)(Theme.IsDark ? 18 : 12));
    private static Brush TitleBarDividerBrush => Theme.Tint((byte)(Theme.IsDark ? 34 : 28));
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

        ConfigureWindow();
        BuildShell();
        UpdateToolTipSetting();

        Loaded += (_, _) => SaveGeometryIfAllowed();
        LocationChanged += (_, _) => SaveGeometryIfAllowed();
        SizeChanged += (_, _) => SaveGeometryIfAllowed();
        PreviewMouseMove += OnWindowPreviewMouseMove;
        PreviewMouseWheel += OnWindowPreviewMouseWheel;
        PreviewMouseLeftButtonUp += OnWindowPreviewMouseLeftButtonUp;
        LostMouseCapture += OnLostMouseCapture;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        PreviewKeyUp += OnWindowPreviewKeyUp;
        Activated += (_, _) => _controller.RefreshFloatingSurfaceZOrder();
        Deactivated += (_, _) =>
        {
            if (_todoDrag != null)
            {
                EndTodoMouseDrag(commit: false);
            }

            if (_noteLinkDrag != null)
            {
                EndNoteLinkMouseGesture(commit: false);
            }
        };
        Closing += OnClosing;

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

    public void CloseForReal()
    {
        CloseExpandedDeepCapsuleSlotHostForReal();
        _closeForReal = true;
        Close();
    }

    public void UpdateToolTipSetting()
    {
        ToolTipPreferences.Apply(this, _controller.State.EnableToolTips);
        ToolTipPreferences.Apply(_deepCapsuleSlotHost, _controller.State.EnableToolTips);
    }

    public void CancelPendingVisibilityTransitions()
    {
        BeginAnimation(Window.OpacityProperty, null);
        if (!_isCollapseAllRetracted)
        {
            Opacity = 1.0;
        }

        _deepCapsuleSlotMoveGeneration++;
        if (_deepCapsuleSlotHost != null)
        {
            _deepCapsuleSlotHost.BeginAnimation(Window.OpacityProperty, null);
            _deepCapsuleSlotHost.Opacity = 1.0;
        }

        if (_deepCapsuleSlotHostRoot != null)
        {
            _deepCapsuleSlotHostRoot.BeginAnimation(UIElement.OpacityProperty, null);
            _deepCapsuleSlotHostRoot.Opacity = 1.0;
        }

        if (IsDeepCapsuleSlotRetracting)
        {
            SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            if (!_paper.IsCollapsed &&
                _controller.State.UseCapsuleMode &&
                _controller.State.UseDeepCapsuleMode &&
                _deepCapsuleSlotHost?.IsVisible == true)
            {
                SetDeepCapsuleSlotState(DeepCapsuleSlotState.ExpandedReserved);
                UpdateDeepCapsuleSlotClosePlacement();
            }
        }
    }

    private void ConfigureWindow()
    {
        InitializeThemeResources();
        Title = _controller.PaperTitleText(_paper);
        ShowInTaskbar = false;
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
        FontFamily = new FontFamily("Segoe UI");
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
            Effect = new DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 2,
                Opacity = 0.18
            }
        };
        _paperChrome.SetResourceReference(Border.BackgroundProperty, "PaperBrushKey");
        _paperChrome.SetResourceReference(Border.BorderBrushProperty, "PaperBorderBrushKey");

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

        top.PreviewMouseLeftButtonDown += (_, _) => ExitNoteEditor();
        top.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && e.ClickCount == 1)
            {
                try { DragMove(); } catch { }
            }
        };

        var titleArea = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        titleArea.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _paperIconButton = IconButton(_paper.Type == PaperTypes.Note ? "✎" : "☑", _paper.AlwaysOnTop ? Strings.Get("Unpin") : Strings.Get("Pin"));
        _paperIconButton.Width = 23;
        _paperIconButton.FontSize = _paper.Type == PaperTypes.Note ? 15 : 13;
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
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(4, 1, 5, 1),
            CornerRadius = new CornerRadius(RadiusControl),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = Brushes.Transparent,
            Cursor = Cursors.IBeam,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 38,
            MaxWidth = 86,
            ToolTip = Strings.Get("ToolTipEditTitle")
        };
        titleHost.SetResourceReference(Border.BorderBrushProperty, "TitleBarDividerBrushKey");

        var titleEditLayer = new Grid
        {
            MinWidth = 30,
            MaxWidth = 76
        };

        _titleText = new TextBlock
        {
            Foreground = TextBrush,
            FontSize = 11,
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
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            MaxLength = PaperTitles.MaxTitleLength,
            // MaxLength is only a coarse UTF-16 guard; the real title limit is applied on commit
            // so IME composition is never interrupted by rewriting TextBox.Text mid-edit.
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            FocusVisualStyle = null
        };
        _titleEditBox.PreviewMouseLeftButtonDown += (_, e) => e.Handled = false;
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
            _linkNoteButton.LostMouseCapture += (_, _) => EndNoteLinkMouseGesture(commit: false);
            buttons.Children.Add(_linkNoteButton);

            _openMarkdownButton = IconButton(ExternalOpenButtonLabel(), OpenMarkdownEditorToolTip());
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
            EndNoteLinkMouseGesture(commit: state.IsDragging);
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
            state.Ghost = CreateNoteLinkDragGhost();
            state.Ghost.Show();
            state.Ghost.UpdateLayout();
        }

        MoveNoteLinkDragGhost(state, currentScreenPoint);
        _controller.UpdateNoteLinkDrag(_paper, currentScreenPoint);
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
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        stack.Children.Add(new TextBlock
        {
            Text = _controller.PaperCapsuleTitle(_paper),
            Foreground = TextBrush,
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
        if (state.Ghost == null)
        {
            return;
        }

        var mousePoint = screenPoint;
        var source = PresentationSource.FromVisual(state.Handle);
        if (source?.CompositionTarget != null)
        {
            mousePoint = source.CompositionTarget.TransformFromDevice.Transform(screenPoint);
        }
        else
        {
            var dpi = VisualTreeHelper.GetDpi(state.Handle);
            if (dpi.DpiScaleX > 0 && dpi.DpiScaleY > 0)
            {
                mousePoint = new Point(screenPoint.X / dpi.DpiScaleX, screenPoint.Y / dpi.DpiScaleY);
            }
        }

        var width = state.Ghost.ActualWidth > 1 ? state.Ghost.ActualWidth : state.Ghost.Width;
        var height = state.Ghost.ActualHeight > 1 ? state.Ghost.ActualHeight : state.Ghost.Height;
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 1)
        {
            width = 120;
        }
        if (double.IsNaN(height) || double.IsInfinity(height) || height <= 1)
        {
            height = 28;
        }

        state.Ghost.Left = mousePoint.X - (width / 2);
        state.Ghost.Top = mousePoint.Y - (height / 2);
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
                    menu.Items.Add(MenuItem(Strings.Get("MenuRestoreWindow"), (_, _) => SetCollapsedState(false)));
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
        if (IsVisible && shouldBeTopmost)
        {
            WindowNative.ApplyTopmostZOrder(this, effectiveTopmost, _controller.FullscreenAvoidanceWindow);
        }

        RefreshDeepCapsuleSlotTopmost();
    }

    internal void RefreshDeepCapsuleSlotTopmost()
    {
        if (_deepCapsuleSlotHost != null)
        {
            var slotShouldBeTopmost = !_controller.SuppressDeepCapsuleTopmostForContextMenu &&
                !_controller.SuppressTopmostForFullscreenForeground;
            _deepCapsuleSlotHost.Topmost = slotShouldBeTopmost;
            if (_deepCapsuleSlotHost.IsVisible)
            {
                WindowNative.ApplyTopmostZOrder(_deepCapsuleSlotHost, slotShouldBeTopmost, _controller.FullscreenAvoidanceWindow);
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
        _paperIconButton.Opacity = _paper.AlwaysOnTop ? 1.0 : 0.58;
        _paperIconButton.Foreground = _paper.AlwaysOnTop ? TextBrush : WeakTextBrush;
        _paperIconButton.FontWeight = _paper.AlwaysOnTop ? FontWeights.SemiBold : FontWeights.Normal;
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
        if (_capsuleLeftArea != null)
        {
            _capsuleLeftArea.ContextMenu = BuildPaperContextMenu();
        }
        if (_paperChrome != null)
        {
            _paperChrome.ContextMenu = BuildPaperContextMenu();
        }
    }

    private void RequestTitleEdit()
    {
        QueueTitleEditAfterWindowIsExpanded();
    }

    private void BeginTitleEdit()
    {
        if (_titleText == null || _titleEditBox == null)
        {
            return;
        }

        if (_isEditingTitle)
        {
            _titleEditBox.Focus();
            _titleEditBox.SelectAll();
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
            !_isApplyingCollapsedState &&
            !_isTransitionVisualsActive &&
            Width > DesiredCapsuleWindowWidth + 8 &&
            Height > PaperLayoutDefaults.CapsuleHeight + 8;
    }

    private void QueueTitleEditAfterWindowIsExpanded()
    {
        if (_pendingTitleEdit)
        {
            return;
        }

        _pendingTitleEdit = true;
        if (_paper.IsCollapsed || !IsVisible)
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
            _pendingTitleEdit = false;
            Dispatcher.BeginInvoke((Action)BeginTitleEdit, System.Windows.Threading.DispatcherPriority.Input);
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

    private static ContextMenu CreateContextMenu()
    {
        var menu = new ContextMenu
        {
            Padding = new Thickness(4, 4, 4, 4),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            HasDropShadow = true,
            Template = SharedContextMenuTemplate
        };
        menu.Resources["PaperBrushKey"] = PaperBrush;
        menu.Resources["PaperBorderBrushKey"] = PaperBorderBrush;
        menu.Resources["TextBrushKey"] = TextBrush;
        menu.Resources["WeakTextBrushKey"] = WeakTextBrush;
        menu.Resources["HoverBrushKey"] = HoverBrush;
        menu.Resources["MenuHoverBrushKey"] = MenuHoverBrush;
        menu.Background = PaperBrush;
        menu.BorderBrush = PaperBorderBrush;
        menu.Foreground = TextBrush;

        menu.Resources.Add(typeof(MenuItem), SharedCompactMenuItemStyle);
        return menu;
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

    private static readonly DependencyProperty DeepCapsuleSlotHorizontalProgressProperty =
        DependencyProperty.Register(
            nameof(DeepCapsuleSlotHorizontalProgress),
            typeof(double),
            typeof(PaperWindow),
            new PropertyMetadata(double.NaN, OnDeepCapsuleSlotHorizontalProgressChanged));

    private double DeepCapsuleSlotHorizontalProgress
    {
        get => (double)GetValue(DeepCapsuleSlotHorizontalProgressProperty);
        set => SetValue(DeepCapsuleSlotHorizontalProgressProperty, value);
    }

    private static void OnTransitionProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PaperWindow window)
        {
            window.UpdateTransitionVisuals((double)e.NewValue);
        }
    }

    private static void OnDeepCapsuleSlotHorizontalProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PaperWindow window || e.NewValue is not double progress || double.IsNaN(progress) || double.IsInfinity(progress))
        {
            return;
        }

        window.ApplyDeepCapsuleSlotHorizontalProgress(progress);
    }

    private void UpdateTransitionVisuals(double progress)
    {
        if (!_isTransitionVisualsActive)
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
        _isTransitionVisualsActive = false;
        _paperChrome.Width = double.NaN;
        _paperChrome.Height = double.NaN;
        _paperChrome.HorizontalAlignment = HorizontalAlignment.Stretch;
        _paperChrome.VerticalAlignment = VerticalAlignment.Stretch;
        _shellScale.ScaleX = 1.0;
        _shellScale.ScaleY = 1.0;
        _paperChrome.CornerRadius = PaperChromeCornerRadiusForState(_paper.IsCollapsed && _controller.State.UseCapsuleMode);
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
        return CapsuleWindowWidth(UsesDeepCapsulePresentation);
    }

    private double CapsuleWindowWidth(bool usesDeepCapsulePresentation)
    {
        var minWidth = usesDeepCapsulePresentation ? PaperLayoutDefaults.CapsuleWidth : CapsuleNormalMinWidth;
        return Math.Max(minWidth, CapsuleShellWidth(usesDeepCapsulePresentation) + WindowChromeInset);
    }

    private double CapsuleShellWidth()
    {
        return CapsuleShellWidth(UsesDeepCapsulePresentation);
    }

    private double CapsuleShellWidth(bool usesDeepCapsulePresentation)
    {
        return Math.Ceiling(CapsuleLeftPadding + MeasureCapsuleIconWidth() + CapsuleIconGap + MeasureCapsuleTitleWidth() + CapsuleCloseWidthForPlacement(usesDeepCapsulePresentation) + CapsuleRightPadding);
    }

    private double CapsuleCloseWidthForCurrentPlacement()
    {
        return CapsuleCloseWidthForPlacement(UsesDeepCapsulePresentation);
    }

    private static double CapsuleCloseWidthForPlacement(bool usesDeepCapsulePresentation)
    {
        return usesDeepCapsulePresentation ? CapsuleCloseWidth : CapsuleNormalCloseWidth;
    }

    private bool UsesDeepCapsulePresentation => false;

    // The pill window clamps to a minimum width (CapsuleWidth), so for short titles the pill is
    // wider than the raw content. The shell must always fill the pill interior, otherwise it is
    // left-aligned inside the pill and the close button's rounded right corner floats off the
    // pill's actual curve. Pill interior = window width minus the chrome margin on both sides.
    private double CapsuleShellLayoutWidth()
    {
        return CapsuleShellLayoutWidth(UsesDeepCapsulePresentation);
    }

    private double CapsuleShellLayoutWidth(bool usesDeepCapsulePresentation)
    {
        return Math.Max(CapsuleShellWidth(usesDeepCapsulePresentation), CapsuleWindowWidth(usesDeepCapsulePresentation) - WindowChromeInset);
    }

    private double MeasureCapsuleTitleWidth()
    {
        return MeasureCapsuleTextWidth(_controller.PaperCapsuleTitle(_paper), CapsuleLabelFontSize, FontWeights.Normal);
    }

    // The capsule icon glyph (✓ / ✎) is not a fixed box — its rendered advance width depends
    // on the font and weight. Measure it with the same SemiBold weight it renders at.
    private double MeasureCapsuleIconWidth()
    {
        return MeasureCapsuleTextWidth(CapsuleIconText(), CapsuleIconFontSizeForCurrentPaper(), FontWeights.SemiBold);
    }

    // Single source of truth for "how wide does this text actually render". Uses the same
    // font family (NoteTypography) and weight the capsule icon/label are bound to, so
    // measurement and rendering never disagree — digits and halfwidth chars get their true
    // advance width.
    private double MeasureCapsuleTextWidth(string text, double fontSize, FontWeight weight)
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
                new Typeface(NoteTypography.FontFamily, FontStyles.Normal, weight, FontStretches.Normal),
                fontSize,
                WeakTextBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
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
        if (_isApplyingCollapsedState || SuppressGeometrySave)
        {
            return;
        }

        _controller.UpdateGeometry(_paper, this);
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

    private void AnimateWindowOpacity(double to, bool animate)
    {
        if (!animate || Math.Abs(Opacity - to) < 0.001)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = to;
            return;
        }

        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = Opacity,
            To = to,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        anim.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = to;
        };
        BeginAnimation(OpacityProperty, anim);
    }
}
