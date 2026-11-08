using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TitanEngine
{
    public sealed class ParticleOverlaySequence
    {
        public string CacheKey { get; set; } = string.Empty;
        public string TempRoot { get; set; } = string.Empty;
        public string OverlayPattern { get; set; } = string.Empty;
        public int FrameCount { get; set; }
        public double FrameRate { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string PreviewDirectory { get; set; } = string.Empty;
        public bool FromCache { get; set; }
        public List<string> TempArtifacts { get; set; } = new List<string>();
    }

    public static class ParticleOverlayGenerator
    {

        internal static bool TryLoadCache(
            string manifestPath,
            string overlayPattern,
            string previewDir,
            out ParticleOverlaySequence? result)
        {
            result = null;
            if (!File.Exists(manifestPath))
                return false;

            try
            {
                string json = File.ReadAllText(manifestPath);
                var cached = JsonSerializer.Deserialize<ParticleOverlaySequence>(json);
                if (cached == null || cached.FrameCount <= 0 || cached.Width <= 0 || cached.Height <= 0)
                    return false;

                string firstFrame = FormatSequenceFilePath(overlayPattern, 1);
                if (!File.Exists(firstFrame))
                    return false;

                cached.TempRoot = Path.GetDirectoryName(manifestPath)!;
                cached.OverlayPattern = overlayPattern;
                cached.PreviewDirectory = previewDir;
                cached.TempArtifacts = new List<string>();
                result = cached;
                return true;
            }
            catch
            {
                return false;
            }
        }


        internal static void WriteManifest(string manifestPath, ParticleOverlaySequence result)
        {
            var payload = new ParticleOverlaySequence
            {
                CacheKey = result.CacheKey,
                TempRoot = result.TempRoot,
                OverlayPattern = result.OverlayPattern,
                FrameCount = result.FrameCount,
                FrameRate = result.FrameRate,
                Width = result.Width,
                Height = result.Height,
                PreviewDirectory = result.PreviewDirectory
            };

            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(manifestPath, json, Encoding.UTF8);
        }


        internal static double ResolveCycleSeconds(ParticleEffectRecipe recipe, double outputDurationSeconds)
        {
            double fallback = recipe.ParticleType == ParticleType.Confetti ? 2.2 : 4.0;
            if (recipe.Parameters.TryGetValue("cycleSeconds", out string? raw) &&
                double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                fallback = parsed;
            }

            if (recipe.ParticleType == ParticleType.Confetti)
                return Math.Clamp(fallback, 1.6, 3.2);

            if (outputDurationSeconds > 0.10)
                fallback = Math.Min(fallback, Math.Max(2.0, outputDurationSeconds));

            return Math.Clamp(fallback, 2.0, 6.0);
        }


        internal static int DeriveSeed(string recipeId, string cacheKey)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(recipeId + "|" + cacheKey));
            return BitConverter.ToInt32(hash, 0) & int.MaxValue;
        }


        internal static double Lerp(EffectRange range, double t)
            => range.Min + ((range.Max - range.Min) * Math.Clamp(t, 0.0, 1.0));


        internal static double Wrap(double value, double min, double max)
        {
            double span = max - min;
            if (span <= 0.0001)
                return min;

            double wrapped = value;
            while (wrapped < min)
                wrapped += span;
            while (wrapped > max)
                wrapped -= span;
            return wrapped;
        }


        internal static int EnsureEven(int value)
        {
            int safe = Math.Max(2, value);
            return (safe & 1) == 0 ? safe : safe + 1;
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
