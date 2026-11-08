using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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


        internal static double CircularDistance(int index, double runner, int total)
        {
            if (total <= 0)
                return 0.0;

            double delta = Math.Abs(index - runner);
            return Math.Min(delta, total - delta);
        }


        internal static string FormatSequenceFilePath(string ffmpegPattern, int frameNumber)
        {
            if (string.IsNullOrWhiteSpace(ffmpegPattern))
                throw new ArgumentException("Sequence pattern cannot be empty.", nameof(ffmpegPattern));

            string csharpPattern = ffmpegPattern.Replace("%04d", "{0:D4}", StringComparison.Ordinal);
            if (string.Equals(csharpPattern, ffmpegPattern, StringComparison.Ordinal))
                csharpPattern = Regex.Replace(ffmpegPattern, @"%0?(\d+)d", m => "{0:D" + m.Groups[1].Value + "}");

            return string.Format(CultureInfo.InvariantCulture, csharpPattern, frameNumber);
        }
    }
}
