using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using MorerialsAvalonia.Controls;
using MorerialsAvalonia.Materials.LiquidGlass;
using MorerialsAvalonia.Rendering;

namespace MorerialsAvalonia.Materials.LiquidGlass;

/// <summary>
/// 将其布局区域注册为液态玻璃的内容控件。
/// </summary>
/// <remarks>
/// 此控件必须位于 <see cref="MaterialHost"/> 的内容树中。它本身保持透明，
/// GPU 背景由宿主在其后方绘制，子内容仍按普通 Avalonia 布局和命中测试工作。
/// </remarks>
public sealed class LiquidGlassContainer : ContentControl
{
    /// <summary>定义液态玻璃圆角半径。</summary>
    public new static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<LiquidGlassContainer, double>(nameof(CornerRadius), 32);

    /// <summary>定义液态玻璃光学材质。</summary>
    public static readonly StyledProperty<LiquidGlassMaterial> MaterialProperty =
        AvaloniaProperty.Register<LiquidGlassContainer, LiquidGlassMaterial>(
            nameof(Material),
            LiquidGlassProfiles.Reference);

    /// <summary>定义可单独覆盖材质高光强度的值。</summary>
    public static readonly StyledProperty<double> HighlightIntensityProperty =
        AvaloniaProperty.Register<LiquidGlassContainer, double>(
            nameof(HighlightIntensity),
            double.NaN);

    private readonly MaterialRegionRegistration _registration;
    private readonly MaterialForegroundScope _foregroundScope;

    /// <summary>初始化 <see cref="LiquidGlassContainer"/>。</summary>
    public LiquidGlassContainer()
    {
        _registration = new MaterialRegionRegistration(this, CreateRegion);
        _foregroundScope = new MaterialForegroundScope(this);
        Background = null;
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
        _registration.Attach();
        _foregroundScope.Attach();
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        _foregroundScope.Dispose();
        _registration.Dispose();
        base.OnDetachedFromLogicalTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CornerRadiusProperty ||
            change.Property == MaterialProperty ||
            change.Property == HighlightIntensityProperty)
            _registration.Update();
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
        Scale = 1,
        Material = EffectiveMaterial,
        Kind = MaterialKind.LiquidGlass,
        ZIndex = 0
    };
}
