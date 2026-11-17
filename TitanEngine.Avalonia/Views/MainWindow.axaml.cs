using System;
using System.IO;
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

        // Nut xem preview video (ban 1): mo cua so thanh thoi gian + trich frame ffmpeg.
        BtnVideoPreview.Click += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm) return;
            var path = vm.SourceTrim.VideoPaths.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            _ = new VideoPreviewWindow(path).ShowDialog(this);
        };

        // Thay the WPF DwmSetWindowAttribute (DWMWA_MICA/Acrylic backdrop) —
        // Avalonia-native, cross-platform (WPF P/Invoke chỉ chạy Windows).
        // Mica/AcrylicBlur chỉ áp dụng trên Windows; Blur là fallback tối đa platform.
        // SkiaSharp vẽ toàn bộ nền overlay nên transparency chỉ cần cho khung trong suốt.
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur };

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
