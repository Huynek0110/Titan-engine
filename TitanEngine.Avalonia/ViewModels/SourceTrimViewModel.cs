using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TitanEngine.Core;

namespace TitanEngine.Avalonia.ViewModels;

/// <summary>1 dòng option cho ComboBox: nhãn hiển thị (verbatim WPF) + tag nội bộ (id render).
/// Enabled=false cho option cần bộ vẽ WPF (quy tắc Giai đoạn 5: hiện control nhưng DISABLE +
/// tooltip "chưa hỗ trợ trên bản này").</summary>
public sealed record ComboOption(string Label, string? Tag = null, bool Enabled = true)
{
    public override string ToString() => Label;
}

/// <summary>
/// Khu SOURCE & TRIM — mirror BtnBrowseVideo_Click / BtnBrowseAudio* / MergeOptionChanged /
/// UpdateRandomButtonVisibility / UpdateAudioEditSummary của MainWindow. Trạng thái video/audio
/// để JobBuilder đọc khi dựng RenderJob.
/// </summary>
public partial class SourceTrimViewModel : ObservableObject
{
    public List<string> VideoPaths { get; private set; } = new();
    public List<string> AudioPaths { get; private set; } = new();
    public List<AudioEditSegment> AudioEditSegments { get; private set; } = new();

    [ObservableProperty] private string _videoPathDisplay = "";
    [ObservableProperty] private string _audioPathDisplay = "Select Audio 1...";
    [ObservableProperty] private string _audioPath2Display = "Select Audio 2...";
    [ObservableProperty] private string? _secondaryAudioPath;
    [ObservableProperty] private bool _mergeVideos;
    [ObservableProperty] private bool _mergeAudios;
    [ObservableProperty] private bool _enableCrossTransitions;
    [ObservableProperty] private ComboOption _crossTransitionType = new("Dissolve");
    [ObservableProperty] private string _crossTransitionDuration = "0.6";
    [ObservableProperty] private string _trimStart = "00:00:00";
    [ObservableProperty] private string _trimDuration = "";
    [ObservableProperty] private string _splitDuration = "";
    [ObservableProperty] private string _imageTimelineDuration = "";
    [ObservableProperty] private string _audioEditSummary = "No audio cuts applied";
    [ObservableProperty] private double _externalAudioSpeed = 1.0;
    [ObservableProperty] private bool _randomVisible;
    [ObservableProperty] private string _randomButtonLabel = "🎲 RANDOM VIDEO × AUDIO";

    public ObservableCollection<ComboOption> CrossTransitionOptions { get; } = new(
        new[]
        {
            "Dissolve", "Fade (Black)", "Fade (White)", "Fade (Blur)", "Fade (Opacity)",
            "Whip Left", "Whip Right", "Whip Up", "Whip Down", "Zoom", "Circle Crop",
            "Radial Clock", "Pixelize", "Light Leak Burn", "HBlur"
        }.Select(l => new ComboOption(l)));

    public string ExternalAudioSpeedDisplay => $"{ExternalAudioSpeed:0.00}x";

    partial void OnExternalAudioSpeedChanged(double value) => OnPropertyChanged(nameof(ExternalAudioSpeedDisplay));

    /// <summary>Callback do MainWindowViewModel gắn (cần StorageProvider của window).</summary>
    public Action? BrowseVideoRequested { get; set; }
    public Action? BrowseAudioRequested { get; set; }
    public Action? BrowseAudio2Requested { get; set; }
    public Action? EditAudioRequested { get; set; }
    public Action? RandomCombosRequested { get; set; }
    public Action? AddToQueueRequested { get; set; }

    public event Action? SourcesChanged;

    [RelayCommand] private void BrowseVideo() => BrowseVideoRequested?.Invoke();
    [RelayCommand] private void BrowseAudio() => BrowseAudioRequested?.Invoke();
    [RelayCommand] private void BrowseAudio2() => BrowseAudio2Requested?.Invoke();
    [RelayCommand] private void EditAudio() => EditAudioRequested?.Invoke();
    [RelayCommand] private void RandomCombos() => RandomCombosRequested?.Invoke();
    [RelayCommand] private void AddToQueue() => AddToQueueRequested?.Invoke();

    partial void OnMergeVideosChanged(bool value) => UpdateRandomButtonVisibility();
    partial void OnMergeAudiosChanged(bool value) => UpdateRandomButtonVisibility();

    // ── MainWindow: BtnBrowseVideo_Click (phần UI; probe soundbar do root VM gọi) ──
    public void SetVideoPaths(List<string> paths)
    {
        VideoPaths = paths;
        VideoPathDisplay = paths.Count > 1 ? $"<{paths.Count} files>" : System.IO.Path.GetFileName(paths[0]);
        UpdateRandomButtonVisibility();
        SourcesChanged?.Invoke();
    }

    // ── MainWindow: BtnBrowseAudio_Click ──
    public void SetAudioPaths(List<string> paths)
    {
        AudioPaths = paths;
        AudioEditSegments = new List<AudioEditSegment>();
        AudioPathDisplay = paths.Count > 1 ? $"<{paths.Count} files>" : System.IO.Path.GetFileName(paths[0]);
        if (paths.Count > 1 && !MergeAudios)
        {
            MergeAudios = true;
        }
        UpdateAudioEditSummary();
        UpdateRandomButtonVisibility();
    }

    // ── MainWindow: BtnBrowseAudio2_Click ──
    public void SetSecondaryAudioPath(string path)
    {
        SecondaryAudioPath = path;
        AudioPath2Display = System.IO.Path.GetFileName(path);
    }

    /// <summary>Dán đường dẫn vào ô (đường dự phòng khi drag-drop không nhận trên Hyprland).</summary>
    public void IngestPastedVideoPath(string text)
    {
        string trimmed = text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed) || !System.IO.File.Exists(trimmed)) return;
        SetVideoPaths(new List<string> { trimmed });
    }

    public void IngestPastedAudioPath(string text, bool secondary)
    {
        string trimmed = text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed) || !System.IO.File.Exists(trimmed)) return;
        if (secondary) SetSecondaryAudioPath(trimmed);
        else SetAudioPaths(new List<string> { trimmed });
    }

    // ── MainWindow: UpdateRandomButtonVisibility ──
    public void UpdateRandomButtonVisibility()
    {
        int videoCount = VideoPaths.Count;
        int audioCount = AudioPaths.Count;
        bool show = videoCount > 1 && audioCount > 1 && !MergeVideos && !MergeAudios;
        RandomVisible = show;
        if (show)
        {
            long total = (long)videoCount * (long)audioCount;
            RandomButtonLabel = $"🎲 RANDOM {videoCount} VIDEO × {audioCount} AUDIO = {total} QUEUE";
        }
    }

    // ── MainWindow: UpdateAudioEditSummary ──
    public void UpdateAudioEditSummary()
    {
        if (AudioEditSegments.Count == 0)
        {
            AudioEditSummary = "No audio cuts applied";
            return;
        }

        string preview = string.Join(" | ", AudioEditSegments
            .OrderBy(s => s.StartSeconds)
            .Take(2)
            .Select(s => s.DisplayLabel));

        if (AudioEditSegments.Count > 2)
            preview += $" | +{AudioEditSegments.Count - 2} more";

        AudioEditSummary = $"Audio cuts: {AudioEditSegments.Count} segment(s) - {preview}";
    }

    public void SetAudioEditSegments(List<AudioEditSegment> segments)
    {
        AudioEditSegments = segments;
        UpdateAudioEditSummary();
    }

    // ── MainWindow: phần cuối BtnAddJob_Click — xoá nguồn sau khi queue ──
    public void ClearSourceInputsAfterQueue()
    {
        VideoPaths = new List<string>();
        AudioPaths = new List<string>();
        SecondaryAudioPath = null;
        AudioEditSegments = new List<AudioEditSegment>();
        VideoPathDisplay = "";
        AudioPathDisplay = "Select Audio 1...";
        AudioPath2Display = "Select Audio 2...";
        ImageTimelineDuration = "";
        UpdateAudioEditSummary();
        UpdateRandomButtonVisibility();
    }
}
