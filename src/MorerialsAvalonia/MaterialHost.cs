using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using MorerialsAvalonia.Controls;
using MorerialsAvalonia.Rendering;

namespace MorerialsAvalonia;

/// <summary>
/// 为其内容树中的材质控件提供共享 Desktop Duplication 和 D3D11 合成上下文。
/// </summary>
/// <remarks>
/// 每个顶级窗口只能放置一个活动宿主。请将 <c>LiquidGlassContainer</c>
/// 等材质控件放入本控件的内容树中；宿主会在内容后方绘制 GPU 材质表面。
/// </remarks>
public sealed class MaterialHost : ContentControl
{
    private static readonly ConditionalWeakTable<TopLevel, MaterialHost> ActiveHosts = new();
    private MaterialBackdropSurface? _backdropSurface;
    private TopLevel? _topLevel;
    private bool _registeredWithTopLevel;

    /// <summary>
    /// 定义是否将当前窗口排除在桌面捕获之外。
    /// </summary>
    public static readonly StyledProperty<bool> ExcludeWindowFromCaptureProperty =
        AvaloniaProperty.Register<MaterialHost, bool>(nameof(ExcludeWindowFromCapture), true);

    /// <summary>
    /// 定义是否在窗口最小化、隐藏或被完全遮挡时暂停捕获和渲染。
    /// </summary>
    public static readonly StyledProperty<bool> EnableOcclusionPauseProperty =
        AvaloniaProperty.Register<MaterialHost, bool>(nameof(EnableOcclusionPause), true);

    /// <summary>
    /// 初始化 <see cref="MaterialHost"/> 的新实例。
    /// </summary>
    public MaterialHost()
    {
        Diagnostics = new MaterialRenderDiagnostics();
    }

    /// <summary>
    /// 获取可绑定的着色器、捕获和 GPU 呈现诊断。
    /// </summary>
    public MaterialRenderDiagnostics Diagnostics { get; }

    /// <summary>
    /// 获取或设置是否排除宿主所在窗口，默认值为 <see langword="true"/>。
    /// </summary>
    /// <remarks>
    /// 开启后可防止桌面捕获递归包含当前窗口。关闭仅适用于布局调试，
    /// 可能产生递归反馈图像。
    /// </remarks>
    public bool ExcludeWindowFromCapture
    {
        get => GetValue(ExcludeWindowFromCaptureProperty);
        set => SetValue(ExcludeWindowFromCaptureProperty, value);
    }

    /// <summary>
    /// 获取或设置是否在窗口不可见或完全遮挡时暂停渲染，默认值为 <see langword="true"/>。
    /// </summary>
    public bool EnableOcclusionPause
    {
        get => GetValue(EnableOcclusionPauseProperty);
        set => SetValue(EnableOcclusionPauseProperty, value);
    }

    internal MaterialRegionRegistry RegionRegistry { get; } = new();

    // 与区域注册表分离，保证未来材质也能复用同一套低频前景采样基础设施。
    internal ForegroundProbeRegistry ForegroundProbeRegistry { get; } = new();

    internal bool IsActiveForTopLevel => _registeredWithTopLevel;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _backdropSurface = e.NameScope.Find<MaterialBackdropSurface>("PART_BackdropSurface");
        if (_backdropSurface is null)
        {
            Diagnostics.Fail("未找到 MaterialHost 模板中的 PART_BackdropSurface。请加载 MorerialsAvalonia 的 Generic.axaml。");
            return;
        }

        _backdropSurface.AttachHost(this);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is null)
        {
            Diagnostics.Fail("MaterialHost 必须附加到一个 Avalonia TopLevel。");
            return;
        }

        try
        {
            ActiveHosts.Add(_topLevel, this);
            _registeredWithTopLevel = true;
        }
        catch (ArgumentException)
        {
            Diagnostics.Fail("每个 TopLevel 只能存在一个活动 MaterialHost。");
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_registeredWithTopLevel && _topLevel is not null &&
            ActiveHosts.TryGetValue(_topLevel, out var activeHost) && ReferenceEquals(activeHost, this))
            ActiveHosts.Remove(_topLevel);

        _registeredWithTopLevel = false;
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ExcludeWindowFromCaptureProperty)
            _backdropSurface?.UpdateCaptureExclusion();
    }
}
