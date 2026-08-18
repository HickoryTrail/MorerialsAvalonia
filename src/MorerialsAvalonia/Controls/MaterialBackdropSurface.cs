using System.Numerics;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MorerialsAvalonia.Capture;
using MorerialsAvalonia.Diagnostics;
using MorerialsAvalonia.Native;
using MorerialsAvalonia.Rendering;

namespace MorerialsAvalonia.Controls;

/// <summary>
/// MaterialHost 模板内部的 GPU 呈现层。
/// 它不参与命中测试，只在内容控件后方显示共享 D3D11 纹理。
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class MaterialBackdropSurface : Control
{
    private readonly Action _updateFrame;
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _activityTimer;
    private CompositionSurfaceVisual? _visual;
    private CompositionDrawingSurface? _surface;
    private Compositor? _compositor;
    private D3D11MaterialRenderer? _renderer;
    private MaterialHost? _host;
    private TopLevel? _topLevel;
    private nint _hwnd;
    private uint _previousDisplayAffinity;
    private bool _captureExclusionApplied;
    private bool _updateQueued;
    private bool _initialized;
    private bool _initializing;
    private bool _renderActive;
    private int _invalidationPosted;

    /// <summary>
    /// 初始化模板内部使用的 GPU 呈现层。
    /// 应由 <see cref="MaterialHost"/> 的默认模板创建，而不是由应用程序直接使用。
    /// </summary>
    public MaterialBackdropSurface()
    {
        IsHitTestVisible = false;
        _updateFrame = UpdateFrame;
        _renderTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(5),
            DispatcherPriority.Render,
            (_, _) => OnRenderTimerTick());
        _activityTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background,
            (_, _) => UpdateRenderActivity());
    }

    internal void AttachHost(MaterialHost host)
    {
        if (_host is not null && !ReferenceEquals(_host, host))
            throw new InvalidOperationException("MaterialBackdropSurface 不能重新附加到另一个 MaterialHost。");

        _host = host;

        // ControlTemplate 的应用顺序不保证：表面可能已经附加到视觉树。
        // 此处补启动可避免该顺序下永远不创建 GPU 合成表面。
        BeginHostSession();
    }

    internal void UpdateCaptureExclusion()
    {
        if (_hwnd == 0 || _host is null)
            return;

        if (_host.ExcludeWindowFromCapture)
        {
            if (_captureExclusionApplied)
                return;

            _previousDisplayAffinity = WindowsNative.TryGetWindowDisplayAffinity(_hwnd, out var affinity)
                ? affinity
                : WindowsNative.WdaNone;
            _captureExclusionApplied = WindowsNative.SetWindowDisplayAffinity(
                _hwnd,
                WindowsNative.WdaExcludeFromCapture) != 0;
            return;
        }

        RestoreCaptureExclusion();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BeginHostSession();
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        _initialized = false;
        _renderActive = false;
        _updateQueued = false;
        _renderTimer.Stop();
        _activityTimer.Stop();
        if (_topLevel is not null)
            _topLevel.PropertyChanged -= OnTopLevelPropertyChanged;
        if (_host is not null)
        {
            _host.RegionRegistry.Changed -= OnRenderInvalidated;
            _host.ForegroundProbeRegistry.Changed -= OnRenderInvalidated;
        }
        Interlocked.Exchange(ref _invalidationPosted, 0);
        if (_renderer is not null)
            _renderer.RenderInvalidated -= OnRenderInvalidated;
        _renderer?.Dispose();
        _renderer = null;
        _surface?.Dispose();
        _surface = null;
        _visual = null;
        _compositor = null;
        RestoreCaptureExclusion();
        _topLevel = null;
        _hwnd = 0;
        base.OnDetachedFromLogicalTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && _renderActive)
            QueueNextFrame();
        else if (change.Property == Visual.IsVisibleProperty)
            UpdateRenderActivity();
    }

    private async void InitializeComposition()
    {
        if (_initializing || _initialized || _host is null)
            return;

        _initializing = true;
        try
        {
            if (_topLevel is null || TopLevel.GetTopLevel(this) is null || !_host.IsActiveForTopLevel)
                return;

            // 用户预热后这里通常只校验哈希和缓存；漏调预热时也不会中断窗口启动。
            _host.Diagnostics.ShaderState = MaterialShaderState.Compiling;
            await MaterialShaderCompiler.EnsureCompiledAsync();
            _host.Diagnostics.ShaderState = MaterialShaderState.Ready;

            var elementVisual = ElementComposition.GetElementVisual(this)
                ?? throw new InvalidOperationException("Avalonia 没有为材质表面创建合成视觉对象。");
            _compositor = elementVisual.Compositor;
            _surface = _compositor.CreateDrawingSurface();
            _visual = _compositor.CreateSurfaceVisual();
            _visual.Size = new Vector2((float)Bounds.Width, (float)Bounds.Height);
            _visual.Surface = _surface;
            ElementComposition.SetElementChildVisual(this, _visual);

            var interop = await _compositor.TryGetCompositionGpuInterop()
                ?? throw new NotSupportedException("当前 Avalonia 渲染器不支持外部 GPU 图像互操作。");
            var handle = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle();
            if (handle is null || handle.Handle == 0 || handle.HandleDescriptor != "HWND")
                throw new NotSupportedException("MaterialHost 需要 Windows HWND 才能执行桌面捕获。");

            _hwnd = handle.Handle;
            UpdateCaptureExclusion();
            _renderer = new D3D11MaterialRenderer(
                _hwnd,
                interop,
                _surface,
                _host.Diagnostics,
                _host.ForegroundProbeRegistry);
            _renderer.RenderInvalidated += OnRenderInvalidated;
            _initialized = true;
            UpdateRenderActivity();
            _renderer.SetSuspended(!_renderActive);
            if (_renderActive)
                QueueNextFrame();
        }
        catch (Exception exception)
        {
            MaterialLogger.Write("MaterialHost 初始化失败", exception);
            _host.Diagnostics.Fail(exception.Message);
        }
        finally
        {
            _initializing = false;
        }
    }

    private void BeginHostSession()
    {
        if (_host is null || _topLevel is not null)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        _topLevel = topLevel;
        _topLevel.PropertyChanged += OnTopLevelPropertyChanged;
        _host.RegionRegistry.Changed += OnRenderInvalidated;
        _host.ForegroundProbeRegistry.Changed += OnRenderInvalidated;
        _activityTimer.Start();
        UpdateRenderActivity();

        // 宿主的单窗口注册在父控件附加后完成，延后到 Loaded 阶段避免首次附加时误判。
        Dispatcher.UIThread.Post(InitializeComposition, DispatcherPriority.Loaded);
    }

    private void QueueNextFrame()
    {
        if (!_initialized || !_renderActive || _updateQueued || _compositor is null)
            return;

        _updateQueued = true;
        _compositor.RequestCompositionUpdate(_updateFrame);
    }

    private void OnRenderInvalidated()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            QueueNextFrame();
            return;
        }

        if (Interlocked.Exchange(ref _invalidationPosted, 1) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _invalidationPosted, 0);
            QueueNextFrame();
        }, DispatcherPriority.Render);
    }

    private void OnRenderTimerTick()
    {
        if (!_renderActive || !_initialized || _renderer is null || _host is null)
            return;

        try
        {
            // WGC 源切换可能泵送 WinRT 消息，必须在合成提交之外执行。
            _renderer.RefreshCaptureSource();
        }
        catch (Exception exception)
        {
            MaterialLogger.Write("Windows Graphics Capture 源刷新失败", exception);
            _host.Diagnostics.Fail(exception.Message);
            _initialized = false;
            return;
        }

        var presentationSource = this.GetPresentationSource();
        if (presentationSource is null)
            return;

        var pixelSize = PixelSize.FromSize(Bounds.Size, presentationSource.RenderScaling);
        var surfaceOffset = GetSurfacePixelOffset(presentationSource.RenderScaling);
        if (_renderer.NeedsRender(
                pixelSize,
                surfaceOffset,
                _host.RegionRegistry.Version,
                _host.ForegroundProbeRegistry.Count,
                _host.ForegroundProbeRegistry.Version))
            QueueNextFrame();
    }

    private void UpdateFrame()
    {
        _updateQueued = false;
        if (!_initialized || _renderer is null || _visual is null || _host is null)
            return;

        try
        {
            var presentationSource = this.GetPresentationSource();
            if (presentationSource is null)
                return;

            _visual.Size = new Vector2((float)Bounds.Width, (float)Bounds.Height);
            var pixelSize = PixelSize.FromSize(Bounds.Size, presentationSource.RenderScaling);
            var surfaceOffset = GetSurfacePixelOffset(presentationSource.RenderScaling);
            Span<MaterialRegion> regions = stackalloc MaterialRegion[MaterialRegionRegistry.MaximumRegions];
            var count = _host.RegionRegistry.CopyTo(regions, out var regionVersion);
            Span<ForegroundProbe> foregroundProbes = stackalloc ForegroundProbe[ForegroundProbeRegistry.MaximumProbes];
            var foregroundProbeCount = _host.ForegroundProbeRegistry.CopyTo(
                foregroundProbes,
                out var foregroundProbeVersion);
            _renderer.Render(
                pixelSize,
                surfaceOffset,
                regions[..count],
                regionVersion,
                foregroundProbes[..foregroundProbeCount],
                foregroundProbeVersion);
        }
        catch (Exception exception)
        {
            MaterialLogger.Write("材质合成帧失败", exception);
            _host.Diagnostics.Fail($"材质合成帧失败: {exception.Message}");
            _initialized = false;
        }
    }

    private void OnTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == Window.WindowStateProperty || change.Property == Visual.IsVisibleProperty)
            UpdateRenderActivity();
    }

    private PixelPoint GetSurfacePixelOffset(double renderScaling)
    {
        if (_topLevel is null || this.TranslatePoint(default, _topLevel) is not { } topLeft)
            return default;

        return new PixelPoint(
            (int)Math.Round(topLeft.X * renderScaling),
            (int)Math.Round(topLeft.Y * renderScaling));
    }

    private void UpdateRenderActivity()
    {
        try
        {
            var pauseWhenOccluded = _host?.EnableOcclusionPause == true;
            var minimized = _topLevel is Window { WindowState: WindowState.Minimized };
            var occluded = pauseWhenOccluded && _hwnd != 0 && WindowsNative.IsWindowFullyOccluded(_hwnd);
            var active = IsEffectivelyVisible &&
                _topLevel is { IsEffectivelyVisible: true } &&
                !minimized &&
                !occluded;
            if (_renderActive == active)
                return;

            _renderActive = active;
            if (!active)
            {
                _renderTimer.Stop();
                _renderer?.SetSuspended(true);
                return;
            }

            _renderer?.SetSuspended(false);
            _renderTimer.Start();
            QueueNextFrame();
        }
        catch (Exception exception)
        {
            _renderActive = false;
            _renderTimer.Stop();
            MaterialLogger.Write("材质渲染活动状态切换失败", exception);
            _host?.Diagnostics.Fail($"材质渲染活动状态切换失败: {exception.Message}");
        }
    }

    private void RestoreCaptureExclusion()
    {
        if (!_captureExclusionApplied || _hwnd == 0)
            return;

        WindowsNative.SetWindowDisplayAffinity(_hwnd, _previousDisplayAffinity);
        _captureExclusionApplied = false;
        _previousDisplayAffinity = WindowsNative.WdaNone;
    }
}
