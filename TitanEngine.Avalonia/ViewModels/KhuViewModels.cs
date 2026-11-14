using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TitanEngine.Avalonia.ViewModels;

/// <summary>Khu ⏱ SLOW MOTION — mirror cmbSlowMotion_SelectionChanged + txtSlowMotionCustom_TextChanged.</summary>
public partial class SlowMotionViewModel : ObservableObject
{
    public ObservableCollection<ComboOption> SpeedOptions { get; } = new(
        new[]
        {
            "0.5x (Half Speed)", "0.75x (3/4 Speed)", "1.0x (Normal)", "1.5x (1.5x Speed)",
            "2.0x (Double Speed)", "Custom (Nhập số)"
        }.Select(l => new ComboOption(l)));

    [ObservableProperty] private ComboOption _speed = new("1.0x (Normal)");
    [ObservableProperty] private string _customSpeedText = "1.0";
    [ObservableProperty] private bool _showCustom;
    [ObservableProperty] private bool _audioStretch = true;
    [ObservableProperty] private double _sourceAudioSpeed = 1.0;

    public string SourceAudioSpeedDisplay => $"{SourceAudioSpeed:0.00}x";
    partial void OnSourceAudioSpeedChanged(double value) => OnPropertyChanged(nameof(SourceAudioSpeedDisplay));

    partial void OnSpeedChanged(ComboOption value)
    {
        ShowCustom = value.Label.Contains("Custom");
    }
}

/// <summary>Khu 🚀 AI UPSCALE ENGINE — mirror cmbUpscale_SelectionChanged_1 (_upscaleMode = nội dung item).</summary>
public partial class UpscaleViewModel : ObservableObject
{
    public ObservableCollection<ComboOption> ModeOptions { get; } = new(
        new[]
        {
            "Off (Original Size)", "2x Upscale (Live-Action AI)", "4x Upscale (Ultra Quality)"
        }.Select(l => new ComboOption(l)));

    [ObservableProperty] private ComboOption _mode = new("Off (Original Size)");
    [ObservableProperty] private double _sharpness = 1.0;

    public string UpscaleMode => Mode.Label;
    public string SharpnessDisplay => $"{Sharpness:0.0}";
    partial void OnSharpnessChanged(double value) => OnPropertyChanged(nameof(SharpnessDisplay));
}

/// <summary>Khu 🎬 MOTION EDITOR.</summary>
public partial class MotionEditorViewModel : ObservableObject
{
    [ObservableProperty] private bool _enable60fps;
    [ObservableProperty] private double _rsmb;

    public string RsmbDisplay => $"{Rsmb:0.0}";
    partial void OnRsmbChanged(double value) => OnPropertyChanged(nameof(RsmbDisplay));
}

/// <summary>Khu ✏️ TEXT OVERLAY & VIBE — defaults đúng XAML (AddQuotes=true, Bottom Left, 32px, 5.0s).</summary>
public partial class TextVibeViewModel : ObservableObject
{
    public ObservableCollection<ComboOption> GlowColorOptions { get; } = new(
        new[] { "Cyan Neon", "Gold Yellow", "Pink Magenta", "Soft White", "Matrix Green", "Flame Red" }
        .Select(l => new ComboOption(l)));

    public ObservableCollection<ComboOption> StyleOptions { get; } = new(
        new[]
        {
            "Aesthetic Lyric (Georgia / Times Italic)", "Modern Sans (Segoe UI Italic)",
            "Classic Serif (Times New Roman Italic)", "Clean Sans (Arial Italic)",
            "Thư Pháp Nghệ Thuật (Calligraphy)"
        }.Select(l => new ComboOption(l)));

    public ObservableCollection<ComboOption> PositionOptions { get; } = new(
        new[]
        {
            "Center", "Top", "Bottom", "Top Left", "Top Right", "Bottom Left", "Bottom Right",
            "Custom (Tọa độ X/Y %)"
        }.Select(l => new ComboOption(l)));

    public ObservableCollection<ComboOption> AlignOptions { get; } = new(
        new[] { "Auto", "Left", "Center", "Right" }.Select(l => new ComboOption(l)));

    public ObservableCollection<ComboOption> AnimationOptions { get; } = new(
        new[]
        {
            "Drawtext cũ (Typewriter + Trượt lên + Neon Glow)", "Mặc định (Hiện tĩnh)",
            "Spotify Scroll (Chữ chạy lên)", "Pop & Bounce (Nảy mạnh mẽ)", "Smooth Drift (Trôi lơ lửng)",
            "Glow Pulse (Toả sáng như hơi thở)", "Karaoke Fade (Từ từ hiện rõ chữ)",
            "Typewriter (Gõ từng chữ)", "Blur Reveal (Mờ ảo hiện dần)"
        }.Select(l => new ComboOption(l)));

    [ObservableProperty] private bool _enable;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _addQuotes = true;
    [ObservableProperty] private bool _disableScroll;
    [ObservableProperty] private bool _enableGlow;
    [ObservableProperty] private ComboOption _glowColor = new("Cyan Neon");
    [ObservableProperty] private ComboOption _style = new("Aesthetic Lyric (Georgia / Times Italic)");
    [ObservableProperty] private ComboOption _position = new("Bottom Left");
    [ObservableProperty] private ComboOption _align = new("Auto");
    [ObservableProperty] private string _xPercent = "50";
    [ObservableProperty] private string _yPercent = "85";
    [ObservableProperty] private double _fontSize = 32;
    [ObservableProperty] private ComboOption _animation = new("Drawtext cũ (Typewriter + Trượt lên + Neon Glow)");
    [ObservableProperty] private string _effectDuration = "5.0";

    public string FontSizeDisplay => $"{FontSize:0}px";
    partial void OnFontSizeChanged(double value) => OnPropertyChanged(nameof(FontSizeDisplay));
}

/// <summary>Khu 🎙️ CAPTION SYNC (AI WHISPER) — defaults đúng XAML (Medium, Clean Sans, Bottom Center, 28px).</summary>
public partial class CaptionSyncViewModel : ObservableObject
{
    public ObservableCollection<ComboOption> LangOptions { get; } = new(
        new[] { "Tự động (Auto Detect)", "Tiếng Việt", "English", "日本語 (Japanese)", "한국어 (Korean)", "中文 (Chinese)" }
        .Select(l => new ComboOption(l)));

    public ObservableCollection<ComboOption> ModelOptions { get; } = new(
        new[] { "Small (Nhanh - 466MB)", "Medium (Chuẩn xác - 1.5GB)", "Large V3 Turbo (Rất chuẩn - 1.6GB)" }
        .Select(l => new ComboOption(l)));

    public ObservableCollection<ComboOption> AnimationOptions { get; } = new(
        new[]
        {
            "Mặc định (Hiện tĩnh)", "Spotify Scroll (Chữ chạy lên)", "Pop & Bounce (Nảy mạnh mẽ)",
            "Smooth Drift (Trôi lơ lửng)", "Glow Pulse (Toả sáng như hơi thở)",
            "Karaoke Fade (Từ từ hiện rõ chữ)", "Typewriter (Gõ từng chữ)", "Blur Reveal (Mờ ảo hiện dần)"
        }.Select(l => new ComboOption(l)));

    public ObservableCollection<ComboOption> FontOptions { get; } = new(
        new[]
        {
            "Aesthetic Lyric (Georgia / Times Italic)", "Modern Sans (Segoe UI Italic)",
            "Classic Serif (Times New Roman Italic)", "Clean Sans (Arial Italic)",
            "Thư Pháp Nghệ Thuật (Calligraphy)"
        }.Select(l => new ComboOption(l)));

    public ObservableCollection<ComboOption> PositionOptions { get; } = new(
        new[] { "Bottom Center", "Top Center", "Center", "Custom (X% Y%)" }.Select(l => new ComboOption(l)));

    [ObservableProperty] private bool _enable;
    [ObservableProperty] private ComboOption _lang = new("Tự động (Auto Detect)");
    [ObservableProperty] private ComboOption _model = new("Medium (Chuẩn xác - 1.5GB)");
    [ObservableProperty] private ComboOption _animation = new("Mặc định (Hiện tĩnh)");
    [ObservableProperty] private ComboOption _font = new("Clean Sans (Arial Italic)");
    [ObservableProperty] private ComboOption _position = new("Bottom Center");
    [ObservableProperty] private string _xPercent = "50";
    [ObservableProperty] private string _yPercent = "85";
    [ObservableProperty] private double _fontSize = 28;
}

/// <summary>Khu AUDIO MIXER — mirrors slider 0-200 + Voice FX combo + cường độ.</summary>
public partial class AudioMixerViewModel : ObservableObject
{
    public ObservableCollection<ComboOption> Audio1FxOptions { get; } = new(
        new[]
        {
            "Không (Giữ nguyên)", "Echo (Vang nhiều tầng)", "Church (Nhà thờ - aecho)",
            "Robot/Glitch (Kim loại)", "Điện thoại (Bandpass)", "Chipmunk (Tăng tone)",
            "Deep Voice (Giảm tone)", "Chorus/Phát xung (Chorus)", "Phaser (Âm kim loại)",
            "Bitcrush (Giọng rè kỹ thuật số)", "Whisper (Thì thầm)"
        }.Select(l => new ComboOption(l)));

    [ObservableProperty] private double _videoVolume = 100;
    [ObservableProperty] private double _audioVolume = 100;
    [ObservableProperty] private double _audio2Volume = 100;
    [ObservableProperty] private ComboOption _audio1Fx = new("Không (Giữ nguyên)");
    [ObservableProperty] private double _audio1FxIntensity = 100;

    public string VideoVolumeDisplay => $"{VideoVolume:0}%";
    public string AudioVolumeDisplay => $"{AudioVolume:0}%";
    public string Audio2VolumeDisplay => $"{Audio2Volume:0}%";
    public string Audio1FxIntensityDisplay => $"{Audio1FxIntensity:0}%";

    partial void OnVideoVolumeChanged(double value) => OnPropertyChanged(nameof(VideoVolumeDisplay));
    partial void OnAudioVolumeChanged(double value) => OnPropertyChanged(nameof(AudioVolumeDisplay));
    partial void OnAudio2VolumeChanged(double value) => OnPropertyChanged(nameof(Audio2VolumeDisplay));
    partial void OnAudio1FxIntensityChanged(double value) => OnPropertyChanged(nameof(Audio1FxIntensityDisplay));
}

/// <summary>Khu WATERMARK ( DEBUGGING ) — trạng thái logo + EDIT POSITION (VISUAL) → cửa sổ preview kéo-thả.</summary>
public partial class WatermarkViewModel : ObservableObject
{
    public Action? BrowseRequested { get; set; }
    public Action? OpenEditorRequested { get; set; }

    [ObservableProperty] private string _watermarkPath = "";
    [ObservableProperty] private string _pathDisplay = "Select Logo...";

    // Trạng thái mirror các field _watermark* của MainWindow.
    public double Scale { get; set; } = 1.0;
    public double Rotation { get; set; }
    public double Opacity { get; set; } = 1.0;
    public double XPercent { get; set; } = 0.05;
    public double YPercent { get; set; } = 0.05;
    public double AspectRatio { get; set; } = 1.0;

    [RelayCommand] private void Browse() => BrowseRequested?.Invoke();
    [RelayCommand] private void OpenEditor() => OpenEditorRequested?.Invoke();

    public void SetPath(string path, double aspect)
    {
        WatermarkPath = path;
        PathDisplay = System.IO.Path.GetFileName(path);
        AspectRatio = aspect;
    }
}

/// <summary>Khu COLOR GRADING.</summary>
public partial class ColorGradingViewModel : ObservableObject
{
    public ObservableCollection<ComboOption> FilterOptions { get; } = new(
        new[] { "None (Original)", "Spring", "Autumn", "Winter", "Cold", "Warm", "Cinematic" }
        .Select(l => new ComboOption(l)));

    [ObservableProperty] private ComboOption _filter = new("None (Original)");
    [ObservableProperty] private double _intensity = 50;
    [ObservableProperty] private double _brightness;

    public string IntensityDisplay => $"{Intensity:0}%";
    public string BrightnessDisplay => Brightness > 0 ? $"+{Brightness:0}" : $"{Brightness:0}";

    partial void OnIntensityChanged(double value) => OnPropertyChanged(nameof(IntensityDisplay));
    partial void OnBrightnessChanged(double value) => OnPropertyChanged(nameof(BrightnessDisplay));
}

/// <summary>Khu EXPORT — mirror CmbRes_SelectionChanged / CmbBitrate_SelectionChanged / TxtOutputResolution_TextChanged.</summary>
public partial class ExportViewModel : ObservableObject
{
    public Action? BrowseOutputRequested { get; set; }

    public ObservableCollection<ComboOption> ResOptions { get; } = new(
        new[] { "Original", "1920x1080 (HD)", "1080x1920 (Portrait)", "720x1280 (Portrait HD)", "1280x720", "Custom (Nhập số)" }
        .Select(l => new ComboOption(l)));

    public ObservableCollection<ComboOption> FpsOptions { get; } = new(
        new[] { "Original", "60", "30", "24" }.Select(l => new ComboOption(l)));

    public ObservableCollection<ComboOption> HardwareOptions { get; } = new(
        new[] { "NVIDIA NVENC", "NVIDIA NVENC (AV1)", "AMD AMF", "AMD AMF (AV1)", "Cpu" }
        .Select(l => new ComboOption(l)));

    public ObservableCollection<ComboOption> BitrateOptions { get; } = new(
        new[] { "Match Source", "Boost Quality", "Custom (Kbps)" }.Select(l => new ComboOption(l)));

    [ObservableProperty] private string _outputDirDisplay = "Default: Desktop";
    [ObservableProperty] private string _customOutputFolder = "";
    [ObservableProperty] private string _outputName = "";
    [ObservableProperty] private ComboOption _res = new("1080x1920 (Portrait)");
    [ObservableProperty] private bool _showCustomRes;
    [ObservableProperty] private string _resW = "1080";
    [ObservableProperty] private string _resH = "1920";
    [ObservableProperty] private ComboOption _fps = new("Original");
    [ObservableProperty] private ComboOption _hardware = new("NVIDIA NVENC");
    [ObservableProperty] private ComboOption _bitrate = new("Match Source");
    [ObservableProperty] private bool _showCustomBitrate;
    [ObservableProperty] private string _customBitrate = "5000";

    [RelayCommand] private void BrowseOutput() => BrowseOutputRequested?.Invoke();

    partial void OnResChanged(ComboOption value) => ShowCustomRes = value.Label.Contains("Custom");
    partial void OnBitrateChanged(ComboOption value) => ShowCustomBitrate = value.Label.Contains("Custom");

    public event System.Action? OutputSizeChanged;

    partial void OnResWChanged(string value) => OutputSizeChanged?.Invoke();
    partial void OnResHChanged(string value) => OutputSizeChanged?.Invoke();
}
