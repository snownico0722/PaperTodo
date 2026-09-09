using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PaperTodo;

internal sealed partial class SkinBorder
{
    private readonly TransformGroup _filmTransform = new();
    private readonly TranslateTransform _idleReflection = new();
    private readonly TranslateTransform _moveReflection = new();
    private bool _reflectionRunning;
    internal bool IsReflectionRunning => _reflectionRunning;
    internal bool HasReflectionWindow => _reflectionWindow != null;
    internal double IdleReflectionOffset => _idleReflection.X;
    internal double MovementReflectionOffset => _moveReflection.X;

    private void InitializeReflection()
    {
        _filmTransform.Children.Add(_idleReflection);
        _filmTransform.Children.Add(_moveReflection);
    }
    private void SyncReflectionSubscription()
    {
        var window = !IsOutline && IsLoaded && IsVisible && !_highContrast && _animateReflection &&
            Skin is PaperSkins.Pearl or PaperSkins.LiquidGlass ? Window.GetWindow(this) : null;
        // Retain the state subscription while minimized so restoring can resume it.
        if (!ReferenceEquals(window, _reflectionWindow))
        {
            DetachReflection();
            _reflectionWindow = window;
            if (window != null)
            {
                window.LocationChanged += OnReflectionLocationChanged;
                window.StateChanged += OnReflectionStateChanged;
                window.PreviewMouseMove += OnReflectionPointerMoved;
                window.Closed += OnReflectionClosed;
            }
        }
        var run = window is { IsVisible: true } && window.WindowState != WindowState.Minimized && Skin == PaperSkins.Pearl;
        if (run != _reflectionRunning)
        {
            _reflectionRunning = run;
            _idleReflection.BeginAnimation(TranslateTransform.XProperty, null);
            if (run)
            {
                var animation = new DoubleAnimation(-.28, .28, TimeSpan.FromSeconds(14))
                {
                    AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                Timeline.SetDesiredFrameRate(animation, 24);
                _idleReflection.BeginAnimation(TranslateTransform.XProperty, animation);
            }
        }
        if (window != null) OnReflectionLocationChanged(window, EventArgs.Empty);
    }
    private void DetachReflection()
    {
        if (_reflectionWindow != null)
        {
            _reflectionWindow.LocationChanged -= OnReflectionLocationChanged;
            _reflectionWindow.StateChanged -= OnReflectionStateChanged;
            _reflectionWindow.PreviewMouseMove -= OnReflectionPointerMoved;
            _reflectionWindow.Closed -= OnReflectionClosed;
        }
        _reflectionWindow = null;
        _reflectionRunning = false;
        _idleReflection.BeginAnimation(TranslateTransform.XProperty, null);
        _idleReflection.X = 0;
        _moveReflection.X = _moveReflection.Y = 0;
    }
    private void OnReflectionClosed(object? sender, EventArgs e) => DetachReflection();
    private void OnReflectionStateChanged(object? sender, EventArgs e) => SyncReflectionSubscription();
    private void OnReflectionLocationChanged(object? sender, EventArgs e)
    {
        if (_reflectionWindow is not { IsVisible: true } window || window.WindowState == WindowState.Minimized ||
            !double.IsFinite(window.Left) || !double.IsFinite(window.Top)) return;
        // Independent from the idle clock, so dragging never restarts slow film movement.
        _moveReflection.X = Math.Sin((window.Left + window.Top * .25) / 380) * .32;
        _moveReflection.Y = Math.Sin(window.Top / 450) * .12;
    }
    private void OnReflectionPointerMoved(object sender, MouseEventArgs e)
    {
        if (Skin != PaperSkins.LiquidGlass || ActualWidth <= 0 || ActualHeight <= 0 ||
            _reflectionWindow?.WindowState == WindowState.Minimized) return;
        var pointer = e.GetPosition(this);
        var point = new Point(Math.Clamp(pointer.X / ActualWidth, 0, 1), Math.Clamp(pointer.Y / ActualHeight, 0, 1));
        if ((point - _lensLight.Center).LengthSquared < .0004) return;
        _lensLight.Center = _lensLight.GradientOrigin = point;
    }
}
