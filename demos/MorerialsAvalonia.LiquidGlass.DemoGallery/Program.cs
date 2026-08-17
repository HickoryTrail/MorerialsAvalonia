using Avalonia;
using Avalonia.Logging;
using System;
using System.Text;
using System.Diagnostics;

namespace MorerialsAvalonia.LiquidGlass.DemoGallery;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
#if DEBUG
        Console.OutputEncoding = Encoding.UTF8;
        Trace.Listeners.Add(new ConsoleTraceListener(useErrorStream: true));
        Trace.AutoFlush = true;
#endif

        try
        {
            // 推荐路径：在创建窗口前由使用方预热，使首次 MaterialHost 显示不承担 HLSL 编译延迟。
            MaterialShaderCompiler.EnsureCompiledAsync().GetAwaiter().GetResult();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception exception)
        {
            Trace.TraceError($"DemoGallery 启动失败: {exception}");
            return 1;
        }
    }

    // Avalonia 设计器也会调用此方法，因此不要把运行时预热放在这里。
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.AngleEgl],
                CompositionMode = [Win32CompositionMode.DirectComposition]
            })
            .WithInterFont();

#if DEBUG
        return builder.LogToTextWriter(Console.Error, LogEventLevel.Debug);
#else
        return builder.LogToTrace();
#endif
    }
}
