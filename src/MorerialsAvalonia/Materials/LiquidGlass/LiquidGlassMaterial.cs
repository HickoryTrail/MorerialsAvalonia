namespace MorerialsAvalonia.Materials.LiquidGlass;

/// <summary>
/// 定义液态玻璃边缘折射曲线。
/// </summary>
/// <param name="Power">作用于曲线结果的最终指数。</param>
/// <param name="A">曲线的基线偏移。</param>
/// <param name="B">折射收缩量。</param>
/// <param name="C">指数底数缩放。</param>
/// <param name="D">折射向中心衰减的速度。</param>
public readonly record struct LiquidGlassRefractionParameters(
    double Power,
    double A,
    double B,
    double C,
    double D);

/// <summary>
/// 定义液态玻璃的方向性边缘发光参数。
/// </summary>
/// <param name="Weight">方向性发光权重。</param>
/// <param name="Bias">整体亮度偏移。</param>
/// <param name="Edge0">发光过渡起点。</param>
/// <param name="Edge1">发光过渡终点。</param>
public readonly record struct LiquidGlassGlowParameters(
    double Weight,
    double Bias,
    double Edge0,
    double Edge1);

/// <summary>
/// 定义液态玻璃描边和反射高光参数。
/// </summary>
/// <param name="Intensity">高光强度，建议范围为 <c>0..1</c>。</param>
/// <param name="BorderWidth">描边宽度，单位为 DIP。</param>
/// <param name="ReflectionFalloffWidth">反射向内部衰减的宽度，单位为 DIP。</param>
public readonly record struct LiquidGlassHighlightParameters(
    double Intensity,
    double BorderWidth,
    double ReflectionFalloffWidth);

/// <summary>
/// 定义一个液态玻璃区域的光学材质参数。
/// </summary>
/// <param name="BlurRadius">高斯模糊半径，单位为 DIP。</param>
/// <param name="BlurDownsampleScale">模糊中间纹理的缩放比例，范围为 <c>0.1..1</c>。</param>
/// <param name="RefractionCurve">边缘折射曲线。</param>
/// <param name="NoiseIntensity">微弱噪声的强度。</param>
/// <param name="Glow">方向性边缘发光。</param>
/// <param name="Highlight">描边和反射高光。</param>
public readonly record struct LiquidGlassMaterial(
    double BlurRadius,
    double BlurDownsampleScale,
    LiquidGlassRefractionParameters RefractionCurve,
    double NoiseIntensity,
    LiquidGlassGlowParameters Glow,
    LiquidGlassHighlightParameters Highlight)
{
    internal LiquidGlassMaterial ScaleLengths(double scale) => this with
    {
        BlurRadius = BlurRadius * scale,
        Highlight = Highlight with
        {
            BorderWidth = Highlight.BorderWidth * scale,
            ReflectionFalloffWidth = Highlight.ReflectionFalloffWidth * scale
        }
    };
}

/// <summary>
/// 提供经过验证的液态玻璃材质预设。
/// </summary>
public static class LiquidGlassProfiles
{
    /// <summary>
    /// 获取默认的轻度模糊、边缘折射和高光预设。
    /// </summary>
    public static LiquidGlassMaterial Reference { get; } = new(
        BlurRadius: 2,
        BlurDownsampleScale: 1,
        RefractionCurve: new LiquidGlassRefractionParameters(
            Power: 2,
            A: 0.5,
            B: 0.6,
            C: 5,
            D: 2),
        NoiseIntensity: 0.01,
        Glow: new LiquidGlassGlowParameters(
            Weight: 0.3,
            Bias: 0,
            Edge0: 0.5,
            Edge1: -0.5),
        Highlight: new LiquidGlassHighlightParameters(
            Intensity: 0.8,
            BorderWidth: 1,
            ReflectionFalloffWidth: 18));
}
