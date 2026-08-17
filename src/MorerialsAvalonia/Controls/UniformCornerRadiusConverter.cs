using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace MorerialsAvalonia.Controls;

/// <summary>
/// 将液态玻璃控件公开的统一数值圆角转换为 Avalonia 原生 Border 圆角。
/// </summary>
/// <remarks>
/// GPU 着色器使用单个 <see cref="double"/> 半径，而 <see cref="CornerRadius"/>
/// 需要四个角值。模板通过此转换器让阴影和裁剪轮廓保持与 GPU 区域一致。
/// </remarks>
internal sealed class UniformCornerRadiusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var radius = value is double number && double.IsFinite(number)
            ? Math.Max(0, number)
            : 0;
        return new CornerRadius(radius);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is CornerRadius radius ? radius.TopLeft : 0d;
}
