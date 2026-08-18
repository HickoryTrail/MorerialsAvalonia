using Avalonia;
using MorerialsAvalonia.Capture;
using MorerialsAvalonia.Controls;
using MorerialsAvalonia.Materials.LiquidGlass;
using Silk.NET.Core.Native;
using Silk.NET.DXGI;
using Silk.NET.Maths;
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

    [Fact]
    public void Desktop_duplication_pointer_only_frame_does_not_intersect_a_crop()
    {
        ComPtr<IDXGIResource> resource = default;
        using var frame = new CaptureFrameLease(
            resource,
            default,
            Array.Empty<Box2D<int>>(),
            Array.Empty<OutduplMoveRect>(),
            1,
            1920,
            1080,
            1);

        Assert.False(frame.IntersectsCrop(0, 0, 500, 500));
    }

    [Fact]
    public void Desktop_duplication_dirty_rect_outside_crop_is_skipped()
    {
        ComPtr<IDXGIResource> resource = default;
        using var frame = new CaptureFrameLease(
            resource,
            new OutduplFrameInfo { LastPresentTime = 1 },
            [new Box2D<int>(1000, 700, 1200, 900)],
            Array.Empty<OutduplMoveRect>(),
            1,
            1920,
            1080,
            1);

        Assert.False(frame.IntersectsCrop(0, 0, 500, 500));
    }

    [Fact]
    public void Desktop_duplication_move_rect_uses_both_source_and_destination()
    {
        ComPtr<IDXGIResource> resource = default;
        using var frame = new CaptureFrameLease(
            resource,
            default,
            Array.Empty<Box2D<int>>(),
            [new OutduplMoveRect
            {
                SourcePoint = new Vector2D<int>(50, 50),
                DestinationRect = new Box2D<int>(300, 300, 400, 400)
            }],
            1,
            1920,
            1080,
            1);

        Assert.True(frame.IntersectsCrop(0, 0, 100, 100));
        Assert.True(frame.IntersectsCrop(350, 350, 450, 450));
    }
}
