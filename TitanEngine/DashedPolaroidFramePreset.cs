using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TitanEngine
{
    public sealed class DashedPolaroidFrameSettings
    {
        public double Inset { get; set; }
        public double CornerRadius { get; set; }
        public double StrokeThickness { get; set; }
        public double ShadowThickness { get; set; }
        public double DashLength { get; set; }
        public double DashGap { get; set; }
        public double FrameOpacity { get; set; }
        public double ShadowOpacity { get; set; }
        public double GlowOpacity { get; set; }
        public double GlowThickness { get; set; }
        public double CycleSeconds { get; set; }
        public double SampleSpacing { get; set; }
    }

    public sealed class DashedPolaroidGeneratedOverlay
    {
        public string TempRoot { get; set; } = string.Empty;
        public string OverlayPattern { get; set; } = string.Empty;
        public int FrameCount { get; set; }
        public double FrameRate { get; set; }
        public string PreviewDirectory { get; set; } = string.Empty;
        public List<string> TempArtifacts { get; set; } = new List<string>();
    }

    public static class DashedPolaroidFramePreset
    {
        public static DashedPolaroidFrameSettings FromJob(RenderJob job, OverlayContentLayout layout)
        {
            double intensity = Math.Clamp(job.FxIntensity / 100.0, 0.0, 1.0);
            double contentWidth = Math.Max(2.0, layout.ContentWidth);
            double minDim = Math.Max(240.0, layout.ContentMinDimension);
            double inset = Math.Clamp((contentWidth / 28.0) + (contentWidth / 84.0) * 0.85, 24.0, contentWidth * 0.14);
            double strokeThickness = OverlayLayoutHelper.ScaleFromHdReference(layout, 6.4, 2.0, 18.0);
            double shadowThickness = OverlayLayoutHelper.ScaleFromHdReference(layout, 7.8, 2.0, 24.0);
            double glowThickness = OverlayLayoutHelper.ScaleFromHdReference(layout, 14.8, 4.0, 40.0);
            double dashLength = Math.Clamp(minDim * (0.020 + (0.004 * intensity)), 14.0, 42.0);
            double dashGap = Math.Clamp(dashLength * 0.72, 10.0, 28.0);

            return new DashedPolaroidFrameSettings
            {
                Inset = inset,
                CornerRadius = Math.Clamp(minDim * 0.026, 14.0, 34.0),
                StrokeThickness = strokeThickness,
                ShadowThickness = shadowThickness,
                DashLength = dashLength,
                DashGap = dashGap,
                FrameOpacity = 0.96,
                ShadowOpacity = 0.14 + (0.03 * intensity),
                GlowOpacity = 0.26 + (0.14 * intensity),
                GlowThickness = glowThickness,
                CycleSeconds = 2.10 - (0.22 * intensity),
                SampleSpacing = 1.7
            };
        }

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
            DashedPolaroidFrameSettings settings = FromJob(job, safeLayout);
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

                    string overlayFile = FormatSequenceFilePath(overlayPattern, i + 1);
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
            string safeOutput = string.IsNullOrWhiteSpace(outputLabel) ? "dashpolaroidout" : outputLabel;
            string fps = Math.Clamp(frameRate, 1.0, 120.0).ToString("F3", inv);
            string duration = Math.Max(0.10, outputDurationSeconds).ToString("F3", inv);
            int safeLoopFrameCount = Math.Max(1, loopFrameCount);

            string baseChain = string.IsNullOrWhiteSpace(baseVideoFilters)
                ? $"{safeBaseInput}format=rgba[dashbase]"
                : $"{safeBaseInput}{baseVideoFilters},format=rgba[dashbase]";

            string overlayChain =
                $"{safeOverlayInput}format=rgba," +
                $"loop=loop=-1:size={safeLoopFrameCount}:start=0," +
                $"setpts=N/{fps}/TB," +
                $"trim=duration={duration}[dashframe]";

            string mergeChain = $"[dashbase][dashframe]overlay=shortest=0:eof_action=repeat:format=auto[{safeOutput}]";

            onLog($"[DASHED-POLAROID] Overlay graph ready: frames={safeLoopFrameCount}, fps={fps}, duration={duration}");
            return $"{baseChain};{overlayChain};{mergeChain}";
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

                double distanceToRunner = CircularDistance(i, runner, dashCount);
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
            Rect contentRect = layout.ContentRect;
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

        private static double CircularDistance(int index, double runner, int total)
        {
            if (total <= 0)
                return 0.0;

            double delta = Math.Abs(index - runner);
            return Math.Min(delta, total - delta);
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
}
