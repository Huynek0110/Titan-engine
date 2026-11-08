using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TitanEngine
{
    public sealed class LightLeakOverlayAsset
    {
        public string Id { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string Type { get; set; } = "blackBackground";
        public string BlendMode { get; set; } = "screen";
        public double DefaultOpacity { get; set; } = 0.14;
        public double RecommendedDuration { get; set; } = 0.75;
        public bool CanLoop { get; set; } = true;
        public string Intensity { get; set; } = "medium";
        public string Category { get; set; } = "filmBurn";
        public bool LikelyColorWash { get; set; } = true;
        public double PreferredSeekSeconds { get; set; }
        public string DefaultTimingMode { get; set; } = "burst";
        public double DefaultFadeInSeconds { get; set; } = 0.08;
        public double DefaultFadeOutSeconds { get; set; } = 0.22;
        public string DefaultPlacement { get; set; } = "fullFrameTransitionOnly";
    }

    public sealed class LightLeakOverlayUsage
    {
        public double StartSeconds { get; set; }
        public double DurationSeconds { get; set; }
        public double FadeInSeconds { get; set; }
        public double FadeOutSeconds { get; set; }
        public double Opacity { get; set; }
        public string BlendMode { get; set; } = "screen";
        public double AssetStartSeconds { get; set; }
        public string Placement { get; set; } = "fullFrameTransitionOnly";
        public bool MirrorHorizontally { get; set; }
        public double EdgeCoverage { get; set; } = 1.0;
    }

    public sealed class LightLeakOverlayRecipe
    {
        public string Mode { get; set; } = "burst";
        public LightLeakOverlayAsset Asset { get; set; } = new LightLeakOverlayAsset();
        public List<LightLeakOverlayUsage> Usages { get; set; } = new List<LightLeakOverlayUsage>();
        public OverlayAssetBackgroundAnalysis? RuntimeAnalysis { get; set; }
        public bool UsesAlphaOverlay => Asset.Type.Equals("alpha", StringComparison.OrdinalIgnoreCase);
    }

    public static class LightLeakOverlayPreset
    {
        private const string PresetId = "FilmBurnWarm";
        private const string AssetFileName = "light_leak_warm_01.mp4";
        private const string AssetType = "blackBackground";
        private const string BlendMode = "screen";
        private const double DefaultOpacity = 0.14;
        private const double DefaultStartSeconds = 0.80;
        private const double DefaultDurationSeconds = 0.75;
        private const double DefaultFadeInSeconds = 0.08;
        private const double DefaultFadeOutSeconds = 0.22;

        public static string NormalizeMode(string? input) => "burst";

        public static string NormalizeAssetId(string? input) => PresetId;

        public static LightLeakOverlayRecipe? ResolveRecipe(
            RenderJob job,
            double outputDurationSeconds,
            Action<string> onLog)
        {
            string preferredPath = BuildPreferredPath();
            string? assetPath = ResolveAssetPath();
            bool exists = !string.IsNullOrWhiteSpace(assetPath) && File.Exists(assetPath);

            onLog($"[LIGHT-LEAK-ASSET] Selected preset: {PresetId}");
            onLog($"[LIGHT-LEAK-ASSET] Asset path: {assetPath ?? preferredPath}");
            onLog($"[LIGHT-LEAK-ASSET] Asset exists: {exists}");
            onLog($"[LIGHT-LEAK-ASSET] Asset type: {AssetType}");
            onLog($"[LIGHT-LEAK-ASSET] Blend mode: {BlendMode}");

            if (!exists || string.IsNullOrWhiteSpace(assetPath))
                return null;

            double opacity = ResolveOpacity(job, onLog);
            double safeDuration = Math.Max(0.10, outputDurationSeconds);
            LightLeakOverlayUsage usage = BuildUsage(safeDuration, opacity);

            onLog($"[LIGHT-LEAK-ASSET] Opacity: {usage.Opacity.ToString("0.000", CultureInfo.InvariantCulture)}");
            onLog($"[LIGHT-LEAK-ASSET] Duration: {usage.DurationSeconds.ToString("0.000", CultureInfo.InvariantCulture)}s");
            onLog(
                $"[LIGHT-LEAK-ASSET] Burst window: start={usage.StartSeconds.ToString("0.000", CultureInfo.InvariantCulture)}s, " +
                $"fadeIn={usage.FadeInSeconds.ToString("0.000", CultureInfo.InvariantCulture)}s, " +
                $"fadeOut={usage.FadeOutSeconds.ToString("0.000", CultureInfo.InvariantCulture)}s");

            return new LightLeakOverlayRecipe
            {
                Mode = "burst",
                Asset = new LightLeakOverlayAsset
                {
                    Id = PresetId,
                    FilePath = assetPath,
                    Type = AssetType,
                    BlendMode = BlendMode,
                    DefaultOpacity = DefaultOpacity,
                    RecommendedDuration = DefaultDurationSeconds,
                    CanLoop = true,
                    Intensity = "medium",
                    Category = "filmBurn",
                    LikelyColorWash = true,
                    PreferredSeekSeconds = 8.0,
                    DefaultTimingMode = "burst",
                    DefaultFadeInSeconds = DefaultFadeInSeconds,
                    DefaultFadeOutSeconds = DefaultFadeOutSeconds,
                    DefaultPlacement = "fullFrameTransitionOnly"
                },
                Usages = new List<LightLeakOverlayUsage> { usage }
            };
        }

        public static string BuildSingleClipGraph(
            string baseInputLabel,
            string baseVideoFilters,
            string overlayInputLabel,
            LightLeakOverlayRecipe recipe,
            int canvasWidth,
            int canvasHeight,
            double frameRate,
            double outputDurationSeconds,
            string outputLabel,
            Action<string> onLog)
        {
            if (recipe == null)
                throw new ArgumentNullException(nameof(recipe));

            LightLeakOverlayUsage usage = recipe.Usages.FirstOrDefault() ?? BuildUsage(Math.Max(0.10, outputDurationSeconds), recipe.Asset.DefaultOpacity);
            string safeBaseInput = string.IsNullOrWhiteSpace(baseInputLabel) ? "[0:v]" : baseInputLabel;
            string safeOverlayInput = string.IsNullOrWhiteSpace(overlayInputLabel) ? "[1:v]" : overlayInputLabel;
            string safeOutput = string.IsNullOrWhiteSpace(outputLabel) ? "lightleakout" : outputLabel;
            int safeWidth = Math.Max(2, canvasWidth);
            int safeHeight = Math.Max(2, canvasHeight);
            double safeFps = Math.Clamp(frameRate, 10.0, 120.0);
            double safeOutputDuration = Math.Max(0.10, outputDurationSeconds);
            double safeStart = Math.Clamp(usage.StartSeconds, 0.0, Math.Max(0.0, safeOutputDuration - 0.02));
            double safeUsageDuration = Math.Clamp(usage.DurationSeconds, 0.10, Math.Max(0.10, safeOutputDuration - safeStart));
            double safeEnd = Math.Min(safeOutputDuration, safeStart + safeUsageDuration);
            double safeFadeIn = Math.Clamp(usage.FadeInSeconds, 0.0, safeUsageDuration);
            double remainingAfterFadeIn = Math.Max(0.0, safeUsageDuration - safeFadeIn);
            double safeFadeOut = Math.Clamp(usage.FadeOutSeconds, 0.0, remainingAfterFadeIn);
            double fadeOutStart = Math.Max(0.0, safeUsageDuration - safeFadeOut);
            var inv = CultureInfo.InvariantCulture;

            OverlayBackgroundType backgroundType = recipe.RuntimeAnalysis?.BackgroundType ?? ResolveBackgroundType(recipe.Asset.Type);
            bool useAlphaOverlay = backgroundType == OverlayBackgroundType.Alpha || backgroundType == OverlayBackgroundType.GreenScreen;
            double safeOpacityValue = ResolveRuntimeOpacity(recipe, usage.Opacity, backgroundType);
            string processingFormat = useAlphaOverlay ? "rgba" : "gbrp";
            string baseChain = string.IsNullOrWhiteSpace(baseVideoFilters)
                ? $"{safeBaseInput}format={processingFormat}[llbase]"
                : $"{safeBaseInput}{baseVideoFilters},format={processingFormat}[llbase]";

            string fadeChain = BuildFadeChain(useAlphaOverlay, safeFadeIn, safeFadeOut, fadeOutStart, inv);
            string sourceChain =
                $"{safeOverlayInput}fps={safeFps.ToString("F3", inv)}," +
                $"scale={safeWidth}:{safeHeight}:force_original_aspect_ratio=increase," +
                $"crop={safeWidth}:{safeHeight},setsar=1,format={processingFormat}," +
                $"trim=start={Math.Max(0.0, usage.AssetStartSeconds).ToString("F3", inv)}:duration={safeUsageDuration.ToString("F3", inv)}{fadeChain}" +
                $"setpts=PTS-STARTPTS+{safeStart.ToString("F3", inv)}/TB[llasset]";

            string mergeChain;
            if (backgroundType == OverlayBackgroundType.Alpha)
            {
                mergeChain =
                    $"[llasset]colorchannelmixer=aa={safeOpacityValue.ToString("F3", inv)}[llalpha];" +
                    $"[llbase][llalpha]overlay=0:0:shortest=0:eof_action=pass:enable='between(t,{safeStart.ToString("F3", inv)},{safeEnd.ToString("F3", inv)})'[{safeOutput}]";
            }
            else if (backgroundType == OverlayBackgroundType.GreenScreen)
            {
                mergeChain =
                    $"[llasset]chromakey={ResolveKeyColor(recipe.RuntimeAnalysis)}:{ResolveSimilarity(recipe.RuntimeAnalysis).ToString("F3", inv)}:{ResolveBlend(recipe.RuntimeAnalysis).ToString("F3", inv)}," +
                    $"format=rgba,colorchannelmixer=aa={safeOpacityValue.ToString("F3", inv)}[llkey];" +
                    $"[llbase][llkey]overlay=0:0:shortest=0:eof_action=pass:enable='between(t,{safeStart.ToString("F3", inv)},{safeEnd.ToString("F3", inv)})'[{safeOutput}]";
            }
            else
            {
                mergeChain =
                    $"[llbase][llasset]blend=all_mode={ResolveBlendMode(usage.BlendMode)}:" +
                    $"all_opacity={safeOpacityValue.ToString("F3", inv)}:" +
                    $"enable='between(t,{safeStart.ToString("F3", inv)},{safeEnd.ToString("F3", inv)})'[{safeOutput}]";
            }

            onLog(
                $"[LIGHT-LEAK-OVERLAY-WINDOW] preset={PresetId} start={safeStart:F2}s duration={safeUsageDuration:F2}s " +
                $"fadeIn={safeFadeIn:F2}s fadeOut={safeFadeOut:F2}s opacity={safeOpacityValue:F2} blend={usage.BlendMode}");
            onLog(
                $"[LIGHT-LEAK-OVERLAY] Asset overlay graph ready: preset={PresetId}, background={backgroundType}, pipeline={recipe.RuntimeAnalysis?.PipelineName ?? "legacy"}, " +
                $"blend={recipe.Asset.BlendMode}, canvas={safeWidth}x{safeHeight}, fps={safeFps:F2}");

            return $"{baseChain};{sourceChain};{mergeChain}";
        }

        private static OverlayBackgroundType ResolveBackgroundType(string? assetType)
        {
            string value = (assetType ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Contains("alpha"))
                return OverlayBackgroundType.Alpha;
            if (value.Contains("green"))
                return OverlayBackgroundType.GreenScreen;
            if (value.Contains("black"))
                return OverlayBackgroundType.BlackBackground;
            return OverlayBackgroundType.Unknown;
        }

        private static double ResolveRuntimeOpacity(LightLeakOverlayRecipe recipe, double requestedOpacity, OverlayBackgroundType backgroundType)
        {
            double opacity = Math.Clamp(requestedOpacity, 0.0, 1.0);
            return backgroundType == OverlayBackgroundType.Unknown
                ? Math.Clamp(recipe.RuntimeAnalysis?.OpacityOverride ?? opacity, 0.08, 0.12)
                : opacity;
        }

        private static string ResolveKeyColor(OverlayAssetBackgroundAnalysis? analysis) => analysis?.KeyColor ?? "0x00FF00";
        private static double ResolveSimilarity(OverlayAssetBackgroundAnalysis? analysis) => Math.Clamp(analysis?.Similarity ?? 0.18, 0.08, 0.35);
        private static double ResolveBlend(OverlayAssetBackgroundAnalysis? analysis) => Math.Clamp(analysis?.Blend ?? 0.08, 0.00, 0.20);

        private static LightLeakOverlayUsage BuildUsage(double outputDurationSeconds, double opacity)
        {
            double start = Math.Min(DefaultStartSeconds, Math.Max(0.0, outputDurationSeconds - 0.10));
            double duration = Math.Min(DefaultDurationSeconds, Math.Max(0.10, outputDurationSeconds - start));
            double fadeIn = Math.Min(DefaultFadeInSeconds, duration);
            double fadeOut = Math.Min(DefaultFadeOutSeconds, Math.Max(0.0, duration - fadeIn));

            return new LightLeakOverlayUsage
            {
                StartSeconds = start,
                DurationSeconds = duration,
                FadeInSeconds = fadeIn,
                FadeOutSeconds = fadeOut,
                Opacity = opacity,
                BlendMode = BlendMode,
                AssetStartSeconds = 8.0,
                Placement = "fullFrameTransitionOnly",
                MirrorHorizontally = false,
                EdgeCoverage = 1.0
            };
        }

        private static double ResolveOpacity(RenderJob job, Action<string> onLog)
        {
            double opacity = job.LightLeakOverlayOpacity >= 0.0
                ? Math.Clamp(job.LightLeakOverlayOpacity, 0.0, 1.0)
                : DefaultOpacity;

            if (opacity > DefaultOpacity)
            {
                opacity = DefaultOpacity;
                onLog("Light leak asset looks like full-frame tint; opacity clamped.");
            }

            return opacity;
        }

        private static string ResolveBlendMode(string? input)
        {
            string value = (input ?? string.Empty).Trim().ToLowerInvariant();
            return value switch
            {
                "lighten" => "lighten",
                _ => "screen"
            };
        }

        private static string BuildFadeChain(bool usesAlphaOverlay, double fadeInSeconds, double fadeOutSeconds, double fadeOutStartSeconds, CultureInfo culture)
        {
            var filters = new List<string>();
            if (fadeInSeconds > 0.001)
            {
                filters.Add(usesAlphaOverlay
                    ? $"fade=t=in:st=0:d={fadeInSeconds.ToString("F3", culture)}:alpha=1"
                    : $"fade=t=in:st=0:d={fadeInSeconds.ToString("F3", culture)}");
            }

            if (fadeOutSeconds > 0.001)
            {
                filters.Add(usesAlphaOverlay
                    ? $"fade=t=out:st={fadeOutStartSeconds.ToString("F3", culture)}:d={fadeOutSeconds.ToString("F3", culture)}:alpha=1"
                    : $"fade=t=out:st={fadeOutStartSeconds.ToString("F3", culture)}:d={fadeOutSeconds.ToString("F3", culture)}");
            }

            return filters.Count == 0 ? "," : "," + string.Join(",", filters) + ",";
        }

        private static string? ResolveAssetPath()
        {
            foreach (string root in EnumerateAssetRoots())
            {
                if (!Directory.Exists(root))
                    continue;

                string candidate = Path.Combine(root, AssetFileName);
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private static string BuildPreferredPath()
        {
            string root = EnumerateAssetRoots().FirstOrDefault() ??
                          Path.Combine(Environment.CurrentDirectory, "Assets", "Overlays", "LightLeaks");
            return Path.Combine(root, AssetFileName);
        }

        private static IEnumerable<string> EnumerateAssetRoots()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            yield return Path.Combine(baseDir, "Assets", "Overlays", "LightLeaks");

            string? parent = Directory.GetParent(baseDir)?.FullName;
            for (int i = 0; i < 5 && !string.IsNullOrWhiteSpace(parent); i++)
            {
                yield return Path.Combine(parent, "Assets", "Overlays", "LightLeaks");
                parent = Directory.GetParent(parent)?.FullName;
            }

            yield return Path.Combine(Environment.CurrentDirectory, "Assets", "Overlays", "LightLeaks");
            yield return Path.Combine(Environment.CurrentDirectory, "TitanEngine", "Assets", "Overlays", "LightLeaks");
        }
    }
}
