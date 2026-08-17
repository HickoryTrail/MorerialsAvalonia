using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MorerialsAvalonia.Materials.LiquidGlass;
using MorerialsAvalonia.Rendering;

namespace MorerialsAvalonia.Controls;

/// <summary>
/// 管理一个液态玻璃控件内容树中的低频前景亮度探针。
/// </summary>
/// <remarks>
/// 容器和按钮各自拥有一个作用域。嵌套的材质容器或按钮会建立自己的作用域，
/// 因此同一窗口中位于不同明暗区域的内容可以独立选择前景色。
/// </remarks>
internal sealed class MaterialForegroundScope : IDisposable
{
    private static int _nextProbeId;

    private readonly Control _owner;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<Control, ProbeTarget> _targets = [];
    private readonly Dictionary<int, ProbeTarget> _targetsById = [];
    private MaterialHost? _host;
    private bool _attached;

    internal MaterialForegroundScope(Control owner)
    {
        _owner = owner;
        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(1000),
            DispatcherPriority.Background,
            (_, _) => Refresh());
    }

    internal void Attach()
    {
        if (_attached)
            return;

        _attached = true;
        UpdateHost();
        Refresh();
        _timer.Start();
        Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Render);
    }

    public void Dispose()
    {
        if (!_attached)
            return;

        _attached = false;
        _timer.Stop();
        DetachHost();

        foreach (var target in _targets.Values)
            target.Release();
        _targets.Clear();
        _targetsById.Clear();
    }

    private void Refresh()
    {
        if (!_attached)
            return;

        UpdateHost();
        if (_host is null || !_host.IsActiveForTopLevel)
            return;

        var liveTargets = new HashSet<Control>();
        foreach (var control in EnumerateTargets(_owner))
        {
            liveTargets.Add(control);
            if (!_targets.TryGetValue(control, out var target))
            {
                target = new ProbeTarget(control, Interlocked.Increment(ref _nextProbeId));
                _targets.Add(control, target);
                _targetsById.Add(target.Id, target);
            }

            UpdateTarget(target);
        }

        foreach (var pair in _targets.Where(pair => !liveTargets.Contains(pair.Key)).ToArray())
            RemoveTarget(pair.Value);
    }

    private void UpdateHost()
    {
        var host = _owner.GetVisualAncestors().OfType<MaterialHost>().FirstOrDefault();
        if (ReferenceEquals(host, _host))
            return;

        DetachHost();
        _host = host;
        if (_host is not null)
            _host.ForegroundProbeRegistry.LuminanceAvailable += OnLuminanceAvailable;
    }

    private void DetachHost()
    {
        if (_host is null)
            return;

        _host.ForegroundProbeRegistry.LuminanceAvailable -= OnLuminanceAvailable;
        foreach (var target in _targets.Values)
            _host.ForegroundProbeRegistry.Remove(target.Id);
        _host = null;
    }

    private void UpdateTarget(ProbeTarget target)
    {
        var mode = MaterialForeground.GetMode(target.Control);
        if (mode is MaterialForegroundMode.Manual or MaterialForegroundMode.Inherit)
        {
            _host!.ForegroundProbeRegistry.Remove(target.Id);
            target.Release();
            return;
        }

        if (mode == MaterialForegroundMode.Light)
        {
            _host!.ForegroundProbeRegistry.Remove(target.Id);
            Apply(target, MaterialForegroundKind.Light);
            return;
        }

        if (mode == MaterialForegroundMode.Dark)
        {
            _host!.ForegroundProbeRegistry.Remove(target.Id);
            Apply(target, MaterialForegroundKind.Dark);
            return;
        }

        if (!TryGetPixelBounds(target.Control, out var bounds))
        {
            _host!.ForegroundProbeRegistry.Remove(target.Id);
            return;
        }

        _host!.ForegroundProbeRegistry.Upsert(target.Id, new ForegroundProbe(target.Id, bounds));
        if (target.AppliedKind != MaterialForegroundKind.Unspecified)
            Apply(target, target.AppliedKind);
    }

    private bool TryGetPixelBounds(Control control, out Rect bounds)
    {
        bounds = default;
        if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
            return false;

        var host = _host;
        var topLevel = TopLevel.GetTopLevel(control);
        var topLeft = host is null ? null : control.TranslatePoint(default, host);
        if (topLevel is null || topLeft is not { } point)
            return false;

        var scaling = topLevel.RenderScaling;
        bounds = new Rect(
            point.X * scaling,
            point.Y * scaling,
            control.Bounds.Width * scaling,
            control.Bounds.Height * scaling);
        return true;
    }

    private void OnLuminanceAvailable(IReadOnlyList<ForegroundLuminanceSample> samples)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplySamples(samples);
            return;
        }

        Dispatcher.UIThread.Post(() => ApplySamples(samples), DispatcherPriority.Background);
    }

    private void ApplySamples(IReadOnlyList<ForegroundLuminanceSample> samples)
    {
        if (!_attached)
            return;

        foreach (var sample in samples)
        {
            if (!_targetsById.TryGetValue(sample.Id, out var target) ||
                MaterialForeground.GetMode(target.Control) != MaterialForegroundMode.Automatic)
                continue;

            Apply(target, MaterialForeground.ResolveKind(
                Math.Clamp(sample.Luminance, 0, 1),
                MaterialForeground.GetLuminanceThreshold(target.Control)));
        }
    }

    private static IEnumerable<Control> EnumerateTargets(Control control)
    {
        if (IsForegroundTarget(control))
            yield return control;

        foreach (var child in control.GetLogicalChildren().OfType<Control>())
        {
            if (child is LiquidGlassContainer or LiquidGlassButton)
                continue;

            foreach (var descendant in EnumerateTargets(child))
                yield return descendant;
        }
    }

    private static bool IsForegroundTarget(Control control)
        => control is TemplatedControl or TextBlock || control.IsSet(MaterialForeground.ModeProperty);

    private static void Apply(ProbeTarget target, MaterialForegroundKind kind)
    {
        var brush = kind switch
        {
            MaterialForegroundKind.Light => MaterialForeground.GetLightForeground(target.Control),
            MaterialForegroundKind.Dark => MaterialForeground.GetDarkForeground(target.Control),
            _ => null
        };
        if (brush is null)
        {
            target.Release();
            return;
        }

        if (target.AppliedKind == kind && ReferenceEquals(target.AppliedBrush, brush))
            return;

        target.Release();
        target.ForegroundOverride = target.Control is TemplatedControl
            ? target.Control.SetValue(TemplatedControl.ForegroundProperty, brush, BindingPriority.StyleTrigger)
            : target.Control.SetValue(TextElement.ForegroundProperty, brush, BindingPriority.StyleTrigger);
        target.KindOverride = target.Control.SetValue(
            MaterialForeground.ResolvedKindProperty,
            kind,
            BindingPriority.StyleTrigger);
        target.AppliedKind = kind;
        target.AppliedBrush = brush;
    }

    private void RemoveTarget(ProbeTarget target)
    {
        _host?.ForegroundProbeRegistry.Remove(target.Id);
        target.Release();
        _targets.Remove(target.Control);
        _targetsById.Remove(target.Id);
    }

    private sealed class ProbeTarget
    {
        internal ProbeTarget(Control control, int id)
        {
            Control = control;
            Id = id;
        }

        internal Control Control { get; }

        internal int Id { get; }

        internal IDisposable? ForegroundOverride { get; set; }

        internal IDisposable? KindOverride { get; set; }

        internal MaterialForegroundKind AppliedKind { get; set; }

        internal IBrush? AppliedBrush { get; set; }

        internal void Release()
        {
            ForegroundOverride?.Dispose();
            ForegroundOverride = null;
            KindOverride?.Dispose();
            KindOverride = null;
            AppliedKind = MaterialForegroundKind.Unspecified;
            AppliedBrush = null;
        }
    }
}
