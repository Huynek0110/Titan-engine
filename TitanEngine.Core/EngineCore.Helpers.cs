using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TitanEngine
{
    public static partial class EngineCore
    {


        private static async Task<bool> DownloadFileWithProgressAsync(string url, string destinationPath, Action<string> onLog, CancellationToken token)
        {
            try
            {
                string? parentDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(parentDir))
                    Directory.CreateDirectory(parentDir);

                using var http = new HttpClient
                {
                    Timeout = TimeSpan.FromMinutes(10)
                };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("TitanEngine/106.0");

                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                if (!resp.IsSuccessStatusCode)
                {
                    onLog($"[DLSS-NGX] HTTP {(int)resp.StatusCode} while downloading package.");
                    return false;
                }

                long? total = resp.Content.Headers.ContentLength;
                await using var inStream = await resp.Content.ReadAsStreamAsync(token);
                await using var outStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                byte[] buffer = new byte[81920];
                long downloaded = 0;
                int lastLoggedPercent = -1;

                while (true)
                {
                    int read = await inStream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                    if (read <= 0) break;

                    await outStream.WriteAsync(buffer.AsMemory(0, read), token);
                    downloaded += read;

                    if (total.HasValue && total.Value > 0)
                    {
                        int percent = (int)Math.Floor((downloaded * 100.0) / total.Value);
                        if (percent >= lastLoggedPercent + 10)
                        {
                            lastLoggedPercent = percent;
                            onLog($"[DLSS-NGX] Download progress: {percent}%");
                        }
                    }
                }

                if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
                {
                    onLog($"[DLSS-NGX] Downloaded {(new FileInfo(destinationPath).Length / (1024.0 * 1024.0)):F1} MB");
                    return true;
                }

                onLog("[DLSS-NGX] Download finished but file is empty.");
                return false;
            }
            catch (OperationCanceledException)
            {
                onLog("[DLSS-NGX] Download canceled.");
                return false;
            }
            catch (Exception ex)
            {
                onLog($"[DLSS-NGX] Download error: {ex.Message}");
                return false;
            }
        }

        private static string? ExtractVfxSamplesZip(string zipPath, string dlssDir, Action<string> onLog)
        {
            try
            {
                if (!File.Exists(zipPath))
                    return null;

                string? existing = ResolveVfxSamplesSourceRoot(dlssDir);
                if (!string.IsNullOrWhiteSpace(existing))
                    return existing;

                onLog("[DLSS-NGX] Extracting source bundle...");
                ZipFile.ExtractToDirectory(zipPath, dlssDir, true);
                return ResolveVfxSamplesSourceRoot(dlssDir);
            }
            catch (Exception ex)
            {
                onLog($"[DLSS-NGX] Extract failed: {ex.Message}");
                return null;
            }
        }

        private static string? FindExecutableOnPath(string fileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileName))
                    return null;

                string? pathValue = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrWhiteSpace(pathValue))
                    return null;

                foreach (string rawPart in pathValue.Split(Path.PathSeparator))
                {
                    string part = rawPart?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(part))
                        continue;

                    string candidate = Path.Combine(part, fileName);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch { }

            return null;
        }

        private static bool LooksLikeVfxSdkRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return false;

            string nvfxDll = Path.Combine(root, "bin", "NVVideoEffects.dll");
            string includeDir = Path.Combine(root, "include");
            string featuresDir = Path.Combine(root, "features");
            return File.Exists(nvfxDll) && Directory.Exists(includeDir) && Directory.Exists(featuresDir);
        }

        private static async Task<int> RunProcessWithLogAsync(string fileName, string arguments, string? workingDir, string tag, Action<string> onLog, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDir) ? null : workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    onLog($"[{tag}] {e.Data}");
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    onLog($"[{tag}-ERR] {e.Data}");
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(token);
            }
            catch (TaskCanceledException)
            {
                try { process.Kill(); } catch { }
                return -1;
            }

            return process.ExitCode;
        }

        public static async Task<(double Duration, long Bitrate)> AnalyzeMediaAsync(string filePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(_ffprobePath)) return (0.0, 5000000);

                    var durPsi = new ProcessStartInfo
                    {
                        FileName = _ffprobePath,
                        Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };
                    string durStr = ExecuteProcessAndRead(durPsi);
                    double.TryParse(durStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double duration);

                    var brPsi = new ProcessStartInfo
                    {
                        FileName = _ffprobePath,
                        Arguments = $"-v error -select_streams v:0 -show_entries stream=bit_rate -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };
                    string brStr = ExecuteProcessAndRead(brPsi);
                    if (!long.TryParse(brStr, out long bitrate) || bitrate == 0)
                    {
                        if (duration > 0 && File.Exists(filePath))
                        {
                            long size = new FileInfo(filePath).Length;
                            bitrate = (long)((size * 8) / duration);
                        }
                        else bitrate = 5000000;
                    }
                    return (duration, bitrate);
                }
                catch { return (0.0, 5000000); }
            });
        }

        public static async Task<(double Duration, long Bitrate, int Width, int Height, double Fps)> AnalyzeMediaDetailsAsync(string filePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var imageResolution = TryGetStillImageResolution(filePath);
                    if (imageResolution.Width > 0 && imageResolution.Height > 0)
                    {
                        return (0.0, 5000000, imageResolution.Width, imageResolution.Height, 30.0);
                    }

                    if (!File.Exists(_ffprobePath))
                        return (0.0, 5000000, 0, 0, 0.0);

                    var psi = new ProcessStartInfo
                    {
                        FileName = _ffprobePath,
                        Arguments = $"-v error -select_streams v:0 -show_entries stream=width,height,avg_frame_rate,r_frame_rate,bit_rate -show_entries format=duration,bit_rate -of json \"{filePath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };

                    string json = ExecuteProcessAndRead(psi);
                    using JsonDocument doc = JsonDocument.Parse(json);

                    double duration = 0.0;
                    long bitrate = 0;
                    int width = 0;
                    int height = 0;
                    double fps = 0.0;

                    if (doc.RootElement.TryGetProperty("format", out JsonElement format))
                    {
                        if (format.TryGetProperty("duration", out JsonElement durationEl))
                        {
                            double.TryParse(durationEl.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out duration);
                        }

                        if (format.TryGetProperty("bit_rate", out JsonElement formatBitrateEl))
                        {
                            long.TryParse(formatBitrateEl.GetString(), out bitrate);
                        }
                    }

                    if (doc.RootElement.TryGetProperty("streams", out JsonElement streams) &&
                        streams.ValueKind == JsonValueKind.Array &&
                        streams.GetArrayLength() > 0)
                    {
                        JsonElement stream = streams[0];

                        if (stream.TryGetProperty("width", out JsonElement widthEl))
                            width = widthEl.GetInt32();

                        if (stream.TryGetProperty("height", out JsonElement heightEl))
                            height = heightEl.GetInt32();

                        if (stream.TryGetProperty("bit_rate", out JsonElement streamBitrateEl) &&
                            long.TryParse(streamBitrateEl.GetString(), out long streamBitrate) &&
                            streamBitrate > 0)
                        {
                            bitrate = streamBitrate;
                        }

                        string fpsText = string.Empty;
                        if (stream.TryGetProperty("avg_frame_rate", out JsonElement avgFpsEl))
                            fpsText = avgFpsEl.GetString() ?? string.Empty;

                        if ((string.IsNullOrWhiteSpace(fpsText) || fpsText == "0/0") &&
                            stream.TryGetProperty("r_frame_rate", out JsonElement rFpsEl))
                        {
                            fpsText = rFpsEl.GetString() ?? string.Empty;
                        }

                        fps = ParseFpsValue(fpsText);
                    }

                    if (bitrate <= 0)
                    {
                        if (duration > 0 && File.Exists(filePath))
                        {
                            long size = new FileInfo(filePath).Length;
                            bitrate = (long)((size * 8) / duration);
                        }
                        else
                        {
                            bitrate = 5000000;
                        }
                    }

                    return (duration, bitrate, width, height, fps);
                }
                catch
                {
                    return (0.0, 5000000, 0, 0, 0.0);
                }
            });
        }


        // Phase 2: BitmapDecoder (TryGetStillImageResolution) moved sang WPF qua IImageServices;
        // headless (Images == null) trả (0, 0) như nhánh catch gốc.
        private static (int Width, int Height) TryGetStillImageResolution(string filePath)
            => EngineServices.Images?.TryGetImageDimensions(filePath) ?? (0, 0);

        private static (double FadeIn, double FadeOut, double FadeOutStart) NormalizeFadeSettings(RenderJob job, double estimatedOutputDuration, Action<string> onLog)
        {
            if (!job.EnableFade)
                return (0.0, 0.0, 0.0);

            double fadeIn = Math.Clamp(job.FadeInSeconds, 0.0, 60.0);
            double fadeOut = Math.Clamp(job.FadeOutSeconds, 0.0, 60.0);

            if (fadeIn <= 0.001 && fadeOut <= 0.001)
                return (0.0, 0.0, 0.0);

            if (estimatedOutputDuration <= 0.05)
            {
                fadeOut = 0.0;
                onLog("[FILTER-FADE-WARN] Output duration unknown/too short. Fade-out disabled.");
                return (fadeIn, 0.0, 0.0);
            }

            double maxTotalFade = Math.Max(0.0, estimatedOutputDuration - 0.05);
            double totalFade = fadeIn + fadeOut;
            if (totalFade > maxTotalFade && totalFade > 0.001)
            {
                double scale = maxTotalFade / totalFade;
                fadeIn *= scale;
                fadeOut *= scale;
                onLog($"[FILTER-FADE-WARN] Fade duration clamped to fit output ({fadeIn:F2}s/{fadeOut:F2}s).");
            }

            double fadeOutStart = fadeOut > 0.001 ? Math.Max(0.0, estimatedOutputDuration - fadeOut) : 0.0;
            return (fadeIn, fadeOut, fadeOutStart);
        }

        private static (int CanvasWidth, int CanvasHeight, PixelRect FrameRect) ResolveBrandLogoFrameReference(RenderJob job)
        {
            int requestedWidth = job.TargetWidth > 0 ? job.TargetWidth : job.SourceWidth;
            int requestedHeight = job.TargetHeight > 0 ? job.TargetHeight : job.SourceHeight;
            var overlayProfile = ResolveOverlayCanvasProfile(job, requestedWidth, requestedHeight);
            OverlayContentLayout layout = overlayProfile.Layout;

            double minDim = Math.Max(180.0, Math.Min(layout.ContentWidth, layout.ContentHeight));
            double inset = Math.Clamp(minDim * 0.034, 26.0, 42.0);
            PixelRect frameRect = new PixelRect(
                layout.ContentX + inset,
                layout.ContentY + inset,
                Math.Max(24.0, layout.ContentWidth - (inset * 2.0)),
                Math.Max(24.0, layout.ContentHeight - (inset * 2.0)));

            return (overlayProfile.CanvasWidth, overlayProfile.CanvasHeight, frameRect);
        }

        public static (int Width, int Height) CoerceResolutionToPortraitNineBySixteen(int width, int height)
        {
            int safeWidth = Math.Max(2, width);
            int safeHeight = Math.Max(2, height);

            int outputHeight = Math.Max(safeWidth, safeHeight);
            int outputWidth = Math.Max(2, (int)Math.Round(outputHeight * 9.0 / 16.0));

            if ((outputWidth & 1) != 0) outputWidth += 1;
            if ((outputHeight & 1) != 0) outputHeight += 1;

            return (outputWidth, outputHeight);
        }

        public static (double X, double Y, double W, double H) BuildCenteredNineBySixteenZoomCrop(
            int sourceWidth,
            int sourceHeight,
            double cropZoomPercent)
        {
            double safeSourceWidth = Math.Max(2.0, sourceWidth);
            double safeSourceHeight = Math.Max(2.0, sourceHeight);
            const double targetRatio = 9.0 / 16.0;

            double baseCropWidth;
            double baseCropHeight;
            double sourceRatio = safeSourceWidth / safeSourceHeight;

            if (sourceRatio > targetRatio)
            {
                baseCropHeight = safeSourceHeight;
                baseCropWidth = baseCropHeight * targetRatio;
            }
            else
            {
                baseCropWidth = safeSourceWidth;
                baseCropHeight = baseCropWidth / targetRatio;
            }

            double zoom = NormalizeCropZoomPercent(cropZoomPercent);
            double retainFactor = Math.Clamp(1.0 - (zoom / 100.0), 0.10, 1.0);
            double cropWidth = Math.Max(2.0, baseCropWidth * retainFactor);
            double cropHeight = Math.Max(2.0, baseCropHeight * retainFactor);

            double x = (safeSourceWidth - cropWidth) / 2.0;
            double y = (safeSourceHeight - cropHeight) / 2.0;

            return (
                Math.Clamp(x / safeSourceWidth, 0.0, 0.98),
                Math.Clamp(y / safeSourceHeight, 0.0, 0.98),
                Math.Clamp(cropWidth / safeSourceWidth, 0.02, 1.0),
                Math.Clamp(cropHeight / safeSourceHeight, 0.02, 1.0));
        }

        public static (double X, double Y, double W, double H) ApplyCropAspectRatio(
            double x,
            double y,
            double w,
            double h,
            int sourceWidth,
            int sourceHeight,
            string? cropAspectRatio)
        {
            x = Math.Clamp(x, 0.0, 0.98);
            y = Math.Clamp(y, 0.0, 0.98);
            w = Math.Clamp(w, 0.02, 1.0);
            h = Math.Clamp(h, 0.02, 1.0);

            if (!TryGetCropAspectRatioValue(cropAspectRatio, out double targetRatio))
            {
                if (x + w > 1.0) w = Math.Max(0.02, 1.0 - x);
                if (y + h > 1.0) h = Math.Max(0.02, 1.0 - y);
                return (x, y, w, h);
            }

            double safeSourceWidth = Math.Max(2.0, sourceWidth);
            double safeSourceHeight = Math.Max(2.0, sourceHeight);
            double currentRatio = (w * safeSourceWidth) / Math.Max(0.0001, h * safeSourceHeight);
            double centerX = x + (w / 2.0);
            double centerY = y + (h / 2.0);

            double adjustedW = w;
            double adjustedH = h;

            if (currentRatio > targetRatio)
                adjustedW = (adjustedH * safeSourceHeight * targetRatio) / safeSourceWidth;
            else
                adjustedH = (adjustedW * safeSourceWidth) / (targetRatio * safeSourceHeight);

            adjustedW = Math.Clamp(adjustedW, 0.02, 1.0);
            adjustedH = Math.Clamp(adjustedH, 0.02, 1.0);

            double newX = centerX - (adjustedW / 2.0);
            double newY = centerY - (adjustedH / 2.0);

            newX = Math.Clamp(newX, 0.0, Math.Max(0.0, 1.0 - adjustedW));
            newY = Math.Clamp(newY, 0.0, Math.Max(0.0, 1.0 - adjustedH));

            return (newX, newY, adjustedW, adjustedH);
        }

        private static (double RelativeWidth, double FrameLeft, double FrameTop, double FrameRight, double FrameBottom, double Padding, string Position) ResolveBrandLogoOverlayProfile(RenderJob job)
        {
            var reference = ResolveBrandLogoFrameReference(job);
            PixelRect frameRect = reference.FrameRect;
            const double referenceCanvasWidth = 1080.0;
            const double referenceLogoWidthPixels = 118.0;
            const double referencePaddingPixels = 8.0;
            double brandLogoAspectRatio = ResolveBrandLogoAspectRatio();

            bool isPortraitCanvas = reference.CanvasHeight > reference.CanvasWidth;
            bool isPortrait720Canvas =
                reference.CanvasWidth <= 720 &&
                reference.CanvasHeight <= 1280 &&
                isPortraitCanvas;

            double canvasScale = reference.CanvasWidth / referenceCanvasWidth;
            double logoWidthPixels = Math.Clamp(referenceLogoWidthPixels * canvasScale, 72.0, 118.0);
            double padding = Math.Clamp(referencePaddingPixels * canvasScale, 5.0, 8.0);

            if (isPortraitCanvas)
            {
                logoWidthPixels = Math.Clamp(logoWidthPixels * 0.68, 44.0, 82.0);
                padding = Math.Clamp(padding * 0.74, 3.0, 5.0);
            }

            if (isPortrait720Canvas)
            {
                logoWidthPixels = Math.Clamp(logoWidthPixels * 0.88, 42.0, 58.0);
                padding = Math.Clamp(padding * 0.88, 3.0, 4.2);
            }

            double maxWidthInsideFrame = Math.Max(20.0, frameRect.Width - (padding * 2.0));
            if (logoWidthPixels > maxWidthInsideFrame)
                logoWidthPixels = maxWidthInsideFrame;

            double maxHeightInsideFrame = Math.Max(20.0, frameRect.Height - (padding * 2.0));
            if ((logoWidthPixels / brandLogoAspectRatio) > maxHeightInsideFrame)
                logoWidthPixels = maxHeightInsideFrame * brandLogoAspectRatio;

            if (isPortraitCanvas)
            {
                // Portrait logos look visually larger because the bundled mark is tall and narrow.
                // Cap it using frame-relative sizing so Original resolution and 9:16 renders stay subtle.
                double portraitWidthCap = Math.Clamp(frameRect.Width * 0.050, 30.0, 52.0);
                double portraitHeightCap = Math.Clamp(frameRect.Height * 0.034, 44.0, 78.0) * brandLogoAspectRatio;
                logoWidthPixels = Math.Min(logoWidthPixels, Math.Min(portraitWidthCap, portraitHeightCap));
            }

            double relativeWidth = logoWidthPixels / Math.Max(2.0, reference.CanvasWidth);
            string position = NormalizeBrandLogoPosition(job.BrandLogoPosition);

            return (
                relativeWidth,
                frameRect.Left,
                frameRect.Top,
                frameRect.Right,
                frameRect.Bottom,
                padding,
                position);
        }

        private static bool TryParseClockValue(string? input, out TimeSpan value)
        {
            value = TimeSpan.Zero;

            if (string.IsNullOrWhiteSpace(input))
                return false;

            string trimmed = input.Trim();
            return TimeSpan.TryParse(trimmed, out value) ||
                   TimeSpan.TryParseExact(trimmed, @"hh\:mm\:ss", null, out value) ||
                   TimeSpan.TryParseExact(trimmed, @"hh\:mm\:ss\.ff", null, out value) ||
                   TimeSpan.TryParseExact(trimmed, @"hh\:mm\:ss\.fff", null, out value);
        }
        private static (double X, double Y, double W, double H) GetNormalizedCropPercentages(RenderJob job)
        {
            if (job.CropZoomPercent > 0.001)
            {
                int sourceWidthForZoom = job.SourceWidth > 0
                    ? job.SourceWidth
                    : (job.TargetWidth > 0 ? job.TargetWidth : 1080);
                int sourceHeightForZoom = job.SourceHeight > 0
                    ? job.SourceHeight
                    : (job.TargetHeight > 0 ? job.TargetHeight : 1920);

                return BuildCenteredNineBySixteenZoomCrop(
                    sourceWidthForZoom,
                    sourceHeightForZoom,
                    job.CropZoomPercent);
            }

            double x = Math.Clamp(job.CropXPercent, 0.0, 0.98);
            double y = Math.Clamp(job.CropYPercent, 0.0, 0.98);
            double w = Math.Clamp(job.CropWidthPercent, 0.02, 1.0);
            double h = Math.Clamp(job.CropHeightPercent, 0.02, 1.0);

            int sourceWidth = job.SourceWidth > 0
                ? job.SourceWidth
                : (job.TargetWidth > 0 ? job.TargetWidth : 1920);
            int sourceHeight = job.SourceHeight > 0
                ? job.SourceHeight
                : (job.TargetHeight > 0 ? job.TargetHeight : 1080);

            return ApplyCropAspectRatio(x, y, w, h, sourceWidth, sourceHeight, job.CropAspectRatio);
        }

        private static int FloorEvenDimension(double value)
        {
            int dimension = Math.Max(2, (int)Math.Floor(value));
            if ((dimension & 1) != 0)
                dimension -= 1;
            return Math.Max(2, dimension);
        }

        private static (int Width, int Height) GetEffectiveSourceDimensionsAfterCrop(RenderJob job)
        {
            int sourceWidth = job.SourceWidth > 0
                ? job.SourceWidth
                : (job.TargetWidth > 0 ? job.TargetWidth : 1920);
            int sourceHeight = job.SourceHeight > 0
                ? job.SourceHeight
                : (job.TargetHeight > 0 ? job.TargetHeight : 1080);

            if (!HasCropEffect(job))
                return (sourceWidth, sourceHeight);

            var crop = GetNormalizedCropPercentages(job);
            int croppedWidth = FloorEvenDimension(sourceWidth * crop.W);
            int croppedHeight = FloorEvenDimension(sourceHeight * crop.H);
            return (croppedWidth, croppedHeight);
        }

        private static (int CanvasWidth, int CanvasHeight, OverlayContentLayout Layout) ResolveOverlayCanvasProfile(RenderJob job, int requestedWidth, int requestedHeight)
        {
            var effectiveSource = GetEffectiveSourceDimensionsAfterCrop(job);
            bool originalResolution =
                string.IsNullOrWhiteSpace(job.Resolution) ||
                job.Resolution.Contains("Original", StringComparison.OrdinalIgnoreCase);
            bool upscaleRequested =
                !string.IsNullOrWhiteSpace(job.UpscaleMode) &&
                !job.UpscaleMode.Contains("Off", StringComparison.OrdinalIgnoreCase);
            bool explicitRequestedSize =
                requestedWidth > 0 &&
                requestedHeight > 0 &&
                (requestedWidth != job.SourceWidth || requestedHeight != job.SourceHeight);
            bool useExplicitCanvas = upscaleRequested || !originalResolution || explicitRequestedSize;

            int canvasWidth = useExplicitCanvas ? requestedWidth : effectiveSource.Width;
            int canvasHeight = useExplicitCanvas ? requestedHeight : effectiveSource.Height;

            if (canvasWidth <= 0 && canvasHeight > 0 && effectiveSource.Width > 0 && effectiveSource.Height > 0)
                canvasWidth = EnsureEvenDimension((int)Math.Round(effectiveSource.Width * (canvasHeight / (double)effectiveSource.Height)));

            if (canvasHeight <= 0 && canvasWidth > 0 && effectiveSource.Width > 0 && effectiveSource.Height > 0)
                canvasHeight = EnsureEvenDimension((int)Math.Round(effectiveSource.Height * (canvasWidth / (double)effectiveSource.Width)));

            if (canvasWidth <= 0)
                canvasWidth = effectiveSource.Width > 0 ? effectiveSource.Width : 1920;
            if (canvasHeight <= 0)
                canvasHeight = effectiveSource.Height > 0 ? effectiveSource.Height : 1080;

            canvasWidth = EnsureEvenDimension(canvasWidth);
            canvasHeight = EnsureEvenDimension(canvasHeight);

            OverlayContentLayout layout = ShouldUseFullPortraitCanvasForImageOverlays(job, canvasWidth, canvasHeight)
                ? OverlayLayoutHelper.Create(canvasWidth, canvasHeight, canvasWidth, canvasHeight)
                : OverlayLayoutHelper.Create(canvasWidth, canvasHeight, effectiveSource.Width, effectiveSource.Height);
            return (canvasWidth, canvasHeight, layout);
        }

        private static string NormalizeMusicSyncMode(string? input)
        {
            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (v == "beat" || v == "beatsync" || v == "beat-sync" || v == "sync")
                return "beat";
            return "off";
        }

        /// <summary>
        /// Builds an ultra-aesthetic cinematic typewriter text overlay:
        /// 1. Adds a subtle dark gradient vignette at the bottom for high contrast & cinematic atmosphere.
        /// 2. Uses dual-layer rendering (soft outer halo glow + sharp serif italic text).
        /// 3. Smooth per-character reveal fade-in for maximum visual polish.
        /// </summary>
        private static bool ContainsNonAscii(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
            {
                if (c > 127) return true;
            }
            return false;
        }

        private static (string fontPart, string fontFamilyName) ResolveFontForText(string fontStyle, string text, Action<string> onLog)
        {
            string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            bool isVietnameseOrUnicode = ContainsNonAscii(text);

            string fontFileName = "timesi.ttf";
            string fontFamilyName = "Times New Roman";

            if (!string.IsNullOrWhiteSpace(fontStyle))
            {
                if (fontStyle.Contains("Times", StringComparison.OrdinalIgnoreCase))
                {
                    fontFileName = "timesi.ttf";
                    fontFamilyName = "Times New Roman";
                }
                else if (fontStyle.Contains("Segoe", StringComparison.OrdinalIgnoreCase))
                {
                    fontFileName = "segoeuii.ttf";
                    fontFamilyName = "Segoe UI";
                }
                else if (fontStyle.Contains("Arial", StringComparison.OrdinalIgnoreCase))
                {
                    fontFileName = "ariali.ttf";
                    fontFamilyName = "Arial";
                }
                else if (fontStyle.Contains("Calibri", StringComparison.OrdinalIgnoreCase))
                {
                    fontFileName = "calibrii.ttf";
                    fontFamilyName = "Calibri";
                }
                else if (fontStyle.Contains("Georgia", StringComparison.OrdinalIgnoreCase))
                {
                    if (isVietnameseOrUnicode)
                    {
                        // Georgia in Windows Fonts lacks glyphs for Vietnamese double diacritics (ế, ề, ể, ễ, ệ, ố, ồ, ớ, ứ, etc.)
                        // Switch to Times New Roman Italic (classic serif aesthetic with 100% Vietnamese support)
                        fontFileName = "timesi.ttf";
                        fontFamilyName = "Times New Roman";
                        onLog("[FILTER-TEXT] Vietnamese/Unicode detected: Selected Times New Roman Italic (full diacritic support) for aesthetic text.");
                    }
                    else
                    {
                        fontFileName = "georgiai.ttf";
                        fontFamilyName = "Georgia";
                    }
                }
                else if (fontStyle.Contains("Thư Pháp", StringComparison.OrdinalIgnoreCase) || fontStyle.Contains("Calligraphy", StringComparison.OrdinalIgnoreCase))
                {
                    fontFileName = "utm_thuphap.ttf";
                    fontFamilyName = "Thư Pháp Nghệ Thuật";
                }
            }
            else if (isVietnameseOrUnicode)
            {
                fontFileName = "timesi.ttf";
                fontFamilyName = "Times New Roman";
            }

            string fontPath = Path.Combine(fontsDir, fontFileName);
            
            // Check local Assets/Fonts first for bundled custom fonts
            string localFontPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Fonts", fontFileName);
            if (File.Exists(localFontPath))
            {
                fontPath = localFontPath;
            }

            if (File.Exists(fontPath))
            {
                string ffmpegPath = fontPath.Replace("\\", "/").Replace(":", "\\\\:");
                onLog($"[FILTER-TEXT] Using font: {fontFileName} ({fontFamilyName})");
                return ($"fontfile={ffmpegPath}", fontFamilyName);
            }

            string[] fallbacks = { "timesi.ttf", "segoeuii.ttf", "ariali.ttf", "calibrii.ttf", "tahoma.ttf", "arial.ttf" };
            foreach (string fb in fallbacks)
            {
                string fbPath = Path.Combine(fontsDir, fb);
                if (File.Exists(fbPath))
                {
                    string ffmpegPath = fbPath.Replace("\\", "/").Replace(":", "\\\\:");
                    string fbFamily = fb.StartsWith("times") ? "Times New Roman" : fb.StartsWith("segoe") ? "Segoe UI" : "Arial";
                    onLog($"[FILTER-TEXT] Fallback font used: {fb} ({fbFamily})");
                    return ($"fontfile={ffmpegPath}", fbFamily);
                }
            }

            string resolvedFallback = ResolveFfmpegFontFile();
            if (!string.IsNullOrWhiteSpace(resolvedFallback))
                return ($"fontfile='{resolvedFallback}'", "Arial");

            return ("font='Arial'", "Arial");
        }

        private static double NormalizeCoordinatePercent(double inputVal, double fallbackDefault)
        {
            if (double.IsNaN(inputVal) || double.IsInfinity(inputVal)) return fallbackDefault / 100.0;
            if (inputVal < 0) return 0.0;
            if (inputVal > 0.0 && inputVal <= 1.0) return inputVal;
            if (inputVal <= 100.0) return inputVal / 100.0;
            return Math.Clamp(inputVal / 100.0, 0.0, 1.0);
        }

        private static async Task<bool> ValidateOutputBitstreamAsync(string filePath, CancellationToken token, Action<string> onLog)
        {
            if (!File.Exists(filePath))
            {
                onLog($"[OUTPUT-VALIDATION-FAIL] File not found: {filePath}");
                return false;
            }

            string validationArgs = $"-v error -xerror -err_detect explode -i \"{filePath}\" -map 0:v:0 -f null NUL";
            var (exitCode, stderr) = await RunFfmpegCaptureAsync(validationArgs, token);

            if (exitCode == 0)
            {
                onLog("[OUTPUT-VALIDATION] OK");
                return true;
            }

            string summary = string.Join(" | ",
                stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(8));
            onLog($"[OUTPUT-VALIDATION-FAIL] {summary}");
            return false;
        }

        public static void TryDeleteFileSafe(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    return;
                }

                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch { }
        }

        public static string GetTitanTempDir()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "TitanEngineTemp");
            if (!Directory.Exists(tempDir))
            {
                Directory.CreateDirectory(tempDir);
            }
            return tempDir;
        }

        // Preserve the source orientation. "Original" should stay original.
        public static (int Width, int Height) AutoOrientResolution(int width, int height)
        {
            return (width, height);
        }
    }
}
