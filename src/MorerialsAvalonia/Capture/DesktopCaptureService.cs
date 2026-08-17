using MorerialsAvalonia;
using MorerialsAvalonia.Diagnostics;
using MorerialsAvalonia.Native;
using System.Diagnostics;
using System.Runtime.Versioning;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Foundation.Metadata;
using Windows.Security.Authorization.AppCapabilityAccess;
using WinRtD3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;

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
    private Direct3D11CaptureFrame? _frame;

    internal CaptureFrameLease(Direct3D11CaptureFrame frame, long version)
    {
        _frame = frame;
        Version = version;
        ContentSize = frame.ContentSize;
        SystemRelativeTime = frame.SystemRelativeTime;
    }

    public long Version { get; }
    public SizeInt32 ContentSize { get; }
    public TimeSpan SystemRelativeTime { get; }

    internal ComPtr<ID3D11Texture2D> GetTexture()
    {
        var frame = _frame ?? throw new ObjectDisposedException(nameof(CaptureFrameLease));
        return WindowsNative.GetD3D11Texture(frame.Surface);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _frame, null)?.Dispose();
    }
}

internal sealed class PendingCaptureFrame : IDisposable
{
    public PendingCaptureFrame(Direct3D11CaptureFrame frame, long version)
    {
        Frame = frame;
        Version = version;
    }

    public Direct3D11CaptureFrame Frame { get; }
    public long Version { get; }

    public void Dispose() => Frame.Dispose();
}

internal static class BorderlessCaptureAccess
{
    public static async Task<bool> RequestAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) ||
            !ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess") ||
            !ApiInformation.IsPropertyPresent(
                "Windows.Graphics.Capture.GraphicsCaptureSession",
                "IsBorderRequired"))
            return false;

        try
        {
            var status = await GraphicsCaptureAccess.RequestAccessAsync(
                GraphicsCaptureAccessKind.Borderless);
            return status == AppCapabilityAccessStatus.Allowed;
        }
        catch
        {
            // 未打包运行的 Windows 应用可能会拒绝受限能力请求，此时继续使用有边框捕获。
            return false;
        }
    }
}

internal sealed unsafe class DesktopCaptureService : IDisposable
{
    private readonly object _gate = new();
    private readonly MaterialRenderDiagnostics _diagnostics;
    private readonly WinRtD3DDevice _winRtDevice;
    private readonly bool _borderlessCaptureAllowed;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;
    private PendingCaptureFrame? _latestFrame;
    private nint _monitor;
    private long _receivedFrames;
    private long _lastFrameTimestamp;
    private long _droppedFrames;
    private long _nextFrameVersion;
    private bool _disposed;
    private bool _suspended;

    public DesktopCaptureService(
        ComPtr<ID3D11Device> device,
        MaterialRenderDiagnostics diagnostics,
        bool borderlessCaptureAllowed)
    {
        _diagnostics = diagnostics;
        _borderlessCaptureAllowed = borderlessCaptureAllowed;
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        _winRtDevice = WindowsNative.CreateWinRtDevice((nint)dxgiDevice.Handle);
    }

    public DesktopCaptureState State { get; private set; } = DesktopCaptureState.Stopped;
    public event Action? FrameAvailable;

    public nint Monitor => _monitor;
    public bool IsBorderlessCapture => _borderlessCaptureAllowed;
    public long ReceivedFrames => Interlocked.Read(ref _receivedFrames);
    public long LastFrameTimestamp => Volatile.Read(ref _lastFrameTimestamp);
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);
    public bool IsSuspended => Volatile.Read(ref _suspended);
    public bool HasLatestFrame => Volatile.Read(ref _latestFrame) is not null;

    public void EnsureMonitor(nint hwnd)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsSuspended)
            return;

        var monitor = WindowsNative.MonitorFromWindow(hwnd, WindowsNative.MonitorDefaultToNearest);
        if (monitor == 0)
            throw new InvalidOperationException("MonitorFromWindow did not return a monitor handle.");
        if (monitor == _monitor && State == DesktopCaptureState.Capturing)
            return;

        RestartForMonitor(monitor);
    }

    public CaptureFrameLease? TryTakeLatestFrame()
    {
        var pending = Interlocked.Exchange(ref _latestFrame, null);
        return pending is null
            ? null
            : new CaptureFrameLease(pending.Frame, pending.Version);
    }

    public void Suspend()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_suspended)
                return;

            _suspended = true;
            Interlocked.Exchange(ref _latestFrame, null)?.Dispose();
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

    private void RestartForMonitor(nint monitor)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            State = DesktopCaptureState.Starting;
            _diagnostics.CaptureState = "starting";
            StopCaptureCore();

            try
            {
                var item = WindowsNative.CreateCaptureItemForMonitor(monitor);
                var size = item.Size;
                if (size.Width <= 0 || size.Height <= 0)
                    throw new InvalidOperationException("Windows Graphics Capture returned an empty monitor size.");

                var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _winRtDevice,
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    3,
                    size);
                var session = pool.CreateCaptureSession(item);
                session.IsCursorCaptureEnabled = false;
                if (_borderlessCaptureAllowed &&
                    OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) &&
                    ApiInformation.IsPropertyPresent(
                        "Windows.Graphics.Capture.GraphicsCaptureSession",
                        "IsBorderRequired"))
                    DisableCaptureBorder(session);

                item.Closed += OnCaptureItemClosed;
                pool.FrameArrived += OnFrameArrived;
                _item = item;
                _framePool = pool;
                _session = session;
                _monitor = monitor;
                session.StartCapture();

                State = DesktopCaptureState.Capturing;
                _diagnostics.CaptureState = _borderlessCaptureAllowed
                    ? $"live {size.Width}x{size.Height}, borderless"
                    : $"live {size.Width}x{size.Height}, system border";
            }
            catch (Exception exception)
            {
                StopCaptureCore();
                State = DesktopCaptureState.Failed;
                _diagnostics.CaptureState = "failed";
                throw new InvalidOperationException(
                    "Windows Graphics Capture could not capture the current monitor.",
                    exception);
            }
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            Direct3D11CaptureFrame? newest = null;
            for (var index = 0; index < 3 && sender.TryGetNextFrame() is { } frame; index++)
            {
                Interlocked.Increment(ref _receivedFrames);
                Volatile.Write(ref _lastFrameTimestamp, Stopwatch.GetTimestamp());
                if (newest is not null)
                {
                    newest.Dispose();
                    Interlocked.Increment(ref _droppedFrames);
                }
                newest = frame;
            }

            if (newest is null)
                return;

            if (IsSuspended)
            {
                newest.Dispose();
                return;
            }

            var pending = new PendingCaptureFrame(
                newest,
                Interlocked.Increment(ref _nextFrameVersion));
            var replaced = Interlocked.Exchange(ref _latestFrame, pending);
            if (replaced is not null)
            {
                replaced.Dispose();
                Interlocked.Increment(ref _droppedFrames);
            }

            if (!IsSuspended)
                FrameAvailable?.Invoke();
        }
        catch (ObjectDisposedException)
        {
            // 切换显示器时，旧的自由线程池可能仍在回调收尾，先让回调安全退出再释放。
        }
        catch (Exception exception)
        {
            State = DesktopCaptureState.Failed;
            _diagnostics.CaptureState = "frame error";
            _diagnostics.Fail($"Windows Graphics Capture frame failure: {exception.Message}");
            MaterialLogger.Write("Windows Graphics Capture frame callback failed", exception);
        }
    }

    private void OnCaptureItemClosed(GraphicsCaptureItem sender, object args)
    {
        State = DesktopCaptureState.Closed;
        _diagnostics.CaptureState = "closed";
        if (!IsSuspended)
            _diagnostics.Fail("The Windows Graphics Capture monitor source was closed.");
    }

    private void StopCaptureCore()
    {
        var pool = _framePool;
        var item = _item;
        _framePool = null;
        _session?.Dispose();
        _session = null;

        if (pool is not null)
        {
            pool.FrameArrived -= OnFrameArrived;
            pool.Dispose();
        }

        if (item is not null)
            item.Closed -= OnCaptureItemClosed;
        _item = null;
        Interlocked.Exchange(ref _latestFrame, null)?.Dispose();
        _monitor = 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            StopCaptureCore();
            (_winRtDevice as IDisposable)?.Dispose();
            State = DesktopCaptureState.Stopped;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DesktopCaptureService));
    }

    [SupportedOSPlatform("windows10.0.20348")]
    private static void DisableCaptureBorder(GraphicsCaptureSession session)
        => session.IsBorderRequired = false;
}
