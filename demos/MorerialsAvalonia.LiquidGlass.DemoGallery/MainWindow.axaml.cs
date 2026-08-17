using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MorerialsAvalonia;
using System.Diagnostics;

namespace MorerialsAvalonia.LiquidGlass.DemoGallery;

public partial class MainWindow : Window
{
    private int _pressCount;

    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        // 仅在 Demo 调试时输出关键 GPU 管线状态，方便定位驱动与合成器问题。
        Materials.Diagnostics.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName is nameof(MaterialRenderDiagnostics.ShaderState) or
                nameof(MaterialRenderDiagnostics.Error) or
                nameof(MaterialRenderDiagnostics.InteropState))
                Trace.TraceInformation("材质诊断: {0}", Materials.Diagnostics.StatusLine);
        };
#endif
    }

    private void OnDragAreaPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ActionButton.IsPointerOver && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnActionClick(object? sender, RoutedEventArgs e)
    {
        _pressCount++;
        ActionButton.Content = (_pressCount % 3) switch
        {
            1 => "Again",
            2 => "One more",
            _ => "Press me"
        };
    }
}
