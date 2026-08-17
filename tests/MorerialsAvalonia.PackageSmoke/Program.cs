using MorerialsAvalonia;

var result = await MaterialShaderCompiler.EnsureCompiledAsync();
if (result.CompiledShaderCount + result.ReusedShaderCount < 3)
{
    Console.Error.WriteLine("NuGet 包未准备完整的内置着色器集。");
    return 1;
}

Console.WriteLine($"NuGet 着色器缓存验证完成: {result.CacheDirectory}");
return 0;
