using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TitanEngine
{
    public sealed class DreamyDotOverlay2Asset
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string AssetType { get; set; } = "blackBackgroundOrDarkBokeh";
        public string BlendMode { get; set; } = "screen";
        public double DefaultOpacity { get; set; } = 0.12;
        public bool CanLoop { get; set; } = true;
        public string TimingMode { get; set; } = "fullTimelineSubtle";
        public string Category { get; set; } = "dreamyDots";
    }

    public sealed class DreamyDotOverlay2Recipe
    {
        public string Id { get; set; } = DreamyDotOverlay2Preset.PresetId;
        public string Name { get; set; } = DreamyDotOverlay2Preset.PresetName;
        public DreamyDotOverlay2Asset Asset { get; set; } = new DreamyDotOverlay2Asset();
        public double Opacity { get; set; } = 0.12;
        public OverlayAssetBackgroundAnalysis? RuntimeAnalysis { get; set; }
    }

    public static class DreamyDotOverlay2Preset
    {
        public const string PresetId = "DreamyDotOverlay2";
        public const string PresetName = "Dreamy Dot Overlay 2";
        public const string AssetRelativePath = @"Assets\Overlays\DreamyDots\dreamy_dot_overlay_02.mp4";
        private const string AssetFileName = "dreamy_dot_overlay_02.mp4";
        private const string AssetType = "blackBackgroundOrDarkBokeh";
        private const string BlendMode = "screen";
        private const double DefaultOpacity = 0.12;
        private const double MinOpacity = 0.06;
        private const double MaxOpacity = 0.18;

        public static DreamyDotOverlay2Recipe? ResolveRecipe(Action<string> onLog)
        {
            string? resolvedPath = ResolveAssetPath();
            if (string.IsNullOrWhiteSpace(resolvedPath))
                return null;

            return new DreamyDotOverlay2Recipe
            {
                Id = PresetId,
                Name = PresetName,
                Opacity = DefaultOpacity,
                Asset = new DreamyDotOverlay2Asset
                {
                    Id = PresetId,
                    Name = PresetName,
                    RelativePath = AssetRelativePath,
                    FilePath = resolvedPath,
                    AssetType = AssetType,
                    BlendMode = BlendMode,
                    DefaultOpacity = DefaultOpacity,
                    CanLoop = true,
                    TimingMode = "fullTimelineSubtle",
                    Category = "dreamyDots"
                }
            };
        }

        public static string BuildSingleClipGraph(
            string baseInputLabel,
            string baseVideoFilters,
            string overlayInputLabel,
            DreamyDotOverlay2Recipe recipe,
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
            string safeOutput = string.IsNullOrWhiteSpace(outputLabel) ? "dreamydot2out" : outputLabel;
            int safeWidth = Math.Max(2, canvasWidth);
            int safeHeight = Math.Max(2, canvasHeight);
            double safeFps = Math.Clamp(frameRate, 10.0, 120.0);
            double safeDuration = Math.Max(0.10, outputDurationSeconds);
            double safeOpacity = Math.Clamp(recipe.Opacity, MinOpacity, MaxOpacity);

            OverlayBackgroundType backgroundType = recipe.RuntimeAnalysis?.BackgroundType ?? OverlayBackgroundType.BlackBackground;
            bool useAlphaOverlay = backgroundType == OverlayBackgroundType.Alpha || backgroundType == OverlayBackgroundType.GreenScreen;
            double safeRuntimeOpacity = backgroundType == OverlayBackgroundType.Unknown
                ? Math.Clamp(recipe.RuntimeAnalysis?.OpacityOverride ?? safeOpacity, 0.08, 0.12)
                : safeOpacity;

            string baseChain = string.IsNullOrWhiteSpace(baseVideoFilters)
                ? $"{safeBaseInput}format={(useAlphaOverlay ? "rgba" : "gbrp")}[dreamydot2base]"
                : $"{safeBaseInput}{baseVideoFilters},format={(useAlphaOverlay ? "rgba" : "gbrp")}[dreamydot2base]";

            string sourceChain =
                $"{safeOverlayInput}scale={safeWidth}:{safeHeight}:force_original_aspect_ratio=increase," +
                $"crop={safeWidth}:{safeHeight},fps={safeFps.ToString("F3", inv)},setsar=1,format={(useAlphaOverlay ? "rgba" : "gbrp")}," +
                $"trim=duration={safeDuration.ToString("F3", inv)},setpts=PTS-STARTPTS[dreamydot2src]";

            string mergeChain = backgroundType switch
            {
                OverlayBackgroundType.Alpha =>
                    $"[dreamydot2src]colorchannelmixer=aa={safeRuntimeOpacity.ToString("F3", inv)}[dreamydot2alpha];[dreamydot2base][dreamydot2alpha]overlay=0:0:shortest=0:eof_action=repeat:format=auto[{safeOutput}]",
                OverlayBackgroundType.GreenScreen =>
                    $"[dreamydot2src]chromakey=0x00FF00:{Math.Clamp(recipe.RuntimeAnalysis?.Similarity ?? 0.18, 0.08, 0.35).ToString("F3", inv)}:{Math.Clamp(recipe.RuntimeAnalysis?.Blend ?? 0.08, 0.00, 0.20).ToString("F3", inv)}," +
                    $"format=rgba,colorchannelmixer=aa={safeRuntimeOpacity.ToString("F3", inv)}[dreamydot2key];[dreamydot2base][dreamydot2key]overlay=0:0:shortest=0:eof_action=repeat:format=auto[{safeOutput}]",
                _ =>
                    $"[dreamydot2base][dreamydot2src]blend=all_mode={ResolveBlendMode(recipe.Asset.BlendMode)}:all_opacity={safeRuntimeOpacity.ToString("F3", inv)}[{safeOutput}]"
            };

            onLog($"[DREAMY-DOT-2] Asset graph ready: background={backgroundType}, pipeline={recipe.RuntimeAnalysis?.PipelineName ?? "legacy"}, blend={recipe.Asset.BlendMode}, opacity={safeRuntimeOpacity:0.000}, canvas={safeWidth}x{safeHeight}, fps={safeFps:F2}");
            return $"{baseChain};{sourceChain};{mergeChain}";
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

        private static IEnumerable<string> EnumerateAssetRoots()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            yield return Path.Combine(baseDir, "Assets", "Overlays", "DreamyDots");
            yield return Path.Combine(baseDir, "Content", "Overlays", "DreamyDots");

            string? parent = Directory.GetParent(baseDir)?.FullName;
            for (int i = 0; i < 5 && !string.IsNullOrWhiteSpace(parent); i++)
            {
                yield return Path.Combine(parent, "Assets", "Overlays", "DreamyDots");
                yield return Path.Combine(parent, "Content", "Overlays", "DreamyDots");
                parent = Directory.GetParent(parent)?.FullName;
            }

            yield return Path.Combine(Environment.CurrentDirectory, "Assets", "Overlays", "DreamyDots");
            yield return Path.Combine(Environment.CurrentDirectory, "Content", "Overlays", "DreamyDots");
            yield return Path.Combine(Environment.CurrentDirectory, "TitanEngine", "Assets", "Overlays", "DreamyDots");
            yield return Path.Combine(Environment.CurrentDirectory, "TitanEngine", "Content", "Overlays", "DreamyDots");
        }

        private static string ResolveBlendMode(string? blendMode)
        {
            string normalized = (blendMode ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "lighten" => "lighten",
                _ => "screen"
            };
        }
    }
}
