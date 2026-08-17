using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MorerialsAvalonia;

/// <summary>
/// 指定材质区域内控件前景色的解析方式。
/// </summary>
public enum MaterialForegroundMode
{
    /// <summary>
    /// 按控件自身所在位置的捕获画面亮度自动选择深色或浅色前景。
    /// </summary>
    Automatic,

    /// <summary>
    /// 不创建独立探针，继承父级已解析的前景色和深浅类型。
    /// </summary>
    Inherit,

    /// <summary>
    /// 始终使用 <see cref="MaterialForeground.LightForegroundProperty"/> 指定的浅色前景。
    /// </summary>
    Light,

    /// <summary>
    /// 始终使用 <see cref="MaterialForeground.DarkForegroundProperty"/> 指定的深色前景。
    /// </summary>
    Dark,

    /// <summary>
    /// 不干预该控件的普通 Avalonia <c>Foreground</c> 值。
    /// </summary>
    Manual
}

/// <summary>
/// 由材质前景采样解析出的颜色类型。
/// </summary>
public enum MaterialForegroundKind
{
    /// <summary>
    /// 尚未获得可用的采样结果，或控件没有启用材质前景控制。
    /// </summary>
    Unspecified,

    /// <summary>
    /// 当前背景较暗，应使用浅色前景。
    /// </summary>
    Light,

    /// <summary>
    /// 当前背景较亮，应使用深色前景。
    /// </summary>
    Dark
}

/// <summary>
/// 为 <see cref="Materials.LiquidGlass.LiquidGlassContainer"/> 和
/// <see cref="Materials.LiquidGlass.LiquidGlassButton"/> 内容树提供动态前景色的附加属性。
/// </summary>
/// <remarks>
/// 自动模式每 1000ms 在 GPU 上对每个目标控件做 3x3 小样本亮度归约。
/// CPU 只接收每个控件一个 <see cref="float"/> 亮度结果，不会读取捕获纹理或整张桌面图像。
/// </remarks>
public sealed class MaterialForeground
{
    private MaterialForeground()
    {
    }

    /// <summary>
    /// 定义前景色解析模式。该属性可继承，父级的强制深浅色或手动设置会传递给子控件。
    /// </summary>
    public static readonly AttachedProperty<MaterialForegroundMode> ModeProperty =
        AvaloniaProperty.RegisterAttached<MaterialForeground, Control, MaterialForegroundMode>(
            "Mode",
            MaterialForegroundMode.Automatic,
            inherits: true);

    /// <summary>
    /// 定义背景较暗时使用的浅色前景，默认值为白色。该属性可继承。
    /// </summary>
    public static readonly AttachedProperty<IBrush?> LightForegroundProperty =
        AvaloniaProperty.RegisterAttached<MaterialForeground, Control, IBrush?>(
            "LightForeground",
            Brushes.White,
            inherits: true);

    /// <summary>
    /// 定义背景较亮时使用的深色前景，默认值为黑色。该属性可继承。
    /// </summary>
    public static readonly AttachedProperty<IBrush?> DarkForegroundProperty =
        AvaloniaProperty.RegisterAttached<MaterialForeground, Control, IBrush?>(
            "DarkForeground",
            Brushes.Black,
            inherits: true);

    /// <summary>
    /// 定义相对亮度阈值。亮度大于等于该值时解析为深色前景，默认值为 0.45。
    /// </summary>
    public static readonly AttachedProperty<double> LuminanceThresholdProperty =
        AvaloniaProperty.RegisterAttached<MaterialForeground, Control, double>(
            "LuminanceThreshold",
            0.45,
            inherits: true,
            validate: static value => double.IsFinite(value) && value >= 0 && value <= 1);

    /// <summary>
    /// 保存当前已解析的深浅类型。该属性可继承，可用于 XAML 样式选择器或绑定。
    /// </summary>
    public static readonly AttachedProperty<MaterialForegroundKind> ResolvedKindProperty =
        AvaloniaProperty.RegisterAttached<MaterialForeground, Control, MaterialForegroundKind>(
            "ResolvedKind",
            MaterialForegroundKind.Unspecified,
            inherits: true);

    /// <summary>获取控件的前景色解析模式。</summary>
    public static MaterialForegroundMode GetMode(Control control) => control.GetValue(ModeProperty);

    /// <summary>设置控件的前景色解析模式。</summary>
    public static void SetMode(Control control, MaterialForegroundMode value) => control.SetValue(ModeProperty, value);

    /// <summary>获取背景较暗时使用的浅色前景。</summary>
    public static IBrush? GetLightForeground(Control control) => control.GetValue(LightForegroundProperty);

    /// <summary>设置背景较暗时使用的浅色前景。</summary>
    public static void SetLightForeground(Control control, IBrush? value) => control.SetValue(LightForegroundProperty, value);

    /// <summary>获取背景较亮时使用的深色前景。</summary>
    public static IBrush? GetDarkForeground(Control control) => control.GetValue(DarkForegroundProperty);

    /// <summary>设置背景较亮时使用的深色前景。</summary>
    public static void SetDarkForeground(Control control, IBrush? value) => control.SetValue(DarkForegroundProperty, value);

    /// <summary>获取相对亮度阈值。</summary>
    public static double GetLuminanceThreshold(Control control) => control.GetValue(LuminanceThresholdProperty);

    /// <summary>设置相对亮度阈值，取值范围为 0 到 1。</summary>
    public static void SetLuminanceThreshold(Control control, double value) => control.SetValue(LuminanceThresholdProperty, value);

    /// <summary>获取当前已解析的前景深浅类型。</summary>
    public static MaterialForegroundKind GetResolvedKind(Control control) => control.GetValue(ResolvedKindProperty);

    internal static MaterialForegroundKind ResolveKind(float luminance, double threshold)
        => luminance >= Math.Clamp(threshold, 0, 1)
            ? MaterialForegroundKind.Dark
            : MaterialForegroundKind.Light;
}
