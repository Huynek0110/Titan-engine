using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TitanEngine
{
    public sealed class ParticleOverlaySequence
    {
        public string CacheKey { get; set; } = string.Empty;
        public string TempRoot { get; set; } = string.Empty;
        public string OverlayPattern { get; set; } = string.Empty;
        public int FrameCount { get; set; }
        public double FrameRate { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string PreviewDirectory { get; set; } = string.Empty;
        public bool FromCache { get; set; }
        public List<string> TempArtifacts { get; set; } = new List<string>();
    }

    public static class ParticleOverlayGenerator
    {
        private sealed class ParticleInstance
        {
            public ParticleLayerRecipe Layer { get; init; } = new ParticleLayerRecipe();
            public double BaseX { get; init; }
            public double BaseY { get; init; }
            public double Size { get; init; }
            public double Opacity { get; init; }
            public double SpeedX { get; init; }
            public double SpeedY { get; init; }
            public double Wind { get; init; }
            public double Blur { get; init; }
            public double RotationDegrees { get; init; }
            public double AngularVelocity { get; init; }
            public double Phase { get; init; }
            public double LifeOffset { get; init; }
            public Color Color { get; init; }
        }

        public static async Task<ParticleOverlaySequence> GenerateOverlayAsync(
            int width,
            int height,
            double frameRate,
            double outputDurationSeconds,
            ParticleEffectRecipe recipe,
            string? tag,
            CancellationToken token,
            Action<string> onLog)
        {
            if (recipe == null)
                throw new ArgumentNullException(nameof(recipe));

            int safeWidth = Math.Max(2, width);
            int safeHeight = Math.Max(2, height);
            double safeFps = Math.Clamp(frameRate, 12.0, 60.0);
            double cycleSeconds = ResolveCycleSeconds(recipe, outputDurationSeconds);
            string cacheKey = recipe.BuildCacheKey(safeWidth, safeHeight, safeFps, cycleSeconds);
            string safeTag = string.IsNullOrWhiteSpace(tag)
                ? cacheKey
                : Regex.Replace(tag.Trim(), @"[^\w\-]+", "_");
            string tempRoot = Path.Combine(Path.GetTempPath(), "TitanEngine", "particle_cache", recipe.Id, cacheKey);
            string overlayPattern = Path.Combine(tempRoot, "particle_overlay_%04d.png");
            string previewDir = Path.Combine(tempRoot, "preview");
            string manifestPath = Path.Combine(tempRoot, "manifest.json");
            double renderScale = Math.Min(1.0, 1440.0 / Math.Max(safeWidth, safeHeight));
            int renderW = EnsureEven(Math.Max(360, (int)Math.Round(safeWidth * renderScale)));
            int renderH = EnsureEven(Math.Max(360, (int)Math.Round(safeHeight * renderScale)));
            int frameCount = Math.Clamp((int)Math.Ceiling(cycleSeconds * safeFps), 24, 180);

            if (TryLoadCache(manifestPath, overlayPattern, previewDir, out ParticleOverlaySequence? cached))
            {
                ParticleOverlaySequence cachedOverlay = cached!;
                cachedOverlay.CacheKey = cacheKey;
                cachedOverlay.FromCache = true;
                onLog($"[PARTICLE-CACHE] Reusing {recipe.Name} overlay ({cacheKey}) @ {cachedOverlay.Width}x{cachedOverlay.Height}, frames={cachedOverlay.FrameCount}, preview={cachedOverlay.PreviewDirectory}");
                return cachedOverlay;
            }

            int baseSeed = DeriveSeed(recipe.Id, cacheKey);
            List<ParticleInstance> particles = BuildParticles(recipe, renderW, renderH, baseSeed);

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
                        DrawParticles(dc, recipe, particles, renderW, renderH, cycleSeconds, progress);
                    }

                    RenderTargetBitmap overlayBitmap = new RenderTargetBitmap(renderW, renderH, 96, 96, PixelFormats.Pbgra32);
                    overlayBitmap.Render(overlayVisual);
                    if (overlayBitmap.CanFreeze)
                        overlayBitmap.Freeze();

                    string overlayFile = FormatSequenceFilePath(overlayPattern, i + 1);
                    SaveBitmap(overlayBitmap, overlayFile);

                    if (i < 8)
                    {
                        string previewFile = Path.Combine(previewDir, $"preview_{i + 1:D2}.png");
                        SaveBitmap(overlayBitmap, previewFile);
                    }
                }
            }, token);

            var result = new ParticleOverlaySequence
            {
                CacheKey = cacheKey,
                TempRoot = tempRoot,
                OverlayPattern = overlayPattern,
                FrameCount = frameCount,
                FrameRate = safeFps,
                Width = renderW,
                Height = renderH,
                PreviewDirectory = previewDir,
                FromCache = false,
                TempArtifacts = new List<string>()
            };

            WriteManifest(manifestPath, result);
            onLog($"[PARTICLE-CACHE] Generated {recipe.Name} overlay ({safeTag}) -> {cacheKey}, target={safeWidth}x{safeHeight}, render={renderW}x{renderH}, frames={frameCount}, preview={previewDir}");
            return result;
        }

        private static List<ParticleInstance> BuildParticles(ParticleEffectRecipe recipe, int width, int height, int baseSeed)
        {
            var particles = new List<ParticleInstance>();
            IReadOnlyList<string> palette = recipe.ColorPalette.Count > 0
                ? recipe.ColorPalette
                : new[] { "#FFFFFF" };

            int layerIndex = 0;
            foreach (ParticleLayerRecipe layer in recipe.Layers)
            {
                Random rng = new Random(baseSeed + (layerIndex * 7919));
                int count = Math.Max(1, layer.Count);

                for (int i = 0; i < count; i++)
                {
                    double baseX;
                    double baseY;
                    if (recipe.ParticleType == ParticleType.Confetti)
                    {
                        baseX = width * (0.35 + (rng.NextDouble() * 0.30));
                        baseY = height * (0.62 + (rng.NextDouble() * 0.16));
                    }
                    else
                    {
                        baseX = rng.NextDouble() * width;
                        baseY = rng.NextDouble() * height;
                    }

                    particles.Add(new ParticleInstance
                    {
                        Layer = layer,
                        BaseX = baseX,
                        BaseY = baseY,
                        Size = Lerp(layer.SizeRange, rng.NextDouble()),
                        Opacity = Lerp(layer.OpacityRange, rng.NextDouble()),
                        SpeedX = Lerp(layer.SpeedXRange, rng.NextDouble()),
                        SpeedY = Lerp(layer.SpeedYRange, rng.NextDouble()),
                        Wind = Lerp(layer.WindRange, rng.NextDouble()),
                        Blur = Lerp(layer.BlurRange, rng.NextDouble()),
                        RotationDegrees = rng.NextDouble() * 360.0,
                        AngularVelocity = (rng.NextDouble() - 0.5) * 140.0,
                        Phase = rng.NextDouble() * Math.PI * 2.0,
                        LifeOffset = rng.NextDouble(),
                        Color = ParseColor(palette[rng.Next(palette.Count)])
                    });
                }

                layerIndex++;
            }

            return particles;
        }

        private static void DrawParticles(
            DrawingContext dc,
            ParticleEffectRecipe recipe,
            IReadOnlyList<ParticleInstance> particles,
            int width,
            int height,
            double cycleSeconds,
            double progress)
        {
            Rect canvas = new Rect(0, 0, width, height);
            dc.PushClip(new RectangleGeometry(canvas));

            double timeSeconds = progress * cycleSeconds;
            foreach (ParticleInstance particle in particles)
            {
                DrawParticle(dc, canvas, recipe, particle, timeSeconds, cycleSeconds);
            }

            dc.Pop();
        }

        private static void DrawParticle(
            DrawingContext dc,
            Rect canvas,
            ParticleEffectRecipe recipe,
            ParticleInstance particle,
            double timeSeconds,
            double cycleSeconds)
        {
            double x;
            double y;
            double visualBoost = recipe.ParticleType switch
            {
                ParticleType.Rain => 5.0,
                ParticleType.Sparkle => 6.2,
                ParticleType.Confetti => 3.6,
                ParticleType.Bokeh => 4.2,
                _ => 4.2
            };
            double opacity = Math.Clamp(particle.Opacity * visualBoost, 0.0, 0.95);
            double size = particle.Size;
            double sway = particle.Wind * Math.Sin((timeSeconds * 0.85) + particle.Phase);

            if (recipe.ParticleType == ParticleType.Confetti)
            {
                double t = ((timeSeconds / Math.Max(0.8, cycleSeconds)) + particle.LifeOffset) % 1.0;
                double localSeconds = t * cycleSeconds;
                x = particle.BaseX + (particle.SpeedX * localSeconds * 0.35) + (particle.Wind * Math.Sin((localSeconds * 2.4) + particle.Phase));
                y = particle.BaseY + (particle.SpeedY * localSeconds * 0.35) + (90.0 * localSeconds * localSeconds);
                opacity *= Math.Clamp(1.20 - t, 0.0, 1.0);
            }
            else
            {
                x = Wrap(particle.BaseX + (particle.SpeedX * timeSeconds) + sway + (particle.LifeOffset * canvas.Width), -size * 2.0, canvas.Width + (size * 2.0));
                y = Wrap(particle.BaseY + (particle.SpeedY * timeSeconds) + (particle.LifeOffset * canvas.Height), -size * 2.0, canvas.Height + (size * 2.0));
            }

            if (recipe.ParticleType == ParticleType.Sparkle)
            {
                double twinkle = 0.62 + (0.38 * (0.5 + (0.5 * Math.Sin((timeSeconds * 5.2) + particle.Phase))));
                opacity *= twinkle;
            }
            else if (recipe.ParticleType == ParticleType.Bokeh)
            {
                opacity *= 0.72 + (0.28 * (0.5 + (0.5 * Math.Sin((timeSeconds * 0.9) + particle.Phase))));
            }

            switch (particle.Layer.Shape)
            {
                case ParticleShape.Streak:
                    DrawStreak(dc, x, y, particle, opacity);
                    break;
                case ParticleShape.Star:
                    DrawStar(dc, x, y, particle, opacity, timeSeconds);
                    break;
                case ParticleShape.Rectangle:
                    DrawConfetti(dc, x, y, particle, opacity, timeSeconds);
                    break;
                default:
                    DrawSoftCircle(dc, x, y, particle, opacity);
                    break;
            }
        }

        private static void DrawSoftCircle(DrawingContext dc, double x, double y, ParticleInstance particle, double opacity)
        {
            Color haloColor = WithAlpha(particle.Color, opacity * 0.42);
            Color coreColor = WithAlpha(Colors.White, opacity * 0.62);
            double radius = Math.Max(0.8, particle.Size);
            double haloRadius = radius * (1.55 + (particle.Blur * 0.42));

            RadialGradientBrush haloBrush = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            haloBrush.GradientStops.Add(new GradientStop(haloColor, 0.0));
            haloBrush.GradientStops.Add(new GradientStop(WithAlpha(particle.Color, opacity * 0.18), 0.56));
            haloBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 1.0));
            if (haloBrush.CanFreeze)
                haloBrush.Freeze();

            Brush coreBrush = new SolidColorBrush(coreColor);
            if (coreBrush.CanFreeze)
                coreBrush.Freeze();

            dc.DrawEllipse(haloBrush, null, new Point(x, y), haloRadius, haloRadius);
            dc.DrawEllipse(coreBrush, null, new Point(x, y), radius * 0.55, radius * 0.55);
        }

        private static void DrawStreak(DrawingContext dc, double x, double y, ParticleInstance particle, double opacity)
        {
            double length = Math.Max(4.0, particle.Size);
            double angle = Math.Atan2(particle.SpeedY, particle.SpeedX == 0.0 ? -1.0 : particle.SpeedX);
            double dx = Math.Cos(angle) * length * 0.5;
            double dy = Math.Sin(angle) * length * 0.5;
            double thickness = Math.Max(0.6, length * 0.08);

            Pen glowPen = CreatePen(WithAlpha(particle.Color, opacity * 0.36), thickness + (particle.Blur * 0.8) + 0.6);
            Pen linePen = CreatePen(WithAlpha(Colors.White, opacity * 0.68), thickness);
            Point start = new Point(x - dx, y - dy);
            Point end = new Point(x + dx, y + dy);

            dc.DrawLine(glowPen, start, end);
            dc.DrawLine(linePen, start, end);
        }

        private static void DrawStar(DrawingContext dc, double x, double y, ParticleInstance particle, double opacity, double timeSeconds)
        {
            double outerRadius = Math.Max(1.4, particle.Size * 0.52);
            double innerRadius = outerRadius * 0.42;
            double rotation = particle.RotationDegrees + (particle.AngularVelocity * timeSeconds * 0.18);
            Geometry star = BuildStarGeometry(new Point(x, y), outerRadius, innerRadius, rotation, 4);
            Brush glowBrush = CreateBrush(WithAlpha(particle.Color, opacity * 0.34));
            Brush coreBrush = CreateBrush(WithAlpha(Colors.White, opacity * 0.78));

            dc.DrawEllipse(glowBrush, null, new Point(x, y), outerRadius * (1.55 + (particle.Blur * 0.35)), outerRadius * (1.55 + (particle.Blur * 0.35)));
            dc.DrawGeometry(coreBrush, null, star);
        }

        private static void DrawConfetti(DrawingContext dc, double x, double y, ParticleInstance particle, double opacity, double timeSeconds)
        {
            double width = Math.Max(2.4, particle.Size);
            double height = Math.Max(1.8, particle.Size * 0.42);
            double angle = particle.RotationDegrees + (particle.AngularVelocity * timeSeconds);
            Brush brush = CreateBrush(WithAlpha(particle.Color, opacity));
            Brush glowBrush = CreateBrush(WithAlpha(Colors.White, opacity * 0.18));

            dc.PushTransform(new RotateTransform(angle, x, y));
            dc.DrawRoundedRectangle(glowBrush, null, new Rect(x - (width * 0.55), y - (height * 0.55), width * 1.10, height * 1.10), height * 0.35, height * 0.35);
            dc.DrawRoundedRectangle(brush, null, new Rect(x - (width * 0.5), y - (height * 0.5), width, height), height * 0.28, height * 0.28);
            dc.Pop();
        }

        private static Geometry BuildStarGeometry(Point center, double outerRadius, double innerRadius, double rotationDegrees, int spikes)
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                double rotation = rotationDegrees * Math.PI / 180.0;
                int pointCount = spikes * 2;
                for (int i = 0; i < pointCount; i++)
                {
                    double angle = rotation + ((Math.PI * 2.0 * i) / pointCount);
                    double radius = (i % 2 == 0) ? outerRadius : innerRadius;
                    Point point = new Point(
                        center.X + (Math.Cos(angle) * radius),
                        center.Y + (Math.Sin(angle) * radius));

                    if (i == 0)
                        ctx.BeginFigure(point, true, true);
                    else
                        ctx.LineTo(point, true, true);
                }
            }

            if (geometry.CanFreeze)
                geometry.Freeze();
            return geometry;
        }

        private static bool TryLoadCache(
            string manifestPath,
            string overlayPattern,
            string previewDir,
            out ParticleOverlaySequence? result)
        {
            result = null;
            if (!File.Exists(manifestPath))
                return false;

            try
            {
                string json = File.ReadAllText(manifestPath);
                var cached = JsonSerializer.Deserialize<ParticleOverlaySequence>(json);
                if (cached == null || cached.FrameCount <= 0 || cached.Width <= 0 || cached.Height <= 0)
                    return false;

                string firstFrame = FormatSequenceFilePath(overlayPattern, 1);
                if (!File.Exists(firstFrame))
                    return false;

                cached.TempRoot = Path.GetDirectoryName(manifestPath)!;
                cached.OverlayPattern = overlayPattern;
                cached.PreviewDirectory = previewDir;
                cached.TempArtifacts = new List<string>();
                result = cached;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteManifest(string manifestPath, ParticleOverlaySequence result)
        {
            var payload = new ParticleOverlaySequence
            {
                CacheKey = result.CacheKey,
                TempRoot = result.TempRoot,
                OverlayPattern = result.OverlayPattern,
                FrameCount = result.FrameCount,
                FrameRate = result.FrameRate,
                Width = result.Width,
                Height = result.Height,
                PreviewDirectory = result.PreviewDirectory
            };

            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(manifestPath, json, Encoding.UTF8);
        }

        private static double ResolveCycleSeconds(ParticleEffectRecipe recipe, double outputDurationSeconds)
        {
            double fallback = recipe.ParticleType == ParticleType.Confetti ? 2.2 : 4.0;
            if (recipe.Parameters.TryGetValue("cycleSeconds", out string? raw) &&
                double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                fallback = parsed;
            }

            if (recipe.ParticleType == ParticleType.Confetti)
                return Math.Clamp(fallback, 1.6, 3.2);

            if (outputDurationSeconds > 0.10)
                fallback = Math.Min(fallback, Math.Max(2.0, outputDurationSeconds));

            return Math.Clamp(fallback, 2.0, 6.0);
        }

        private static int DeriveSeed(string recipeId, string cacheKey)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(recipeId + "|" + cacheKey));
            return BitConverter.ToInt32(hash, 0) & int.MaxValue;
        }

        private static Brush CreateBrush(Color color)
        {
            Brush brush = new SolidColorBrush(color);
            if (brush.CanFreeze)
                brush.Freeze();
            return brush;
        }

        private static Pen CreatePen(Color color, double thickness)
        {
            Pen pen = new Pen(CreateBrush(color), thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            if (pen.CanFreeze)
                pen.Freeze();
            return pen;
        }

        private static double Lerp(EffectRange range, double t)
            => range.Min + ((range.Max - range.Min) * Math.Clamp(t, 0.0, 1.0));

        private static double Wrap(double value, double min, double max)
        {
            double span = max - min;
            if (span <= 0.0001)
                return min;

            double wrapped = value;
            while (wrapped < min)
                wrapped += span;
            while (wrapped > max)
                wrapped -= span;
            return wrapped;
        }

        private static int EnsureEven(int value)
        {
            int safe = Math.Max(2, value);
            return (safe & 1) == 0 ? safe : safe + 1;
        }

        private static Color ParseColor(string value)
        {
            string normalized = (value ?? "#FFFFFF").Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return Colors.White;

            if (normalized.StartsWith("#", StringComparison.Ordinal))
                normalized = normalized[1..];

            try
            {
                return normalized.Length switch
                {
                    6 => Color.FromRgb(
                        byte.Parse(normalized.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        byte.Parse(normalized.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        byte.Parse(normalized.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
                    8 => Color.FromArgb(
                        byte.Parse(normalized.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        byte.Parse(normalized.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        byte.Parse(normalized.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        byte.Parse(normalized.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
                    _ => Colors.White
                };
            }
            catch
            {
                return Colors.White;
            }
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
                Name = "TitanEngine_Particle_STA"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return tcs.Task;
        }
    }
}
