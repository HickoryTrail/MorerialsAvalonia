using Avalonia;
using MorerialsAvalonia.Diagnostics;
using MorerialsAvalonia.Native;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using System.Diagnostics;

namespace MorerialsAvalonia.Capture;

internal enum DesktopCaptureState
{
    Stopped,
    Starting,
    Capturing,
    Closed,
    Failed
}

internal sealed unsafe class CaptureFrameLease : IDisposable
{
    private ComPtr<IDXGIResource> _resource;
    private readonly ManualResetEventSlim? _consumed;
    private readonly bool _signalsConsumption;
    private int _disposed;

    internal CaptureFrameLease(
        ComPtr<IDXGIResource> resource,
        OutduplFrameInfo frameInfo,
        Box2D<int>[] dirtyRects,
        OutduplMoveRect[] moveRects,
        nint monitor,
        uint sourceWidth,
        uint sourceHeight,
        long version,
        ManualResetEventSlim? consumed = null,
        bool signalsConsumption = true)
    {
        _resource = resource;
        _consumed = consumed;
        _signalsConsumption = signalsConsumption;
        FrameInfo = frameInfo;
        DirtyRects = dirtyRects;
        MoveRects = moveRects;
        Monitor = monitor;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        Version = version;
    }

    public long Version { get; }
    public OutduplFrameInfo FrameInfo { get; }
    public Box2D<int>[] DirtyRects { get; }
    public OutduplMoveRect[] MoveRects { get; }
    public nint Monitor { get; }
    public uint SourceWidth { get; }
    public uint SourceHeight { get; }

    internal bool IsConsumed => _consumed?.IsSet == true;

    internal bool HasDesktopChanges
        => FrameInfo.LastPresentTime != 0 || DirtyRects.Length != 0 || MoveRects.Length != 0;

    internal ComPtr<ID3D11Texture2D> GetTexture()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(CaptureFrameLease));
        return _resource.QueryInterface<ID3D11Texture2D>();
    }

    internal CaptureFrameLease Clone()
    {
        if (Volatile.Read(ref _disposed) != 0 || IsConsumed || _resource.Handle is null)
            throw new ObjectDisposedException(nameof(CaptureFrameLease));

        _resource.Handle->AddRef();
        return new CaptureFrameLease(
            new ComPtr<IDXGIResource>(_resource.Handle),
            FrameInfo,
            DirtyRects,
            MoveRects,
            Monitor,
            SourceWidth,
            SourceHeight,
            Version,
            _consumed,
            signalsConsumption: false);
    }

    internal void MarkConsumed() => _consumed?.Set();

    internal void DisposeConsumptionSignal()
    {
        if (_signalsConsumption)
            _consumed?.Dispose();
    }

    internal void WaitForConsumer(CancellationToken cancellation)
    {
        if (_consumed is null)
            return;

        try
        {
            _consumed.Wait(cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // StopCaptureCore cancels the wait before releasing the duplication frame.
        }
    }

    internal bool IntersectsCrop(int left, int top, int right, int bottom)
    {
        if (!HasDesktopChanges)
            return false;

        left = Math.Max(left, 0);
        top = Math.Max(top, 0);
        right = Math.Min(right, (int)SourceWidth);
        bottom = Math.Min(bottom, (int)SourceHeight);
        if (right <= left || bottom <= top)
            return false;

        if (DirtyRects.Length == 0 && MoveRects.Length == 0)
            return true;

        foreach (var dirty in DirtyRects)
        {
            if (Intersects(dirty.Min.X, dirty.Min.Y, dirty.Max.X, dirty.Max.Y, left, top, right, bottom))
                return true;
        }

        foreach (var move in MoveRects)
        {
            var destination = move.DestinationRect;
            var width = destination.Max.X - destination.Min.X;
            var height = destination.Max.Y - destination.Min.Y;
            if (Intersects(
                    destination.Min.X,
                    destination.Min.Y,
                    destination.Max.X,
                    destination.Max.Y,
                    left,
                    top,
                    right,
                    bottom) ||
                Intersects(
                    move.SourcePoint.X,
                    move.SourcePoint.Y,
                    move.SourcePoint.X + width,
                    move.SourcePoint.Y + height,
                    left,
                    top,
                    right,
                    bottom))
                return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _resource.Dispose();
            if (_signalsConsumption)
                _consumed?.Set();
        }
    }

    private static bool Intersects(
        int left,
        int top,
        int right,
        int bottom,
        int otherLeft,
        int otherTop,
        int otherRight,
        int otherBottom)
        => left < otherRight && right > otherLeft && top < otherBottom && bottom > otherTop;
}

internal sealed unsafe class DesktopCaptureService : IDisposable
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const int DxgiErrorMoreData = unchecked((int)0x887A0003);
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiErrorSessionDisconnected = unchecked((int)0x887A0028);

    private readonly object _gate = new();
    private readonly object _frameGate = new();
    private readonly MaterialRenderDiagnostics _diagnostics;
    private readonly nint _device;
    private Thread? _captureThread;
    private CancellationTokenSource? _captureCancellation;
    private CaptureFrameLease? _latestFrame;
    private nint _monitor;
    private long _receivedFrames;
    private long _lastFrameTimestamp;
    private long _droppedFrames;
    private long _nextFrameVersion;
    private bool _disposed;
    private bool _suspended;

    public DesktopCaptureService(ComPtr<ID3D11Device> device, MaterialRenderDiagnostics diagnostics)
    {
        _device = (nint)device.Handle;
        _diagnostics = diagnostics;
    }

    public DesktopCaptureState State { get; private set; } = DesktopCaptureState.Stopped;
    public event Action? FrameAvailable;

    public nint Monitor => Volatile.Read(ref _monitor);
    public long ReceivedFrames => Interlocked.Read(ref _receivedFrames);
    public long LastFrameTimestamp => Volatile.Read(ref _lastFrameTimestamp);
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);
    public bool IsSuspended => Volatile.Read(ref _suspended);
    public bool HasLatestFrame
    {
        get
        {
            lock (_frameGate)
                return _latestFrame is { IsConsumed: false };
        }
    }

    public void EnsureMonitor(nint hwnd)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsSuspended)
            return;

        var monitor = WindowsNative.MonitorFromWindow(hwnd, WindowsNative.MonitorDefaultToNearest);
        if (monitor == 0)
            throw new InvalidOperationException("MonitorFromWindow did not return a monitor handle.");

        lock (_gate)
        {
            ThrowIfDisposed();
            if (monitor == _monitor && _captureThread is { IsAlive: true })
                return;

            RestartForMonitorCore(monitor);
        }
    }

    internal bool HasPendingFrameForCrop(
        PixelSize outputSize,
        PixelPoint surfaceOffset,
        nint hwnd,
        long afterVersion)
    {
        using var frame = TryGetLatestFrame();
        if (frame is null || frame.Version <= afterVersion ||
            !frame.HasDesktopChanges || outputSize.Width <= 0 || outputSize.Height <= 0)
            return false;

        var monitorRect = WindowsNative.GetMonitorRect(frame.Monitor);
        var clientOrigin = WindowsNative.GetClientScreenOrigin(hwnd);
        var left = clientOrigin.X - monitorRect.Left + surfaceOffset.X;
        var top = clientOrigin.Y - monitorRect.Top + surfaceOffset.Y;
        var intersects = frame.IntersectsCrop(left, top, left + outputSize.Width, top + outputSize.Height);
        if (!intersects)
            frame.MarkConsumed();
        return intersects;
    }

    public CaptureFrameLease? TryGetLatestFrame()
    {
        lock (_frameGate)
        {
            if (_latestFrame is null || _latestFrame.IsConsumed)
                return null;

            try
            {
                return _latestFrame.Clone();
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }
    }

    public void Suspend()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_suspended)
                return;

            _suspended = true;
            StopCaptureCore();
            _diagnostics.CaptureState = "paused (window hidden, minimized, or fully occluded)";
        }
    }

    public void Resume(nint hwnd)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_suspended)
                return;

            _suspended = false;
        }

        try
        {
            EnsureMonitor(hwnd);
        }
        catch
        {
            lock (_gate)
                _suspended = true;
            throw;
        }
    }

    private void RestartForMonitorCore(nint monitor)
    {
        StopCaptureCore();
        _monitor = monitor;
        State = DesktopCaptureState.Starting;
        _diagnostics.CaptureState = "starting Desktop Duplication";

        var cancellation = new CancellationTokenSource();
        _captureCancellation = cancellation;
        _captureThread = new Thread(() => CaptureLoop(monitor, cancellation.Token))
        {
            IsBackground = true,
            Name = "MorerialsAvalonia Desktop Duplication"
        };
        _captureThread.Start();
    }

    private void CaptureLoop(nint monitor, CancellationToken cancellation)
    {
        try
        {
            using var dxgiDevice = new ComPtr<ID3D11Device>((ID3D11Device*)_device).QueryInterface<IDXGIDevice>();
            ComPtr<IDXGIAdapter> baseAdapter = default;
            ThrowHResult(dxgiDevice.GetAdapter(baseAdapter.GetAddressOf()));
            using (baseAdapter)
            using (var adapter = baseAdapter.QueryInterface<IDXGIAdapter1>())
            {
                while (!cancellation.IsCancellationRequested)
                {
                    using var duplication = CreateDuplication(adapter, monitor);
                    OutduplDesc description = default;
                    duplication.GetDesc(&description);
                    State = DesktopCaptureState.Capturing;
                    _diagnostics.CaptureState =
                        $"live {description.ModeDesc.Width}x{description.ModeDesc.Height}, Desktop Duplication";

                    var rebuild = false;
                    while (!cancellation.IsCancellationRequested && !rebuild)
                    {
                        OutduplFrameInfo frameInfo = default;
                        ComPtr<IDXGIResource> resource = default;
                        var result = duplication.AcquireNextFrame(
                            100,
                            &frameInfo,
                            resource.GetAddressOf());
                        if (result == DxgiErrorWaitTimeout)
                            continue;
                        if (result == DxgiErrorAccessLost || result == DxgiErrorSessionDisconnected)
                        {
                            ClearLatestFrame();
                            _diagnostics.CaptureState = "rebuilding Desktop Duplication";
                            rebuild = true;
                            continue;
                        }

                        ThrowHResult(result);
                        CaptureFrameLease? publishedFrame = null;
                        try
                        {
                            Interlocked.Increment(ref _receivedFrames);
                            Volatile.Write(ref _lastFrameTimestamp, Stopwatch.GetTimestamp());

                            var moveRects = ReadMoveRects(duplication, frameInfo.TotalMetadataBufferSize);
                            var dirtyRects = ReadDirtyRects(duplication, frameInfo.TotalMetadataBufferSize);
                            if (resource.Handle is null)
                                continue;

                            var frame = new CaptureFrameLease(
                                resource,
                                frameInfo,
                                dirtyRects,
                                moveRects,
                                monitor,
                                description.ModeDesc.Width,
                                description.ModeDesc.Height,
                                Interlocked.Increment(ref _nextFrameVersion),
                                new ManualResetEventSlim(false));
                            resource = default;

                            // Desktop Duplication also reports pointer-only updates. They do not alter the material input.
                            if (!frame.HasDesktopChanges)
                            {
                                frame.Dispose();
                                frame.DisposeConsumptionSignal();
                                continue;
                            }

                            CaptureFrameLease? replaced;
                            lock (_frameGate)
                            {
                                replaced = _latestFrame;
                                _latestFrame = frame;
                            }
                            if (replaced is not null)
                            {
                                replaced.Dispose();
                                replaced.DisposeConsumptionSignal();
                                Interlocked.Increment(ref _droppedFrames);
                            }

                            publishedFrame = frame;
                            if (!IsSuspended)
                                FrameAvailable?.Invoke();

                            // The renderer must copy the texture while the duplication frame is still acquired.
                            // ReleaseFrame is deferred until the renderer disposes its clone after CopySubresourceRegion.
                            publishedFrame.WaitForConsumer(cancellation);
                            lock (_frameGate)
                            {
                                if (ReferenceEquals(_latestFrame, publishedFrame))
                                    _latestFrame = null;
                            }
                            publishedFrame.Dispose();
                            publishedFrame.DisposeConsumptionSignal();
                            publishedFrame = null;
                        }
                        finally
                        {
                            if (publishedFrame is not null)
                            {
                                lock (_frameGate)
                                {
                                    if (ReferenceEquals(_latestFrame, publishedFrame))
                                        _latestFrame = null;
                                }
                            }
                            publishedFrame?.Dispose();
                            publishedFrame?.DisposeConsumptionSignal();
                            var releaseResult = duplication.ReleaseFrame();
                            resource.Dispose();
                            if (releaseResult == DxgiErrorAccessLost ||
                                releaseResult == DxgiErrorSessionDisconnected)
                            {
                                ClearLatestFrame();
                                _diagnostics.CaptureState = "rebuilding Desktop Duplication";
                                rebuild = true;
                            }
                            else
                            {
                                ThrowHResult(releaseResult);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
            // Shutdown races with the 100 ms AcquireNextFrame timeout; no diagnostic is needed.
        }
        catch (Exception exception)
        {
            State = DesktopCaptureState.Failed;
            _diagnostics.CaptureState = "Desktop Duplication frame error";
            _diagnostics.Fail($"Desktop Duplication frame failure: {exception.Message}");
            MaterialLogger.Write("Desktop Duplication frame thread failed", exception);
        }
        finally
        {
            if (!cancellation.IsCancellationRequested && State == DesktopCaptureState.Capturing)
                State = DesktopCaptureState.Closed;
        }
    }

    private ComPtr<IDXGIOutputDuplication> CreateDuplication(
        ComPtr<IDXGIAdapter1> adapter,
        nint monitor)
    {
        ComPtr<IDXGIOutput> output = default;
        for (uint index = 0; ; index++)
        {
            var result = adapter.EnumOutputs(index, output.GetAddressOf());
            if (result == DxgiErrorNotFound)
                break;
            ThrowHResult(result);

            OutputDesc outputDescription;
            ThrowHResult(output.GetDesc(&outputDescription));
            if (outputDescription.Monitor == monitor)
                break;

            output.Dispose();
        }

        if (output.Handle is null)
            throw new InvalidOperationException("DXGI could not find the output for the current monitor.");

        using (output)
        using (var output1 = output.QueryInterface<IDXGIOutput1>())
        {
            ComPtr<IDXGIOutputDuplication> duplication = default;
            ThrowHResult(output1.DuplicateOutput((IUnknown*)_device, duplication.GetAddressOf()));
            return duplication;
        }
    }

    private static Box2D<int>[] ReadDirtyRects(
        ComPtr<IDXGIOutputDuplication> duplication,
        uint metadataBufferSize)
    {
        if (metadataBufferSize == 0)
            return Array.Empty<Box2D<int>>();

        var bufferSize = Math.Max(metadataBufferSize, (uint)sizeof(Box2D<int>));
        while (true)
        {
            var buffer = new byte[bufferSize];
            fixed (byte* pointer = buffer)
            {
                var required = bufferSize;
                var result = duplication.GetFrameDirtyRects(
                    bufferSize,
                    (Box2D<int>*)pointer,
                    &required);
                if (result == DxgiErrorMoreData && required > bufferSize)
                {
                    bufferSize = required;
                    continue;
                }

                ThrowHResult(result);
                var count = checked((int)(required / (uint)sizeof(Box2D<int>)));
                return new ReadOnlySpan<Box2D<int>>(pointer, count).ToArray();
            }
        }
    }

    private static OutduplMoveRect[] ReadMoveRects(
        ComPtr<IDXGIOutputDuplication> duplication,
        uint metadataBufferSize)
    {
        if (metadataBufferSize == 0)
            return Array.Empty<OutduplMoveRect>();

        var bufferSize = Math.Max(metadataBufferSize, (uint)sizeof(OutduplMoveRect));
        while (true)
        {
            var buffer = new byte[bufferSize];
            fixed (byte* pointer = buffer)
            {
                var required = bufferSize;
                var result = duplication.GetFrameMoveRects(
                    bufferSize,
                    (OutduplMoveRect*)pointer,
                    &required);
                if (result == DxgiErrorMoreData && required > bufferSize)
                {
                    bufferSize = required;
                    continue;
                }

                ThrowHResult(result);
                var count = checked((int)(required / (uint)sizeof(OutduplMoveRect)));
                return new ReadOnlySpan<OutduplMoveRect>(pointer, count).ToArray();
            }
        }
    }

    private static void ThrowHResult(int result)
        => SilkMarshal.ThrowHResult(result);

    private void ClearLatestFrame()
    {
        CaptureFrameLease? latest;
        lock (_frameGate)
        {
            latest = _latestFrame;
            _latestFrame = null;
        }
        latest?.Dispose();
    }

    private void StopCaptureCore()
    {
        var cancellation = _captureCancellation;
        var thread = _captureThread;
        _captureCancellation = null;
        _captureThread = null;
        cancellation?.Cancel();
        if (thread is not null && thread != Thread.CurrentThread && thread.IsAlive)
            thread.Join();
        cancellation?.Dispose();

        ClearLatestFrame();
        _monitor = 0;
        State = DesktopCaptureState.Stopped;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            StopCaptureCore();
            State = DesktopCaptureState.Stopped;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DesktopCaptureService));
    }
}
