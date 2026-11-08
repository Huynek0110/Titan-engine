using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TitanEngine
{
    public sealed class DreamyDotLayerRecipe
    {
        public string Name { get; set; } = "dust";
        public int Count { get; set; }
        public double MinRadius { get; set; }
        public double MaxRadius { get; set; }
        public double MinOpacity { get; set; }
        public double MaxOpacity { get; set; }
        public double MinTravelLoops { get; set; }
        public double MaxTravelLoops { get; set; }
        public double MinStretch { get; set; }
        public double MaxStretch { get; set; }
        public double GlowScale { get; set; }
        public bool DrawCore { get; set; }
    }

    public sealed class DreamyDotOverlaySettings
    {
        public string Variant { get; set; } = "classic";
        public double CycleSeconds { get; set; } = 5.2;
        public double FrameRate { get; set; } = 30.0;
        public Vector Direction { get; set; } = new Vector(1.0, -0.24);
        public double DirectionJitter { get; set; }
        public double SwirlAmount { get; set; }
        public List<DreamyDotLayerRecipe> Layers { get; set; } = new List<DreamyDotLayerRecipe>();
    }

    public sealed class DreamyDotGeneratedOverlay
    {
        public string TempRoot { get; set; } = string.Empty;
        public string OverlayPattern { get; set; } = string.Empty;
        public int FrameCount { get; set; }
        public double FrameRate { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Variant { get; set; } = "classic";
        public string AssetCacheKey { get; set; } = string.Empty;
        public string PreRenderedAssetPath { get; set; } = string.Empty;
        public string AssetType { get; set; } = "blackBackground";
        public string BlendMode { get; set; } = "screen";
        public string PreviewDirectory { get; set; } = string.Empty;
        public List<string> TempArtifacts { get; set; } = new List<string>();
    }

    public static class DreamyDotOverlayPreset
    {

        public static DreamyDotOverlaySettings FromJob(RenderJob job)
        {
            double intensity = Math.Clamp(job.FxIntensity / 100.0, 0.0, 1.0);
            bool chaosVariant =
                (job.OverlayEffect ?? string.Empty).Contains("chaos", StringComparison.OrdinalIgnoreCase) ||
                (job.OverlayEffect ?? string.Empty).Contains("random", StringComparison.OrdinalIgnoreCase);
            double densityScale = 0.96 + (intensity * 1.08);
            double opacityScale = 1.08 + (intensity * 0.54);
            double cycleSeconds = chaosVariant
                ? Math.Clamp(4.4 - (intensity * 0.45), 3.8, 4.6)
                : Math.Clamp(5.2 - (intensity * 0.65), 4.6, 5.4);

            return new DreamyDotOverlaySettings
            {
                Variant = chaosVariant ? "chaos" : "classic",
                CycleSeconds = cycleSeconds,
                FrameRate = 18.0,
                Direction = NormalizeDirection(new Vector(1.0, -0.22)),
                DirectionJitter = chaosVariant ? (0.95 + (intensity * 0.45)) : (0.18 + (intensity * 0.08)),
                SwirlAmount = chaosVariant ? (0.85 + (intensity * 0.55)) : (0.18 + (intensity * 0.10)),
                Layers = new List<DreamyDotLayerRecipe>
                {
                    CreateLayer("dust", 48, 1.4, 3.8, 0.06, 0.14, 0.72, 1.28, 1.55, 2.18, 0.78, false, densityScale, opacityScale),
                    CreateLayer("soft", 20, 5.5, 13.5, 0.07, 0.16, 0.46, 0.90, 1.10, 1.42, 1.10, true, densityScale, opacityScale),
                    CreateLayer("bokeh", 10, 12.0, 24.0, 0.06, 0.13, 0.24, 0.52, 1.00, 1.20, 1.42, true, densityScale, opacityScale),
                    CreateLayer("accent", 4, 22.0, 44.0, 0.03, 0.08, 0.16, 0.28, 0.96, 1.12, 1.88, true, densityScale, opacityScale)
                }
            };
        }


        public static DreamyDotGeneratedOverlay CreateAssetDescriptor(
            int width,
            int height,
            double frameRate,
            double outputDurationSeconds,
            RenderJob job)
        {
            var plan = CreateOverlayPlan(width, height, frameRate, outputDurationSeconds, job);
            return new DreamyDotGeneratedOverlay
            {
                FrameCount = plan.FrameCount,
                FrameRate = plan.SafeFps,
                Width = plan.RenderWidth,
                Height = plan.RenderHeight,
                Variant = plan.Settings.Variant,
                AssetCacheKey = plan.AssetCacheKey,
                AssetType = "blackBackground",
                BlendMode = "screen"
            };
        }


        public static string BuildSingleClipGraph(
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
            string safeOutput = string.IsNullOrWhiteSpace(outputLabel) ? "dreamydotout" : outputLabel;
            string fps = Math.Clamp(frameRate, 1.0, 120.0).ToString("F3", inv);
            string duration = Math.Max(0.10, outputDurationSeconds).ToString("F3", inv);
            int safeLoopFrameCount = Math.Max(1, loopFrameCount);

            string baseChain = string.IsNullOrWhiteSpace(baseVideoFilters)
                ? $"{safeBaseInput}format=rgba[dreamydotbase]"
                : $"{safeBaseInput}{baseVideoFilters},format=rgba[dreamydotbase]";

            string overlayChain =
                $"{safeOverlayInput}format=rgba," +
                $"loop=loop=-1:size={safeLoopFrameCount}:start=0," +
                $"setpts=N/{fps}/TB," +
                $"trim=duration={duration}[dreamydotsrc]";

            string mergeChain =
                $"[dreamydotsrc][dreamydotbase]scale2ref=w=main_w:h=main_h:flags=lanczos[dreamydotscaled][dreamydotbase2];" +
                $"[dreamydotbase2][dreamydotscaled]overlay=shortest=0:eof_action=repeat:format=auto[{safeOutput}]";

            onLog($"[DREAMY-DOT] Overlay graph ready: frames={safeLoopFrameCount}, fps={fps}, duration={duration}");
            return $"{baseChain};{overlayChain};{mergeChain}";
        }


        public static string BuildAssetClipGraph(
            string baseInputLabel,
            string baseVideoFilters,
            string overlayInputLabel,
            DreamyDotGeneratedOverlay overlay,
            int canvasWidth,
            int canvasHeight,
            double frameRate,
            double outputDurationSeconds,
            string outputLabel,
            Action<string> onLog)
        {
            if (overlay == null)
                throw new ArgumentNullException(nameof(overlay));

            var inv = CultureInfo.InvariantCulture;
            string safeBaseInput = string.IsNullOrWhiteSpace(baseInputLabel) ? "[0:v]" : baseInputLabel;
            string safeOverlayInput = string.IsNullOrWhiteSpace(overlayInputLabel) ? "[1:v]" : overlayInputLabel;
            string safeOutput = string.IsNullOrWhiteSpace(outputLabel) ? "dreamydotout" : outputLabel;
            int safeWidth = Math.Max(2, canvasWidth);
            int safeHeight = Math.Max(2, canvasHeight);
            double safeFps = Math.Clamp(frameRate, 10.0, 120.0);
            double safeDuration = Math.Max(0.10, outputDurationSeconds);

            string baseChain = string.IsNullOrWhiteSpace(baseVideoFilters)
                ? $"{safeBaseInput}format=gbrp[dreamydotbase]"
                : $"{safeBaseInput}{baseVideoFilters},format=gbrp[dreamydotbase]";

            string sourceChain =
                $"{safeOverlayInput}fps={safeFps.ToString("F3", inv)}," +
                $"scale={safeWidth}:{safeHeight}:force_original_aspect_ratio=increase:flags=lanczos," +
                $"crop={safeWidth}:{safeHeight}," +
                $"setsar=1,format=gbrp," +
                $"trim=duration={safeDuration.ToString("F3", inv)},setpts=PTS-STARTPTS[dreamydotscaled]";

            string mergeChain =
                $"[dreamydotbase][dreamydotscaled]blend=all_mode=screen:all_opacity=1.000[{safeOutput}]";

            onLog($"[DREAMY-DOT] Asset graph ready: variant={overlay.Variant}, blend=screen, asset={overlay.Width}x{overlay.Height}, base={safeWidth}x{safeHeight}, fps={safeFps:F2}");
            return $"{baseChain};{sourceChain};{mergeChain}";
        }


        private static DreamyDotLayerRecipe CreateLayer(
            string name,
            int count,
            double minRadius,
            double maxRadius,
            double minOpacity,
            double maxOpacity,
            double minTravelLoops,
            double maxTravelLoops,
            double minStretch,
            double maxStretch,
            double glowScale,
            bool drawCore,
            double densityScale,
            double opacityScale)
        {
            return new DreamyDotLayerRecipe
            {
                Name = name,
                Count = Math.Max(1, (int)Math.Round(count * densityScale)),
                MinRadius = minRadius,
                MaxRadius = maxRadius,
                MinOpacity = Math.Clamp(minOpacity * opacityScale, 0.01, 0.30),
                MaxOpacity = Math.Clamp(maxOpacity * opacityScale, 0.04, 0.45),
                MinTravelLoops = minTravelLoops,
                MaxTravelLoops = maxTravelLoops,
                MinStretch = minStretch,
                MaxStretch = maxStretch,
                GlowScale = glowScale,
                DrawCore = drawCore
            };
        }


        internal static Vector ResolveParticleDirection(
            DreamyDotOverlaySettings settings,
            DreamyDotLayerRecipe layer,
            int particleIndex,
            double progress,
            Random rng)
        {
            Vector baseDirection = settings.Direction;
            if (!settings.Variant.Equals("chaos", StringComparison.OrdinalIgnoreCase))
                return baseDirection;

            double baseAngle = Math.Atan2(baseDirection.Y, baseDirection.X);
            double randomAngle = (rng.NextDouble() * Math.PI * 2.0) - Math.PI;
            double wobble = Math.Sin((progress * Math.PI * 2.0 * (0.42 + (rng.NextDouble() * 0.86))) + (particleIndex * 0.37)) * settings.DirectionJitter;
            double layerBias = layer.Name switch
            {
                "dust" => -0.22,
                "soft" => 0.18,
                "bokeh" => -0.10,
                _ => 0.0
            };

            double finalAngle = baseAngle + randomAngle + wobble + layerBias;
            return NormalizeDirection(new Vector(Math.Cos(finalAngle), Math.Sin(finalAngle)));
        }


        internal static double GetChaosLayerBias(string layerName)
        {
            return layerName switch
            {
                "dust" => -0.85,
                "soft" => 0.24,
                "bokeh" => 1.36,
                "accent" => -1.28,
                _ => 0.0
            };
        }


        internal static Vector NormalizeDirection(Vector direction)
        {
            Vector safeDirection = direction;
            if (safeDirection.LengthSquared < 0.0001)
                safeDirection = new Vector(1.0, 0.0);

            safeDirection.Normalize();
            return safeDirection;
        }


        internal static int HashSeed(string layerName, int particleIndex)
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + layerName.GetHashCode(StringComparison.Ordinal);
                hash = (hash * 31) + particleIndex;
                hash = (hash * 31) + 49157;
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


        private static int EnsureEven(int value)
        {
            int safeValue = Math.Max(2, value);
            return (safeValue & 1) == 0 ? safeValue : safeValue + 1;
        }


        internal static (DreamyDotOverlaySettings Settings, int RenderWidth, int RenderHeight, double SafeFps, int FrameCount, string AssetCacheKey) CreateOverlayPlan(
            int width,
            int height,
            double frameRate,
            double outputDurationSeconds,
            RenderJob job)
        {
            int safeW = Math.Max(2, width);
            int safeH = Math.Max(2, height);
            _ = frameRate;
            _ = outputDurationSeconds;
            DreamyDotOverlaySettings settings = FromJob(job);
            double safeFps = Math.Clamp(settings.FrameRate, 12.0, 30.0);
            (int renderW, int renderH) = ResolveRenderSize(safeW, safeH);
            int frameCount = Math.Clamp((int)Math.Ceiling(settings.CycleSeconds * safeFps), 48, 180);
            string assetCacheKey = BuildAssetCacheKey(settings, renderW, renderH, safeFps, frameCount);
            return (settings, renderW, renderH, safeFps, frameCount, assetCacheKey);
        }


        private static (int Width, int Height) ResolveRenderSize(int width, int height)
        {
            int safeW = EnsureEven(width);
            int safeH = EnsureEven(height);
            int shortestSide = Math.Min(safeW, safeH);
            if (shortestSide <= 540)
                return (safeW, safeH);

            double scale = 540.0 / shortestSide;
            int renderW = EnsureEven((int)Math.Round(safeW * scale));
            int renderH = EnsureEven((int)Math.Round(safeH * scale));
            return (renderW, renderH);
        }


        private static string BuildAssetCacheKey(
            DreamyDotOverlaySettings settings,
            int width,
            int height,
            double frameRate,
            int frameCount)
        {
            string signature =
                $"dreamydot-asset-v4|{settings.Variant}|{width}x{height}|fps={frameRate:0.###}|frames={frameCount}";
            byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(signature));
            return Convert.ToHexString(hash).Substring(0, 12).ToLowerInvariant();
        }
    }
}
