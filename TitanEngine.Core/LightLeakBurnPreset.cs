using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TitanEngine
{
    public sealed class LightLeakBurnSettings
    {
        public double Intensity { get; set; } = 65.0;
        public double Spread { get; set; } = 55.0;
        public double Warmth { get; set; } = 72.0;
        public double Burn { get; set; } = 60.0;
        public double EdgeSoftness { get; set; } = 75.0;
        public double Grain { get; set; } = 35.0;
        public string Direction { get; set; } = "from-left";
        public double DurationSeconds { get; set; } = 0.95;
        public string BlendMode { get; set; } = "screen";
        public double OverlayOpacity { get; set; } = 0.82;
        public string? OverlayAssetPath { get; set; }
        public bool UseOverlayAsset { get; set; } = false;
    }

    public sealed class LightLeakBurnTransitionGraph
    {
        public string FilterComplex { get; set; } = string.Empty;
        public double OutputDurationSeconds { get; set; }
        public bool HasAudioOutput { get; set; }
        public string OutputPath { get; set; } = string.Empty;
        public List<string> TempArtifacts { get; set; } = new List<string>();
    }

    public sealed class LightLeakBurnGeneratedLayers
    {
        public string TempRoot { get; set; } = string.Empty;
        public string MaskPattern { get; set; } = string.Empty;
        public string OverlayPattern { get; set; } = string.Empty;
        public int FrameCount { get; set; }
        public double FrameRate { get; set; }
        public List<string> TempArtifacts { get; set; } = new List<string>();
    }

    public static class LightLeakBurnPreset
    {
        public static LightLeakBurnSettings FromJob(RenderJob job)
        {
            return new LightLeakBurnSettings
            {
                Intensity = Math.Clamp(job.LightLeakBurnIntensity > 0.0 ? job.LightLeakBurnIntensity : job.FxIntensity, 0.0, 100.0),
                Spread = Math.Clamp(job.LightLeakBurnSpread, 0.0, 100.0),
                Warmth = Math.Clamp(job.LightLeakBurnWarmth, 0.0, 100.0),
                Burn = Math.Clamp(job.LightLeakBurnBurn, 0.0, 100.0),
                EdgeSoftness = Math.Clamp(job.LightLeakBurnEdgeSoftness, 0.0, 100.0),
                Grain = Math.Clamp(job.LightLeakBurnGrain, 0.0, 100.0),
                Direction = NormalizeDirection(job.LightLeakBurnDirection),
                DurationSeconds = Math.Max(0.10, job.LightLeakBurnDuration),
                BlendMode = NormalizeBlendMode(job.LightLeakBurnBlendMode),
                OverlayOpacity = Math.Clamp(job.LightLeakBurnOpacity <= 0.0 ? 0.82 : job.LightLeakBurnOpacity, 0.0, 1.0),
                OverlayAssetPath = job.LightLeakBurnAssetPath,
                UseOverlayAsset = job.LightLeakBurnUseAssetOverlay && !string.IsNullOrWhiteSpace(job.LightLeakBurnAssetPath) && File.Exists(job.LightLeakBurnAssetPath)
            };
        }

        public static string NormalizeDirection(string? input)
        {
            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (v.Contains("right")) return "from-right";
            if (v.Contains("top")) return "from-top";
            if (v.Contains("bottom")) return "from-bottom";
            return "from-left";
        }

        public static string NormalizeBlendMode(string? input)
        {
            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (v.Contains("add")) return "addition";
            if (v.Contains("light")) return "lighten";
            if (v.Contains("screen")) return "screen";
            return "screen";
        }

        public static string BuildFxFilter(RenderJob job, Action<string> onLog)
        {
            _ = job;
            onLog("[LIGHT-LEAK-BURN] Procedural burn path disabled. Asset-only render path is required.");
            throw new InvalidOperationException("Light leak asset missing. Asset-only effect cannot render.");
        }

        public static LightLeakOverlayRecipe? ResolveSingleClipAssetRecipe(
            RenderJob job,
            double outputDurationSeconds,
            Action<string> onLog)
        {
            LightLeakBurnSettings settings = FromJob(job);
            string? assetPath = ResolvePrimaryAssetPath(settings, onLog);
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            double opacity = ResolveAssetOpacity(settings);
            double preferredSeekSeconds = ResolvePreferredSeekSeconds(assetPath);
            var asset = CreateAssetDescriptor(assetPath, settings, opacity, preferredSeekSeconds);
            var usages = BuildSingleClipAssetUsages(settings, Math.Max(0.10, outputDurationSeconds), opacity, preferredSeekSeconds, assetPath);
            if (usages.Count == 0)
                return null;

            onLog($"[LIGHT-LEAK-BURN] Asset-backed single clip recipe prepared: {Path.GetFileName(assetPath)} windows={usages.Count} opacity={opacity:F2}");
            return new LightLeakOverlayRecipe
            {
                Mode = "burst",
                Asset = asset,
                Usages = usages
            };
        }

        public static LightLeakOverlayRecipe? ResolveTransitionAssetRecipe(
            RenderJob job,
            double transitionOffsetSeconds,
            double transitionDurationSeconds,
            double outputDurationSeconds,
            Action<string> onLog)
        {
            LightLeakBurnSettings settings = FromJob(job);
            string? assetPath = ResolvePrimaryAssetPath(settings, onLog);
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            double opacity = ResolveAssetOpacity(settings);
            double preferredSeekSeconds = ResolvePreferredSeekSeconds(assetPath);
            var asset = CreateAssetDescriptor(assetPath, settings, opacity, preferredSeekSeconds);
            var usages = BuildTransitionAssetUsages(
                settings,
                Math.Max(0.10, outputDurationSeconds),
                opacity,
                Math.Max(0.0, transitionOffsetSeconds),
                Math.Max(0.10, transitionDurationSeconds),
                preferredSeekSeconds,
                assetPath);
            if (usages.Count == 0)
                return null;

            onLog($"[LIGHT-LEAK-BURN] Asset-backed transition recipe prepared: {Path.GetFileName(assetPath)} windows={usages.Count} opacity={opacity:F2}");
            return new LightLeakOverlayRecipe
            {
                Mode = "transition",
                Asset = asset,
                Usages = usages
            };
        }

        public static async Task<LightLeakBurnGeneratedLayers> GenerateLayersAsync(
            int width,
            int height,
            double frameRate,
            double durationSeconds,
            LightLeakBurnSettings settings,
            string? tag,
            CancellationToken token,
            Action<string> onLog)
        {
            _ = width;
            _ = height;
            _ = frameRate;
            _ = durationSeconds;
            _ = settings;
            _ = tag;
            _ = token;
            onLog("[LIGHT-LEAK-BURN] Procedural generator disabled. Asset-only mode is active.");
            await Task.CompletedTask;
            throw new InvalidOperationException("Light leak asset missing. Asset-only effect cannot render.");
        }

        public static string BuildSingleClipLightLeakGraph(
            string baseInputLabel,
            string baseVideoFilters,
            string effectInputLabel,
            double overlayOpacity,
            string outputLabel,
            Action<string> onLog)
        {
            _ = baseInputLabel;
            _ = baseVideoFilters;
            _ = effectInputLabel;
            _ = overlayOpacity;
            _ = outputLabel;
            onLog("[LIGHT-LEAK-BURN] Procedural single-clip graph disabled. Asset-only mode is active.");
            throw new InvalidOperationException("Light leak asset missing. Asset-only effect cannot render.");
        }

        public static string BuildAssetBackedTransitionFilterComplex(
            int targetWidth,
            int targetHeight,
            double targetFps,
            double durationA,
            double durationB,
            double transitionDuration,
            double outputDuration,
            LightLeakOverlayRecipe overlayRecipe,
            bool hasAnyAudio,
            bool hasAudioA,
            bool hasAudioB,
            Action<string> onLog)
        {
            var inv = CultureInfo.InvariantCulture;
            string w = Math.Max(2, targetWidth).ToString(inv);
            string h = Math.Max(2, targetHeight).ToString(inv);
            string fps = Math.Clamp(targetFps, 1.0, 120.0).ToString("F3", inv);
            string trans = Math.Max(0.10, transitionDuration).ToString("F3", inv);
            string outDur = Math.Max(0.10, outputDuration).ToString("F3", inv);
            string xfadeOffset = Math.Max(0.0, durationA - transitionDuration).ToString("F3", inv);

            string normalizeVideo = $"scale={w}:{h}:flags=lanczos:force_original_aspect_ratio=increase,crop={w}:{h},setsar=1,fps={fps},format=gbrp,setpts=PTS-STARTPTS";
            string videoGraph =
                $"[0:v]{normalizeVideo}[v0];" +
                $"[1:v]{normalizeVideo}[v1];" +
                $"[v0][v1]xfade=transition=fadeblack:duration={trans}:offset={xfadeOffset}[xfbase]";

            string burnGraph = LightLeakOverlayPreset.BuildSingleClipGraph(
                "[xfbase]",
                string.Empty,
                "[2:v]",
                overlayRecipe,
                targetWidth,
                targetHeight,
                targetFps,
                outputDuration,
                "burntransition",
                onLog);

            string audioGraph = string.Empty;
            if (hasAnyAudio)
            {
                if (hasAudioA && hasAudioB)
                {
                    audioGraph =
                        $"[0:a]aresample=48000,asetpts=PTS-STARTPTS[a0];" +
                        $"[1:a]aresample=48000,asetpts=PTS-STARTPTS[a1];" +
                        $"[a0][a1]acrossfade=d={trans}:c1=tri:c2=tri[aout]";
                }
                else if (hasAudioA)
                {
                    audioGraph = $"[0:a]aresample=48000,asetpts=PTS-STARTPTS,apad=pad_dur={outDur}[aout]";
                }
                else if (hasAudioB)
                {
                    audioGraph = $"[1:a]aresample=48000,asetpts=PTS-STARTPTS,apad=pad_dur={outDur}[aout]";
                }
            }

            string videoOutputGraph = "[burntransition]format=yuv420p[vout]";
            string filterComplex = string.IsNullOrWhiteSpace(audioGraph)
                ? $"{videoGraph};{burnGraph};{videoOutputGraph}"
                : $"{videoGraph};{burnGraph};{videoOutputGraph};{audioGraph}";

            onLog($"[LIGHT-LEAK-BURN] Asset-backed transition graph ready: offset={xfadeOffset}s, duration={trans}s, output={outDur}s");
            return filterComplex;
        }

        public static async Task<LightLeakBurnTransitionGraph> RenderTransitionSourceAsync(
            IReadOnlyList<string> sourcePaths,
            string outDir,
            string mergeTag,
            RenderJob job,
            CancellationToken token,
            Action<string> onLog,
            Func<string, Task<(double Duration, long Bitrate, int Width, int Height, double Fps)>> analyzeMediaAsync,
            Func<string, Task<bool>> hasAudioStreamAsync,
            Func<string, CancellationToken, Task<(int ExitCode, string Stderr)>> runFfmpegCaptureAsync)
        {
            if (sourcePaths == null || sourcePaths.Count != 2)
                throw new InvalidOperationException("LightLeakBurn transition requires exactly 2 input clips.");

            var normalizedPaths = new List<string>();
            foreach (string input in sourcePaths)
            {
                if (string.IsNullOrWhiteSpace(input))
                    throw new InvalidOperationException("LightLeakBurn transition requires valid input paths.");

                string fullPath = Path.GetFullPath(input.Trim());
                if (!File.Exists(fullPath))
                    throw new FileNotFoundException($"Transition source does not exist: {fullPath}");
                normalizedPaths.Add(fullPath);
            }

            var metaA = await analyzeMediaAsync(normalizedPaths[0]);
            var metaB = await analyzeMediaAsync(normalizedPaths[1]);

            int imageCount = normalizedPaths.Count(IsImageSourcePath);
            double durationA = IsImageSourcePath(normalizedPaths[0])
                ? EngineCore.ResolveImageTimelineSegmentDuration(job.ImageTimelineDurationSeconds, Math.Max(1, imageCount))
                : Math.Max(0.10, metaA.Duration);
            double durationB = IsImageSourcePath(normalizedPaths[1])
                ? EngineCore.ResolveImageTimelineSegmentDuration(job.ImageTimelineDurationSeconds, Math.Max(1, imageCount))
                : Math.Max(0.10, metaB.Duration);

            int targetW = metaA.Width > 0 ? metaA.Width : (metaB.Width > 0 ? metaB.Width : 1920);
            int targetH = metaA.Height > 0 ? metaA.Height : (metaB.Height > 0 ? metaB.Height : 1080);
            if ((targetW & 1) != 0) targetW++;
            if ((targetH & 1) != 0) targetH++;

            double targetFps = metaA.Fps > 0.1 ? metaA.Fps : (metaB.Fps > 0.1 ? metaB.Fps : 30.0);
            targetFps = Math.Clamp(targetFps, 1.0, 120.0);

            LightLeakBurnSettings settings = FromJob(job);
            double minInputDuration = Math.Max(0.05, Math.Min(durationA, durationB));
            double maxTransition = Math.Max(0.05, minInputDuration * 0.80);
            double transitionDuration = Math.Clamp(settings.DurationSeconds, 0.05, maxTransition);
            transitionDuration = Math.Min(transitionDuration, Math.Max(0.05, Math.Min(durationA, durationB) - 0.02));
            double outputDuration = Math.Max(0.10, durationA + durationB - transitionDuration);
            double transitionOffset = Math.Max(0.0, durationA - transitionDuration);
            bool hasAudioA = await hasAudioStreamAsync(normalizedPaths[0]);
            bool hasAudioB = await hasAudioStreamAsync(normalizedPaths[1]);
            bool hasAnyAudio = hasAudioA || hasAudioB;
            var tempArtifacts = new List<string>();
            string safeTag = string.IsNullOrWhiteSpace(mergeTag) ? Guid.NewGuid().ToString("N").Substring(0, 8) : Regex.Replace(mergeTag, @"[^\w\-]+", "_");
            string outputPath = Path.Combine(outDir, $"lightleakburn_{safeTag}_source.mp4");
            LightLeakOverlayRecipe? assetRecipe = ResolveTransitionAssetRecipe(
                job,
                transitionOffset,
                transitionDuration,
                outputDuration,
                onLog);
            if (assetRecipe == null)
                throw new InvalidOperationException("Light leak asset missing. Asset-only effect cannot render.");

            await EngineCore.ValidateLightLeakAssetRecipeAsync("Light Leak Burn Transition", assetRecipe, token, onLog);

            string filterComplex = BuildAssetBackedTransitionFilterComplex(
                targetW,
                targetH,
                targetFps,
                durationA,
                durationB,
                transitionDuration,
                outputDuration,
                assetRecipe,
                hasAnyAudio,
                hasAudioA,
                hasAudioB,
                onLog);

            var cmd = new StringBuilder();
            cmd.Append("-y ");

            if (IsImageSourcePath(normalizedPaths[0]))
                cmd.Append($"-loop 1 -t {durationA.ToString("F3", CultureInfo.InvariantCulture)} ");
            cmd.Append($"-i \"{normalizedPaths[0]}\" ");

            if (IsImageSourcePath(normalizedPaths[1]))
                cmd.Append($"-loop 1 -t {durationB.ToString("F3", CultureInfo.InvariantCulture)} ");
            cmd.Append($"-i \"{normalizedPaths[1]}\" ");

            cmd.Append($"{(assetRecipe.Asset.CanLoop ? "-stream_loop -1 " : string.Empty)}-i \"{assetRecipe.Asset.FilePath}\" ");

            cmd.Append($"-filter_complex \"{filterComplex}\" ");
            cmd.Append("-map \"[vout]\" ");

            if (hasAnyAudio)
                cmd.Append("-map \"[aout]\" ");
            else
                cmd.Append("-an ");

            cmd.Append("-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p ");
            if (hasAnyAudio)
                cmd.Append("-c:a aac -b:a 192k -ar 48000 -ac 2 ");

            cmd.Append($"-t {outputDuration.ToString("F3", CultureInfo.InvariantCulture)} -movflags +faststart \"{outputPath}\"");

            onLog("[LIGHT-LEAK-BURN] Rendering transition source...");
            onLog($"[LIGHT-LEAK-BURN] A={Path.GetFileName(normalizedPaths[0])} ({durationA:F2}s), B={Path.GetFileName(normalizedPaths[1])} ({durationB:F2}s), transition={transitionDuration:F2}s, output={outputDuration:F2}s");
            onLog($"[LIGHT-LEAK-BURN] Using asset-backed transition source: {Path.GetFileName(assetRecipe.Asset.FilePath)}");
            onLog($"[LIGHT-LEAK-BURN] FilterComplex: {filterComplex}");
            onLog($"[FFMPEG-CMD] {cmd}");

            var (exitCode, stderr) = await runFfmpegCaptureAsync(cmd.ToString(), token);
            if (exitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
            {
                string summary = string.Join(" | ",
                    (stderr ?? string.Empty)
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Take(12));
                throw new Exception($"LightLeakBurn transition render failed: {summary}");
            }

            return new LightLeakBurnTransitionGraph
            {
                FilterComplex = filterComplex,
                OutputDurationSeconds = outputDuration,
                HasAudioOutput = hasAnyAudio,
                OutputPath = outputPath,
                TempArtifacts = tempArtifacts
            };
        }

        private static LightLeakOverlayAsset CreateAssetDescriptor(
            string assetPath,
            LightLeakBurnSettings settings,
            double opacity,
            double preferredSeekSeconds)
        {
            return new LightLeakOverlayAsset
            {
                Id = Path.GetFileNameWithoutExtension(assetPath),
                FilePath = assetPath,
                Type = "blackBackground",
                BlendMode = NormalizeBlendMode(settings.BlendMode),
                DefaultOpacity = opacity,
                RecommendedDuration = Math.Clamp(settings.DurationSeconds, 0.55, 0.90),
                CanLoop = true,
                Intensity = "medium",
                Category = "filmBurn",
                LikelyColorWash = true,
                PreferredSeekSeconds = preferredSeekSeconds
            };
        }

        private static List<LightLeakOverlayUsage> BuildSingleClipAssetUsages(
            LightLeakBurnSettings settings,
            double outputDurationSeconds,
            double opacity,
            double preferredSeekSeconds,
            string assetPath)
        {
            double safeDuration = Math.Max(0.10, outputDurationSeconds);
            double baseDuration = Math.Clamp(settings.DurationSeconds, 0.58, Math.Min(0.82, safeDuration));
            double fadeIn = Math.Min(0.10, baseDuration * 0.16);
            double fadeOut = Math.Min(0.22, baseDuration * 0.28);
            double primaryAnchor = safeDuration <= 2.40 ? 0.16 : 0.14;
            string primaryPlacement = ResolveDirectionalPlacement(settings, false);
            var usages = new List<LightLeakOverlayUsage>
            {
                CreateUsage(
                    safeDuration,
                    primaryAnchor,
                    baseDuration,
                    fadeIn,
                    fadeOut,
                    opacity,
                    preferredSeekSeconds,
                    NormalizeBlendMode(settings.BlendMode),
                    false,
                    ResolveAssetPlacement(assetPath, "burst", primaryPlacement),
                    ResolveDirectionalEdgeCoverage(settings, "burst"))
            };

            if (safeDuration > 3.20)
            {
                string secondaryPlacement = ResolveDirectionalPlacement(settings, true);
                usages.Add(CreateUsage(
                    safeDuration,
                    0.58,
                    Math.Max(0.55, baseDuration * 0.88),
                    fadeIn,
                    fadeOut,
                    Math.Clamp(opacity * 0.92, 0.14, 0.20),
                    preferredSeekSeconds + 0.70,
                    NormalizeBlendMode(settings.BlendMode),
                    true,
                    ResolveAssetPlacement(assetPath, "burst", secondaryPlacement),
                    ResolveDirectionalEdgeCoverage(settings, "burst")));
            }

            return usages;
        }

        private static List<LightLeakOverlayUsage> BuildTransitionAssetUsages(
            LightLeakBurnSettings settings,
            double outputDurationSeconds,
            double opacity,
            double transitionOffsetSeconds,
            double transitionDurationSeconds,
            double preferredSeekSeconds,
            string assetPath)
        {
            double safeDuration = Math.Max(0.10, outputDurationSeconds);
            double baseDuration = Math.Clamp(Math.Max(settings.DurationSeconds, transitionDurationSeconds), 0.55, Math.Min(0.90, safeDuration));
            double transitionStart = Math.Clamp(transitionOffsetSeconds - 0.15, 0.0, Math.Max(0.0, safeDuration - baseDuration));
            double anchorSeconds = transitionStart + (baseDuration * 0.30);
            double anchorRatio = safeDuration <= 0.10 ? 0.0 : (anchorSeconds / safeDuration);
            double fadeIn = Math.Min(0.10, baseDuration * 0.14);
            double fadeOut = Math.Min(0.22, baseDuration * 0.28);

            return new List<LightLeakOverlayUsage>
            {
                CreateUsage(
                    safeDuration,
                    anchorRatio,
                    baseDuration,
                    fadeIn,
                    fadeOut,
                    Math.Clamp(opacity, 0.16, 0.22),
                    preferredSeekSeconds,
                    NormalizeBlendMode(settings.BlendMode),
                    false,
                    ResolveAssetPlacement(assetPath, "transition", ResolveDirectionalPlacement(settings, false)),
                    ResolveDirectionalEdgeCoverage(settings, "transition"))
            };
        }

        private static LightLeakOverlayUsage CreateUsage(
            double outputDurationSeconds,
            double anchorRatio,
            double usageDurationSeconds,
            double fadeInSeconds,
            double fadeOutSeconds,
            double opacity,
            double assetStartSeconds,
            string blendMode,
            bool mirrorHorizontally,
            string placement,
            double edgeCoverage)
        {
            double safeDuration = Math.Max(0.10, outputDurationSeconds);
            double duration = Math.Clamp(usageDurationSeconds, 0.18, safeDuration);
            double start = Math.Clamp((safeDuration * Math.Clamp(anchorRatio, 0.0, 1.0)) - (duration * 0.30), 0.0, Math.Max(0.0, safeDuration - duration));

            return new LightLeakOverlayUsage
            {
                StartSeconds = start,
                DurationSeconds = duration,
                FadeInSeconds = Math.Clamp(fadeInSeconds, 0.0, duration * 0.40),
                FadeOutSeconds = Math.Clamp(fadeOutSeconds, 0.0, duration * 0.50),
                Opacity = Math.Clamp(opacity, 0.14, 0.28),
                BlendMode = blendMode,
                AssetStartSeconds = Math.Max(0.0, assetStartSeconds),
                Placement = placement,
                MirrorHorizontally = mirrorHorizontally,
                EdgeCoverage = Math.Clamp(edgeCoverage, 0.30, 0.60)
            };
        }

        private static string? ResolvePrimaryAssetPath(LightLeakBurnSettings settings, Action<string> onLog)
        {
            if (!string.IsNullOrWhiteSpace(settings.OverlayAssetPath))
            {
                string customPath = Path.GetFullPath(settings.OverlayAssetPath.Trim());
                if (File.Exists(customPath))
                {
                    onLog($"[LIGHT-LEAK-BURN] Using custom asset override: {customPath}");
                    return customPath;
                }
            }

            string[] preferredFiles =
            {
                "pexels_light_leak_30042778.mp4",
                "warm_film_burn_01.mp4",
                "mixkit_light_leaks_overlay_48011.mp4",
                "soft_bokeh_leak_01.mp4"
            };

            foreach (string root in EnumerateAssetRoots())
            {
                if (!Directory.Exists(root))
                    continue;

                foreach (string fileName in preferredFiles)
                {
                    string candidate = Path.Combine(root, fileName);
                    if (File.Exists(candidate))
                        return candidate;
                }

                string? wildcardMatch = Directory.EnumerateFiles(root, "*.mp4")
                    .FirstOrDefault(file =>
                        Path.GetFileName(file).Contains("burn", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(file).Contains("leak", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(wildcardMatch))
                    return wildcardMatch;
            }

            onLog("[LIGHT-LEAK-BURN-WARN] No built-in burn asset found in Content\\Overlays\\LightLeaks. Falling back to generator.");
            return null;
        }

        private static IEnumerable<string> EnumerateAssetRoots()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            yield return Path.Combine(baseDir, "Content", "Overlays", "LightLeaks");
            yield return Path.Combine(baseDir, "Assets", "Overlays", "LightLeaks");

            string? parent = Directory.GetParent(baseDir)?.FullName;
            for (int i = 0; i < 5 && !string.IsNullOrWhiteSpace(parent); i++)
            {
                yield return Path.Combine(parent, "Content", "Overlays", "LightLeaks");
                yield return Path.Combine(parent, "Assets", "Overlays", "LightLeaks");
                parent = Directory.GetParent(parent)?.FullName;
            }

            yield return Path.Combine(Environment.CurrentDirectory, "Content", "Overlays", "LightLeaks");
            yield return Path.Combine(Environment.CurrentDirectory, "Assets", "Overlays", "LightLeaks");
            yield return Path.Combine(Environment.CurrentDirectory, "TitanEngine", "Content", "Overlays", "LightLeaks");
            yield return Path.Combine(Environment.CurrentDirectory, "TitanEngine", "Assets", "Overlays", "LightLeaks");
        }

        private static double ResolveAssetOpacity(LightLeakBurnSettings settings)
        {
            double intensity = Math.Clamp(settings.Intensity / 100.0, 0.0, 1.0);
            double rawOpacity = settings.OverlayOpacity > 0.0 ? settings.OverlayOpacity : 0.82;
            if (rawOpacity >= 0.65)
                return Math.Clamp(0.16 + (intensity * 0.06), 0.16, 0.22);

            return Math.Clamp(rawOpacity, 0.14, 0.24);
        }

        private static double ResolvePreferredSeekSeconds(string assetPath)
        {
            string fileName = Path.GetFileName(assetPath ?? string.Empty);
            if (fileName.Equals("pexels_light_leak_30042778.mp4", StringComparison.OrdinalIgnoreCase))
                return 0.0;
            if (fileName.Equals("warm_film_burn_01.mp4", StringComparison.OrdinalIgnoreCase))
                return 2.8;
            if (fileName.Equals("mixkit_light_leaks_overlay_48011.mp4", StringComparison.OrdinalIgnoreCase))
                return 0.0;
            if (fileName.Equals("soft_bokeh_leak_01.mp4", StringComparison.OrdinalIgnoreCase))
                return 4.3;
            return 0.0;
        }

        private static string ResolveDirectionalPlacement(LightLeakBurnSettings settings, bool alternate)
        {
            string direction = NormalizeDirection(settings.Direction);
            return direction switch
            {
                "from-right" => alternate ? "leftEdge" : "rightEdge",
                "from-top" => alternate ? "bottomRight" : "topRight",
                "from-bottom" => alternate ? "topLeft" : "bottomLeft",
                _ => alternate ? "rightEdge" : "leftEdge"
            };
        }

        private static double ResolveDirectionalEdgeCoverage(LightLeakBurnSettings settings, string mode)
        {
            double spread = Math.Clamp(settings.Spread / 100.0, 0.0, 1.0);
            double baseCoverage = mode.Equals("transition", StringComparison.OrdinalIgnoreCase) ? 0.44 : 0.40;
            return Math.Clamp(baseCoverage + (spread * 0.10), 0.30, 0.56);
        }

        private static string ResolveAssetPlacement(string assetPath, string mode, string fallbackPlacement)
        {
            string fileName = Path.GetFileName(assetPath ?? string.Empty);
            if (fileName.Equals("pexels_light_leak_30042778.mp4", StringComparison.OrdinalIgnoreCase) &&
                mode.Equals("transition", StringComparison.OrdinalIgnoreCase))
            {
                return "fullFrameTransitionOnly";
            }

            return fallbackPlacement;
        }

        public static Task<string> BuildTransitionFilterComplexAsync(
            int targetWidth,
            int targetHeight,
            double targetFps,
            double durationA,
            double durationB,
            double transitionDuration,
            double outputDuration,
            string maskPattern,
            string overlayPattern,
            LightLeakBurnSettings settings,
            bool useOverlayAsset,
            bool hasAnyAudio,
            bool hasAudioA,
            bool hasAudioB,
            Action<string> onLog,
            CancellationToken token = default)
        {
            _ = targetWidth;
            _ = targetHeight;
            _ = targetFps;
            _ = durationA;
            _ = durationB;
            _ = transitionDuration;
            _ = outputDuration;
            _ = maskPattern;
            _ = overlayPattern;
            _ = settings;
            _ = useOverlayAsset;
            _ = hasAnyAudio;
            _ = hasAudioA;
            _ = hasAudioB;
            _ = token;
            onLog("[LIGHT-LEAK-BURN] Procedural transition graph disabled. Asset-only mode is active.");
            throw new InvalidOperationException("Light leak asset missing. Asset-only effect cannot render.");
        }

        private static async Task GenerateBurnFrameSequencesAsync(
            string tempRoot,
            string maskPattern,
            string overlayPattern,
            int frameCount,
            int width,
            int height,
            double transitionDuration,
            LightLeakBurnSettings settings,
            Action<string> onLog,
            CancellationToken token)
        {
            await RunOnStaThreadAsync(() =>
            {
                Directory.CreateDirectory(tempRoot);
                int safeW = Math.Max(2, width);
                int safeH = Math.Max(2, height);
                var rng = new Random(unchecked((int)(DateTime.UtcNow.Ticks ^ safeW ^ (safeH << 1))));

                for (int i = 0; i < frameCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    double progress = frameCount <= 1 ? 1.0 : (double)i / (frameCount - 1);
                    var maskVisual = new DrawingVisual();
                    var overlayVisual = new DrawingVisual();

                    using (DrawingContext mg = maskVisual.RenderOpen())
                    using (DrawingContext og = overlayVisual.RenderOpen())
                    {
                        mg.DrawRectangle(Brushes.Black, null, new Rect(0, 0, safeW, safeH));
                        og.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, safeW, safeH));
                        DrawBurnFrame(mg, og, safeW, safeH, progress, settings, rng);
                    }

                    RenderTargetBitmap maskBitmap = new RenderTargetBitmap(safeW, safeH, 96, 96, PixelFormats.Pbgra32);
                    maskBitmap.Render(maskVisual);
                    if (maskBitmap.CanFreeze) maskBitmap.Freeze();

                    RenderTargetBitmap overlayBitmap = new RenderTargetBitmap(safeW, safeH, 96, 96, PixelFormats.Pbgra32);
                    overlayBitmap.Render(overlayVisual);
                    if (overlayBitmap.CanFreeze) overlayBitmap.Freeze();

                    string maskFile = FormatSequenceFilePath(maskPattern, i + 1);
                    string overlayFile = FormatSequenceFilePath(overlayPattern, i + 1);
                    SaveBitmap(maskBitmap, maskFile);
                    SaveBitmap(overlayBitmap, overlayFile);
                }

                onLog($"[LIGHT-LEAK-BURN] Generated {frameCount} burn mask/overlay frames at {safeW}x{safeH}");
            }, token);
        }

        private static void SaveBitmap(BitmapSource bitmap, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }

        private static string FormatSequenceFilePath(string ffmpegPattern, int frameNumber)
        {
            if (string.IsNullOrWhiteSpace(ffmpegPattern))
                throw new ArgumentException("Sequence pattern cannot be empty.", nameof(ffmpegPattern));

            string csharpPattern = ffmpegPattern.Replace("%04d", "{0:D4}", StringComparison.Ordinal);
            if (string.Equals(csharpPattern, ffmpegPattern, StringComparison.Ordinal))
            {
                csharpPattern = Regex.Replace(ffmpegPattern, @"%0?(\d+)d", m => "{0:D" + m.Groups[1].Value + "}");
            }

            return string.Format(CultureInfo.InvariantCulture, csharpPattern, frameNumber);
        }

        private static Task RunOnStaThreadAsync(Action action, CancellationToken token)
        {
            if (token.IsCancellationRequested)
                return Task.FromCanceled(token);

            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread thread = new Thread(() =>
            {
                try
                {
                    if (token.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled(token);
                        return;
                    }

                    action();
                    tcs.TrySetResult(null);
                }
                catch (OperationCanceledException oce)
                {
                    tcs.TrySetCanceled(oce.CancellationToken.CanBeCanceled ? oce.CancellationToken : token);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = "TitanEngine_LightLeakBurn_STA"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return tcs.Task;
        }

        private static void DrawBurnFrame(DrawingContext mg, DrawingContext og, int width, int height, double progress, LightLeakBurnSettings settings, Random rng)
        {
            double intensity = settings.Intensity / 100.0;
            double spread = settings.Spread / 100.0;
            double warmth = settings.Warmth / 100.0;
            double burn = settings.Burn / 100.0;
            double edgeSoftness = Math.Max(0.05, settings.EdgeSoftness / 100.0);
            double grain = settings.Grain / 100.0;
            double alphaPeak = Math.Sin(Math.PI * progress);
            double motion = 0.35 + 0.65 * alphaPeak;
            Color hotWhite = Color.FromRgb(255, 245, 230);
            Color warmGold = Color.FromRgb(255, 208, 108);
            Color warmOrange = Color.FromRgb(255, 122, 42);
            Color emberRed = Color.FromRgb(255, 70, 20);

            var blobs = BuildLeakCenters(settings.Direction, progress, width, height);
            foreach (var blob in blobs)
            {
                DrawRadialGlow(mg, og, width, height, blob.cx, blob.cy, blob.rx, blob.ry, hotWhite, warmGold, motion * (0.18 + intensity * 0.36), 0.0);
                DrawRadialGlow(mg, og, width, height, blob.cx * 1.01, blob.cy * 0.99, blob.rx * 1.55, blob.ry * 1.35, warmGold, warmOrange, motion * (0.16 + spread * 0.28), 0.0);
                DrawRadialGlow(mg, og, width, height, blob.cx * 0.98, blob.cy * 1.02, blob.rx * 2.10, blob.ry * 1.65, warmOrange, emberRed, motion * (0.10 + burn * 0.20), 0.0);
            }

            var grainBrush = new SolidColorBrush(Color.FromArgb((byte)(10 + 42 * grain), 255, 255, 255));
            int speckCount = Math.Max(220, (int)(width * height * (0.00002 + grain * 0.00003)));
            for (int i = 0; i < speckCount; i++)
            {
                int x = rng.Next(0, width);
                int y = rng.Next(0, height);
                int size = rng.Next(1, 3);
                mg.DrawRectangle(Brushes.White, null, new Rect(x, y, size, size));
                og.DrawRectangle(grainBrush, null, new Rect(x, y, size, size));
            }

            if (settings.Direction is "from-left" or "from-right")
            {
                var linePen = new System.Windows.Media.Pen(new SolidColorBrush(Color.FromArgb((byte)(48 + 72 * alphaPeak), 255, 242, 194)), Math.Max(1.0, height / 240.0));
                double y = height * (0.08 + 0.12 * alphaPeak);
                og.DrawLine(linePen, new Point(0, y), new Point(width, y + height * 0.03 * Math.Sin(progress * 8.0)));
            }
            else
            {
                var linePen = new System.Windows.Media.Pen(new SolidColorBrush(Color.FromArgb((byte)(42 + 66 * alphaPeak), 255, 230, 165)), Math.Max(1.0, width / 240.0));
                double x = width * (0.10 + 0.14 * alphaPeak);
                og.DrawLine(linePen, new Point(x, 0), new Point(x + width * 0.03 * Math.Sin(progress * 8.0), height));
            }
        }

        private readonly struct BurnBlob
        {
            public BurnBlob(double cx, double cy, double rx, double ry)
            {
                this.cx = cx;
                this.cy = cy;
                this.rx = rx;
                this.ry = ry;
            }

            public double cx { get; }
            public double cy { get; }
            public double rx { get; }
            public double ry { get; }
        }

        private static List<BurnBlob> BuildLeakCenters(string direction, double progress, int width, int height)
        {
            double px = progress;
            double eased = 0.5 - 0.5 * Math.Cos(Math.PI * px);
            double x1;
            double y1;
            double x2;
            double y2;
            double x3;
            double y3;

            switch (direction)
            {
                case "from-right":
                    x1 = width * (1.08 - eased * 0.95);
                    y1 = height * (0.42 + 0.08 * Math.Sin(px * 7.0));
                    x2 = width * (0.92 - eased * 0.72);
                    y2 = height * (0.64 - 0.14 * Math.Cos(px * 6.0));
                    x3 = width * (0.76 - eased * 0.42);
                    y3 = height * (0.18 + 0.08 * Math.Sin(px * 5.0));
                    break;
                case "from-top":
                    x1 = width * (0.18 + 0.12 * Math.Sin(px * 4.0));
                    y1 = height * (0.02 + eased * 0.86);
                    x2 = width * (0.58 + 0.14 * Math.Cos(px * 6.0));
                    y2 = height * (0.10 + eased * 0.58);
                    x3 = width * (0.84 - 0.10 * Math.Sin(px * 5.0));
                    y3 = height * (0.22 + eased * 0.42);
                    break;
                case "from-bottom":
                    x1 = width * (0.16 + 0.12 * Math.Sin(px * 4.0));
                    y1 = height * (1.00 - eased * 0.86);
                    x2 = width * (0.56 + 0.16 * Math.Cos(px * 6.0));
                    y2 = height * (0.90 - eased * 0.58);
                    x3 = width * (0.82 - 0.10 * Math.Sin(px * 5.0));
                    y3 = height * (0.78 - eased * 0.42);
                    break;
                default:
                    x1 = width * (0.00 + eased * 0.92);
                    y1 = height * (0.38 + 0.10 * Math.Sin(px * 5.0));
                    x2 = width * (0.18 + eased * 0.66);
                    y2 = height * (0.60 - 0.12 * Math.Cos(px * 6.0));
                    x3 = width * (0.40 + eased * 0.46);
                    y3 = height * (0.20 + 0.08 * Math.Sin(px * 4.0));
                    break;
            }

            double baseScale = 0.20 + 0.22 * eased;
            return new List<BurnBlob>
            {
                new BurnBlob(x1, y1, width * (baseScale * 0.95), height * (baseScale * 0.72)),
                new BurnBlob(x2, y2, width * (baseScale * 0.70), height * (baseScale * 0.56)),
                new BurnBlob(x3, y3, width * (baseScale * 0.55), height * (baseScale * 0.48)),
            };
        }

        private static void DrawRadialGlow(
            DrawingContext mg,
            DrawingContext og,
            int width,
            int height,
            double cx,
            double cy,
            double rx,
            double ry,
            Color centerColor,
            Color outerColor,
            double overlayAlpha,
            double maskAlpha)
        {
            var geometry = new EllipseGeometry(new Point(cx, cy), Math.Max(1.0, rx), Math.Max(1.0, ry));
            Color whiteBase = Color.FromRgb(255, 255, 255);
            Color outerMask = Color.FromRgb(0, 0, 0);
            Color centerWithAlpha = Color.FromArgb((byte)(255 * Math.Clamp(maskAlpha + overlayAlpha, 0.0, 1.0)), whiteBase.R, whiteBase.G, whiteBase.B);
            Color centerOverlay = Color.FromArgb((byte)(255 * Math.Clamp(overlayAlpha, 0.0, 1.0)), centerColor.R, centerColor.G, centerColor.B);
            Color outerOverlay = Color.FromArgb(0, outerColor.R, outerColor.G, outerColor.B);

            var maskBrush = new RadialGradientBrush(centerWithAlpha, outerMask)
            {
                Center = new Point(cx, cy),
                GradientOrigin = new Point(cx, cy),
                RadiusX = Math.Max(0.01, rx / Math.Max(1, width)),
                RadiusY = Math.Max(0.01, ry / Math.Max(1, height))
            };
            mg.DrawGeometry(maskBrush, null, geometry);

            var overlayBrush = new RadialGradientBrush(centerOverlay, outerOverlay)
            {
                Center = new Point(cx, cy),
                GradientOrigin = new Point(cx, cy),
                RadiusX = Math.Max(0.01, rx / Math.Max(1, width)),
                RadiusY = Math.Max(0.01, ry / Math.Max(1, height))
            };
            og.DrawGeometry(overlayBrush, null, geometry);
        }

        private static string BuildCustomXfadeExpr(string direction, double spread, double burn, double edgeSoftness)
        {
            var inv = CultureInfo.InvariantCulture;
            string axis = direction switch
            {
                "from-right" => $"(1-(X/W))",
                "from-top" => $"(Y/H)",
                "from-bottom" => $"(1-(Y/H))",
                _ => $"(X/W)"
            };

            string gateSlip = direction is "from-top" or "from-bottom"
                ? $"(sin(((X/W)*8+P*24)*6.28318)*0.012*(0.35+{spread.ToString("F3", inv)}))"
                : $"(sin(((Y/H)*8+P*24)*6.28318)*0.012*(0.35+{spread.ToString("F3", inv)}))";

            string organicNoise =
                $"((sin((X*0.017+Y*0.019+P*18)*{(1.0 + spread * 1.5).ToString("F3", inv)}) + " +
                $"cos((X*0.031-Y*0.013+P*11)*{(1.0 + spread * 0.9).ToString("F3", inv)}) + " +
                $"sin((X+Y)*0.004+P*7.5))*0.09*{Math.Clamp(0.35 + spread * 0.65, 0.15, 1.0).ToString("F3", inv)})";

            string burnPush = $"({burn.ToString("F3", inv)}*0.06*(0.5+0.5*sin(P*13.0+X*0.03+Y*0.04)))";
            string front = $"({axis}+{gateSlip}+{organicNoise}-{burnPush})";
            string soft = Math.Max(0.01, edgeSoftness * 0.18).ToString("F4", inv);
            string mix = $"((P-({front}))/({soft})+0.5)";

            return $"if(lte({mix},0),A,if(gte({mix},1),B,A*(1-({mix}))+B*({mix})))";
        }

        private static bool IsImageSourcePath(string path)
        {
            string ext = Path.GetExtension(path).Trim().ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" or ".gif" or ".tif" or ".tiff";
        }

        private static string NormalizeFfmpegColor(string color)
        {
            if (string.IsNullOrWhiteSpace(color))
                return "white";

            string trimmed = color.Trim();
            int alphaIndex = trimmed.IndexOf('@');
            string baseColor = alphaIndex >= 0 ? trimmed[..alphaIndex] : trimmed;
            string alpha = alphaIndex >= 0 ? trimmed[alphaIndex..] : string.Empty;

            if (baseColor.StartsWith("#", StringComparison.Ordinal))
                baseColor = "0x" + baseColor[1..];

            return baseColor + alpha;
        }
    }
}
