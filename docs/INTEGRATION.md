# MorerialsAvalonia 接入教程

本教程以现有 Avalonia Windows 桌面应用为对象，接入 `MorerialsAvalonia` 的 Liquid Glass 材质。完成后，材质背景由 Windows Graphics Capture 和 D3D11 在 GPU 中持续合成；普通 Avalonia 控件仍负责布局、文本、输入和业务逻辑。

## 1. 确认前提条件

当前版本只支持 Windows 10 2004 (19041) 及以上，并面向 Avalonia 12.1.0 与 .NET 10。应用必须通过 Windows 桌面后端创建 HWND，且当前 Avalonia 渲染器必须支持外部 GPU 图像互操作。

不要为不支持的系统实现 CPU 截图或模糊备用效果。该库刻意不提供这种路径，以确保材质的性能和画面一致性。

Windows 桌面应用应选择能提供合成互操作的后端；Demo 使用下面的配置：

```csharp
using Avalonia;

public static AppBuilder BuildAvaloniaApp()
    => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new Win32PlatformOptions
        {
            RenderingMode = [Win32RenderingMode.AngleEgl],
            CompositionMode = [Win32CompositionMode.DirectComposition]
        });
```

如果应用已有自己的 `AppBuilder` 配置，请保留其字体、日志和生命周期设置，只需确认最终 Windows 后端支持 D3D11 外部纹理互操作。

## 2. 安装 NuGet 包

在应用项目中添加：

```xml
<ItemGroup>
  <PackageReference Include="MorerialsAvalonia" Version="1.0.0" />
</ItemGroup>
```

如果你的仓库使用集中式包版本管理，则把版本添加到相应的 `Directory.Packages.props`。

## 3. 引入默认模板

`MaterialHost` 的后台 GPU 表面由包内 `Generic.axaml` 模板创建。将下面一行放进 `App.axaml` 的 `Application.Styles`，并保留应用原有主题：

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="YourApp.App">
  <Application.Styles>
    <FluentTheme />
    <StyleInclude Source="avares://MorerialsAvalonia/Themes/Generic.axaml" />
  </Application.Styles>
</Application>
```

缺少该 `StyleInclude` 时，`MaterialHost.Diagnostics.Error` 会指出找不到 `PART_BackdropSurface`。

## 4. 在启动时预热着色器

库不会在打包时携带与用户 GPU/Windows 环境绑定的已编译 CSO。它在用户机器上从嵌入的 HLSL 进行编译，并将通过哈希校验的结果缓存在当前用户目录。

推荐在任何 Avalonia 窗口创建之前调用：

```csharp
using MorerialsAvalonia;

[STAThread]
public static int Main(string[] args)
{
    MaterialShaderCompiler.EnsureCompiledAsync().GetAwaiter().GetResult();
    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    return 0;
}
```

`EnsureCompiledAsync` 的结果包含 `CacheDirectory`、`CompiledShaderCount` 和 `ReusedShaderCount`，适合写入应用自己的启动日志：

```csharp
var result = await MaterialShaderCompiler.EnsureCompiledAsync();
logger.LogInformation(
    "材质着色器: 编译 {Compiled}，复用 {Reused}，目录 {Directory}",
    result.CompiledShaderCount,
    result.ReusedShaderCount,
    result.CacheDirectory);
```

### 忘记预热时的行为

预热不是硬性前置条件。若首个 `MaterialHost` 发现没有有效缓存，它会在该窗口的初始化期间调用同一编译器：

1. `Diagnostics.ShaderState` 变为 `Compiling`。
2. HLSL 在用户机器上编译，缓存被原子写入。
3. 成功后状态变为 `Ready`，随后创建 WGC/D3D11 管线。

因此不会因为“缓存尚未生成”报错；代价是首次窗口显示可能多一次编译等待。编译器本身或 HLSL 真正失败时，错误会写入 `Diagnostics.Error`。

## 5. 用 `MaterialHost` 包裹窗口内容

每个 Avalonia `TopLevel` 仅创建一个活动宿主。将其视为该窗口的材质合成根，而不是每张卡片都放一个宿主。

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:materials="using:MorerialsAvalonia"
        xmlns:liquid="using:MorerialsAvalonia.Materials.LiquidGlass"
        x:Class="YourApp.MainWindow">
  <materials:MaterialHost x:Name="Materials"
                          ExcludeWindowFromCapture="True"
                          EnableOcclusionPause="True">
    <Grid Margin="32">
      <liquid:LiquidGlassContainer CornerRadius="24"
                                    HighlightIntensity="0.45">
        <StackPanel Margin="24" Spacing="10">
          <TextBlock FontSize="20" FontWeight="SemiBold" Text="设置" />
          <TextBlock Opacity="0.75" Text="内容保持正常的 Avalonia 布局与输入行为。" />
        </StackPanel>
      </liquid:LiquidGlassContainer>
    </Grid>
  </materials:MaterialHost>
</Window>
```

`ExcludeWindowFromCapture` 默认值为 `true`。它会把当前窗口排除在桌面捕获外，防止材质不断捕获自身而形成递归画面。只在调试布局时才应关闭它。

`EnableOcclusionPause` 默认值为 `true`。当窗口最小化、隐藏或被完全遮挡时，捕获和呈现会暂停，重新可见后恢复。

## 6. 添加液态玻璃控件

### `LiquidGlassContainer`

用在卡片、工具栏、标题栏和任意带子内容的区域。它是透明的 `ContentControl`，GPU 只在其注册区域绘制玻璃背景：

```xml
<liquid:LiquidGlassContainer CornerRadius="18" HighlightIntensity="0.55">
  <Grid Margin="18">
    <!-- 任何普通 Avalonia 内容 -->
  </Grid>
</liquid:LiquidGlassContainer>
```

### `LiquidGlassButton`

用在需要玻璃区域并带悬停/按下弹簧动画的按钮。它继承 Avalonia `Button`，可照常使用 `Command`、`Click` 和键盘操作：

```xml
<liquid:LiquidGlassButton Content="应用"
                          CornerRadius="20"
                          HighlightIntensity="0.7"
                          Command="{Binding ApplyCommand}" />
```

默认模板使用原生 `Border.BoxShadow` 提供按钮下方阴影，并以透明的完整按钮区域承接命中测试；文字会由 `ContentPresenter` 在按钮区域内居中。

`LiquidGlassButton` 是 `Button` 的派生控件。Avalonia 的 `Button.xxx` 是精确类型选择器，不能匹配该派生类型；为按钮添加样式时请使用 `:is(Button)`，或直接指定液态玻璃按钮类型：

```xml
<Style Selector=":is(Button).glass-action">
  <Setter Property="Width" Value="110" />
  <Setter Property="Height" Value="40" />
  <Setter Property="Padding" Value="20,10" />
  <Setter Property="HorizontalContentAlignment" Value="Center" />
  <Setter Property="VerticalContentAlignment" Value="Center" />
</Style>

<!-- 等价的精确类型写法： -->
<Style Selector="liquid|LiquidGlassButton.glass-action" />
```

两个控件都必须位于 `MaterialHost` 内容树中。放在宿主外时，它们不会注册 GPU 材质区域。

## 7. 调整材质参数

`LiquidGlassProfiles.Reference` 是稳定的默认预设。可用 C# `with` 表达式局部修改并绑定到控件：

```csharp
using MorerialsAvalonia.Materials.LiquidGlass;

var strongerGlass = LiquidGlassProfiles.Reference with
{
    BlurRadius = 3.5,
    NoiseIntensity = 0.02,
    Highlight = LiquidGlassProfiles.Reference.Highlight with
    {
        Intensity = 0.65,
        BorderWidth = 1.25
    }
};
```

```xml
<liquid:LiquidGlassContainer Material="{Binding StrongerGlass}"
                              CornerRadius="28" />
```

常用属性：

| 属性 | 用途 |
| --- | --- |
| `CornerRadius` | 区域圆角，单位 DIP。 |
| `HighlightIntensity` | 单控件覆盖高光强度；`NaN` 时使用 `Material.Highlight.Intensity`。 |
| `Material.BlurRadius` | 高斯模糊半径，单位 DIP。 |
| `Material.BlurDownsampleScale` | 模糊中间纹理缩放，范围 `0.1..1`。 |
| `Material.RefractionCurve` | 边缘折射曲线。 |
| `Material.Glow` | 方向性边缘发光。 |
| `Material.Highlight` | 描边和内侧反射高光。 |

## 8. 动态前景色

`LiquidGlassContainer` 和 `LiquidGlassButton` 会为自身及其内容树中的可前景控件建立独立亮度探针。默认 `Automatic` 模式会基于该控件位置的桌面捕获纹理选择浅色或深色前景，因此较大的窗口里，不同位置的文本和按钮可以分别适配背景明暗。

每 1000ms，GPU 会对每个自动目标做固定 3x3 采样、在线性空间归约为一个相对亮度值。CPU 不会读取桌面图像或材质纹理，只会接收每个控件一个 `float` 结果；单个 `MaterialHost` 每轮最多处理 128 个目标，即最多 512B 的读取量。

```xml
<liquid:LiquidGlassContainer xmlns:materials="using:MorerialsAvalonia"
                              CornerRadius="24"
                              materials:MaterialForeground.LightForeground="#FFF8FAFC"
                              materials:MaterialForeground.DarkForeground="#FF101827"
                              materials:MaterialForeground.LuminanceThreshold="0.45">
  <StackPanel Margin="24" Spacing="8">
    <TextBlock Text="独立采样的标题"
               materials:MaterialForeground.Mode="Automatic">
      <TextBlock.Transitions>
        <Transitions>
          <BrushTransition Property="TextElement.Foreground" Duration="0:0:0.3" />
        </Transitions>
      </TextBlock.Transitions>
    </TextBlock>

    <liquid:LiquidGlassButton Content="保存"
                              materials:MaterialForeground.Mode="Automatic">
      <liquid:LiquidGlassButton.Transitions>
        <Transitions>
          <BrushTransition Property="Foreground" Duration="0:0:0.3" />
        </Transitions>
      </liquid:LiquidGlassButton.Transitions>
    </liquid:LiquidGlassButton>
  </StackPanel>
</liquid:LiquidGlassContainer>
```

`MaterialForeground.Mode` 的取值：

| 模式 | 行为 |
| --- | --- |
| `Automatic` | 默认值。控件独立采样并自动选择 `LightForeground` 或 `DarkForeground`。 |
| `Inherit` | 不创建新探针，继承父级已解析的颜色和 `ResolvedKind`，适合大量重复子项。 |
| `Light` / `Dark` | 不采样，强制使用配置的浅色或深色前景。该模式可继承给子控件。 |
| `Manual` | 不干预普通 Avalonia `Foreground`，适合业务自行设置颜色的控件。 |

`LightForeground`、`DarkForeground`、`LuminanceThreshold` 和 `ResolvedKind` 都是可继承附加属性。`ResolvedKind` 可绑定到样式或视图模型；对于 128 个自动目标以外的重复内容，请将父级自动采样、子级设为 `Inherit`，避免不必要的采样。

## 9. 观察运行状态

`MaterialHost.Diagnostics` 是可绑定的 `INotifyPropertyChanged` 对象，可用于开发版状态栏、日志或错误提示。关键属性包括：

- `ShaderState`：`NotPrepared`、`Compiling`、`Ready` 或 `Failed`。
- `CaptureState`：WGC 会话状态。
- `Adapter`：与 Avalonia 合成器匹配的 D3D11 适配器。
- `InteropState`：共享纹理/合成互操作状态。
- `FramesPerSecond`、`CaptureFramesPerSecond`、`CaptureFrameAgeMilliseconds` 和 `DroppedFrames`。
- `Error`：不可恢复的初始化或合成错误。

例如，在代码中订阅诊断而不改变 UI：

```csharp
Materials.Diagnostics.PropertyChanged += (_, eventArgs) =>
{
    if (eventArgs.PropertyName == nameof(MaterialRenderDiagnostics.Error) &&
        Materials.Diagnostics.HasError)
    {
        logger.LogError("材质初始化失败: {Error}", Materials.Diagnostics.Error);
    }
};
```

## 10. 常见问题

### 首次打开窗口较慢

在 `Main` 中调用 `MaterialShaderCompiler.EnsureCompiledAsync()`。这会把编译成本移到创建窗口之前；后续启动会命中 SHA-256 校验的缓存。

### 窗口显示但没有材质，或诊断提示模板部件缺失

检查 `App.axaml` 是否包含：

```xml
<StyleInclude Source="avares://MorerialsAvalonia/Themes/Generic.axaml" />
```

### 出现“每个 TopLevel 只能存在一个活动 MaterialHost”

同一窗口只保留最外层的一个 `MaterialHost`，把所有液态玻璃卡片和按钮移入其内容树。

### 录屏或截图里看不到应用窗口

默认的 `ExcludeWindowFromCapture` 会让当前窗口不进入 WGC 捕获，以防递归。录屏工具的行为依实现而异；如需临时调试，明确设为 `False`，完成后恢复默认值。

### `ShaderState` 为 `Failed`

查看 `Diagnostics.Error`。确认运行在受支持的 Windows 版本、D3D 编译器可用，且用户具有 `%LOCALAPPDATA%` 缓存目录的写入权限。不要手工复制其他机器的 CSO；请在目标用户机器重新调用编译 API。

### 动态前景色没有立即变化

自动模式固定每 1000ms 采样一次，以避免为文本颜色引入高频 CPU/GPU 同步。Demo 使用 300ms `BrushTransition` 平滑切换；应用可按上面的 XAML 示例为自身控件添加相同过渡。检查该控件是否处于 `MaterialHost` 内容树，且没有更高优先级的本地 `Foreground` 值覆盖动态颜色。

## 11. 发布自己的应用

该库可随普通框架依赖或 self-contained Avalonia 应用分发。示例命令：

```powershell
dotnet publish YourApp.csproj -c Release -r win-x64 --self-contained true
```

首个材质窗口前仍应保留预热调用。对于宿主应用，建议记录 `MaterialShaderCompilationResult` 和 `MaterialHost.Diagnostics.Error`，这样可区分首次缓存编译、设备互操作失败和 WGC 权限/平台问题。

## 12. 参与本项目发布

项目使用三段式 Git 标签：例如 `v1.0.0`。发布工作流会检查标签是否位于 `master` 历史，并要求存在 `docs/CHANGELOG/v1.0.0.md`。通过检查后，工作流会生成包、运行包消费烟雾测试、发布每个 DemoGallery 的 `win-x64` self-contained trimmed zip，然后在受保护的 `release` Environment 中发布 NuGet.org、GitHub Packages 和 GitHub Release。

仓库管理员需要在 `release` Environment 设置 `NUGET_USERNAME`，并在 NuGet.org 配置 `NuGet/login@v1` 使用的 Trusted Publishing。GitHub Packages 使用工作流自动提供的 `GITHUB_TOKEN`。
