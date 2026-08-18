using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using MorerialsAvalonia;
using MorerialsAvalonia.Capture;
using MorerialsAvalonia.Diagnostics;
using MorerialsAvalonia.Native;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using static Silk.NET.Core.Native.SilkMarshal;

namespace MorerialsAvalonia.Rendering;

internal sealed unsafe class D3D11MaterialRenderer : IDisposable
{
    private const int OutputRingSize = 3;
    private const int ForegroundReadbackSlotCount = 2;
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const int DxgiErrorWasStillDrawing = unchecked((int)0x887A000A);
    private const uint D3D11MapFlagDoNotWait = 0x100000;
    private const float BlurMixEpsilon = 0.001f;
    private const int MaximumGaussianRadius = 64;

    private readonly nint _hwnd;
    private readonly ICompositionGpuInterop _interop;
    private readonly CompositionDrawingSurface _surface;
    private readonly MaterialRenderDiagnostics _diagnostics;
    private readonly ForegroundProbeRegistry _foregroundProbes;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly OutputSlot?[] _outputSlots = new OutputSlot[OutputRingSize];
    private readonly ForegroundReadbackSlot?[] _foregroundReadbackSlots =
        new ForegroundReadbackSlot[ForegroundReadbackSlotCount];
    private ComPtr<ID3D11Device> _device;
    private ComPtr<ID3D11DeviceContext> _context;
    private ComPtr<ID3D11ComputeShader> _gaussianBlurShader;
    private ComPtr<ID3D11ComputeShader> _compositeShader;
    private ComPtr<ID3D11ComputeShader> _foregroundLuminanceShader;
    private ComPtr<ID3D11SamplerState> _linearSampler;
    private ComPtr<ID3D11Buffer> _gaussianBlurConstants;
    private ComPtr<ID3D11Buffer> _compositeConstants;
    private ComPtr<ID3D11Buffer> _foregroundLuminanceConstants;
    private ComPtr<ID3D11Buffer> _foregroundLuminanceOutput;
    private ComPtr<ID3D11UnorderedAccessView> _foregroundLuminanceOutputUav;
    private ComPtr<ID3D11Texture2D> _captureTexture;
    private ComPtr<ID3D11ShaderResourceView> _captureView;
    private ComPtr<ID3D11RenderTargetView> _captureRenderTarget;
    private ComPtr<ID3D11Texture2D> _blurTexture;
    private ComPtr<ID3D11ShaderResourceView> _blurView;
    private ComPtr<ID3D11UnorderedAccessView> _blurUav;
    private ComPtr<ID3D11Texture2D> _blurIntermediateTexture;
    private ComPtr<ID3D11ShaderResourceView> _blurIntermediateView;
    private ComPtr<ID3D11UnorderedAccessView> _blurIntermediateUav;
    private DesktopCaptureService? _capture;
    private PixelSize _outputSize;
    private PixelSize _captureSize;
    private PixelSize _blurSize;
    private long _renderedFrames;
    private long _skippedFrames;
    private long _idleSkippedFrames;
    private long _captureCopies;
    private long _captureClears;
    private long _captureClearSkipped;
    private long _blurDispatches;
    private long _fpsFrameBase;
    private long _captureFrameBase;
    private long _nextCaptureSourceCheck;
    private long _lastPresentedCaptureVersion = -1;
    private long _lastObservedCaptureVersion = -1;
    private long _pendingCaptureVersion = -1;
    private long _lastPresentedRegionVersion = -1;
    private long _lastForegroundProbeVersion = -1;
    private long _nextForegroundSamplingTick;
    private PixelSize _lastPresentedOutputSize;
    private PixelPoint _lastPresentedSurfaceOffset;
    private PixelPoint _captureSourceOrigin;
    private bool _hasCaptureSourceOrigin;
    private double _fpsTimeBase;
    private bool _suspended;
    private bool _disposed;

    internal D3D11MaterialRenderer(
        nint hwnd,
        ICompositionGpuInterop interop,
        CompositionDrawingSurface surface,
        MaterialRenderDiagnostics diagnostics,
        ForegroundProbeRegistry foregroundProbes)
    {
        _hwnd = hwnd;
        _interop = interop;
        _surface = surface;
        _diagnostics = diagnostics;
        _foregroundProbes = foregroundProbes;

        if (!interop.SupportedImageHandleTypes.Contains(
                KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle))
            throw new NotSupportedException(
                "The active Avalonia GPU backend cannot import D3D11 shared texture handles.");

        CreateDevice(interop.DeviceLuid);
        CreatePipelineResources();
        _capture = new DesktopCaptureService(_device, diagnostics);
        _capture.FrameAvailable += OnCaptureFrameAvailable;
        _capture.EnsureMonitor(_hwnd);
        _nextCaptureSourceCheck = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
        _diagnostics.InteropState = "D3D11 shared handle + keyed mutex ready";
    }

    internal event Action? RenderInvalidated;

    internal bool NeedsRender(
        PixelSize outputSize,
        PixelPoint surfaceOffset,
        long regionVersion,
        int foregroundProbeCount,
        long foregroundProbeVersion)
    {
        if (_disposed || _suspended || _capture is null ||
            outputSize.Width <= 0 || outputSize.Height <= 0 || _capture.IsSuspended)
            return false;

        // 首个 Desktop Duplication 帧到达前没有可呈现纹理；等待期间让合成回调保持空闲，避免忙等。
        if (_captureView.Handle is null)
            return _capture.HasLatestFrame;

        return NeedsComposite(outputSize, surfaceOffset, regionVersion) ||
               HasPendingForegroundReadback ||
               IsForegroundSamplingDue(foregroundProbeCount, foregroundProbeVersion);
    }

    internal void SetSuspended(bool suspended)
    {
        if (_disposed || _capture is null || _suspended == suspended)
            return;

        if (suspended)
        {
            _suspended = true;
            _capture.Suspend();
            _diagnostics.FramesPerSecond = 0;
            _diagnostics.CaptureFramesPerSecond = 0;
            _diagnostics.CaptureFrameAgeMilliseconds = double.NaN;
            return;
        }

        _capture.Resume(_hwnd);
        _suspended = false;
        _lastPresentedCaptureVersion = -1;
        _pendingCaptureVersion = -1;
        _lastObservedCaptureVersion = -1;
        _lastPresentedRegionVersion = -1;
        _lastPresentedOutputSize = default;
        _lastPresentedSurfaceOffset = default;
        _captureSourceOrigin = default;
        _hasCaptureSourceOrigin = false;
        _lastForegroundProbeVersion = -1;
        _nextForegroundSamplingTick = 0;
    }

    internal void Render(
        PixelSize outputSize,
        PixelPoint surfaceOffset,
        ReadOnlySpan<MaterialRegion> regions,
        long regionVersion,
        ReadOnlySpan<ForegroundProbe> foregroundProbes,
        long foregroundProbeVersion)
    {
        if (_disposed || _suspended || outputSize.Width <= 0 || outputSize.Height <= 0)
            return;

        CaptureFrameLease? pendingCaptureFrame = null;
        try
        {
            TryReadForegroundLuminance();
            var needsComposite = NeedsComposite(outputSize, surfaceOffset, regionVersion);
            var needsForegroundSampling = _captureView.Handle is not null &&
                IsForegroundSamplingDue(foregroundProbes.Length, foregroundProbeVersion);
            if (!needsComposite && !needsForegroundSampling)
            {
                _idleSkippedFrames++;
                return;
            }

            // 单独的低频亮度采样不需要借用 Avalonia 输出纹理或 keyed mutex 槽位。
            if (!needsComposite)
            {
                DispatchForegroundLuminance(foregroundProbes, surfaceOffset, foregroundProbeVersion);
                _context.Flush();
                PublishMetrics();
                return;
            }

            EnsureOutputResources(outputSize);
            var slot = FindAvailableOutputSlot();
            if (slot is null)
            {
                _skippedFrames++;
                PublishMetrics();
                return;
            }

            var acquireResult = slot.Mutex.AcquireSync(0, 0);
            if (acquireResult != 0)
            {
                _skippedFrames++;
                PublishMetrics();
                return;
            }

            var released = false;
            try
            {
                if (CopyLatestCaptureFrame(
                        outputSize,
                        surfaceOffset,
                        out var captureVersion,
                        out pendingCaptureFrame))
                {
                    _pendingCaptureVersion = captureVersion;
                    _captureCopies++;
                }

                var shouldComposite = _captureView.Handle is not null &&
                    (_pendingCaptureVersion != _lastPresentedCaptureVersion ||
                     _lastPresentedRegionVersion != regionVersion ||
                     _lastPresentedOutputSize != outputSize ||
                     _lastPresentedSurfaceOffset != surfaceOffset);
                needsForegroundSampling = _captureView.Handle is not null &&
                    IsForegroundSamplingDue(foregroundProbes.Length, foregroundProbeVersion);
                if (shouldComposite)
                {
                    var maximumBlurRadius = GetMaximumBlurRadius(regions);
                    if (maximumBlurRadius > 0.001f)
                    {
                        var blurDownsampleScale = GetBlurDownsampleScale(regions);
                        EnsureBlurResources(blurDownsampleScale);
                        DispatchGaussianBlur(maximumBlurRadius, blurDownsampleScale);
                        _blurDispatches += 2;
                    }

                    DispatchComposite(slot, regions, surfaceOffset, maximumBlurRadius);
                }

                if (needsForegroundSampling)
                    DispatchForegroundLuminance(foregroundProbes, surfaceOffset, foregroundProbeVersion);

                if (shouldComposite || needsForegroundSampling)
                    _context.Flush();

                pendingCaptureFrame?.MarkConsumed();
                pendingCaptureFrame?.Dispose();
                pendingCaptureFrame = null;

                if (shouldComposite)
                {
                    ThrowHResult(slot.Mutex.ReleaseSync(1));
                    released = true;
                    slot.Present();
                    _renderedFrames++;
                    _lastPresentedCaptureVersion = _pendingCaptureVersion;
                    _lastPresentedRegionVersion = regionVersion;
                    _lastPresentedOutputSize = outputSize;
                    _lastPresentedSurfaceOffset = surfaceOffset;
                    _diagnostics.IsOperational = true;
                    _diagnostics.Error = null;
                }
            }
            finally
            {
                if (!released)
                    slot.Mutex.ReleaseSync(0);
            }

            PublishMetrics();
        }
        catch (Exception exception)
        {
            pendingCaptureFrame?.MarkConsumed();
            pendingCaptureFrame?.Dispose();
            MaterialLogger.Write("D3D11 材质渲染失败", exception);
            _diagnostics.Fail($"GPU 材质渲染已停止: {exception.Message}");
            throw;
        }
    }

    private bool NeedsComposite(PixelSize outputSize, PixelPoint surfaceOffset, long regionVersion)
        => _capture!.HasPendingFrameForCrop(
               outputSize,
               surfaceOffset,
               _hwnd,
               _lastObservedCaptureVersion) ||
           HasCaptureSourceOriginChanged(surfaceOffset) ||
           _pendingCaptureVersion != _lastPresentedCaptureVersion ||
           _lastPresentedRegionVersion != regionVersion ||
           _lastPresentedOutputSize != outputSize ||
           _lastPresentedSurfaceOffset != surfaceOffset;

    private bool HasCaptureSourceOriginChanged(PixelPoint surfaceOffset)
    {
        if (_capture is null || _capture.Monitor == 0)
            return false;

        var monitorRect = WindowsNative.GetMonitorRect(_capture.Monitor);
        var clientOrigin = WindowsNative.GetClientScreenOrigin(_hwnd);
        var origin = new PixelPoint(
            clientOrigin.X - monitorRect.Left + surfaceOffset.X,
            clientOrigin.Y - monitorRect.Top + surfaceOffset.Y);
        return !_hasCaptureSourceOrigin || origin != _captureSourceOrigin;
    }

    private bool HasPendingForegroundReadback
        => _foregroundReadbackSlots.Any(static slot => slot is { Pending: true });

    private bool IsForegroundSamplingDue(int probeCount, long probeVersion)
        => probeCount > 0 &&
           (probeVersion != _lastForegroundProbeVersion ||
            Stopwatch.GetTimestamp() >= _nextForegroundSamplingTick);

    internal void RefreshCaptureSource()
    {
        if (_disposed || _suspended || _capture is null)
            return;

        var timestamp = Stopwatch.GetTimestamp();
        if (timestamp < _nextCaptureSourceCheck)
            return;

        _nextCaptureSourceCheck = timestamp + Stopwatch.Frequency / 4;
        _capture.EnsureMonitor(_hwnd);
    }

    private void OnCaptureFrameAvailable()
    {
        if (!_disposed && !_suspended)
            RenderInvalidated?.Invoke();
    }

    private void CreateDevice(byte[]? deviceLuid)
    {
        if (deviceLuid is null || deviceLuid.Length < sizeof(long))
            throw new NotSupportedException("Avalonia did not expose the compositor adapter LUID.");

        var requestedLuid = BitConverter.ToInt64(deviceLuid, 0);
        using var dxgi = new DXGI(DXGI.CreateDefaultContext(["dxgi.dll"]));
        using var d3d11 = new D3D11(D3D11.CreateDefaultContext(["d3d11.dll"]));
        using var factory = dxgi.CreateDXGIFactory1<IDXGIFactory1>();
        ComPtr<IDXGIAdapter1> selected = default;
        AdapterDesc1 selectedDescription = default;

        for (uint index = 0; ; index++)
        {
            ComPtr<IDXGIAdapter1> candidate = default;
            var result = factory.EnumAdapters1(index, candidate.GetAddressOf());
            if (result == DxgiErrorNotFound)
                break;
            ThrowHResult(result);

            AdapterDesc1 description;
            ThrowHResult(candidate.GetDesc1(&description));
            var candidateLuid = ((long)description.AdapterLuid.High << 32) | description.AdapterLuid.Low;
            if (candidateLuid == requestedLuid)
            {
                selected = candidate;
                selectedDescription = description;
                break;
            }
            candidate.Dispose();
        }

        if (selected.Handle is null)
            throw new NotSupportedException("No D3D11 adapter matches Avalonia's compositor adapter LUID.");

        using (selected)
        {
            var featureLevels = stackalloc D3DFeatureLevel[]
            {
                D3DFeatureLevel.Level121,
                D3DFeatureLevel.Level120,
                D3DFeatureLevel.Level111,
                D3DFeatureLevel.Level110
            };

            var flags = (uint)CreateDeviceFlag.BgraSupport;
#if D3D11_DEBUG_LAYER
            flags |= (uint)CreateDeviceFlag.Debug;
#endif
            D3DFeatureLevel actualFeatureLevel;
            ThrowHResult(d3d11.CreateDevice(
                (IDXGIAdapter*)selected.Handle,
                D3DDriverType.Unknown,
                0,
                flags,
                featureLevels,
                4,
                D3D11.SdkVersion,
                _device.GetAddressOf(),
                &actualFeatureLevel,
                _context.GetAddressOf()));

            if (actualFeatureLevel < D3DFeatureLevel.Level110)
                throw new NotSupportedException("D3D11 feature level 11_0 is required.");

            var description = PtrToString(
                (nint)selectedDescription.Description,
                NativeStringEncoding.LPWStr) ?? "Unknown adapter";
            _diagnostics.Adapter = $"{description} / D3D {actualFeatureLevel}";
        }
    }

    private void CreatePipelineResources()
    {
        _gaussianBlurShader = CreateComputeShader(LiquidGlassRenderPass.GaussianBlurShader);
        _compositeShader = CreateComputeShader(LiquidGlassRenderPass.CompositeShader);
        _foregroundLuminanceShader = CreateComputeShader(LiquidGlassRenderPass.ForegroundLuminanceShader);

        var samplerDescription = new SamplerDesc
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunc.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue
        };
        ThrowHResult(_device.CreateSamplerState(
            &samplerDescription,
            _linearSampler.GetAddressOf()));

        var gaussianBlurBufferDescription = new BufferDesc
        {
            ByteWidth = (uint)sizeof(GaussianBlurFrameConstants),
            Usage = Usage.Default,
            BindFlags = (uint)BindFlag.ConstantBuffer
        };
        ThrowHResult(_device.CreateBuffer(
            &gaussianBlurBufferDescription,
            (SubresourceData*)null,
            _gaussianBlurConstants.GetAddressOf()));

        var compositeSize = (uint)sizeof(CompositeFrameConstants);
        compositeSize = (compositeSize + 15) & ~15u;
        var compositeBufferDescription = new BufferDesc
        {
            ByteWidth = compositeSize,
            Usage = Usage.Default,
            BindFlags = (uint)BindFlag.ConstantBuffer
        };
        ThrowHResult(_device.CreateBuffer(
            &compositeBufferDescription,
            (SubresourceData*)null,
            _compositeConstants.GetAddressOf()));

        var foregroundConstantsSize = (uint)sizeof(ForegroundLuminanceFrameConstants);
        foregroundConstantsSize = (foregroundConstantsSize + 15) & ~15u;
        var foregroundConstantsDescription = new BufferDesc
        {
            ByteWidth = foregroundConstantsSize,
            Usage = Usage.Default,
            BindFlags = (uint)BindFlag.ConstantBuffer
        };
        ThrowHResult(_device.CreateBuffer(
            &foregroundConstantsDescription,
            (SubresourceData*)null,
            _foregroundLuminanceConstants.GetAddressOf()));

        var foregroundOutputDescription = new BufferDesc
        {
            ByteWidth = (uint)(ForegroundProbeRegistry.MaximumProbes * sizeof(float)),
            Usage = Usage.Default,
            BindFlags = (uint)BindFlag.UnorderedAccess,
            CPUAccessFlags = 0,
            MiscFlags = (uint)ResourceMiscFlag.BufferStructured,
            StructureByteStride = sizeof(float)
        };
        ThrowHResult(_device.CreateBuffer(
            &foregroundOutputDescription,
            (SubresourceData*)null,
            _foregroundLuminanceOutput.GetAddressOf()));

        var foregroundOutputViewDescription = new UnorderedAccessViewDesc
        {
            Format = Format.FormatUnknown,
            ViewDimension = UavDimension.Buffer,
            Buffer = new BufferUav
            {
                FirstElement = 0,
                NumElements = ForegroundProbeRegistry.MaximumProbes,
                Flags = 0
            }
        };
        ThrowHResult(_device.CreateUnorderedAccessView(
            (ID3D11Resource*)_foregroundLuminanceOutput.Handle,
            &foregroundOutputViewDescription,
            _foregroundLuminanceOutputUav.GetAddressOf()));

        for (var index = 0; index < _foregroundReadbackSlots.Length; index++)
            _foregroundReadbackSlots[index] = new ForegroundReadbackSlot(_device);
    }

    private ComPtr<ID3D11ComputeShader> CreateComputeShader(MaterialShaderDescriptor descriptor)
    {
        var bytecode = ShaderBytecodeCache.Load(descriptor);
        ComPtr<ID3D11ComputeShader> shader = default;
        fixed (byte* bytecodePointer = bytecode)
        {
            ThrowHResult(_device.CreateComputeShader(
                bytecodePointer,
                (nuint)bytecode.Length,
                (ID3D11ClassLinkage*)null,
                shader.GetAddressOf()));
        }
        return shader;
    }

    private bool CopyLatestCaptureFrame(
        PixelSize outputSize,
        PixelPoint surfaceOffset,
        out long frameVersion,
        out CaptureFrameLease? pendingFrame)
    {
        pendingFrame = null;
        var frame = _capture!.TryGetLatestFrame();
        if (frame is null)
        {
            frameVersion = 0;
            return false;
        }

        try
        {
            frameVersion = frame.Version;

        var monitorRect = WindowsNative.GetMonitorRect(frame.Monitor);
        var clientOrigin = WindowsNative.GetClientScreenOrigin(_hwnd);
        var desiredLeft = clientOrigin.X - monitorRect.Left + surfaceOffset.X;
        var desiredTop = clientOrigin.Y - monitorRect.Top + surfaceOffset.Y;
        var desiredRight = desiredLeft + outputSize.Width;
        var desiredBottom = desiredTop + outputSize.Height;
        var sourceOrigin = new PixelPoint(desiredLeft, desiredTop);
        var requiresFullCopy = _captureTexture.Handle is null ||
            outputSize != _captureSize ||
            !_hasCaptureSourceOrigin ||
            sourceOrigin != _captureSourceOrigin;

        if (!requiresFullCopy && frame.Version <= _lastObservedCaptureVersion)
        {
            frame.MarkConsumed();
            frame.Dispose();
            return false;
        }

        _lastObservedCaptureVersion = Math.Max(_lastObservedCaptureVersion, frame.Version);

        using var source = frame.GetTexture();
        Texture2DDesc sourceDescription;
        source.GetDesc(&sourceDescription);
        if (!requiresFullCopy && !frame.IntersectsCrop(
                desiredLeft,
                desiredTop,
                desiredRight,
                desiredBottom))
        {
            frame.MarkConsumed();
            frame.Dispose();
            return false;
        }

        if (requiresFullCopy)
            RecreateCaptureResources(sourceDescription, outputSize);

        _captureSourceOrigin = sourceOrigin;
        _hasCaptureSourceOrigin = true;

        var sourceLeft = Math.Clamp(desiredLeft, 0, (int)sourceDescription.Width);
        var sourceTop = Math.Clamp(desiredTop, 0, (int)sourceDescription.Height);
        var sourceRight = Math.Clamp(desiredRight, 0, (int)sourceDescription.Width);
        var sourceBottom = Math.Clamp(desiredBottom, 0, (int)sourceDescription.Height);
        if (sourceRight <= sourceLeft || sourceBottom <= sourceTop)
        {
            ClearCaptureTexture();
            frame.MarkConsumed();
            frame.Dispose();
            return true;
        }

        var coversEntireDestination =
            desiredLeft >= 0 &&
            desiredTop >= 0 &&
            desiredRight <= (int)sourceDescription.Width &&
            desiredBottom <= (int)sourceDescription.Height &&
            sourceRight - sourceLeft == outputSize.Width &&
            sourceBottom - sourceTop == outputSize.Height;
        if (!coversEntireDestination)
            ClearCaptureTexture();
        else
            _captureClearSkipped++;

        var sourceBox = new Box
        {
            Left = (uint)sourceLeft,
            Top = (uint)sourceTop,
            Front = 0,
            Right = (uint)sourceRight,
            Bottom = (uint)sourceBottom,
            Back = 1
        };
        var destinationX = (uint)Math.Max(0, -desiredLeft);
        var destinationY = (uint)Math.Max(0, -desiredTop);
        _context.CopySubresourceRegion(
            (ID3D11Resource*)_captureTexture.Handle,
            0,
            destinationX,
            destinationY,
            0,
            (ID3D11Resource*)source.Handle,
            0,
            &sourceBox);
            pendingFrame = frame;
            return true;
        }
        catch
        {
            frame.MarkConsumed();
            frame.Dispose();
            throw;
        }
    }

    private void ClearCaptureTexture()
    {
        var clear = stackalloc float[4];
        _context.ClearRenderTargetView(_captureRenderTarget.Handle, clear);
        _captureClears++;
    }

    private void RecreateCaptureResources(Texture2DDesc sourceDescription, PixelSize size)
    {
        _captureView.Dispose();
        _captureRenderTarget.Dispose();
        _captureTexture.Dispose();
        _captureSize = size;

        sourceDescription.Width = (uint)size.Width;
        sourceDescription.Height = (uint)size.Height;
        sourceDescription.MipLevels = 1;
        sourceDescription.ArraySize = 1;
        sourceDescription.SampleDesc = new SampleDesc(1, 0);
        sourceDescription.Usage = Usage.Default;
        sourceDescription.BindFlags = (uint)(BindFlag.ShaderResource | BindFlag.RenderTarget);
        sourceDescription.CPUAccessFlags = 0;
        sourceDescription.MiscFlags = 0;
        ThrowHResult(_device.CreateTexture2D(
            &sourceDescription,
            (SubresourceData*)null,
            _captureTexture.GetAddressOf()));
        ThrowHResult(_device.CreateShaderResourceView(
            (ID3D11Resource*)_captureTexture.Handle,
            (ShaderResourceViewDesc*)null,
            _captureView.GetAddressOf()));
        ThrowHResult(_device.CreateRenderTargetView(
            (ID3D11Resource*)_captureTexture.Handle,
            (RenderTargetViewDesc*)null,
            _captureRenderTarget.GetAddressOf()));
        DisposeBlurResources();
        _diagnostics.CaptureState =
            $"live window {size.Width}x{size.Height}, GPU crop, Desktop Duplication";
    }

    private void EnsureBlurResources(float downsampleScale)
    {
        var clampedScale = Math.Clamp(downsampleScale, 0.1f, 1f);
        var size = new PixelSize(
            Math.Max(1, (int)MathF.Ceiling(_captureSize.Width * clampedScale)),
            Math.Max(1, (int)MathF.Ceiling(_captureSize.Height * clampedScale)));
        if (_blurSize == size && _blurTexture.Handle is not null)
            return;

        DisposeBlurResources();
        _blurSize = size;
        if (_captureSize.Width <= 0 || _captureSize.Height <= 0)
            return;

        var description = new Texture2DDesc
        {
            Width = (uint)_blurSize.Width,
            Height = (uint)_blurSize.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.FormatR16G16B16A16Float,
            SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default,
            BindFlags = (uint)(BindFlag.ShaderResource | BindFlag.UnorderedAccess)
        };
        ThrowHResult(_device.CreateTexture2D(
            &description,
            (SubresourceData*)null,
            _blurTexture.GetAddressOf()));
        ThrowHResult(_device.CreateShaderResourceView(
            (ID3D11Resource*)_blurTexture.Handle,
            (ShaderResourceViewDesc*)null,
            _blurView.GetAddressOf()));
        ThrowHResult(_device.CreateUnorderedAccessView(
            (ID3D11Resource*)_blurTexture.Handle,
            (UnorderedAccessViewDesc*)null,
            _blurUav.GetAddressOf()));

        ThrowHResult(_device.CreateTexture2D(
            &description,
            (SubresourceData*)null,
            _blurIntermediateTexture.GetAddressOf()));
        ThrowHResult(_device.CreateShaderResourceView(
            (ID3D11Resource*)_blurIntermediateTexture.Handle,
            (ShaderResourceViewDesc*)null,
            _blurIntermediateView.GetAddressOf()));
        ThrowHResult(_device.CreateUnorderedAccessView(
            (ID3D11Resource*)_blurIntermediateTexture.Handle,
            (UnorderedAccessViewDesc*)null,
            _blurIntermediateUav.GetAddressOf()));
    }

    private void DisposeBlurResources()
    {
        _blurIntermediateUav.Dispose();
        _blurIntermediateView.Dispose();
        _blurIntermediateTexture.Dispose();
        _blurUav.Dispose();
        _blurView.Dispose();
        _blurTexture.Dispose();
        _blurSize = default;
    }

    private void EnsureOutputResources(PixelSize size)
    {
        if (_outputSize == size && _outputSlots[0] is not null)
            return;

        foreach (var slot in _outputSlots)
            slot?.Dispose();
        Array.Clear(_outputSlots);
        _outputSize = size;
        for (var index = 0; index < OutputRingSize; index++)
            _outputSlots[index] = new OutputSlot(_device, _interop, _surface, size);
    }

    private OutputSlot? FindAvailableOutputSlot()
    {
        for (var index = 0; index < _outputSlots.Length; index++)
        {
            var slot = _outputSlots[index];
            if (slot is null)
                continue;

            if (slot.GetPresentationFailure() is { } failure)
            {
                MaterialLogger.Write("Avalonia GPU 材质表面呈现失败", failure);
                slot.Dispose();
                slot = new OutputSlot(_device, _interop, _surface, _outputSize);
                _outputSlots[index] = slot;
            }

            if (slot.LastPresent is null || slot.LastPresent.IsCompleted)
                return slot;
        }
        return null;
    }

    private void DispatchGaussianBlur(float maximumBlurRadius, float downsampleScale)
    {
        GaussianBlurFrameConstants constants = default;
        var scaledBlurRadius = maximumBlurRadius * Math.Clamp(downsampleScale, 0.1f, 1f);
        var radius = Math.Clamp(
            (int)MathF.Ceiling(scaledBlurRadius),
            1,
            MaximumGaussianRadius);
        // 将 BlurRadius 视作三倍标准差边界，保证半径始终按像素空间计算。
        var sigma = MathF.Max(
            MathF.Min(scaledBlurRadius, MaximumGaussianRadius) / 3f,
            0.001f);
        var inverseTwoSigmaSquared = 1.0 / (2.0 * sigma * sigma);
        var normalization = 0.0;

        for (var offset = 0; offset <= radius; offset++)
        {
            var weight = Math.Exp(-(offset * offset) * inverseTwoSigmaSquared);
            constants.PackedWeights[offset] = (float)weight;
            normalization += offset == 0 ? weight : weight * 2.0;
        }

        for (var offset = 0; offset <= radius; offset++)
            constants.PackedWeights[offset] /= (float)normalization;

        constants.KernelParameters = new Vector4(radius, sigma, 0, 0);
        DispatchGaussianPass(
            _captureView.Handle,
            _blurIntermediateUav.Handle,
            ref constants,
            new Vector2(1, 0));
        DispatchGaussianPass(
            _blurIntermediateView.Handle,
            _blurUav.Handle,
            ref constants,
            new Vector2(0, 1));
    }

    private void DispatchGaussianPass(
        ID3D11ShaderResourceView* source,
        ID3D11UnorderedAccessView* destination,
        ref GaussianBlurFrameConstants constants,
        Vector2 direction)
    {
        constants.OutputSizeAndDirection = new Vector4(
            _blurSize.Width,
            _blurSize.Height,
            direction.X,
            direction.Y);
        fixed (GaussianBlurFrameConstants* constantsPointer = &constants)
        {
            _context.UpdateSubresource(
                (ID3D11Resource*)_gaussianBlurConstants.Handle,
                0,
                (Box*)null,
                constantsPointer,
                0,
                0);
        }

        var constantBuffer = _gaussianBlurConstants.Handle;
        _context.CSSetShader(_gaussianBlurShader.Handle, null, 0);
        _context.CSSetShaderResources(0, 1, &source);
        var sampler = _linearSampler.Handle;
        _context.CSSetSamplers(0, 1, &sampler);
        _context.CSSetUnorderedAccessViews(0, 1, &destination, (uint*)null);
        _context.CSSetConstantBuffers(0, 1, &constantBuffer);
        _context.Dispatch(
            (uint)((_blurSize.Width + 7) / 8),
            (uint)((_blurSize.Height + 7) / 8),
            1);
        UnbindComputeResources(1, 1);
    }

    private void DispatchComposite(
        OutputSlot slot,
        ReadOnlySpan<MaterialRegion> regions,
        PixelPoint surfaceOffset,
        float maximumBlurRadius)
    {
        var constants = new CompositeFrameConstants
        {
            OutputAndCaptureSize = new Vector4(
                _outputSize.Width,
                _outputSize.Height,
                _captureSize.Width,
                _captureSize.Height),
            RegionCountAndPadding = Vector4.Zero
        };

        var count = 0;
        var usesSharpTexture = false;
        var usesBlurTexture = false;
        foreach (ref readonly var region in regions)
        {
            if (region.Kind != MaterialKind.LiquidGlass || count == MaterialRegionRegistry.MaximumRegions)
                continue;

            var material = region.Material;
            var left = (float)region.Bounds.Left - surfaceOffset.X;
            var top = (float)region.Bounds.Top - surfaceOffset.Y;
            var right = (float)region.Bounds.Right - surfaceOffset.X;
            var bottom = (float)region.Bounds.Bottom - surfaceOffset.Y;
            var scale = (float)region.Scale;
            var center = new Vector2(
                (left + right) * 0.5f,
                (top + bottom) * 0.5f + (float)region.OffsetY);
            var halfSize = new Vector2(
                MathF.Max((right - left) * 0.5f * scale, 1f),
                MathF.Max((bottom - top) * 0.5f * scale, 1f));
            var radius = MathF.Min(
                (float)region.CornerRadius,
                MathF.Min(halfSize.X, halfSize.Y));
            var usesBlur = maximumBlurRadius > BlurMixEpsilon &&
                material.BlurRadius > BlurMixEpsilon;

            usesSharpTexture |= !usesBlur;
            usesBlurTexture |= usesBlur;
            constants.Regions[count++] = new GpuLiquidGlassRegion
            {
                Bounds = new Vector4(
                    center.X,
                    center.Y,
                    halfSize.X,
                    halfSize.Y),
                Geometry = new Vector4(
                    radius,
                    0,
                    0,
                    0),
                RefractionCurve = new Vector4(
                    (float)material.RefractionCurve.Power,
                    (float)material.RefractionCurve.A,
                    (float)material.RefractionCurve.B,
                    (float)material.RefractionCurve.C),
                OpticalEffects = new Vector4(
                    (float)material.RefractionCurve.D,
                    (float)material.NoiseIntensity,
                    usesBlur ? 1 : 0,
                    0),
                Glow = new Vector4(
                    (float)material.Glow.Weight,
                    (float)material.Glow.Bias,
                    (float)material.Glow.Edge0,
                    (float)material.Glow.Edge1),
                Highlight = new Vector4(
                    (float)material.Highlight.Intensity,
                    (float)material.Highlight.BorderWidth,
                    (float)material.Highlight.ReflectionFalloffWidth,
                    0)
            };
        }

        constants.RegionCountAndPadding.X = count;

        _context.UpdateSubresource(
            (ID3D11Resource*)_compositeConstants.Handle,
            0,
            (Box*)null,
            &constants,
            0,
            0);

        var sources = stackalloc ID3D11ShaderResourceView*[2];
        sources[0] = usesSharpTexture ? _captureView.Handle : null;
        sources[1] = usesBlurTexture ? _blurView.Handle : null;
        var destination = slot.UnorderedAccessView.Handle;
        var constantBuffer = _compositeConstants.Handle;
        var sampler = _linearSampler.Handle;
        _context.CSSetShader(_compositeShader.Handle, null, 0);
        _context.CSSetShaderResources(0, 2, sources);
        _context.CSSetSamplers(0, 1, &sampler);
        _context.CSSetUnorderedAccessViews(0, 1, &destination, (uint*)null);
        _context.CSSetConstantBuffers(0, 1, &constantBuffer);
        _context.Dispatch(
            (uint)((_outputSize.Width + 7) / 8),
            (uint)((_outputSize.Height + 7) / 8),
            1);
        UnbindComputeResources(2, 1);
    }

    private void DispatchForegroundLuminance(
        ReadOnlySpan<ForegroundProbe> probes,
        PixelPoint surfaceOffset,
        long probeVersion)
    {
        if (probes.IsEmpty || _captureView.Handle is null ||
            _captureSize.Width <= 0 || _captureSize.Height <= 0)
            return;

        var slot = FindAvailableForegroundReadbackSlot();
        if (slot is null)
        {
            // 极端 GPU 忙碌时跳过本轮，下一秒再试，绝不为前景颜色阻塞主合成。
            _nextForegroundSamplingTick = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            return;
        }

        ForegroundLuminanceFrameConstants constants = default;
        var count = 0;
        foreach (ref readonly var probe in probes)
        {
            if (count == ForegroundProbeRegistry.MaximumProbes)
                break;

            var left = Math.Clamp((float)probe.Bounds.Left - surfaceOffset.X, 0, _captureSize.Width);
            var top = Math.Clamp((float)probe.Bounds.Top - surfaceOffset.Y, 0, _captureSize.Height);
            var right = Math.Clamp((float)probe.Bounds.Right - surfaceOffset.X, 0, _captureSize.Width);
            var bottom = Math.Clamp((float)probe.Bounds.Bottom - surfaceOffset.Y, 0, _captureSize.Height);
            if (right - left < 1 || bottom - top < 1)
                continue;

            constants.Probes[count] = new Vector4(left, top, right, bottom);
            slot.ProbeIds[count] = probe.Id;
            count++;
        }

        if (count == 0)
        {
            _lastForegroundProbeVersion = probeVersion;
            _nextForegroundSamplingTick = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            return;
        }

        constants.CaptureSizeAndProbeCount = new Vector4(
            _captureSize.Width,
            _captureSize.Height,
            count,
            0);
        _context.UpdateSubresource(
            (ID3D11Resource*)_foregroundLuminanceConstants.Handle,
            0,
            (Box*)null,
            &constants,
            0,
            0);

        var source = _captureView.Handle;
        var destination = _foregroundLuminanceOutputUav.Handle;
        var sampler = _linearSampler.Handle;
        var constantBuffer = _foregroundLuminanceConstants.Handle;
        _context.CSSetShader(_foregroundLuminanceShader.Handle, null, 0);
        _context.CSSetShaderResources(0, 1, &source);
        _context.CSSetSamplers(0, 1, &sampler);
        _context.CSSetUnorderedAccessViews(0, 1, &destination, (uint*)null);
        _context.CSSetConstantBuffers(0, 1, &constantBuffer);
        _context.Dispatch((uint)count, 1, 1);
        UnbindComputeResources(1, 1);

        _context.CopyResource(
            (ID3D11Resource*)slot.Staging.Handle,
            (ID3D11Resource*)_foregroundLuminanceOutput.Handle);
        slot.Count = count;
        slot.Pending = true;
        _lastForegroundProbeVersion = probeVersion;
        _nextForegroundSamplingTick = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
    }

    private ForegroundReadbackSlot? FindAvailableForegroundReadbackSlot()
    {
        foreach (var slot in _foregroundReadbackSlots)
        {
            if (slot is { Pending: false })
                return slot;
        }

        return null;
    }

    private void TryReadForegroundLuminance()
    {
        Span<ForegroundLuminanceSample> samples = stackalloc ForegroundLuminanceSample[ForegroundProbeRegistry.MaximumProbes];
        foreach (var slot in _foregroundReadbackSlots)
        {
            if (slot is not { Pending: true })
                continue;

            MappedSubresource mapped = default;
            var result = _context.Map(
                (ID3D11Resource*)slot.Staging.Handle,
                0,
                Map.Read,
                D3D11MapFlagDoNotWait,
                &mapped);
            if (result == DxgiErrorWasStillDrawing)
                continue;
            ThrowHResult(result);

            var count = slot.Count;
            try
            {
                var luminance = (float*)mapped.PData;
                for (var index = 0; index < count; index++)
                {
                    var value = luminance[index];
                    samples[index] = new ForegroundLuminanceSample(
                        slot.ProbeIds[index],
                        float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.5f);
                }
            }
            finally
            {
                _context.Unmap((ID3D11Resource*)slot.Staging.Handle, 0);
            }

            slot.Pending = false;
            slot.Count = 0;
            _foregroundProbes.Publish(samples[..count]);
        }
    }

    private static float GetMaximumBlurRadius(ReadOnlySpan<MaterialRegion> regions)
    {
        var maximum = 0f;
        foreach (ref readonly var region in regions)
        {
            if (region.Kind == MaterialKind.LiquidGlass)
                maximum = Math.Max(maximum, (float)region.Material.BlurRadius);
        }
        return maximum;
    }

    private static float GetBlurDownsampleScale(ReadOnlySpan<MaterialRegion> regions)
    {
        var maximum = 0.1f;
        foreach (ref readonly var region in regions)
        {
            if (region.Kind != MaterialKind.LiquidGlass || region.Material.BlurRadius <= BlurMixEpsilon)
                continue;
            maximum = Math.Max(
                maximum,
                Math.Clamp((float)region.Material.BlurDownsampleScale, 0.1f, 1f));
        }
        return maximum;
    }

    private void UnbindComputeResources(uint sourceCount, uint uavCount)
    {
        var nullSources = stackalloc ID3D11ShaderResourceView*[(int)sourceCount];
        var nullUavs = stackalloc ID3D11UnorderedAccessView*[(int)uavCount];
        for (var index = 0; index < sourceCount; index++)
            nullSources[index] = null;
        for (var index = 0; index < uavCount; index++)
            nullUavs[index] = null;
        _context.CSSetShaderResources(0, sourceCount, nullSources);
        _context.CSSetUnorderedAccessViews(0, uavCount, nullUavs, (uint*)null);
    }

    private void PublishMetrics()
    {
        var elapsed = _clock.Elapsed.TotalSeconds;
        if (elapsed - _fpsTimeBase < 0.5)
            return;

        _diagnostics.FramesPerSecond =
            (_renderedFrames - _fpsFrameBase) / Math.Max(0.001, elapsed - _fpsTimeBase);
        var receivedFrames = _capture!.ReceivedFrames;
        _diagnostics.CaptureFramesPerSecond =
            (receivedFrames - _captureFrameBase) / Math.Max(0.001, elapsed - _fpsTimeBase);
        var lastFrameTimestamp = _capture.LastFrameTimestamp;
        _diagnostics.CaptureFrameAgeMilliseconds = lastFrameTimestamp == 0
            ? double.NaN
            : Stopwatch.GetElapsedTime(lastFrameTimestamp).TotalMilliseconds;
        _fpsFrameBase = _renderedFrames;
        _captureFrameBase = receivedFrames;
        _fpsTimeBase = elapsed;
        _diagnostics.DroppedFrames = _capture!.DroppedFrames + _skippedFrames;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_capture is not null)
            _capture.FrameAvailable -= OnCaptureFrameAvailable;
        _capture?.Dispose();
        _capture = null;
        foreach (var slot in _outputSlots)
            slot?.Dispose();
        _blurIntermediateUav.Dispose();
        _blurIntermediateView.Dispose();
        _blurIntermediateTexture.Dispose();
        _blurUav.Dispose();
        _blurView.Dispose();
        _blurTexture.Dispose();
        _captureView.Dispose();
        _captureRenderTarget.Dispose();
        _captureTexture.Dispose();
        foreach (var slot in _foregroundReadbackSlots)
            slot?.Dispose();
        _foregroundLuminanceOutputUav.Dispose();
        _foregroundLuminanceOutput.Dispose();
        _foregroundLuminanceConstants.Dispose();
        _compositeConstants.Dispose();
        _gaussianBlurConstants.Dispose();
        _linearSampler.Dispose();
        _foregroundLuminanceShader.Dispose();
        _compositeShader.Dispose();
        _gaussianBlurShader.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    /// <summary>
    /// 仅保存探针归约结果的 staging buffer。它不包含任何桌面图像像素。
    /// </summary>
    private sealed unsafe class ForegroundReadbackSlot : IDisposable
    {
        internal ForegroundReadbackSlot(ComPtr<ID3D11Device> device)
        {
            var description = new BufferDesc
            {
                ByteWidth = (uint)(ForegroundProbeRegistry.MaximumProbes * sizeof(float)),
                Usage = Usage.Staging,
                BindFlags = 0,
                CPUAccessFlags = (uint)CpuAccessFlag.Read,
                MiscFlags = (uint)ResourceMiscFlag.BufferStructured,
                StructureByteStride = sizeof(float)
            };
            ThrowHResult(device.CreateBuffer(
                &description,
                (SubresourceData*)null,
                Staging.GetAddressOf()));
        }

        internal ComPtr<ID3D11Buffer> Staging;

        internal int[] ProbeIds { get; } = new int[ForegroundProbeRegistry.MaximumProbes];

        internal int Count { get; set; }

        internal bool Pending { get; set; }

        public void Dispose()
        {
            Pending = false;
            Count = 0;
            Staging.Dispose();
        }
    }

    private sealed class OutputSlot : IDisposable
    {
        private readonly CompositionDrawingSurface _target;
        private readonly ICompositionImportedGpuImage _importedImage;
        private readonly nint _sharedHandle;
        private int _disposeRequested;

        public OutputSlot(
            ComPtr<ID3D11Device> device,
            ICompositionGpuInterop interop,
            CompositionDrawingSurface target,
            PixelSize size)
        {
            _target = target;
            Size = size;
            var description = new Texture2DDesc
            {
                Width = (uint)size.Width,
                Height = (uint)size.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.FormatR8G8B8A8Unorm,
                SampleDesc = new SampleDesc(1, 0),
                Usage = Usage.Default,
                BindFlags = (uint)(
                    BindFlag.RenderTarget |
                    BindFlag.ShaderResource |
                    BindFlag.UnorderedAccess),
                MiscFlags = (uint)ResourceMiscFlag.SharedKeyedmutex
            };

            ThrowHResult(device.CreateTexture2D(
                &description,
                (SubresourceData*)null,
                Texture.GetAddressOf()));
            ThrowHResult(device.CreateUnorderedAccessView(
                (ID3D11Resource*)Texture.Handle,
                (UnorderedAccessViewDesc*)null,
                UnorderedAccessView.GetAddressOf()));
            Mutex = Texture.QueryInterface<IDXGIKeyedMutex>();

            using (var resource = Texture.QueryInterface<IDXGIResource>())
            {
                void* handle = null;
                ThrowHResult(resource.GetSharedHandle(ref handle));
                _sharedHandle = (nint)handle;
            }

            _importedImage = interop.ImportImage(
                new PlatformHandle(
                    _sharedHandle,
                    KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle),
                new PlatformGraphicsExternalImageProperties
                {
                    Width = size.Width,
                    Height = size.Height,
                    Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm
                });
        }

        public PixelSize Size { get; }
        public ComPtr<ID3D11Texture2D> Texture;
        public ComPtr<ID3D11UnorderedAccessView> UnorderedAccessView;
        public ComPtr<IDXGIKeyedMutex> Mutex;
        public Task? LastPresent { get; private set; }

        public void Present()
        {
            LastPresent = _target.UpdateWithKeyedMutexAsync(_importedImage, 1, 0);
        }

        public Exception? GetPresentationFailure()
        {
            var pendingPresent = LastPresent;
            if (pendingPresent is null || !pendingPresent.IsCompleted)
                return null;
            if (pendingPresent.IsCanceled)
                return new TaskCanceledException(
                    "Avalonia canceled a liquid glass surface presentation.");
            if (pendingPresent.Exception is { } exception)
                return new InvalidOperationException(
                    "Avalonia failed to present the liquid glass GPU surface.",
                    exception.GetBaseException());
            return null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
                return;

            var pendingPresent = LastPresent;
            if (pendingPresent is not null && !pendingPresent.IsCompleted)
            {
                _ = pendingPresent.ContinueWith(
                    _ => DisposeGraphicsResources(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return;
            }

            DisposeGraphicsResources();
        }

        private void DisposeGraphicsResources()
        {
            try
            {
                var disposal = _importedImage.DisposeAsync();
                if (disposal.IsCompletedSuccessfully)
                {
                    DisposeD3DResources();
                    return;
                }

                _ = disposal.AsTask().ContinueWith(
                    static (task, state) =>
                    {
                        var slot = (OutputSlot)state!;
                        if (task.Exception is { } exception)
                            MaterialLogger.Write("Avalonia GPU 材质图像释放失败", exception.GetBaseException());
                        slot.DisposeD3DResources();
                    },
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception exception)
            {
                MaterialLogger.Write("Avalonia GPU 材质图像释放失败", exception);
                DisposeD3DResources();
            }
        }

        private void DisposeD3DResources()
        {
            Mutex.Dispose();
            UnorderedAccessView.Dispose();
            Texture.Dispose();
        }
    }
}
