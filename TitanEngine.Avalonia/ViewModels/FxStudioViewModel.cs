using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using TitanEngine.Core;

namespace TitanEngine.Avalonia.ViewModels;

/// <summary>
/// Khu FX STUDIO — mirror UpdateSnowfallOpacityUi / SldFx_ValueChanged / UpdateFxTimelineVisualization
/// của MainWindow. Các hiệu ứng cần bộ vẽ WPF (Snowfall/Polaroid) vẫn hiện control nhưng bị tắt
/// ở lớp xử lý job (xem JobBuilder + PORT-PROGRESS.md).
/// </summary>
public partial class FxStudioViewModel : ObservableObject
{
    public ObservableCollection<ComboOption> FxTypeOptions { get; } = new(
        new[]
        {
            new ComboOption("None"),
            new ComboOption("Flip Horizontal (Anti-Copyright)"),
            new ComboOption("Rò phim (Leak 1 / Light Leak)"),
            new ComboOption("Light Leak Burn"),
            new ComboOption("Cinematic Glow"),
            new ComboOption("Halo Glow"),
            new ComboOption("Prism Light"),
            new ComboOption("Editorial Bloom"),
            // Crop visual editor dùng MediaElement (WPF-stay) → disabled + tooltip (Giai đoạn 5).
            new ComboOption("Crop Video (Custom Frame)", Enabled: false),
        });

    public ObservableCollection<ComboOption> TemplateStyleOptions { get; } = new(
        new[]
        {
            "None", "Smooth Zoom In", "Smooth Zoom Out", "Pan Left", "Pan Right", "Velocity Punch",
            "Cinematic Fade Zoom", "Glitch Distort", "3D Spin Lite", "Trend Zoom Flash", "Trend Blur Pulse",
            "Trend Spin Glitch", "Digicam Memory", "Beat Photo Dump", "Film Strip", "Magazine Cover"
        }.Select(l => new ComboOption(l)));

    public ObservableCollection<ComboOption> BorderTypeOptions { get; } = new(
        new[]
        {
            new ComboOption("None"),
            // 4 polaroid border đều cần bộ vẽ WPF (IImageServices.Generate*Overlay) → disabled.
            new ComboOption("Polaroid Scrapbook", Enabled: false),
            new ComboOption("Polaroid Scrapbook 2", Enabled: false),
            new ComboOption("RGB Polaroid Border", Enabled: false),
            new ComboOption("Dashed Polaroid Frame", Enabled: false),
        });

    public ObservableCollection<ComboOption> RainPresetOptions { get; } = new(
        new[] { "Heavy Rain", "Cinematic Rain", "Window Drops" }.Select(l => new ComboOption(l)));

    public ObservableCollection<ComboOption> RainIntensityOptions { get; } = new(
        new[] { "medium", "subtle", "strong" }.Select(l => new ComboOption(l)));

    public ObservableCollection<ComboOption> BrandLogoPositionOptions { get; } = new(
        new[] { "Top Right", "Top Left", "Center Top", "Bottom Right", "Bottom Left", "Center Bottom" }
        .Select(l => new ComboOption(l)));

    public ObservableCollection<ComboOption> CropAspectRatioOptions { get; } = new(new[] { "Free" }.Select(l => new ComboOption(l)));

    [ObservableProperty] private ComboOption _fxType = new("None");
    [ObservableProperty] private ComboOption _templateStyle = new("None");
    [ObservableProperty] private ComboOption _borderType = new("None");
    [ObservableProperty] private ComboOption _rainPreset = new("Heavy Rain");
    [ObservableProperty] private ComboOption _rainIntensity = new("medium");
    [ObservableProperty] private ComboOption _brandLogoPosition = new("Top Right");
    [ObservableProperty] private bool _snowOverlay1;
    [ObservableProperty] private bool _snowOverlay2;
    [ObservableProperty] private bool _snowOverlay3;
    [ObservableProperty] private bool _overlayIntro;
    [ObservableProperty] private bool _enableBrandLogo;
    [ObservableProperty] private double _fxIntensity = 50;
    [ObservableProperty] private string _magazineCoverTitle = "TITAN";
    [ObservableProperty] private string _magazineCoverSubtitle = "COVER STORY";
    [ObservableProperty] private double _cropZoom;
    [ObservableProperty] private double _fxStart;
    [ObservableProperty] private double _fxEnd = 100;
    [ObservableProperty] private bool _enableFade;
    [ObservableProperty] private string _fadeInSeconds = "0.8";
    [ObservableProperty] private string _fadeOutSeconds = "0.8";

    // ── UpdateSnowfallOpacityUi: visibility các panel opacity ──
    [ObservableProperty] private bool _showSnowfallOpacity;
    [ObservableProperty] private bool _showSnowOverlay1Opacity;
    [ObservableProperty] private bool _showSnowOverlay2Opacity;
    [ObservableProperty] private bool _showSnowOverlay3Opacity;
    [ObservableProperty] private bool _showOverlayIntroOpacity;
    [ObservableProperty] private bool _showSharedSnowOpacity;

    public string CropZoomDisplay => $"{CropZoom:0}%";
    public double FxRangeLeft => FxStart;
    public double FxRangeWidth => System.Math.Max(0, FxEnd - FxStart);

    partial void OnCropZoomChanged(double value) => OnPropertyChanged(nameof(CropZoomDisplay));
    partial void OnFxStartChanged(double value) { OnPropertyChanged(nameof(FxRangeLeft)); OnPropertyChanged(nameof(FxRangeWidth)); }
    partial void OnFxEndChanged(double value) { OnPropertyChanged(nameof(FxRangeWidth)); }

    partial void OnSnowOverlay1Changed(bool value) => UpdateSnowfallOpacityUi();
    partial void OnSnowOverlay2Changed(bool value) => UpdateSnowfallOpacityUi();
    partial void OnSnowOverlay3Changed(bool value) => UpdateSnowfallOpacityUi();
    partial void OnOverlayIntroChanged(bool value) => UpdateSnowfallOpacityUi();
    partial void OnFxTypeChanged(ComboOption value) => UpdateSnowfallOpacityUi();

    // ── MainWindow: GetSelectedOverlayEffectFromUi ──
    public string GetSelectedOverlayEffect()
        => SnowOverlay1 || SnowOverlay2 || SnowOverlay3 || OverlayIntro ? "Snowfall Overlay" : "None";

    // ── MainWindow: UpdateSnowfallOpacityUi ──
    public void UpdateSnowfallOpacityUi()
    {
        string overlayEffect = GetSelectedOverlayEffect();
        string fxEffect = LocalhostRequestMapper.ResolveFxEffectName(FxType.Label);
        bool showSnowUi = overlayEffect.Equals("Snowfall Overlay", System.StringComparison.OrdinalIgnoreCase) ||
                          fxEffect.Equals("Snowfall Overlay", System.StringComparison.OrdinalIgnoreCase);
        ShowSnowOverlay1Opacity = showSnowUi && SnowOverlay1;
        ShowSnowOverlay2Opacity = showSnowUi && SnowOverlay2;
        ShowSnowOverlay3Opacity = showSnowUi && SnowOverlay3;
        ShowOverlayIntroOpacity = showSnowUi && OverlayIntro;
        ShowSharedSnowOpacity = showSnowUi && !ShowSnowOverlay1Opacity && !ShowSnowOverlay2Opacity
                                && !ShowSnowOverlay3Opacity && !ShowOverlayIntroOpacity;
        ShowSnowfallOpacity = showSnowUi;
    }
}
