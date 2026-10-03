using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TitanEngine
{
    public enum OverlayBackgroundType
    {
        Alpha,
        GreenScreen,
        BlackBackground,
        Unknown
    }

    public sealed class OverlayAssetBackgroundAnalysis
    {
        public string FilePath { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public DateTime LastModifiedTimeUtc { get; set; }
        public VideoAssetProbeInfo ProbeInfo { get; set; } = new VideoAssetProbeInfo();
        public bool HasAlpha { get; set; }
        public double GreenCoverageFullFrame { get; set; }
        public double GreenCoverageBorder { get; set; }
        public double DarkCoverageFullFrame { get; set; }
        public double DarkCoverageBorder { get; set; }
        public OverlayBackgroundType BackgroundType { get; set; } = OverlayBackgroundType.Unknown;
        public string PipelineName { get; set; } = "blend_screen_low_opacity";
        public double OpacityOverride { get; set; } = -1.0;
        public string KeyColor { get; set; } = "0x00FF00";
        public double Similarity { get; set; } = 0.18;
        public double Blend { get; set; } = 0.08;
        public string Warning { get; set; } = string.Empty;
    }

    public static class OverlayAssetBackgroundAnalyzer
    {
        private sealed class CoverageAccumulator
        {
            public double GreenFull;
            public double GreenBorder;
            public double DarkFull;
            public double DarkBorder;
            public int Samples;
        }

        private static readonly ConcurrentDictionary<string, OverlayAssetBackgroundAnalysis> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> AlphaPixelFormats = new(StringComparer.OrdinalIgnoreCase)
        {
            "rgba", "bgra", "yuva420p", "gbrap", "argb", "abgr", "yuva422p", "yuva444p"
        };

        public static async Task<OverlayAssetBackgroundAnalysis> AnalyzeAsync(
            string assetPath,
            CancellationToken token)
        {
            string normalizedPath = Path.GetFullPath(assetPath.Trim());
            FileInfo fileInfo = new FileInfo(normalizedPath);
            string cacheKey = BuildCacheKey(normalizedPath, fileInfo.Exists ? fileInfo.Length : 0L, fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTime.MinValue);
            if (Cache.TryGetValue(cacheKey, out OverlayAssetBackgroundAnalysis? cached))
                return cached;

            VideoAssetProbeInfo probe = await EngineCore.ProbeVideoAssetAsync(normalizedPath, token);
            var analysis = new OverlayAssetBackgroundAnalysis
            {
                FilePath = normalizedPath,
                FileSize = fileInfo.Exists ? fileInfo.Length : 0L,
                LastModifiedTimeUtc = fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTime.MinValue,
                ProbeInfo = probe,
                Similarity = 0.18,
                Blend = 0.08
            };

            if (!probe.ProbeSucceeded || !probe.HasVideoStream)
            {
                analysis.BackgroundType = OverlayBackgroundType.Unknown;
                analysis.PipelineName = "blend_screen_low_opacity";
                analysis.OpacityOverride = 0.10;
                analysis.Warning = "Overlay background type unknown, defaulting to black-background screen blend with low opacity.";
                Cache[cacheKey] = analysis;
                return analysis;
            }

            analysis.HasAlpha = HasAlphaPixelFormat(probe.PixelFormat);
            if (analysis.HasAlpha)
            {
                analysis.BackgroundType = OverlayBackgroundType.Alpha;
                analysis.PipelineName = "alpha_overlay";
                Cache[cacheKey] = analysis;
                return analysis;
            }

            CoverageAccumulator coverage = await SampleCoverageAsync(normalizedPath, probe.Duration, token);
            if (coverage.Samples > 0)
            {
                analysis.GreenCoverageFullFrame = coverage.GreenFull / coverage.Samples;
                analysis.GreenCoverageBorder = coverage.GreenBorder / coverage.Samples;
                analysis.DarkCoverageFullFrame = coverage.DarkFull / coverage.Samples;
                analysis.DarkCoverageBorder = coverage.DarkBorder / coverage.Samples;
            }

            if (analysis.GreenCoverageBorder > 0.45 || analysis.GreenCoverageFullFrame > 0.30)
            {
                analysis.BackgroundType = OverlayBackgroundType.GreenScreen;
                analysis.PipelineName = "chromakey_overlay";
            }
            else if (analysis.DarkCoverageBorder > 0.55 || analysis.DarkCoverageFullFrame > 0.40)
            {
                analysis.BackgroundType = OverlayBackgroundType.BlackBackground;
                analysis.PipelineName = "blend_screen";
            }
            else
            {
                analysis.BackgroundType = OverlayBackgroundType.Unknown;
                analysis.PipelineName = "blend_screen_low_opacity";
                analysis.OpacityOverride = 0.10;
                analysis.Warning = "Overlay background type unknown, defaulting to black-background screen blend with low opacity.";
            }

            Cache[cacheKey] = analysis;
            return analysis;
        }

        private static async Task<CoverageAccumulator> SampleCoverageAsync(
            string assetPath,
            double durationSeconds,
            CancellationToken token)
        {
            var accumulator = new CoverageAccumulator();
            string tempRoot = Path.Combine(Path.GetTempPath(), "TitanEngine", "overlay_detect");
            Directory.CreateDirectory(tempRoot);

            foreach (double time in BuildSampleTimes(durationSeconds))
            {
                token.ThrowIfCancellationRequested();
                string tempFrame = Path.Combine(tempRoot, $"frame_{Guid.NewGuid():N}.jpg");
                try
                {
                    bool extracted = await EngineCore.ExtractFrameToImageAsync(assetPath, time, tempFrame, token);
                    if (!extracted || !File.Exists(tempFrame))
                        continue;

                    AnalyzeFrame(tempFrame, accumulator);
                }
                finally
                {
                    try { File.Delete(tempFrame); } catch { }
                }
            }

            return accumulator;
        }

        private static IEnumerable<double> BuildSampleTimes(double durationSeconds)
        {
            double safeDuration = Math.Max(0.0, durationSeconds);
            double endCap = Math.Max(0.0, safeDuration - 0.05);
            var times = new List<double> { Math.Min(0.3, endCap) };
            if (safeDuration > 0.40)
            {
                times.Add(Math.Min(endCap, safeDuration * 0.25));
                times.Add(Math.Min(endCap, safeDuration * 0.50));
                times.Add(Math.Min(endCap, safeDuration * 0.75));
            }
            if (safeDuration > 1.0)
                times.Add(Math.Min(endCap, Math.Max(0.0, safeDuration - 0.20)));

            return times
                .Where(t => t >= 0.0)
                .Distinct()
                .OrderBy(t => t)
                .Take(5);
        }

        private static void AnalyzeFrame(string framePath, CoverageAccumulator accumulator)
        {
            using FileStream stream = new FileStream(framePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource source = decoder.Frames[0];
            BitmapSource scaled = new TransformedBitmap(source, new ScaleTransform(
                160.0 / Math.Max(1.0, source.PixelWidth),
                90.0 / Math.Max(1.0, source.PixelHeight)));
            BitmapSource rgba = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0.0);

            int width = rgba.PixelWidth;
            int height = rgba.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            rgba.CopyPixels(pixels, stride, 0);

            int borderX = Math.Max(1, (int)Math.Round(width * 0.10));
            int borderY = Math.Max(1, (int)Math.Round(height * 0.10));
            double greenFull = 0.0;
            double darkFull = 0.0;
            double greenBorder = 0.0;
            double darkBorder = 0.0;
            int fullCount = 0;
            int borderCount = 0;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = (y * stride) + (x * 4);
                    double b = pixels[idx];
                    double g = pixels[idx + 1];
                    double r = pixels[idx + 2];
                    double brightness = (r + g + b) / 3.0;
                    bool isDark = brightness < 35.0;
                    bool isGreen = g > 110.0 && g > (r * 1.35) && g > (b * 1.35);
                    bool isBorder = y < borderY || y >= height - borderY || x < borderX || x >= width - borderX;

                    fullCount++;
                    if (isGreen) greenFull++;
                    if (isDark) darkFull++;

                    if (isBorder)
                    {
                        borderCount++;
                        if (isGreen) greenBorder++;
                        if (isDark) darkBorder++;
                    }
                }
            }

            if (fullCount > 0)
            {
                accumulator.GreenFull += greenFull / fullCount;
                accumulator.DarkFull += darkFull / fullCount;
            }

            if (borderCount > 0)
            {
                accumulator.GreenBorder += greenBorder / borderCount;
                accumulator.DarkBorder += darkBorder / borderCount;
            }

            accumulator.Samples++;
        }

        private static bool HasAlphaPixelFormat(string? pixelFormat)
        {
            string value = (pixelFormat ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return AlphaPixelFormats.Contains(value) ||
                   value.StartsWith("yuva", StringComparison.OrdinalIgnoreCase) ||
                   value.EndsWith("a", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildCacheKey(string filePath, long fileSize, DateTime lastModifiedTimeUtc)
        {
            return $"{filePath}|{fileSize}|{lastModifiedTimeUtc.Ticks}";
        }
    }
}
