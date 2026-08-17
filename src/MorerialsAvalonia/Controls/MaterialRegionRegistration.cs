using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MorerialsAvalonia.Rendering;

namespace MorerialsAvalonia.Controls;

/// <summary>
/// 将材质控件的布局转换为宿主局部像素坐标。
/// 同一宿主中的所有区域使用一个注册表，因此不会跨窗口泄漏状态。
/// </summary>
internal sealed class MaterialRegionRegistration : IDisposable
{
    private static int _nextId;
    private readonly Control _owner;
    private readonly Func<MaterialRegion> _stateFactory;
    private readonly int _id = Interlocked.Increment(ref _nextId);
    private TopLevel? _topLevel;
    private MaterialHost? _host;
    private bool _attached;

    internal MaterialRegionRegistration(Control owner, Func<MaterialRegion> stateFactory)
    {
        _owner = owner;
        _stateFactory = stateFactory;
    }

    internal void Attach()
    {
        if (_attached)
            return;

        _attached = true;
        _host = _owner.GetVisualAncestors().OfType<MaterialHost>().FirstOrDefault();
        _topLevel = TopLevel.GetTopLevel(_owner);
        if (_topLevel is not null)
            _topLevel.LayoutUpdated += OnLayoutUpdated;

        Update();
        Dispatcher.UIThread.Post(Update, DispatcherPriority.Render);
    }

    internal void Update()
    {
        if (!_attached || _host is null || !_host.IsActiveForTopLevel)
            return;

        if (!_owner.IsEffectivelyVisible)
        {
            _host.RegionRegistry.Remove(_id);
            return;
        }

        var topLevel = TopLevel.GetTopLevel(_owner);
        if (topLevel is null || _owner.Bounds.Width <= 0 || _owner.Bounds.Height <= 0)
        {
            _host.RegionRegistry.Remove(_id);
            return;
        }

        var translated = _owner.TranslatePoint(default, _host);
        if (translated is not { } topLeft)
            return;

        var baseRegion = _stateFactory();
        topLeft = new Point(
            topLeft.X - _owner.Bounds.Width * (1 - baseRegion.Scale) * 0.5,
            topLeft.Y - _owner.Bounds.Height * (1 - baseRegion.Scale) * 0.5 - baseRegion.OffsetY);

        var scaling = topLevel.RenderScaling;
        var bounds = new Rect(
            topLeft.X * scaling,
            topLeft.Y * scaling,
            _owner.Bounds.Width * scaling,
            _owner.Bounds.Height * scaling);

        _host.RegionRegistry.Upsert(
            _id,
            baseRegion with
            {
                Bounds = bounds,
                CornerRadius = baseRegion.CornerRadius * scaling,
                OffsetY = baseRegion.OffsetY * scaling,
                Material = baseRegion.Material.ScaleLengths(scaling),
                ZIndex = baseRegion.ZIndex + _owner.GetValue(Visual.ZIndexProperty)
            });
    }

    public void Dispose()
    {
        if (!_attached)
            return;

        _attached = false;
        if (_topLevel is not null)
            _topLevel.LayoutUpdated -= OnLayoutUpdated;
        _host?.RegionRegistry.Remove(_id);
        _topLevel = null;
        _host = null;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => Update();
}
