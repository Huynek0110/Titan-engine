using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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
