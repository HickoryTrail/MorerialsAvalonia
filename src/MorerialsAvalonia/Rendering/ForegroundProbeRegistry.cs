using Avalonia;

namespace MorerialsAvalonia.Rendering;

/// <summary>
/// 单个控件在宿主像素坐标中的亮度采样区域。
/// </summary>
internal readonly record struct ForegroundProbe(int Id, Rect Bounds);

/// <summary>
/// GPU 对单个控件归约后的相对亮度结果。
/// </summary>
internal readonly record struct ForegroundLuminanceSample(int Id, float Luminance);

/// <summary>
/// 一个 <see cref="MaterialHost"/> 私有的动态前景采样集合。
/// </summary>
/// <remarks>
/// 该集合只保存控件边界。渲染器在 GPU 上完成采样和亮度归约，
/// 每个周期最多回读 <see cref="MaximumProbes"/> 个 <see cref="float"/>。
/// </remarks>
internal sealed class ForegroundProbeRegistry
{
    internal const int MaximumProbes = 128;

    private readonly object _gate = new();
    private readonly Dictionary<int, ForegroundProbe> _probes = new(MaximumProbes);
    private long _version;

    internal long Version => Interlocked.Read(ref _version);

    internal int Count
    {
        get
        {
            lock (_gate)
                return _probes.Count;
        }
    }

    internal event Action? Changed;

    internal event Action<IReadOnlyList<ForegroundLuminanceSample>>? LuminanceAvailable;

    internal void Upsert(int id, in ForegroundProbe probe)
    {
        lock (_gate)
        {
            if (_probes.TryGetValue(id, out var current) && current.Equals(probe))
                return;

            _probes[id] = probe;
            Interlocked.Increment(ref _version);
        }

        Changed?.Invoke();
    }

    internal void Remove(int id)
    {
        lock (_gate)
        {
            if (!_probes.Remove(id))
                return;

            Interlocked.Increment(ref _version);
        }

        Changed?.Invoke();
    }

    internal int CopyTo(Span<ForegroundProbe> destination, out long version)
    {
        lock (_gate)
        {
            var count = 0;
            foreach (var probe in _probes.Values)
            {
                if (count == destination.Length || count == MaximumProbes)
                    break;
                destination[count++] = probe;
            }

            version = _version;
            return count;
        }
    }

    internal void Publish(ReadOnlySpan<ForegroundLuminanceSample> samples)
    {
        var handler = LuminanceAvailable;
        if (handler is null || samples.IsEmpty)
            return;

        // 采样在渲染回调内完成；复制这至多 128 项的小结果集，避免跨线程持有栈内存。
        handler(samples.ToArray());
    }
}
