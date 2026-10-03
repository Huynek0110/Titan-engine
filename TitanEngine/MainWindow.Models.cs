using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Media;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;


namespace TitanEngine {
    #region --- [DATA MODELS] ---

    public class RenderJob : INotifyPropertyChanged
    {
        private string _statusDisplay = "WAITING";
        private SolidColorBrush _statusColor = Brushes.Cyan;
        private double _progress;

        public int Id { get; set; }
        public string Guid { get; private set; } = System.Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper();

        public string SourcePath { get; set; } = string.Empty;
        public List<string> MergeInputPaths { get; set; } = new List<string>();
        public bool MergeVideos { get; set; } = false;
        public List<string> MergeAudioPaths { get; set; } = new List<string>();
        public bool MergeAudio { get; set; } = false;
        public string? AudioPath { get; set; }
        public string? SecondaryAudioPath { get; set; }
        public string? WatermarkPath { get; set; }
        public string OutputName { get; set; } = string.Empty;
        public string ColorFilter { get; set; } = "None";
        public double ColorIntensity { get; set; } = 1.0;
        public double VideoBrightness { get; set; } = 0.0;

        public string HardwareProfile { get; set; } = "Auto";
        public string BitrateStrategy { get; set; } = "Match Source";
        public string Resolution { get; set; } = "Original";
        public int TargetWidth { get; set; } = -1;
        public int TargetHeight { get; set; } = -1;
        public string Framerate { get; set; } = "Original";
        public string CustomBitrate { get; set; } = "5000";

        public string VideoTrimStart { get; set; } = "00:00:00";
        public string VideoTrimDuration { get; set; } = "";
        public double SplitDurationSeconds { get; set; } = 0.0;
        public double ImageTimelineDurationSeconds { get; set; } = 0.0;
        public string AudioTrimStart { get; set; } = "00:00:00";
        public string AudioTrimEnd { get; set; } = "";
        public string AudioTrimDuration { get; set; } = "";

        public double WatermarkScale { get; set; } = 1.0;
        public double WatermarkRotation { get; set; } = 0.0;
        public double WatermarkOpacity { get; set; } = 1.0;
        public double WatermarkXPercent { get; set; } = 0.05;
        public double WatermarkYPercent { get; set; } = 0.05;
        public double WatermarkAspectRatio { get; set; } = 1.0;

        public bool Enable60fps { get; set; } = false;
        public double RsmbIntensity { get; set; } = 0.0;

        public string FxEffect { get; set; } = "None";
        public string OverlayEffect { get; set; } = "None";
        public string BorderEffect { get; set; } = "None";
        public bool EnableSnowOverlay1 { get; set; } = false;
        public bool EnableSnowOverlay2 { get; set; } = false;
        public bool EnableSnowOverlay3 { get; set; } = false;
        public bool EnableOverlayIntro { get; set; } = false;
        public bool EnableCrossTransitions { get; set; } = false;
        public string CrossTransitionType { get; set; } = "Dissolve";
        public double CrossTransitionDuration { get; set; } = 0.6;
        public string TemplateName { get; set; } = "None";
        public bool EnablePolaroidScrapbook { get; set; } = false;
        public bool EnableRgbPolaroidScrapbook { get; set; } = false;
        public bool EnableDashedPolaroidScrapbook { get; set; } = false;
        public bool EnableBrandLogo { get; set; } = false;
        public string BrandLogoPosition { get; set; } = "Top Right";
        public string MusicSyncMode { get; set; } = "off";
        public string MagazineCoverTitle { get; set; } = "TITAN";
        public string MagazineCoverSubtitle { get; set; } = "COVER STORY";
        public double LightLeakBurnIntensity { get; set; } = 65.0;
        public double LightLeakBurnSpread { get; set; } = 55.0;
        public double LightLeakBurnWarmth { get; set; } = 72.0;
        public double LightLeakBurnBurn { get; set; } = 60.0;
        public double LightLeakBurnEdgeSoftness { get; set; } = 75.0;
        public double LightLeakBurnGrain { get; set; } = 35.0;
        public string LightLeakBurnDirection { get; set; } = "from-left";
        public double LightLeakBurnDuration { get; set; } = 0.95;
        public string LightLeakBurnBlendMode { get; set; } = "screen";
        public double LightLeakBurnOpacity { get; set; } = 0.82;
        public string? LightLeakBurnAssetPath { get; set; }
        public bool LightLeakBurnUseAssetOverlay { get; set; } = false;
        public string LightLeakOverlayMode { get; set; } = "auto";
        public string? LightLeakOverlayId { get; set; }
        public string? LightLeakOverlayAssetPath { get; set; }
        public double LightLeakOverlayOpacity { get; set; } = -1.0;
        public string SnowfallPreset { get; set; } = "Snow Cinematic";
        public string SnowfallMode { get; set; } = "auto";
        public string? SnowfallAssetPath { get; set; }
        public double SnowfallOpacity { get; set; } = -1.0;
        public double SnowOverlay1Opacity { get; set; } = -1.0;
        public double SnowOverlay2Opacity { get; set; } = -1.0;
        public double SnowOverlay3Opacity { get; set; } = -1.0;
        public double OverlayIntroOpacity { get; set; } = -1.0;
        public string RainOverlayPreset { get; set; } = "Heavy Rain";
        public string RainOverlayIntensity { get; set; } = "medium";
        public string? RainOverlayAssetPath { get; set; }
        public double FxIntensity { get; set; } = 50.0;
        public double FxStartPercent { get; set; } = 0.0;
        public double FxEndPercent { get; set; } = 100.0;
        public bool EnableFade { get; set; } = false;
        public double FadeInSeconds { get; set; } = 0.8;
        public double FadeOutSeconds { get; set; } = 0.8;
        public bool CropEnabled { get; set; } = false;
        public double CropZoomPercent { get; set; } = 0.0;
        public string CropAspectRatio { get; set; } = "Free";
        public double CropXPercent { get; set; } = 0.0;
        public double CropYPercent { get; set; } = 0.0;
        public double CropWidthPercent { get; set; } = 1.0;
        public double CropHeightPercent { get; set; } = 1.0;

        public double VideoVolume { get; set; } = 1.0;
        public double AudioVolume { get; set; } = 1.0;
        public double SecondaryAudioVolume { get; set; } = 1.0;
        public double SourceAudioSpeed { get; set; } = 1.0;
        public double ExternalAudioSpeed { get; set; } = 1.0;
        public List<AudioEditSegment> AudioSegments { get; set; } = new List<AudioEditSegment>();

        public string UpscaleMode { get; set; } = "Off";
        public double SharpnessIntensity { get; set; } = 1.0;

        public double SlowMotionSpeed { get; set; } = 1.0;
        public bool SlowMotionAudio { get; set; } = true;

        public double DurationSec { get; set; }
        public long SourceBitrate { get; set; }
        public int SourceWidth { get; set; }
        public int SourceHeight { get; set; }
        public double SourceFps { get; set; }

        public string? ResultPath { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public bool IsApiJob { get; set; }
        public string? WebhookUrl { get; set; }
        public bool EnableLoudnorm { get; set; }
        public string? Audio1Effect { get; set; }
        public int Audio1EffectIntensity { get; set; } = 100;
        public string? TextOverlay { get; set; }
        public string? TextOverlayPosition { get; set; }
        public string? TextAlign { get; set; }
        public int? TextOverlayFontSize { get; set; }
        public string? TextOverlayColor { get; set; }
        public string? TextOverlayStyle { get; set; } = "Aesthetic Lyric (Georgia Italic)";
        public bool EnableAudioSpectrum { get; set; } = false;
        public bool AddQuotes { get; set; } = true;
        public bool DisableTextScroll { get; set; } = false;
        public double? TextOverlayXPercent { get; set; }
        public double? TextOverlayYPercent { get; set; }
        public bool EnableTextGlow { get; set; } = false;
        public string? TextGlowColor { get; set; }

        // Text Overlay animations ported from Caption Sync
        public string? TextOverlayAnimation { get; set; } // empty = legacy drawtext typewriter
        public double TextOverlayEffectDuration { get; set; } = 5.0;

        // Caption Sync (Whisper AI)
        public bool EnableCaptionSync { get; set; } = false;
        public string? CaptionSyncLanguage { get; set; }
        public string? CaptionSyncPosition { get; set; }
        public double? CaptionSyncXPercent { get; set; }
        public double? CaptionSyncYPercent { get; set; }
        public int? CaptionSyncFontSize { get; set; }
        public string? CaptionSyncColor { get; set; }
        public string? CaptionSyncModelSize { get; set; } // "Small", "Medium", "Large-v3-Turbo"
        public string? CaptionSyncAnimation { get; set; } // "None", "Spotify Scroll"
        public string? CaptionSyncFontFamily { get; set; }
        public string Name
        {
            get
            {
                if (MergeVideos && MergeInputPaths.Count > 1)
                {
                    string firstName = System.IO.Path.GetFileName(MergeInputPaths[0]);
                    return $"{firstName} (+{MergeInputPaths.Count - 1})";
                }

                return System.IO.Path.GetFileName(SourcePath);
            }
        }

        public string ConfigSummary
        {
            get
            {
                string info = $"[{HardwareProfile}] {Resolution} @ {Framerate}";
                if (MergeVideos && MergeInputPaths.Count > 1)
                    info += $" | Merge:{MergeInputPaths.Count}";
                if (MergeAudio && MergeAudioPaths.Count > 1)
                    info += $" | AudioMerge:{MergeAudioPaths.Count}";
                if (!string.IsNullOrWhiteSpace(VideoTrimDuration))
                    info += $" | VTrim:{VideoTrimDuration}";
                if (ImageTimelineDurationSeconds > 0.001)
                    info += $" | ImgDur:{ImageTimelineDurationSeconds:0.##}s";
                if (!string.IsNullOrWhiteSpace(WatermarkPath))
                    info += " | Wmk";
                if (Math.Abs(VideoBrightness) > 0.001)
                    info += $" | Bright:{VideoBrightness:+0;-0;0}";
                if (Math.Abs(SourceAudioSpeed - 1.0) > 0.001)
                    info += $" | AudSpd:{SourceAudioSpeed:0.00}x";
                if (Math.Abs(ExternalAudioSpeed - 1.0) > 0.001)
                    info += $" | ExtAudSpd:{ExternalAudioSpeed:0.00}x";
                if (AudioSegments != null && AudioSegments.Count > 0)
                    info += $" | AudCuts:{AudioSegments.Count}";
                if (!string.IsNullOrWhiteSpace(TemplateName) &&
                    !TemplateName.Equals("None", StringComparison.OrdinalIgnoreCase))
                    info += $" | Tpl:{TemplateName}";
                if (!string.IsNullOrWhiteSpace(OverlayEffect) &&
                    !OverlayEffect.Equals("None", StringComparison.OrdinalIgnoreCase))
                    info += $" | Ovl:{OverlayEffect}";
                if (!string.IsNullOrWhiteSpace(BorderEffect) &&
                    !BorderEffect.Equals("None", StringComparison.OrdinalIgnoreCase))
                    info += $" | Border:{BorderEffect}";
                if (EnablePolaroidScrapbook)
                    info += " | Polaroid";
                if (EnableRgbPolaroidScrapbook)
                    info += " | RGBPolaroid";
                if (EnableDashedPolaroidScrapbook)
                    info += " | DashPolaroid";
                if (EnableBrandLogo)
                    info += $" | BrandLogo:{BrandLogoPosition}";
                if (!string.IsNullOrWhiteSpace(SnowfallPreset) &&
                    !SnowfallPreset.Equals("Snow Cinematic", StringComparison.OrdinalIgnoreCase))
                    info += $" | Snow:{SnowfallPreset}";
                if (!string.IsNullOrWhiteSpace(MusicSyncMode) &&
                    MusicSyncMode.Equals("beat", StringComparison.OrdinalIgnoreCase))
                    info += " | Beat";
                if (EnableFade)
                    info += $" | Fade:{FadeInSeconds:0.##}/{FadeOutSeconds:0.##}s";
                if (CropZoomPercent > 0.001)
                    info += $" | Crop:{CropZoomPercent:0.#}%";
                else if (!string.IsNullOrWhiteSpace(CropAspectRatio) &&
                    !CropAspectRatio.Equals("Free", StringComparison.OrdinalIgnoreCase))
                    info += $" | CropAR:{CropAspectRatio}";
                return info;
            }
        }

        public string StatusDisplay
        {
            get => _statusDisplay;
            set { _statusDisplay = value; OnPropertyChanged(); }
        }

        public SolidColorBrush StatusColor
        {
            get => _statusColor;
            set { _statusColor = value; OnPropertyChanged(); }
        }

        public double Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class LocalhostRenderRequest
    {
        public string InputFile { get; set; } = string.Empty;
        public List<string>? InputFiles { get; set; }
        public bool? MergeVideos { get; set; }
        public List<string>? AudioFiles { get; set; }
        public bool? MergeAudio { get; set; }
        public string OutputVideo { get; set; } = string.Empty;
        public string? AudioFile { get; set; }
        public string? AudioFile2 { get; set; }
        public string? WatermarkFile { get; set; }
        public bool? WaitForCompletion { get; set; }
        public string? WebhookUrl { get; set; }
        public bool? EnableLoudnorm { get; set; }
        public string? TextOverlay { get; set; }
        public string? TextOverlayPosition { get; set; }
        public string? TextAlign { get; set; }
        public int? TextOverlayFontSize { get; set; }
        public string? TextOverlayColor { get; set; }
        public string? TextOverlayStyle { get; set; }
        public bool? EnableAudioSpectrum { get; set; }
        public bool? AddQuotes { get; set; }
        public bool? DisableTextScroll { get; set; }
        public double? TextOverlayXPercent { get; set; }
        public double? TextOverlayYPercent { get; set; }
        public bool? EnableTextGlow { get; set; }
        public string? TextGlowColor { get; set; }
        public string? TextOverlayAnimation { get; set; }
        public double? TextOverlayEffectDuration { get; set; }

        public string? ColorGrading { get; set; }
        public double? ColorIntensity { get; set; }
        public double? VideoBrightness { get; set; }

        public string? FxEffect { get; set; }
        public string? Overlay { get; set; }
        public string? OverlayEffect { get; set; }
        public string? Border { get; set; }
        public string? BorderEffect { get; set; }
        public bool? EnableSnowOverlay1 { get; set; }
        public bool? EnableSnowOverlay2 { get; set; }
        public bool? EnableSnowOverlay3 { get; set; }
        public bool? EnableOverlayIntro { get; set; }
        public bool? EnableCrossTransitions { get; set; }
        public string? CrossTransitionType { get; set; }
        public double? CrossTransitionDuration { get; set; }
        public string? TemplateName { get; set; }
        public bool? EnablePolaroidScrapbook { get; set; }
        public bool? EnableRgbPolaroidScrapbook { get; set; }
        public bool? EnableDashedPolaroidScrapbook { get; set; }
        public bool? EnableBrandLogo { get; set; }
        public string? BrandLogoPosition { get; set; }
        public string? MusicSyncMode { get; set; }
        public string? MagazineCoverTitle { get; set; }
        public string? MagazineCoverSubtitle { get; set; }
        public double? LightLeakBurnIntensity { get; set; }
        public double? LightLeakBurnSpread { get; set; }
        public double? LightLeakBurnWarmth { get; set; }
        public double? LightLeakBurnBurn { get; set; }
        public double? LightLeakBurnEdgeSoftness { get; set; }
        public double? LightLeakBurnGrain { get; set; }
        public string? LightLeakBurnDirection { get; set; }
        public double? LightLeakBurnDuration { get; set; }
        public double? LightLeakBurnOpacity { get; set; }
        public string? LightLeakBurnBlendMode { get; set; }
        public string? LightLeakBurnAssetPath { get; set; }
        public bool? LightLeakBurnUseAssetOverlay { get; set; }
        public string? LightLeakOverlayMode { get; set; }
        public string? LightLeakOverlayId { get; set; }
        public string? LightLeakOverlayAssetPath { get; set; }
        public double? LightLeakOverlayOpacity { get; set; }
        public string? SnowfallPreset { get; set; }
        public string? SnowfallMode { get; set; }
        public string? SnowfallAssetPath { get; set; }
        public double? SnowfallOpacity { get; set; }
        public double? SnowOverlay1Opacity { get; set; }
        public double? SnowOverlay2Opacity { get; set; }
        public double? SnowOverlay3Opacity { get; set; }
        public double? OverlayIntroOpacity { get; set; }
        public string? RainOverlayPreset { get; set; }
        public string? RainOverlayIntensity { get; set; }
        public string? RainOverlayAssetPath { get; set; }
        public double? FxIntensity { get; set; }
        public double? FxStartPercent { get; set; }
        public double? FxEndPercent { get; set; }
        public bool? EnableFade { get; set; }
        public double? FadeInSeconds { get; set; }
        public double? FadeOutSeconds { get; set; }
        public double? ImageTimelineDurationSeconds { get; set; }
        public double? CropZoomPercent { get; set; }
        public string? CropAspectRatio { get; set; }
        public double? CropXPercent { get; set; }
        public double? CropYPercent { get; set; }
        public double? CropWidthPercent { get; set; }
        public double? CropHeightPercent { get; set; }

        public string? HardwareProfile { get; set; }
        public string? BitrateStrategy { get; set; }
        public string? Resolution { get; set; }
        public int? TargetWidth { get; set; }
        public int? TargetHeight { get; set; }
        public string? Framerate { get; set; }
        public string? CustomBitrate { get; set; }

        public string? VideoTrimStart { get; set; }
        public string? VideoTrimDuration { get; set; }
        public double? SplitDurationSeconds { get; set; }
        public string? AudioTrimStart { get; set; }
        public string? AudioTrimEnd { get; set; }
        public string? AudioTrimDuration { get; set; }

        public double? WatermarkScale { get; set; }
        public double? WatermarkRotation { get; set; }
        public double? WatermarkOpacity { get; set; }
        public double? WatermarkXPercent { get; set; }
        public double? WatermarkYPercent { get; set; }
        public double? WatermarkAspectRatio { get; set; }

        public double? VideoVolume { get; set; }
        public double? AudioVolume { get; set; }
        public double? Audio2Volume { get; set; }
        public double? SecondaryAudioVolume { get; set; }
        public string? Audio1Effect { get; set; }
        public double? Audio1EffectIntensity { get; set; }
        public double? SourceAudioSpeed { get; set; }
        public double? ExternalAudioSpeed { get; set; }
        public List<AudioEditSegment>? AudioSegments { get; set; }

        public string? UpscaleMode { get; set; }
        public double? SharpnessIntensity { get; set; }

        public double? SlowMotionSpeed { get; set; }
        public bool? SlowMotionAudio { get; set; }

        // Caption Sync (Whisper AI)
        public bool? EnableCaptionSync { get; set; }
        public string? CaptionSyncLanguage { get; set; }
        public string? CaptionSyncPosition { get; set; }
        public double? CaptionSyncXPercent { get; set; }
        public double? CaptionSyncYPercent { get; set; }
        public int? CaptionSyncFontSize { get; set; }
        public string? CaptionSyncColor { get; set; }
        public string? CaptionSyncModelSize { get; set; }
        public string? CaptionSyncAnimation { get; set; }
        public string? CaptionSyncFontFamily { get; set; }
        public Dictionary<string, JsonElement>? Features { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtraFields { get; set; }
    }

    public class LocalhostRenderResponse
    {
        public bool Success { get; set; }
        public int? QueueId { get; set; }
        public int? TotalJobs { get; set; }
        public int? StartedJobs { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? JobId { get; set; }
        public string? Status { get; set; }
        public bool? Done { get; set; }
        public bool? JobSuccess { get; set; }
        public double? Progress { get; set; }
        public string? StatusUrl { get; set; }
        public string? RequestedOutput { get; set; }
        public string? EngineOutput { get; set; }
        public string? FinalOutput { get; set; }
        public string? InputFile { get; set; }
        public List<string>? InputFiles { get; set; }
        public int? InputCount { get; set; }
        public bool? MergeVideos { get; set; }
        public string? AudioFile { get; set; }
        public string? AudioFile2 { get; set; }
        public List<string>? AudioFiles { get; set; }
        public int? AudioCount { get; set; }
        public bool? MergeAudio { get; set; }
        public string? AudioTrimStart { get; set; }
        public string? AudioTrimEnd { get; set; }
        public string? AudioTrimDuration { get; set; }
        public List<AudioEditSegment>? AudioSegments { get; set; }
        public string? OutputVideo { get; set; }

        // [NEW] Video Information Fields
        public string? VideoId { get; set; }
        public double? SourceDuration { get; set; }
        public int? SourceWidth { get; set; }
        public int? SourceHeight { get; set; }
        public long? SourceBitrate { get; set; }
        public double? SourceFramerate { get; set; }
        public int? OutputWidth { get; set; }
        public int? OutputHeight { get; set; }
        public string? OutputFramerate { get; set; }
        public string? SourceFileName { get; set; }
        public string? OutputFileName { get; set; }
        public long? OutputFileSize { get; set; }
        public double? EncodingTime { get; set; }
        public bool? EnableFade { get; set; }
        public double? FadeInSeconds { get; set; }
        public double? FadeOutSeconds { get; set; }
        public double? ImageTimelineDurationSeconds { get; set; }
        public double? CropZoomPercent { get; set; }
        public string? CropAspectRatio { get; set; }
        public string? TemplateName { get; set; }
        public string? OverlayEffect { get; set; }
        public bool? EnableSnowOverlay1 { get; set; }
        public bool? EnableSnowOverlay2 { get; set; }
        public bool? EnableSnowOverlay3 { get; set; }
        public bool? EnableOverlayIntro { get; set; }
        public bool? EnableCrossTransitions { get; set; }
        public string? CrossTransitionType { get; set; }
        public double? CrossTransitionDuration { get; set; }
        public bool? EnablePolaroidScrapbook { get; set; }
        public bool? EnableRgbPolaroidScrapbook { get; set; }
        public bool? EnableDashedPolaroidScrapbook { get; set; }
        public bool? EnableBrandLogo { get; set; }
        public string? BrandLogoPosition { get; set; }
        public string? SnowfallPreset { get; set; }
        public string? SnowfallMode { get; set; }
        public double? SnowfallOpacity { get; set; }
        public double? SnowOverlay1Opacity { get; set; }
        public double? SnowOverlay2Opacity { get; set; }
        public double? SnowOverlay3Opacity { get; set; }
        public double? OverlayIntroOpacity { get; set; }
        public string? RainOverlayPreset { get; set; }
        public string? RainOverlayIntensity { get; set; }
        public string? MusicSyncMode { get; set; }
        public List<LocalhostQueueJobInfo>? Jobs { get; set; }
    }

    public class LocalhostQueueJobInfo
    {
        public int QueueId { get; set; }
        public string JobId { get; set; } = string.Empty;
        public string VideoId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool Done { get; set; }
        public bool JobSuccess { get; set; }
        public double Progress { get; set; }
        public string StatusUrl { get; set; } = string.Empty;
        public string? InputFile { get; set; }
        public List<string>? InputFiles { get; set; }
        public int? InputCount { get; set; }
        public bool? MergeVideos { get; set; }
        public bool EnableCrossTransitions { get; set; }
        public string? CrossTransitionType { get; set; }
        public double CrossTransitionDuration { get; set; }
        public string? AudioFile { get; set; }
        public string? AudioFile2 { get; set; }
        public List<string>? AudioFiles { get; set; }
        public int? AudioCount { get; set; }
        public bool? MergeAudio { get; set; }
        public string? AudioTrimStart { get; set; }
        public string? AudioTrimEnd { get; set; }
        public string? AudioTrimDuration { get; set; }
        public List<AudioEditSegment>? AudioSegments { get; set; }
        public string? OutputVideo { get; set; }
        public string? SourceFileName { get; set; }
        public string? OutputFileName { get; set; }
        public double? SourceDuration { get; set; }
        public int? SourceWidth { get; set; }
        public int? SourceHeight { get; set; }
        public long? SourceBitrate { get; set; }
        public double? SourceFramerate { get; set; }
        public int? OutputWidth { get; set; }
        public int? OutputHeight { get; set; }
        public string? OutputFramerate { get; set; }
        public bool? EnableFade { get; set; }
        public double? FadeInSeconds { get; set; }
        public double? FadeOutSeconds { get; set; }
        public double? ImageTimelineDurationSeconds { get; set; }
        public double? CropZoomPercent { get; set; }
        public string? CropAspectRatio { get; set; }
        public string? TemplateName { get; set; }
        public string? OverlayEffect { get; set; }
        public bool? EnableSnowOverlay1 { get; set; }
        public bool? EnableSnowOverlay2 { get; set; }
        public bool? EnableSnowOverlay3 { get; set; }
        public bool? EnableOverlayIntro { get; set; }
        public bool? EnablePolaroidScrapbook { get; set; }
        public bool? EnableRgbPolaroidScrapbook { get; set; }
        public bool? EnableDashedPolaroidScrapbook { get; set; }
        public bool? EnableBrandLogo { get; set; }
        public string? BrandLogoPosition { get; set; }
        public string? SnowfallPreset { get; set; }
        public string? SnowfallMode { get; set; }
        public double? SnowfallOpacity { get; set; }
        public double? SnowOverlay1Opacity { get; set; }
        public double? SnowOverlay2Opacity { get; set; }
        public double? SnowOverlay3Opacity { get; set; }
        public double? OverlayIntroOpacity { get; set; }
        public string? RainOverlayPreset { get; set; }
        public string? RainOverlayIntensity { get; set; }
        public string? MusicSyncMode { get; set; }
    }

    public class ApiRenderJobState
    {
        public int QueueId { get; set; }
        public string JobId { get; set; } = string.Empty;
        public string Status { get; set; } = "queued";
        public bool Done { get; set; }
        public bool Success { get; set; }
        public double Progress { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? WebhookUrl { get; set; }
        public string? RequestedOutput { get; set; }
        public string? EngineOutput { get; set; }
        public string? FinalOutput { get; set; }
        public string? InputFile { get; set; }
        public List<string>? InputFiles { get; set; }
        public bool MergeVideos { get; set; }
        public string? AudioFile { get; set; }
        public string? AudioFile2 { get; set; }
        public List<string>? AudioFiles { get; set; }
        public bool MergeAudio { get; set; }
        public string? AudioTrimStart { get; set; }
        public string? AudioTrimEnd { get; set; }
        public string? AudioTrimDuration { get; set; }
        public List<AudioEditSegment>? AudioSegments { get; set; }
        public string? OutputVideo { get; set; }
        public string? TemplateName { get; set; }
        public string? OverlayEffect { get; set; }
        public bool EnableSnowOverlay1 { get; set; }
        public bool EnableSnowOverlay2 { get; set; }
        public bool EnableSnowOverlay3 { get; set; }
        public bool EnableOverlayIntro { get; set; }
        public bool EnableCrossTransitions { get; set; }
        public string? CrossTransitionType { get; set; }
        public double CrossTransitionDuration { get; set; }
        public bool EnablePolaroidScrapbook { get; set; }
        public bool EnableRgbPolaroidScrapbook { get; set; }
        public bool EnableDashedPolaroidScrapbook { get; set; }
        public bool EnableBrandLogo { get; set; }
        public string? BrandLogoPosition { get; set; }
        public string? SnowfallPreset { get; set; }
        public string? SnowfallMode { get; set; }
        public double SnowfallOpacity { get; set; }
        public double SnowOverlay1Opacity { get; set; }
        public double SnowOverlay2Opacity { get; set; }
        public double SnowOverlay3Opacity { get; set; }
        public double OverlayIntroOpacity { get; set; }
        public string? RainOverlayPreset { get; set; }
        public string? RainOverlayIntensity { get; set; }
        public string? MusicSyncMode { get; set; }
        public double ImageTimelineDurationSeconds { get; set; }
        public double CropZoomPercent { get; set; }
        public string? CropAspectRatio { get; set; }
    }

    #endregion
}
