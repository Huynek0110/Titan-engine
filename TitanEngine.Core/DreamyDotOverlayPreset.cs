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
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
        private sealed class ParticleBrushSet
        {
            public Brush Halo { get; init; } = Brushes.Transparent;
            public Brush Core { get; init; } = Brushes.Transparent;
            public Brush Spark { get; init; } = Brushes.Transparent;
        }

        private static readonly Color[] Palette =
        {
            Colors.White,
            Color.FromRgb(247, 239, 219),
            Color.FromRgb(221, 242, 255),
            Color.FromRgb(255, 231, 214)
        };

        private static readonly Lazy<IReadOnlyList<ParticleBrushSet>> BrushCache =
            new(CreateBrushCache, LazyThreadSafetyMode.ExecutionAndPublication);

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

        public static async Task<DreamyDotGeneratedOverlay> GenerateOverlayAsync(
            int width,
            int height,
            OverlayContentLayout? layout,
            double frameRate,
            double outputDurationSeconds,
            RenderJob job,
            string? tag,
            CancellationToken token,
            Action<string> onLog)
        {
            string safeTag = string.IsNullOrWhiteSpace(tag)
                ? Guid.NewGuid().ToString("N").Substring(0, 8)
                : Regex.Replace(tag.Trim(), @"[^\w\-]+", "_");
            string tempRoot = Path.Combine(Path.GetTempPath(), "TitanEngine", $"dreamydot_{safeTag}");
            string overlayPattern = Path.Combine(tempRoot, "dreamy_dot_overlay_%04d.png");
            string previewDir = Path.Combine(tempRoot, "preview");
            int safeW = Math.Max(2, width);
            int safeH = Math.Max(2, height);
            var plan = CreateOverlayPlan(safeW, safeH, frameRate, outputDurationSeconds, job);
            DreamyDotOverlaySettings settings = plan.Settings;
            double safeFps = plan.SafeFps;
            int renderW = plan.RenderWidth;
            int renderH = plan.RenderHeight;
            int frameCount = plan.FrameCount;
            string assetCacheKey = plan.AssetCacheKey;
            OverlayContentLayout renderLayout = new OverlayContentLayout
            {
                CanvasWidth = renderW,
                CanvasHeight = renderH,
                ContentRect = new Rect(0, 0, renderW, renderH)
            };

            await RunOnStaThreadAsync(() =>
            {
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(previewDir);

                for (int i = 0; i < frameCount; i++)
                {
                    token.ThrowIfCancellationRequested();

                    double progress = frameCount <= 1 ? 0.0 : i / (double)frameCount;
                    DrawingVisual overlayVisual = new DrawingVisual();
                    using (DrawingContext dc = overlayVisual.RenderOpen())
                    {
                        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, renderW, renderH));
                        DrawDotLayers(dc, renderLayout, settings, progress);
                    }

                    RenderTargetBitmap overlayBitmap = new RenderTargetBitmap(renderW, renderH, 96, 96, PixelFormats.Pbgra32);
                    overlayBitmap.Render(overlayVisual);
                    if (overlayBitmap.CanFreeze) overlayBitmap.Freeze();

                    string overlayFile = FormatSequenceFilePath(overlayPattern, i + 1);
                    SaveBitmap(overlayBitmap, overlayFile);

                    if (i < 8)
                    {
                        string previewFile = Path.Combine(previewDir, $"preview_{i + 1:D2}.png");
                        SaveBitmap(overlayBitmap, previewFile);
                    }
                }
            }, token);

            onLog($"[DREAMY-DOT] Generated dreamy dot drift overlay @ {renderW}x{renderH}, fps={safeFps:0.00}, frames={frameCount}, preview={previewDir}");

            return new DreamyDotGeneratedOverlay
            {
                TempRoot = tempRoot,
                OverlayPattern = overlayPattern,
                FrameCount = frameCount,
                FrameRate = safeFps,
                Width = renderW,
                Height = renderH,
                Variant = settings.Variant,
                AssetCacheKey = assetCacheKey,
                PreviewDirectory = previewDir,
                TempArtifacts = new List<string> { tempRoot }
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

        private static void DrawDotLayers(DrawingContext dc, OverlayContentLayout layout, DreamyDotOverlaySettings settings, double progress)
        {
            Rect canvasRect = new Rect(0, 0, Math.Max(2.0, layout.CanvasWidth), Math.Max(2.0, layout.CanvasHeight));
            dc.PushClip(new RectangleGeometry(canvasRect));

            foreach (DreamyDotLayerRecipe layer in settings.Layers)
            {
                for (int i = 0; i < layer.Count; i++)
                    DrawParticle(dc, canvasRect, settings, layer, i, progress);
            }

            dc.Pop();
        }

        private static void DrawParticle(
            DrawingContext dc,
            Rect canvasRect,
            DreamyDotOverlaySettings settings,
            DreamyDotLayerRecipe layer,
            int particleIndex,
            double progress)
        {
            Random rng = new Random(HashSeed(layer.Name, particleIndex));
            double radius = Lerp(layer.MinRadius, layer.MaxRadius, rng.NextDouble());
            double opacityPulse = 0.90 + (0.24 * Math.Sin((progress * Math.PI * 2.0 * (0.55 + (rng.NextDouble() * 1.4))) + (rng.NextDouble() * Math.PI * 2.0)));
            double opacity = Math.Clamp(
                Lerp(layer.MinOpacity, layer.MaxOpacity, rng.NextDouble()) * opacityPulse,
                0.01,
                1.0);
            double travelLoops = Lerp(layer.MinTravelLoops, layer.MaxTravelLoops, rng.NextDouble());
            double stretch = Lerp(layer.MinStretch, layer.MaxStretch, rng.NextDouble());
            Point particleCenter;
            Vector direction;

            if (settings.Variant.Equals("chaos", StringComparison.OrdinalIgnoreCase))
            {
                particleCenter = ResolveChaosParticleCenter(canvasRect, settings, layer, particleIndex, progress, radius, travelLoops, out direction);
            }
            else
            {
                direction = ResolveParticleDirection(settings, layer, particleIndex, progress, rng);
                Vector perpendicular = new Vector(-direction.Y, direction.X);
                double diagonal = Math.Sqrt((canvasRect.Width * canvasRect.Width) + (canvasRect.Height * canvasRect.Height));
                double margin = Math.Max(radius * (5.0 + layer.GlowScale), 24.0);
                double pathSpan = diagonal + (margin * 2.0);
                double acrossSpan = diagonal + (margin * 2.0);
                Point center = new Point(canvasRect.X + (canvasRect.Width * 0.5), canvasRect.Y + (canvasRect.Height * 0.5));
                double alongStart = ((rng.NextDouble() * pathSpan) - (pathSpan * 0.5));
                double acrossStart = ((rng.NextDouble() * acrossSpan) - (acrossSpan * 0.5));
                double alongPosition = PositiveMod(alongStart + (progress * travelLoops * pathSpan) + (pathSpan * 0.5), pathSpan) - (pathSpan * 0.5);
                double driftPulse = Math.Sin((progress * Math.PI * 2.0 * (0.24 + (rng.NextDouble() * 0.42))) + (rng.NextDouble() * Math.PI * 2.0)) * radius * 0.45;
                particleCenter = new Point(
                    center.X + (direction.X * alongPosition) + (perpendicular.X * (acrossStart + driftPulse)),
                    center.Y + (direction.Y * alongPosition) + (perpendicular.Y * (acrossStart + driftPulse)));
            }

            double angleDegrees = Math.Atan2(direction.Y, direction.X) * (180.0 / Math.PI);

            int paletteIndex = SelectPaletteIndex(layer.Name, particleIndex);
            ParticleBrushSet brushes = BrushCache.Value[paletteIndex];
            double haloRadiusX = radius * stretch * (2.0 + (layer.GlowScale * 0.45));
            double haloRadiusY = radius * (2.0 + (layer.GlowScale * 0.45));
            double coreRadiusX = radius * stretch;
            double coreRadiusY = radius;

            dc.PushTransform(new RotateTransform(angleDegrees, particleCenter.X, particleCenter.Y));

            dc.PushOpacity(Math.Clamp(opacity * (0.56 + (layer.GlowScale * 0.12)), 0.0, 1.0));
            dc.DrawEllipse(brushes.Halo, null, particleCenter, haloRadiusX, haloRadiusY);
            dc.Pop();

            dc.PushOpacity(Math.Clamp(opacity * 1.12, 0.0, 1.0));
            dc.DrawEllipse(brushes.Core, null, particleCenter, coreRadiusX, coreRadiusY);
            dc.Pop();

            if (layer.DrawCore)
            {
                double coreGlowRadius = Math.Max(1.2, radius * 0.28);
                dc.PushOpacity(Math.Clamp(opacity * 1.06, 0.0, 1.0));
                dc.DrawEllipse(brushes.Spark, null, particleCenter, coreGlowRadius * Math.Max(1.0, stretch * 0.75), coreGlowRadius);
                dc.Pop();
            }

            dc.Pop();
        }

        private static Vector ResolveParticleDirection(
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

        private static Point ResolveChaosParticleCenter(
            Rect canvasRect,
            DreamyDotOverlaySettings settings,
            DreamyDotLayerRecipe layer,
            int particleIndex,
            double progress,
            double radius,
            double travelLoops,
            out Vector direction)
        {
            double margin = Math.Max(radius * (6.0 + layer.GlowScale), 28.0);
            double spanX = canvasRect.Width + (margin * 2.0);
            double spanY = canvasRect.Height + (margin * 2.0);
            double sampleStep = 0.0025;
            int stableSeed = HashSeed(layer.Name, particleIndex);

            Point current = EvaluateChaosParticlePosition(canvasRect, settings, layer, progress, radius, travelLoops, new Random(stableSeed), spanX, spanY, margin);
            Point ahead = EvaluateChaosParticlePosition(canvasRect, settings, layer, PositiveMod(progress + sampleStep, 1.0), radius, travelLoops, new Random(stableSeed), spanX, spanY, margin);
            direction = NormalizeDirection(new Vector(ahead.X - current.X, ahead.Y - current.Y));
            return current;
        }

        private static Point EvaluateChaosParticlePosition(
            Rect canvasRect,
            DreamyDotOverlaySettings settings,
            DreamyDotLayerRecipe layer,
            double progress,
            double radius,
            double travelLoops,
            Random rng,
            double spanX,
            double spanY,
            double margin)
        {
            double diagonal = Math.Sqrt((canvasRect.Width * canvasRect.Width) + (canvasRect.Height * canvasRect.Height));
            double layerBias = GetChaosLayerBias(layer.Name);
            double phase = rng.NextDouble();
            double localProgress = PositiveMod((progress * travelLoops) + phase, 1.0);
            double anchorX = (rng.NextDouble() * spanX) - margin;
            double anchorY = (rng.NextDouble() * spanY) - margin;

            double driftAngle = (rng.NextDouble() * Math.PI * 2.0) + layerBias;
            double driftDistance = diagonal * (0.10 + (rng.NextDouble() * 0.38) + (travelLoops * 0.18));
            double driftSpeed = 0.40 + (rng.NextDouble() * 0.90);

            double ampX1 = radius * (3.2 + settings.SwirlAmount + (rng.NextDouble() * 3.2));
            double ampX2 = radius * (1.8 + (rng.NextDouble() * 2.6));
            double ampY1 = radius * (3.0 + (settings.SwirlAmount * 1.15) + (rng.NextDouble() * 3.6));
            double ampY2 = radius * (1.7 + (rng.NextDouble() * 2.9));

            double freqX1 = 0.45 + (rng.NextDouble() * 1.75);
            double freqX2 = 0.70 + (rng.NextDouble() * 2.20);
            double freqY1 = 0.42 + (rng.NextDouble() * 1.82);
            double freqY2 = 0.66 + (rng.NextDouble() * 2.10);

            double phaseX1 = rng.NextDouble() * Math.PI * 2.0;
            double phaseX2 = rng.NextDouble() * Math.PI * 2.0;
            double phaseY1 = rng.NextDouble() * Math.PI * 2.0;
            double phaseY2 = rng.NextDouble() * Math.PI * 2.0;

            double drift = localProgress * Math.PI * 2.0 * driftSpeed;
            double driftX = Math.Cos(driftAngle) * driftDistance * Math.Sin(drift + phaseX1) * 0.55;
            double driftY = Math.Sin(driftAngle) * driftDistance * Math.Cos(drift + phaseY1) * 0.55;

            double motionX =
                (Math.Sin((localProgress * Math.PI * 2.0 * freqX1) + phaseX1) * ampX1) +
                (Math.Cos((localProgress * Math.PI * 2.0 * freqX2) + phaseX2) * ampX2);
            double motionY =
                (Math.Cos((localProgress * Math.PI * 2.0 * freqY1) + phaseY1) * ampY1) +
                (Math.Sin((localProgress * Math.PI * 2.0 * freqY2) + phaseY2) * ampY2);

            double x = PositiveMod(anchorX + driftX + motionX, spanX) - margin;
            double y = PositiveMod(anchorY + driftY + motionY, spanY) - margin;
            return new Point(canvasRect.X + x, canvasRect.Y + y);
        }

        private static double GetChaosLayerBias(string layerName)
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

        private static IReadOnlyList<ParticleBrushSet> CreateBrushCache()
        {
            var brushes = new List<ParticleBrushSet>(Palette.Length);
            foreach (Color color in Palette)
            {
                brushes.Add(new ParticleBrushSet
                {
                    Halo = CreateRadialBrush(color, 0.84, 0.28, 0.08),
                    Core = CreateRadialBrush(color, 1.00, 0.80, 0.20),
                    Spark = CreateBrush(WithAlpha(color, 1.00))
                });
            }

            return brushes;
        }

        private static Brush CreateRadialBrush(Color color, double centerAlpha, double midAlpha, double outerAlpha)
        {
            var brush = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                Center = new Point(0.50, 0.50),
                GradientOrigin = new Point(0.44, 0.43),
                RadiusX = 0.50,
                RadiusY = 0.50
            };
            brush.GradientStops.Add(new GradientStop(WithAlpha(color, centerAlpha), 0.00));
            brush.GradientStops.Add(new GradientStop(WithAlpha(color, midAlpha), 0.34));
            brush.GradientStops.Add(new GradientStop(WithAlpha(color, outerAlpha), 0.76));
            brush.GradientStops.Add(new GradientStop(WithAlpha(color, 0.0), 1.00));
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }

        private static int SelectPaletteIndex(string layerName, int particleIndex)
        {
            int hash = Math.Abs(HashSeed(layerName, particleIndex));
            if (layerName.Equals("dust", StringComparison.OrdinalIgnoreCase))
                return hash % 2;
            if (layerName.Equals("soft", StringComparison.OrdinalIgnoreCase))
                return hash % 3;
            return hash % Palette.Length;
        }

        private static Vector NormalizeDirection(Vector direction)
        {
            Vector safeDirection = direction;
            if (safeDirection.LengthSquared < 0.0001)
                safeDirection = new Vector(1.0, 0.0);

            safeDirection.Normalize();
            return safeDirection;
        }

        private static int HashSeed(string layerName, int particleIndex)
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

        private static double PositiveMod(double value, double modulus)
        {
            if (modulus <= 0.0)
                return 0.0;

            double result = value % modulus;
            return result < 0.0 ? result + modulus : result;
        }

        private static double Lerp(double a, double b, double t)
            => a + ((b - a) * Math.Clamp(t, 0.0, 1.0));

        private static Brush CreateBrush(Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }

        private static Color WithAlpha(Color color, double alphaFactor)
        {
            byte alpha = (byte)Math.Clamp(Math.Round(alphaFactor * 255.0), 0.0, 255.0);
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        private static void SaveBitmap(BitmapSource bitmap, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream stream = File.Create(path);
            encoder.Save(stream);
        }

        private static string FormatSequenceFilePath(string ffmpegPattern, int frameNumber)
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

        private static (DreamyDotOverlaySettings Settings, int RenderWidth, int RenderHeight, double SafeFps, int FrameCount, string AssetCacheKey) CreateOverlayPlan(
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
                Name = "TitanEngine_DreamyDot_STA"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return tcs.Task;
        }
    }
}
