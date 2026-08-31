using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PaperTodo;

public static class AnimationHelper
{
    public static readonly IEasingFunction SmoothEase = new CubicEase { EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction QuickEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction SnapEase = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };

    // 确保元素有 RenderTransform（TransformGroup 包含 ScaleTransform 和 TranslateTransform）
    public static void EnsureTransform(UIElement element)
    {
        if (element.RenderTransform is TransformGroup group &&
            group.Children.Count >= 2 &&
            group.Children[0] is ScaleTransform &&
            group.Children[1] is TranslateTransform)
        {
            return;
        }

        var existingTransform = element.RenderTransform;
        var transforms = new TransformCollection
        {
            new ScaleTransform(1, 1),
            new TranslateTransform(0, 0)
        };
        if (existingTransform != null && !ReferenceEquals(existingTransform, Transform.Identity))
        {
            transforms.Add(existingTransform);
        }
        element.RenderTransform = new TransformGroup
        {
            Children = transforms
        };
        element.RenderTransformOrigin = new Point(0.5, 0.5);
    }

    public static ScaleTransform GetScaleTransform(UIElement element)
    {
        EnsureTransform(element);
        return ((TransformGroup)element.RenderTransform).Children[0] as ScaleTransform
               ?? new ScaleTransform(1, 1);
    }

    public static TranslateTransform GetTranslateTransform(UIElement element)
    {
        EnsureTransform(element);
        return ((TransformGroup)element.RenderTransform).Children[1] as TranslateTransform
               ?? new TranslateTransform(0, 0);
    }

    // 淡入
    public static void FadeIn(UIElement element, double duration = 200, EventHandler? onComplete = null)
    {
        FadeTo(element, 1, duration, QuickEase, onComplete);
    }

    // 淡出
    public static void FadeOut(UIElement element, double duration = 150, EventHandler? onComplete = null)
    {
        FadeTo(element, 0, duration, QuickEase, onComplete);
    }

    // 淡化到指定透明度；最终值写回基础属性，避免动画结束后继续覆盖交互状态。
    public static void FadeTo(
        UIElement element,
        double opacity,
        double duration = 200,
        IEasingFunction? easing = null,
        EventHandler? onComplete = null)
    {
        AnimateDouble(element, UIElement.OpacityProperty, opacity, duration, easing ?? QuickEase, onComplete);
    }

    // 缩放到指定比例
    public static void ScaleTo(UIElement element, double scale, double duration = 200, IEasingFunction? easing = null)
    {
        var transform = GetScaleTransform(element);
        var ease = easing ?? SmoothEase;
        AnimateDouble(transform, ScaleTransform.ScaleXProperty, scale, duration, ease);
        AnimateDouble(transform, ScaleTransform.ScaleYProperty, scale, duration, ease);
    }

    // 平移到指定位置
    public static void TranslateTo(UIElement element, double x, double y, double duration = 200, IEasingFunction? easing = null, EventHandler? onComplete = null)
    {
        var transform = GetTranslateTransform(element);
        var ease = easing ?? SmoothEase;
        AnimateDouble(transform, TranslateTransform.XProperty, x, duration, ease);
        AnimateDouble(transform, TranslateTransform.YProperty, y, duration, ease, onComplete);
    }

    // 颜色过渡
    public static void TransitionColor(Brush brush, Color toColor, double duration = 300)
    {
        if (brush is not SolidColorBrush solidBrush) return;

        var anim = new ColorAnimation(toColor, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = SmoothEase
        };
        solidBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    // 取消所有动画
    public static void StopAllAnimations(UIElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (element.RenderTransform is TransformGroup group)
        {
            foreach (var transform in group.Children)
            {
                if (transform is ScaleTransform st)
                {
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                }
                else if (transform is TranslateTransform tt)
                {
                    tt.BeginAnimation(TranslateTransform.XProperty, null);
                    tt.BeginAnimation(TranslateTransform.YProperty, null);
                }
            }
        }
    }

    // 快速弹跳（用于强调）
    public static void QuickBounce(UIElement element, double scale = 1.03, double duration = 100)
    {
        var transform = GetScaleTransform(element);
        BounceDouble(transform, ScaleTransform.ScaleXProperty, scale, duration);
        BounceDouble(transform, ScaleTransform.ScaleYProperty, scale, duration);
    }

    private static void AnimateDouble(
        DependencyObject target,
        DependencyProperty property,
        double value,
        double duration,
        IEasingFunction easing,
        EventHandler? onComplete = null)
    {
        var from = (double)target.GetValue(property);
        var animatable = (IAnimatable)target;
        animatable.BeginAnimation(property, null);
        target.SetValue(property, value);

        var animation = new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        if (onComplete != null) animation.Completed += onComplete;
        animatable.BeginAnimation(property, animation);
    }

    private static void BounceDouble(Animatable target, DependencyProperty property, double scale, double duration)
    {
        var baseValue = (double)target.GetAnimationBaseValue(property);
        target.BeginAnimation(property, null);

        var animation = new DoubleAnimation(baseValue, scale, TimeSpan.FromMilliseconds(duration))
        {
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop
        };
        target.BeginAnimation(property, animation);
    }

    // 闪烁高亮（用于撤销提示）
    public static void FlashHighlight(Border element, Color highlightColor, double duration = 120)
    {
        var originalBg = element.Background;
        var highlightBrush = new SolidColorBrush(Colors.Transparent);
        element.Background = highlightBrush;

        var flashAnim = new ColorAnimation
        {
            From = Colors.Transparent,
            To = Color.FromArgb((byte)(highlightColor.A * 0.4), highlightColor.R, highlightColor.G, highlightColor.B),
            Duration = TimeSpan.FromMilliseconds(duration),
            AutoReverse = true,
            EasingFunction = new QuadraticEase()
        };
        flashAnim.Completed += (s, e) => element.Background = originalBg;

        highlightBrush.BeginAnimation(SolidColorBrush.ColorProperty, flashAnim);
    }
}
