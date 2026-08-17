# MorerialsAvalonia

适用于 Avalonia 的 Windows GPU 材质控件库。目前包含 `MaterialHost`、`LiquidGlassContainer` 和 `LiquidGlassButton`。

## 使用前准备

在创建 Avalonia 窗口前预热用户侧着色器缓存：

```csharp
MaterialShaderCompiler.EnsureCompiledAsync().GetAwaiter().GetResult();
```

未调用也可以使用：首个 `MaterialHost` 会自动完成同一编译流程，但首次显示可能出现等待。缓存位于 `%LOCALAPPDATA%\MorerialsAvalonia\Shaders`。

## AXAML

先在 `App.axaml` 引入：

```xml
<StyleInclude Source="avares://MorerialsAvalonia/Themes/Generic.axaml" />
```

再将液态玻璃控件置于一个 `MaterialHost` 内：

```xml
<materials:MaterialHost xmlns:materials="using:MorerialsAvalonia"
                        xmlns:liquid="using:MorerialsAvalonia.Materials.LiquidGlass">
  <liquid:LiquidGlassContainer CornerRadius="24"
                                materials:MaterialForeground.LightForeground="#FFF8FAFC"
                                materials:MaterialForeground.DarkForeground="#FF101827">
    <TextBlock Margin="24"
               Text="Liquid Glass"
               materials:MaterialForeground.Mode="Automatic" />
  </liquid:LiquidGlassContainer>
</materials:MaterialHost>
```

## 动态前景色

`MaterialForeground` 默认按每个控件自己的位置独立采样背景亮度。GPU 每 1000ms 对自动目标做固定 3x3 采样并归约；CPU 每轮最多读取 128 个亮度 `float`，不会读取捕获图像。

- `Automatic`：默认值，独立选择浅色或深色前景。
- `Inherit`：不重复采样，继承父级已解析的前景类型。
- `Light` / `Dark`：强制使用配置的颜色。
- `Manual`：保留应用自身的 `Foreground`。

可通过 `LightForeground`、`DarkForeground` 和 `LuminanceThreshold` 定制颜色与判定阈值。为平滑变化，可为控件的 `Foreground`（文本用 `TextElement.Foreground`）配置 Avalonia `BrushTransition`。

每个顶级窗口仅放置一个 `MaterialHost`。完整教程、限制和故障排查：
[MorerialsAvalonia 接入教程](https://github.com/HickoryTrail/MorerialsAvalonia/blob/master/docs/INTEGRATION.md)。

LGPL-3.0-only。
