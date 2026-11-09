using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TitanEngine
{
    /// <summary>
    /// Giai đoạn 3: mapper LocalhostRenderRequest -> RenderJob, chuyển nguyên văn từ
    /// HandleLocalApiRequestAsync (MainWindow) để CLI `render job.json` / `host` đi đúng
    /// cùng một đường mapping như HTTP API của WPF. Fail validate -> LocalhostRequestException
    /// (handler WPF bắt và trả đúng status/message như cũ).
    /// </summary>
    public sealed class LocalhostRequestException : Exception
    {
        public int StatusCode { get; }
        public LocalhostRequestException(int statusCode, string message) : base(message)
            => StatusCode = statusCode;
    }

    /// <summary>Kết quả mapping: job + các biến local handler gốc cần cho phần log/response.</summary>
    public sealed class LocalhostRequestMapping
    {
        public RenderJob Job { get; init; } = null!;
        public string InputPath { get; init; } = string.Empty;
        public string OutputPath { get; init; } = string.Empty;
        public string OutputDir { get; init; } = string.Empty;
        public string ColorFilter { get; init; } = string.Empty;
        public string FxEffect { get; init; } = string.Empty;
        public string OverlayEffect { get; init; } = string.Empty;
        public string TemplateName { get; init; } = string.Empty;
        public bool CropEnabled { get; init; }
        public string UpscaleMode { get; init; } = string.Empty;
    }

    public static class LocalhostRequestMapper
    {
        /// <summary>Map request -> (job, inputPath, outputPath, outputDir). Logic nguyên văn từ handler gốc.</summary>
        public static async Task<LocalhostRequestMapping> MapAsync(
            LocalhostRenderRequest request, Action<string> onLog)
        {
            var requestedInputFiles = new List<string>();
            if (request.InputFiles != null && request.InputFiles.Count > 0)
                requestedInputFiles.AddRange(request.InputFiles);

            List<string>? featureInputFiles = GetFeatureStringList(request,
                "inputFiles", "inputfiles", "videos", "videoFiles");
            if (featureInputFiles != null && featureInputFiles.Count > 0)
                requestedInputFiles.AddRange(featureInputFiles);

            string? singleInput = FirstNonEmpty(
                request.InputFile,
                GetFeatureString(request, "inputFile", "inputfile", "sourcePath", "source", "videoPath", "video")
            );
            if (!string.IsNullOrWhiteSpace(singleInput))
                requestedInputFiles.Add(singleInput);

            List<string> inputPaths = requestedInputFiles
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => Path.GetFullPath(x.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool allImageInputs = ShouldUsePortraitHdDefaultForImages(inputPaths);

            if (inputPaths.Count == 0)
            {
                throw new LocalhostRequestException(400, "inputFile/inputFiles is missing");
            }

            foreach (string inputCandidate in inputPaths)
            {
                if (!File.Exists(inputCandidate))
                {
                    throw new LocalhostRequestException(400, $"Input file does not exist: {inputCandidate}");
                }
            }

            bool mergeVideosRequested = request.MergeVideos ??
                GetFeatureBool(request, "mergeVideos", "merge", "concat", "joinVideos") ??
                false;
            if (inputPaths.Count > 1 && !mergeVideosRequested)
            {
                // If caller sends multiple files, default to merge workflow.
                mergeVideosRequested = true;
            }

            string inputPath = inputPaths[0];

            string outputPath = (FirstNonEmpty(
                request.OutputVideo,
                GetFeatureString(request, "outputVideo", "outputvideo", "outputPath", "output", "saveTo")
            ) ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new LocalhostRequestException(400, "outputVideo is required");
            }

            outputPath = Path.GetFullPath(outputPath);
            string outputDir = Path.GetDirectoryName(outputPath) ?? string.Empty;
            string outputName = Path.GetFileNameWithoutExtension(outputPath);

            if (string.IsNullOrWhiteSpace(outputDir) || string.IsNullOrWhiteSpace(outputName))
            {
                throw new LocalhostRequestException(400, "outputVideo path is invalid");
            }

            Directory.CreateDirectory(outputDir);

            var requestedAudioFiles = new List<string>();
            if (request.AudioFiles != null && request.AudioFiles.Count > 0)
                requestedAudioFiles.AddRange(request.AudioFiles);

            List<string>? featureAudioFiles = GetFeatureStringList(request,
                "audioFiles", "audiofiles", "musicFiles", "musicfiles");
            if (featureAudioFiles != null && featureAudioFiles.Count > 0)
                requestedAudioFiles.AddRange(featureAudioFiles);

            string? singleAudioInput = FirstNonEmpty(
                request.AudioFile,
                GetFeatureString(request, "audioFile", "audiopath", "audio", "musicFile")
            );
            if (!string.IsNullOrWhiteSpace(singleAudioInput))
                requestedAudioFiles.Add(singleAudioInput);

            string? secondaryAudioPath = FirstNonEmpty(
                request.AudioFile2,
                GetFeatureString(request, "audioFile2", "audio2", "musicFile2", "secondaryAudioFile", "overlayAudioFile")
            );
            if (!string.IsNullOrWhiteSpace(secondaryAudioPath))
            {
                secondaryAudioPath = Path.GetFullPath(secondaryAudioPath.Trim());
                if (!File.Exists(secondaryAudioPath))
                {
                    throw new LocalhostRequestException(400, $"audioFile2 does not exist: {secondaryAudioPath}");
                }
            }

            List<string> audioPaths = requestedAudioFiles
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => Path.GetFullPath(x.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string audioCandidate in audioPaths)
            {
                if (!File.Exists(audioCandidate))
                {
                    throw new LocalhostRequestException(400, $"audioFile does not exist: {audioCandidate}");
                }
            }

            bool mergeAudioRequested = request.MergeAudio ??
                GetFeatureBool(request, "mergeAudio", "mergeAudios", "audioMerge", "concatAudio", "joinAudio") ??
                false;
            if (audioPaths.Count > 1 && !mergeAudioRequested)
            {
                // If caller sends multiple audio files, default to merge audio workflow.
                mergeAudioRequested = true;
            }

            string? audioPath = audioPaths.Count > 0 ? audioPaths[0] : null;

            string? watermarkPath = FirstNonEmpty(
                request.WatermarkFile,
                GetFeatureString(request, "watermarkFile", "watermarkPath", "watermark", "logo")
            );

            if (!string.IsNullOrWhiteSpace(watermarkPath))
            {
                watermarkPath = Path.GetFullPath(watermarkPath.Trim());
                if (!File.Exists(watermarkPath))
                {
                    throw new LocalhostRequestException(400, "watermarkFile does not exist");
                }
            }

            string colorFilter = ResolveColorFilterFromRequest(request);
            double colorIntensity = Math.Clamp(
                request.ColorIntensity ??
                GetFeatureDouble(request, "colorIntensity", "color_intensity", "colorPower", "color_power") ??
                1.0,
                0.0, 1.0
            );

            string? rawFxEffectInput = FirstNonEmpty(
                request.FxEffect,
                GetFeatureString(request, "fxEffect", "effect", "fx")
            );
            string fxEffect = ResolveFxEffectName(rawFxEffectInput);

            string overlayEffect = ResolveOverlayEffectName(FirstNonEmpty(
                request.Overlay,
                request.OverlayEffect,
                GetFeatureString(request, "overlay", "overlayEffect", "overlayPreset", "overlayType", "overlayName")
            ));
            bool enableSnowOverlay1 = request.EnableSnowOverlay1 ??
                GetFeatureBool(request, "enableSnowOverlay1", "snowOverlay1", "overlay1", "overlayOne") ??
                false;
            bool enableSnowOverlay2 = request.EnableSnowOverlay2 ??
                GetFeatureBool(request, "enableSnowOverlay2", "snowOverlay2", "overlay2", "overlayTwo") ??
                false;
            bool enableSnowOverlay3 = request.EnableSnowOverlay3 ??
                GetFeatureBool(request, "enableSnowOverlay3", "snowOverlay3", "overlay3", "overlayThree") ??
                false;
            bool enableOverlayIntro = request.EnableOverlayIntro ??
                GetFeatureBool(request, "enableOverlayIntro", "overlayIntro", "introOverlay") ??
                false;
            bool enableCrossTransitions = request.EnableCrossTransitions ?? GetFeatureBool(request, "enableCrossTransitions", "crossTransitions", "enableTransitions", "transitions") ?? false;
            string crossTransitionType = FirstNonEmpty(request.CrossTransitionType, GetFeatureString(request, "crossTransitionType", "transitionType", "transitionPreset", "transitionName"), "Dissolve") ?? "Dissolve";
            double crossTransitionDuration = request.CrossTransitionDuration ?? GetFeatureDouble(request, "crossTransitionDuration", "transitionDuration", "transitionSec") ?? 0.6;
            string borderEffect = ResolveOverlayEffectName(FirstNonEmpty(
                request.Border,
                request.BorderEffect,
                GetFeatureString(request, "border", "borderEffect", "borderPreset", "borderType", "borderName")
            ));
            string snowfallPreset = ResolveSnowfallPresetName(FirstNonEmpty(
                request.SnowfallPreset,
                rawFxEffectInput,
                GetFeatureString(request, "snowfallPreset", "snowPreset", "snowProfile", "snowModePreset")
            ));
            string snowfallMode = SnowfallOverlayPreset.NormalizeMode(FirstNonEmpty(
                request.SnowfallMode,
                GetFeatureString(request, "snowfallMode", "snowOverlayMode", "snowRenderMode")
            ));
            string? snowfallAssetPath = FirstNonEmpty(
                request.SnowfallAssetPath,
                GetFeatureString(request, "snowfallAssetPath", "snowOverlayAssetPath", "snowAssetPath")
            );
            double snowfallOpacity = NormalizeOptionalOpacity(
                request.SnowfallOpacity ??
                GetFeatureDouble(
                    request,
                    "snowfallOpacity",
                    "snowOverlayOpacity",
                    "snowOpacity",
                    "snowfallOpacityPercent",
                    "snowOverlayOpacityPercent",
                    "snowOpacityPercent") ??
                -1.0);
            double snowOverlay1Opacity = NormalizeOptionalOpacity(
                request.SnowOverlay1Opacity ??
                GetFeatureDouble(
                    request,
                    "snowOverlay1Opacity",
                    "snow1Opacity",
                    "overlay1Opacity",
                    "snowOverlay1OpacityPercent",
                    "snow1OpacityPercent",
                    "overlay1OpacityPercent") ??
                snowfallOpacity);
            double snowOverlay2Opacity = NormalizeOptionalOpacity(
                request.SnowOverlay2Opacity ??
                GetFeatureDouble(
                    request,
                    "snowOverlay2Opacity",
                    "snow2Opacity",
                    "overlay2Opacity",
                    "snowOverlay2OpacityPercent",
                    "snow2OpacityPercent",
                    "overlay2OpacityPercent") ??
                snowfallOpacity);
            double snowOverlay3Opacity = NormalizeOptionalOpacity(
                request.SnowOverlay3Opacity ??
                GetFeatureDouble(
                    request,
                    "snowOverlay3Opacity",
                    "snow3Opacity",
                    "overlay3Opacity",
                    "snowOverlay3OpacityPercent",
                    "snow3OpacityPercent",
                    "overlay3OpacityPercent") ??
                snowfallOpacity);
            double overlayIntroOpacity = NormalizeOptionalOpacity(
                request.OverlayIntroOpacity ??
                GetFeatureDouble(
                    request,
                    "overlayIntroOpacity",
                    "introOpacity",
                    "overlayIntroOpacityPercent",
                    "introOpacityPercent") ??
                snowfallOpacity);
            string rainOverlayPreset = ResolveRainOverlayPresetName(FirstNonEmpty(
                request.RainOverlayPreset,
                GetFeatureString(request, "rainOverlayPreset", "rainPreset", "overlayPreset", "rainProfile")
            ));
            string rainOverlayIntensity = ResolveRainOverlayIntensityName(FirstNonEmpty(
                request.RainOverlayIntensity,
                GetFeatureString(request, "rainOverlayIntensity", "rainIntensity", "overlayIntensity")
            ));
            string? rainOverlayAssetPath = FirstNonEmpty(
                request.RainOverlayAssetPath,
                GetFeatureString(request, "rainOverlayAssetPath", "rainAssetPath", "overlayAssetPath")
            );

            string templateName = NormalizeTemplateName(FirstNonEmpty(
                request.TemplateName,
                GetFeatureString(request, "templateName", "template", "presetTemplate")
            ));
            bool enablePolaroidScrapbook = request.EnablePolaroidScrapbook ??
                GetFeatureBool(request, "enablePolaroidScrapbook", "polaroidScrapbook", "usePolaroidScrapbook", "polaroidFrame") ??
                false;
            bool enableRgbPolaroidScrapbook = request.EnableRgbPolaroidScrapbook ??
                GetFeatureBool(request, "enableRgbPolaroidScrapbook", "rgbPolaroidScrapbook", "enableRgbPolaroidBorder", "polaroidRgbBorder", "rainbowPolaroid") ??
                false;
            bool enableDashedPolaroidScrapbook = request.EnableDashedPolaroidScrapbook ??
                GetFeatureBool(request, "enableDashedPolaroidScrapbook", "dashedPolaroidScrapbook", "enableDashedPolaroidFrame", "polaroidDashedFrame", "roundedDashedPolaroid") ??
                false;

            bool overlayContainsLegacyBorder =
                overlayEffect.Equals("Polaroid Scrapbook", StringComparison.OrdinalIgnoreCase) ||
                overlayEffect.Equals("Polaroid Scrapbook 2", StringComparison.OrdinalIgnoreCase) ||
                overlayEffect.Equals("RGB Polaroid Border", StringComparison.OrdinalIgnoreCase) ||
                overlayEffect.Equals("Dashed Polaroid Frame", StringComparison.OrdinalIgnoreCase);
            if (borderEffect.Equals("None", StringComparison.OrdinalIgnoreCase) && overlayContainsLegacyBorder)
            {
                borderEffect = overlayEffect;
                overlayEffect = "None";
            }

            enablePolaroidScrapbook |= borderEffect.Equals("Polaroid Scrapbook", StringComparison.OrdinalIgnoreCase);
            enableRgbPolaroidScrapbook |= borderEffect.Equals("RGB Polaroid Border", StringComparison.OrdinalIgnoreCase);
            enableDashedPolaroidScrapbook |= borderEffect.Equals("Dashed Polaroid Frame", StringComparison.OrdinalIgnoreCase);
            bool enableBrandLogo = request.EnableBrandLogo ??
                GetFeatureBool(request, "enableBrandLogo", "brandLogo", "videoLogo", "useBrandLogo", "useVideoLogo") ??
                false;
            string brandLogoPosition = EngineCore.NormalizeBrandLogoPosition(FirstNonEmpty(
                request.BrandLogoPosition,
                GetFeatureString(request, "brandLogoPosition", "logoPosition", "videoLogoPosition", "titanLogoPosition")
            ));

            if (overlayEffect.Equals("Polaroid Scrapbook", StringComparison.OrdinalIgnoreCase))
                enablePolaroidScrapbook = true;
            else if (overlayEffect.Equals("RGB Polaroid Border", StringComparison.OrdinalIgnoreCase))
                enableRgbPolaroidScrapbook = true;
            else if (overlayEffect.Equals("Dashed Polaroid Frame", StringComparison.OrdinalIgnoreCase))
                enableDashedPolaroidScrapbook = true;
            if (overlayEffect.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                fxEffect.Equals("Snowfall Overlay", StringComparison.OrdinalIgnoreCase))
            {
                overlayEffect = "Snowfall Overlay";
                fxEffect = "None";
            }
            if (enableSnowOverlay1 || enableSnowOverlay2 || enableSnowOverlay3 || enableOverlayIntro)
            {
                overlayEffect = "Snowfall Overlay";
            }
            if (overlayEffect.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                fxEffect.Equals("Dust & Scratches", StringComparison.OrdinalIgnoreCase))
            {
                overlayEffect = "Dust & Scratches";
                fxEffect = "None";
            }

            if (templateName.Equals("Polaroid Scrapbook", StringComparison.OrdinalIgnoreCase))
            {
                enablePolaroidScrapbook = true;
                templateName = "None";
            }
            string? rawMusicSyncMode = FirstNonEmpty(
                request.MusicSyncMode,
                GetFeatureString(request, "musicSyncMode", "musicSync", "syncMode")
            );
            string musicSyncMode = string.IsNullOrWhiteSpace(rawMusicSyncMode) &&
                templateName.Equals("Beat Photo Dump", StringComparison.OrdinalIgnoreCase)
                ? "beat"
                : NormalizeMusicSyncModeName(rawMusicSyncMode);
            string? rawMagazineCoverTitle = FirstNonEmpty(
                request.MagazineCoverTitle,
                GetFeatureString(request, "magazineCoverTitle", "magazineTitle", "coverTitle")
            );
            string? rawMagazineCoverSubtitle = FirstNonEmpty(
                request.MagazineCoverSubtitle,
                GetFeatureString(request, "magazineCoverSubtitle", "magazineSubtitle", "coverSubtitle")
            );
            string? rawLightLeakBurnDirection = FirstNonEmpty(
                request.LightLeakBurnDirection,
                GetFeatureString(request, "lightLeakBurnDirection", "leakBurnDirection", "transitionDirection", "direction")
            );
            string? rawLightLeakBurnBlendMode = FirstNonEmpty(
                request.LightLeakBurnBlendMode,
                GetFeatureString(request, "lightLeakBurnBlendMode", "leakBurnBlendMode", "blendMode")
            );
            string? rawLightLeakBurnAssetPath = FirstNonEmpty(
                request.LightLeakBurnAssetPath,
                GetFeatureString(request, "lightLeakBurnAssetPath", "leakBurnAssetPath", "transitionAsset", "overlayAsset")
            );
            string lightLeakOverlayMode = LightLeakOverlayPreset.NormalizeMode(FirstNonEmpty(
                request.LightLeakOverlayMode,
                GetFeatureString(request, "lightLeakOverlayMode", "overlayLightLeakMode", "lightLeakMode", "overlayMode")
            ));
            string? lightLeakOverlayId = FirstNonEmpty(
                request.LightLeakOverlayId,
                GetFeatureString(request, "lightLeakOverlayId", "overlayLightLeakId", "lightLeakAssetId", "overlayAssetId")
            );
            string? lightLeakOverlayAssetPath = FirstNonEmpty(
                request.LightLeakOverlayAssetPath,
                GetFeatureString(request, "lightLeakOverlayAssetPath", "overlayLightLeakAssetPath", "lightLeakAssetPath")
            );
            bool forceTemplateMerge = !templateName.Equals("None", StringComparison.OrdinalIgnoreCase) && inputPaths.Count > 1;
            bool forceTransitionMerge = fxEffect.Equals("Light Leak Burn", StringComparison.OrdinalIgnoreCase) && inputPaths.Count > 1;
            bool forceMerge = forceTemplateMerge || forceTransitionMerge;

            double fxIntensity = Math.Clamp(
                request.FxIntensity ??
                GetFeatureDouble(request, "fxIntensity", "fx_intensity", "effectIntensity") ??
                50.0,
                0.0, 100.0
            );

            double fxStart = Math.Clamp(
                request.FxStartPercent ??
                GetFeatureDouble(request, "fxStartPercent", "fxStart", "effectStart") ??
                0.0,
                0.0, 100.0
            );

            double lightLeakBurnIntensity = Math.Clamp(
                request.LightLeakBurnIntensity ??
                GetFeatureDouble(request, "lightLeakBurnIntensity", "leakBurnIntensity", "transitionIntensity") ??
                fxIntensity,
                0.0, 100.0
            );

            double lightLeakBurnSpread = Math.Clamp(
                request.LightLeakBurnSpread ??
                GetFeatureDouble(request, "lightLeakBurnSpread", "leakBurnSpread", "spread") ??
                55.0,
                0.0, 100.0
            );

            double lightLeakBurnWarmth = Math.Clamp(
                request.LightLeakBurnWarmth ??
                GetFeatureDouble(request, "lightLeakBurnWarmth", "leakBurnWarmth", "warmth") ??
                72.0,
                0.0, 100.0
            );

            double lightLeakBurnBurn = Math.Clamp(
                request.LightLeakBurnBurn ??
                GetFeatureDouble(request, "lightLeakBurnBurn", "leakBurnBurn", "burn") ??
                60.0,
                0.0, 100.0
            );

            double lightLeakBurnEdgeSoftness = Math.Clamp(
                request.LightLeakBurnEdgeSoftness ??
                GetFeatureDouble(request, "lightLeakBurnEdgeSoftness", "leakBurnEdgeSoftness", "edgeSoftness") ??
                75.0,
                0.0, 100.0
            );

            double lightLeakBurnGrain = Math.Clamp(
                request.LightLeakBurnGrain ??
                GetFeatureDouble(request, "lightLeakBurnGrain", "leakBurnGrain", "grain") ??
                35.0,
                0.0, 100.0
            );

            double lightLeakBurnDuration = Math.Max(0.10,
                request.LightLeakBurnDuration ??
                GetFeatureDouble(request, "lightLeakBurnDuration", "leakBurnDuration", "transitionDuration") ??
                0.95);

            double lightLeakBurnOpacity = Math.Clamp(
                request.LightLeakBurnOpacity ??
                GetFeatureDouble(request, "lightLeakBurnOpacity", "leakBurnOpacity", "transitionOpacity") ??
                0.82,
                0.0, 1.0
            );

            string lightLeakBurnDirection = LightLeakBurnPreset.NormalizeDirection(rawLightLeakBurnDirection);
            string lightLeakBurnBlendMode = LightLeakBurnPreset.NormalizeBlendMode(rawLightLeakBurnBlendMode);
            string? lightLeakBurnAssetPath = rawLightLeakBurnAssetPath;
            bool lightLeakBurnUseAssetOverlay = (request.LightLeakBurnUseAssetOverlay ??
                GetFeatureBool(request, "lightLeakBurnUseAssetOverlay", "leakBurnUseAssetOverlay", "useAssetOverlay") ??
                false) && !string.IsNullOrWhiteSpace(lightLeakBurnAssetPath);
            double lightLeakOverlayOpacity = request.LightLeakOverlayOpacity ??
                GetFeatureDouble(request, "lightLeakOverlayOpacity", "overlayLightLeakOpacity", "lightLeakOpacity") ??
                -1.0;
            if (lightLeakOverlayOpacity >= 0.0)
                lightLeakOverlayOpacity = Math.Clamp(lightLeakOverlayOpacity, 0.0, 1.0);

            double fxEnd = Math.Clamp(
                request.FxEndPercent ??
                GetFeatureDouble(request, "fxEndPercent", "fxEnd", "effectEnd") ??
                100.0,
                0.0, 100.0
            );

            if (fxEnd < fxStart)
            {
                (fxStart, fxEnd) = (fxEnd, fxStart);
            }

            double cropX = Math.Clamp(
                request.CropXPercent ??
                GetFeatureDouble(request, "cropXPercent", "cropX", "crop_left_percent") ??
                0.0,
                0.0, 0.98
            );

            double cropY = Math.Clamp(
                request.CropYPercent ??
                GetFeatureDouble(request, "cropYPercent", "cropY", "crop_top_percent") ??
                0.0,
                0.0, 0.98
            );

            double cropWidth = Math.Clamp(
                request.CropWidthPercent ??
                GetFeatureDouble(request, "cropWidthPercent", "cropW", "crop_width_percent") ??
                1.0,
                0.02, 1.0
            );

            double cropHeight = Math.Clamp(
                request.CropHeightPercent ??
                GetFeatureDouble(request, "cropHeightPercent", "cropH", "crop_height_percent") ??
                1.0,
                0.02, 1.0
            );

            double cropZoomPercent = EngineCore.NormalizeCropZoomPercent(
                request.CropZoomPercent ??
                GetFeatureDouble(request, "cropZoomPercent", "cropPercent", "cropZoom", "zoomCropPercent") ??
                0.0
            );

            string cropAspectRatio = EngineCore.NormalizeCropAspectRatioName(FirstNonEmpty(
                request.CropAspectRatio,
                GetFeatureString(request, "cropAspectRatio", "cropRatio", "aspectRatio", "cropPreset")
            ));

            bool hasCropValues =
                request.CropXPercent.HasValue ||
                request.CropYPercent.HasValue ||
                request.CropWidthPercent.HasValue ||
                request.CropHeightPercent.HasValue ||
                GetFeatureDouble(request, "cropXPercent", "cropX", "crop_left_percent").HasValue ||
                GetFeatureDouble(request, "cropYPercent", "cropY", "crop_top_percent").HasValue ||
                GetFeatureDouble(request, "cropWidthPercent", "cropW", "crop_width_percent").HasValue ||
                GetFeatureDouble(request, "cropHeightPercent", "cropH", "crop_height_percent").HasValue;

            bool cropEnabled = (request.FxEffect?.Contains("crop", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (GetFeatureBool(request, "cropEnabled", "enableCrop") ?? false) ||
                cropZoomPercent > 0.001 ||
                hasCropValues ||
                !cropAspectRatio.Equals("Free", StringComparison.OrdinalIgnoreCase);

            int targetWidth = request.TargetWidth ??
                GetFeatureInt(request, "targetWidth", "targetW", "width") ??
                -1;

            int targetHeight = request.TargetHeight ??
                GetFeatureInt(request, "targetHeight", "targetH", "height") ??
                -1;

            string rawResolution = FirstNonEmpty(
                request.Resolution,
                GetFeatureString(request, "resolution", "res")
            ) ?? "Original";

            string resolution = NormalizeResolutionName(rawResolution);
            if ((targetWidth <= 0 || targetHeight <= 0) && TryExtractResolution(rawResolution, out int rw, out int rh))
            {
                targetWidth = rw;
                targetHeight = rh;
                resolution = $"{rw}x{rh} (API Custom)";
            }
            else if ((targetWidth <= 0 || targetHeight <= 0) &&
                allImageInputs &&
                resolution.Equals("Original", StringComparison.OrdinalIgnoreCase))
            {
                targetWidth = 1080;
                targetHeight = 1920;
                resolution = "1080x1920 (Portrait Image Default)";
                onLog("[API] Image source detected -> defaulting Original resolution to 1080x1920 portrait");
            }
            else if (targetWidth > 0 && targetHeight > 0)
            {
                resolution = $"{targetWidth}x{targetHeight} (API Custom)";
            }

            if (cropZoomPercent > 0.001 && targetWidth > 0 && targetHeight > 0)
            {
                int originalTargetWidth = targetWidth;
                int originalTargetHeight = targetHeight;
                (targetWidth, targetHeight) = EngineCore.CoerceResolutionToPortraitNineBySixteen(targetWidth, targetHeight);
                resolution = $"{targetWidth}x{targetHeight} (Crop 9:16)";
                onLog($"[API-CROP-ZOOM] {cropZoomPercent:0.#}% center crop enabled -> forcing 9:16 output {originalTargetWidth}x{originalTargetHeight} -> {targetWidth}x{targetHeight}");
            }

            string hardwareProfile = NormalizeHardwareProfileName(FirstNonEmpty(
                request.HardwareProfile,
                GetFeatureString(request, "hardwareProfile", "hardware", "encoder")
            ) ?? "NVIDIA NVENC");

            string bitrateStrategy = NormalizeBitrateStrategy(FirstNonEmpty(
                request.BitrateStrategy,
                GetFeatureString(request, "bitrateStrategy", "bitrateMode", "bitrate")
            ) ?? "Match Source");

            string framerate = ResolveFramerateName(FirstNonEmpty(
                request.Framerate,
                GetFeatureString(request, "framerate", "fps")
            ) ?? "Original");

            string customBitrate = (FirstNonEmpty(
                request.CustomBitrate,
                GetFeatureString(request, "customBitrate", "targetBitrate", "kbps")
            ) ?? "5000").Trim();

            string upscaleMode = NormalizeUpscaleModeName(FirstNonEmpty(
                request.UpscaleMode,
                GetFeatureString(request, "upscaleMode", "upscale")
            ) ?? "Off (Original Size)");

            double sharpness = Math.Clamp(
                request.SharpnessIntensity ??
                GetFeatureDouble(request, "sharpnessIntensity", "sharpness") ??
                1.0,
                0.0, 2.0
            );

            double videoBrightness = Math.Clamp(
                request.VideoBrightness ??
                GetFeatureDouble(request, "videoBrightness", "brightness", "videoBright") ??
                0.0,
                -100.0, 100.0
            );

            double slowMotionSpeed = Math.Clamp(
                request.SlowMotionSpeed ??
                GetFeatureDouble(request, "slowMotionSpeed", "playbackSpeed", "speed") ??
                1.0,
                0.10, 8.0
            );

            bool slowMotionAudio = request.SlowMotionAudio ??
                GetFeatureBool(request, "slowMotionAudio", "syncAudio") ??
                true;

            double imageTimelineDurationSeconds = Math.Max(0.0,
                request.ImageTimelineDurationSeconds ??
                GetFeatureDouble(request, "imageTimelineDurationSeconds", "imageTimelineDuration", "imageDurationSeconds", "slideshowDuration", "slideDuration") ??
                0.0);

            double videoVolume = Math.Clamp(
                request.VideoVolume ??
                GetFeatureDouble(request, "videoVolume", "videoVol") ??
                1.0,
                0.0, 2.0
            );

            double audioVolume = Math.Clamp(
                request.AudioVolume ??
                GetFeatureDouble(request, "audioVolume", "audioVol", "musicVolume") ??
                1.0,
                0.0, 2.0
            );

            double audio2Volume = Math.Clamp(
                request.Audio2Volume ??
                request.SecondaryAudioVolume ??
                GetFeatureDouble(request, "audio2Volume", "audio2Vol", "secondaryAudioVolume", "overlayAudioVolume", "music2Volume") ??
                1.0,
                0.0, 2.0
            );

            double sourceAudioSpeed = Math.Clamp(
                request.SourceAudioSpeed ??
                GetFeatureDouble(request, "sourceAudioSpeed", "audioSpeed", "sourceAudioRate") ??
                1.0,
                0.10, 8.0
            );

            double externalAudioSpeed = Math.Clamp(
                request.ExternalAudioSpeed ??
                GetFeatureDouble(request, "externalAudioSpeed", "audioTrackSpeed", "musicSpeed", "addedAudioSpeed", "externalMusicSpeed") ??
                1.0,
                0.10, 8.0
            );

            string videoTrimStart = (FirstNonEmpty(
                request.VideoTrimStart,
                GetFeatureString(request, "videoTrimStart", "trimStart", "videoStart")
            ) ?? "00:00:00").Trim();

            string videoTrimDuration = (FirstNonEmpty(
                request.VideoTrimDuration,
                GetFeatureString(request, "videoTrimDuration", "trimDuration", "videoDuration")
            ) ?? string.Empty).Trim();

            string audioTrimStart = (FirstNonEmpty(
                request.AudioTrimStart,
                GetFeatureString(request, "audioTrimStart", "audioStart")
            ) ?? "00:00:00").Trim();

            string audioTrimDuration = (FirstNonEmpty(
                request.AudioTrimDuration,
                GetFeatureString(request, "audioTrimDuration", "audioDuration")
            ) ?? string.Empty).Trim();

            string audioTrimEnd = (FirstNonEmpty(
                request.AudioTrimEnd,
                GetFeatureString(request, "audioTrimEnd", "audioEnd")
            ) ?? string.Empty).Trim();

            List<AudioEditSegment> requestAudioSegments = new List<AudioEditSegment>();
            if (request.AudioSegments != null && request.AudioSegments.Count > 0)
            {
                requestAudioSegments = AudioEditHelpers.NormalizeSegments(request.AudioSegments, null);
            }
            else
            {
                bool hasAudioTrimStart = AudioEditHelpers.TryParseFlexibleTime(audioTrimStart, out double parsedAudioTrimStart);
                bool hasAudioTrimEnd = AudioEditHelpers.TryParseFlexibleTime(audioTrimEnd, out double parsedAudioTrimEnd);
                bool hasAudioTrimDuration = AudioEditHelpers.TryParseFlexibleTime(audioTrimDuration, out double parsedAudioTrimDuration);

                if (hasAudioTrimStart || hasAudioTrimEnd || hasAudioTrimDuration)
                {
                    double audioTrimStartValue = hasAudioTrimStart ? parsedAudioTrimStart : 0.0;
                    double audioTrimEndValue = hasAudioTrimEnd
                        ? parsedAudioTrimEnd
                        : (hasAudioTrimDuration ? audioTrimStartValue + parsedAudioTrimDuration : audioTrimStartValue);

                    requestAudioSegments = AudioEditHelpers.NormalizeSegments(new[]
                    {
                        new AudioEditSegment
                        {
                            StartSeconds = audioTrimStartValue,
                            EndSeconds = audioTrimEndValue
                        }
                    });
                }
            }

            double watermarkScale = Math.Clamp(
                request.WatermarkScale ??
                GetFeatureDouble(request, "watermarkScale", "logoScale") ??
                1.0,
                0.01, 6.0
            );

            double watermarkRotation = request.WatermarkRotation ??
                GetFeatureDouble(request, "watermarkRotation", "logoRotation") ??
                0.0;

            double watermarkOpacity = Math.Clamp(
                request.WatermarkOpacity ??
                GetFeatureDouble(request, "watermarkOpacity", "logoOpacity") ??
                1.0,
                0.0, 1.0
            );

            double watermarkX = Math.Clamp(
                request.WatermarkXPercent ??
                GetFeatureDouble(request, "watermarkXPercent", "watermarkX", "logoX") ??
                0.05,
                0.0, 1.0
            );

            double watermarkY = Math.Clamp(
                request.WatermarkYPercent ??
                GetFeatureDouble(request, "watermarkYPercent", "watermarkY", "logoY") ??
                0.05,
                0.0, 1.0
            );

            double watermarkAspect = Math.Clamp(
                request.WatermarkAspectRatio ??
                GetFeatureDouble(request, "watermarkAspectRatio", "logoAspect") ??
                1.0,
                0.01, 100.0
            );

            bool enableFade = request.EnableFade ??
                GetFeatureBool(request, "enableFade", "fadeEnabled", "fade", "fadeInOut") ??
                false;

            double fadeInSeconds = Math.Clamp(
                request.FadeInSeconds ??
                GetFeatureDouble(request, "fadeInSeconds", "fadeIn", "fadeInSec") ??
                0.8,
                0.0, 60.0
            );

            double fadeOutSeconds = Math.Clamp(
                request.FadeOutSeconds ??
                GetFeatureDouble(request, "fadeOutSeconds", "fadeOut", "fadeOutSec") ??
                0.8,
                0.0, 60.0
            );

            var job = new RenderJob
            {
                SourcePath = inputPath,
                MergeVideos = (mergeVideosRequested || forceMerge) && inputPaths.Count > 1,
                MergeInputPaths = (mergeVideosRequested || forceMerge) && inputPaths.Count > 1
                    ? new List<string>(inputPaths)
                    : new List<string> { inputPath },
                MergeAudio = mergeAudioRequested && audioPaths.Count > 1,
                MergeAudioPaths = mergeAudioRequested && audioPaths.Count > 1
                    ? new List<string>(audioPaths)
                    : (string.IsNullOrWhiteSpace(audioPath) ? new List<string>() : new List<string> { audioPath }),
                AudioPath = audioPath,
                SecondaryAudioPath = secondaryAudioPath,
                WatermarkPath = watermarkPath,
                OutputName = outputName,
                HardwareProfile = hardwareProfile,
                BitrateStrategy = bitrateStrategy,
                Resolution = resolution,
                TargetWidth = targetWidth,
                TargetHeight = targetHeight,
                Framerate = framerate,
                CustomBitrate = customBitrate,
                VideoTrimStart = videoTrimStart,
                VideoTrimDuration = videoTrimDuration,
                AudioTrimStart = audioTrimStart,
                AudioTrimEnd = audioTrimEnd,
                AudioTrimDuration = audioTrimDuration,
                ImageTimelineDurationSeconds = imageTimelineDurationSeconds,
                WatermarkScale = watermarkScale,
                WatermarkRotation = watermarkRotation,
                WatermarkOpacity = watermarkOpacity,
                WatermarkXPercent = watermarkX,
                WatermarkYPercent = watermarkY,
                WatermarkAspectRatio = watermarkAspect,
                FxEffect = fxEffect,
                OverlayEffect = overlayEffect,
                BorderEffect = borderEffect,
                EnableSnowOverlay1 = enableSnowOverlay1,
                EnableSnowOverlay2 = enableSnowOverlay2,
                EnableSnowOverlay3 = enableSnowOverlay3,
                EnableOverlayIntro = enableOverlayIntro,
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
                SnowfallMode = snowfallMode,
                SnowfallAssetPath = snowfallAssetPath,
                SnowfallOpacity = snowfallOpacity,
                SnowOverlay1Opacity = snowOverlay1Opacity,
                SnowOverlay2Opacity = snowOverlay2Opacity,
                SnowOverlay3Opacity = snowOverlay3Opacity,
                OverlayIntroOpacity = overlayIntroOpacity,
                RainOverlayPreset = rainOverlayPreset,
                RainOverlayIntensity = rainOverlayIntensity,
                RainOverlayAssetPath = rainOverlayAssetPath,
                MusicSyncMode = musicSyncMode,
                MagazineCoverTitle = string.IsNullOrWhiteSpace(rawMagazineCoverTitle) ? "TITAN" : rawMagazineCoverTitle!,
                MagazineCoverSubtitle = string.IsNullOrWhiteSpace(rawMagazineCoverSubtitle) ? "COVER STORY" : rawMagazineCoverSubtitle!,
                LightLeakBurnIntensity = lightLeakBurnIntensity,
                LightLeakBurnSpread = lightLeakBurnSpread,
                LightLeakBurnWarmth = lightLeakBurnWarmth,
                LightLeakBurnBurn = lightLeakBurnBurn,
                LightLeakBurnEdgeSoftness = lightLeakBurnEdgeSoftness,
                LightLeakBurnGrain = lightLeakBurnGrain,
                LightLeakBurnDirection = lightLeakBurnDirection,
                LightLeakBurnDuration = lightLeakBurnDuration,
                LightLeakBurnBlendMode = lightLeakBurnBlendMode,
                LightLeakBurnOpacity = lightLeakBurnOpacity,
                LightLeakBurnAssetPath = lightLeakBurnAssetPath,
                LightLeakBurnUseAssetOverlay = lightLeakBurnUseAssetOverlay,
                LightLeakOverlayMode = lightLeakOverlayMode,
                LightLeakOverlayId = lightLeakOverlayId,
                LightLeakOverlayAssetPath = lightLeakOverlayAssetPath,
                LightLeakOverlayOpacity = lightLeakOverlayOpacity,
                FxIntensity = fxIntensity,
                FxStartPercent = fxStart,
                FxEndPercent = fxEnd,
                VideoBrightness = videoBrightness,
                EnableFade = enableFade,
                FadeInSeconds = fadeInSeconds,
                FadeOutSeconds = fadeOutSeconds,
                CropEnabled = cropEnabled,
                CropZoomPercent = cropZoomPercent,
                CropAspectRatio = cropZoomPercent > 0.001 ? "9:16" : cropAspectRatio,
                CropXPercent = cropX,
                CropYPercent = cropY,
                CropWidthPercent = cropWidth,
                CropHeightPercent = cropHeight,
                ColorFilter = colorFilter,
                ColorIntensity = colorIntensity,
                VideoVolume = videoVolume,
                AudioVolume = audioVolume,
                SecondaryAudioVolume = audio2Volume,
                Audio1Effect = request.Audio1Effect ?? GetFeatureString(request, "audio1Effect", "voiceFx", "audioEffect"),
                Audio1EffectIntensity = request.Audio1EffectIntensity.HasValue
                    ? (int)Math.Clamp(request.Audio1EffectIntensity.Value, 0, 100)
                    : (int?)GetFeatureDouble(request, "audio1EffectIntensity", "audioFxIntensity", "fxIntensity1") ?? 100,
                SourceAudioSpeed = sourceAudioSpeed,
                ExternalAudioSpeed = externalAudioSpeed,
                AudioSegments = requestAudioSegments,
                UpscaleMode = upscaleMode,
                SharpnessIntensity = sharpness,
                SlowMotionSpeed = slowMotionSpeed,
                SlowMotionAudio = slowMotionAudio,
                SplitDurationSeconds = request.SplitDurationSeconds ?? GetFeatureDouble(request, "splitDurationSeconds", "splitDuration", "splitSec") ?? 0.0,
                StatusDisplay = "WAITING",
                StatusColor = "Yellow"
            };
            job.IsApiJob = true;
            job.WebhookUrl = request.WebhookUrl;
            job.EnableLoudnorm = request.EnableLoudnorm ?? GetFeatureBool(request, "enableLoudnorm", "loudnorm") ?? false;
            job.TextOverlay = request.TextOverlay ?? GetFeatureString(request, "textOverlay", "text");
            job.TextOverlayPosition = request.TextOverlayPosition ?? GetFeatureString(request, "textOverlayPosition", "textPosition");
            job.TextAlign = request.TextAlign ?? GetFeatureString(request, "textAlign", "align");
            job.TextOverlayFontSize = request.TextOverlayFontSize;
            job.TextOverlayColor = request.TextOverlayColor ?? GetFeatureString(request, "textOverlayColor", "textColor");
            job.TextOverlayStyle = request.TextOverlayStyle ?? GetFeatureString(request, "textOverlayStyle", "textStyle", "fontStyle");
            job.AddQuotes = request.AddQuotes ?? GetFeatureBool(request, "addQuotes", "quotes") ?? true;
            job.DisableTextScroll = request.DisableTextScroll ?? GetFeatureBool(request, "disableTextScroll", "disableTextAnimation", "staticText", "noScroll") ?? false;
            job.TextOverlayXPercent = request.TextOverlayXPercent ?? GetFeatureDouble(request, "textOverlayXPercent", "textXPercent", "textX") ?? job.TextOverlayXPercent;
            job.TextOverlayYPercent = request.TextOverlayYPercent ?? GetFeatureDouble(request, "textOverlayYPercent", "textYPercent", "textY") ?? job.TextOverlayYPercent;
            job.EnableTextGlow = request.EnableTextGlow ?? GetFeatureBool(request, "enableTextGlow", "textGlow", "glowingText", "glow") ?? false;
            job.TextGlowColor = request.TextGlowColor ?? GetFeatureString(request, "textGlowColor", "glowColor") ?? "Cyan Neon";
            job.TextOverlayAnimation = request.TextOverlayAnimation ?? GetFeatureString(request, "textOverlayAnimation", "textAnimation", "overlayAnimation");
            job.TextOverlayEffectDuration = request.TextOverlayEffectDuration ?? GetFeatureDouble(request, "textOverlayEffectDuration", "textEffectDuration", "animationDuration") ?? 5.0;
                
            // Caption Sync mapping
            job.EnableCaptionSync = request.EnableCaptionSync ?? GetFeatureBool(request, "enableCaptionSync", "captionSync", "whisper") ?? false;
            job.CaptionSyncLanguage = request.CaptionSyncLanguage ?? GetFeatureString(request, "captionSyncLanguage", "captionLanguage", "whisperLanguage") ?? "auto";
            job.CaptionSyncModelSize = request.CaptionSyncModelSize ?? GetFeatureString(request, "captionSyncModelSize", "captionModelSize", "whisperModel") ?? "Small";
            job.CaptionSyncAnimation = request.CaptionSyncAnimation ?? GetFeatureString(request, "captionSyncAnimation", "captionAnimation", "whisperAnimation") ?? "None";
            job.CaptionSyncFontFamily = request.CaptionSyncFontFamily ?? GetFeatureString(request, "captionSyncFontFamily", "captionFont", "whisperFont") ?? "Clean Sans (Arial Italic)";
            job.CaptionSyncPosition = request.CaptionSyncPosition ?? GetFeatureString(request, "captionSyncPosition", "captionPosition", "captionStyle") ?? "Bottom Center";
            job.CaptionSyncFontSize = request.CaptionSyncFontSize ?? (int?)(GetFeatureDouble(request, "captionSyncFontSize", "captionFontSize") ?? null) ?? 28;
            job.CaptionSyncColor = request.CaptionSyncColor ?? GetFeatureString(request, "captionSyncColor", "captionColor") ?? "white";
            job.CaptionSyncXPercent = request.CaptionSyncXPercent ?? GetFeatureDouble(request, "captionSyncXPercent", "captionXPercent", "captionX") ?? 50.0;
            job.CaptionSyncYPercent = request.CaptionSyncYPercent ?? GetFeatureDouble(request, "captionSyncYPercent", "captionYPercent", "captionY") ?? 85.0;

            // SOUNDBAR (audio visualizer) mapping. Every value is clamped here so the
            // render pipeline never sees an out-of-range style request.
            job.EnableSoundBar = request.EnableSoundBar
                ?? GetFeatureBool(request, "enableSoundBar", "soundBar", "soundbar")
                // EnableAudioSpectrum is the ORIGINAL public name for this feature and is
                // still advertised in TITAN_ENGINE_SUMMARY.md, but it was parsed onto the DTO
                // and then read nowhere - so a client posting {"enableAudioSpectrum": true}
                // got a silently audio-less render. Treat it as a last-resort alias, after
                // the explicit field and after the feature bag, so nothing already honoured
                // changes meaning.
                ?? request.EnableAudioSpectrum
                ?? GetFeatureBool(request, "enableAudioSpectrum", "audioSpectrum")
                ?? false;
            if (job.EnableSoundBar)
            {
                job.SoundBarStyle = SoundBarVisualizerPreset.NormalizeStyleId(
                    request.SoundBarStyle ?? GetFeatureString(request, "soundBarStyle", "soundbarStyle"));
                job.SoundBarPalette = string.IsNullOrWhiteSpace(request.SoundBarPalette)
                    // Same per-style fallback as the local-job path: audio-ring's reference is
                    // the yellow NCS sphere, everything else keeps cyan. An explicit palette
                    // always wins.
                    ? (job.SoundBarStyle == "audio-ring" ? "ncs-house-yellow" : "ncs-cyan")
                    : request.SoundBarPalette.Trim();
                job.SoundBarAudioSource = NormalizeSoundBarAudioSource(
                    request.SoundBarAudioSource ?? GetFeatureString(request, "soundBarAudioSource", "soundbarAudioSource"));
                job.SoundBarXPercent = NormalizeSoundBarFraction(request.SoundBarXPercent, 0.50);
                job.SoundBarYPercent = NormalizeSoundBarFraction(request.SoundBarYPercent, 0.82);
                job.SoundBarWidthPercent = NormalizeSoundBarFraction(request.SoundBarWidthPercent, 0.62, 0.02);
                job.SoundBarHeightPercent = NormalizeSoundBarFraction(request.SoundBarHeightPercent, 0.26, 0.02);
                job.SoundBarOpacity = Math.Clamp(request.SoundBarOpacity ?? 1.0, EngineCore.SoundBarMinOpacity, EngineCore.SoundBarMaxOpacity);
                job.SoundBarSensitivity = Math.Clamp(request.SoundBarSensitivity ?? 1.0, EngineCore.SoundBarMinSensitivity, EngineCore.SoundBarMaxSensitivity);
                job.SoundBarSmoothing = Math.Clamp(request.SoundBarSmoothing ?? 1.0, EngineCore.SoundBarMinSmoothing, EngineCore.SoundBarMaxSmoothing);
                job.SoundBarBarCount = (int)Math.Round((double)Math.Clamp(request.SoundBarBarCount ?? 64, EngineCore.SoundBarMinBarCount, EngineCore.SoundBarMaxBarCount), MidpointRounding.AwayFromZero);
                job.SoundBarRingCount = (int)Math.Round((double)Math.Clamp(request.SoundBarRingCount ?? 2, EngineCore.SoundBarMinRingCount, EngineCore.SoundBarMaxRingCount), MidpointRounding.AwayFromZero);
                job.SoundBarRoundness = Math.Clamp(request.SoundBarRoundness ?? 1.0, 0.0, 1.0);
                job.SoundBarGlowIntensity = Math.Clamp(request.SoundBarGlowIntensity ?? 1.0, 0.0, 3.0);
                job.SoundBarLoopSeconds = Math.Clamp(request.SoundBarLoopSeconds ?? 5.0, EngineCore.SoundBarMinLoopSeconds, EngineCore.SoundBarMaxLoopSeconds);
            }

            if (job.MergeVideos && job.MergeInputPaths.Count > 1)
            {
                string referenceInput = job.MergeInputPaths.FirstOrDefault(p => !EngineCore.IsImageSourcePath(p)) ?? job.MergeInputPaths[0];
                var firstMeta = await EngineCore.AnalyzeMediaDetailsAsync(referenceInput);
                double totalDuration = 0.0;
                long maxBitrate = Math.Max(0L, firstMeta.Bitrate);
                int imageCount = job.MergeInputPaths.Count(EngineCore.IsImageSourcePath);
                double imageSegmentDuration = EngineCore.ResolveImageTimelineSegmentDuration(job.ImageTimelineDurationSeconds, imageCount);
                foreach (string mergeInput in job.MergeInputPaths)
                {
                    if (EngineCore.IsImageSourcePath(mergeInput))
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
                if (EngineCore.IsImageSourcePath(job.SourcePath))
                {
                    var meta = await EngineCore.AnalyzeMediaDetailsAsync(job.SourcePath);
                    job.DurationSec = EngineCore.ResolveImageTimelineSegmentDuration(job.ImageTimelineDurationSeconds, 1);
                    job.SourceBitrate = Math.Max(meta.Bitrate, 5000000);
                    job.SourceWidth = meta.Width > 0
                        ? meta.Width
                        : (job.TargetWidth > 0 ? job.TargetWidth : 1920);
                    job.SourceHeight = meta.Height > 0
                        ? meta.Height
                        : (job.TargetHeight > 0 ? job.TargetHeight : 1080);
                    job.SourceFps = meta.Fps > 0.1 ? meta.Fps : 30.0;
                    onLog($"[IMAGE-META] {job.SourceWidth}x{job.SourceHeight} @ {job.SourceFps:F2}fps (image source)");
                }
                else
                {
                    var meta = await EngineCore.AnalyzeMediaDetailsAsync(job.SourcePath);
                    job.DurationSec = meta.Duration;
                    job.SourceBitrate = meta.Bitrate;
                    job.SourceWidth = meta.Width;
                    job.SourceHeight = meta.Height;
                    job.SourceFps = meta.Fps;
                }
            }
            return new LocalhostRequestMapping
            {
                Job = job,
                InputPath = inputPath,
                OutputPath = outputPath,
                OutputDir = outputDir,
                ColorFilter = colorFilter,
                FxEffect = fxEffect,
                OverlayEffect = overlayEffect,
                TemplateName = templateName,
                CropEnabled = cropEnabled,
                UpscaleMode = upscaleMode,
            };
        }

        internal static bool TryParseFlexibleDouble(string? input, out double value)
        {
            value = 0.0;

            if (string.IsNullOrWhiteSpace(input))
                return false;

            string trimmed = input.Trim();

            return double.TryParse(trimmed, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out value) ||
                   double.TryParse(trimmed, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out value) ||
                   double.TryParse(trimmed.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        internal static bool ShouldUsePortraitHdDefaultForImages(IReadOnlyCollection<string>? inputPaths)
        {
            return inputPaths != null &&
                   inputPaths.Count > 0 &&
                   inputPaths.All(EngineCore.IsImageSourcePath);
        }

        internal static string ResolveColorFilterFromRequest(LocalhostRenderRequest request)
        {
            string? color = FirstNonEmpty(
                request.ColorGrading,
                GetFeatureString(request, "colorgrading", "color", "colorFilter")
            );

            if (!string.IsNullOrWhiteSpace(color))
                return NormalizeColorFilterName(color);

            return "None (Original)";
        }

        internal static string? FirstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        internal static bool TryGetCaseInsensitive(Dictionary<string, JsonElement>? dict, string key, out JsonElement value)
        {
            if (dict != null)
            {
                foreach (var kv in dict)
                {
                    if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        value = kv.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        internal static string? GetFeatureString(LocalhostRenderRequest request, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (TryGetCaseInsensitive(request.Features, key, out JsonElement fv))
                {
                    string? parsed = ParseJsonString(fv);
                    if (!string.IsNullOrWhiteSpace(parsed))
                        return parsed;
                }

                if (TryGetCaseInsensitive(request.ExtraFields, key, out JsonElement ev))
                {
                    string? parsed = ParseJsonString(ev);
                    if (!string.IsNullOrWhiteSpace(parsed))
                        return parsed;
                }
            }

            return null;
        }

        internal static List<string>? GetFeatureStringList(LocalhostRenderRequest request, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (TryGetCaseInsensitive(request.Features, key, out JsonElement fv))
                {
                    List<string>? parsed = ParseJsonStringList(fv);
                    if (parsed != null && parsed.Count > 0)
                        return parsed;
                }

                if (TryGetCaseInsensitive(request.ExtraFields, key, out JsonElement ev))
                {
                    List<string>? parsed = ParseJsonStringList(ev);
                    if (parsed != null && parsed.Count > 0)
                        return parsed;
                }
            }

            return null;
        }

        internal static double? GetFeatureDouble(LocalhostRenderRequest request, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (TryGetCaseInsensitive(request.Features, key, out JsonElement fv))
                {
                    double? parsed = ParseJsonDouble(fv);
                    if (parsed.HasValue) return parsed.Value;
                }

                if (TryGetCaseInsensitive(request.ExtraFields, key, out JsonElement ev))
                {
                    double? parsed = ParseJsonDouble(ev);
                    if (parsed.HasValue) return parsed.Value;
                }
            }

            return null;
        }

        internal static int? GetFeatureInt(LocalhostRenderRequest request, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (TryGetCaseInsensitive(request.Features, key, out JsonElement fv))
                {
                    int? parsed = ParseJsonInt(fv);
                    if (parsed.HasValue) return parsed.Value;
                }

                if (TryGetCaseInsensitive(request.ExtraFields, key, out JsonElement ev))
                {
                    int? parsed = ParseJsonInt(ev);
                    if (parsed.HasValue) return parsed.Value;
                }
            }

            return null;
        }

        internal static bool? GetFeatureBool(LocalhostRenderRequest request, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (TryGetCaseInsensitive(request.Features, key, out JsonElement fv))
                {
                    bool? parsed = ParseJsonBool(fv);
                    if (parsed.HasValue) return parsed.Value;
                }

                if (TryGetCaseInsensitive(request.ExtraFields, key, out JsonElement ev))
                {
                    bool? parsed = ParseJsonBool(ev);
                    if (parsed.HasValue) return parsed.Value;
                }
            }

            return null;
        }

        internal static string? ParseJsonString(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString()?.Trim(),
                JsonValueKind.Number => element.GetRawText().Trim(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        internal static List<string>? ParseJsonStringList(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    string? value = ParseJsonString(item);
                    if (!string.IsNullOrWhiteSpace(value))
                        list.Add(value);
                }
                return list;
            }

            string? single = ParseJsonString(element);
            if (!string.IsNullOrWhiteSpace(single))
                return new List<string> { single };

            return null;
        }

        internal static double? ParseJsonDouble(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double n))
                return n;

            if (element.ValueKind == JsonValueKind.String &&
                TryParseFlexibleDouble(element.GetString(), out double s))
                return s;

            return null;
        }

        internal static int? ParseJsonInt(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number)
            {
                if (element.TryGetInt32(out int i32)) return i32;
                if (element.TryGetDouble(out double nd)) return (int)Math.Round(nd);
            }

            if (element.ValueKind == JsonValueKind.String &&
                int.TryParse(element.GetString()?.Trim(), out int si))
                return si;

            return null;
        }

        internal static bool? ParseJsonBool(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.True) return true;
            if (element.ValueKind == JsonValueKind.False) return false;
            if (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString()?.Trim(), out bool sb)) return sb;
            return null;
        }

        internal static double NormalizeOptionalOpacity(double value)
        {
            if (value < 0.0)
                return -1.0;

            double normalized = value > 1.0 ? value / 100.0 : value;
            return Math.Clamp(normalized, 0.0, 1.0);
        }

        internal static bool TryExtractResolution(string input, out int width, out int height)
        {
            width = -1;
            height = -1;

            if (string.IsNullOrWhiteSpace(input))
                return false;

            Match match = Regex.Match(input, @"(\d+)\s*x\s*(\d+)", RegexOptions.IgnoreCase);
            if (!match.Success)
                return false;

            if (!int.TryParse(match.Groups[1].Value, out width) || !int.TryParse(match.Groups[2].Value, out height))
                return false;

            return width > 0 && height > 0;
        }

        internal static string NormalizeHardwareProfileName(string input)
        {
            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (v.Contains("nvidia")) return "NVIDIA NVENC";
            if (v.Contains("amd")) return "AMD AMF";
            if (v.Contains("cpu")) return "Cpu";
            return "NVIDIA NVENC";
        }

        internal static string NormalizeBitrateStrategy(string input)
        {
            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (v.Contains("boost")) return "Boost Quality";
            if (v.Contains("custom")) return "Custom (Kbps)";
            return "Match Source";
        }

        internal static string NormalizeResolutionName(string input)
        {
            string raw = (input ?? string.Empty).Trim();
            string v = raw.ToLowerInvariant();

            if (v.Contains("1920x1080")) return "1920x1080 (HD)";
            if (v.Contains("1080x1920")) return "1080x1920 (Portrait)";
            if (v.Contains("720x1280")) return "720x1280 (Portrait HD)";
            if (v.Contains("1280x720")) return "1280x720";
            if (v.Contains("custom")) return "Custom (Nhap so)";
            if (v.Contains("original") || string.IsNullOrWhiteSpace(v)) return "Original";

            return raw;
        }

        internal static string ResolveFramerateName(string input)
        {
            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (v == "60" || v.Contains("60")) return "60";
            if (v == "30" || v.Contains("30")) return "30";
            if (v == "24" || v.Contains("24")) return "24";
            return "Original";
        }

        internal static string NormalizeUpscaleModeName(string input)
        {
            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (v.Contains("2x")) return "2x Upscale (FSR + DLSS)";
            if (v.Contains("4x")) return "4x Upscale (Ultra Quality)";
            return "Off (Original Size)";
        }

        internal static string NormalizeFxEffectName(string? input)
        {
            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(v) || v == "none")
                return "None";
            if (v.Contains("crop")) return "Crop Video (Custom Frame)";
            if (v.Contains("flip")) return "Flip Horizontal (Anti-Copyright)";
            if (v.Contains("light leak burn") || v.Contains("lightleakburn") || v.Contains("film burn") || v.Contains("burn transition") || v.Contains("light burn"))
                return "Light Leak Burn";
            if (v.Contains("film leak") || v.Contains("light leak") || v.Contains("leak 1") || v.Contains("rÃƒÂ² phim") || v.Contains("ro phim") || v.Contains("leak")) return "RÃƒÂ² phim (Leak 1 / Light Leak)";
            if (v.Contains("cinematic glow") || v.Contains("glow cinematic") || v == "glow") return "Cinematic Glow";
            if (v.Contains("halo glow") || v == "halo") return "Halo Glow";
            if (v.Contains("prism light") || v == "prism") return "Prism Light";
            if (v.Contains("editorial bloom") || v.Contains("bloom")) return "Editorial Bloom";
            if (v.Contains("dust & scratches") || v.Contains("dust and scratches") || v.Contains("dust scratches") ||
                v.Contains("old film scratches") || v.Contains("scratch video") || v == "scratch" ||
                v.Contains("film scratch") || v.Contains("video scratch")) return "Dust & Scratches";
            return "None";
        }

        internal static string ResolveFxEffectName(string? input)
        {
            if (EffectRecipeLibrary.IsRetiredRecipeName(input))
                return "None";

            if (EffectRecipeLibrary.TryResolveCanonicalEffectName(input, out string canonicalRecipeName))
                return canonicalRecipeName;

            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(v) || v == "none")
                return "None";

            if (v.Contains("crop"))
                return "Crop Video (Custom Frame)";
            if (v.Contains("flip"))
                return "Flip Horizontal (Anti-Copyright)";
            if (v.Contains("snowfall") || v == "snow" || v.Contains("snow overlay") || v.Contains("snow soft") ||
                v.Contains("snow bokeh") || v.Contains("snow heavy") || v.Contains("snow windy") || v.Contains("snow cinematic"))
                return "Snowfall Overlay";
            if (v.Contains("film leak") || v.Contains("light leak") || v.Contains("leak 1") || v.Contains("rÃƒÂ² phim") || v.Contains("ro phim") || v.Contains("leak"))
                return "RÃƒÂ² phim (Leak 1 / Light Leak)";
            if (v.Contains("cinematic glow") || v.Contains("glow cinematic") || v == "glow")
                return "Cinematic Glow";
            if (v.Contains("halo glow") || v == "halo" || v.Contains("halo"))
                return "Halo Glow";
            if (v.Contains("prism light") || v == "prism" || v.Contains("prism"))
                return "Prism Light";
            if (v.Contains("editorial bloom") || v.Contains("bloom"))
                return "Editorial Bloom";
            if (v.Contains("dust & scratches") || v.Contains("dust and scratches") || v.Contains("dust scratches") ||
                v.Contains("old film scratches") || v.Contains("scratch video") || v == "scratch" ||
                v.Contains("film scratch") || v.Contains("video scratch"))
                return "Dust & Scratches";

            return "None";
        }

        internal static string ResolveOverlayEffectName(string? input)
        {
            if (EffectRecipeLibrary.IsRetiredRecipeName(input))
                return "None";

            if (EffectRecipeLibrary.TryResolveCanonicalEffectName(input, out string canonicalRecipeName))
                return canonicalRecipeName;

            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(v) || v == "none")
                return "None";

            if (v.Contains("light leak overlay") || v.Contains("lightleakoverlay") || v.Contains("light leak pack") ||
                v.Contains("film burn overlay") || v.Contains("camera leak overlay") || v.Contains("overlay leak") ||
                v == "light leak" || v == "film leak")
                return "Light Leak Overlay";
            if (v.Contains("dreamydotoverlay2") || v.Contains("dreamy dot overlay 2") || v.Contains("dreamy dot 2") ||
                v.Contains("bokeh light overlay 2") || v.Contains("dreamy bokeh 2"))
                return "Dreamy Dot Overlay 2";
            if (v.Contains("dreamy dot chaos") || v.Contains("chaos dot") || v.Contains("random dot") ||
                v.Contains("dreamy chaos") || v.Contains("wandering dots"))
                return "Dreamy Dot Chaos Overlay";
            if (v.Contains("dreamy dot") || v.Contains("soft dot") || v.Contains("dot overlay") ||
                v.Contains("bokeh dot") || v.Contains("bokeh drift") || v.Contains("particle drift") ||
                v.Contains("floating dots") || v.Contains("blur dot"))
                return "Dreamy Dot Chaos Overlay";
            if (v.Contains("snowfall") || v == "snow" || v.Contains("snow overlay") || v.Contains("snow soft") ||
                v.Contains("snow bokeh") || v.Contains("snow heavy") || v.Contains("snow windy") || v.Contains("snow cinematic"))
                return "Snowfall Overlay";
            if (v.Contains("rain overlay") || v == "rain" || v.Contains("heavy rain") ||
                v.Contains("cinematic rain") || v.Contains("window drops") || v.Contains("heavyrainblackbg") ||
                v.Contains("cinematicrainblackbg") || v.Contains("windowdrops"))
                return "Rain Overlay";
            if (v.Contains("polaroid scrapbook 2") || v.Contains("polaroidscrapbook2") || v.Contains("rounded polaroid scrapbook") || v.Contains("rounded polaroid"))
                return "Polaroid Scrapbook 2";
            if (v.Contains("rgb polaroid") || v.Contains("polaroid rgb") || v.Contains("rgb border") || v.Contains("rainbow polaroid"))
                return "RGB Polaroid Border";
            if (v.Contains("dashed polaroid") || v.Contains("polaroid dashed") || v.Contains("dashed frame"))
                return "Dashed Polaroid Frame";
            if (v.Contains("dust & scratches") || v.Contains("dust and scratches") || v.Contains("dust scratches") ||
                v.Contains("old film scratches") || v.Contains("scratch video") || v == "scratch" ||
                v.Contains("film scratch") || v.Contains("video scratch"))
                return "Dust & Scratches";
            if (v.Contains("polaroid") || v.Contains("scrapbook") || v.Contains("photo album"))
                return "Polaroid Scrapbook";

            return "None";
        }

        internal static string ResolveSnowfallPresetName(string? input)
        {
            return SnowfallOverlayPreset.NormalizePreset((input ?? string.Empty).Trim());
        }

        internal static string ResolveRainOverlayPresetName(string? input)
        {
            return RainOverlayPreset.NormalizePreset((input ?? string.Empty).Trim());
        }

        internal static string ResolveRainOverlayIntensityName(string? input)
        {
            return RainOverlayPreset.NormalizeIntensity((input ?? string.Empty).Trim());
        }

        internal static string NormalizeTemplateName(string? input)
        {
            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(v) || v == "none")
                return "None";
            if (v.Contains("smooth zoom in") || v.Contains("zoom in"))
                return "Smooth Zoom In";
            if (v.Contains("smooth zoom out") || v.Contains("zoom out"))
                return "Smooth Zoom Out";
            if (v.Contains("pan left"))
                return "Pan Left";
            if (v.Contains("pan right"))
                return "Pan Right";
            if (v.Contains("velocity punch") || v == "velocity" || v == "velocitypunch" || v.Contains("velocitypunch"))
                return "Velocity Punch";
            if (v.Contains("cinematic fade zoom"))
                return "Cinematic Fade Zoom";
            if (v.Contains("glitch distort"))
                return "Glitch Distort";
            if (v.Contains("3d spin"))
                return "3D Spin Lite";
            if (v.Contains("trend zoom flash"))
                return "Trend Zoom Flash";
            if (v.Contains("trend blur pulse"))
                return "Trend Blur Pulse";
            if (v.Contains("trend spin glitch"))
                return "Trend Spin Glitch";
            if (v.Contains("digicam") || v.Contains("retro camera") || v.Contains("camera memory"))
                return "Digicam Memory";
            if (v.Contains("polaroid") || v.Contains("scrapbook") || v.Contains("photo album"))
                return "Polaroid Scrapbook";
            if (v.Contains("photo dump") || v.Contains("photodump") || v.Contains("recap dump") || v.Contains("beat dump"))
                return "Beat Photo Dump";
            if (v.Contains("film strip") || v.Contains("retro film") || v == "reel" || v.Contains("reel "))
                return "Film Strip";
            if (v.Contains("magazine cover") || v == "magazine" || v.Contains("editorial"))
                return "Magazine Cover";
            return "None";
        }

        internal static string NormalizeMusicSyncModeName(string? input)
        {
            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (v == "beat" || v == "beatsync" || v == "beat-sync" || v == "sync")
                return "beat";
            return "off";
        }

        internal static string NormalizeColorFilterName(string? input)
        {
            string value = (input ?? string.Empty).Trim().ToLowerInvariant();

            return value switch
            {
                "spring" => "Spring",
                "autumn" => "Autumn",
                "winter" => "Winter",
                "cold" => "Cold",
                "warm" => "Warm",
                "cinematic" => "Cinematic",
                "vintage" => "Vintage",
                "pastel" => "Pastel",
                "cyberpunk" => "Cyberpunk",
                "b&w pro" => "B&W Pro",
                "bw pro" => "B&W Pro",
                "none" => "None (Original)",
                "original" => "None (Original)",
                "none (original)" => "None (Original)",
                _ => "None (Original)"
            };
        }

        internal static string NormalizeSoundBarAudioSource(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "auto";

            return raw.Trim().ToLowerInvariant() switch
            {
                "external" or "ext" => "external",
                "audio1" or "track1" or "a1" => "audio1",
                "audio2" or "track2" or "a2" or "voice" or "vocal" => "audio2",
                "video" or "source" or "internal" or "original" => "video",
                _ => "auto"
            };
        }

        internal static double NormalizeSoundBarFraction(double? raw, double fallback, double min = 0.0)
        {
            if (!raw.HasValue || double.IsNaN(raw.Value) || double.IsInfinity(raw.Value))
                return fallback;

            double value = raw.Value;
            if (value > 1.0)
                value /= 100.0;

            return Math.Clamp(value, min, 1.0);
        }
    }
}
