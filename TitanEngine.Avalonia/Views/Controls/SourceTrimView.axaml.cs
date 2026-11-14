using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TitanEngine.Avalonia.ViewModels;

namespace TitanEngine.Avalonia.Views.Controls;

public partial class SourceTrimView : UserControl
{
    public SourceTrimView()
    {
        InitializeComponent();
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
