using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TitanEngine
{
    public enum OverlayAssetType
    {
        BlackBackground,
        Alpha,
        Generated
    }

    public enum OverlayBlendMode
    {
        Screen,
        Lighten,
        Add,
        Normal
    }

    public enum OverlayTimingMode
    {
        FullTimeline,
        Burst,
        Transition,
        BeatCut
    }

    public enum OverlayPlacement
    {
        FullFrame,
        LeftEdge,
        RightEdge,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
        Center
    }

    public enum OverlayScaleMode
    {
        Cover,
        Contain,
        Stretch
    }

    public enum ParticleType
    {
        Snow,
        Rain,
        Sparkle,
        Confetti,
        Firework,
        Bokeh
    }

    public enum ParticleShape
    {
        Circle,
        Streak,
        Star,
        Heart,
        Rectangle,
        CustomSvg
    }

    public enum ParticleOutputMode
    {
        AlphaWebm,
        BlackBackgroundMp4,
        PngSequence
    }

    public enum PostProcessEffectType
    {
        Vhs,
        Glitch,
        RgbSplit,
        ChromaticAberration,
        Vignette,
        LensDistortion,
        Grain,
        Blur,
        LightSweep,
        Crt
    }

    public enum TransitionEffectType
    {
        LightLeak,
        Glitch,
        BlurZoom,
        ZoomPunch,
        Shake,
        FlashPop
    }

    public readonly record struct EffectRange(double Min, double Max)
    {
        public double Clamp(double value)
            => Math.Clamp(value, Min, Max);

        public double Lerp(double t)
            => Min + ((Max - Min) * Math.Clamp(t, 0.0, 1.0));

        public override string ToString()
            => $"{Min.ToString("0.###", CultureInfo.InvariantCulture)}..{Max.ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    public abstract class EffectRecipe
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public List<string> Aliases { get; init; } = new List<string>();
    }

    public sealed class OverlayEffectRecipe : EffectRecipe
    {
        public string AssetPath { get; init; } = string.Empty;
        public OverlayAssetType AssetType { get; init; } = OverlayAssetType.BlackBackground;
        public OverlayBlendMode BlendMode { get; init; } = OverlayBlendMode.Screen;
        public double Opacity { get; init; } = 0.18;
        public OverlayTimingMode TimingMode { get; init; } = OverlayTimingMode.FullTimeline;
        public OverlayPlacement Placement { get; init; } = OverlayPlacement.FullFrame;
        public OverlayScaleMode ScaleMode { get; init; } = OverlayScaleMode.Cover;
        public double StartTime { get; init; } = 0.0;
        public double Duration { get; init; } = 0.0;
        public double FadeIn { get; init; } = 0.12;
        public double FadeOut { get; init; } = 0.18;
        public string? LinkedParticleRecipeId { get; init; }
        public string? LegacyResolverKey { get; init; }
        public string? LegacyAssetId { get; init; }
        public string? LegacyMode { get; init; }
    }

    public sealed class ParticleLayerRecipe
    {
        public string Layer { get; init; } = "mid";
        public int Count { get; init; }
        public EffectRange SizeRange { get; init; } = new(1.0, 4.0);
        public EffectRange OpacityRange { get; init; } = new(0.08, 0.24);
        public EffectRange SpeedXRange { get; init; } = new(-10.0, 10.0);
        public EffectRange SpeedYRange { get; init; } = new(20.0, 80.0);
        public EffectRange WindRange { get; init; } = new(-8.0, 8.0);
        public EffectRange BlurRange { get; init; } = new(0.0, 2.0);
        public ParticleShape Shape { get; init; } = ParticleShape.Circle;
    }

    public sealed class ParticleEffectRecipe : EffectRecipe
    {
        public ParticleType ParticleType { get; init; } = ParticleType.Snow;
        public List<ParticleLayerRecipe> Layers { get; init; } = new List<ParticleLayerRecipe>();
        public int Count { get; init; }
        public EffectRange SizeRange { get; init; } = new(1.0, 4.0);
        public EffectRange OpacityRange { get; init; } = new(0.08, 0.22);
        public EffectRange SpeedXRange { get; init; } = new(-10.0, 10.0);
        public EffectRange SpeedYRange { get; init; } = new(20.0, 80.0);
        public EffectRange WindRange { get; init; } = new(-8.0, 8.0);
        public EffectRange BlurRange { get; init; } = new(0.0, 2.0);
        public List<string> ColorPalette { get; init; } = new List<string>();
        public ParticleShape Shape { get; init; } = ParticleShape.Circle;
        public ParticleOutputMode OutputMode { get; init; } = ParticleOutputMode.PngSequence;
        public Dictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string BuildCacheKey(int width, int height, double fps, double durationSeconds)
        {
            string payload = JsonSerializer.Serialize(new
            {
                RendererVersion = 4,
                Id,
                ParticleType,
                Count,
                SizeRange,
                OpacityRange,
                SpeedXRange,
                SpeedYRange,
                WindRange,
                BlurRange,
                Shape,
                OutputMode,
                ColorPalette,
                Layers,
                width,
                height,
                fps = Math.Round(fps, 3),
                durationSeconds = Math.Round(durationSeconds, 3),
                Parameters
            });

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
            return Convert.ToHexString(hash).ToLowerInvariant()[..24];
        }
    }

    public sealed class PostProcessEffectRecipe : EffectRecipe
    {
        public PostProcessEffectType EffectType { get; init; } = PostProcessEffectType.Grain;
        public double Intensity { get; init; } = 0.35;
        public int Seed { get; init; } = 1337;
        public bool EnableTimeRange { get; init; }
        public double StartTime { get; init; } = 0.0;
        public double EndTime { get; init; } = 0.0;
        public Dictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class TransitionRecipe : EffectRecipe
    {
        public TransitionEffectType EffectType { get; init; } = TransitionEffectType.FlashPop;
        public double Duration { get; init; } = 0.18;
        public Dictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class TemplateEffectStackRecipe
    {
        public string TemplateName { get; init; } = string.Empty;
        public List<string> OverlayEffectIds { get; init; } = new List<string>();
        public List<string> ParticleEffectIds { get; init; } = new List<string>();
        public List<string> PostProcessEffectIds { get; init; } = new List<string>();
        public List<string> TransitionEffectIds { get; init; } = new List<string>();
    }
}
