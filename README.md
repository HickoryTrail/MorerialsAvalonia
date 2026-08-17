# MorerialsAvalonia

面向 Avalonia 的高性能 Windows GPU 材质库。`MorerialsAvalonia` 通过 Windows Graphics Capture、D3D11 和 Avalonia GPU 合成在控件后方绘制动态材质。

目前支持的材质有：

- [x] LiquidGlass
- [ ] Acrylic

- [ ] Blur

后续材质会复用统一的桌面捕获、着色器缓存和 D3D11 呈现基础设施，并以各自的 `Materials.<MaterialName>` 命名空间、渲染通道和 HLSL 着色器实现。

## 特性

- 面向 `Windows10.0.19041+` 的 Avalonia 12 控件库。
- `MaterialHost` 为一个顶级窗口维护共享的 WGC/D3D11 合成上下文。
- `LiquidGlassContainer` 与 `LiquidGlassButton` 提供可组合的液态玻璃区域。
- `MaterialForeground` 会让材质控件中的前景控件按各自所在位置独立选择深色或浅色文本。
- 内置 HLSL 在**最终用户机器**上编译并缓存；应用可在创建窗口前预热。

## 环境要求

- Windows 10 2004 (build 19041) 或更高版本。
- .NET 10 应用和 Avalonia 12.1.0。
- 能提供 HWND 与 Avalonia 外部 GPU 图像互操作的 Windows 桌面后端。Demo 使用 DirectComposition。
- 可用的 Windows D3D 编译器。首次预热或自动补编译需要 `d3dcompiler_47.dll`。

该包目前仅支持 Windows。它不会以 CPU 路径模拟材质，因此不支持的平台会通过 `MaterialHost.Diagnostics` 报告初始化错误。

## 快速接入

安装包：

```xml
<PackageReference Include="MorerialsAvalonia" Version="1.0.0" />
```

在 `App.axaml` 引入默认模板：

```xml
<Application.Styles>
  <FluentTheme />
  <StyleInclude Source="avares://MorerialsAvalonia/Themes/Generic.axaml" />
</Application.Styles>
```

推荐在创建任何 Avalonia 窗口之前预热着色器：

```csharp
MaterialShaderCompiler.EnsureCompiledAsync().GetAwaiter().GetResult();
```

然后将材质控件放入一个 `MaterialHost`：

```xml
<materials:MaterialHost xmlns:materials="using:MorerialsAvalonia"
                        xmlns:liquid="using:MorerialsAvalonia.Materials.LiquidGlass">
  <liquid:LiquidGlassContainer CornerRadius="24"
                                HighlightIntensity="0.45"
                                materials:MaterialForeground.LightForeground="#FFF8FAFC"
                                materials:MaterialForeground.DarkForeground="#FF101827">
    <TextBlock Margin="24"
               Text="Liquid Glass"
               materials:MaterialForeground.Mode="Automatic" />
  </liquid:LiquidGlassContainer>
</materials:MaterialHost>
```

完整步骤、故障排查和参数示例见[接入教程](docs/INTEGRATION.md)。

## 着色器生命周期

`MaterialShaderCompiler.EnsureCompiledAsync()` 会从程序集资源读取当前版本的 HLSL，在当前用户机器上编译为 CSO，并通过 SHA-256 校验后写入：

```text
%LOCALAPPDATA%\MorerialsAvalonia\Shaders\v1\<程序集版本>
```

缓存使用进程内信号量和跨进程命名互斥体，避免多窗口或多进程重复写入。每次调用都会校验缓存；有效缓存会复用。

预热是推荐的启动流程，因为它避免首次 `MaterialHost` 可见时承担编译耗时。若应用没有预热，宿主会自动调用同一套编译逻辑，并将 `Diagnostics.ShaderState` 置为 `Compiling` 后恢复为 `Ready`；缓存缺失不会成为报错条件。

## 架构与约束

```text
MaterialHost
  -> MaterialBackdropSurface
     -> DesktopCaptureService (Windows Graphics Capture)
     -> D3D11MaterialRenderer
     -> LiquidGlass HLSL pass
  -> LiquidGlassContainer / LiquidGlassButton (区域注册)
```

- 每个 `TopLevel` 只能有一个活动 `MaterialHost`，以避免同一窗口重复捕获和竞争合成表面。
- `ExcludeWindowFromCapture` 默认开启，防止当前窗口递归进入桌面捕获。关闭它只适合布局调试。
- `EnableOcclusionPause` 默认开启；窗口最小化、隐藏或完全遮挡时会暂停捕获和渲染。
- `MaterialHost.Diagnostics` 可用于绑定捕获状态、帧率、GPU 互操作状态、着色器状态和错误信息。
- 透明内容控件本身仍由普通 Avalonia 布局和命中测试处理，GPU 管线只负责其后方的材质像素。
- 动态前景每 1000ms 对每个目标进行固定 3x3 GPU 采样。GPU 归约后，CPU 每次最多只读取 128 个亮度 `float`（512B），绝不读取整张捕获图像。

## DemoGallery

运行开发版：

```powershell
dotnet run --project demos/MorerialsAvalonia.LiquidGlass.DemoGallery -c Debug
```

生成与 GitHub Release 相同类型的发布物：

```powershell
dotnet publish demos/MorerialsAvalonia.LiquidGlass.DemoGallery -c Release -r win-x64 --self-contained true
```

Demo 会在创建窗口前调用 `MaterialShaderCompiler.EnsureCompiledAsync()`，并展示 `MaterialHost`、`LiquidGlassContainer` 与 `LiquidGlassButton` 的组合。所有可见文字和按钮均使用独立自动前景色，颜色变化使用 300ms 过渡动画。

## 构建与验证

```powershell
dotnet restore MorerialsAvalonia.slnx
dotnet build MorerialsAvalonia.slnx -c Release --no-restore
dotnet test tests/MorerialsAvalonia.Tests -c Release --no-build
dotnet pack src/MorerialsAvalonia -c Release --no-build -o artifacts/nuget
```

测试会在当前用户目录验证 HLSL 的首次编译或缓存复用。材质合成不会执行整图 CPU 像素回读；动态前景只会读取固定小型亮度结果缓冲。

`tests/MorerialsAvalonia.PackageSmoke` 有意不加入解决方案默认构建；它必须在包生成后从本地 NuGet 源还原，用于验证真实 `.nupkg` 的 AXAML 编译和嵌入着色器加载。CI 会自动执行这一步。

## 版本与发布

全局版本遵循三段式语义版本：NuGet 使用 `1.0.0`，GitHub Release 标签使用 `v1.0.0`。不接受四段式标签。

推送位于 `master` 可达提交上的 `vX.Y.Z` 标签会触发发布工作流：

- 生成 `MorerialsAvalonia.X.Y.Z.nupkg` 和符号包。
- 发布包到 NuGet.org 与 GitHub Packages。
- 为每个 `*DemoGallery` 项目生成 `win-x64` self-contained、trimmed zip。
- 创建 GitHub Release，并同时上传 `.nupkg` 与所有 DemoGallery zip。

发布说明放在 `docs/CHANGELOG/vX.Y.Z.md`。

仓库需要一个名为 `release` 的受保护 Environment，并在其中配置 NuGet Trusted Publishing 所需的 `NUGET_USERNAME` 变量。GitHub Actions 的 `GITHUB_TOKEN` 由工作流权限用于 GitHub Packages 和 GitHub Release；首次发布前还需在 NuGet.org 与仓库之间完成 Trusted Publishing 的配置。

## 许可

本项目采用 [LGPL-3.0-only](LICENSE)。NuGet 包要求在安装时确认许可。