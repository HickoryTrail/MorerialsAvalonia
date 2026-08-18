using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;

namespace MorerialsAvalonia;

/// <summary>
/// 表示材质着色器的准备状态。
/// </summary>
public enum MaterialShaderState
{
    /// <summary>尚未开始准备着色器。</summary>
    NotPrepared,

    /// <summary>正在当前用户的设备上编译或恢复缓存。</summary>
    Compiling,

    /// <summary>着色器缓存已就绪，可创建 GPU 管线。</summary>
    Ready,

    /// <summary>编译器或缓存发生不可恢复错误。</summary>
    Failed
}

/// <summary>
/// 提供 <see cref="MaterialHost"/> 的可绑定运行时诊断信息。
/// </summary>
public sealed class MaterialRenderDiagnostics : INotifyPropertyChanged
{
    private string _captureState = "等待 Desktop Duplication";
    private string _adapter = "D3D11 适配器尚未初始化";
    private string _interopState = "等待 Avalonia GPU 互操作";
    private string? _error;
    private double _framesPerSecond;
    private double _captureFramesPerSecond;
    private double _captureFrameAgeMilliseconds = double.NaN;
    private long _droppedFrames;
    private bool _isOperational;
    private MaterialShaderState _shaderState = MaterialShaderState.NotPrepared;

    /// <summary>
    /// 在任一公开诊断属性发生变化时引发。
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>获取当前 Desktop Duplication 状态。</summary>
    public string CaptureState
    {
        get => _captureState;
        internal set
        {
            SetOnUiThread(_captureState, value, updated => _captureState = updated);
            RaiseOnUiThread(nameof(StatusLine));
        }
    }

    /// <summary>获取与 Avalonia 合成器匹配的 D3D11 适配器。</summary>
    public string Adapter
    {
        get => _adapter;
        internal set => SetOnUiThread(_adapter, value, updated => _adapter = updated);
    }

    /// <summary>获取共享纹理和 Avalonia 合成互操作状态。</summary>
    public string InteropState
    {
        get => _interopState;
        internal set => SetOnUiThread(_interopState, value, updated => _interopState = updated);
    }

    /// <summary>获取最后一个不可恢复错误；无错误时为 <see langword="null"/>。</summary>
    public string? Error
    {
        get => _error;
        internal set
        {
            SetOnUiThread(_error, value, updated => _error = updated);
            RaiseOnUiThread(nameof(HasError));
            RaiseOnUiThread(nameof(StatusLine));
        }
    }

    /// <summary>获取是否存在不可恢复错误。</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    /// <summary>获取合成输出的帧率。</summary>
    public double FramesPerSecond
    {
        get => _framesPerSecond;
        internal set
        {
            SetOnUiThread(_framesPerSecond, value, updated => _framesPerSecond = updated);
            RaiseOnUiThread(nameof(StatusLine));
        }
    }

    /// <summary>获取 Desktop Duplication 输入帧率。</summary>
    public double CaptureFramesPerSecond
    {
        get => _captureFramesPerSecond;
        internal set
        {
            SetOnUiThread(_captureFramesPerSecond, value, updated => _captureFramesPerSecond = updated);
            RaiseOnUiThread(nameof(StatusLine));
        }
    }

    /// <summary>获取最新捕获帧的年龄，单位为毫秒；未知时为 <see cref="double.NaN"/>。</summary>
    public double CaptureFrameAgeMilliseconds
    {
        get => _captureFrameAgeMilliseconds;
        internal set
        {
            SetOnUiThread(_captureFrameAgeMilliseconds, value, updated => _captureFrameAgeMilliseconds = updated);
            RaiseOnUiThread(nameof(StatusLine));
        }
    }

    /// <summary>获取因最新帧策略或输出槽繁忙而丢弃的帧数。</summary>
    public long DroppedFrames
    {
        get => _droppedFrames;
        internal set
        {
            SetOnUiThread(_droppedFrames, value, updated => _droppedFrames = updated);
            RaiseOnUiThread(nameof(StatusLine));
        }
    }

    /// <summary>获取 GPU 管线是否已成功呈现至少一帧。</summary>
    public bool IsOperational
    {
        get => _isOperational;
        internal set => SetOnUiThread(_isOperational, value, updated => _isOperational = updated);
    }

    /// <summary>获取当前着色器缓存准备状态。</summary>
    public MaterialShaderState ShaderState
    {
        get => _shaderState;
        internal set
        {
            SetOnUiThread(_shaderState, value, updated => _shaderState = updated);
            RaiseOnUiThread(nameof(StatusLine));
        }
    }

    /// <summary>获取适合直接显示的简短状态文本。</summary>
    public string StatusLine
    {
        get
        {
            var captureTiming = double.IsFinite(CaptureFrameAgeMilliseconds)
                ? $"{CaptureFramesPerSecond:0} Hz / {CaptureFrameAgeMilliseconds:0} ms"
                : "等待中";
            return $"着色器 {ShaderState} | {FramesPerSecond:0} FPS | Desktop Duplication {captureTiming} | {CaptureState} | 丢帧 {DroppedFrames}";
        }
    }

    internal void Fail(string message)
    {
        Error = message;
        IsOperational = false;
        if (ShaderState == MaterialShaderState.Compiling)
            ShaderState = MaterialShaderState.Failed;
    }

    private void SetOnUiThread<T>(
        T current,
        T value,
        Action<T> assign,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
            return;

        if (Dispatcher.UIThread.CheckAccess())
        {
            assign(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            assign(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        });
    }

    private void RaiseOnUiThread(string propertyName)
    {
        if (Dispatcher.UIThread.CheckAccess())
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        else
            Dispatcher.UIThread.Post(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));
    }
}
