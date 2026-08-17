using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;

namespace MorerialsAvalonia.Rendering;

/// <summary>
/// 将 HLSL 编译从窗口初始化路径移到可显式预热的用户缓存。
/// 缓存内容只包含已编译字节码，HLSL 源码继续嵌入 NuGet 程序集。
/// </summary>
internal static class ShaderBytecodeCache
{
    private const string CacheFormat = "v1";
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);

    internal static async Task<MaterialShaderCompilationResult> EnsureCompiledAsync(
        CancellationToken cancellationToken)
    {
        await ProcessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => EnsureCompiledCore(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ProcessGate.Release();
        }
    }

    internal static byte[] Load(MaterialShaderDescriptor descriptor)
    {
        var source = ReadSource(descriptor);
        var paths = GetCachePaths(descriptor, source);
        if (TryReadValidBytecode(paths.BytecodePath, paths.HashPath, out var bytecode))
            return bytecode;

        // 另一个进程可能正好处于原子替换阶段；等待共享编译锁后再读取，
        // 同时覆盖应用未提前调用公开预热 API 的兜底路径。
        EnsureCompiledAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (TryReadValidBytecode(paths.BytecodePath, paths.HashPath, out bytecode))
            return bytecode;

        throw new InvalidOperationException(
            $"找不到有效的着色器缓存: {descriptor.Id}。请先调用 MaterialShaderCompiler.EnsureCompiledAsync。");
    }

    internal static string GetCacheDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
            localApplicationData = Path.GetTempPath();

        var assemblyVersion = typeof(MaterialShaderCompiler).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        return Path.Combine(localApplicationData, "MorerialsAvalonia", "Shaders", CacheFormat, assemblyVersion);
    }

    private static MaterialShaderCompilationResult EnsureCompiledCore(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("MorerialsAvalonia 的 HLSL 编译器仅支持 Windows。");

        var cacheDirectory = GetCacheDirectory();
        Directory.CreateDirectory(cacheDirectory);
        using var cacheMutex = new Mutex(false, GetMutexName(cacheDirectory));
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = cacheMutex.WaitOne(TimeSpan.FromMinutes(2));
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
                throw new TimeoutException("等待 MorerialsAvalonia 着色器缓存锁超时。");

            var compiled = 0;
            var reused = 0;
            foreach (var descriptor in MaterialShaderManifest.All)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = ReadSource(descriptor);
                var paths = GetCachePaths(descriptor, source);
                if (TryReadValidBytecode(paths.BytecodePath, paths.HashPath, out _))
                {
                    reused++;
                    continue;
                }

                var bytecode = Compile(descriptor, source);
                WriteAtomically(paths.BytecodePath, paths.HashPath, bytecode);
                compiled++;
            }

            return new MaterialShaderCompilationResult(cacheDirectory, compiled, reused);
        }
        finally
        {
            if (ownsMutex)
                cacheMutex.ReleaseMutex();
        }
    }

    private static (string BytecodePath, string HashPath) GetCachePaths(
        MaterialShaderDescriptor descriptor,
        byte[] source)
    {
        var keyInput = Encoding.UTF8.GetBytes(
            $"{CacheFormat}\0{descriptor.Id}\0{descriptor.EntryPoint}\0{descriptor.TargetProfile}\0");
        var input = new byte[keyInput.Length + source.Length];
        System.Buffer.BlockCopy(keyInput, 0, input, 0, keyInput.Length);
        System.Buffer.BlockCopy(source, 0, input, keyInput.Length, source.Length);
        var key = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        var directory = Path.Combine(GetCacheDirectory(), descriptor.Id);
        return (Path.Combine(directory, $"{key}.cso"), Path.Combine(directory, $"{key}.sha256"));
    }

    private static byte[] ReadSource(MaterialShaderDescriptor descriptor)
    {
        using var resource = typeof(ShaderBytecodeCache).Assembly
            .GetManifestResourceStream(descriptor.ResourceName)
            ?? throw new InvalidOperationException($"未找到嵌入着色器资源: {descriptor.ResourceName}");
        var source = new byte[checked((int)resource.Length)];
        resource.ReadExactly(source);
        return source;
    }

    private static bool TryReadValidBytecode(string bytecodePath, string hashPath, out byte[] bytecode)
    {
        bytecode = [];
        if (!File.Exists(bytecodePath) || !File.Exists(hashPath))
            return false;

        try
        {
            bytecode = File.ReadAllBytes(bytecodePath);
            var expectedHash = File.ReadAllText(hashPath).Trim();
            if (bytecode.Length == 0 || string.IsNullOrWhiteSpace(expectedHash))
                return false;

            var actualHash = Convert.ToHexString(SHA256.HashData(bytecode));
            return string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WriteAtomically(string bytecodePath, string hashPath, byte[] bytecode)
    {
        var directory = Path.GetDirectoryName(bytecodePath)
            ?? throw new InvalidOperationException("着色器缓存路径无效。");
        Directory.CreateDirectory(directory);

        var bytecodeTemporaryPath = $"{bytecodePath}.{Guid.NewGuid():N}.tmp";
        var hashTemporaryPath = $"{hashPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(bytecodeTemporaryPath, bytecode);
            File.WriteAllText(hashTemporaryPath, Convert.ToHexString(SHA256.HashData(bytecode)));
            File.Move(bytecodeTemporaryPath, bytecodePath, true);
            File.Move(hashTemporaryPath, hashPath, true);
        }
        finally
        {
            if (File.Exists(bytecodeTemporaryPath))
                File.Delete(bytecodeTemporaryPath);
            if (File.Exists(hashTemporaryPath))
                File.Delete(hashTemporaryPath);
        }
    }

    private static string GetMutexName(string cacheDirectory)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheDirectory)));
        return $"Local\\MorerialsAvalonia.ShaderCache.{hash}";
    }

    private static unsafe byte[] Compile(MaterialShaderDescriptor descriptor, byte[] source)
    {
        using var compiler = new D3DCompiler(
            D3DCompiler.CreateDefaultContext(["d3dcompiler_47.dll"]));
        ComPtr<ID3D10Blob> bytecode = default;
        ComPtr<ID3D10Blob> errors = default;

        try
        {
            fixed (byte* sourcePointer = source)
            {
                var compileResult = compiler.Compile(
                    sourcePointer,
                    (nuint)source.Length,
                    descriptor.Id,
                    (D3DShaderMacro*)null,
                    (ID3DInclude*)null,
                    descriptor.EntryPoint,
                    descriptor.TargetProfile,
                    0,
                    0,
                    bytecode.GetAddressOf(),
                    errors.GetAddressOf());

                if (compileResult < 0)
                {
                    var detail = errors.Handle is null
                        ? $"HRESULT 0x{compileResult:X8}"
                        : Marshal.PtrToStringUTF8(
                            (nint)errors.Handle->GetBufferPointer(),
                            checked((int)errors.Handle->GetBufferSize()))?.TrimEnd('\0', '\r', '\n');
                    throw new InvalidOperationException(
                        $"HLSL 编译失败 ({descriptor.Id}): {detail ?? "未知错误"}");
                }
            }

            var length = checked((int)bytecode.Handle->GetBufferSize());
            var compiledBytecode = new byte[length];
            Marshal.Copy((nint)bytecode.Handle->GetBufferPointer(), compiledBytecode, 0, length);
            return compiledBytecode;
        }
        finally
        {
            bytecode.Dispose();
            errors.Dispose();
        }
    }
}
