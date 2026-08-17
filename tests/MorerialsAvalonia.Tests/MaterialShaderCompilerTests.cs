using Avalonia;
using MorerialsAvalonia.Controls;
using MorerialsAvalonia.Materials.LiquidGlass;
using Xunit;

namespace MorerialsAvalonia.Tests;

public sealed class MaterialShaderCompilerTests
{
    [Fact]
    public async Task EnsureCompiledAsync_creates_or_reuses_all_embedded_shaders()
    {
        var first = await MaterialShaderCompiler.EnsureCompiledAsync();

        Assert.True(Directory.Exists(first.CacheDirectory));
        Assert.True(first.CompiledShaderCount + first.ReusedShaderCount >= 3);

        var second = await MaterialShaderCompiler.EnsureCompiledAsync();

        Assert.Equal(0, second.CompiledShaderCount);
        Assert.True(second.ReusedShaderCount >= 3);
    }

    [Fact]
    public void Reference_profile_has_renderable_values()
    {
        var profile = LiquidGlassProfiles.Reference;

        Assert.True(profile.BlurRadius > 0);
        Assert.InRange(profile.BlurDownsampleScale, 0.1, 1.0);
        Assert.InRange(profile.Highlight.Intensity, 0, 1);
    }

    [Fact]
    public void Uniform_corner_radius_converter_preserves_the_button_capsule()
    {
        var converter = new UniformCornerRadiusConverter();

        var radius = Assert.IsType<CornerRadius>(converter.Convert(20d, typeof(CornerRadius), null, null!));

        Assert.Equal(20, radius.TopLeft);
        Assert.Equal(20, radius.TopRight);
        Assert.Equal(20, radius.BottomRight);
        Assert.Equal(20, radius.BottomLeft);
    }

    [Fact]
    public void Material_foreground_selects_a_contrasting_kind_from_relative_luminance()
    {
        Assert.Equal(
            MaterialForegroundKind.Light,
            MaterialForeground.ResolveKind(0.18f, 0.45));
        Assert.Equal(
            MaterialForegroundKind.Dark,
            MaterialForeground.ResolveKind(0.72f, 0.45));
    }
}
