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
    public sealed class PolaroidScrapbook2Settings
    {
        public double Inset { get; set; }
        public double CornerRadius { get; set; }
        public double StrokeThickness { get; set; }
        public double ShadowThickness { get; set; }
        public double ShadowOffset { get; set; }
        public double FrameOpacity { get; set; }
        public double ShadowOpacity { get; set; }
        public double TapeOpacity { get; set; }
        public double StickyOpacity { get; set; }
        public double TapeWidth { get; set; }
        public double TapeHeight { get; set; }
        public double StickyWidth { get; set; }
        public double StickyHeight { get; set; }
    }

    public sealed class PolaroidScrapbook2GeneratedOverlay
    {
        public string TempRoot { get; set; } = string.Empty;
        public string OverlayPattern { get; set; } = string.Empty;
        public int FrameCount { get; set; }
        public double FrameRate { get; set; }
        public string PreviewDirectory { get; set; } = string.Empty;
        public List<string> TempArtifacts { get; set; } = new List<string>();
    }

    public static class PolaroidScrapbook2Preset
    {
        public static PolaroidScrapbook2Settings FromJob(RenderJob job, OverlayContentLayout layout)
        {
            double intensity = Math.Clamp(job.FxIntensity / 100.0, 0.0, 1.0);
            double minDim = Math.Max(240.0, layout.ContentMinDimension);
            double frameThickness = OverlayLayoutHelper.ScaleFromHdReference(layout, 6.0, 2.0, 16.0);
            double shadowThickness = OverlayLayoutHelper.ScaleFromHdReference(layout, 4.0, 1.0, 12.0);
            double shadowOffset = OverlayLayoutHelper.ScaleFromHdReference(layout, 3.0, 1.0, 8.0);
            return new PolaroidScrapbook2Settings
            {
                Inset = Math.Clamp(minDim * 0.034, 28.0, 42.0),
                CornerRadius = Math.Clamp(minDim * 0.038, 22.0, 48.0),
                StrokeThickness = frameThickness,
                ShadowThickness = shadowThickness,
                ShadowOffset = shadowOffset,
                FrameOpacity = 0.94,
                ShadowOpacity = 0.14 + (0.04 * intensity),
                TapeOpacity = 0.56 + (0.08 * intensity),
                StickyOpacity = 0.46 + (0.07 * intensity),
                TapeWidth = Math.Clamp(minDim * 0.19, 56.0, 120.0),
                TapeHeight = Math.Clamp(minDim * 0.032, 18.0, 28.0),
                StickyWidth = Math.Clamp(minDim * 0.120, 46.0, 82.0),
                StickyHeight = Math.Clamp(minDim * 0.048, 22.0, 34.0)
            };
        }

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
            var settings = FromJob(job, safeLayout);

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

                string outputFile = FormatSequenceFilePath(overlayPattern, 1);
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
            string safeOutput = string.IsNullOrWhiteSpace(outputLabel) ? "polaroid2out" : outputLabel;
            string fps = Math.Clamp(frameRate, 1.0, 120.0).ToString("F3", inv);
            string duration = Math.Max(0.10, outputDurationSeconds).ToString("F3", inv);
            int safeLoopFrameCount = Math.Max(1, loopFrameCount);

            string baseChain = string.IsNullOrWhiteSpace(baseVideoFilters)
                ? $"{safeBaseInput}format=rgba[polaroid2base]"
                : $"{safeBaseInput}{baseVideoFilters},format=rgba[polaroid2base]";

            string overlayChain =
                $"{safeOverlayInput}format=rgba," +
                $"loop=loop=-1:size={safeLoopFrameCount}:start=0," +
                $"setpts=N/{fps}/TB," +
                $"trim=duration={duration}[polaroid2frame]";

            string mergeChain = $"[polaroid2base][polaroid2frame]overlay=shortest=0:eof_action=repeat:format=auto[{safeOutput}]";

            onLog($"[POLAROID-2] Overlay graph ready: frames={safeLoopFrameCount}, fps={fps}, duration={duration}");
            return $"{baseChain};{overlayChain};{mergeChain}";
        }

        private static void DrawOverlay(DrawingContext dc, OverlayContentLayout layout, PolaroidScrapbook2Settings settings)
        {
            Rect contentRect = layout.ContentRect;
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

        private static string FormatSequenceFilePath(string pattern, int index)
        {
            return Regex.Replace(pattern, @"%0(\d+)d", m =>
            {
                int digits = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                return index.ToString("D" + digits, CultureInfo.InvariantCulture);
            });
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
}
