using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TitanEngine
{
    public sealed class SnowOverlayAsset
    {
        public string FilePath { get; set; } = string.Empty;
        public string Type { get; set; } = "alpha";
        public string BlendMode { get; set; } = "screen";
        public bool CanLoop { get; set; } = true;
    }

    public sealed class SnowOverlayLayerRecipe
    {
        public string Name { get; set; } = "far";
        public int Count { get; set; }
        public double MinSize { get; set; }
        public double MaxSize { get; set; }
        public double MinOpacity { get; set; }
        public double MaxOpacity { get; set; }
        public double MinVerticalLoops { get; set; }
        public double MaxVerticalLoops { get; set; }
        public double MinSway { get; set; }
        public double MaxSway { get; set; }
        public double MinDrift { get; set; }
        public double MaxDrift { get; set; }
        public bool UseGlow { get; set; }
        public bool UseStreaks { get; set; }
        public double BlurMultiplier { get; set; }
        public double StreakLengthMultiplier { get; set; }
    }

    public sealed class SnowfallOverlayRecipe
    {
        public string PresetName { get; set; } = "Snow Cinematic";
        public string Mode { get; set; } = "generatedOverlay";
        public double Opacity { get; set; } = 0.30;
        public double CycleSeconds { get; set; } = 4.0;
        public double FrameRate { get; set; } = 30.0;
        public SnowOverlayAsset? Asset { get; set; }
        public OverlayAssetBackgroundAnalysis? RuntimeAnalysis { get; set; }
        public List<SnowOverlayLayerRecipe> Layers { get; set; } = new List<SnowOverlayLayerRecipe>();
        public bool UseGeneratedOverlay => !Mode.Equals("preRenderedOverlay", StringComparison.OrdinalIgnoreCase) || Asset == null;
        public bool UsesAlphaOverlay => UseGeneratedOverlay || Asset?.Type.Equals("alpha", StringComparison.OrdinalIgnoreCase) == true;
        public bool IsIntroOverlay { get; set; } = false;
    }

    public sealed class SnowfallGeneratedOverlay
    {
        public string TempRoot { get; set; } = string.Empty;
        public string OverlayPattern { get; set; } = string.Empty;
        public int FrameCount { get; set; }
        public double FrameRate { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string PreviewDirectory { get; set; } = string.Empty;
        public List<string> TempArtifacts { get; set; } = new List<string>();
    }

    public static class SnowfallOverlayPreset
    {

        public static string NormalizeMode(string? input)
        {
            string value = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (value is "asset" or "videoasset" or "prerendered" or "prerenderedoverlay" or "pre-rendered" or "pre-rendered-overlay")
                return "preRenderedOverlay";
            if (value is "generated" or "generatedoverlay" or "particles" or "procedural")
                return "generatedOverlay";
            return "auto";
        }


        public static string NormalizePreset(string? input)
        {
            string value = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(value))
                return "Snow Cinematic";

            if (value.Contains("soft"))
                return "Snow Soft";
            if (value.Contains("bokeh"))
                return "Snow Bokeh";
            if (value.Contains("heavy"))
                return "Snow Heavy";
            if (value.Contains("wind"))
                return "Snow Windy";
            if (value.Contains("cinematic"))
                return "Snow Cinematic";
            if (value.Contains("snow"))
                return "Snow Cinematic";

            return "Snow Cinematic";
        }


        public static IReadOnlyList<SnowfallOverlayRecipe> ResolveRecipes(RenderJob job, double outputDurationSeconds, Action<string> onLog)
        {
            if (!job.EnableSnowOverlay1 && !job.EnableSnowOverlay2 && !job.EnableSnowOverlay3 && !job.EnableOverlayIntro)
                return new[] { ResolveRecipe(job, outputDurationSeconds, onLog) };

            double defaultOpacity = ResolveDefaultOpacity("Snow Cinematic");
            double sharedOpacity = job.SnowfallOpacity >= 0.0
                ? Math.Clamp(job.SnowfallOpacity, 0.05, 0.70)
                : defaultOpacity;
            double overlay1Opacity = job.SnowOverlay1Opacity >= 0.0
                ? Math.Clamp(job.SnowOverlay1Opacity, 0.05, 0.70)
                : sharedOpacity;
            double overlay2Opacity = job.SnowOverlay2Opacity >= 0.0
                ? Math.Clamp(job.SnowOverlay2Opacity, 0.05, 0.70)
                : sharedOpacity;
            double overlay3Opacity = job.SnowOverlay3Opacity >= 0.0
                ? Math.Clamp(job.SnowOverlay3Opacity, 0.05, 0.70)
                : sharedOpacity;
            double overlayIntroOpacity = job.OverlayIntroOpacity >= 0.0
                ? Math.Clamp(job.OverlayIntroOpacity, 0.05, 1.0)
                : sharedOpacity;

            var recipes = new List<SnowfallOverlayRecipe>();
            if (job.EnableSnowOverlay1)
            {
                recipes.Add(CreateNamedAssetRecipe("Overlay 1", "snow_1", overlay1Opacity, onLog));
            }

            if (job.EnableSnowOverlay2)
            {
                recipes.Add(CreateNamedAssetRecipe("Overlay 2", "snow_2", overlay2Opacity, onLog));
            }

            if (job.EnableSnowOverlay3)
            {
                recipes.Add(CreateNamedAssetRecipe("Overlay 3", "snow_3", overlay3Opacity, onLog));
            }

            if (job.EnableOverlayIntro)
            {
                var introRecipe = CreateNamedAssetRecipe("Overlay Intro", "intro", overlayIntroOpacity, onLog);
                introRecipe.IsIntroOverlay = true;
                recipes.Add(introRecipe);
            }

            if (recipes.Count >= 2 &&
                recipes[0]?.Asset is { FilePath: not null } asset1 &&
                recipes[1]?.Asset is { FilePath: not null } asset2 &&
                FilesLookIdentical(asset1.FilePath, asset2.FilePath))
            {
                onLog("[SNOWFALL-WARN] snow_1 and snow_2 resolve to identical media. Stacked output may look like only one overlay.");
            }

            onLog($"[SNOWFALL] UI snow overlays selected: {string.Join(", ", recipes.Select(r => $"{r.PresetName} ({r.Opacity:0.00})"))}");
            return recipes;
        }


        public static SnowfallOverlayRecipe ResolveRecipe(RenderJob job, double outputDurationSeconds, Action<string> onLog)
        {
            string presetName = NormalizePreset(job.SnowfallPreset);
            string requestedMode = NormalizeMode(job.SnowfallMode);
            double intensity = Math.Clamp(job.FxIntensity / 100.0, 0.0, 1.0);
            SnowOverlayAsset? asset = ResolveAsset(job);
            string mode = requestedMode == "auto"
                ? (asset == null ? "generatedOverlay" : "preRenderedOverlay")
                : requestedMode;

            if (mode.Equals("preRenderedOverlay", StringComparison.OrdinalIgnoreCase) && asset == null)
            {
                onLog("[SNOWFALL-WARN] No snow overlay asset found. Falling back to generated particle overlay.");
                mode = "generatedOverlay";
            }

            double defaultOpacity = ResolveDefaultOpacity(presetName);
            double opacity = job.SnowfallOpacity >= 0.0
                ? Math.Clamp(job.SnowfallOpacity, 0.05, 0.70)
                : defaultOpacity;

            var recipe = new SnowfallOverlayRecipe
            {
                PresetName = presetName,
                Mode = mode,
                Opacity = opacity,
                CycleSeconds = ResolveCycleSeconds(presetName, intensity, outputDurationSeconds),
                FrameRate = 30.0,
                Asset = asset,
                Layers = BuildLayers(presetName, intensity)
            };

            onLog($"[SNOWFALL] Preset={recipe.PresetName}, mode={recipe.Mode}, opacity={recipe.Opacity:0.00}, cycle={recipe.CycleSeconds:0.00}s, layers={recipe.Layers.Count}");
            return recipe;
        }


        private static SnowfallOverlayRecipe CreateNamedAssetRecipe(string presetName, string assetBaseName, double opacity, Action<string> onLog)
        {
            SnowOverlayAsset asset = ResolveUiNamedAsset(assetBaseName, onLog);
            var recipe = new SnowfallOverlayRecipe
            {
                PresetName = presetName,
                Mode = "preRenderedOverlay",
                Opacity = opacity,
                CycleSeconds = 4.0,
                FrameRate = 30.0,
                Asset = asset,
                Layers = new List<SnowOverlayLayerRecipe>()
            };

            onLog($"[SNOWFALL] {presetName} -> {asset.FilePath}");
            return recipe;
        }


        private static SnowOverlayAsset ResolveUiNamedAsset(string assetBaseName, Action<string> onLog)
        {
            var candidates = new List<string>();
            foreach (string overlayDir in EnumerateSnowAssetDirectories())
            {
                foreach (string ext in new[] { ".mp4", ".mov", ".webm" })
                {
                    string candidate = Path.Combine(overlayDir, assetBaseName + ext);
                    if (File.Exists(candidate))
                        candidates.Add(candidate);
                }
            }

            if (candidates.Count == 0)
                throw new InvalidOperationException($"Snow overlay asset missing: {assetBaseName}");

            string selectedCandidate = candidates
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ThenByDescending(info => info.Length)
                .Select(info => info.FullName)
                .First();

            if (candidates.Count > 1)
            {
                onLog($"[SNOWFALL] Multiple candidates found for {assetBaseName}: {string.Join(" | ", candidates)}");
            }

            onLog($"[SNOWFALL] Resolved named asset {assetBaseName} -> {selectedCandidate}");
            return new SnowOverlayAsset
            {
                FilePath = selectedCandidate,
                Type = GuessAssetType(selectedCandidate),
                BlendMode = GuessBlendMode(selectedCandidate),
                CanLoop = true
            };
        }


        private static IEnumerable<string> EnumerateSnowAssetDirectories()
        {
            string baseDirectory = AppContext.BaseDirectory;
            string currentDirectory = Environment.CurrentDirectory;
            string[] candidates =
            {
                Path.Combine(baseDirectory, "Assets", "Overlays", "Snow"),
                Path.Combine(currentDirectory, "Assets", "Overlays", "Snow"),
                Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "Assets", "Overlays", "Snow")),
                Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "Content", "Overlays", "Snow"))
            };

            return candidates
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists);
        }


        private static bool FilesLookIdentical(string firstPath, string secondPath)
        {
            try
            {
                var firstInfo = new FileInfo(firstPath);
                var secondInfo = new FileInfo(secondPath);
                if (!firstInfo.Exists || !secondInfo.Exists)
                    return false;

                if (firstInfo.Length != secondInfo.Length)
                    return false;

                using var sha = SHA256.Create();
                using var firstStream = File.OpenRead(firstPath);
                using var secondStream = File.OpenRead(secondPath);
                byte[] firstHash = sha.ComputeHash(firstStream);
                byte[] secondHash = sha.ComputeHash(secondStream);
                return firstHash.SequenceEqual(secondHash);
            }
            catch
            {
                return false;
            }
        }


        public static string BuildGeneratedClipGraph(
            string baseInputLabel,
            string baseVideoFilters,
            string overlayInputLabel,
            int loopFrameCount,
            double frameRate,
            double outputDurationSeconds,
            string outputLabel,
            Action<string> onLog)
        {
            var inv = CultureInfo.InvariantCulture;
            string safeBaseInput = string.IsNullOrWhiteSpace(baseInputLabel) ? "[0:v]" : baseInputLabel;
            string safeOverlayInput = string.IsNullOrWhiteSpace(overlayInputLabel) ? "[1:v]" : overlayInputLabel;
            string safeOutput = string.IsNullOrWhiteSpace(outputLabel) ? "snowout" : outputLabel;
            string fps = Math.Clamp(frameRate, 1.0, 120.0).ToString("F3", inv);
            string duration = Math.Max(0.10, outputDurationSeconds).ToString("F3", inv);
            int safeLoopFrameCount = Math.Max(1, loopFrameCount);

            string baseChain = string.IsNullOrWhiteSpace(baseVideoFilters)
                ? $"{safeBaseInput}format=rgba[snowbase]"
                : $"{safeBaseInput}{baseVideoFilters},format=rgba[snowbase]";

            string overlayChain =
                $"{safeOverlayInput}format=rgba," +
                $"loop=loop=-1:size={safeLoopFrameCount}:start=0," +
                $"setpts=N/{fps}/TB," +
                $"trim=duration={duration}[snowoverlay]";

            string mergeChain =
                $"[snowoverlay][snowbase]scale2ref=w=main_w:h=main_h:flags=lanczos[snowoverlayscaled][snowbase2];" +
                $"[snowbase2][snowoverlayscaled]overlay=shortest=0:eof_action=repeat:format=auto[{safeOutput}]";
            onLog($"[SNOWFALL] Generated overlay graph ready: frames={safeLoopFrameCount}, fps={fps}, duration={duration}");
            return $"{baseChain};{overlayChain};{mergeChain}";
        }


        public static string BuildAssetClipGraph(
            string baseInputLabel,
            string baseVideoFilters,
            string overlayInputLabel,
            SnowfallOverlayRecipe recipe,
            int canvasWidth,
            int canvasHeight,
            double frameRate,
            double outputDurationSeconds,
            string outputLabel,
            Action<string> onLog)
        {
            if (recipe.Asset == null)
                throw new InvalidOperationException("Snow asset graph requested without a resolved asset.");

            var inv = CultureInfo.InvariantCulture;
            string safeBaseInput = string.IsNullOrWhiteSpace(baseInputLabel) ? "[0:v]" : baseInputLabel;
            string safeOverlayInput = string.IsNullOrWhiteSpace(overlayInputLabel) ? "[1:v]" : overlayInputLabel;
            string safeOutput = string.IsNullOrWhiteSpace(outputLabel) ? "snowout" : outputLabel;
            int safeW = Math.Max(2, canvasWidth);
            int safeH = Math.Max(2, canvasHeight);
            double safeFps = Math.Clamp(frameRate, 10.0, 120.0);
            double safeDuration = Math.Max(0.10, outputDurationSeconds);

            OverlayBackgroundType assetTypeBackground = ResolveBackgroundType(recipe.Asset.Type);
            OverlayBackgroundType detectedBackgroundType = recipe.RuntimeAnalysis?.BackgroundType ?? OverlayBackgroundType.Unknown;
            bool usedAssetTypeFallback =
                detectedBackgroundType == OverlayBackgroundType.Unknown &&
                assetTypeBackground != OverlayBackgroundType.Unknown;
            OverlayBackgroundType backgroundType = usedAssetTypeFallback
                ? assetTypeBackground
                : (detectedBackgroundType != OverlayBackgroundType.Unknown
                    ? detectedBackgroundType
                    : assetTypeBackground);
            bool useAlphaOverlay = backgroundType == OverlayBackgroundType.Alpha || backgroundType == OverlayBackgroundType.GreenScreen;
            double safeOpacity = backgroundType == OverlayBackgroundType.Unknown
                ? Math.Clamp(recipe.RuntimeAnalysis?.OpacityOverride ?? recipe.Opacity, 0.08, 0.12)
                : Math.Clamp(recipe.Opacity, 0.0, 1.0);
            string processingFormat = useAlphaOverlay ? "rgba" : "gbrp";
            string baseChain = string.IsNullOrWhiteSpace(baseVideoFilters)
                ? $"{safeBaseInput}format={processingFormat}[snowbase]"
                : $"{safeBaseInput}{baseVideoFilters},format={processingFormat}[snowbase]";

            string sourceFormat = useAlphaOverlay ? "rgba" : "gbrp";
            string sourceChain =
                $"{safeOverlayInput}fps={safeFps.ToString("F3", inv)}," +
                $"scale={safeW}:{safeH}:force_original_aspect_ratio=increase," +
                $"crop={safeW}:{safeH},setsar=1,format={sourceFormat}," +
                $"trim=duration={safeDuration.ToString("F3", inv)}[snowasset]";

            // Delay intro by 0.05s (1-2 frames) so frame 0 remains clean for OS thumbnail extractors
            string enableExpr = recipe.IsIntroOverlay ? ":enable='between(t,0.05,1.55)'" : "";

            string mergeChain = backgroundType switch
            {
                OverlayBackgroundType.Alpha =>
                    $"[snowasset]colorchannelmixer=aa={safeOpacity.ToString("F3", inv)}[snowasseta];[snowbase][snowasseta]overlay=shortest=0:eof_action=repeat:format=auto{enableExpr}[{safeOutput}]",
                OverlayBackgroundType.GreenScreen =>
                    $"[snowasset]chromakey=0x00FF00:{Math.Clamp(recipe.RuntimeAnalysis?.Similarity ?? 0.18, 0.08, 0.35).ToString("F3", inv)}:{Math.Clamp(recipe.RuntimeAnalysis?.Blend ?? 0.08, 0.00, 0.20).ToString("F3", inv)}," +
                    $"format=rgba,colorchannelmixer=aa={safeOpacity.ToString("F3", inv)}[snowkey];[snowbase][snowkey]overlay=shortest=0:eof_action=repeat:format=auto{enableExpr}[{safeOutput}]",
                _ =>
                    $"[snowbase][snowasset]blend=all_mode={recipe.Asset.BlendMode}:all_opacity={safeOpacity.ToString("F3", inv)}{enableExpr}[{safeOutput}]"
            };

            string pipelineName = recipe.RuntimeAnalysis?.PipelineName ?? "legacy";
            if (usedAssetTypeFallback)
            {
                pipelineName = $"{pipelineName}->assetTypeFallback";
                onLog($"[SNOWFALL] Runtime background detect was Unknown; falling back to asset type {assetTypeBackground} for {recipe.PresetName}.");
            }

            onLog($"[SNOWFALL] Asset overlay graph ready: background={backgroundType}, pipeline={pipelineName}, blend={recipe.Asset.BlendMode}, opacity={safeOpacity:0.00}");
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


        private static SnowOverlayAsset? ResolveAsset(RenderJob job)
        {
            if (!string.IsNullOrWhiteSpace(job.SnowfallAssetPath) && File.Exists(job.SnowfallAssetPath))
            {
                return new SnowOverlayAsset
                {
                    FilePath = job.SnowfallAssetPath,
                    Type = GuessAssetType(job.SnowfallAssetPath),
                    BlendMode = GuessBlendMode(job.SnowfallAssetPath),
                    CanLoop = true
                };
            }

            string overlayDir = Path.Combine(AppContext.BaseDirectory, "Content", "Overlays", "Snow");
            if (!Directory.Exists(overlayDir))
                return null;

            string presetName = NormalizePreset(job.SnowfallPreset);
            foreach (string preferredName in ResolvePreferredAssetNames(presetName))
            {
                foreach (string ext in new[] { ".mp4", ".mov", ".webm" })
                {
                    string candidate = Path.Combine(overlayDir, preferredName + ext);
                    if (!File.Exists(candidate))
                        continue;

                    return new SnowOverlayAsset
                    {
                        FilePath = candidate,
                        Type = GuessAssetType(candidate),
                        BlendMode = GuessBlendMode(candidate),
                        CanLoop = true
                    };
                }
            }

            foreach (string candidate in Directory.GetFiles(overlayDir).OrderBy(Path.GetFileName))
            {
                string ext = Path.GetExtension(candidate).ToLowerInvariant();
                if (ext is ".mp4" or ".mov" or ".webm")
                {
                    return new SnowOverlayAsset
                    {
                        FilePath = candidate,
                        Type = GuessAssetType(candidate),
                        BlendMode = GuessBlendMode(candidate),
                        CanLoop = true
                    };
                }
            }

            return null;
        }


        private static IEnumerable<string> ResolvePreferredAssetNames(string presetName)
        {
            switch (presetName)
            {
                case "Snow Soft":
                    yield return "snow_soft_01";
                    break;
                case "Snow Bokeh":
                    yield return "snow_bokeh_01";
                    break;
                case "Snow Heavy":
                    yield return "snow_heavy_01";
                    break;
                case "Snow Windy":
                    yield return "snow_windy_01";
                    break;
                default:
                    yield return "snow_cinematic_01";
                    break;
            }

            yield return "snow_overlay_01";
            yield return "snow_default_01";
        }


        private static string GuessAssetType(string path)
        {
            string fileName = Path.GetFileName(path).ToLowerInvariant();
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (fileName.Contains("alpha") || ext is ".webm" or ".mov")
                return "alpha";
            return "blackBackground";
        }


        private static string GuessBlendMode(string path)
        {
            return GuessAssetType(path).Equals("alpha", StringComparison.OrdinalIgnoreCase)
                ? "screen"
                : "screen";
        }


        private static double ResolveDefaultOpacity(string presetName)
        {
            return presetName switch
            {
                "Snow Soft" => 0.28,
                "Snow Bokeh" => 0.31,
                "Snow Heavy" => 0.46,
                "Snow Windy" => 0.40,
                _ => 0.36
            };
        }


        private static double ResolveCycleSeconds(string presetName, double intensity, double outputDurationSeconds)
        {
            double baseCycle = presetName switch
            {
                "Snow Soft" => 4.8,
                "Snow Bokeh" => 4.4,
                "Snow Heavy" => 3.8,
                "Snow Windy" => 3.4,
                _ => 4.1
            };

            double intensityShift = (1.0 - intensity) * 0.6;
            double cycle = baseCycle + intensityShift;
            return Math.Clamp(cycle, 2.8, Math.Min(6.0, Math.Max(3.0, outputDurationSeconds)));
        }


        private static List<SnowOverlayLayerRecipe> BuildLayers(string presetName, double intensity)
        {
            double densityScale = 0.82 + (intensity * 0.78);
            double opacityScale = 0.82 + (intensity * 0.62);
            double driftScale = 0.70 + (intensity * 0.80);

            var layers = new List<SnowOverlayLayerRecipe>();

            if (presetName.Equals("Snow Heavy", StringComparison.OrdinalIgnoreCase))
            {
                layers.Add(CreateLayer("far", 142, 0.9, 2.6, 0.10, 0.26, 1.2, 2.8, 4.0, 16.0, 10.0, 26.0, false, false, 0.0, 1.0, densityScale, opacityScale, driftScale));
                layers.Add(CreateLayer("mid", 62, 1.4, 3.8, 0.11, 0.24, 1.4, 2.6, 6.0, 20.0, 14.0, 34.0, true, false, 0.6, 1.0, densityScale, opacityScale, driftScale));
                layers.Add(CreateLayer("near", 18, 4.0, 9.4, 0.06, 0.13, 1.0, 1.9, 10.0, 26.0, 18.0, 48.0, true, false, 2.0, 1.0, densityScale, opacityScale, driftScale));
                layers.Add(CreateLayer("streak", 14, 2.8, 7.2, 0.04, 0.10, 2.1, 3.6, 8.0, 18.0, 22.0, 58.0, true, true, 0.2, 4.7, densityScale, opacityScale, driftScale));
            }
            else if (presetName.Equals("Snow Windy", StringComparison.OrdinalIgnoreCase))
            {
                layers.Add(CreateLayer("far", 104, 0.9, 2.4, 0.09, 0.20, 1.0, 2.1, 12.0, 30.0, 20.0, 48.0, false, false, 0.0, 1.0, densityScale, opacityScale, driftScale));
                layers.Add(CreateLayer("mid", 42, 1.3, 3.4, 0.10, 0.21, 1.1, 2.0, 14.0, 34.0, 24.0, 56.0, true, false, 0.45, 1.0, densityScale, opacityScale, driftScale));
                layers.Add(CreateLayer("near", 12, 3.6, 8.6, 0.06, 0.12, 0.9, 1.6, 18.0, 38.0, 28.0, 68.0, true, false, 1.7, 1.0, densityScale, opacityScale, driftScale));
                layers.Add(CreateLayer("streak", 18, 3.4, 8.2, 0.05, 0.11, 1.8, 3.2, 18.0, 34.0, 36.0, 84.0, true, true, 0.25, 5.2, densityScale, opacityScale, driftScale));
            }
            else if (presetName.Equals("Snow Bokeh", StringComparison.OrdinalIgnoreCase))
            {
                layers.Add(CreateLayer("far", 74, 0.9, 2.3, 0.08, 0.18, 0.8, 1.8, 4.0, 12.0, 8.0, 18.0, false, false, 0.0, 1.0, densityScale, opacityScale, driftScale));
                layers.Add(CreateLayer("mid", 28, 1.3, 3.2, 0.09, 0.18, 0.9, 1.6, 6.0, 14.0, 10.0, 20.0, true, false, 0.45, 1.0, densityScale, opacityScale, driftScale));
                layers.Add(CreateLayer("near", 16, 4.4, 9.2, 0.05, 0.11, 0.6, 1.2, 10.0, 20.0, 12.0, 26.0, true, false, 2.4, 1.0, densityScale, opacityScale, driftScale));
                layers.Add(CreateLayer("streak", 7, 2.6, 6.4, 0.03, 0.08, 1.2, 2.0, 4.0, 10.0, 8.0, 18.0, true, true, 0.15, 3.8, densityScale, opacityScale, driftScale));
            }
            else if (presetName.Equals("Snow Soft", StringComparison.OrdinalIgnoreCase))
            {
                layers.Add(CreateLayer("far", 66, 0.8, 2.0, 0.06, 0.16, 0.8, 1.6, 3.0, 10.0, 6.0, 16.0, false, false, 0.0, 1.0, densityScale, opacityScale, driftScale));
                layers.Add(CreateLayer("mid", 22, 1.1, 2.8, 0.08, 0.16, 0.8, 1.4, 5.0, 12.0, 8.0, 18.0, true, false, 0.4, 1.0, densityScale, opacityScale, driftScale));
                layers.Add(CreateLayer("near", 7, 3.4, 7.0, 0.04, 0.08, 0.6, 1.0, 7.0, 14.0, 10.0, 20.0, true, false, 1.9, 1.0, densityScale, opacityScale, driftScale));
                layers.Add(CreateLayer("streak", 4, 1.8, 4.2, 0.03, 0.06, 1.0, 1.6, 4.0, 10.0, 6.0, 16.0, true, true, 0.12, 3.2, densityScale, opacityScale, driftScale));
            }
            else
            {
                layers.Add(CreateLayer("far", 112, 1.0, 2.8, 0.10, 0.24, 0.9, 2.0, 5.0, 14.0, 8.0, 22.0, false, false, 0.0, 1.0, densityScale, opacityScale, driftScale));
                layers.Add(CreateLayer("mid", 44, 1.5, 4.0, 0.12, 0.24, 1.0, 1.8, 6.0, 16.0, 10.0, 24.0, true, false, 0.45, 1.0, densityScale, opacityScale, driftScale));
                layers.Add(CreateLayer("near", 16, 4.2, 9.8, 0.07, 0.15, 0.8, 1.3, 8.0, 18.0, 12.0, 28.0, true, false, 2.0, 1.0, densityScale, opacityScale, driftScale));
                layers.Add(CreateLayer("streak", 12, 2.8, 7.2, 0.04, 0.10, 1.4, 2.4, 7.0, 18.0, 14.0, 32.0, true, true, 0.18, 4.0, densityScale, opacityScale, driftScale));
            }

            return layers;
        }


        private static SnowOverlayLayerRecipe CreateLayer(
            string name,
            int count,
            double minSize,
            double maxSize,
            double minOpacity,
            double maxOpacity,
            double minVerticalLoops,
            double maxVerticalLoops,
            double minSway,
            double maxSway,
            double minDrift,
            double maxDrift,
            bool useGlow,
            bool useStreaks,
            double blurMultiplier,
            double streakLengthMultiplier,
            double densityScale,
            double opacityScale,
            double driftScale)
        {
            return new SnowOverlayLayerRecipe
            {
                Name = name,
                Count = Math.Max(1, (int)Math.Round(count * densityScale)),
                MinSize = minSize,
                MaxSize = maxSize,
                MinOpacity = Math.Clamp(minOpacity * opacityScale, 0.04, 0.62),
                MaxOpacity = Math.Clamp(maxOpacity * opacityScale, 0.08, 0.82),
                MinVerticalLoops = minVerticalLoops,
                MaxVerticalLoops = maxVerticalLoops,
                MinSway = minSway * driftScale,
                MaxSway = maxSway * driftScale,
                MinDrift = minDrift * driftScale,
                MaxDrift = maxDrift * driftScale,
                UseGlow = useGlow,
                UseStreaks = useStreaks,
                BlurMultiplier = blurMultiplier,
                StreakLengthMultiplier = streakLengthMultiplier
            };
        }


        internal static int HashSeed(string presetName, string layerName, int particleIndex)
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + presetName.GetHashCode(StringComparison.Ordinal);
                hash = (hash * 31) + layerName.GetHashCode(StringComparison.Ordinal);
                hash = (hash * 31) + particleIndex;
                hash = (hash * 31) + 9241;
                return hash;
            }
        }


        internal static double PositiveMod(double value, double modulus)
        {
            if (modulus <= 0.0)
                return 0.0;

            double result = value % modulus;
            return result < 0.0 ? result + modulus : result;
        }


        internal static double Lerp(double a, double b, double t)
            => a + ((b - a) * Math.Clamp(t, 0.0, 1.0));


        internal static string FormatSequenceFilePath(string ffmpegPattern, int frameNumber)
        {
            if (string.IsNullOrWhiteSpace(ffmpegPattern))
                throw new ArgumentException("Sequence pattern cannot be empty.", nameof(ffmpegPattern));

            string csharpPattern = ffmpegPattern.Replace("%04d", "{0:D4}", StringComparison.Ordinal);
            if (string.Equals(csharpPattern, ffmpegPattern, StringComparison.Ordinal))
                csharpPattern = Regex.Replace(ffmpegPattern, @"%0?(\d+)d", m => "{0:D" + m.Groups[1].Value + "}");

            return string.Format(CultureInfo.InvariantCulture, csharpPattern, frameNumber);
        }
    }
}
