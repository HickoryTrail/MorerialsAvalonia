namespace MorerialsAvalonia.Rendering;

/// <summary>
/// 描述一个可缓存的内置 HLSL 着色器。
/// </summary>
internal sealed record MaterialShaderDescriptor(
    string Id,
    string ResourceName,
    string EntryPoint,
    string TargetProfile);

/// <summary>
/// 未来材质渲染通道的最小内部契约。
/// 新材质只需声明其 HLSL 描述符，并由共享缓存与 D3D11 渲染器加载。
/// </summary>
internal interface IMaterialRenderPass
{
    string Id { get; }
    IReadOnlyList<MaterialShaderDescriptor> Shaders { get; }
}

internal sealed class LiquidGlassRenderPass : IMaterialRenderPass
{
    internal static MaterialShaderDescriptor GaussianBlurShader { get; } = new(
        "liquid-glass-gaussian-blur",
        "MorerialsAvalonia.Shaders.GaussianBlur.compute.hlsl",
        "main",
        "cs_5_0");

    internal static MaterialShaderDescriptor CompositeShader { get; } = new(
        "liquid-glass-composite",
        "MorerialsAvalonia.Shaders.LiquidGlassComposite.compute.hlsl",
        "main",
        "cs_5_0");

    internal static MaterialShaderDescriptor ForegroundLuminanceShader { get; } = new(
        "material-foreground-luminance",
        "MorerialsAvalonia.Shaders.MaterialForegroundLuminance.compute.hlsl",
        "main",
        "cs_5_0");

    public string Id => "liquid-glass";

    public IReadOnlyList<MaterialShaderDescriptor> Shaders { get; } =
        [GaussianBlurShader, CompositeShader, ForegroundLuminanceShader];
}

internal static class MaterialShaderManifest
{
    private static readonly IMaterialRenderPass[] Passes = [new LiquidGlassRenderPass()];

    internal static IReadOnlyList<MaterialShaderDescriptor> All { get; } =
        Passes.SelectMany(static pass => pass.Shaders).ToArray();
}
