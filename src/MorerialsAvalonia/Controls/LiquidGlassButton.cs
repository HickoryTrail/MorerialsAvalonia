using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MorerialsAvalonia.Controls;
using MorerialsAvalonia.Native;
using MorerialsAvalonia.Rendering;

namespace MorerialsAvalonia.Materials.LiquidGlass;

/// <summary>
/// 具备悬停和按下弹簧动画的液态玻璃按钮。
/// </summary>
/// <remarks>
/// 此控件继承普通 Avalonia <see cref="Button"/>，因此保留键盘、命令和点击行为。
/// 请将它放入 <see cref="MaterialHost"/> 的内容树中以启用 GPU 材质背景。
/// </remarks>
public sealed class LiquidGlassButton : Button
{
    /// <summary>定义液态玻璃圆角半径。</summary>
    public new static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<LiquidGlassButton, double>(nameof(CornerRadius), 29);

    /// <summary>定义液态玻璃材质。</summary>
    public static readonly StyledProperty<LiquidGlassMaterial> MaterialProperty =
        AvaloniaProperty.Register<LiquidGlassButton, LiquidGlassMaterial>(
            nameof(Material),
            LiquidGlassProfiles.Reference);

    /// <summary>定义可单独覆盖材质高光强度的值。</summary>
    public static readonly StyledProperty<double> HighlightIntensityProperty =
        AvaloniaProperty.Register<LiquidGlassButton, double>(
            nameof(HighlightIntensity),
            double.NaN);

    private readonly MaterialRegionRegistration _registration;
    private readonly MaterialForegroundScope _foregroundScope;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ScaleTransform _scaleTransform = new(1, 1);
    private readonly TranslateTransform _translateTransform = new();
    private double _lastTime;
    private double _scale = 1;
    private double _scaleVelocity;
    private double _offsetY;
    private double _offsetVelocity;
    private bool _hovered;
    private bool _pressed;
    private bool _animationsEnabled = true;
    private WindowBase? _window;

    /// <summary>初始化 <see cref="LiquidGlassButton"/>。</summary>
    public LiquidGlassButton()
    {
        MinHeight = 44;
        MinWidth = 44;
        RenderTransformOrigin = RelativePoint.Center;
        RenderTransform = new TransformGroup
        {
            Children = new Transforms { _scaleTransform, _translateTransform }
        };

        _registration = new MaterialRegionRegistration(this, CreateRegion);
        _foregroundScope = new MaterialForegroundScope(this);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Render, OnAnimationTick);
        LostFocus += (_, _) => ResetInteractionState();
    }

    /// <summary>获取或设置液态玻璃圆角半径，单位为 DIP。</summary>
    public new double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>获取或设置液态玻璃光学参数。</summary>
    public LiquidGlassMaterial Material
    {
        get => GetValue(MaterialProperty);
        set => SetValue(MaterialProperty, value);
    }

    /// <summary>
    /// 获取或设置高光强度覆盖值；<see cref="double.NaN"/> 表示使用 <see cref="Material"/> 中的值。
    /// </summary>
    public double HighlightIntensity
    {
        get => GetValue(HighlightIntensityProperty);
        set => SetValue(HighlightIntensityProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _animationsEnabled = WindowsNative.AreClientAnimationsEnabled();
        _lastTime = _clock.Elapsed.TotalSeconds;
        _window = TopLevel.GetTopLevel(this) as WindowBase;
        if (_window is not null)
            _window.Deactivated += OnWindowDeactivated;
        _registration.Attach();
        _foregroundScope.Attach();
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        if (_window is not null)
            _window.Deactivated -= OnWindowDeactivated;
        _window = null;
        _hovered = false;
        _pressed = false;
        _timer.Stop();
        _foregroundScope.Dispose();
        _registration.Dispose();
        base.OnDetachedFromLogicalTree(e);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        _hovered = true;
        EnsureAnimationRunning();
        base.OnPointerEntered(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hovered = false;
        EnsureAnimationRunning();
        base.OnPointerExited(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _pressed = true;
        EnsureAnimationRunning();
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _pressed = false;
        EnsureAnimationRunning();
        base.OnPointerReleased(e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _pressed = false;
        EnsureAnimationRunning();
        base.OnPointerCaptureLost(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            _pressed = true;
            EnsureAnimationRunning();
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            _pressed = false;
            EnsureAnimationRunning();
        }

        base.OnKeyUp(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CornerRadiusProperty ||
            change.Property == MaterialProperty ||
            change.Property == HighlightIntensityProperty)
            _registration.Update();
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _lastTime, 0.001, 1.0 / 30.0);
        _lastTime = now;

        var targetScale = _pressed ? 0.965 : _hovered ? 1.015 : 1.0;
        var targetOffset = _pressed ? 2.0 : 0.0;
        if (_animationsEnabled)
        {
            IntegrateSpring(ref _scale, ref _scaleVelocity, targetScale, dt);
            IntegrateSpring(ref _offsetY, ref _offsetVelocity, targetOffset, dt);
        }
        else
        {
            _scale = targetScale;
            _offsetY = targetOffset;
        }

        _scaleTransform.ScaleX = _scale;
        _scaleTransform.ScaleY = _scale;
        _translateTransform.Y = _offsetY;
        _registration.Update();

        if (!IsSettled(targetScale, targetOffset))
            return;

        _scale = targetScale;
        _offsetY = targetOffset;
        _scaleVelocity = 0;
        _offsetVelocity = 0;
        _scaleTransform.ScaleX = targetScale;
        _scaleTransform.ScaleY = targetScale;
        _translateTransform.Y = targetOffset;
        _registration.Update();
        _timer.Stop();
    }

    private LiquidGlassMaterial EffectiveMaterial
    {
        get
        {
            var material = Material;
            if (!double.IsNaN(HighlightIntensity))
            {
                material = material with
                {
                    Highlight = material.Highlight with
                    {
                        Intensity = Math.Clamp(HighlightIntensity, 0, 1)
                    }
                };
            }

            return material;
        }
    }

    private MaterialRegion CreateRegion() => new()
    {
        CornerRadius = CornerRadius,
        Scale = _scale,
        OffsetY = _offsetY,
        Material = EffectiveMaterial,
        Kind = MaterialKind.LiquidGlass,
        ZIndex = 10
    };

    private void EnsureAnimationRunning()
    {
        if (_timer.IsEnabled)
            return;

        _lastTime = _clock.Elapsed.TotalSeconds;
        _timer.Start();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
        => ResetInteractionState();

    private void ResetInteractionState()
    {
        if (!_hovered && !_pressed)
            return;

        _hovered = false;
        _pressed = false;
        EnsureAnimationRunning();
    }

    private bool IsSettled(double targetScale, double targetOffset)
        => Math.Abs(_scale - targetScale) < 0.0002 &&
           Math.Abs(_scaleVelocity) < 0.002 &&
           Math.Abs(_offsetY - targetOffset) < 0.002 &&
           Math.Abs(_offsetVelocity) < 0.002;

    private static void IntegrateSpring(ref double value, ref double velocity, double target, double dt)
    {
        value += velocity * dt;
        velocity += ((target - value) * 280 - velocity * 34) * dt;
    }
}
