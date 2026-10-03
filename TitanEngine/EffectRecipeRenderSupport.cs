using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TitanEngine
{
    public static class EffectRecipeBridge
    {
        public static OverlayEffectRecipe CreateGeneratedOverlayRecipe(ParticleEffectRecipe recipe, RenderJob? job = null)
        {
            if (recipe == null)
                throw new ArgumentNullException(nameof(recipe));

            OverlayTimingMode timingMode = recipe.ParticleType switch
            {
                ParticleType.Confetti => OverlayTimingMode.Burst,
                ParticleType.Firework => OverlayTimingMode.Burst,
                _ => OverlayTimingMode.FullTimeline
            };

            double opacity = recipe.ParticleType switch
            {
                ParticleType.Bokeh => 0.30,
                ParticleType.Sparkle => 0.34,
                ParticleType.Confetti => 0.35,
                ParticleType.Rain => 0.32,
                _ => 0.30
            };

            if (recipe.ParticleType == ParticleType.Snow && job?.SnowfallOpacity >= 0.0)
                opacity = Math.Clamp(job.SnowfallOpacity, 0.0, 1.0);

            return new OverlayEffectRecipe
            {
                Id = recipe.Id,
                Name = recipe.Name,
                Category = recipe.Category,
                AssetType = OverlayAssetType.Generated,
                BlendMode = OverlayBlendMode.Normal,
                Opacity = opacity,
                TimingMode = timingMode,
                Placement = OverlayPlacement.FullFrame,
                ScaleMode = OverlayScaleMode.Cover,
                FadeIn = timingMode == OverlayTimingMode.FullTimeline ? 0.18 : 0.06,
                FadeOut = timingMode == OverlayTimingMode.FullTimeline ? 0.22 : 0.16,
                LinkedParticleRecipeId = recipe.Id,
                Aliases = new List<string>(recipe.Aliases)
            };
        }

        public static LightLeakOverlayRecipe? ResolveLightLeakOverlayRecipe(
            RenderJob sourceJob,
            OverlayEffectRecipe overlayRecipe,
            double outputDurationSeconds,
            Action<string> onLog)
        {
            if (sourceJob == null)
                throw new ArgumentNullException(nameof(sourceJob));
            if (overlayRecipe == null)
                throw new ArgumentNullException(nameof(overlayRecipe));

            if (!string.Equals(overlayRecipe.LegacyResolverKey, "light-leak-overlay", StringComparison.OrdinalIgnoreCase))
                return null;

            var adapterJob = new RenderJob
            {
                SourcePath = sourceJob.SourcePath,
                MergeVideos = sourceJob.MergeVideos,
                MergeInputPaths = sourceJob.MergeInputPaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>(),
                TemplateName = sourceJob.TemplateName,
                ImageTimelineDurationSeconds = sourceJob.ImageTimelineDurationSeconds,
                FxIntensity = sourceJob.FxIntensity,
                LightLeakOverlayMode = "burst",
                LightLeakOverlayId = "FilmBurnWarm",
                LightLeakOverlayAssetPath = null,
                LightLeakOverlayOpacity = overlayRecipe.Opacity
            };

            onLog($"[RECIPE-BRIDGE] Mapping {overlayRecipe.Name} to FilmBurnWarm asset-only light leak preset.");
            return LightLeakOverlayPreset.ResolveRecipe(adapterJob, outputDurationSeconds, onLog);
        }

        public static string? ResolveOverlayAssetPath(OverlayEffectRecipe recipe)
        {
            if (recipe == null)
                throw new ArgumentNullException(nameof(recipe));
            if (string.IsNullOrWhiteSpace(recipe.AssetPath))
                return null;

            var candidates = new List<string>();
            string raw = recipe.AssetPath.Trim();

            if (Path.IsPathRooted(raw))
            {
                candidates.Add(raw);
            }
            else
            {
                candidates.Add(Path.GetFullPath(raw));
                candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, raw));
                candidates.Add(Path.Combine(Environment.CurrentDirectory, raw));

                string? parent = Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory)?.FullName;
                for (int i = 0; i < 5 && !string.IsNullOrWhiteSpace(parent); i++)
                {
                    candidates.Add(Path.Combine(parent, raw));
                    parent = Directory.GetParent(parent)?.FullName;
                }
            }

            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string full = Path.GetFullPath(candidate);
                    if (File.Exists(full))
                        return full;
                }
                catch
                {
                    // Ignore invalid path candidates and keep probing.
                }
            }

            return null;
        }

        public static string BuildMissingAssetFallbackFilter(
            OverlayEffectRecipe recipe,
            double outputDurationSeconds,
            Action<string> onLog)
        {
            if (string.Equals(recipe.LegacyResolverKey, "light-leak-overlay", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(recipe.Name, "FilmBurnWarm", StringComparison.OrdinalIgnoreCase))
            {
                onLog("[RECIPE-OVERLAY-ERROR] Light leak asset missing.");
                throw new InvalidOperationException("Light leak asset missing.");
            }

            double duration = Math.Max(0.5, outputDurationSeconds);
            string filter;

            if (recipe.Name.Equals("FilmBurnWarm", StringComparison.OrdinalIgnoreCase))
            {
                double center = Math.Max(0.18, duration - 0.46);
                filter = $"eq=brightness='0.18*exp(-10*abs(t-{center.ToString("0.###", CultureInfo.InvariantCulture)}))':contrast=1.08:saturation=1.18";
            }
            else
            {
                filter = "eq=brightness='0.055*(0.5+0.5*sin(2*PI*t/4.8))':contrast=1.045:saturation=1.12,colorbalance=rs=0.06:gs=0.015:bs=-0.035";
            }

            onLog($"[RECIPE-OVERLAY-FALLBACK] {recipe.Name} asset missing; using built-in FFmpeg fallback.");
            return filter;
        }

        private static string MapTimingMode(OverlayTimingMode timingMode)
            => timingMode switch
            {
                OverlayTimingMode.FullTimeline => "fullTimeline",
                OverlayTimingMode.Transition => "transition",
                OverlayTimingMode.Burst => "burst",
                OverlayTimingMode.BeatCut => "transition",
                _ => "auto"
            };
    }

    public static class PostProcessEffectGraphBuilder
    {
        public static string BuildFilterChain(
            PostProcessEffectRecipe recipe,
            RenderJob job,
            int targetWidth,
            int targetHeight,
            double outputDurationSeconds,
            Action<string> onLog)
        {
            if (recipe == null)
                throw new ArgumentNullException(nameof(recipe));
            if (job == null)
                throw new ArgumentNullException(nameof(job));

            double strength = Math.Clamp(recipe.Intensity * (0.70 + (Math.Clamp(job.FxIntensity, 0.0, 100.0) / 100.0 * 0.90)), 0.05, 1.0);
            int safeWidth = Math.Max(320, targetWidth);
            int safeHeight = Math.Max(320, targetHeight);

            string filter = recipe.EffectType switch
            {
                PostProcessEffectType.Grain => BuildFilmGrainFilter(recipe, strength),
                PostProcessEffectType.Vhs => BuildVhsFilter(recipe, strength, safeWidth, safeHeight),
                PostProcessEffectType.Glitch => BuildGlitchFilter(recipe, strength),
                PostProcessEffectType.RgbSplit => BuildRgbSplitFilter(recipe, strength),
                PostProcessEffectType.ChromaticAberration => BuildChromaticAberrationFilter(recipe, strength),
                PostProcessEffectType.Vignette => BuildLensVignetteFilter(recipe, strength),
                PostProcessEffectType.LensDistortion => BuildLensVignetteFilter(recipe, strength),
                PostProcessEffectType.Crt => BuildCrtFilter(recipe, strength, safeWidth, safeHeight),
                PostProcessEffectType.LightSweep => BuildLightSweepFilter(recipe, strength, outputDurationSeconds),
                PostProcessEffectType.Blur => BuildBlurFilter(strength),
                _ => string.Empty
            };

            if (!string.IsNullOrWhiteSpace(filter))
                onLog($"[RECIPE-POST] {recipe.Name} -> {filter}");

            return filter;
        }

        private static string BuildFilmGrainFilter(PostProcessEffectRecipe recipe, double strength)
        {
            double baseNoise = GetDouble(recipe.Parameters, "noiseBase", 8.0);
            double noiseStrength = Math.Clamp(baseNoise * (0.70 + (0.85 * strength)), 4.0, 18.0);
            return $"noise=all_seed={recipe.Seed}:alls={F(noiseStrength)}:allf=t+u";
        }

        private static string BuildVhsFilter(PostProcessEffectRecipe recipe, double strength, int width, int height)
        {
            int shift = Math.Clamp((int)Math.Round(GetDouble(recipe.Parameters, "chromaShift", 4.0) * (0.90 + strength)), 3, 9);
            double noiseStrength = Math.Clamp(GetDouble(recipe.Parameters, "noiseBase", 12.0) * (0.75 + (0.75 * strength)), 8.0, 22.0);
            double saturation = 0.90 - (0.08 * strength);
            double contrast = 1.08 + (0.08 * strength);
            double brightness = 0.010 + (0.018 * strength);
            return
                $"crop=iw-8:ih-4:x='4+3*sin(17*t)':y='2+1*sin(11*t)',scale={width}:{height}:flags=lanczos," +
                $"rgbashift=rh={shift}:gh=0:bh={-shift}:edge=smear," +
                $"noise=all_seed={recipe.Seed}:alls={F(noiseStrength)}:allf=t+u," +
                "drawgrid=width=iw:height=3:thickness=1:color=black@0.16," +
                $"eq=contrast={F(contrast)}:saturation={F(saturation)}:brightness='{F(brightness)}+0.018*sin(2*PI*t*8)'";
        }

        private static string BuildGlitchFilter(PostProcessEffectRecipe recipe, double strength)
        {
            double chromaShift = Math.Clamp(GetDouble(recipe.Parameters, "shiftPixels", 4.5) * (0.70 + (0.75 * strength)), 1.5, 8.0);
            double noiseStrength = Math.Clamp(GetDouble(recipe.Parameters, "noiseBase", 7.0) * (0.45 + (0.55 * strength)), 2.0, 12.0);
            double hueSwing = 4.0 + (6.0 * strength);
            double saturation = 1.04 + (0.10 * strength);
            return
                $"chromashift=cbh={F(chromaShift)}:crh={F(-chromaShift)}:edge=smear," +
                $"hue=h='{F(hueSwing)}*sin(6*t)':s={F(saturation)}," +
                $"noise=all_seed={recipe.Seed}:alls={F(noiseStrength)}:allf=t";
        }

        private static string BuildRgbSplitFilter(PostProcessEffectRecipe recipe, double strength)
        {
            double shiftPixels = Math.Clamp(GetDouble(recipe.Parameters, "shiftPixels", 4.5) * (0.55 + (0.65 * strength)), 0.8, 6.0);
            double noiseStrength = Math.Clamp(GetDouble(recipe.Parameters, "noiseBase", 7.0) * (0.32 + (0.34 * strength)), 0.8, 7.0);
            int shift = Math.Clamp((int)Math.Round(shiftPixels), 3, 10);
            return
                $"rgbashift=rh={shift}:rv=1:gh=0:bh={-shift}:bv=-1:edge=smear," +
                $"noise=all_seed={recipe.Seed}:alls={F(noiseStrength + 2.5)}:allf=t+u," +
                "eq=contrast=1.10:saturation=1.18";
        }

        private static string BuildChromaticAberrationFilter(PostProcessEffectRecipe recipe, double strength)
        {
            double shiftPixels = Math.Clamp(GetDouble(recipe.Parameters, "shiftPixels", 1.6) * (0.55 + (0.55 * strength)), 0.5, 3.2);
            int shift = Math.Clamp((int)Math.Round(shiftPixels), 2, 5);
            return $"rgbashift=rh={shift}:gh=0:bh={-shift}:edge=smear,eq=contrast=1.06:saturation=1.10";
        }

        private static string BuildLensVignetteFilter(PostProcessEffectRecipe recipe, double strength)
        {
            double k1 = GetDouble(recipe.Parameters, "lensK1", -0.032) * (1.10 + (0.55 * strength));
            double k2 = GetDouble(recipe.Parameters, "lensK2", 0.010) * (1.10 + (0.55 * strength));
            double angle = Math.Clamp(0.35 - (0.08 * strength), 0.27, 0.36);
            return
                $"lenscorrection=k1={F(k1)}:k2={F(k2)}:fc=black@0:i=bilinear," +
                $"vignette={F(angle)},eq=contrast=1.08:saturation=1.06";
        }

        private static string BuildCrtFilter(PostProcessEffectRecipe recipe, double strength, int width, int height)
        {
            int spacing = Math.Clamp((int)Math.Round(GetDouble(recipe.Parameters, "scanlineSpacing", 3.0)), 2, 5);
            int thickness = Math.Clamp((int)Math.Round(1.0 + (Math.Max(width, height) / 1800.0)), 1, 2);
            double opacity = Math.Clamp(0.16 + (0.12 * strength), 0.16, 0.30);
            double contrast = 1.10 + (0.08 * strength);
            double saturation = 0.90 - (0.08 * strength);
            return
                $"drawgrid=width=iw:height={spacing}:thickness={thickness}:color=black@{F(opacity)}," +
                $"noise=alls={F(4.0 + (5.0 * strength))}:allf=t," +
                $"eq=contrast={F(contrast)}:saturation={F(saturation)}:brightness='0.025*sin(2*PI*t*7)',vignette=0.42";
        }

        private static string BuildLightSweepFilter(PostProcessEffectRecipe recipe, double strength, double outputDurationSeconds)
        {
            double period = Math.Clamp(GetDouble(recipe.Parameters, "periodSeconds", 3.8), 1.8, Math.Max(2.0, outputDurationSeconds > 0.1 ? outputDurationSeconds : 3.8));
            double brightness = 0.025 + (0.035 * strength);
            double contrast = 1.04 + (0.04 * strength);
            double saturation = 1.04 + (0.06 * strength);
            string center = $"W*(mod(T/{F(Math.Max(1.4, period * 0.65))},1)*1.35-0.18)";
            string falloff = $"exp(-pow((X-({center}))/(W*0.095),2))";
            return
                "format=gbrp," +
                $"geq=r='clip(r(X,Y)+58*{falloff},0,255)':" +
                $"g='clip(g(X,Y)+50*{falloff},0,255)':" +
                $"b='clip(b(X,Y)+38*{falloff},0,255)'," +
                $"eq=brightness='{F(brightness)}*sin(2*PI*t/{F(period)})':contrast={F(contrast)}:saturation={F(saturation)}";
        }

        private static string BuildBlurFilter(double strength)
        {
            double radius = Math.Clamp(0.8 + (2.4 * strength), 0.6, 3.2);
            return $"gblur=sigma={F(radius)}";
        }

        private static double GetDouble(IReadOnlyDictionary<string, string> values, string key, double fallback)
        {
            if (values.TryGetValue(key, out string? raw) &&
                double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                return parsed;
            }

            return fallback;
        }

        private static string F(double value)
            => value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public static class OverlayEffectGraphBuilder
    {
        public static string BuildLoopedSequenceClipGraph(
            string baseInputLabel,
            string baseVideoFilters,
            string overlayInputLabel,
            OverlayEffectRecipe recipe,
            int loopFrameCount,
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
            string safeOutput = string.IsNullOrWhiteSpace(outputLabel) ? "recipeout" : outputLabel;
            string fps = Math.Clamp(frameRate, 1.0, 120.0).ToString("F3", inv);
            int safeLoopFrameCount = Math.Max(1, loopFrameCount);
            var window = ResolveWindow(recipe, outputDurationSeconds);
            double overlayOpacity = ResolveSafeOpacity(recipe);
            double windowEnd = window.Start + window.Duration;

            string baseChain = string.IsNullOrWhiteSpace(baseVideoFilters)
                ? $"{safeBaseInput}format=rgba[recipebase]"
                : $"{safeBaseInput}{baseVideoFilters},format=rgba[recipebase]";

            var overlayOps = new StringBuilder();
            overlayOps.Append($"{safeOverlayInput}format=rgba,");
            overlayOps.Append($"loop=loop=-1:size={safeLoopFrameCount}:start=0,");
            overlayOps.Append($"setpts=N/{fps}/TB,");
            overlayOps.Append($"trim=duration={window.Duration.ToString("F3", inv)},");
            overlayOps.Append($"colorchannelmixer=aa={overlayOpacity.ToString("F3", inv)}");

            if (window.FadeIn > 0.001)
                overlayOps.Append($",fade=t=in:st=0:d={window.FadeIn.ToString("F3", inv)}:alpha=1");
            if (window.FadeOut > 0.001 && window.Duration > window.FadeOut)
            {
                double fadeOutStart = Math.Max(0.0, window.Duration - window.FadeOut);
                overlayOps.Append($",fade=t=out:st={fadeOutStart.ToString("F3", inv)}:d={window.FadeOut.ToString("F3", inv)}:alpha=1");
            }
            overlayOps.Append($",setpts=PTS+{window.Start.ToString("F3", inv)}/TB");

            if (recipe.Placement == OverlayPlacement.FullFrame)
            {
                string overlayChain = overlayOps.Append("[recipeseq]").ToString();
                string mergeChain =
                    $"[recipeseq][recipebase]scale2ref=w=main_w:h=main_h:flags=lanczos[recipescaled][recipebase2];" +
                    $"[recipebase2][recipescaled]overlay=shortest=0:eof_action=pass:format=auto:enable='between(t,{window.Start.ToString("F3", inv)},{windowEnd.ToString("F3", inv)})'[{safeOutput}]";

                onLog($"[RECIPE-OVERLAY] {recipe.Name} sequence graph ready: fps={fps}, frames={safeLoopFrameCount}, opacity={overlayOpacity:0.00}, timing={window.Start:0.00}+{window.Duration:0.00}");
                return $"{baseChain};{overlayChain};{mergeChain}";
            }

            (int overlayWidth, int overlayHeight, int x, int y) = ResolvePlacementGeometry(recipe, 1920, 1920);
            string placedChain =
                $"{overlayOps},scale={overlayWidth}:{overlayHeight}:flags=lanczos[recipescaled];" +
                $"[recipebase][recipescaled]overlay=x={x}:y={y}:shortest=0:eof_action=pass:format=auto:enable='between(t,{window.Start.ToString("F3", inv)},{windowEnd.ToString("F3", inv)})'[{safeOutput}]";

            onLog($"[RECIPE-OVERLAY] {recipe.Name} sequence graph ready (placed mode): opacity={overlayOpacity:0.00}, timing={window.Start:0.00}+{window.Duration:0.00}");
            return $"{baseChain};{placedChain}";
        }

        public static string BuildAssetClipGraph(
            string baseInputLabel,
            string baseVideoFilters,
            string overlayInputLabel,
            OverlayEffectRecipe recipe,
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
            string safeOutput = string.IsNullOrWhiteSpace(outputLabel) ? "recipeassetout" : outputLabel;
            int safeWidth = Math.Max(2, canvasWidth);
            int safeHeight = Math.Max(2, canvasHeight);
            double safeFps = Math.Clamp(frameRate, 10.0, 120.0);
            var window = ResolveWindow(recipe, outputDurationSeconds);
            double overlayOpacity = ResolveSafeOpacity(recipe);
            double windowEnd = window.Start + window.Duration;
            (int overlayWidth, int overlayHeight, int x, int y) = ResolvePlacementGeometry(recipe, safeWidth, safeHeight);

            string baseChain = string.IsNullOrWhiteSpace(baseVideoFilters)
                ? $"{safeBaseInput}format=rgba[recipebase]"
                : $"{safeBaseInput}{baseVideoFilters},format=rgba[recipebase]";

            var overlayChain = new StringBuilder();
            overlayChain.Append($"{safeOverlayInput}fps={safeFps.ToString("F3", inv)},");
            overlayChain.Append($"scale={overlayWidth}:{overlayHeight}:flags=lanczos,");
            overlayChain.Append("format=rgba,trim=duration=");
            overlayChain.Append(window.Duration.ToString("F3", inv));
            overlayChain.Append(",setpts=PTS-STARTPTS");

            if (recipe.AssetType == OverlayAssetType.Alpha)
            {
                overlayChain.Append($",colorchannelmixer=aa={overlayOpacity.ToString("F3", inv)}");
                if (window.FadeIn > 0.001)
                    overlayChain.Append($",fade=t=in:st=0:d={window.FadeIn.ToString("F3", inv)}:alpha=1");
                if (window.FadeOut > 0.001 && window.Duration > window.FadeOut)
                {
                    double fadeOutStart = Math.Max(0.0, window.Duration - window.FadeOut);
                    overlayChain.Append($",fade=t=out:st={fadeOutStart.ToString("F3", inv)}:d={window.FadeOut.ToString("F3", inv)}:alpha=1");
                }
                overlayChain.Append($",setpts=PTS+{window.Start.ToString("F3", inv)}/TB");

                overlayChain.Append("[recipeoverlay]");
                string merge = $"[recipebase][recipeoverlay]overlay=x={x}:y={y}:shortest=0:eof_action=pass:format=auto:enable='between(t,{window.Start.ToString("F3", inv)},{windowEnd.ToString("F3", inv)})'[{safeOutput}]";
                onLog($"[RECIPE-OVERLAY] {recipe.Name} asset-alpha graph ready: opacity={overlayOpacity:0.00}, placement={recipe.Placement}");
                return $"{baseChain};{overlayChain};{merge}";
            }

            overlayChain.Append($",setpts=PTS+{window.Start.ToString("F3", inv)}/TB");
            string blendMode = ResolveBlendMode(recipe.BlendMode);
            overlayChain.Append("[recipeoverlay]");
            string colorCanvas =
                $"color=c=black:s={safeWidth}x{safeHeight}:r={safeFps.ToString("F3", inv)}:d={window.Duration.ToString("F3", inv)},format=rgba[recipecanvas]";
            string placeOverlay = $"[recipecanvas][recipeoverlay]overlay=x={x}:y={y}:format=auto[recipeplaced]";
            string blend = $"[recipebase][recipeplaced]blend=all_mode={blendMode}:all_opacity={overlayOpacity.ToString("F3", inv)}:enable='between(t,{window.Start.ToString("F3", inv)},{windowEnd.ToString("F3", inv)})'[{safeOutput}]";
            onLog($"[RECIPE-OVERLAY] {recipe.Name} asset-blend graph ready: mode={blendMode}, opacity={overlayOpacity:0.00}, placement={recipe.Placement}");
            return $"{baseChain};{overlayChain};{colorCanvas};{placeOverlay};{blend}";
        }

        private static (double Start, double Duration, double FadeIn, double FadeOut) ResolveWindow(OverlayEffectRecipe recipe, double outputDurationSeconds)
        {
            double safeDuration = Math.Max(0.10, outputDurationSeconds);
            double duration = recipe.Duration > 0.001 ? recipe.Duration : safeDuration;
            double start = Math.Max(0.0, recipe.StartTime);

            switch (recipe.TimingMode)
            {
                case OverlayTimingMode.Transition:
                    duration = Math.Clamp(duration <= 0.001 ? 0.28 : duration, 0.10, 0.95);
                    start = Math.Clamp(recipe.StartTime > 0.001 ? recipe.StartTime : (safeDuration - duration - 0.06), 0.0, Math.Max(0.0, safeDuration - duration));
                    break;
                case OverlayTimingMode.Burst:
                    {
                        double maxBurstDuration = Math.Min(1.20, safeDuration);
                        double minBurstDuration = Math.Min(0.18, maxBurstDuration);
                        duration = Math.Clamp(duration <= 0.001 ? Math.Min(0.90, safeDuration * 0.28) : duration, minBurstDuration, maxBurstDuration);
                    }
                    start = Math.Clamp(recipe.StartTime > 0.001 ? recipe.StartTime : (safeDuration * 0.20), 0.0, Math.Max(0.0, safeDuration - duration));
                    break;
                case OverlayTimingMode.BeatCut:
                    duration = Math.Clamp(duration <= 0.001 ? Math.Min(0.42, safeDuration * 0.18) : duration, 0.10, 0.42);
                    start = Math.Clamp(recipe.StartTime > 0.001 ? recipe.StartTime : (safeDuration * 0.48), 0.0, Math.Max(0.0, safeDuration - duration));
                    break;
                default:
                    duration = Math.Clamp(duration, 0.10, safeDuration);
                    start = Math.Clamp(start, 0.0, Math.Max(0.0, safeDuration - duration));
                    break;
            }

            if (recipe.Name.Contains("flash", StringComparison.OrdinalIgnoreCase) ||
                recipe.Name.Contains("pop", StringComparison.OrdinalIgnoreCase))
            {
                duration = Math.Clamp(duration, 0.10, 0.35);
            }

            double fadeIn = Math.Clamp(recipe.FadeIn, 0.0, duration * 0.48);
            double fadeOut = Math.Clamp(recipe.FadeOut, 0.0, duration * 0.48);
            return (start, duration, fadeIn, fadeOut);
        }

        private static double ResolveSafeOpacity(OverlayEffectRecipe recipe)
        {
            double opacity = Math.Clamp(recipe.Opacity, 0.01, 1.0);
            if (recipe.TimingMode == OverlayTimingMode.FullTimeline)
                opacity = Math.Min(opacity, 0.35);
            if (recipe.Placement == OverlayPlacement.FullFrame && recipe.AssetType == OverlayAssetType.BlackBackground)
                opacity = Math.Min(opacity, 0.24);
            return opacity;
        }

        private static (int Width, int Height, int X, int Y) ResolvePlacementGeometry(OverlayEffectRecipe recipe, int canvasWidth, int canvasHeight)
        {
            int safeWidth = Math.Max(2, canvasWidth);
            int safeHeight = Math.Max(2, canvasHeight);
            int width;
            int height;

            if (recipe.Placement == OverlayPlacement.FullFrame)
            {
                width = safeWidth;
                height = safeHeight;
            }
            else if (recipe.Placement is OverlayPlacement.LeftEdge or OverlayPlacement.RightEdge)
            {
                width = EnsureEven((int)Math.Round(safeWidth * 0.56));
                height = safeHeight;
            }
            else if (recipe.Placement == OverlayPlacement.Center)
            {
                width = EnsureEven((int)Math.Round(safeWidth * 0.58));
                height = EnsureEven((int)Math.Round(safeHeight * 0.58));
            }
            else
            {
                width = EnsureEven((int)Math.Round(safeWidth * 0.42));
                height = EnsureEven((int)Math.Round(safeHeight * 0.42));
            }

            int marginX = Math.Max(16, safeWidth / 24);
            int marginY = Math.Max(16, safeHeight / 24);
            int x = recipe.Placement switch
            {
                OverlayPlacement.LeftEdge or OverlayPlacement.TopLeft or OverlayPlacement.BottomLeft => marginX,
                OverlayPlacement.RightEdge or OverlayPlacement.TopRight or OverlayPlacement.BottomRight => Math.Max(marginX, safeWidth - width - marginX),
                OverlayPlacement.Center => Math.Max(0, (safeWidth - width) / 2),
                _ => 0
            };

            int y = recipe.Placement switch
            {
                OverlayPlacement.TopLeft or OverlayPlacement.TopRight => marginY,
                OverlayPlacement.BottomLeft or OverlayPlacement.BottomRight => Math.Max(marginY, safeHeight - height - marginY),
                OverlayPlacement.Center => Math.Max(0, (safeHeight - height) / 2),
                OverlayPlacement.LeftEdge or OverlayPlacement.RightEdge => 0,
                _ => 0
            };

            return (EnsureEven(width), EnsureEven(height), x, y);
        }

        private static string ResolveBlendMode(OverlayBlendMode blendMode)
            => blendMode switch
            {
                OverlayBlendMode.Lighten => "lighten",
                OverlayBlendMode.Add => "addition",
                OverlayBlendMode.Normal => "normal",
                _ => "screen"
            };

        private static int EnsureEven(int value)
        {
            int safe = Math.Max(2, value);
            return (safe & 1) == 0 ? safe : safe + 1;
        }
    }

    public static class TransitionRecipeGraphBuilder
    {
        public static string BuildFilter(
            TransitionRecipe recipe,
            int targetWidth,
            int targetHeight,
            double segmentDurationSeconds,
            double fxIntensity)
        {
            if (recipe == null)
                throw new ArgumentNullException(nameof(recipe));

            string tw = F(Math.Max(2, targetWidth));
            string th = F(Math.Max(2, targetHeight));
            string d = F(Math.Max(0.12, Math.Min(segmentDurationSeconds, Math.Max(recipe.Duration, 0.12))));
            double strength = Math.Clamp(fxIntensity / 100.0, 0.0, 1.0);
            int upW = EnsureEven((int)Math.Ceiling(targetWidth * (1.08 + (0.08 * strength))));
            int upH = EnsureEven((int)Math.Ceiling(targetHeight * (1.08 + (0.08 * strength))));

            return recipe.EffectType switch
            {
                TransitionEffectType.ZoomPunch or TransitionEffectType.BlurZoom =>
                    $"scale=w='ceil(({tw})*(1+0.20*exp(-9*mod(t,1.20)/{d})*(0.72+0.28*sin(24*mod(t,1.20))))/2)*2':h='ceil(({th})*(1+0.20*exp(-9*mod(t,1.20)/{d})*(0.72+0.28*sin(24*mod(t,1.20))))/2)*2':eval=frame:flags=lanczos," +
                    $"crop={targetWidth}:{targetHeight}:x='(in_w-out_w)/2':y='(in_h-out_h)/2',eq=contrast={F(1.12 + (0.08 * strength))}:saturation={F(1.08 + (0.06 * strength))}",

                TransitionEffectType.Shake =>
                    $"scale={upW}:{upH}:flags=lanczos," +
                    $"crop={targetWidth}:{targetHeight}:x='(in_w-out_w)/2+({F(16 + (18 * strength))}*exp(-9*mod(t,0.95)/{d})*sin(52*mod(t,0.95)))':y='(in_h-out_h)/2+({F(10 + (13 * strength))}*exp(-9*mod(t,0.95)/{d})*cos(44*mod(t,0.95)))'",

                TransitionEffectType.FlashPop =>
                    $"eq=brightness='{F(0.32 + (0.18 * strength))}*exp(-14*mod(t,1.25)/{d})':contrast={F(1.16 + (0.10 * strength))}:saturation={F(1.10 + (0.10 * strength))}",

                TransitionEffectType.Glitch =>
                    $"rgbashift=rh={Math.Clamp((int)Math.Round(8 + (8 * strength)), 8, 16)}:rv=2:gh=0:bh={-Math.Clamp((int)Math.Round(8 + (8 * strength)), 8, 16)}:bv=-2:edge=smear:enable='lt(mod(t,0.62),0.22)'," +
                    $"noise=alls={F(8 + (8 * strength))}:allf=t+u:enable='lt(mod(t,0.62),0.22)'," +
                    $"hue=h='{F(14 + (10 * strength))}*sin(24*t)':s={F(1.14 + (0.12 * strength))}",

                TransitionEffectType.LightLeak =>
                    $"eq=brightness='{F(0.18 + (0.10 * strength))}*exp(-7*mod(t,1.45)/{d})':contrast={F(1.10 + (0.08 * strength))}:saturation={F(1.14 + (0.12 * strength))}," +
                    "colorbalance=rs=0.10:gs=0.025:bs=-0.055",

                _ => string.Empty
            };
        }

        private static int EnsureEven(int value)
        {
            int safe = Math.Max(2, value);
            return (safe & 1) == 0 ? safe : safe + 1;
        }

        private static string F(double value)
            => value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
