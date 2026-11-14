using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TitanEngine.Avalonia.Views.Controls;

public partial class SoundbarView : UserControl
{
    public SoundbarView()
    {
        InitializeComponent();
        var widthBox = this.FindControl<TextBox>("txtSoundBarWidthPercent");
        var heightBox = this.FindControl<TextBox>("txtSoundBarHeightPercent");
        if (widthBox != null) widthBox.LostFocus += (_, _) => OnSizeBoxLostFocus();
        if (heightBox != null) heightBox.LostFocus += (_, _) => OnSizeBoxLostFocus();
    }

    // ── MainWindow: TxtSoundBarSizeBox_LostKeyboardFocus → re-arm settle-commit 700ms ──
    private void OnSizeBoxLostFocus()
    {
        if (DataContext is ViewModels.SoundbarViewModel vm)
            vm.OnSizeBoxLostFocus();
    }
}
