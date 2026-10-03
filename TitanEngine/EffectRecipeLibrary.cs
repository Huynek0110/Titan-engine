using System;
using System.Collections.Generic;
using System.Linq;

namespace TitanEngine
{
    public static class EffectRecipeLibrary
    {
        private static readonly HashSet<string> RetiredRecipeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "filmgrain35mm",
            "vhsretro",
            "glitchrgb",
            "crtscanline",
            "chromaticaberrationsubtle",
            "lensvignette",
            "lightsweepluxury",
            "lightleaktransition",
            "glitchtransition",
            "zoompunch",
            "shakehit",
            "flashpop",
            "lightleaksoft",
            "filmburnwarm",
            "snowsoft",
            "snowbokeh",
            "raincinematic",
            "sparklesoft",
            "confettiburst",
            "bokehdream",
            "dustscratchretro"
        };

        private static readonly IReadOnlyList<OverlayEffectRecipe> OverlayRecipes = new[]
        {
            new OverlayEffectRecipe
            {
                Id = "FilmBurnWarm",
                Name = "FilmBurnWarm",
                Category = "overlayAsset",
                AssetPath = @"Assets\Overlays\LightLeaks\light_leak_warm_01.mp4",
                AssetType = OverlayAssetType.BlackBackground,
                BlendMode = OverlayBlendMode.Screen,
                Opacity = 0.14,
                TimingMode = OverlayTimingMode.Burst,
                Placement = OverlayPlacement.FullFrame,
                ScaleMode = OverlayScaleMode.Cover,
                Duration = 0.75,
                FadeIn = 0.08,
                FadeOut = 0.22,
                LegacyResolverKey = "light-leak-overlay",
                LegacyAssetId = "FilmBurnWarm",
                LegacyMode = "burst",
                Aliases = new List<string> { "film burn warm", "warm film burn", "warm leak burn" }
            },
            new OverlayEffectRecipe
            {
                Id = "DustScratchRetro",
                Name = "DustScratchRetro",
                Category = "overlayGenerated",
                AssetType = OverlayAssetType.Generated,
                BlendMode = OverlayBlendMode.Normal,
                Opacity = 0.26,
                TimingMode = OverlayTimingMode.FullTimeline,
                Placement = OverlayPlacement.FullFrame,
                ScaleMode = OverlayScaleMode.Stretch,
                FadeIn = 0.08,
                FadeOut = 0.12,
                LegacyResolverKey = "scratch-video",
                Aliases = new List<string> { "dust scratch retro", "retro dust scratch", "35mm dust scratch" }
            }
        };

        private static readonly IReadOnlyList<ParticleEffectRecipe> ParticleRecipes = new[]
        {
            new ParticleEffectRecipe
            {
                Id = "SnowSoft",
                Name = "SnowSoft",
                Category = "particleOverlay",
                ParticleType = ParticleType.Snow,
                Count = 84,
                SizeRange = new EffectRange(0.8, 6.0),
                OpacityRange = new EffectRange(0.04, 0.18),
                SpeedXRange = new EffectRange(-10.0, 10.0),
                SpeedYRange = new EffectRange(24.0, 74.0),
                WindRange = new EffectRange(-8.0, 8.0),
                BlurRange = new EffectRange(0.0, 2.0),
                Shape = ParticleShape.Circle,
                OutputMode = ParticleOutputMode.PngSequence,
                ColorPalette = new List<string> { "#FFFFFF", "#F4F8FF", "#FFF8EF" },
                Layers = new List<ParticleLayerRecipe>
                {
                    new ParticleLayerRecipe { Layer = "far", Count = 46, SizeRange = new EffectRange(0.8, 2.0), OpacityRange = new EffectRange(0.04, 0.10), SpeedXRange = new EffectRange(-6.0, 6.0), SpeedYRange = new EffectRange(20.0, 40.0), WindRange = new EffectRange(-4.0, 4.0), BlurRange = new EffectRange(0.0, 0.4), Shape = ParticleShape.Circle },
                    new ParticleLayerRecipe { Layer = "mid", Count = 26, SizeRange = new EffectRange(1.2, 3.8), OpacityRange = new EffectRange(0.06, 0.14), SpeedXRange = new EffectRange(-9.0, 9.0), SpeedYRange = new EffectRange(28.0, 58.0), WindRange = new EffectRange(-7.0, 7.0), BlurRange = new EffectRange(0.3, 1.0), Shape = ParticleShape.Circle },
                    new ParticleLayerRecipe { Layer = "near", Count = 12, SizeRange = new EffectRange(3.4, 6.0), OpacityRange = new EffectRange(0.06, 0.18), SpeedXRange = new EffectRange(-10.0, 10.0), SpeedYRange = new EffectRange(36.0, 74.0), WindRange = new EffectRange(-8.0, 8.0), BlurRange = new EffectRange(1.0, 2.0), Shape = ParticleShape.Circle }
                },
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["cycleSeconds"] = "4.8"
                },
                Aliases = new List<string> { "snow soft", "soft snow", "soft snowfall" }
            },
            new ParticleEffectRecipe
            {
                Id = "SnowBokeh",
                Name = "SnowBokeh",
                Category = "particleOverlay",
                ParticleType = ParticleType.Snow,
                Count = 64,
                SizeRange = new EffectRange(1.0, 16.0),
                OpacityRange = new EffectRange(0.04, 0.16),
                SpeedXRange = new EffectRange(-8.0, 10.0),
                SpeedYRange = new EffectRange(18.0, 60.0),
                WindRange = new EffectRange(-6.0, 8.0),
                BlurRange = new EffectRange(0.2, 4.2),
                Shape = ParticleShape.Circle,
                OutputMode = ParticleOutputMode.PngSequence,
                ColorPalette = new List<string> { "#FFFFFF", "#F3F8FF", "#FFF3EA" },
                Layers = new List<ParticleLayerRecipe>
                {
                    new ParticleLayerRecipe { Layer = "far", Count = 28, SizeRange = new EffectRange(1.0, 2.6), OpacityRange = new EffectRange(0.04, 0.08), SpeedXRange = new EffectRange(-5.0, 5.0), SpeedYRange = new EffectRange(18.0, 34.0), WindRange = new EffectRange(-4.0, 4.0), BlurRange = new EffectRange(0.0, 0.6), Shape = ParticleShape.Circle },
                    new ParticleLayerRecipe { Layer = "mid", Count = 20, SizeRange = new EffectRange(3.0, 8.0), OpacityRange = new EffectRange(0.05, 0.10), SpeedXRange = new EffectRange(-6.0, 6.0), SpeedYRange = new EffectRange(22.0, 42.0), WindRange = new EffectRange(-5.0, 5.0), BlurRange = new EffectRange(1.0, 2.4), Shape = ParticleShape.Circle },
                    new ParticleLayerRecipe { Layer = "near", Count = 16, SizeRange = new EffectRange(8.0, 16.0), OpacityRange = new EffectRange(0.06, 0.16), SpeedXRange = new EffectRange(-8.0, 8.0), SpeedYRange = new EffectRange(26.0, 60.0), WindRange = new EffectRange(-6.0, 8.0), BlurRange = new EffectRange(2.0, 4.2), Shape = ParticleShape.Circle }
                },
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["cycleSeconds"] = "4.4"
                },
                Aliases = new List<string> { "snow bokeh", "bokeh snow", "dream snow" }
            },
            new ParticleEffectRecipe
            {
                Id = "RainCinematic",
                Name = "RainCinematic",
                Category = "particleOverlay",
                ParticleType = ParticleType.Rain,
                Count = 120,
                SizeRange = new EffectRange(6.0, 24.0),
                OpacityRange = new EffectRange(0.05, 0.18),
                SpeedXRange = new EffectRange(-28.0, -10.0),
                SpeedYRange = new EffectRange(140.0, 260.0),
                WindRange = new EffectRange(-22.0, -8.0),
                BlurRange = new EffectRange(0.0, 0.8),
                Shape = ParticleShape.Streak,
                OutputMode = ParticleOutputMode.PngSequence,
                ColorPalette = new List<string> { "#D6E4F8", "#E8F1FF", "#FFFFFF" },
                Layers = new List<ParticleLayerRecipe>
                {
                    new ParticleLayerRecipe { Layer = "far", Count = 52, SizeRange = new EffectRange(6.0, 12.0), OpacityRange = new EffectRange(0.05, 0.10), SpeedXRange = new EffectRange(-20.0, -8.0), SpeedYRange = new EffectRange(120.0, 180.0), WindRange = new EffectRange(-18.0, -8.0), BlurRange = new EffectRange(0.0, 0.4), Shape = ParticleShape.Streak },
                    new ParticleLayerRecipe { Layer = "mid", Count = 42, SizeRange = new EffectRange(10.0, 18.0), OpacityRange = new EffectRange(0.06, 0.14), SpeedXRange = new EffectRange(-24.0, -10.0), SpeedYRange = new EffectRange(160.0, 220.0), WindRange = new EffectRange(-20.0, -10.0), BlurRange = new EffectRange(0.2, 0.6), Shape = ParticleShape.Streak },
                    new ParticleLayerRecipe { Layer = "near", Count = 26, SizeRange = new EffectRange(14.0, 24.0), OpacityRange = new EffectRange(0.08, 0.18), SpeedXRange = new EffectRange(-28.0, -12.0), SpeedYRange = new EffectRange(190.0, 260.0), WindRange = new EffectRange(-22.0, -12.0), BlurRange = new EffectRange(0.3, 0.8), Shape = ParticleShape.Streak }
                },
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["cycleSeconds"] = "3.6"
                },
                Aliases = new List<string> { "rain cinematic", "cinematic rain", "rain overlay" }
            },
            new ParticleEffectRecipe
            {
                Id = "SparkleSoft",
                Name = "SparkleSoft",
                Category = "particleOverlay",
                ParticleType = ParticleType.Sparkle,
                Count = 56,
                SizeRange = new EffectRange(2.4, 14.0),
                OpacityRange = new EffectRange(0.08, 0.34),
                SpeedXRange = new EffectRange(-8.0, 8.0),
                SpeedYRange = new EffectRange(-10.0, 22.0),
                WindRange = new EffectRange(-6.0, 6.0),
                BlurRange = new EffectRange(0.0, 1.4),
                Shape = ParticleShape.Star,
                OutputMode = ParticleOutputMode.PngSequence,
                ColorPalette = new List<string> { "#FFFFFF", "#FFF4C8", "#FFE8FA", "#DDF7FF" },
                Layers = new List<ParticleLayerRecipe>
                {
                    new ParticleLayerRecipe { Layer = "far", Count = 18, SizeRange = new EffectRange(2.4, 5.0), OpacityRange = new EffectRange(0.08, 0.16), SpeedXRange = new EffectRange(-4.0, 4.0), SpeedYRange = new EffectRange(-4.0, 12.0), WindRange = new EffectRange(-3.0, 3.0), BlurRange = new EffectRange(0.0, 0.5), Shape = ParticleShape.Circle },
                    new ParticleLayerRecipe { Layer = "mid", Count = 22, SizeRange = new EffectRange(5.0, 9.0), OpacityRange = new EffectRange(0.10, 0.24), SpeedXRange = new EffectRange(-6.0, 6.0), SpeedYRange = new EffectRange(-6.0, 16.0), WindRange = new EffectRange(-4.0, 4.0), BlurRange = new EffectRange(0.2, 0.9), Shape = ParticleShape.Star },
                    new ParticleLayerRecipe { Layer = "near", Count = 16, SizeRange = new EffectRange(8.0, 14.0), OpacityRange = new EffectRange(0.14, 0.34), SpeedXRange = new EffectRange(-8.0, 8.0), SpeedYRange = new EffectRange(-10.0, 22.0), WindRange = new EffectRange(-6.0, 6.0), BlurRange = new EffectRange(0.4, 1.5), Shape = ParticleShape.Star }
                },
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["cycleSeconds"] = "3.8",
                    ["twinkleRate"] = "1.8"
                },
                Aliases = new List<string> { "sparkle soft", "soft sparkle", "pixie dust", "glitter soft" }
            },
            new ParticleEffectRecipe
            {
                Id = "ConfettiBurst",
                Name = "ConfettiBurst",
                Category = "particleOverlay",
                ParticleType = ParticleType.Confetti,
                Count = 64,
                SizeRange = new EffectRange(4.0, 14.0),
                OpacityRange = new EffectRange(0.12, 0.30),
                SpeedXRange = new EffectRange(-180.0, 180.0),
                SpeedYRange = new EffectRange(-240.0, 40.0),
                WindRange = new EffectRange(-22.0, 22.0),
                BlurRange = new EffectRange(0.0, 0.6),
                Shape = ParticleShape.Rectangle,
                OutputMode = ParticleOutputMode.PngSequence,
                ColorPalette = new List<string> { "#FF4D6D", "#FFD166", "#06D6A0", "#118AB2", "#F7F7FF" },
                Layers = new List<ParticleLayerRecipe>
                {
                    new ParticleLayerRecipe { Layer = "mid", Count = 40, SizeRange = new EffectRange(4.0, 9.0), OpacityRange = new EffectRange(0.12, 0.24), SpeedXRange = new EffectRange(-160.0, 160.0), SpeedYRange = new EffectRange(-220.0, 20.0), WindRange = new EffectRange(-18.0, 18.0), BlurRange = new EffectRange(0.0, 0.4), Shape = ParticleShape.Rectangle },
                    new ParticleLayerRecipe { Layer = "near", Count = 24, SizeRange = new EffectRange(7.0, 14.0), OpacityRange = new EffectRange(0.14, 0.30), SpeedXRange = new EffectRange(-180.0, 180.0), SpeedYRange = new EffectRange(-240.0, 40.0), WindRange = new EffectRange(-22.0, 22.0), BlurRange = new EffectRange(0.0, 0.6), Shape = ParticleShape.Rectangle }
                },
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["cycleSeconds"] = "2.2",
                    ["spawnMode"] = "burst"
                },
                Aliases = new List<string> { "confetti burst", "confetti", "party confetti" }
            }
        };

        private static readonly IReadOnlyList<PostProcessEffectRecipe> PostProcessRecipes = new[]
        {
            new PostProcessEffectRecipe
            {
                Id = "VHSRetro",
                Name = "VHSRetro",
                Category = "postProcess",
                EffectType = PostProcessEffectType.Vhs,
                Intensity = 0.46,
                Seed = 8080,
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["chromaShift"] = "2.6",
                    ["noiseBase"] = "10.0"
                },
                Aliases = new List<string> { "vhs retro", "vhs", "retro tape" }
            },
            new PostProcessEffectRecipe
            {
                Id = "GlitchRGB",
                Name = "GlitchRGB",
                Category = "postProcess",
                EffectType = PostProcessEffectType.RgbSplit,
                Intensity = 0.42,
                Seed = 2401,
                EnableTimeRange = true,
                StartTime = 0.0,
                EndTime = 0.0,
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["shiftPixels"] = "4.5",
                    ["noiseBase"] = "7.0"
                },
                Aliases = new List<string> { "glitch rgb", "rgb split", "rgb glitch" }
            },
            new PostProcessEffectRecipe
            {
                Id = "CRTScanline",
                Name = "CRTScanline",
                Category = "postProcess",
                EffectType = PostProcessEffectType.Crt,
                Intensity = 0.28,
                Seed = 909,
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["scanlineSpacing"] = "4"
                },
                Aliases = new List<string> { "crt scanline", "crt", "old tv" }
            },
            new PostProcessEffectRecipe
            {
                Id = "LightSweepLuxury",
                Name = "LightSweepLuxury",
                Category = "postProcess",
                EffectType = PostProcessEffectType.LightSweep,
                Intensity = 0.22,
                Seed = 512,
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["periodSeconds"] = "3.8"
                },
                Aliases = new List<string> { "light sweep luxury", "luxury light sweep", "editorial sweep" }
            }
        };

        private static readonly IReadOnlyList<TransitionRecipe> TransitionRecipes = new[]
        {
            new TransitionRecipe
            {
                Id = "LightLeakTransition",
                Name = "LightLeakTransition",
                Category = "transition",
                EffectType = TransitionEffectType.LightLeak,
                Duration = 0.90,
                Aliases = new List<string> { "light leak transition", "film burn transition" }
            },
            new TransitionRecipe
            {
                Id = "GlitchTransition",
                Name = "GlitchTransition",
                Category = "transition",
                EffectType = TransitionEffectType.Glitch,
                Duration = 0.24,
                Aliases = new List<string> { "glitch transition" }
            },
            new TransitionRecipe
            {
                Id = "ZoomPunch",
                Name = "ZoomPunch",
                Category = "transition",
                EffectType = TransitionEffectType.ZoomPunch,
                Duration = 0.20,
                Aliases = new List<string> { "zoom punch", "blur zoom", "zoom hit" }
            },
            new TransitionRecipe
            {
                Id = "ShakeHit",
                Name = "ShakeHit",
                Category = "transition",
                EffectType = TransitionEffectType.Shake,
                Duration = 0.18,
                Aliases = new List<string> { "shake hit", "shake transition" }
            },
            new TransitionRecipe
            {
                Id = "FlashPop",
                Name = "FlashPop",
                Category = "transition",
                EffectType = TransitionEffectType.FlashPop,
                Duration = 0.16,
                Aliases = new List<string> { "flash pop", "flash pop transition" }
            }
        };

        private static readonly IReadOnlyList<TemplateEffectStackRecipe> TemplateStacks = new[]
        {
            new TemplateEffectStackRecipe
            {
                TemplateName = "Digicam Memory",
                OverlayEffectIds = new List<string> { "DustScratchRetro" },
                PostProcessEffectIds = new List<string> { "VHSRetro" }
            },
            new TemplateEffectStackRecipe
            {
                TemplateName = "Polaroid Scrapbook",
                OverlayEffectIds = new List<string> { "LightLeakSoft" },
                PostProcessEffectIds = new List<string>()
            },
            new TemplateEffectStackRecipe
            {
                TemplateName = "Beat Photo Dump",
                ParticleEffectIds = new List<string> { "SparkleSoft" },
                PostProcessEffectIds = new List<string>(),
                TransitionEffectIds = new List<string> { "FlashPop", "ShakeHit" }
            },
            new TemplateEffectStackRecipe
            {
                TemplateName = "Film Strip",
                OverlayEffectIds = new List<string> { "LightLeakSoft" },
                PostProcessEffectIds = new List<string>()
            },
            new TemplateEffectStackRecipe
            {
                TemplateName = "Magazine Cover",
                ParticleEffectIds = new List<string>(),
                PostProcessEffectIds = new List<string> { "LightSweepLuxury" }
            },
            new TemplateEffectStackRecipe
            {
                TemplateName = "Before/After",
                PostProcessEffectIds = new List<string>(),
                TransitionEffectIds = new List<string> { "ZoomPunch" }
            }
        };

        private static readonly Dictionary<string, OverlayEffectRecipe> OverlayLookup = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ParticleEffectRecipe> ParticleLookup = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, PostProcessEffectRecipe> PostProcessLookup = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, TransitionRecipe> TransitionLookup = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, TemplateEffectStackRecipe> TemplateLookup = new(StringComparer.OrdinalIgnoreCase);

        public static bool IsRetiredRecipeName(string? input)
            => RetiredRecipeNames.Contains(NormalizeKey(input));

        public static bool TryResolveCanonicalEffectName(string? input, out string canonicalName)
        {
            canonicalName = string.Empty;
            string key = NormalizeKey(input);
            if (string.IsNullOrWhiteSpace(key))
                return false;

            if (OverlayLookup.TryGetValue(key, out OverlayEffectRecipe? overlay))
            {
                canonicalName = overlay.Name;
                return true;
            }

            if (ParticleLookup.TryGetValue(key, out ParticleEffectRecipe? particle))
            {
                canonicalName = particle.Name;
                return true;
            }

            if (PostProcessLookup.TryGetValue(key, out PostProcessEffectRecipe? postProcess))
            {
                canonicalName = postProcess.Name;
                return true;
            }

            if (TransitionLookup.TryGetValue(key, out TransitionRecipe? transition))
            {
                canonicalName = transition.Name;
                return true;
            }

            return false;
        }

        public static bool TryGetOverlayEffectRecipe(string? input, out OverlayEffectRecipe recipe)
            => OverlayLookup.TryGetValue(NormalizeKey(input), out recipe!);

        public static bool TryGetParticleEffectRecipe(string? input, out ParticleEffectRecipe recipe)
            => ParticleLookup.TryGetValue(NormalizeKey(input), out recipe!);

        public static bool TryGetPostProcessEffectRecipe(string? input, out PostProcessEffectRecipe recipe)
            => PostProcessLookup.TryGetValue(NormalizeKey(input), out recipe!);

        public static bool TryGetTransitionRecipe(string? input, out TransitionRecipe recipe)
            => TransitionLookup.TryGetValue(NormalizeKey(input), out recipe!);

        public static TemplateEffectStackRecipe? ResolveTemplateStack(string? templateName)
        {
            string key = NormalizeKey(templateName);
            if (string.IsNullOrWhiteSpace(key))
                return null;

            return TemplateLookup.TryGetValue(key, out TemplateEffectStackRecipe? recipe) ? recipe : null;
        }

        private static Dictionary<string, TRecipe> BuildLookup<TRecipe>(IEnumerable<TRecipe> recipes)
            where TRecipe : EffectRecipe
        {
            var lookup = new Dictionary<string, TRecipe>(StringComparer.OrdinalIgnoreCase);
            foreach (TRecipe recipe in recipes)
            {
                AddLookupKey(lookup, recipe.Id, recipe);
                AddLookupKey(lookup, recipe.Name, recipe);
                foreach (string alias in recipe.Aliases)
                    AddLookupKey(lookup, alias, recipe);
            }

            return lookup;
        }

        private static void AddLookupKey<TRecipe>(IDictionary<string, TRecipe> lookup, string? value, TRecipe recipe)
            where TRecipe : EffectRecipe
        {
            string key = NormalizeKey(value);
            if (string.IsNullOrWhiteSpace(key))
                return;

            lookup[key] = recipe;
        }

        private static string NormalizeKey(string? input)
        {
            string value = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var chars = value.Where(char.IsLetterOrDigit).ToArray();
            return new string(chars);
        }
    }
}
