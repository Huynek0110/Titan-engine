using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TitanEngine.Avalonia.Services;
using TitanEngine.Avalonia.ViewModels;

namespace TitanEngine.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var vm = new MainWindowViewModel();
        DataContext = vm;
        vm.RootWindow = this;
        UserPrompt.Owner = this;

        // Điểm 3 đã duyệt: drag-drop có thể không nhận qua XWayland/Hyprland →
        // đường dự phòng là nút Browse + ô dán đường dẫn (SourceTrimView).
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (!e.Data.Contains(DataFormats.FileNames)) return;
        var files = e.Data.GetFiles();
        if (files != null)
        {
            var paths = files.Select(f => f.Path.LocalPath).ToList();
            if (paths.Count > 0)
            {
                e.Handled = true;
                vm.IngestDroppedFiles(paths);
            }
        }
    }
}
