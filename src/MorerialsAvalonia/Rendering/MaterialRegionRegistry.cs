using Avalonia;
using MorerialsAvalonia.Materials.LiquidGlass;

namespace MorerialsAvalonia.Rendering;

internal enum MaterialKind
{
    LiquidGlass = 1
}

internal readonly record struct MaterialRegion
{
    public Rect Bounds { get; init; }
    public double CornerRadius { get; init; }
    public double Scale { get; init; }
    public double OffsetY { get; init; }
    public LiquidGlassMaterial Material { get; init; }
    public MaterialKind Kind { get; init; }
    public int ZIndex { get; init; }
}

/// <summary>
/// 一个 <see cref="MaterialHost"/> 私有的材质区域集合。
/// 不使用静态共享实例，防止多个窗口互相提交渲染区域。
/// </summary>
internal sealed class MaterialRegionRegistry
{
    internal const int MaximumRegions = 16;

    private readonly object _gate = new();
    private readonly Dictionary<int, MaterialRegion> _regions = new(MaximumRegions);
    private long _version;

    internal long Version => Interlocked.Read(ref _version);
    internal event Action? Changed;

    internal void Upsert(int id, in MaterialRegion region)
    {
        lock (_gate)
        {
            if (_regions.TryGetValue(id, out var current) && current.Equals(region))
                return;

            _regions[id] = region;
            Interlocked.Increment(ref _version);
        }

        Changed?.Invoke();
    }

    internal void Remove(int id)
    {
        lock (_gate)
        {
            if (!_regions.Remove(id))
                return;

            Interlocked.Increment(ref _version);
        }

        Changed?.Invoke();
    }

    internal int CopyTo(Span<MaterialRegion> destination, out long version)
    {
        lock (_gate)
        {
            var count = 0;
            foreach (var region in _regions.Values)
            {
                if (count == destination.Length || count == MaximumRegions)
                    break;
                destination[count++] = region;
            }

            for (var index = 1; index < count; index++)
            {
                var value = destination[index];
                var cursor = index - 1;
                while (cursor >= 0 && destination[cursor].ZIndex > value.ZIndex)
                {
                    destination[cursor + 1] = destination[cursor];
                    cursor--;
                }
                destination[cursor + 1] = value;
            }

            version = _version;
            return count;
        }
    }
}
