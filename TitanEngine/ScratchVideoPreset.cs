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
    public sealed class ScratchVideoSettings
    {
        public int ScratchCount { get; set; }
        public int MicroScratchCount { get; set; }
        public int DustCount { get; set; }
        public int DustClusterCount { get; set; }
        public double MinLength { get; set; }
        public double MaxLength { get; set; }
        public double MinThickness { get; set; }
        public double MaxThickness { get; set; }
        public double BaseOpacity { get; set; }
        public double GlowOpacity { get; set; }
        public double ChangeIntervalSeconds { get; set; }
        public double CycleSeconds { get; set; }
    }

    public sealed class ScratchVideoGeneratedOverlay
    {
        public string TempRoot { get; set; } = string.Empty;
        public string OverlayPattern { get; set; } = string.Empty;
        public int FrameCount { get; set; }
        public double FrameRate { get; set; }
        public string PreviewDirectory { get; set; } = string.Empty;
        public List<string> TempArtifacts { get; set; } = new List<string>();
    }

    public static class ScratchVideoPreset
    {
        public static ScratchVideoSettings FromJob(RenderJob job, OverlayContentLayout layout)
        {
            double intensity = Math.Clamp(job.FxIntensity / 100.0, 0.0, 1.0);
            double minDim = Math.Max(240.0, layout.ContentMinDimension);

            return new ScratchVideoSettings
            {
                ScratchCount = 3 + (int)Math.Round(4 * intensity),
                MicroScratchCount = 10 + (int)Math.Round(10 * intensity),
                DustCount = 120 + (int)Math.Round(110 * intensity),
                DustClusterCount = 20 + (int)Math.Round(16 * intensity),
                MinLength = Math.Clamp(minDim * 0.014, 6.0, 18.0),
                MaxLength = Math.Clamp(minDim * (0.026 + (0.010 * intensity)), 14.0, 38.0),
                MinThickness = 0.6,
                MaxThickness = 1.15 + (0.60 * intensity),
                BaseOpacity = 0.38 + (0.22 * intensity),
                GlowOpacity = 0.14 + (0.10 * intensity),
                ChangeIntervalSeconds = 0.5,
                CycleSeconds = 4.0
            };
        }

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
            ScratchVideoSettings settings = FromJob(job, safeLayout);
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

                    string overlayFile = FormatSequenceFilePath(overlayPattern, i + 1);
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
            string safeOutput = string.IsNullOrWhiteSpace(outputLabel) ? "scratchout" : outputLabel;
            string fps = Math.Clamp(frameRate, 1.0, 120.0).ToString("F3", inv);
            string duration = Math.Max(0.10, outputDurationSeconds).ToString("F3", inv);
            int safeLoopFrameCount = Math.Max(1, loopFrameCount);

            string baseChain = string.IsNullOrWhiteSpace(baseVideoFilters)
                ? $"{safeBaseInput}format=rgba[scratchbase]"
                : $"{safeBaseInput}{baseVideoFilters},format=rgba[scratchbase]";

            string overlayChain =
                $"{safeOverlayInput}format=rgba," +
                $"loop=loop=-1:size={safeLoopFrameCount}:start=0," +
                $"setpts=N/{fps}/TB," +
                $"trim=duration={duration}[scratchoverlay]";

            string mergeChain = $"[scratchbase][scratchoverlay]overlay=shortest=0:eof_action=repeat:format=auto[{safeOutput}]";

            onLog($"[SCRATCH-VIDEO] Overlay graph ready: frames={safeLoopFrameCount}, fps={fps}, duration={duration}");
            return $"{baseChain};{overlayChain};{mergeChain}";
        }

        private static void DrawScratchPattern(DrawingContext dc, OverlayContentLayout layout, ScratchVideoSettings settings, int patternIndex, double localProgress)
        {
            Rect contentRect = layout.ContentRect;
            Random rng = new Random(HashSeed(layout.CanvasWidth, layout.CanvasHeight, patternIndex));
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
                ? Lerp(settings.MinLength * 0.45, settings.MaxLength * 0.40, rng.NextDouble())
                : Lerp(settings.MinLength, settings.MaxLength, rng.NextDouble());
            double angle = (rng.NextDouble() * Math.PI * 2.0) + ((rng.NextDouble() - 0.5) * (micro ? 0.18 : 0.10));
            double dx = Math.Cos(angle) * length * 0.5;
            double dy = Math.Sin(angle) * length * 0.5;
            double jitter = micro ? 0.6 : 1.2;
            double jitterX = Math.Sin((localProgress * Math.PI * 2.0) + rng.NextDouble()) * jitter;
            double jitterY = Math.Cos((localProgress * Math.PI * 2.0) + rng.NextDouble()) * jitter;

            Point start = new Point(x - dx + jitterX, y - dy + jitterY);
            Point end = new Point(x + dx + jitterX, y + dy + jitterY);
            double thickness = Lerp(settings.MinThickness, settings.MaxThickness, rng.NextDouble()) * (micro ? 0.65 : 1.0);
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

        private static int HashSeed(int width, int height, int patternIndex)
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + width;
                hash = (hash * 31) + height;
                hash = (hash * 31) + patternIndex;
                hash = (hash * 31) + 8731;
                return hash;
            }
        }

        private static double Lerp(double a, double b, double t)
            => a + ((b - a) * Math.Clamp(t, 0.0, 1.0));

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
                Name = "TitanEngine_ScratchVideo_STA"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return tcs.Task;
        }
    }
}
