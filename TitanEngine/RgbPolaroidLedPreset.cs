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
    public sealed class RgbPolaroidLedSettings
    {
        public double BorderThickness { get; set; }
        public double GlowSize { get; set; }
        public double GlowOpacity { get; set; }
        public double CoreOpacity { get; set; }
        public double AnimationSpeed { get; set; }
        public double HueSpeed { get; set; }
        public int PulseCount { get; set; }
        public double PulseWidth { get; set; }
        public double PulseIntensity { get; set; }
        public double CornerRadius { get; set; }
        public double Inset { get; set; }
        public double CycleSeconds { get; set; }
        public double SampleSpacing { get; set; }
    }

    public sealed class RgbPolaroidLedGeneratedOverlay
    {
        public string TempRoot { get; set; } = string.Empty;
        public string OverlayPattern { get; set; } = string.Empty;
        public int FrameCount { get; set; }
        public double FrameRate { get; set; }
        public double CycleSeconds { get; set; }
        public string PreviewDirectory { get; set; } = string.Empty;
        public List<string> TempArtifacts { get; set; } = new List<string>();
    }

    public static class RgbPolaroidLedPreset
    {
        public static RgbPolaroidLedSettings FromJob(RenderJob job, OverlayContentLayout layout)
        {
            double intensity = Math.Clamp(job.FxIntensity / 100.0, 0.0, 1.0);
            double minDim = Math.Max(240.0, layout.ContentMinDimension);
            double borderThickness = OverlayLayoutHelper.ScaleFromHdReference(layout, 2.5, 1.0, 8.0);
            double glowSize = OverlayLayoutHelper.ScaleFromHdReference(layout, 5.0, 2.0, 18.0);

            return new RgbPolaroidLedSettings
            {
                BorderThickness = borderThickness,
                GlowSize = glowSize,
                GlowOpacity = 0.18 + (0.07 * intensity),
                CoreOpacity = 0.74 + (0.10 * intensity),
                AnimationSpeed = 0.48 + (0.14 * intensity),
                HueSpeed = 0.88 + (0.22 * intensity),
                PulseCount = 0,
                PulseWidth = 0.0,
                PulseIntensity = 0.0,
                CornerRadius = Math.Clamp(minDim * 0.026, 14.0, 34.0),
                Inset = Math.Clamp(minDim * 0.034, 28.0, 42.0),
                CycleSeconds = 3.35 - (0.35 * intensity),
                SampleSpacing = 1.35
            };
        }

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
            RgbPolaroidLedSettings settings = FromJob(job, safeLayout);
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

                    string overlayFile = FormatSequenceFilePath(overlayPattern, i + 1);
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
            string safeOutput = string.IsNullOrWhiteSpace(outputLabel) ? "rgbledout" : outputLabel;
            string fps = Math.Clamp(frameRate, 1.0, 120.0).ToString("F3", inv);
            string duration = Math.Max(0.10, outputDurationSeconds).ToString("F3", inv);
            int safeLoopFrameCount = Math.Max(1, loopFrameCount);

            string baseChain = string.IsNullOrWhiteSpace(baseVideoFilters)
                ? $"{safeBaseInput}format=rgba[rgbbase]"
                : $"{safeBaseInput}{baseVideoFilters},format=rgba[rgbbase]";

            string overlayChain =
                $"{safeOverlayInput}format=rgba," +
                $"loop=loop=-1:size={safeLoopFrameCount}:start=0," +
                $"setpts=N/{fps}/TB," +
                $"trim=duration={duration}[rgbled]";

            string mergeChain = $"[rgbbase][rgbled]overlay=shortest=0:eof_action=repeat:format=auto[{safeOutput}]";

            onLog($"[RGB-POLAROID] LED overlay graph ready: frames={safeLoopFrameCount}, fps={fps}, duration={duration}");
            return $"{baseChain};{overlayChain};{mergeChain}";
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
            Rect contentRect = layout.ContentRect;
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
}
