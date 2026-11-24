using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TitanEngine
{
    /// <summary>
    /// Lossless multi-threaded PNG sequence writer for WPF <see cref="RenderTargetBitmap"/> loops.
    /// Copies raw Pbgra32 pixels on the STA producer thread into pooled buffers, then runs WIC
    /// lossless PNG compression + disk I/O across worker threads with bounded memory concurrency.
    /// Pixel output is bit-for-bit identical to single-threaded <see cref="PngBitmapEncoder"/>.
    /// </summary>
    internal sealed class ParallelPngSequenceWriter : IDisposable
    {
        private readonly int _width;
        private readonly int _height;
        private readonly int _stride;
        private readonly int _byteCount;
        private readonly CancellationToken _token;
        private readonly SemaphoreSlim _slots;
        private readonly List<Task> _tasks = new List<Task>();
        private Exception? _firstError;
        private bool _disposed;

        private readonly HashSet<string> _ensuredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ParallelPngSequenceWriter(int width, int height, CancellationToken token)
        {
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            _stride = checked(_width * 4);
            _byteCount = checked(_stride * _height);
            _token = token;
            int maxInFlight = Math.Clamp(Environment.ProcessorCount, 2, 8);
            _slots = new SemaphoreSlim(maxInFlight, maxInFlight);
        }

        public void CaptureAndEnqueue(RenderTargetBitmap bitmap, string outputPath, string? previewCopyPath = null)
        {
            ThrowIfFaulted();
            _token.ThrowIfCancellationRequested();

            string? outDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outDir) && _ensuredDirs.Add(outDir))
                Directory.CreateDirectory(outDir);

            string? prevDir = string.IsNullOrEmpty(previewCopyPath) ? null : Path.GetDirectoryName(previewCopyPath);
            if (!string.IsNullOrEmpty(prevDir) && _ensuredDirs.Add(prevDir))
                Directory.CreateDirectory(prevDir);

            _slots.Wait(_token);
            byte[]? buffer = null;
            try
            {
                ThrowIfFaulted();
                _token.ThrowIfCancellationRequested();
                buffer = ArrayPool<byte>.Shared.Rent(_byteCount);
                bitmap.CopyPixels(buffer, _stride, 0);
            }
            catch
            {
                if (buffer != null)
                    ArrayPool<byte>.Shared.Return(buffer);
                _slots.Release();
                throw;
            }

            byte[] capturedBuffer = buffer;
            Task writeTask = Task.Run(() =>
            {
                try
                {
                    _token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref _firstError) != null)
                        return;

                    BitmapSource frameSource = BitmapSource.Create(
                        _width,
                        _height,
                        96.0,
                        96.0,
                        PixelFormats.Pbgra32,
                        null,
                        capturedBuffer,
                        _stride);

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(frameSource));
                    using (FileStream fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read, 65536))
                    {
                        encoder.Save(fs);
                    }

                    if (!string.IsNullOrEmpty(previewCopyPath))
                        File.Copy(outputPath, previewCopyPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref _firstError, ex, null);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(capturedBuffer);
                    _slots.Release();
                }
            });

            _tasks.Add(writeTask);
        }

        public void Complete()
        {
            if (_tasks.Count > 0)
                Task.WhenAll(_tasks).GetAwaiter().GetResult();

            ThrowIfFaulted();
            _token.ThrowIfCancellationRequested();
        }

        private void ThrowIfFaulted()
        {
            Exception? err = Volatile.Read(ref _firstError);
            if (err != null)
                ExceptionDispatchInfo.Capture(err).Throw();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            try
            {
                if (_tasks.Count > 0)
                    Task.WhenAll(_tasks).GetAwaiter().GetResult();
            }
            catch
            {
            }
            _slots.Dispose();
        }
    }

    internal static class ProceduralOverlayDiskCache
    {
        private sealed class OverlayCacheManifest
        {
            public string CacheKey { get; set; } = string.Empty;
            public int FrameCount { get; set; }
            public double FrameRate { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
        }

        public static string BuildHashKey(string material, int hexChars = 20)
        {
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(material ?? string.Empty));
            int len = Math.Clamp(hexChars, 8, digest.Length * 2);
            return Convert.ToHexString(digest)[..len].ToLowerInvariant();
        }

        public static string ResolveCacheRoot(string family, string cacheKey)
        {
            string safeFamily = Regex.Replace((family ?? "overlay").Trim(), @"[^\w\-]+", "_");
            return Path.Combine(Path.GetTempPath(), "TitanEngine", "overlay_cache", safeFamily, cacheKey);
        }

        public static bool TryLoad(
            string tempRoot,
            string overlayPattern,
            string cacheKey,
            out int frameCount,
            out double frameRate,
            out int width,
            out int height)
        {
            frameCount = 0;
            frameRate = 30.0;
            width = 0;
            height = 0;

            string manifestPath = Path.Combine(tempRoot, "manifest.json");
            if (!File.Exists(manifestPath))
                return false;

            try
            {
                string json = File.ReadAllText(manifestPath);
                OverlayCacheManifest? manifest = JsonSerializer.Deserialize<OverlayCacheManifest>(json);
                if (manifest == null || manifest.FrameCount <= 0 || manifest.Width <= 0 || manifest.Height <= 0)
                    return false;

                if (!string.Equals(manifest.CacheKey, cacheKey, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (!File.Exists(FormatSequencePath(overlayPattern, 1)))
                    return false;

                if (manifest.FrameCount > 1 && !File.Exists(FormatSequencePath(overlayPattern, manifest.FrameCount)))
                    return false;

                try { Directory.SetLastWriteTimeUtc(tempRoot, DateTime.UtcNow); } catch { }

                frameCount = manifest.FrameCount;
                frameRate = manifest.FrameRate;
                width = manifest.Width;
                height = manifest.Height;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void Commit(
            string tempRoot,
            string cacheKey,
            int frameCount,
            double frameRate,
            int width,
            int height,
            int maxEntriesPerFamily = 6)
        {
            Directory.CreateDirectory(tempRoot);
            var manifest = new OverlayCacheManifest
            {
                CacheKey = cacheKey,
                FrameCount = frameCount,
                FrameRate = frameRate,
                Width = width,
                Height = height
            };

            string manifestPath = Path.Combine(tempRoot, "manifest.json");
            string tmpManifestPath = manifestPath + ".tmp";
            string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmpManifestPath, json, new UTF8Encoding(false));
            File.Move(tmpManifestPath, manifestPath, overwrite: true);
            try { Directory.SetLastWriteTimeUtc(tempRoot, DateTime.UtcNow); } catch { }

            PruneSiblings(tempRoot, maxEntriesPerFamily);
        }

        public static void DiscardPartial(string? tempRoot)
        {
            if (string.IsNullOrWhiteSpace(tempRoot))
                return;

            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }

        private static void PruneSiblings(string liveRoot, int maxEntries)
        {
            try
            {
                string fullLive = Path.GetFullPath(liveRoot);
                string? parent = Path.GetDirectoryName(fullLive);
                if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                    return;

                int safeCap = Math.Max(1, maxEntries);
                var dirs = new List<DirectoryInfo>();
                foreach (string sub in Directory.GetDirectories(parent))
                {
                    try { dirs.Add(new DirectoryInfo(sub)); } catch { }
                }

                if (dirs.Count <= safeCap)
                    return;

                dirs.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                string liveName = Path.GetFileName(fullLive);
                int nonLiveBudget = Math.Max(0, safeCap - 1);
                int nonLiveKept = 0;
                foreach (DirectoryInfo dir in dirs)
                {
                    if (string.Equals(dir.Name, liveName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (nonLiveKept < nonLiveBudget)
                    {
                        nonLiveKept++;
                        continue;
                    }

                    try { dir.Delete(recursive: true); } catch { }
                }
            }
            catch
            {
            }
        }

        private static string FormatSequencePath(string ffmpegPattern, int frameNumber)
        {
            string csharpPattern = ffmpegPattern.Replace("%04d", "{0:D4}", StringComparison.Ordinal);
            if (string.Equals(csharpPattern, ffmpegPattern, StringComparison.Ordinal))
                csharpPattern = Regex.Replace(ffmpegPattern, @"%0?(\d+)d", m => "{0:D" + m.Groups[1].Value + "}");
            return string.Format(CultureInfo.InvariantCulture, csharpPattern, frameNumber);
        }
    }
}
