using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TitanEngine
{
    public sealed class RainOverlayAsset
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string AssetType { get; set; } = "blackBackground";
        public string Category { get; set; } = "heavyRain";
        public string BlendMode { get; set; } = "screen";
        public double DefaultOpacity { get; set; } = 0.24;
        public bool CanLoop { get; set; } = true;
        public double RecommendedDuration { get; set; } = 6.0;
        public string SourceName { get; set; } = string.Empty;
        public string LicenseNote { get; set; } = string.Empty;
    }

    public sealed class RainOverlayRecipe
    {
        public string Preset { get; set; } = "Heavy Rain";
        public string Intensity { get; set; } = "medium";
        public RainOverlayAsset Asset { get; set; } = new RainOverlayAsset();
        public double Opacity { get; set; } = 0.24;
        public OverlayAssetBackgroundAnalysis? RuntimeAnalysis { get; set; }
        public bool UsesAlphaOverlay => Asset.AssetType.Equals("alpha", StringComparison.OrdinalIgnoreCase);
    }

    public static class RainOverlayPreset
    {
        private sealed class RainOverlayAssetDefinition
        {
            public string Id { get; init; } = string.Empty;
            public string Name { get; init; } = string.Empty;
            public string AssetType { get; init; } = "blackBackground";
            public string Category { get; init; } = "heavyRain";
            public string BlendMode { get; init; } = "screen";
            public double DefaultOpacity { get; init; } = 0.24;
            public bool CanLoop { get; init; } = true;
            public double RecommendedDuration { get; init; } = 6.0;
            public string SourceName { get; init; } = string.Empty;
            public string LicenseNote { get; init; } = string.Empty;
            public IReadOnlyList<string> FileHints { get; init; } = Array.Empty<string>();
            public IReadOnlyList<string> SourceUrls { get; init; } = Array.Empty<string>();
        }

        private static readonly IReadOnlyList<RainOverlayAssetDefinition> KnownAssets = new[]
        {
            new RainOverlayAssetDefinition
            {
                Id = "heavy_rain_black_bg_01",
                Name = "Heavy Rain Black BG",
                AssetType = "blackBackground",
                Category = "heavyRain",
                BlendMode = "screen",
                DefaultOpacity = 0.28,
                CanLoop = true,
                RecommendedDuration = 8.0,
                SourceName = "Pexels",
                LicenseNote = "Pexels free photo and video license.",
                FileHints = new[] { "rain_heavy_black_bg_01.mp4", "heavy_rain_black_bg_01.mp4" },
                SourceUrls = new[] { "https://www.pexels.com/video/heavy-rain-falling-on-black-background-36344044/" }
            },
            new RainOverlayAssetDefinition
            {
                Id = "rain_cinematic_black_bg_01",
                Name = "Cinematic Rain Black BG",
                AssetType = "blackBackground",
                Category = "cinematicRain",
                BlendMode = "lighten",
                DefaultOpacity = 0.24,
                CanLoop = true,
                RecommendedDuration = 8.0,
                SourceName = "Pixabay or Pexels",
                LicenseNote = "Use a royalty-free rain overlay clip and keep the source note in README.",
                FileHints = new[] { "rain_cinematic_black_bg_01.mp4", "cinematic_rain_black_bg_01.mp4" },
                SourceUrls = new[]
                {
                    "https://pixabay.com/videos/rain-water-raining-nature-overlay-214990/",
                    "https://www.pexels.com/video/a-pouring-rain-7043616/"
                }
            },
            new RainOverlayAssetDefinition
            {
                Id = "rain_window_drops_01",
                Name = "Window Drops",
                AssetType = "blackBackground",
                Category = "windowDrops",
                BlendMode = "screen",
                DefaultOpacity = 0.20,
                CanLoop = true,
                RecommendedDuration = 8.0,
                SourceName = "Pexels",
                LicenseNote = "Pexels free photo and video license.",
                FileHints = new[] { "rain_window_drops_01.mp4", "window_drops_01.mp4" },
                SourceUrls = new[] { "https://www.pexels.com/video/rain-drops-streaming-down-a-window-pane-35184743/" }
            }
        };

        public static string NormalizePreset(string? input)
        {
            string value = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(value))
                return "Heavy Rain";

            if (value.Contains("window"))
                return "Window Drops";
            if (value.Contains("cinematic"))
                return "Cinematic Rain";
            if (value.Contains("heavy"))
                return "Heavy Rain";
            if (value.Contains("rain"))
                return "Heavy Rain";

            return "Heavy Rain";
        }

        public static string NormalizeIntensity(string? input)
        {
            string value = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Contains("strong"))
                return "strong";
            if (value.Contains("subtle") || value.Contains("soft") || value.Contains("low"))
                return "subtle";
            if (value.Contains("medium") || value.Contains("mid") || value.Contains("normal"))
                return "medium";
            return "medium";
        }

        public static RainOverlayRecipe? ResolveRecipe(RenderJob job, Action<string> onLog)
        {
            string preset = NormalizePreset(job.RainOverlayPreset);
            string intensity = NormalizeIntensity(job.RainOverlayIntensity);
            RainOverlayAsset? asset = ResolveAsset(job, preset, onLog);
            if (asset == null)
                return null;

            double opacity = ResolveOpacity(asset, intensity);
            onLog($"[RAIN-OVERLAY] Preset={preset}, intensity={intensity}, blend={asset.BlendMode}, opacity={opacity:0.00}");
            return new RainOverlayRecipe
            {
                Preset = preset,
                Intensity = intensity,
                Asset = asset,
                Opacity = opacity
            };
        }

        public static string BuildSingleClipGraph(
            string baseInputLabel,
            string baseVideoFilters,
            string overlayInputLabel,
            RainOverlayRecipe recipe,
            int canvasWidth,
            int canvasHeight,
            double frameRate,
            double outputDurationSeconds,
            string outputLabel,
            Action<string> onLog)
        {
            if (recipe == null)
                throw new ArgumentNullException(nameof(recipe));

            var inv = CultureInfo.InvariantCulture;
            string safeBaseInput = string.IsNullOrWhiteSpace(baseInputLabel) ? "[0:v]" : baseInputLabel;
            string safeOverlayInput = string.IsNullOrWhiteSpace(overlayInputLabel) ? "[1:v]" : overlayInputLabel;
            string safeOutput = string.IsNullOrWhiteSpace(outputLabel) ? "rainout" : outputLabel;
            int safeWidth = Math.Max(2, canvasWidth);
            int safeHeight = Math.Max(2, canvasHeight);
            double safeFps = Math.Clamp(frameRate, 10.0, 120.0);
            double safeDuration = Math.Max(0.10, outputDurationSeconds);

            OverlayBackgroundType backgroundType = recipe.RuntimeAnalysis?.BackgroundType ?? ResolveBackgroundType(recipe.Asset.AssetType);
            bool useAlphaOverlay = backgroundType == OverlayBackgroundType.Alpha || backgroundType == OverlayBackgroundType.GreenScreen;
            double safeOpacity = ResolveRuntimeOpacity(recipe, backgroundType);
            string baseFormat = useAlphaOverlay ? "rgba" : "gbrp";
            string baseChain = string.IsNullOrWhiteSpace(baseVideoFilters)
                ? $"{safeBaseInput}format={baseFormat}[rainbase]"
                : $"{safeBaseInput}{baseVideoFilters},format={baseFormat}[rainbase]";

            string rainFormat = useAlphaOverlay ? "rgba" : "gbrp";
            string rainLayerFilters = BuildRainLayerFilters(recipe);
            string sourceChain =
                $"{safeOverlayInput}fps={safeFps.ToString("F3", inv)}," +
                $"scale={safeWidth}:{safeHeight}:force_original_aspect_ratio=increase," +
                $"crop={safeWidth}:{safeHeight}," +
                $"setsar=1,format={rainFormat}," +
                $"{rainLayerFilters}" +
                $"trim=duration={safeDuration.ToString("F3", inv)},setpts=PTS-STARTPTS[rainsrc]";

            string mergeChain = backgroundType switch
            {
                OverlayBackgroundType.Alpha =>
                    $"[rainsrc]colorchannelmixer=aa={safeOpacity.ToString("F3", inv)}[rainalpha];[rainbase][rainalpha]overlay=0:0:shortest=0:eof_action=repeat:format=auto[{safeOutput}]",
                OverlayBackgroundType.GreenScreen =>
                    $"[rainsrc]chromakey={ResolveKeyColor(recipe.RuntimeAnalysis)}:{ResolveSimilarity(recipe.RuntimeAnalysis).ToString("F3", inv)}:{ResolveBlend(recipe.RuntimeAnalysis).ToString("F3", inv)}," +
                    $"format=rgba,colorchannelmixer=aa={safeOpacity.ToString("F3", inv)}[rainkey];[rainbase][rainkey]overlay=0:0:shortest=0:eof_action=repeat:format=auto[{safeOutput}]",
                _ =>
                    $"[rainbase][rainsrc]blend=all_mode={ResolveBlendMode(recipe.Asset.BlendMode)}:all_opacity={safeOpacity.ToString("F3", inv)}[{safeOutput}]"
            };

            onLog($"[RAIN-OVERLAY] Graph ready: background={backgroundType}, pipeline={recipe.RuntimeAnalysis?.PipelineName ?? "legacy"}, blend={recipe.Asset.BlendMode}, opacity={safeOpacity:0.000}, canvas={safeWidth}x{safeHeight}, fps={safeFps:F2}, preset={recipe.Preset}");
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

        private static double ResolveRuntimeOpacity(RainOverlayRecipe recipe, OverlayBackgroundType backgroundType)
        {
            double opacity = Math.Clamp(recipe.Opacity, 0.0, 1.0);
            return backgroundType == OverlayBackgroundType.Unknown
                ? Math.Clamp(recipe.RuntimeAnalysis?.OpacityOverride ?? opacity, 0.08, 0.12)
                : opacity;
        }

        private static string ResolveKeyColor(OverlayAssetBackgroundAnalysis? analysis) => analysis?.KeyColor ?? "0x00FF00";
        private static double ResolveSimilarity(OverlayAssetBackgroundAnalysis? analysis) => Math.Clamp(analysis?.Similarity ?? 0.18, 0.08, 0.35);
        private static double ResolveBlend(OverlayAssetBackgroundAnalysis? analysis) => Math.Clamp(analysis?.Blend ?? 0.08, 0.00, 0.20);

        private static RainOverlayAsset? ResolveAsset(RenderJob job, string preset, Action<string> onLog)
        {
            if (!string.IsNullOrWhiteSpace(job.RainOverlayAssetPath))
            {
                string customPath = Path.GetFullPath(job.RainOverlayAssetPath.Trim());
                string customId = string.IsNullOrWhiteSpace(job.RainOverlayPreset) ? "custom_rain_overlay" : job.RainOverlayPreset.Trim();
                onLog($"[RAIN-ASSET] Selected rain asset id: {customId}");
                onLog($"[RAIN-ASSET] Selected rain asset path: {customPath}");
                onLog($"[RAIN-ASSET] Asset exists: {File.Exists(customPath)}");
                onLog($"[RAIN-ASSET] Asset type: {InferAssetType(customPath)}");
                if (File.Exists(customPath))
                {
                    return new RainOverlayAsset
                    {
                        Id = customId,
                        Name = customId,
                        FilePath = customPath,
                        AssetType = InferAssetType(customPath),
                        Category = ResolveCategoryFromPreset(preset),
                        BlendMode = InferBlendMode(customPath),
                        DefaultOpacity = 0.24,
                        CanLoop = true,
                        RecommendedDuration = 8.0,
                        SourceName = "Custom",
                        LicenseNote = "User-supplied asset."
                    };
                }

                return null;
            }

            RainOverlayAssetDefinition definition = SelectDefinition(preset);
            string? resolvedPath = ResolveDefinitionPath(definition);
            string preferredPath = BuildPreferredPath(definition);
            onLog($"[RAIN-ASSET] Selected rain asset id: {definition.Id}");
            onLog($"[RAIN-ASSET] Selected rain asset path: {(resolvedPath ?? preferredPath)}");
            onLog($"[RAIN-ASSET] Asset exists: {!string.IsNullOrWhiteSpace(resolvedPath)}");
            onLog($"[RAIN-ASSET] Asset type: {definition.AssetType}");
            if (string.IsNullOrWhiteSpace(resolvedPath))
                return null;

            return new RainOverlayAsset
            {
                Id = definition.Id,
                Name = definition.Name,
                FilePath = resolvedPath,
                AssetType = definition.AssetType,
                Category = definition.Category,
                BlendMode = definition.BlendMode,
                DefaultOpacity = definition.DefaultOpacity,
                CanLoop = definition.CanLoop,
                RecommendedDuration = definition.RecommendedDuration,
                SourceName = definition.SourceName,
                LicenseNote = definition.LicenseNote
            };
        }

        private static RainOverlayAssetDefinition SelectDefinition(string preset)
        {
            return preset switch
            {
                "Cinematic Rain" => KnownAssets.First(a => a.Id == "rain_cinematic_black_bg_01"),
                "Window Drops" => KnownAssets.First(a => a.Id == "rain_window_drops_01"),
                _ => KnownAssets.First(a => a.Id == "heavy_rain_black_bg_01")
            };
        }

        private static string? ResolveDefinitionPath(RainOverlayAssetDefinition definition)
        {
            foreach (string root in EnumerateAssetRoots())
            {
                if (!Directory.Exists(root))
                    continue;

                foreach (string fileHint in definition.FileHints)
                {
                    string exact = Path.Combine(root, fileHint);
                    if (File.Exists(exact))
                        return exact;
                }
            }

            return null;
        }

        private static string BuildPreferredPath(RainOverlayAssetDefinition definition)
        {
            string root = EnumerateAssetRoots().FirstOrDefault() ?? Path.Combine(Environment.CurrentDirectory, "Assets", "Overlays", "Rain");
            return Path.Combine(root, definition.FileHints.FirstOrDefault() ?? $"{definition.Id}.mp4");
        }

        private static IEnumerable<string> EnumerateAssetRoots()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            yield return Path.Combine(baseDir, "Assets", "Overlays", "Rain");
            yield return Path.Combine(baseDir, "Content", "Overlays", "Rain");

            string? parent = Directory.GetParent(baseDir)?.FullName;
            for (int i = 0; i < 5 && !string.IsNullOrWhiteSpace(parent); i++)
            {
                yield return Path.Combine(parent, "Assets", "Overlays", "Rain");
                yield return Path.Combine(parent, "Content", "Overlays", "Rain");
                parent = Directory.GetParent(parent)?.FullName;
            }

            yield return Path.Combine(Environment.CurrentDirectory, "Assets", "Overlays", "Rain");
            yield return Path.Combine(Environment.CurrentDirectory, "Content", "Overlays", "Rain");
            yield return Path.Combine(Environment.CurrentDirectory, "TitanEngine", "Assets", "Overlays", "Rain");
            yield return Path.Combine(Environment.CurrentDirectory, "TitanEngine", "Content", "Overlays", "Rain");
        }

        private static string ResolveCategoryFromPreset(string preset)
        {
            return preset switch
            {
                "Cinematic Rain" => "cinematicRain",
                "Window Drops" => "windowDrops",
                _ => "heavyRain"
            };
        }

        private static string InferAssetType(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string fileName = Path.GetFileName(path).ToLowerInvariant();
            if (ext is ".webm" or ".mov" || fileName.Contains("alpha"))
                return "alpha";
            return "blackBackground";
        }

        private static string InferBlendMode(string path)
            => InferAssetType(path).Equals("alpha", StringComparison.OrdinalIgnoreCase) ? "overlay" : "screen";

        private static double ResolveOpacity(RainOverlayAsset asset, string intensity)
        {
            bool isCinematicRain = asset.Category.Equals("cinematicRain", StringComparison.OrdinalIgnoreCase);

            if (isCinematicRain)
            {
                return intensity switch
                {
                    "subtle" => Math.Clamp(asset.DefaultOpacity - 0.06, 0.12, 0.18),
                    "strong" => Math.Clamp(asset.DefaultOpacity + 0.08, 0.28, 0.36),
                    _ => Math.Clamp(asset.DefaultOpacity, 0.18, 0.26)
                };
            }

            return intensity switch
            {
                "subtle" => Math.Clamp(asset.DefaultOpacity - 0.08, 0.12, 0.18),
                "strong" => Math.Clamp(asset.DefaultOpacity + 0.10, 0.32, 0.45),
                _ => Math.Clamp(asset.DefaultOpacity, 0.20, 0.30)
            };
        }

        private static string BuildRainLayerFilters(RainOverlayRecipe recipe)
        {
            if (recipe.Preset.Equals("Cinematic Rain", StringComparison.OrdinalIgnoreCase))
            {
                return "tmix=frames=3:weights='1 2 1',gblur=sigma=0.55:steps=1,eq=contrast=1.06:brightness=0.020,";
            }

            if (recipe.Preset.Equals("Window Drops", StringComparison.OrdinalIgnoreCase))
            {
                return "gblur=sigma=0.30:steps=1,";
            }

            return string.Empty;
        }

        private static string ResolveBlendMode(string blendMode)
        {
            string normalized = (blendMode ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "lighten" => "lighten",
                "add" or "addition" => "addition",
                "overlay" => "overlay",
                _ => "screen"
            };
        }
    }
}
