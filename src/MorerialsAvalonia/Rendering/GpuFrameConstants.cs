using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MorerialsAvalonia.Rendering;

[StructLayout(LayoutKind.Sequential)]
internal struct GpuLiquidGlassRegion
{
    // Bounds 由布局阶段计算为中心点 (xy) 与半尺寸 (zw)。
    public Vector4 Bounds;
    // Geometry.x 保存 Avalonia 布局提供的圆角半径。
    public Vector4 Geometry;
    public Vector4 RefractionCurve;
    public Vector4 OpticalEffects;
    public Vector4 Glow;
    public Vector4 Highlight;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct GaussianBlurFrameConstants
{
    public Vector4 OutputSizeAndDirection;
    public Vector4 KernelParameters;
    public fixed float PackedWeights[68];
}

[InlineArray(16)]
internal struct GpuLiquidGlassRegionBuffer
{
    private GpuLiquidGlassRegion _element0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CompositeFrameConstants
{
    public Vector4 OutputAndCaptureSize;
    public Vector4 RegionCountAndPadding;
    public GpuLiquidGlassRegionBuffer Regions;
}

[InlineArray(ForegroundProbeRegistry.MaximumProbes)]
internal struct GpuForegroundProbeBuffer
{
    private Vector4 _element0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ForegroundLuminanceFrameConstants
{
    // xy 为捕获纹理尺寸，z 为本次实际提交的探针数。
    public Vector4 CaptureSizeAndProbeCount;
    // 每个元素依次为 left、top、right、bottom，单位为材质表面局部像素。
    public GpuForegroundProbeBuffer Probes;
}
