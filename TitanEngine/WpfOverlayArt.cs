using System;
using System.Collections.Concurrent;
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
    // Giai đoạn 1 (đính chính): các member vẽ pixel (WPF-stay) chuyển nguyên văn từ TitanEngine.Core.
    // Core gọi qua IImageServices (EngineServices.Images) — Core không tham chiếu project WPF.

    /// <summary>Adapter PixelRect (Core) → Rect WPF cho code moved verbatim. Phase 4 (Skia) sẽ xoá.</summary>
    internal static class WpfArtConvert
    {
        public static System.Windows.Rect ToRect(PixelRect r) => new System.Windows.Rect(r.X, r.Y, r.Width, r.Height);
    }
    /// <summary>WPF-stay members moved verbatim từ DashedPolaroidFramePreset.cs (Giai đoạn 1 đính chính). Gọi qua IImageServices.</summary>
    internal static class DashedPolaroidFrameArt
    {

        public static async Task<DashedPolaroidGeneratedOverlay> GenerateOverlayAsync(
            int width,
            int height,
            OverlayContentLayout? layout,
            double frameRate,
            RenderJob job,
            string? tag,
            CancellationToken token,
            Action<string> onLog)
        {
            string safeTag = string.IsNullOrWhiteSpace(tag)
                ? Guid.NewGuid().ToString("N").Substring(0, 8)
                : Regex.Replace(tag.Trim(), @"[^\w\-]+", "_");
            string tempRoot = Path.Combine(Path.GetTempPath(), "TitanEngine", $"dashedpolaroid_{safeTag}");
            string overlayPattern = Path.Combine(tempRoot, "dashed_frame_%04d.png");
            string previewDir = Path.Combine(tempRoot, "preview");
            int safeW = Math.Max(2, width);
            int safeH = Math.Max(2, height);
            double safeFps = Math.Clamp(frameRate, 12.0, 60.0);
            OverlayContentLayout safeLayout = layout ?? OverlayLayoutHelper.Create(safeW, safeH, safeW, safeH);
            DashedPolaroidFrameSettings settings = DashedPolaroidFramePreset.FromJob(job, safeLayout);
            int frameCount = Math.Clamp((int)Math.Ceiling(settings.CycleSeconds * safeFps), 20, 72);

            await RunOnStaThreadAsync(() =>
            {
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(previewDir);

                for (int i = 0; i < frameCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    double progress = frameCount <= 1 ? 0.0 : (double)i / frameCount;
                    DrawingVisual overlayVisual = new DrawingVisual();
                    using (DrawingContext dc = overlayVisual.RenderOpen())
                    {
                        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, safeW, safeH));
                        DrawDashedFrame(dc, safeLayout, settings, progress);
                    }

                    RenderTargetBitmap overlayBitmap = new RenderTargetBitmap(safeW, safeH, 96, 96, PixelFormats.Pbgra32);
                    overlayBitmap.Render(overlayVisual);
                    if (overlayBitmap.CanFreeze) overlayBitmap.Freeze();

                    string overlayFile = DashedPolaroidFramePreset.FormatSequenceFilePath(overlayPattern, i + 1);
                    SaveBitmap(overlayBitmap, overlayFile);

                    if (i < 6)
                    {
                        string previewFile = Path.Combine(previewDir, $"preview_{i + 1:D2}.png");
                        SaveBitmap(overlayBitmap, previewFile);
                    }
                }
            }, token);

            onLog($"[DASHED-POLAROID] Generated rounded dashed frame overlay @ {safeW}x{safeH} with {frameCount} pulse frames. Preview={previewDir}");

            return new DashedPolaroidGeneratedOverlay
            {
                TempRoot = tempRoot,
                OverlayPattern = overlayPattern,
                FrameCount = frameCount,
                FrameRate = safeFps,
                PreviewDirectory = previewDir,
                TempArtifacts = new List<string> { tempRoot }
            };
        }


        private static void DrawDashedFrame(DrawingContext dc, OverlayContentLayout layout, DashedPolaroidFrameSettings settings, double progress)
        {
            FrameLayout frameLayout = GetFrameLayout(layout, settings, 0.0, 0.0);
            double shadowOffset = Math.Max(2.0, settings.StrokeThickness * 0.38);
            FrameLayout shadowLayout = GetFrameLayout(layout, settings, shadowOffset, shadowOffset);
            double step = Math.Max(4.0, settings.DashLength + settings.DashGap);
            int dashCount = Math.Max(8, (int)Math.Ceiling(frameLayout.Perimeter / step));
            double dashStride = frameLayout.Perimeter / dashCount;
            double dashLength = Math.Min(settings.DashLength, Math.Max(8.0, dashStride * 0.78));
            double runner = progress * dashCount;

            Color baseFrameColor = Color.FromArgb(
                (byte)Math.Clamp(Math.Round(settings.FrameOpacity * 255.0), 0.0, 255.0),
                255, 249, 240);
            Color shadowColor = Color.FromArgb(
                (byte)Math.Clamp(Math.Round(settings.ShadowOpacity * 255.0), 0.0, 255.0),
                0, 0, 0);

            for (int i = 0; i < dashCount; i++)
            {
                double startDistance = (i * dashStride) + Math.Max(0.0, (dashStride - dashLength) * 0.5);
                StreamGeometry shadowGeometry = CreateSegmentGeometry(shadowLayout, startDistance, dashLength, settings.SampleSpacing);
                StreamGeometry baseGeometry = CreateSegmentGeometry(frameLayout, startDistance, dashLength, settings.SampleSpacing);

                double distanceToRunner = DashedPolaroidFramePreset.CircularDistance(i, runner, dashCount);
                double activeWeight = Math.Max(0.0, 1.0 - (distanceToRunner / 1.15));
                double trailWeight = Math.Max(0.0, 1.0 - (distanceToRunner / 2.35));

                if (shadowGeometry != null)
                {
                    Pen shadowPen = CreateStrokePen(shadowColor, settings.ShadowThickness);
                    dc.DrawGeometry(null, shadowPen, shadowGeometry);
                }

                if (trailWeight > 0.01 && baseGeometry != null)
                {
                    double outerAlpha = settings.GlowOpacity * (0.34 + (1.02 * activeWeight));
                    double innerAlpha = settings.GlowOpacity * (0.22 + (0.82 * activeWeight));
                    double outerThickness = settings.GlowThickness * (1.02 + (0.24 * activeWeight));
                    double innerThickness = settings.GlowThickness * (0.66 + (0.14 * activeWeight));

                    Pen glowOuterPen = CreateStrokePen(WithAlpha(Colors.White, outerAlpha), outerThickness);
                    Pen glowInnerPen = CreateStrokePen(WithAlpha(Colors.White, innerAlpha), innerThickness);
                    dc.DrawGeometry(null, glowOuterPen, baseGeometry);
                    dc.DrawGeometry(null, glowInnerPen, baseGeometry);
                }

                if (baseGeometry != null)
                {
                    double brightBoost = 0.84 + (0.34 * trailWeight) + (0.10 * activeWeight);
                    Color litFrameColor = Color.FromArgb(
                        (byte)Math.Clamp(Math.Round(settings.FrameOpacity * brightBoost * 255.0), 0.0, 255.0),
                        255,
                        (byte)Math.Clamp(Math.Round(250 + (5 * activeWeight)), 0.0, 255.0),
                        (byte)Math.Clamp(Math.Round(244 + (11 * activeWeight)), 0.0, 255.0));

                    Pen framePen = CreateStrokePen(litFrameColor, settings.StrokeThickness);
                    dc.DrawGeometry(null, framePen, baseGeometry);
                }
            }
        }


        private static Pen CreateStrokePen(Color color, double thickness)
        {
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze) brush.Freeze();

            var pen = new Pen(brush, thickness)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                DashCap = PenLineCap.Round
            };
            if (pen.CanFreeze) pen.Freeze();
            return pen;
        }


        private static FrameLayout GetFrameLayout(OverlayContentLayout layout, DashedPolaroidFrameSettings settings, double offsetX, double offsetY)
        {
            Rect contentRect = WpfArtConvert.ToRect(layout.ContentRect);
            double x = contentRect.X + settings.Inset + offsetX;
            double y = contentRect.Y + settings.Inset + offsetY;
            double w = Math.Max(24.0, contentRect.Width - (settings.Inset * 2.0));
            double h = Math.Max(24.0, contentRect.Height - (settings.Inset * 2.0));
            double radius = Math.Min(settings.CornerRadius, Math.Min(w, h) * 0.25);
            return new FrameLayout(x, y, w, h, radius);
        }


        private static StreamGeometry CreateSegmentGeometry(FrameLayout layout, double startDistance, double segmentLength, double sampleSpacing)
        {
            double safeSpacing = Math.Max(0.8, sampleSpacing);
            int subdivisions = Math.Max(3, (int)Math.Ceiling(segmentLength / safeSpacing));
            StreamGeometry geometry = new StreamGeometry();

            using (StreamGeometryContext ctx = geometry.Open())
            {
                Point start = layout.GetPointAt(startDistance);
                ctx.BeginFigure(start, false, false);

                for (int stepIndex = 1; stepIndex <= subdivisions; stepIndex++)
                {
                    double t = stepIndex / (double)subdivisions;
                    double distance = startDistance + (segmentLength * t);
                    Point point = layout.GetPointAt(distance);
                    ctx.LineTo(point, true, false);
                }
            }

            if (geometry.CanFreeze) geometry.Freeze();
            return geometry;
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
                Name = "TitanEngine_DashedPolaroid_STA"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return tcs.Task;
        }


        private readonly struct FrameLayout
        {
            private readonly double _x;
            private readonly double _y;
            private readonly double _w;
            private readonly double _h;
            private readonly double _r;
            private readonly double _line;
            private readonly double _arc;

            public FrameLayout(double x, double y, double width, double height, double radius)
            {
                _x = x;
                _y = y;
                _w = Math.Max(16.0, width);
                _h = Math.Max(16.0, height);
                _r = Math.Max(0.0, Math.Min(radius, Math.Min(_w, _h) * 0.25));
                _line = Math.Max(0.0, _w - (2.0 * _r));
                _arc = (Math.PI * _r) / 2.0;
                Perimeter = (2.0 * _line) + (2.0 * Math.Max(0.0, _h - (2.0 * _r))) + (4.0 * _arc);
            }

            public double Perimeter { get; }

            public Point GetPointAt(double distance)
            {
                double d = distance % Perimeter;
                if (d < 0) d += Perimeter;

                double rightX = _x + _w - _r;
                double bottomY = _y + _h - _r;
                double vertical = Math.Max(0.0, _h - (2.0 * _r));

                if (d <= _line)
                    return new Point(_x + _r + d, _y);

                d -= _line;
                if (d <= _arc)
                    return ArcPoint(rightX, _y + _r, _r, -90.0 + ((d / _arc) * 90.0));

                d -= _arc;
                if (d <= vertical)
                    return new Point(_x + _w, _y + _r + d);

                d -= vertical;
                if (d <= _arc)
                    return ArcPoint(rightX, bottomY, _r, ((d / _arc) * 90.0));

                d -= _arc;
                if (d <= _line)
                    return new Point(_x + _w - _r - d, _y + _h);

                d -= _line;
                if (d <= _arc)
                    return ArcPoint(_x + _r, bottomY, _r, 90.0 + ((d / _arc) * 90.0));

                d -= _arc;
                if (d <= vertical)
                    return new Point(_x, _y + _h - _r - d);

                d -= vertical;
                return ArcPoint(_x + _r, _y + _r, _r, 180.0 + ((d / _arc) * 90.0));
            }

            private static Point ArcPoint(double cx, double cy, double radius, double angleDegrees)
            {
                if (radius <= 0.001)
                    return new Point(cx, cy);

                double radians = angleDegrees * Math.PI / 180.0;
                return new Point(
                    cx + (Math.Cos(radians) * radius),
                    cy + (Math.Sin(radians) * radius));
            }
        }
    }

    /// <summary>WPF-stay members moved verbatim từ DreamyDotOverlayPreset.cs (Giai đoạn 1 đính chính). Gọi qua IImageServices.</summary>
    internal static class DreamyDotOverlayArt
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
            var plan = DreamyDotOverlayPreset.CreateOverlayPlan(safeW, safeH, frameRate, outputDurationSeconds, job);
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
                ContentRect = new PixelRect(0, 0, renderW, renderH)
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

                    string overlayFile = DreamyDotOverlayPreset.FormatSequenceFilePath(overlayPattern, i + 1);
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
            Random rng = new Random(DreamyDotOverlayPreset.HashSeed(layer.Name, particleIndex));
            double radius = DreamyDotOverlayPreset.Lerp(layer.MinRadius, layer.MaxRadius, rng.NextDouble());
            double opacityPulse = 0.90 + (0.24 * Math.Sin((progress * Math.PI * 2.0 * (0.55 + (rng.NextDouble() * 1.4))) + (rng.NextDouble() * Math.PI * 2.0)));
            double opacity = Math.Clamp(
                DreamyDotOverlayPreset.Lerp(layer.MinOpacity, layer.MaxOpacity, rng.NextDouble()) * opacityPulse,
                0.01,
                1.0);
            double travelLoops = DreamyDotOverlayPreset.Lerp(layer.MinTravelLoops, layer.MaxTravelLoops, rng.NextDouble());
            double stretch = DreamyDotOverlayPreset.Lerp(layer.MinStretch, layer.MaxStretch, rng.NextDouble());
            Point particleCenter;
            Vector direction;

            if (settings.Variant.Equals("chaos", StringComparison.OrdinalIgnoreCase))
            {
                particleCenter = ResolveChaosParticleCenter(canvasRect, settings, layer, particleIndex, progress, radius, travelLoops, out direction);
            }
            else
            {
                direction = DreamyDotOverlayPreset.ResolveParticleDirection(settings, layer, particleIndex, progress, rng);
                Vector perpendicular = new Vector(-direction.Y, direction.X);
                double diagonal = Math.Sqrt((canvasRect.Width * canvasRect.Width) + (canvasRect.Height * canvasRect.Height));
                double margin = Math.Max(radius * (5.0 + layer.GlowScale), 24.0);
                double pathSpan = diagonal + (margin * 2.0);
                double acrossSpan = diagonal + (margin * 2.0);
                Point center = new Point(canvasRect.X + (canvasRect.Width * 0.5), canvasRect.Y + (canvasRect.Height * 0.5));
                double alongStart = ((rng.NextDouble() * pathSpan) - (pathSpan * 0.5));
                double acrossStart = ((rng.NextDouble() * acrossSpan) - (acrossSpan * 0.5));
                double alongPosition = DreamyDotOverlayPreset.PositiveMod(alongStart + (progress * travelLoops * pathSpan) + (pathSpan * 0.5), pathSpan) - (pathSpan * 0.5);
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
            int stableSeed = DreamyDotOverlayPreset.HashSeed(layer.Name, particleIndex);

            Point current = EvaluateChaosParticlePosition(canvasRect, settings, layer, progress, radius, travelLoops, new Random(stableSeed), spanX, spanY, margin);
            Point ahead = EvaluateChaosParticlePosition(canvasRect, settings, layer, DreamyDotOverlayPreset.PositiveMod(progress + sampleStep, 1.0), radius, travelLoops, new Random(stableSeed), spanX, spanY, margin);
            direction = DreamyDotOverlayPreset.NormalizeDirection(new Vector(ahead.X - current.X, ahead.Y - current.Y));
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
            double layerBias = DreamyDotOverlayPreset.GetChaosLayerBias(layer.Name);
            double phase = rng.NextDouble();
            double localProgress = DreamyDotOverlayPreset.PositiveMod((progress * travelLoops) + phase, 1.0);
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

            double x = DreamyDotOverlayPreset.PositiveMod(anchorX + driftX + motionX, spanX) - margin;
            double y = DreamyDotOverlayPreset.PositiveMod(anchorY + driftY + motionY, spanY) - margin;
            return new Point(canvasRect.X + x, canvasRect.Y + y);
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
            int hash = Math.Abs(DreamyDotOverlayPreset.HashSeed(layerName, particleIndex));
            if (layerName.Equals("dust", StringComparison.OrdinalIgnoreCase))
                return hash % 2;
            if (layerName.Equals("soft", StringComparison.OrdinalIgnoreCase))
                return hash % 3;
            return hash % Palette.Length;
        }


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

    /// <summary>WPF-stay members moved verbatim từ LightLeakBurnPreset.cs (Giai đoạn 1 đính chính). Gọi qua IImageServices.</summary>
    internal static class LightLeakBurnArt
    {

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

                    string maskFile = LightLeakBurnPreset.FormatSequenceFilePath(maskPattern, i + 1);
                    string overlayFile = LightLeakBurnPreset.FormatSequenceFilePath(overlayPattern, i + 1);
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

            var blobs = LightLeakBurnPreset.BuildLeakCenters(settings.Direction, progress, width, height);
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
    }

    /// <summary>WPF-stay members moved verbatim từ ParticleOverlayGenerator.cs (Giai đoạn 1 đính chính). Gọi qua IImageServices.</summary>
    internal static class ParticleOverlayArt
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
            double cycleSeconds = ParticleOverlayGenerator.ResolveCycleSeconds(recipe, outputDurationSeconds);
            string cacheKey = recipe.BuildCacheKey(safeWidth, safeHeight, safeFps, cycleSeconds);
            string safeTag = string.IsNullOrWhiteSpace(tag)
                ? cacheKey
                : Regex.Replace(tag.Trim(), @"[^\w\-]+", "_");
            string tempRoot = Path.Combine(Path.GetTempPath(), "TitanEngine", "particle_cache", recipe.Id, cacheKey);
            string overlayPattern = Path.Combine(tempRoot, "particle_overlay_%04d.png");
            string previewDir = Path.Combine(tempRoot, "preview");
            string manifestPath = Path.Combine(tempRoot, "manifest.json");
            double renderScale = Math.Min(1.0, 1440.0 / Math.Max(safeWidth, safeHeight));
            int renderW = ParticleOverlayGenerator.EnsureEven(Math.Max(360, (int)Math.Round(safeWidth * renderScale)));
            int renderH = ParticleOverlayGenerator.EnsureEven(Math.Max(360, (int)Math.Round(safeHeight * renderScale)));
            int frameCount = Math.Clamp((int)Math.Ceiling(cycleSeconds * safeFps), 24, 180);

            if (ParticleOverlayGenerator.TryLoadCache(manifestPath, overlayPattern, previewDir, out ParticleOverlaySequence? cached))
            {
                ParticleOverlaySequence cachedOverlay = cached!;
                cachedOverlay.CacheKey = cacheKey;
                cachedOverlay.FromCache = true;
                onLog($"[PARTICLE-CACHE] Reusing {recipe.Name} overlay ({cacheKey}) @ {cachedOverlay.Width}x{cachedOverlay.Height}, frames={cachedOverlay.FrameCount}, preview={cachedOverlay.PreviewDirectory}");
                return cachedOverlay;
            }

            int baseSeed = ParticleOverlayGenerator.DeriveSeed(recipe.Id, cacheKey);
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

                    string overlayFile = ParticleOverlayGenerator.FormatSequenceFilePath(overlayPattern, i + 1);
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

            ParticleOverlayGenerator.WriteManifest(manifestPath, result);
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
                        Size = ParticleOverlayGenerator.Lerp(layer.SizeRange, rng.NextDouble()),
                        Opacity = ParticleOverlayGenerator.Lerp(layer.OpacityRange, rng.NextDouble()),
                        SpeedX = ParticleOverlayGenerator.Lerp(layer.SpeedXRange, rng.NextDouble()),
                        SpeedY = ParticleOverlayGenerator.Lerp(layer.SpeedYRange, rng.NextDouble()),
                        Wind = ParticleOverlayGenerator.Lerp(layer.WindRange, rng.NextDouble()),
                        Blur = ParticleOverlayGenerator.Lerp(layer.BlurRange, rng.NextDouble()),
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
                x = ParticleOverlayGenerator.Wrap(particle.BaseX + (particle.SpeedX * timeSeconds) + sway + (particle.LifeOffset * canvas.Width), -size * 2.0, canvas.Width + (size * 2.0));
                y = ParticleOverlayGenerator.Wrap(particle.BaseY + (particle.SpeedY * timeSeconds) + (particle.LifeOffset * canvas.Height), -size * 2.0, canvas.Height + (size * 2.0));
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

    /// <summary>WPF-stay members moved verbatim từ PolaroidScrapbook2Preset.cs (Giai đoạn 1 đính chính). Gọi qua IImageServices.</summary>
    internal static class PolaroidScrapbook2Art
    {

        public static async Task<PolaroidScrapbook2GeneratedOverlay> GenerateOverlayAsync(
            int width,
            int height,
            OverlayContentLayout? layout,
            RenderJob job,
            string? tag,
            CancellationToken token,
            Action<string> onLog)
        {
            string safeTag = string.IsNullOrWhiteSpace(tag)
                ? Guid.NewGuid().ToString("N").Substring(0, 8)
                : Regex.Replace(tag.Trim(), @"[^\w\-]+", "_");
            string tempRoot = Path.Combine(Path.GetTempPath(), "TitanEngine", $"polaroid2_{safeTag}");
            string overlayPattern = Path.Combine(tempRoot, "polaroid2_%04d.png");
            string previewDir = Path.Combine(tempRoot, "preview");
            int safeW = Math.Max(2, width);
            int safeH = Math.Max(2, height);
            OverlayContentLayout safeLayout = layout ?? OverlayLayoutHelper.Create(safeW, safeH, safeW, safeH);
            var settings = PolaroidScrapbook2Preset.FromJob(job, safeLayout);

            await RunOnStaThreadAsync(() =>
            {
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(previewDir);

                DrawingVisual overlayVisual = new DrawingVisual();
                using (DrawingContext dc = overlayVisual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, safeW, safeH));
                    DrawOverlay(dc, safeLayout, settings);
                }

                RenderTargetBitmap overlayBitmap = new RenderTargetBitmap(safeW, safeH, 96, 96, PixelFormats.Pbgra32);
                overlayBitmap.Render(overlayVisual);
                if (overlayBitmap.CanFreeze) overlayBitmap.Freeze();

                string outputFile = PolaroidScrapbook2Preset.FormatSequenceFilePath(overlayPattern, 1);
                SaveBitmap(overlayBitmap, outputFile);
                SaveBitmap(overlayBitmap, Path.Combine(previewDir, "preview_01.png"));
            }, token);

            onLog($"[POLAROID-2] Generated rounded scrapbook overlay @ {safeW}x{safeH}. Preview={previewDir}");

            return new PolaroidScrapbook2GeneratedOverlay
            {
                TempRoot = tempRoot,
                OverlayPattern = overlayPattern,
                FrameCount = 1,
                FrameRate = 1.0,
                PreviewDirectory = previewDir,
                TempArtifacts = new List<string> { tempRoot }
            };
        }


        private static void DrawOverlay(DrawingContext dc, OverlayContentLayout layout, PolaroidScrapbook2Settings settings)
        {
            Rect contentRect = WpfArtConvert.ToRect(layout.ContentRect);
            Rect frameRect = new Rect(
                contentRect.X + settings.Inset,
                contentRect.Y + settings.Inset,
                Math.Max(24.0, contentRect.Width - (settings.Inset * 2.0)),
                Math.Max(24.0, contentRect.Height - (settings.Inset * 2.0)));
            Rect shadowRect = new Rect(frameRect.X + settings.ShadowOffset, frameRect.Y + settings.ShadowOffset, frameRect.Width, frameRect.Height);

            var shadowPen = CreatePen(Color.FromArgb((byte)Math.Round(settings.ShadowOpacity * 255.0), 0, 0, 0), settings.ShadowThickness);
            var framePen = CreatePen(Color.FromArgb((byte)Math.Round(settings.FrameOpacity * 255.0), 255, 249, 240), settings.StrokeThickness);

            dc.DrawRoundedRectangle(null, shadowPen, shadowRect, settings.CornerRadius, settings.CornerRadius);
            dc.DrawRoundedRectangle(null, framePen, frameRect, settings.CornerRadius, settings.CornerRadius);

            Brush tapeBrush = CreateBrush(Color.FromArgb((byte)Math.Round(settings.TapeOpacity * 255.0), 231, 211, 168));
            Brush stickyBrush = CreateBrush(Color.FromArgb((byte)Math.Round(settings.StickyOpacity * 255.0), 214, 240, 228));

            Rect tapeRect = new Rect(
                frameRect.X + (settings.Inset * 0.65),
                Math.Max(10.0, frameRect.Y - (settings.TapeHeight * 0.35)),
                settings.TapeWidth,
                settings.TapeHeight);

            Point tapeCenter = new Point(tapeRect.X + (tapeRect.Width / 2.0), tapeRect.Y + (tapeRect.Height / 2.0));
            dc.PushTransform(new RotateTransform(-6.5, tapeCenter.X, tapeCenter.Y));
            dc.DrawRoundedRectangle(tapeBrush, null, tapeRect, settings.TapeHeight * 0.22, settings.TapeHeight * 0.22);
            dc.Pop();

            Rect stickyRect = new Rect(
                frameRect.Right - settings.StickyWidth - (settings.Inset * 0.7),
                frameRect.Bottom - settings.StickyHeight - Math.Max(18.0, settings.Inset * 0.35),
                settings.StickyWidth,
                settings.StickyHeight);
            Point stickyCenter = new Point(stickyRect.X + (stickyRect.Width / 2.0), stickyRect.Y + (stickyRect.Height / 2.0));
            dc.PushTransform(new RotateTransform(4.0, stickyCenter.X, stickyCenter.Y));
            dc.DrawRoundedRectangle(stickyBrush, null, stickyRect, settings.StickyHeight * 0.18, settings.StickyHeight * 0.18);
            dc.Pop();
        }


        private static Pen CreatePen(Color color, double thickness)
        {
            var pen = new Pen(CreateBrush(color), thickness)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            if (pen.CanFreeze) pen.Freeze();
            return pen;
        }


        private static SolidColorBrush CreateBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }


        private static void SaveBitmap(BitmapSource bitmap, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
        }


        private static Task RunOnStaThreadAsync(Action action, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread thread = new Thread(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    action();
                    tcs.TrySetResult(null);
                }
                catch (OperationCanceledException oce)
                {
                    tcs.TrySetCanceled(oce.CancellationToken);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return tcs.Task;
        }
    }

    /// <summary>WPF-stay members moved verbatim từ RgbPolaroidLedPreset.cs (Giai đoạn 1 đính chính). Gọi qua IImageServices.</summary>
    internal static class RgbPolaroidLedArt
    {

        public static async Task<RgbPolaroidLedGeneratedOverlay> GenerateOverlayAsync(
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
            string tempRoot = Path.Combine(Path.GetTempPath(), "TitanEngine", $"rgbpolaroid_{safeTag}");
            string overlayPattern = Path.Combine(tempRoot, "rgb_border_%04d.png");
            string previewDir = Path.Combine(tempRoot, "preview");
            int safeW = Math.Max(2, width);
            int safeH = Math.Max(2, height);
            double safeFps = Math.Clamp(frameRate, 12.0, 60.0);
            OverlayContentLayout safeLayout = layout ?? OverlayLayoutHelper.Create(safeW, safeH, safeW, safeH);
            RgbPolaroidLedSettings settings = RgbPolaroidLedPreset.FromJob(job, safeLayout);
            int frameCount = Math.Clamp((int)Math.Ceiling(settings.CycleSeconds * safeFps), 42, 96);

            await RunOnStaThreadAsync(() =>
            {
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(previewDir);

                for (int i = 0; i < frameCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    double progress = frameCount <= 1 ? 0.0 : (double)i / frameCount;
                    DrawingVisual overlayVisual = new DrawingVisual();

                    using (DrawingContext dc = overlayVisual.RenderOpen())
                    {
                        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, safeW, safeH));
                        DrawRgbLedFrame(dc, safeLayout, progress, settings);
                    }

                    RenderTargetBitmap overlayBitmap = new RenderTargetBitmap(safeW, safeH, 96, 96, PixelFormats.Pbgra32);
                    overlayBitmap.Render(overlayVisual);
                    if (overlayBitmap.CanFreeze) overlayBitmap.Freeze();

                    string overlayFile = RgbPolaroidLedPreset.FormatSequenceFilePath(overlayPattern, i + 1);
                    SaveBitmap(overlayBitmap, overlayFile);

                    if (i < 6)
                    {
                        string previewFile = Path.Combine(previewDir, $"preview_{i + 1:D2}.png");
                        SaveBitmap(overlayBitmap, previewFile);
                    }
                }
            }, token);

            onLog($"[RGB-POLAROID] Generated {frameCount} LED border frames @ {safeW}x{safeH}. Preview={previewDir}");

            return new RgbPolaroidLedGeneratedOverlay
            {
                TempRoot = tempRoot,
                OverlayPattern = overlayPattern,
                FrameCount = frameCount,
                FrameRate = safeFps,
                CycleSeconds = settings.CycleSeconds,
                PreviewDirectory = previewDir,
                TempArtifacts = new List<string> { tempRoot }
            };
        }


        private static void DrawRgbLedFrame(DrawingContext dc, OverlayContentLayout layout, double progress, RgbPolaroidLedSettings settings)
        {
            var frameLayout = GetFrameLayout(layout, settings);
            int sampleCount = Math.Max(260, (int)Math.Ceiling(frameLayout.Perimeter / settings.SampleSpacing));
            double travel = (progress * settings.AnimationSpeed) % 1.0;
            double hueBands = Math.Max(1.05, settings.HueSpeed);
            DrawRoundedPaperFrame(dc, layout, frameLayout, settings);

            for (int i = 0; i < sampleCount; i++)
            {
                double distance = (i / (double)sampleCount) * frameLayout.Perimeter;
                Point point = frameLayout.GetPointAt(distance);
                double frac = distance / frameLayout.Perimeter;
                double hue = (((frac * hueBands) + travel) % 1.0) * 360.0;
                double wave = 0.5 + (0.5 * Math.Sin(((frac * Math.PI * 2.0 * 3.0) - (travel * Math.PI * 2.0))));
                double vibrance = 0.90 + (0.10 * wave);
                double glowAlpha = settings.GlowOpacity * (0.88 + (0.20 * wave));
                double coreAlpha = settings.CoreOpacity * (0.92 + (0.10 * wave));
                double bloomRadius = settings.GlowSize * (1.02 + (0.14 * wave));
                double glowRadius = Math.Max(settings.BorderThickness * 1.22, settings.GlowSize * (0.70 + (0.08 * wave)));
                double coreRadius = Math.Max(1.8, settings.BorderThickness * (0.44 + (0.06 * wave)));

                Color baseColor = ColorFromHsv(hue, 0.98, vibrance);
                Color glowColor = ColorFromHsv(hue + 10.0, 0.94, Math.Min(1.0, 0.94 + (0.04 * wave)));
                Color coreColor = ColorFromHsv(hue, 0.82, 1.0);

                DrawSoftDisc(dc, point, bloomRadius, WithAlpha(baseColor, glowAlpha * 0.26));
                DrawSoftDisc(dc, point, glowRadius, WithAlpha(glowColor, glowAlpha));
                DrawSoftDisc(dc, point, coreRadius, WithAlpha(coreColor, coreAlpha));
            }
        }


        private static void DrawRoundedPaperFrame(DrawingContext dc, OverlayContentLayout overlayLayout, RgbFrameLayout layout, RgbPolaroidLedSettings settings)
        {
            double outerStroke = OverlayLayoutHelper.ScaleFromHdReference(overlayLayout, 5.6, 2.0, 16.0);
            double shadowStroke = OverlayLayoutHelper.ScaleFromHdReference(overlayLayout, 6.6, 2.0, 18.0);

            Color paperColor = Color.FromArgb(238, 255, 249, 240);
            Color shadowColor = Color.FromArgb(38, 0, 0, 0);

            var shadowPen = new Pen(new SolidColorBrush(shadowColor), shadowStroke)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            if (shadowPen.CanFreeze) shadowPen.Freeze();

            var paperPen = new Pen(new SolidColorBrush(paperColor), outerStroke)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            if (paperPen.CanFreeze) paperPen.Freeze();

            var shadowRect = new Rect(layout.X + Math.Max(2.0, settings.BorderThickness * 0.40),
                                      layout.Y + Math.Max(2.0, settings.BorderThickness * 0.40),
                                      layout.Width,
                                      layout.Height);
            dc.DrawRoundedRectangle(null, shadowPen, shadowRect, layout.CornerRadius, layout.CornerRadius);
            dc.DrawRoundedRectangle(null, paperPen, layout.Rect, layout.CornerRadius, layout.CornerRadius);
        }


        private static void DrawSoftDisc(DrawingContext dc, Point center, double radius, Color color)
        {
            if (radius <= 0.1 || color.A == 0)
                return;

            RadialGradientBrush brush = new RadialGradientBrush
            {
                RadiusX = 1.0,
                RadiusY = 1.0,
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                MappingMode = BrushMappingMode.RelativeToBoundingBox
            };
            brush.GradientStops.Add(new GradientStop(color, 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(color.A * 0.62), color.R, color.G, color.B), 0.38));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(color.A * 0.16), color.R, color.G, color.B), 0.72));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1.0));
            if (brush.CanFreeze) brush.Freeze();

            dc.DrawEllipse(brush, null, center, radius, radius);
        }


        private static RgbFrameLayout GetFrameLayout(OverlayContentLayout layout, RgbPolaroidLedSettings settings)
        {
            Rect contentRect = WpfArtConvert.ToRect(layout.ContentRect);
            double x = contentRect.X + settings.Inset;
            double y = contentRect.Y + settings.Inset;
            double w = Math.Max(32.0, contentRect.Width - (settings.Inset * 2.0));
            double h = Math.Max(32.0, contentRect.Height - (settings.Inset * 2.0));
            double radius = Math.Min(settings.CornerRadius, Math.Min(w, h) * 0.25);
            return new RgbFrameLayout(x, y, w, h, radius);
        }


        private static Color WithAlpha(Color color, double alphaFactor)
        {
            byte alpha = (byte)Math.Clamp(Math.Round(alphaFactor * 255.0), 0.0, 255.0);
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }


        private static Color BlendColors(Color a, Color b, double bWeight)
        {
            double weight = Math.Clamp(bWeight, 0.0, 1.0);
            double aWeight = 1.0 - weight;
            return Color.FromRgb(
                (byte)Math.Clamp(Math.Round((a.R * aWeight) + (b.R * weight)), 0.0, 255.0),
                (byte)Math.Clamp(Math.Round((a.G * aWeight) + (b.G * weight)), 0.0, 255.0),
                (byte)Math.Clamp(Math.Round((a.B * aWeight) + (b.B * weight)), 0.0, 255.0));
        }


        private static Color ColorFromHsv(double hue, double saturation, double value)
        {
            double h = hue % 360.0;
            if (h < 0) h += 360.0;
            double s = Math.Clamp(saturation, 0.0, 1.0);
            double v = Math.Clamp(value, 0.0, 1.0);
            double c = v * s;
            double x = c * (1 - Math.Abs(((h / 60.0) % 2) - 1));
            double m = v - c;

            double r;
            double g;
            double b;

            if (h < 60)
                (r, g, b) = (c, x, 0);
            else if (h < 120)
                (r, g, b) = (x, c, 0);
            else if (h < 180)
                (r, g, b) = (0, c, x);
            else if (h < 240)
                (r, g, b) = (0, x, c);
            else if (h < 300)
                (r, g, b) = (x, 0, c);
            else
                (r, g, b) = (c, 0, x);

            return Color.FromRgb(
                (byte)Math.Clamp(Math.Round((r + m) * 255.0), 0.0, 255.0),
                (byte)Math.Clamp(Math.Round((g + m) * 255.0), 0.0, 255.0),
                (byte)Math.Clamp(Math.Round((b + m) * 255.0), 0.0, 255.0));
        }


        private static void SaveBitmap(BitmapSource bitmap, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream stream = File.Create(path);
            encoder.Save(stream);
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
                Name = "TitanEngine_RgbPolaroidLed_STA"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return tcs.Task;
        }


        private readonly struct RgbFrameLayout
        {
            private readonly double _x;
            private readonly double _y;
            private readonly double _w;
            private readonly double _h;
            private readonly double _r;
            private readonly double _line;
            private readonly double _arc;

            public RgbFrameLayout(double x, double y, double width, double height, double radius)
            {
                _x = x;
                _y = y;
                _w = Math.Max(16.0, width);
                _h = Math.Max(16.0, height);
                _r = Math.Max(0.0, Math.Min(radius, Math.Min(_w, _h) * 0.25));
                _line = Math.Max(0.0, _w - (2.0 * _r));
                _arc = (Math.PI * _r) / 2.0;
                Perimeter = (2.0 * _line) + (2.0 * Math.Max(0.0, _h - (2.0 * _r))) + (4.0 * _arc);
            }

            public double Perimeter { get; }
            public double X => _x;
            public double Y => _y;
            public double Width => _w;
            public double Height => _h;
            public double CornerRadius => _r;
            public Rect Rect => new Rect(_x, _y, _w, _h);

            public Point GetPointAt(double distance)
            {
                double d = distance % Perimeter;
                if (d < 0) d += Perimeter;

                double rightX = _x + _w - _r;
                double bottomY = _y + _h - _r;
                double vertical = Math.Max(0.0, _h - (2.0 * _r));

                if (d <= _line)
                    return new Point(_x + _r + d, _y);

                d -= _line;
                if (d <= _arc)
                    return ArcPoint(rightX, _y + _r, _r, -90.0 + ((d / _arc) * 90.0));

                d -= _arc;
                if (d <= vertical)
                    return new Point(_x + _w, _y + _r + d);

                d -= vertical;
                if (d <= _arc)
                    return ArcPoint(rightX, bottomY, _r, ((d / _arc) * 90.0));

                d -= _arc;
                if (d <= _line)
                    return new Point(_x + _w - _r - d, _y + _h);

                d -= _line;
                if (d <= _arc)
                    return ArcPoint(_x + _r, bottomY, _r, 90.0 + ((d / _arc) * 90.0));

                d -= _arc;
                if (d <= vertical)
                    return new Point(_x, _y + _h - _r - d);

                d -= vertical;
                return ArcPoint(_x + _r, _y + _r, _r, 180.0 + ((d / _arc) * 90.0));
            }

            private static Point ArcPoint(double cx, double cy, double radius, double angleDegrees)
            {
                if (radius <= 0.001)
                    return new Point(cx, cy);

                double radians = angleDegrees * Math.PI / 180.0;
                return new Point(
                    cx + (Math.Cos(radians) * radius),
                    cy + (Math.Sin(radians) * radius));
            }
        }
    }

    /// <summary>WPF-stay members moved verbatim từ ScratchVideoPreset.cs (Giai đoạn 1 đính chính). Gọi qua IImageServices.</summary>
    internal static class ScratchVideoArt
    {

        public static async Task<ScratchVideoGeneratedOverlay> GenerateOverlayAsync(
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
            string tempRoot = Path.Combine(Path.GetTempPath(), "TitanEngine", $"scratchvideo_{safeTag}");
            string overlayPattern = Path.Combine(tempRoot, "scratch_overlay_%04d.png");
            string previewDir = Path.Combine(tempRoot, "preview");
            int safeW = Math.Max(2, width);
            int safeH = Math.Max(2, height);
            double safeFps = Math.Clamp(frameRate, 12.0, 60.0);
            OverlayContentLayout safeLayout = layout ?? OverlayLayoutHelper.Create(safeW, safeH, safeW, safeH);
            ScratchVideoSettings settings = ScratchVideoPreset.FromJob(job, safeLayout);
            double cycleSeconds = Math.Max(2.0, Math.Min(6.0, Math.Max(settings.CycleSeconds, Math.Min(outputDurationSeconds, 6.0))));
            int frameCount = Math.Clamp((int)Math.Ceiling(cycleSeconds * safeFps), 36, 180);

            await RunOnStaThreadAsync(() =>
            {
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(previewDir);

                for (int i = 0; i < frameCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    double timeSeconds = i / safeFps;
                    int patternIndex = Math.Max(0, (int)Math.Floor(timeSeconds / settings.ChangeIntervalSeconds));
                    double localProgress = (timeSeconds % settings.ChangeIntervalSeconds) / settings.ChangeIntervalSeconds;
                    DrawingVisual overlayVisual = new DrawingVisual();

                    using (DrawingContext dc = overlayVisual.RenderOpen())
                    {
                        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, safeW, safeH));
                        DrawScratchPattern(dc, safeLayout, settings, patternIndex, localProgress);
                    }

                    RenderTargetBitmap overlayBitmap = new RenderTargetBitmap(safeW, safeH, 96, 96, PixelFormats.Pbgra32);
                    overlayBitmap.Render(overlayVisual);
                    if (overlayBitmap.CanFreeze) overlayBitmap.Freeze();

                    string overlayFile = ScratchVideoPreset.FormatSequenceFilePath(overlayPattern, i + 1);
                    SaveBitmap(overlayBitmap, overlayFile);

                    if (i < 8)
                    {
                        string previewFile = Path.Combine(previewDir, $"preview_{i + 1:D2}.png");
                        SaveBitmap(overlayBitmap, previewFile);
                    }
                }
            }, token);

            onLog($"[SCRATCH-VIDEO] Generated random scratch overlay @ {safeW}x{safeH} with {frameCount} frames. Pattern change interval={settings.ChangeIntervalSeconds:0.0}s. Preview={previewDir}");

            return new ScratchVideoGeneratedOverlay
            {
                TempRoot = tempRoot,
                OverlayPattern = overlayPattern,
                FrameCount = frameCount,
                FrameRate = safeFps,
                PreviewDirectory = previewDir,
                TempArtifacts = new List<string> { tempRoot }
            };
        }


        private static void DrawScratchPattern(DrawingContext dc, OverlayContentLayout layout, ScratchVideoSettings settings, int patternIndex, double localProgress)
        {
            Rect contentRect = WpfArtConvert.ToRect(layout.ContentRect);
            Random rng = new Random(ScratchVideoPreset.HashSeed(layout.CanvasWidth, layout.CanvasHeight, patternIndex));
            double flicker = 0.88 + (0.18 * Math.Sin(localProgress * Math.PI * 2.0));
            dc.PushClip(new RectangleGeometry(contentRect));

            for (int i = 0; i < settings.ScratchCount; i++)
                DrawScratchLine(dc, contentRect, settings, rng, false, flicker, localProgress);

            for (int i = 0; i < settings.MicroScratchCount; i++)
                DrawScratchLine(dc, contentRect, settings, rng, true, flicker, localProgress);

            for (int i = 0; i < settings.DustCount; i++)
                DrawDustShape(dc, contentRect, settings, rng, flicker);

            for (int i = 0; i < settings.DustClusterCount; i++)
                DrawDustCluster(dc, contentRect, settings, rng, flicker, localProgress);

            dc.Pop();
        }


        private static void DrawScratchLine(DrawingContext dc, Rect contentRect, ScratchVideoSettings settings, Random rng, bool micro, double flicker, double localProgress)
        {
            double x = contentRect.X + (rng.NextDouble() * contentRect.Width);
            double y = contentRect.Y + (rng.NextDouble() * contentRect.Height);
            double length = micro
                ? ScratchVideoPreset.Lerp(settings.MinLength * 0.45, settings.MaxLength * 0.40, rng.NextDouble())
                : ScratchVideoPreset.Lerp(settings.MinLength, settings.MaxLength, rng.NextDouble());
            double angle = (rng.NextDouble() * Math.PI * 2.0) + ((rng.NextDouble() - 0.5) * (micro ? 0.18 : 0.10));
            double dx = Math.Cos(angle) * length * 0.5;
            double dy = Math.Sin(angle) * length * 0.5;
            double jitter = micro ? 0.6 : 1.2;
            double jitterX = Math.Sin((localProgress * Math.PI * 2.0) + rng.NextDouble()) * jitter;
            double jitterY = Math.Cos((localProgress * Math.PI * 2.0) + rng.NextDouble()) * jitter;

            Point start = new Point(x - dx + jitterX, y - dy + jitterY);
            Point end = new Point(x + dx + jitterX, y + dy + jitterY);
            double thickness = ScratchVideoPreset.Lerp(settings.MinThickness, settings.MaxThickness, rng.NextDouble()) * (micro ? 0.65 : 1.0);
            double alpha = (settings.BaseOpacity * (micro ? 0.72 : 1.0) * (0.72 + (0.38 * rng.NextDouble())) * flicker);
            double glowAlpha = settings.GlowOpacity * (micro ? 0.60 : 1.0) * (0.70 + (0.40 * rng.NextDouble())) * flicker;

            Pen glowPen = CreatePen(WithAlpha(Colors.White, glowAlpha), thickness + (micro ? 0.8 : 1.6));
            Pen linePen = CreatePen(WithAlpha(Colors.White, alpha), thickness);

            dc.DrawLine(glowPen, start, end);
            dc.DrawLine(linePen, start, end);
        }


        private static void DrawDustShape(DrawingContext dc, Rect contentRect, ScratchVideoSettings settings, Random rng, double flicker)
        {
            double x = contentRect.X + (rng.NextDouble() * contentRect.Width);
            double y = contentRect.Y + (rng.NextDouble() * contentRect.Height);
            double shapeSize = 0.8 + (rng.NextDouble() * 2.4);
            double alpha = settings.BaseOpacity * 0.40 * (0.45 + (0.70 * rng.NextDouble())) * flicker;
            Brush dustBrush = CreateBrush(WithAlpha(Colors.White, alpha));

            if (rng.NextDouble() < 0.35)
            {
                dc.DrawEllipse(dustBrush, null, new Point(x, y), shapeSize, shapeSize);
            }
            else if (rng.NextDouble() < 0.65)
            {
                dc.DrawRoundedRectangle(dustBrush, null, new Rect(x, y, shapeSize * 1.6, shapeSize * 0.9), shapeSize * 0.25, shapeSize * 0.25);
            }
            else
            {
                double w = shapeSize * (1.4 + (rng.NextDouble() * 1.6));
                double h = shapeSize * (0.25 + (rng.NextDouble() * 0.35));
                double angle = rng.NextDouble() * 360.0;
                dc.PushTransform(new RotateTransform(angle, x, y));
                dc.DrawRoundedRectangle(dustBrush, null, new Rect(x, y, w, h), h * 0.5, h * 0.5);
                dc.Pop();
            }
        }


        private static void DrawDustCluster(DrawingContext dc, Rect contentRect, ScratchVideoSettings settings, Random rng, double flicker, double localProgress)
        {
            double centerX = contentRect.X + (rng.NextDouble() * contentRect.Width);
            double centerY = contentRect.Y + (rng.NextDouble() * contentRect.Height);
            int particleCount = 4 + rng.Next(8);
            double radius = 4.0 + (rng.NextDouble() * 18.0);
            double drift = 1.2 + (rng.NextDouble() * 2.4);
            double phase = rng.NextDouble() * Math.PI * 2.0;

            for (int i = 0; i < particleCount; i++)
            {
                double t = particleCount <= 1 ? 0.0 : (double)i / (particleCount - 1);
                double theta = phase + (t * Math.PI * 2.0);
                double offset = radius * (0.35 + rng.NextDouble());
                double x = centerX + (Math.Cos(theta + (localProgress * 0.8)) * offset) + (Math.Sin(localProgress * Math.PI * 2.0 + phase) * drift);
                double y = centerY + (Math.Sin(theta + (localProgress * 0.6)) * offset * 0.7) + (Math.Cos(localProgress * Math.PI * 2.0 + phase) * drift * 0.8);
                double size = 0.6 + (rng.NextDouble() * 1.8);
                double alpha = settings.BaseOpacity * (0.18 + (0.22 * rng.NextDouble())) * flicker;
                Brush brush = CreateBrush(WithAlpha(Colors.White, alpha));
                dc.DrawEllipse(brush, null, new Point(x, y), size, size);
            }

            if (rng.NextDouble() < 0.55)
            {
                double smudgeW = 10.0 + (rng.NextDouble() * 26.0);
                double smudgeH = 1.2 + (rng.NextDouble() * 2.4);
                double smudgeAngle = rng.NextDouble() * 360.0;
                double smudgeAlpha = settings.BaseOpacity * 0.14 * flicker;
                Brush smudgeBrush = CreateBrush(WithAlpha(Colors.White, smudgeAlpha));
                dc.PushTransform(new RotateTransform(smudgeAngle, centerX, centerY));
                dc.DrawRoundedRectangle(smudgeBrush, null, new Rect(centerX - (smudgeW * 0.5), centerY - (smudgeH * 0.5), smudgeW, smudgeH), smudgeH, smudgeH);
                dc.Pop();
            }
        }


        private static Brush CreateBrush(Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }


        private static Pen CreatePen(Color color, double thickness)
        {
            Pen pen = new Pen(CreateBrush(color), Math.Max(0.4, thickness))
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            if (pen.CanFreeze) pen.Freeze();
            return pen;
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
                Name = "TitanEngine_ScratchVideo_STA"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return tcs.Task;
        }
    }

    /// <summary>WPF-stay members moved verbatim từ SnowfallOverlayPreset.cs (Giai đoạn 1 đính chính). Gọi qua IImageServices.</summary>
    internal static class SnowfallOverlayArt
    {
        private static readonly Lazy<IReadOnlyList<BitmapSource>> SnowflakeSpriteCache =
            new(CreateSnowflakeSprites, LazyThreadSafetyMode.ExecutionAndPublication);


        public static async Task<SnowfallGeneratedOverlay> GenerateOverlayAsync(
            int width,
            int height,
            OverlayContentLayout? layout,
            double frameRate,
            SnowfallOverlayRecipe recipe,
            string? tag,
            CancellationToken token,
            Action<string> onLog)
        {
            string safeTag = string.IsNullOrWhiteSpace(tag)
                ? Guid.NewGuid().ToString("N").Substring(0, 8)
                : Regex.Replace(tag.Trim(), @"[^\w\-]+", "_");
            string tempRoot = Path.Combine(Path.GetTempPath(), "TitanEngine", $"snowfall_{safeTag}");
            string overlayPattern = Path.Combine(tempRoot, "snow_overlay_%04d.png");
            string previewDir = Path.Combine(tempRoot, "preview");
            int safeW = Math.Max(2, width);
            int safeH = Math.Max(2, height);
            double safeFps = Math.Clamp(frameRate, 12.0, 60.0);
            double renderScale = Math.Min(1.0, 1280.0 / Math.Max(safeW, safeH));
            int renderW = Math.Max(320, (int)Math.Round(safeW * renderScale));
            int renderH = Math.Max(320, (int)Math.Round(safeH * renderScale));
            if ((renderW & 1) != 0) renderW--;
            if ((renderH & 1) != 0) renderH--;
            int frameCount = Math.Clamp((int)Math.Ceiling(Math.Max(2.0, recipe.CycleSeconds) * safeFps), 36, 90);
            OverlayContentLayout renderLayout = new OverlayContentLayout
            {
                CanvasWidth = renderW,
                CanvasHeight = renderH,
                // Snow should behave like an environmental overlay, not a media-content-only frame.
                // Use the full render canvas so particles can cover the entire output frame.
                ContentRect = new PixelRect(0, 0, renderW, renderH)
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
                        DrawSnowLayers(dc, renderLayout, recipe, progress);
                    }

                    RenderTargetBitmap overlayBitmap = new RenderTargetBitmap(renderW, renderH, 96, 96, PixelFormats.Pbgra32);
                    overlayBitmap.Render(overlayVisual);
                    if (overlayBitmap.CanFreeze) overlayBitmap.Freeze();

                    string overlayFile = SnowfallOverlayPreset.FormatSequenceFilePath(overlayPattern, i + 1);
                    SaveBitmap(overlayBitmap, overlayFile);

                    if (i < 8)
                    {
                        string previewFile = Path.Combine(previewDir, $"preview_{i + 1:D2}.png");
                        SaveBitmap(overlayBitmap, previewFile);
                    }
                }
            }, token);

            onLog($"[SNOWFALL] Generated particle overlay @ {renderW}x{renderH} (target {safeW}x{safeH}), fps={safeFps:0.00}, frames={frameCount}, preview={previewDir}");

            return new SnowfallGeneratedOverlay
            {
                TempRoot = tempRoot,
                OverlayPattern = overlayPattern,
                FrameCount = frameCount,
                FrameRate = safeFps,
                Width = renderW,
                Height = renderH,
                PreviewDirectory = previewDir,
                TempArtifacts = new List<string> { tempRoot }
            };
        }


        private static void DrawSnowLayers(DrawingContext dc, OverlayContentLayout layout, SnowfallOverlayRecipe recipe, double progress)
        {
            Rect canvasRect = new Rect(
                0,
                0,
                Math.Max(2.0, layout.CanvasWidth),
                Math.Max(2.0, layout.CanvasHeight));

            dc.PushClip(new RectangleGeometry(canvasRect));

            foreach (SnowOverlayLayerRecipe layer in recipe.Layers)
            {
                for (int i = 0; i < layer.Count; i++)
                    DrawSnowParticle(dc, canvasRect, recipe, layer, i, progress);
            }

            dc.Pop();
        }


        private static void DrawSnowParticle(DrawingContext dc, Rect contentRect, SnowfallOverlayRecipe recipe, SnowOverlayLayerRecipe layer, int index, double progress)
        {
            Random rng = new Random(SnowfallOverlayPreset.HashSeed(recipe.PresetName, layer.Name, index));
            double size = SnowfallOverlayPreset.Lerp(layer.MinSize, layer.MaxSize, rng.NextDouble());
            double opacityPulse = 0.96 + (0.22 * Math.Sin((progress * Math.PI * 2.0 * (1.0 + rng.NextDouble())) + (rng.NextDouble() * Math.PI * 2.0)));
            double opacity = Math.Clamp(
                SnowfallOverlayPreset.Lerp(layer.MinOpacity, layer.MaxOpacity, rng.NextDouble()) * opacityPulse,
                0.05,
                1.0);
            double verticalLoops = SnowfallOverlayPreset.Lerp(layer.MinVerticalLoops, layer.MaxVerticalLoops, rng.NextDouble());
            double sway = SnowfallOverlayPreset.Lerp(layer.MinSway, layer.MaxSway, rng.NextDouble());
            double drift = SnowfallOverlayPreset.Lerp(layer.MinDrift, layer.MaxDrift, rng.NextDouble());
            double baseX = rng.NextDouble() * contentRect.Width;
            double baseY = rng.NextDouble();
            double phase = rng.NextDouble() * Math.PI * 2.0;
            double frequency = 1.0 + (rng.NextDouble() * 2.5);
            double margin = Math.Max(12.0, size * 3.0);
            double rangeH = contentRect.Height + (margin * 2.0);
            double rangeW = contentRect.Width + (margin * 2.0);

            double totalFall = (baseY * rangeH) + (verticalLoops * progress * rangeH);
            double fallPosition = SnowfallOverlayPreset.PositiveMod(totalFall, rangeH);
            double descentTravel = baseY + (verticalLoops * progress);
            double flutter =
                (Math.Sin((progress * Math.PI * 2.0 * frequency) + phase) * sway * 0.12) +
                (Math.Cos((progress * Math.PI * 2.0 * (0.45 + (frequency * 0.18))) + phase) * sway * 0.04);
            double windPush = drift * (0.35 + (descentTravel * 4.20));
            double particleX = contentRect.X - margin + SnowfallOverlayPreset.PositiveMod(baseX + windPush + flutter + margin, rangeW);
            double particleY = contentRect.Y - margin + fallPosition;

            if (layer.UseStreaks)
            {
                DrawStreakParticle(dc, particleX, particleY, size, opacity, layer, phase, progress);
                return;
            }

            BitmapSource sprite = SelectSnowflakeSprite(layer.Name, index);
            double spriteSize = Math.Max(8.0, size * 4.60);
            double rotation = (rng.NextDouble() * 360.0) + (Math.Sin((progress * Math.PI * 2.0 * (0.7 + rng.NextDouble())) + phase) * 8.0);

            if (layer.UseGlow || layer.BlurMultiplier > 0.01)
            {
                double glowSize = spriteSize * (1.24 + (layer.BlurMultiplier * 0.12));
                DrawSnowflakeSprite(dc, sprite, particleX, particleY, glowSize, opacity * 0.26, rotation);
            }

            DrawSnowflakeSprite(dc, sprite, particleX, particleY, spriteSize, opacity, rotation);
        }


        private static void DrawStreakParticle(
            DrawingContext dc,
            double x,
            double y,
            double size,
            double opacity,
            SnowOverlayLayerRecipe layer,
            double phase,
            double progress)
        {
            double angle = (72.0 + (Math.Sin((progress * Math.PI * 2.0 * 0.75) + phase) * 4.0)) * (Math.PI / 180.0);
            double length = Math.Max(size * 2.0, size * layer.StreakLengthMultiplier);
            double dx = Math.Cos(angle) * length * 0.5;
            double dy = Math.Sin(angle) * length * 0.5;
            Point start = new Point(x - dx, y - dy);
            Point end = new Point(x + dx, y + dy);

            Pen glowPen = CreatePen(WithAlpha(Colors.White, opacity * 0.24), Math.Max(1.0, size * 0.95));
            Pen corePen = CreatePen(WithAlpha(Colors.White, Math.Min(1.0, opacity * 0.82)), Math.Max(1.0, size * 0.56));
            dc.DrawLine(glowPen, start, end);
            dc.DrawLine(corePen, start, end);

            Point tail = new Point(x - (dx * 0.70), y - (dy * 0.70));
            Brush tailBrush = CreateBrush(WithAlpha(Colors.White, opacity * 0.16));
            dc.DrawEllipse(tailBrush, null, tail, size * 0.92, size * 0.92);
        }


        private static Brush CreateBrush(Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }


        private static IReadOnlyList<BitmapSource> CreateSnowflakeSprites()
        {
            var sprites = new List<BitmapSource>(5);
            for (int variant = 0; variant < 5; variant++)
                sprites.Add(CreateSnowflakeSprite(128, variant));
            return sprites;
        }


        private static BitmapSource CreateSnowflakeSprite(int canvasSize, int variant)
        {
            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                double center = canvasSize / 2.0;
                Point pivot = new Point(center, center);
                double radius = canvasSize * (0.28 + (variant * 0.012));
                double branchInsetA = 0.42 + (variant * 0.03);
                double branchInsetB = 0.66 + (variant * 0.02);
                double branchLengthA = canvasSize * (0.09 + (variant * 0.006));
                double branchLengthB = canvasSize * (0.07 + (variant * 0.005));
                double branchAngleA = (26.0 + (variant * 2.0)) * (Math.PI / 180.0);
                double branchAngleB = (18.0 + (variant * 1.5)) * (Math.PI / 180.0);
                double coreThickness = Math.Max(1.9, canvasSize * 0.030);
                double branchThickness = Math.Max(1.2, coreThickness * 0.56);
                Pen mainPen = CreatePen(WithAlpha(Colors.White, 0.96), coreThickness);
                Pen branchPen = CreatePen(WithAlpha(Colors.White, 0.90), branchThickness);
                Pen sparklePen = CreatePen(WithAlpha(Colors.White, 0.68), Math.Max(1.0, branchThickness * 0.62));

                for (int arm = 0; arm < 6; arm++)
                {
                    double baseAngle = (Math.PI / 3.0 * arm) + (variant * 0.045);
                    Point armStart = PointOnCircle(pivot, radius * 0.08, baseAngle + Math.PI);
                    Point armEnd = PointOnCircle(pivot, radius, baseAngle);
                    dc.DrawLine(mainPen, armStart, armEnd);

                    DrawBranchPair(dc, pivot, baseAngle, radius * branchInsetA, branchLengthA, branchAngleA, branchPen);
                    DrawBranchPair(dc, pivot, baseAngle, radius * branchInsetB, branchLengthB, branchAngleB, branchPen);

                    if ((variant % 2) == 0)
                    {
                        double miniInset = radius * (0.78 + (arm % 2 == 0 ? 0.00 : 0.04));
                        double miniLength = branchLengthB * 0.58;
                        DrawBranchPair(dc, pivot, baseAngle, miniInset, miniLength, branchAngleB * 0.8, sparklePen);
                    }
                }

                double innerRadius = canvasSize * (0.072 + (variant * 0.004));
                dc.DrawEllipse(CreateBrush(WithAlpha(Colors.White, 0.90)), null, pivot, innerRadius, innerRadius);
                dc.DrawEllipse(CreateBrush(WithAlpha(Colors.White, 0.18)), null, pivot, innerRadius * 1.80, innerRadius * 1.80);
            }

            RenderTargetBitmap bitmap = new RenderTargetBitmap(canvasSize, canvasSize, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            if (bitmap.CanFreeze) bitmap.Freeze();
            return bitmap;
        }


        private static void DrawBranchPair(
            DrawingContext dc,
            Point pivot,
            double baseAngle,
            double inset,
            double branchLength,
            double branchAngle,
            Pen branchPen)
        {
            Point origin = PointOnCircle(pivot, inset, baseAngle);
            Point first = PointOnCircle(origin, branchLength, baseAngle + Math.PI - branchAngle);
            Point second = PointOnCircle(origin, branchLength, baseAngle + Math.PI + branchAngle);
            dc.DrawLine(branchPen, origin, first);
            dc.DrawLine(branchPen, origin, second);
        }


        private static Point PointOnCircle(Point pivot, double radius, double angle)
            => new Point(
                pivot.X + (Math.Cos(angle) * radius),
                pivot.Y + (Math.Sin(angle) * radius));


        private static BitmapSource SelectSnowflakeSprite(string layerName, int particleIndex)
        {
            IReadOnlyList<BitmapSource> sprites = SnowflakeSpriteCache.Value;
            int spriteIndex = Math.Abs(SnowfallOverlayPreset.HashSeed("snowflake", layerName, particleIndex)) % sprites.Count;
            return sprites[spriteIndex];
        }


        private static void DrawSnowflakeSprite(
            DrawingContext dc,
            BitmapSource sprite,
            double centerX,
            double centerY,
            double size,
            double opacity,
            double rotationDegrees)
        {
            if (opacity <= 0.001 || size <= 1.0)
                return;

            double safeSize = Math.Max(2.0, size);
            Rect rect = new Rect(centerX - (safeSize / 2.0), centerY - (safeSize / 2.0), safeSize, safeSize);

            dc.PushOpacity(Math.Clamp(opacity, 0.0, 1.0));
            dc.PushTransform(new RotateTransform(rotationDegrees, centerX, centerY));
            dc.DrawImage(sprite, rect);
            dc.Pop();
            dc.Pop();
        }


        private static Pen CreatePen(Color color, double thickness)
        {
            Pen pen = new Pen(CreateBrush(color), Math.Max(0.4, thickness))
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            if (pen.CanFreeze) pen.Freeze();
            return pen;
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
                Name = "TitanEngine_Snowfall_STA"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return tcs.Task;
        }
    }

    /// <summary>WPF-stay members moved verbatim từ SoundBarVisualizerPreset.cs (Giai đoạn 1 đính chính). Gọi qua IImageServices.</summary>
    internal static class SoundBarVisualizerArt
    {

        /// <summary>
        /// Creates the single <see cref="RenderTargetBitmap"/> a whole overlay pass draws through.
        /// <para>
        /// THE DEFECT. This used to be <c>new RenderTargetBitmap(...)</c> INSIDE the per-frame loop.
        /// Each frame allocated a fresh D3D/WIC render target plus a <c>BitmapVisualManager</c>, and
        /// the finaliser-driven reclamation of that chain promoted it straight to the oldest
        /// generation, so a full BLOCKING gen-2 collection ran every other frame. Measured on this
        /// job at 1080x1080, 50 frames of a deliberately trivial draw method:
        /// <c>GC.CollectionCount(0) = (1) = (2) = 33</c> with a fresh bitmap per frame, versus
        /// <c>1 / 1 / 1</c> with one reused + cleared bitmap. Scaled to the 150-frame loop that is
        /// ~99 forced full GCs per render, with the pause on every one of them.
        /// <para>
        /// It was also STYLE-INDEPENDENT, which is why an earlier measurement round could not use
        /// GC.CollectionCount to tell any two styles apart: the floor was identical under all of
        /// them, and it sits underneath whichever renderer allocation the round was trying to rank.
        /// <para>
        /// WHY REUSE IS SAFE HERE. <see cref="RenderTargetBitmap.Render"/> requires the calling
        /// thread to be the one allowed to touch the bitmap, so a shared target would be unsafe if
        /// the loop were threaded. It is not: <see cref="RunOnStaThreadAsync"/> runs the ENTIRE
        /// pass body - every frame's build, every Render and every Save - inside one
        /// <c>action()</c> on ONE freshly created STA thread. The bitmap never leaves that thread,
        /// and each pass builds its own, so two concurrent renders cannot alias.
        /// <para>
        /// WHY IT MUST NOT BE FROZEN. The old code froze each bitmap before saving it, because
        /// freezing is what makes a <see cref="BitmapSource"/> safe to read from anywhere. A target
        /// that is going to be Render()ed into again must stay UNFROZEN:
        /// <c>BitmapVisualManager</c> throws
        /// <c>"The Image passed to the ImageVisualManager cannot be frozen."</c> on the second
        /// frame. Freezing is therefore dropped here; nothing downstream needs it, because
        /// <see cref="SaveBitmap"/> encodes on the same thread that produced the pixels.
        /// <para>
        /// PIXEL EQUALITY. Verified, not assumed. Rendering the loop's exact frame body (DrawingVisual +
        /// full-canvas transparent rect + <see cref="SoundBarStyleRenderer.DrawFrame"/>) and comparing
        /// every frame's pixels against a fresh-bitmap render of the same frame, at 1080x1080 with
        /// BarCount 256: <c>Clear()</c> present -> 0 of 60 frames differ; <c>Clear()</c> removed ->
        /// 59 of 60 differ, the first at frame 1. Independently, a SHA-256 census of frames
        /// 1/2/17/75/149 of the real <see cref="GenerateOverlayAsync"/> matched byte for byte between
        /// this path and the per-frame-allocation path it replaced, for five styles at BarCount 40
        /// and 256. So the clear is load-bearing, not decorative: <see cref="BitmapSource.Clear"/>
        /// returns the surface to exactly the state a freshly constructed target is in.
        /// <para>
        /// ONE ANOMALY, recorded because it was observed and not explained. Deleting the
        /// <see cref="BitmapSource.Clear"/> call from THIS method and re-rendering all ten
        /// style/bar-count combinations through the real <see cref="GenerateOverlayAsync"/> produced
        /// PNG hashes identical to the run with the clear present - both builds hash-verified, cache
        /// purged, frame mtimes confirmed fresh. The direct loop reproduction above, which differs
        /// from this method only in running on the harness's own STA thread rather than the one
        /// <see cref="RunOnStaThreadAsync"/> creates, does show the corruption. Whatever makes this
        /// particular call site currently immune is not something to depend on, and the clear costs
        /// one memset of the target, so it stays. Do not remove it on the strength of that anomaly.
        /// </summary>
        private static RenderTargetBitmap RentFrameTarget(int width, int height)
            => new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);


        /// <summary>
        /// Renders the full PNG sequence (or reuses the cached one). Never throws: failures are logged and
        /// null is returned so the pipeline can continue without a soundbar.
        /// <para>
        /// A render that is cancelled or that throws leaves a directory of PNGs with NO manifest. The
        /// manifest is what makes a directory a usable cache, and nothing else ever lists a directory
        /// for cleanup, so without <see cref="DeletePartialTempRoot"/> those frames accumulated in the
        /// temp tree forever (a 150-frame 1280x720 sequence is tens of MB). The partial directory is
        /// deleted on both failure paths; whether a SUCCESSFUL directory should be cleaned up at all is
        /// a separate question, answered by whoever owns <see cref="SoundBarGeneratedOverlay.TempArtifacts"/>.
        /// </para>
        /// </summary>
        public static async Task<SoundBarGeneratedOverlay?> GenerateOverlayAsync(
            AudioAnalysisResult analysis,
            SoundBarSettings settings,
            ColorPreset palette,
            int canvasWidth,
            int canvasHeight,
            string? tag,
            Action<string> onLog,
            CancellationToken token)
        {
            // Non-null from the moment the cache directory is chosen until the manifest is committed.
            // Cleared on success so the catch blocks below leave the finished cache alone.
            string? pendingTempRoot = null;

            try
            {
                if (onLog == null)
                    onLog = _ => { };

                if (analysis == null)
                {
                    onLog("[SOUNDBAR] No audio analysis supplied; skipping the soundbar overlay.");
                    return null;
                }

                AudioFrameData[] source = analysis.Frames ?? Array.Empty<AudioFrameData>();
                if (source.Length == 0)
                {
                    onLog("[SOUNDBAR] Audio analysis contains zero frames; skipping the soundbar overlay.");
                    return null;
                }

                SoundBarSettings safeSettings = settings is null ? new SoundBarSettings() : settings.Clone();
                ColorPreset safePalette = palette ?? ColorPresets.GetOrDefault(null);

                int safeW = Math.Max(2, canvasWidth);
                int safeH = Math.Max(2, canvasHeight);
                double safeFps = SoundBarVisualizerPreset.ResolveFrameRate(safeSettings);
                int frameCount = SoundBarVisualizerPreset.ResolveFrameCount(safeSettings);
                SoundBarVisualizerPreset.ResolveRenderSize(safeW, safeH, out int renderW, out int renderH);

                string styleId = SoundBarVisualizerPreset.NormalizeStyleId(safeSettings.Style.ToString());
                string cacheKey = SoundBarVisualizerPreset.BuildCacheKey(safeSettings, safePalette, safeW, safeH, analysis.AudioFingerprint);
                string safeTag = string.IsNullOrWhiteSpace(tag)
                    ? cacheKey
                    : Regex.Replace(tag.Trim(), @"[^\w\-]+", "_");

                string tempRoot = Path.Combine(Path.GetTempPath(), "TitanEngine", "soundbar_cache", styleId, cacheKey);
                pendingTempRoot = tempRoot;
                string overlayPattern = Path.Combine(tempRoot, "soundbar_%04d.png");
                string previewDir = Path.Combine(tempRoot, "preview");
                string manifestPath = Path.Combine(tempRoot, "manifest.json");

                if (SoundBarVisualizerPreset.TryLoadCache(manifestPath, overlayPattern, previewDir, cacheKey, out SoundBarGeneratedOverlay? cached))
                {
                    onLog($"[SOUNDBAR-CACHE] Reusing {styleId} overlay ({cacheKey}) @ {cached!.Width}x{cached.Height}, frames={cached.FrameCount}, preview={cached.PreviewDirectory}");
                    return cached;
                }

                // Resample onto the exact PNG count so the ffmpeg loop length and the render loop agree.
                // Only references are copied - the band/waveform arrays are shared, never duplicated.
                var renderFrames = new AudioFrameData[frameCount];
                int sourceCount = source.Length;
                for (int i = 0; i < frameCount; i++)
                {
                    int sourceIndex = (int)Math.Floor((i / (double)frameCount) * sourceCount);
                    renderFrames[i] = source[Math.Clamp(sourceIndex, 0, sourceCount - 1)];
                }

                var state = new SoundBarRenderState();

                await RunOnStaThreadAsync(() =>
                {
                    Directory.CreateDirectory(tempRoot);
                    Directory.CreateDirectory(previewDir);

                    // ONE target for the whole pass, cleared per frame. See RentFrameTarget.
                    RenderTargetBitmap overlayBitmap = RentFrameTarget(renderW, renderH);

                    for (int i = 0; i < frameCount; i++)
                    {
                        token.ThrowIfCancellationRequested();

                        DrawingVisual overlayVisual = new DrawingVisual();
                        using (DrawingContext dc = overlayVisual.RenderOpen())
                        {
                            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, renderW, renderH));
                            SoundBarStyleRenderer.DrawFrame(dc, renderW, renderH, safeSettings, safePalette, renderFrames, i, state);
                        }

                        // Clear before Render. Render COMPOSITES onto whatever the surface already
                        // holds, so without this frame N would carry frame N-1's pixels into itself
                        // wherever this frame draws nothing.
                        overlayBitmap.Clear();
                        overlayBitmap.Render(overlayVisual);

                        SaveBitmap(overlayBitmap, SoundBarVisualizerPreset.FormatSequenceFilePath(overlayPattern, i + 1));

                        if (i < SoundBarVisualizerPreset.MaxPreviewFrames)
                        {
                            string previewFile = Path.Combine(previewDir, $"preview_{i + 1:D2}.png");
                            SaveBitmap(overlayBitmap, previewFile);
                        }
                    }
                }, token);

                var overlay = new SoundBarGeneratedOverlay
                {
                    TempRoot = tempRoot,
                    OverlayPattern = overlayPattern,
                    FrameCount = frameCount,
                    FrameRate = safeFps,
                    Width = renderW,
                    Height = renderH,
                    PreviewDirectory = previewDir,
                    TempArtifacts = new List<string> { tempRoot }
                };

                SoundBarVisualizerPreset.WriteManifest(manifestPath, overlay, styleId, safePalette.Id, analysis.AudioFingerprint, cacheKey);

                // Committed: the directory is now a valid cache and must survive.
                pendingTempRoot = null;

                onLog($"[SOUNDBAR] Generated {styleId} overlay ({safeTag}) -> {cacheKey}, palette={safePalette.Id}, target={safeW}x{safeH}, render={renderW}x{renderH}, fps={safeFps.ToString("F3", CultureInfo.InvariantCulture)}, frames={frameCount}, preview={previewDir}");
                return overlay;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                SoundBarVisualizerPreset.DeletePartialTempRoot(pendingTempRoot, onLog);
                onLog?.Invoke("[SOUNDBAR] Cancelled.");
                return null;
            }
            catch (Exception ex)
            {
                SoundBarVisualizerPreset.DeletePartialTempRoot(pendingTempRoot, onLog);
                onLog?.Invoke($"[SOUNDBAR-ERROR] Soundbar overlay generation failed: {ex.Message}");
                return null;
            }
        }


        /// <summary>
        /// Renders a single representative PNG for the picker. Fully deterministic - no audio file needed.
        /// Returns the file path, or null on any failure. Never throws.
        /// </summary>
        public static async Task<string?> GeneratePreviewPngAsync(
            SoundBarSettings settings,
            ColorPreset palette,
            int width,
            int height,
            string cacheDirectory,
            Action<string> onLog,
            CancellationToken token)
        {
            try
            {
                SoundBarSettings safeSettings = settings is null ? new SoundBarSettings() : settings.Clone();
                ColorPreset safePalette = palette ?? ColorPresets.GetOrDefault(null);
                string styleId = SoundBarVisualizerPreset.NormalizeStyleId(safeSettings.Style.ToString());

                string directory = string.IsNullOrWhiteSpace(cacheDirectory)
                    ? Path.Combine(Path.GetTempPath(), "TitanEngine", "soundbar_cache", "previews")
                    : cacheDirectory;

                int renderW = Math.Max(16, width);
                int renderH = Math.Max(16, height);

                // The filename must change whenever ANY tunable changes: the picker re-requests this on a
                // 250 ms debounce while the user drags sliders, so a styleId__paletteId key returned the
                // previously written PNG and dragging showed literally no change. The hash covers the
                // same visual material as the overlay cache key. Old files are simply never hit again.
                string previewKey = SoundBarVisualizerPreset.BuildPreviewCacheKey(safeSettings, safePalette, renderW, renderH);
                string filePath = Path.Combine(directory, $"{styleId}__{safePalette.Id}__{previewKey}.png");
                if (File.Exists(filePath))
                    return filePath;

                AudioFrameData[] frames = SoundBarVisualizerPreset.BuildPreviewFrames();
                const int PreviewFrameIndex = 6;
                var state = new SoundBarRenderState();

                await RunOnStaThreadAsync(() =>
                {
                    Directory.CreateDirectory(directory);

                    DrawingVisual previewVisual = new DrawingVisual();
                    using (DrawingContext dc = previewVisual.RenderOpen())
                    {
                        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, renderW, renderH));
                        SoundBarStyleRenderer.DrawFrame(dc, renderW, renderH, safeSettings, safePalette, frames, PreviewFrameIndex, state);
                    }

                    RenderTargetBitmap previewBitmap = new RenderTargetBitmap(renderW, renderH, 96, 96, PixelFormats.Pbgra32);
                    previewBitmap.Render(previewVisual);
                    if (previewBitmap.CanFreeze)
                        previewBitmap.Freeze();

                    SaveBitmap(previewBitmap, filePath);
                }, token);

                onLog?.Invoke($"[SOUNDBAR] Preview written -> {filePath}");
                return filePath;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                onLog?.Invoke("[SOUNDBAR] Preview cancelled.");
                return null;
            }
            catch (Exception ex)
            {
                onLog?.Invoke($"[SOUNDBAR-PREVIEW-ERROR] Preview generation failed: {ex.Message}");
                return null;
            }
        }


        // ------------------------------------------------------------------
        // IO / threading helpers
        // ------------------------------------------------------------------

        private static void SaveBitmap(BitmapSource bitmap, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream stream = File.Create(path);
            encoder.Save(stream);
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
                Name = "TitanEngine_SoundBar_STA"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return tcs.Task;
        }
    }
}
