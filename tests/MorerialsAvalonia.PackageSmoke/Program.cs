using Avalonia.Controls;
using MorerialsAvalonia;

var foregroundTarget = new TextBlock();
MaterialForeground.SetMode(foregroundTarget, MaterialForegroundMode.Automatic);
MaterialForeground.SetLuminanceThreshold(foregroundTarget, 0.45);

if (MaterialForeground.GetMode(foregroundTarget) != MaterialForegroundMode.Automatic ||
    MaterialForeground.GetLuminanceThreshold(foregroundTarget) != 0.45)
{
    Console.Error.WriteLine("NuGet 包未暴露自动前景色 API。");
    return 1;
}

var result = await MaterialShaderCompiler.EnsureCompiledAsync();
if (result.CompiledShaderCount + result.ReusedShaderCount < 3)
{
    Console.Error.WriteLine("NuGet 包未准备完整的内置着色器集。");
    return 1;
}

Console.WriteLine($"NuGet 着色器缓存验证完成: {result.CacheDirectory}");
return 0;
