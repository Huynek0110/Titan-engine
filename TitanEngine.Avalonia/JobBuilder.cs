using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TitanEngine.Avalonia.ViewModels;
using TitanEngine.Core;

namespace TitanEngine.Avalonia;

/// <summary>
/// Giai đoạn 5 — dựng RenderJob từ trạng thái UI, mirror 1:1 BtnAddJob_Click (MainWindow:3625)
/// và EnqueueSingleRandomJobAsync (MainWindow:4149): cùng thứ tự resolve, cùng giá trị mặc định,
/// cùng log. Toàn bộ helper chuẩn hoá (ResolveFxEffectName, NormalizeTemplateName,
/// ResolveOverlayEffectName, ResolveSnowfallPresetName, TryParseFlexibleDouble,
/// NormalizeBrandLogoPosition, NormalizeCropZoomPercent, AutoOrientResolution…) gọi THẲNG từ Core
/// — đúng seam engine đang chạy.
/// </summary>
public static class JobBuilder
{
    // ── MainWindow: ResolveSelectedSnowfallOpacity / …SnowOverlayNOpacity / …OverlayIntroOpacity ──
    private static bool IsSnowOverlaySelection(string? overlayEffect, string? fxEffect)
        => string.Equals(overlayEffect, "Snowfall Overlay", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(fxEffect, "Snowfall Overlay", StringComparison.OrdinalIgnoreCase);

    private static double ResolveSelectedSnowfallOpacity(FxStudioViewModel fx, string overlayEffect, string fxEffect)
    {
        if (!IsSnowOverlaySelection(overlayEffect, fxEffect)) return -1.0;
        if (fx.SnowOverlay1 || fx.SnowOverlay2 || fx.SnowOverlay3 || fx.OverlayIntro) return -1.0;
        return Math.Clamp(55.0 / 100.0, 0.0, 1.0); // sldSnowfallOpacity mặc định 55 (khu bị tắt → dùng mặc định authored)
    }

    private static double ResolveSelectedSnowOverlay1Opacity(FxStudioViewModel fx)
        => fx.SnowOverlay1 ? Math.Clamp(55.0 / 100.0, 0.0, 1.0) : -1.0;
    private static double ResolveSelectedSnowOverlay2Opacity(FxStudioViewModel fx)
        => fx.SnowOverlay2 ? Math.Clamp(55.0 / 100.0, 0.0, 1.0) : -1.0;
    private static double ResolveSelectedSnowOverlay3Opacity(FxStudioViewModel fx)
        => fx.SnowOverlay3 ? Math.Clamp(55.0 / 100.0, 0.0, 1.0) : -1.0;
    private static double ResolveSelectedOverlayIntroOpacity(FxStudioViewModel fx)
        => fx.OverlayIntro ? Math.Clamp(100.0 / 100.0, 0.0, 1.0) : -1.0;

    private static string ResolveSelectedRainOverlayPreset(FxStudioViewModel fx, string overlayEffect)
        => !overlayEffect.Equals("Rain Overlay", StringComparison.OrdinalIgnoreCase)
            ? "Heavy Rain"
            : RainOverlayPreset.NormalizePreset(fx.RainPreset.Label);

    private static string ResolveSelectedRainOverlayIntensity(FxStudioViewModel fx, string overlayEffect)
        => !overlayEffect.Equals("Rain Overlay", StringComparison.OrdinalIgnoreCase)
            ? "medium"
            : RainOverlayPreset.NormalizeIntensity(fx.RainIntensity.Label);

    // ── MainWindow: ResolveSelectedPlaybackSpeed ──
    public static double ResolveSelectedPlaybackSpeed(SlowMotionViewModel sm)
    {
        string content = sm.Speed.Label;
        if (content.Contains("Custom", StringComparison.OrdinalIgnoreCase))
        {
            if (LocalhostRequestMapper.TryParseFlexibleDouble(sm.CustomSpeedText, out double customSpeed) && customSpeed > 0.0)
                return Math.Clamp(customSpeed, 0.10, 8.0);
            return 1.0;
        }
        if (UiLogic.TryExtractPlaybackSpeed(content, out double presetSpeed) && presetSpeed > 0.0)
            return Math.Clamp(presetSpeed, 0.10, 8.0);
        return 1.0;
    }

    // ── MainWindow: ResolveFadeSettingsFromUi ──
    private static (bool Enabled, double FadeIn, double FadeOut) ResolveFadeSettings(FxStudioViewModel fx)
    {
        double fadeIn = 0.8, fadeOut = 0.8;
        if (LocalhostRequestMapper.TryParseFlexibleDouble(fx.FadeInSeconds, out double parsedIn))
            fadeIn = parsedIn;
        if (LocalhostRequestMapper.TryParseFlexibleDouble(fx.FadeOutSeconds, out double parsedOut))
            fadeOut = parsedOut;
        return (fx.EnableFade, Math.Clamp(fadeIn, 0.0, 60.0), Math.Clamp(fadeOut, 0.0, 60.0));
    }

    /// <summary>
    /// Resolve độ phân giải (Custom/Original/preset) + upscale multiplier + auto-orient +
    /// crop 9:16 — phần chung của BtnAddJob_Click:3711-3797 và EnqueueSingleRandomJobAsync:4190-4242.
    /// </summary>
    private static async Task<(int W, int H, string ResolutionText)> ResolveTargetResolutionAsync(
        ExportViewModel export, UpscaleViewModel upscale, string vp, bool allImageSources, double cropZoomPercent)
    {
        string resolutionText = export.Res.Label;
        int targetW = -1, targetH = -1;
        bool isOriginalResolution = resolutionText.Contains("Original");

        double upscaleMultiplier = 1.0;
        if (upscale.UpscaleMode.Contains("2x") && !upscale.UpscaleMode.Contains("4x")) upscaleMultiplier = 2.0;
        else if (upscale.UpscaleMode.Contains("4x")) upscaleMultiplier = 4.0;

        if (resolutionText.Contains("Custom"))
        {
            if (int.TryParse(export.ResW.Trim(), out int w) && w > 0) targetW = w;
            if (int.TryParse(export.ResH.Trim(), out int h) && h > 0) targetH = h;
        }
        else if (resolutionText.Contains("Original"))
        {
            if (allImageSources && UiLogic.IsImageSourcePath(vp))
            {
                targetW = 1080;
                targetH = 1920;
                resolutionText = "1080x1920 (Portrait Image Default)";
            }
            else
            {
                var sourceRes = await EngineCore.GetVideoResolutionAsync(vp);
                targetW = sourceRes.Width;
                targetH = sourceRes.Height;
            }
        }
        else
        {
            Match match = Regex.Match(resolutionText, @"(\d+)x(\d+)");
            if (match.Success)
            {
                if (int.TryParse(match.Groups[1].Value, out int w)) targetW = w;
                if (int.TryParse(match.Groups[2].Value, out int h)) targetH = h;
            }
        }

        if (upscaleMultiplier > 1.0 && targetW > 0 && targetH > 0)
        {
            targetW = (int)(targetW * upscaleMultiplier);
            targetH = (int)(targetH * upscaleMultiplier);
            resolutionText = $"{targetW}x{targetH} ({upscaleMultiplier}x Upscaled)";
        }

        if (isOriginalResolution && upscaleMultiplier == 1.0 && targetW > 0 && targetH > 0 && targetH > targetW)
            (targetW, targetH) = EngineCore.AutoOrientResolution(targetW, targetH);

        if (cropZoomPercent > 0.001 && targetW > 0 && targetH > 0)
        {
            (targetW, targetH) = EngineCore.CoerceResolutionToPortraitNineBySixteen(targetW, targetH);
            resolutionText = $"{targetW}x{targetH} (Crop 9:16)";
        }

        return (targetW, targetH, resolutionText);
    }

    /// <summary>Dựng RenderJob cho 1 cặp (video, audio) — phần thân chung của 2 builder WPF.</summary>
    public static async Task<RenderJob> BuildJobAsync(
        MainWindowViewModel root,
        string vp,
        string? ap,
        string outName,
        bool mergeVideos,
        bool mergeAudios,
        List<string>? mergeInputPaths)
    {
        var st = root.SourceTrim;
        var fx = root.FxStudio;
        var sm = root.SlowMotion;
        var up = root.Upscale;
        var me = root.MotionEditor;
        var tv = root.TextVibe;
        var cs = root.CaptionSync;
        var am = root.AudioMixer;
        var wm = root.Watermark;
        var cg = root.ColorGrading;
        var ex = root.Export;

        string templateName = LocalhostRequestMapper.NormalizeTemplateName(fx.TemplateStyle.Label);
        string selectedBorderEffect = LocalhostRequestMapper.ResolveOverlayEffectName(fx.BorderType.Label);
        string selectedOverlayEffect = fx.GetSelectedOverlayEffect();
        bool enableSnowOverlay1 = fx.SnowOverlay1;
        bool enableSnowOverlay2 = fx.SnowOverlay2;
        bool enableSnowOverlay3 = fx.SnowOverlay3;
        bool enableCrossTransitions = st.EnableCrossTransitions;
        string crossTransitionType = st.CrossTransitionType.Label;
        double crossTransitionDuration = double.TryParse(st.CrossTransitionDuration, NumberStyles.Any, CultureInfo.InvariantCulture, out double durVal) ? durVal : 0.6;
        bool enablePolaroidScrapbook = selectedBorderEffect.Equals("Polaroid Scrapbook", StringComparison.OrdinalIgnoreCase);
        bool enableRgbPolaroidScrapbook = selectedBorderEffect.Equals("RGB Polaroid Border", StringComparison.OrdinalIgnoreCase);
        bool enableDashedPolaroidScrapbook = selectedBorderEffect.Equals("Dashed Polaroid Frame", StringComparison.OrdinalIgnoreCase);
        bool enableBrandLogo = fx.EnableBrandLogo;
        string brandLogoPosition = EngineCore.NormalizeBrandLogoPosition(fx.BrandLogoPosition.Label);
        double cropZoomPercent = EngineCore.NormalizeCropZoomPercent(fx.CropZoom);
        if (templateName.Equals("Polaroid Scrapbook", StringComparison.OrdinalIgnoreCase))
        {
            enablePolaroidScrapbook = true;
            templateName = "None";
        }
        bool templateEnabled = !templateName.Equals("None", StringComparison.OrdinalIgnoreCase);
        string rawFxSelection = fx.FxType.Label;
        string selectedFxEffect = LocalhostRequestMapper.ResolveFxEffectName(rawFxSelection);
        string snowfallPreset = LocalhostRequestMapper.ResolveSnowfallPresetName(rawFxSelection);
        bool lightLeakBurnEffect = selectedFxEffect.Equals("Light Leak Burn", StringComparison.OrdinalIgnoreCase);
        if (selectedOverlayEffect.Equals("None", StringComparison.OrdinalIgnoreCase) &&
            selectedFxEffect.Equals("Snowfall Overlay", StringComparison.OrdinalIgnoreCase))
        {
            selectedOverlayEffect = "Snowfall Overlay";
            selectedFxEffect = "None";
        }
        double selectedSnowfallOpacity = ResolveSelectedSnowfallOpacity(fx, selectedOverlayEffect, selectedFxEffect);
        double selectedSnowOverlay1Opacity = ResolveSelectedSnowOverlay1Opacity(fx);
        double selectedSnowOverlay2Opacity = ResolveSelectedSnowOverlay2Opacity(fx);
        double selectedSnowOverlay3Opacity = ResolveSelectedSnowOverlay3Opacity(fx);
        double selectedOverlayIntroOpacity = ResolveSelectedOverlayIntroOpacity(fx);
        string selectedRainOverlayPreset = ResolveSelectedRainOverlayPreset(fx, selectedOverlayEffect);
        string selectedRainOverlayIntensity = ResolveSelectedRainOverlayIntensity(fx, selectedOverlayEffect);

        // (mergeVideos đã được caller quyết — gồm auto-merge theo template/LightLeakBurn ở BtnAddJob_Click.)
        bool allImageSources = LocalhostRequestMapper.ShouldUsePortraitHdDefaultForImages(mergeInputPaths ?? new List<string> { vp });

        (int targetW, int targetH, string resolutionText) = await ResolveTargetResolutionAsync(ex, up, vp, allImageSources, cropZoomPercent);

        double selectedPlaybackSpeed = ResolveSelectedPlaybackSpeed(sm);
        var fadeSettings = ResolveFadeSettings(fx);
        double imageTimelineDurationSeconds = 0.0;
        if (LocalhostRequestMapper.TryParseFlexibleDouble(st.ImageTimelineDuration, out double parsedImageTimelineDuration))
            imageTimelineDurationSeconds = Math.Max(0.0, parsedImageTimelineDuration);

        var job = new RenderJob
        {
            Id = root.Engine.Jobs.Count + 1,
            SourcePath = vp,
            MergeVideos = mergeVideos,
            MergeInputPaths = mergeInputPaths ?? new List<string> { vp },
            MergeAudio = mergeAudios,
            MergeAudioPaths = mergeAudios
                ? (st.AudioPaths.Count > 0 ? new List<string>(st.AudioPaths) : new List<string>())
                : (string.IsNullOrWhiteSpace(ap) ? new List<string>() : new List<string> { ap }),
            AudioPath = ap,
            SecondaryAudioPath = st.SecondaryAudioPath,
            WatermarkPath = wm.WatermarkPath,
            OutputName = outName,
            HardwareProfile = ex.Hardware.Label,
            BitrateStrategy = ex.Bitrate.Label,
            Resolution = resolutionText,
            TargetWidth = targetW,
            TargetHeight = targetH,
            Framerate = ex.Fps.Label,
            CustomBitrate = ex.CustomBitrate.Trim(),
            VideoTrimStart = st.TrimStart.Trim(),
            VideoTrimDuration = st.TrimDuration.Trim(),
            SplitDurationSeconds = double.TryParse(st.SplitDuration.Trim(), out double splitSec) ? splitSec : 0.0,
            ImageTimelineDurationSeconds = imageTimelineDurationSeconds,
            WatermarkScale = wm.Scale > 0.001 ? wm.Scale : 0.20,
            WatermarkRotation = wm.Rotation,
            WatermarkOpacity = wm.Opacity,
            WatermarkXPercent = wm.XPercent,
            WatermarkYPercent = wm.YPercent,
            WatermarkAspectRatio = wm.AspectRatio,
            FxEffect = selectedFxEffect,
            OverlayEffect = selectedOverlayEffect,
            BorderEffect = selectedBorderEffect,
            EnableSnowOverlay1 = enableSnowOverlay1,
            EnableSnowOverlay2 = enableSnowOverlay2,
            EnableSnowOverlay3 = enableSnowOverlay3,
            EnableCrossTransitions = enableCrossTransitions,
            CrossTransitionType = crossTransitionType,
            CrossTransitionDuration = crossTransitionDuration,
            TemplateName = templateName,
            EnablePolaroidScrapbook = enablePolaroidScrapbook,
            EnableRgbPolaroidScrapbook = enableRgbPolaroidScrapbook,
            EnableDashedPolaroidScrapbook = enableDashedPolaroidScrapbook,
            EnableBrandLogo = enableBrandLogo,
            BrandLogoPosition = brandLogoPosition,
            SnowfallPreset = snowfallPreset,
            SnowfallMode = "auto",
            SnowfallAssetPath = null,
            SnowfallOpacity = selectedSnowfallOpacity,
            SnowOverlay1Opacity = selectedSnowOverlay1Opacity,
            SnowOverlay2Opacity = selectedSnowOverlay2Opacity,
            SnowOverlay3Opacity = selectedSnowOverlay3Opacity,
            OverlayIntroOpacity = selectedOverlayIntroOpacity,
            RainOverlayPreset = selectedRainOverlayPreset,
            RainOverlayIntensity = selectedRainOverlayIntensity,
            RainOverlayAssetPath = null,
            MusicSyncMode = templateName.Equals("Beat Photo Dump", StringComparison.OrdinalIgnoreCase) ? "beat" : "off",
            MagazineCoverTitle = fx.MagazineCoverTitle.Trim(),
            MagazineCoverSubtitle = fx.MagazineCoverSubtitle.Trim(),
            LightLeakBurnIntensity = fx.FxIntensity,
            LightLeakBurnSpread = 55.0,
            LightLeakBurnWarmth = 72.0,
            LightLeakBurnBurn = 60.0,
            LightLeakBurnEdgeSoftness = 75.0,
            LightLeakBurnGrain = 35.0,
            LightLeakBurnDirection = "from-left",
            LightLeakBurnDuration = 0.95,
            LightLeakBurnBlendMode = "screen",
            LightLeakBurnOpacity = 0.82,
            LightLeakBurnAssetPath = null,
            LightLeakBurnUseAssetOverlay = false,
            LightLeakOverlayMode = "auto",
            LightLeakOverlayId = null,
            LightLeakOverlayAssetPath = null,
            LightLeakOverlayOpacity = -1.0,
            FxIntensity = fx.FxIntensity,
            FxStartPercent = fx.FxStart,
            FxEndPercent = fx.FxEnd,
            VideoBrightness = cg.Brightness,
            EnableFade = fadeSettings.Enabled,
            FadeInSeconds = fadeSettings.FadeIn,
            FadeOutSeconds = fadeSettings.FadeOut,
            CropEnabled = selectedFxEffect.Contains("Crop", StringComparison.OrdinalIgnoreCase) || cropZoomPercent > 0.001,
            CropZoomPercent = cropZoomPercent,
            CropAspectRatio = cropZoomPercent > 0.001 ? "9:16" : "Free",
            CropXPercent = st.CropXPercent,
            CropYPercent = st.CropYPercent,
            CropWidthPercent = st.CropWidthPercent,
            CropHeightPercent = st.CropHeightPercent,
            ColorFilter = cg.Filter.Label,
            ColorIntensity = cg.Intensity / 100.0,
            StatusDisplay = "WAITING",
            StatusColor = "Cyan",
            VideoVolume = am.VideoVolume / 100.0,
            AudioVolume = am.AudioVolume / 100.0,
            Audio1Effect = am.Audio1Fx.Label,
            Audio1EffectIntensity = (int)am.Audio1FxIntensity,
            SecondaryAudioVolume = am.Audio2Volume / 100.0,
            SourceAudioSpeed = sm.SourceAudioSpeed,
            ExternalAudioSpeed = st.ExternalAudioSpeed,
            UpscaleMode = up.UpscaleMode,
            SharpnessIntensity = up.Sharpness,
            SlowMotionSpeed = selectedPlaybackSpeed,
            SlowMotionAudio = sm.AudioStretch,
            Enable60fps = me.Enable60fps,
            RsmbIntensity = me.Rsmb,
            TextOverlay = tv.Enable ? tv.Text.Trim() : null,
            TextOverlayPosition = tv.Position.Label,
            TextAlign = tv.Align.Label,
            TextOverlayFontSize = (int)tv.FontSize,
            TextOverlayColor = "white",
            TextOverlayStyle = tv.Style.Label,
            AddQuotes = tv.AddQuotes,
            DisableTextScroll = tv.DisableScroll,
            TextOverlayXPercent = double.TryParse(tv.XPercent, NumberStyles.Any, CultureInfo.InvariantCulture, out double tx) ? tx : 50.0,
            TextOverlayYPercent = double.TryParse(tv.YPercent, NumberStyles.Any, CultureInfo.InvariantCulture, out double ty) ? ty : 85.0,
            EnableTextGlow = tv.EnableGlow,
            TextGlowColor = tv.GlowColor.Label,
            TextOverlayAnimation = tv.Animation.Label,
            TextOverlayEffectDuration = double.TryParse(tv.EffectDuration, NumberStyles.Any, CultureInfo.InvariantCulture, out double te) ? te : 5.0,
            EnableCaptionSync = cs.Enable,
            CaptionSyncLanguage = cs.Lang.Label,
            CaptionSyncModelSize = cs.Model.Label,
            CaptionSyncAnimation = cs.Animation.Label,
            CaptionSyncFontFamily = cs.Font.Label,
            CaptionSyncPosition = cs.Position.Label,
            CaptionSyncXPercent = double.TryParse(cs.XPercent, NumberStyles.Any, CultureInfo.InvariantCulture, out double cx) ? cx : 50.0,
            CaptionSyncYPercent = double.TryParse(cs.YPercent, NumberStyles.Any, CultureInfo.InvariantCulture, out double cy) ? cy : 85.0,
            CaptionSyncFontSize = (int)cs.FontSize,
            CaptionSyncColor = "white"
        };

        root.Soundbar.ApplyToJob(job);

        // ── Metadata (BtnAddJob_Click:3945-3999) ──
        if (job.MergeVideos && job.MergeInputPaths.Count > 1)
        {
            string referenceInput = job.MergeInputPaths.FirstOrDefault(p => !UiLogic.IsImageSourcePath(p)) ?? job.MergeInputPaths[0];
            var firstMeta = await EngineCore.AnalyzeMediaDetailsAsync(referenceInput);
            double totalDuration = 0.0;
            long maxBitrate = Math.Max(0L, firstMeta.Bitrate);
            int imageCount = job.MergeInputPaths.Count(UiLogic.IsImageSourcePath);
            double imageSegmentDuration = EngineCore.ResolveImageTimelineSegmentDuration(job.ImageTimelineDurationSeconds, imageCount);
            foreach (string mergeInput in job.MergeInputPaths)
            {
                if (UiLogic.IsImageSourcePath(mergeInput))
                {
                    totalDuration += imageSegmentDuration;
                    continue;
                }
                var mergeMeta = await EngineCore.AnalyzeMediaDetailsAsync(mergeInput);
                totalDuration += Math.Max(0.0, mergeMeta.Duration);
                if (mergeMeta.Bitrate > maxBitrate)
                    maxBitrate = mergeMeta.Bitrate;
            }

            job.DurationSec = totalDuration > 0.0 ? totalDuration : firstMeta.Duration;
            job.SourceBitrate = Math.Max(Math.Max(firstMeta.Bitrate, maxBitrate), 5000000);
            job.SourceWidth = firstMeta.Width > 0 ? firstMeta.Width : 1920;
            job.SourceHeight = firstMeta.Height > 0 ? firstMeta.Height : 1080;
            job.SourceFps = firstMeta.Fps > 0.1 ? firstMeta.Fps : 30.0;
        }
        else
        {
            var meta = await EngineCore.AnalyzeMediaDetailsAsync(job.SourcePath);
            if (UiLogic.IsImageSourcePath(job.SourcePath))
            {
                job.DurationSec = EngineCore.ResolveImageTimelineSegmentDuration(job.ImageTimelineDurationSeconds, 1);
                job.SourceBitrate = Math.Max(meta.Bitrate, 5000000);
                job.SourceWidth = meta.Width > 0 ? meta.Width : (job.TargetWidth > 0 ? job.TargetWidth : 1920);
                job.SourceHeight = meta.Height > 0 ? meta.Height : (job.TargetHeight > 0 ? job.TargetHeight : 1080);
                job.SourceFps = meta.Fps > 0.1 ? meta.Fps : 30.0;
            }
            else
            {
                job.DurationSec = meta.Duration;
                job.SourceBitrate = meta.Bitrate;
                job.SourceWidth = meta.Width;
                job.SourceHeight = meta.Height;
                job.SourceFps = meta.Fps;
            }
        }

        job.AudioSegments = AudioEditHelpers.NormalizeSegments(st.AudioEditSegments, job.DurationSec > 0.0 ? job.DurationSec : null);
        return job;
    }

    /// <summary>Out name — mirror BtnAddJob_Click:3705-3709.</summary>
    public static string BuildOutputName(string vp, string overrideName, int index, int count, bool mergeVideos)
    {
        string outName = string.IsNullOrWhiteSpace(overrideName)
            ? Path.GetFileNameWithoutExtension(vp) + "_Titan"
            : overrideName;
        if (!mergeVideos && count > 1) outName += $"_{(index + 1):D2}";
        return outName;
    }
}
