using MorerialsAvalonia.Rendering;

namespace MorerialsAvalonia;

/// <summary>
/// 在当前用户的设备上准备 MorerialsAvalonia 所需的 HLSL 着色器缓存。
/// </summary>
/// <remarks>
/// 请在创建 Avalonia 窗口前调用 <see cref="EnsureCompiledAsync(CancellationToken)"/>。
/// 未提前调用时，<see cref="MaterialHost"/> 会自动准备缓存并在诊断中显示编译状态，
/// 不会因为缓存缺失直接报错。
/// </remarks>
public static class MaterialShaderCompiler
{
    /// <summary>
    /// 编译或验证当前版本全部内置材质的着色器缓存。
    /// </summary>
    /// <param name="cancellationToken">用于取消等待缓存锁的令牌。</param>
    /// <returns>本次复用和编译的统计结果。</returns>
    /// <exception cref="PlatformNotSupportedException">当前系统没有可用的 Windows D3D 编译器时引发。</exception>
    /// <exception cref="InvalidOperationException">内置 HLSL 无法编译时引发。</exception>
    public static Task<MaterialShaderCompilationResult> EnsureCompiledAsync(
        CancellationToken cancellationToken = default)
        => ShaderBytecodeCache.EnsureCompiledAsync(cancellationToken);
}

/// <summary>
/// 描述一次着色器缓存准备的结果。
/// </summary>
/// <param name="CacheDirectory">当前用户的着色器缓存目录。</param>
/// <param name="CompiledShaderCount">本次实际编译的着色器数量。</param>
/// <param name="ReusedShaderCount">从有效缓存复用的着色器数量。</param>
public sealed record MaterialShaderCompilationResult(
    string CacheDirectory,
    int CompiledShaderCount,
    int ReusedShaderCount);
