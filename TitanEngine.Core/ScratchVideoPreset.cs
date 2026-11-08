using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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


        internal static int HashSeed(int width, int height, int patternIndex)
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


        internal static double Lerp(double a, double b, double t)
            => a + ((b - a) * Math.Clamp(t, 0.0, 1.0));


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
