using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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


        internal static string FormatSequenceFilePath(string pattern, int index)
        {
            return Regex.Replace(pattern, @"%0(\d+)d", m =>
            {
                int digits = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                return index.ToString("D" + digits, CultureInfo.InvariantCulture);
            });
        }
    }
}
