using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TitanEngine.Avalonia.ViewModels;
using TitanEngine.Avalonia.Views;

namespace TitanEngine.Avalonia.Views.Controls;

public partial class SourceTrimView : UserControl
{
    public SourceTrimView()
    {
        InitializeComponent();
        BtnCropVideo.Click += async (_, _) =>
        {
            if (DataContext is not SourceTrimViewModel st) return;
            var path = st.VideoPaths.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            var win = new CropPreviewWindow(path,
                st.CropXPercent, st.CropYPercent, st.CropWidthPercent, st.CropHeightPercent);
            var owner = TopLevel.GetTopLevel(this) is Window w ? w : null;
            if (owner is null) return;
            await win.ShowDialog(owner);
            if (win.Applied)
            {
                st.CropXPercent = win.CropXPercent;
                st.CropYPercent = win.CropYPercent;
                st.CropWidthPercent = win.CropWidthPercent;
                st.CropHeightPercent = win.CropHeightPercent;
            }
        };
    }

    private MainWindowViewModel? Root => TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel;

    // ── Đường dự phòng khi drag-drop không nhận trên Hyprland (đã duyệt, điểm 3):
    //    dán đường dẫn vào ô rồi Enter / rời ô → nạp file. ──
    private void VideoPath_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (Root != null && DataContext is SourceTrimViewModel st && st.VideoPaths.Count == 0 && !string.IsNullOrWhiteSpace(txtVideoPath.Text))
            Root.IngestPastedPath(txtVideoPath.Text, "video");
    }

    private void VideoPath_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Root != null && !string.IsNullOrWhiteSpace(txtVideoPath.Text))
            Root.IngestPastedPath(txtVideoPath.Text, "video");
    }

    private void AudioPath_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (Root != null && DataContext is SourceTrimViewModel st && st.AudioPaths.Count == 0 && !string.IsNullOrWhiteSpace(txtAudioPath.Text))
            Root.IngestPastedPath(txtAudioPath.Text, "audio1");
    }

    private void AudioPath_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Root != null && !string.IsNullOrWhiteSpace(txtAudioPath.Text))
            Root.IngestPastedPath(txtAudioPath.Text, "audio1");
    }

    private void AudioPath2_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (Root != null && string.IsNullOrWhiteSpace(txtAudioPath2.Text) == false && DataContext is SourceTrimViewModel st && st.SecondaryAudioPath == null)
            Root.IngestPastedPath(txtAudioPath2.Text, "audio2");
    }

    private void AudioPath2_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Root != null && !string.IsNullOrWhiteSpace(txtAudioPath2.Text))
            Root.IngestPastedPath(txtAudioPath2.Text, "audio2");
    }
}
