using System;
using Avalonia;
using Avalonia.Controls;
using TitanEngine.Avalonia.ViewModels;

namespace TitanEngine.Avalonia.Views.Controls;

public partial class FxStudioView : UserControl
{
    public FxStudioView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookVm();
        AttachedToVisualTree += (_, _) => { HookVm(); UpdateBar(); };
        gridTimelinePreview.SizeChanged += (_, _) => UpdateBar();
    }

    private FxStudioViewModel? _hooked;

    private void HookVm()
    {
        if (DataContext is FxStudioViewModel vm && !ReferenceEquals(vm, _hooked))
        {
            _hooked = vm;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(FxStudioViewModel.FxStart) or nameof(FxStudioViewModel.FxEnd))
                    UpdateBar();
            };
        }
    }

    // ── MainWindow: UpdateFxTimelineVisualization (bản WPF cũng tính pixel runtime) ──
    private void UpdateBar()
    {
        if (_hooked == null) return;
        double gridWidth = gridTimelinePreview.Bounds.Width;
        if (gridWidth <= 0) gridWidth = 200;

        double leftPos = (_hooked.FxStart / 100.0) * gridWidth;
        double barWidth = ((_hooked.FxEnd - _hooked.FxStart) / 100.0) * gridWidth;
        leftPos = Math.Max(0, Math.Min(leftPos, gridWidth));
        barWidth = Math.Max(0, Math.Min(barWidth, gridWidth - leftPos));

        bdrFxRange.Width = barWidth;
        Canvas.SetLeft(bdrFxRange, leftPos);
    }
}
