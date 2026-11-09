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


        private static void RefreshNvidiaUpscaleBackendPaths()
        {
            try
            {
                string expectedDlvsr = GetNgxDlssExpectedPath();
                if (File.Exists(expectedDlvsr))
                {
                    _vfxUpscaleExePath = null;
                    _vfxModelDir = null;
                    _vfxRuntimePathPrefix = null;
                    return;
                }

                string dlssDir = GetDlssDirectoryPath();
                _vfxUpscaleExePath = FindUpscalePipelineExe(dlssDir);
                _vfxModelDir = null;
                _vfxRuntimePathPrefix = null;

                if (!string.IsNullOrWhiteSpace(_vfxUpscaleExePath))
                {
                    ParseUpscaleRunScriptMetadata(_vfxUpscaleExePath, out string? modelDir, out string? runtimePathPrefix);
                    _vfxModelDir = modelDir;
                    _vfxRuntimePathPrefix = runtimePathPrefix;
                }
            }
            catch
            {
                _vfxUpscaleExePath = null;
                _vfxModelDir = null;
                _vfxRuntimePathPrefix = null;
            }
        }

        private static string? FindUpscalePipelineExe(string dlssDir)
        {
            if (string.IsNullOrWhiteSpace(dlssDir) || !Directory.Exists(dlssDir))
                return null;

            string stagedCandidate = Path.Combine(dlssDir, "UpscalePipelineApp", "UpscalePipelineApp.exe");
            if (File.Exists(stagedCandidate))
                return stagedCandidate;

            string directCandidate = Path.Combine(dlssDir, "UpscalePipelineApp.exe");
            if (File.Exists(directCandidate))
                return directCandidate;

            foreach (string sampleRoot in Directory.GetDirectories(dlssDir, "NVIDIA-Maxine-VFX-SDK-Samples-*", SearchOption.TopDirectoryOnly))
            {
                string autoBuildCandidate = Path.Combine(sampleRoot, "build_titan_auto", "apps", "UpscalePipelineApp", "Release", "UpscalePipelineApp.exe");
                if (File.Exists(autoBuildCandidate))
                    return autoBuildCandidate;

                string manualBuildCandidate = Path.Combine(sampleRoot, "build", "apps", "UpscalePipelineApp", "Release", "UpscalePipelineApp.exe");
                if (File.Exists(manualBuildCandidate))
                    return manualBuildCandidate;
            }

            return null;
        }

        private static void ParseUpscaleRunScriptMetadata(string upscaleExePath, out string? modelDir, out string? runtimePathPrefix)
        {
            modelDir = null;
            runtimePathPrefix = null;

            try
            {
                string? exeDir = Path.GetDirectoryName(upscaleExePath);
                if (string.IsNullOrWhiteSpace(exeDir))
                    return;

                string runBatPath = Path.Combine(exeDir, "run_upscalepipelineapp.bat");
                if (!File.Exists(runBatPath))
                    return;

                foreach (string rawLine in File.ReadLines(runBatPath))
                {
                    string line = rawLine.Trim();

                    if (line.StartsWith("SET \"PATH=", StringComparison.OrdinalIgnoreCase))
                    {
                        int start = line.IndexOf("PATH=", StringComparison.OrdinalIgnoreCase);
                        int end = line.LastIndexOf("\"", StringComparison.Ordinal);
                        if (start >= 0 && end > start + 5)
                        {
                            string pathExpr = line.Substring(start + 5, end - (start + 5));
                            runtimePathPrefix = pathExpr.Replace(";%PATH%", string.Empty, StringComparison.OrdinalIgnoreCase);
                        }
                    }

                    if (line.Contains("--model_dir=", StringComparison.OrdinalIgnoreCase))
                    {
                        Match m = Regex.Match(line, "--model_dir=\"([^\"]+)\"", RegexOptions.IgnoreCase);
                        if (m.Success)
                        {
                            string candidate = m.Groups[1].Value.Trim();
                            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                                modelDir = candidate;
                        }
                    }
                }
            }
            catch
            {
                // best-effort metadata parse only
            }
        }

        private static bool HasUpscaleFeatureInstalled(string vfxRoot)
        {
            try
            {
                string featuresDir = Path.Combine(vfxRoot, "features");
                if (!Directory.Exists(featuresDir))
                    return false;

                return Directory.GetDirectories(featuresDir, "*", SearchOption.TopDirectoryOnly)
                    .Any(d => string.Equals(Path.GetFileName(d), "nvvfxupscale", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private static void StageBuiltUpscalePipelineBackend(string builtExePath, string dlssDir, Action<string> onLog)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(builtExePath) || !File.Exists(builtExePath))
                    return;

                string? builtDir = Path.GetDirectoryName(builtExePath);
                if (string.IsNullOrWhiteSpace(builtDir) || !Directory.Exists(builtDir))
                    return;

                string stagedDir = Path.Combine(dlssDir, "UpscalePipelineApp");
                Directory.CreateDirectory(stagedDir);

                foreach (string sourcePath in Directory.GetFiles(builtDir))
                {
                    string destPath = Path.Combine(stagedDir, Path.GetFileName(sourcePath));
                    File.Copy(sourcePath, destPath, true);
                }

                onLog($"[DLSS-BUILD] Staged backend: {Path.Combine(stagedDir, "UpscalePipelineApp.exe")}");
            }
            catch (Exception ex)
            {
                onLog($"[DLSS-BUILD] Could not stage backend files: {ex.Message}");
            }
        }

        public static async Task<VideoAssetProbeInfo> ProbeVideoAssetAsync(string filePath, CancellationToken token)
        {
            string normalizedPath = string.IsNullOrWhiteSpace(filePath)
                ? string.Empty
                : Path.GetFullPath(filePath.Trim());

            var info = new VideoAssetProbeInfo
            {
                FilePath = normalizedPath,
                Exists = !string.IsNullOrWhiteSpace(normalizedPath) && File.Exists(normalizedPath)
            };

            if (!info.Exists)
            {
                info.FailureReason = "File does not exist.";
                return info;
            }

            FileInfo? fileInfo = null;
            try { fileInfo = new FileInfo(normalizedPath); } catch { }
            if (fileInfo != null && fileInfo.Exists)
            {
                info.FileSize = fileInfo.Length;
                info.LastModifiedTimeUtc = fileInfo.LastWriteTimeUtc;
            }

            if (string.IsNullOrWhiteSpace(_ffprobePath) || !File.Exists(_ffprobePath))
            {
                info.FailureReason = "ffprobe executable not found.";
                return info;
            }

            var psi = new ProcessStartInfo
            {
                FileName = _ffprobePath,
                Arguments = $"-v error -select_streams v:0 -show_entries stream=codec_type,codec_name,width,height,avg_frame_rate,r_frame_rate,pix_fmt -show_entries format=duration -of json \"{normalizedPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using var process = new Process { StartInfo = psi };
                process.Start();
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();

                try
                {
                    await process.WaitForExitAsync(token);
                }
                catch (TaskCanceledException)
                {
                    try { process.Kill(); } catch { }
                    throw;
                }

                string stdout = await stdoutTask;
                string stderr = await stderrTask;

                if (process.ExitCode != 0)
                {
                    info.FailureReason = string.IsNullOrWhiteSpace(stderr)
                        ? $"ffprobe exited with code {process.ExitCode}."
                        : stderr.Trim();
                    return info;
                }

                if (string.IsNullOrWhiteSpace(stdout))
                {
                    info.FailureReason = "ffprobe returned no JSON output.";
                    return info;
                }

                using JsonDocument doc = JsonDocument.Parse(stdout);
                if (!doc.RootElement.TryGetProperty("streams", out JsonElement streams) ||
                    streams.ValueKind != JsonValueKind.Array ||
                    streams.GetArrayLength() == 0)
                {
                    info.FailureReason = "No video stream found.";
                    return info;
                }

                JsonElement stream = streams[0];
                info.HasVideoStream = !stream.TryGetProperty("codec_type", out JsonElement codecTypeEl) ||
                    string.Equals(codecTypeEl.GetString(), "video", StringComparison.OrdinalIgnoreCase);

                if (!info.HasVideoStream)
                {
                    info.FailureReason = "Selected stream is not a video stream.";
                    return info;
                }

                if (stream.TryGetProperty("width", out JsonElement widthEl) && widthEl.TryGetInt32(out int width))
                    info.Width = width;

                if (stream.TryGetProperty("height", out JsonElement heightEl) && heightEl.TryGetInt32(out int height))
                    info.Height = height;

                if (stream.TryGetProperty("pix_fmt", out JsonElement pixFmtEl))
                    info.PixelFormat = pixFmtEl.GetString() ?? string.Empty;

                if (stream.TryGetProperty("codec_name", out JsonElement codecNameEl))
                    info.CodecName = codecNameEl.GetString() ?? string.Empty;

                string fpsText = string.Empty;
                if (stream.TryGetProperty("avg_frame_rate", out JsonElement avgFpsEl))
                    fpsText = avgFpsEl.GetString() ?? string.Empty;

                if ((string.IsNullOrWhiteSpace(fpsText) || fpsText == "0/0") &&
                    stream.TryGetProperty("r_frame_rate", out JsonElement rFpsEl))
                {
                    fpsText = rFpsEl.GetString() ?? string.Empty;
                }

                info.Fps = ParseFpsValue(fpsText);

                if (doc.RootElement.TryGetProperty("format", out JsonElement formatEl) &&
                    formatEl.TryGetProperty("duration", out JsonElement durationEl))
                {
                    double.TryParse(
                        durationEl.GetString(),
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out double duration);
                    info.Duration = duration;
                }

                info.ProbeSucceeded = info.Width > 0 && info.Height > 0;
                if (!info.ProbeSucceeded)
                    info.FailureReason = "ffprobe could not read width/height from the asset.";

                return info;
            }
            catch (TaskCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                info.FailureReason = ex.Message;
                return info;
            }
        }

        private static double ParseFpsValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0.0;

            string trimmed = value.Trim();
            string[] parts = trimmed.Split('/');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double numerator) &&
                double.TryParse(parts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double denominator) &&
                denominator != 0)
            {
                return numerator / denominator;
            }

            if (double.TryParse(trimmed, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double fps))
                return fps;

            return 0.0;
        }

        private static string ExecuteProcessAndRead(ProcessStartInfo psi)
        {
            try
            {
                using (var p = Process.Start(psi))
                {
                    if (p == null) return string.Empty;
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    return output.Trim();
                }
            }
            catch { return string.Empty; }
        }

        public static async Task ExecuteRenderAsync(RenderJob job, string customOutputDir, CancellationToken token, Action<double> onProgress, Action<string> onLog)
        {
            string? tempConcatListPath = null;
            string? tempMergedSourcePath = null;
            string? tempAudioConcatListPath = null;
            string? tempMergedAudioPath = null;
            string? tempAudioOverlayMixPath = null;
            string? tempEditedAudioPath = null;
            var tempTemplateArtifacts = new List<string>();
            try
            {
                string baseDir = customOutputDir;
                if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
                {
                    baseDir = AppDomain.CurrentDomain.BaseDirectory;
                }

                string outDir = job.IsApiJob
                    ? baseDir
                    : System.IO.Path.Combine(baseDir, "Titan_Output");
                if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

                string rawName = string.IsNullOrWhiteSpace(job.OutputName)
                    ? $"Titan_{System.IO.Path.GetFileNameWithoutExtension(job.SourcePath)}"
                    : job.OutputName;
                if (!rawName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) rawName += ".mp4";
                string outPath = System.IO.Path.Combine(outDir, rawName);

                onLog($"[OUTPUT] {outPath}");

                string? externalAudioPath = job.AudioPath;
                bool externalAudioVolumePreApplied = false;
                double? externalAudioVolumeOverride = null;
                if (job.MergeAudio && job.MergeAudioPaths.Count > 1)
                {
                    onLog($"[AUDIO-MERGE] Concatenating {job.MergeAudioPaths.Count} audio track(s)...");
                    string audioMergeTag = $"audio_{Path.GetFileNameWithoutExtension(rawName)}_{job.Guid}";
                    (tempAudioConcatListPath, tempMergedAudioPath) = await MergeAudiosToTempTrackAsync(job.MergeAudioPaths, outDir, audioMergeTag, token, onLog);
                    externalAudioPath = tempMergedAudioPath;
                    onLog($"[AUDIO-MERGE] Ready: {Path.GetFileName(externalAudioPath)}");
                }

                string? secondaryAudioPath = string.IsNullOrWhiteSpace(job.SecondaryAudioPath)
                    ? null
                    : Path.GetFullPath(job.SecondaryAudioPath.Trim());
                if (!string.IsNullOrWhiteSpace(secondaryAudioPath) && File.Exists(secondaryAudioPath))
                {
                    if (!string.IsNullOrWhiteSpace(externalAudioPath) && File.Exists(externalAudioPath))
                    {
                        onLog("[AUDIO-OVERLAY] Mixing external audio track 1 + track 2...");
                        string audioOverlayTag = $"audio_overlay_{Path.GetFileNameWithoutExtension(rawName)}_{job.Guid}";
                        tempAudioOverlayMixPath = await MixAudiosToTempTrackAsync(
                            new[] { externalAudioPath, secondaryAudioPath },
                            new[] { job.AudioVolume, job.SecondaryAudioVolume },
                            outDir,
                            audioOverlayTag,
                            token,
                            onLog);
                        externalAudioPath = tempAudioOverlayMixPath;
                        externalAudioVolumePreApplied = true;
                        onLog($"[AUDIO-OVERLAY] Ready: {Path.GetFileName(externalAudioPath)}");
                    }
                    else
                    {
                        externalAudioPath = secondaryAudioPath;
                        externalAudioVolumeOverride = job.SecondaryAudioVolume;
                        onLog("[AUDIO-OVERLAY] Track 1 missing; using audio track 2 as external audio.");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(job.SecondaryAudioPath))
                {
                    onLog($"[AUDIO-OVERLAY-WARN] Audio track 2 not found: {job.SecondaryAudioPath}");
                }

                if (!string.IsNullOrWhiteSpace(externalAudioPath) && File.Exists(externalAudioPath))
                {
                    string audioEditTag = $"audioedit_{Path.GetFileNameWithoutExtension(rawName)}_{job.Guid}";
                    string? editedAudio = await PrepareEditedAudioTrackAsync(externalAudioPath, job, outDir, audioEditTag, token, onLog);
                    if (!string.IsNullOrWhiteSpace(editedAudio) && File.Exists(editedAudio))
                    {
                        tempEditedAudioPath = editedAudio;
                        tempTemplateArtifacts.Add(editedAudio);
                        externalAudioPath = editedAudio;
                        onLog($"[AUDIO-EDIT] Using edited external audio: {Path.GetFileName(editedAudio)}");
                    }

                    // Audio 1 voice FX pre-pass: Echo & Church use IR convolution reverb
                    // (real reverb tails instead of aecho's discrete repeats).
                    string? fxAudio = await PrepareAudioFxTrackAsync(externalAudioPath, job, outDir, audioEditTag, token, onLog);
                    if (!string.IsNullOrWhiteSpace(fxAudio) && File.Exists(fxAudio))
                    {
                        tempTemplateArtifacts.Add(fxAudio);
                        try { tempTemplateArtifacts.Add(Path.Combine(GetTitanTempDir(), $"fx_ir_{Path.GetFileNameWithoutExtension(externalAudioPath)}.wav")); } catch { }
                        externalAudioPath = fxAudio;
                        onLog($"[AUDIO1-FX] Using FX-processed external audio: {Path.GetFileName(fxAudio)}");
                    }
                }

                // ═══════════════════════════════════════════════════════════════
                // CAPTION SYNC: Whisper AI Pre-pass
                // ═══════════════════════════════════════════════════════════════
                string? captionAssPath = null;
                if (job.EnableCaptionSync)
                {
                    if (!string.IsNullOrWhiteSpace(job.AudioPath) && File.Exists(job.AudioPath))
                    {
                        onLog("[CAPTION-SYNC] 🎙️ Generating AI Captions from Audio 1 (Pre-pass)...");
                        captionAssPath = await GenerateCaptionSyncAssAsync(job, job.AudioPath, token, onProgress, onLog);
                    }
                    else
                    {
                        onLog("[CAPTION-SYNC-WARN] Caption Sync enabled but Audio 1 is missing. Skipping AI captions.");
                    }
                }

                List<string> timelineInputs = (job.MergeVideos && job.MergeInputPaths.Count > 0)
                    ? new List<string>(job.MergeInputPaths)
                    : new List<string> { job.SourcePath };

                bool containsImageSource = timelineInputs.Any(IsImageSourcePath);
                bool templateEnabled = !string.IsNullOrWhiteSpace(job.TemplateName) &&
                    !job.TemplateName.Equals("None", StringComparison.OrdinalIgnoreCase);
                string normalizedFxEffectForMerge = ResolveFxEffectName(job.FxEffect);
                bool useLightLeakBurnTransition = job.MergeVideos &&
                    job.MergeInputPaths.Count == 2 &&
                    normalizedFxEffectForMerge.Equals("Light Leak Burn", StringComparison.OrdinalIgnoreCase);

                // ═══════════════════════════════════════════════════════════════
                // IMAGE FAST-PATH GATE.
                //
                // A job that is exactly ONE still image + external music, with no template,
                // no merge, no upscale and no video trim, gains nothing from the timeline
                // pre-render pass: that pass only re-encodes an unchanging frame N times into
                // an intermediate MP4 which the main render then decodes and encodes AGAIN -
                // one whole lossy generation spent on static pixels (measured on this machine,
                // the two passes cost ~3.4s + ~4.2s per 30s of output). Such jobs feed the
                // looped still straight into the main render instead.
                //
                // Every other combination keeps the existing path UNCHANGED: multi-image
                // timelines (the pre-render stitches them), any template (motion filters and
                // drawtext run in it), merges/transitions, upscale (needs a video to work on),
                // and video-trimmed jobs (the pre-render normalises the trim away first).
                // External music is required so the output keeps an audio track exactly as the
                // pre-rendered path produced one.
                // ═══════════════════════════════════════════════════════════════
                bool imageFastPathVideoTrimInactive =
                    (string.IsNullOrWhiteSpace(job.VideoTrimStart) || job.VideoTrimStart == "00:00:00") &&
                    string.IsNullOrWhiteSpace(job.VideoTrimDuration);
                bool imageFastPathUpscaleInactive =
                    job.UpscaleMode == "Off" || job.UpscaleMode == "Off (Original Size)";
                bool imageFastPathAudioReady =
                    !string.IsNullOrWhiteSpace(externalAudioPath) && File.Exists(externalAudioPath);
                bool singleImageFastPath =
                    containsImageSource &&
                    !templateEnabled &&
                    !job.MergeVideos &&
                    timelineInputs.Count == 1 &&
                    IsImageSourcePath(job.SourcePath) &&
                    imageFastPathVideoTrimInactive &&
                    imageFastPathUpscaleInactive &&
                    imageFastPathAudioReady;

                string videoToEncode = job.SourcePath;
                bool lightLeakBurnTransitionBaked = false;
                double imageTimelineTotalDurationOverride = 0.0;
                if (templateEnabled || containsImageSource)
                {
                    if (containsImageSource)
                    {
                        imageTimelineTotalDurationOverride = await ResolveEffectiveImageTimelineTotalDurationAsync(
                            timelineInputs,
                            job,
                            externalAudioPath,
                            onLog);
                    }

                    if (singleImageFastPath)
                    {
                        // DurationSec carries the resolved timeline length into the
                        // DURATION-MASTER logic and the output -t cap, exactly the value the
                        // pre-rendered intermediate used to report - so audio-authority and
                        // duration-sync behave identically. videoToEncode stays the still
                        // itself; the input args below loop it.
                        job.DurationSec = imageTimelineTotalDurationOverride;
                        onLog($"[IMAGE-FASTPATH] Single still + music: skipping the timeline pre-render ({imageTimelineTotalDurationOverride:F2}s, 1 encode pass instead of 2)");
                    }
                    else
                    {
                        string templateTag = $"{Path.GetFileNameWithoutExtension(rawName)}_{job.Guid}";
                        onLog($"[TEMPLATE] Preparing timeline source (template={job.TemplateName}, inputs={timelineInputs.Count})...");
                        var prepared = await BuildTemplateTimelineSourceAsync(
                            timelineInputs,
                            outDir,
                            templateTag,
                            job,
                            externalAudioPath,
                            imageTimelineTotalDurationOverride,
                            token,
                            onLog);
                        videoToEncode = prepared.OutputPath;
                        tempTemplateArtifacts.AddRange(prepared.TempArtifacts);
                        if (prepared.DurationSec > 0.01)
                            job.DurationSec = prepared.DurationSec;
                        onLog($"[TEMPLATE] Timeline ready: {Path.GetFileName(videoToEncode)}");
                    }
                }
                else if (job.MergeVideos && job.MergeInputPaths.Count > 1)
                {
                    if (useLightLeakBurnTransition)
                    {
                        onLog($"[TRANSITION] Rendering Light Leak Burn between 2 source clip(s)...");
                        string mergeTag = $"{Path.GetFileNameWithoutExtension(rawName)}_{job.Guid}";
                        var transition = await LightLeakBurnPreset.RenderTransitionSourceAsync(
                            job.MergeInputPaths,
                            outDir,
                            mergeTag,
                            job,
                            token,
                            onLog,
                            AnalyzeMediaDetailsAsync,
                            HasAudioStreamAsync,
                            RunFfmpegCaptureAsync);

                        videoToEncode = transition.OutputPath;
                        tempTemplateArtifacts.AddRange(transition.TempArtifacts);
                        tempTemplateArtifacts.Add(transition.OutputPath);
                        job.DurationSec = transition.OutputDurationSeconds;
                        lightLeakBurnTransitionBaked = true;
                        onLog($"[TRANSITION] Ready: {Path.GetFileName(videoToEncode)}");
                    }
                    else
                    {
                        onLog($"[MERGE] Concatenating {job.MergeInputPaths.Count} source video(s)...");
                        string mergeTag = $"{Path.GetFileNameWithoutExtension(rawName)}_{job.Guid}";
                        var transSettings = new TitanTransitionSettings
                        {
                            Enabled = job.EnableCrossTransitions,
                            TransitionType = TitanTransitionLibrary.ParseTransitionType(job.CrossTransitionType),
                            DurationSeconds = job.CrossTransitionDuration > 0.01 ? job.CrossTransitionDuration : 0.6
                        };
                        (tempConcatListPath, tempMergedSourcePath) = await MergeVideosToTempSourceAsync(
                            job.MergeInputPaths, outDir, mergeTag, token, onLog, transSettings, job.TargetWidth, job.TargetHeight);
                        videoToEncode = tempMergedSourcePath;
                        onLog($"[MERGE] Ready: {Path.GetFileName(videoToEncode)}");
                    }
                }

                // [NEW] PRE-PROCESSING: Prefer native NVIDIA DLSS (NGX DLVSR), then Upscayl
                bool upscalePreprocessedExternally = false;
                if (job.UpscaleMode != "Off" && job.UpscaleMode != "Off (Original Size)")
                {
                    onLog("[PREPROCESSING] Upscale requested");
                    string upscaledPath = System.IO.Path.Combine(GetTitanTempDir(), $"{Path.GetFileNameWithoutExtension(rawName)}_upscaled.mp4");

                    bool upscaleSuccess = false;
                    if (job.HardwareProfile.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                    {
                        onLog("[PREPROCESSING] Trying NVIDIA DLSS (NGX DLVSR)...");
                        upscaleSuccess = await UpscaleWithNgxDlssAsync(videoToEncode, upscaledPath, job.UpscaleMode, onLog, token);
                    }

                    if (!upscaleSuccess)
                    {
                        onLog("[PREPROCESSING] Trying Upscayl external AI upscale...");
                        upscaleSuccess = await UpscaleWithUpscaylAsync(videoToEncode, upscaledPath, job.UpscaleMode, onLog, token);
                    }

                    if (upscaleSuccess && File.Exists(upscaledPath))
                    {
                        videoToEncode = upscaledPath;
                        upscalePreprocessedExternally = true;
                        onLog($"[PREPROCESSING] External upscale complete, using: {upscaledPath}");
                        
                        var newRes = await GetVideoResolutionAsync(videoToEncode);
                        if (newRes.Item1 > 0 && newRes.Item2 > 0)
                        {
                            job.SourceWidth = newRes.Item1;
                            job.SourceHeight = newRes.Item2;
                        }
                    }
                    else
                    {
                        onLog("[PREPROCESSING-WARN] External upscale unavailable/failed. Falling back to internal DLSS/FSR pipeline");
                    }
                }

                StringBuilder cmd = new StringBuilder();

                // INPUT SEEKING
                if (!string.IsNullOrWhiteSpace(job.VideoTrimStart) && job.VideoTrimStart != "00:00:00")
                {
                    cmd.Append($"-ss {job.VideoTrimStart} ");
                }
                if (!string.IsNullOrWhiteSpace(job.VideoTrimDuration))
                {
                    cmd.Append($"-t {job.VideoTrimDuration} ");
                }

                if (singleImageFastPath)
                {
                    // Loop the still for exactly the resolved timeline length. -t here is
                    // REQUIRED, not an optimisation: an uncapped -loop 1 input renders forever.
                    // -framerate mirrors what the pre-rendered intermediate used to carry out
                    // of its own -r (job fps, image-only default 30), so the output fps, the
                    // looped soundbar sequence and the duration math all see the same value.
                    double fastPathFps = job.SourceFps > 0.1 && !double.IsNaN(job.SourceFps) && !double.IsInfinity(job.SourceFps)
                        ? Math.Clamp(job.SourceFps, 10.0, 120.0)
                        : 30.0;
                    double fastPathDuration = Math.Max(0.10, imageTimelineTotalDurationOverride);
                    cmd.Append($"-y {FfmpegThreadCapArguments()}-loop 1 -framerate {FfmpegDouble(fastPathFps, 3)} -t {FfmpegDouble(fastPathDuration, 3)} -i \"{videoToEncode}\" ");
                    onLog($"[IMAGE-FASTPATH] Input: looped still @ {fastPathFps:F3}fps for {fastPathDuration:F3}s");
                }
                else
                {
                    cmd.Append($"-y {FfmpegThreadCapArguments()}-i \"{videoToEncode}\" ");
                }

                bool hasExternalAudio = !string.IsNullOrEmpty(externalAudioPath) && File.Exists(externalAudioPath);
                if (hasExternalAudio)
                {
                    if (string.IsNullOrWhiteSpace(tempEditedAudioPath))
                    {
                        if (!string.IsNullOrWhiteSpace(job.AudioTrimStart) && job.AudioTrimStart != "00:00:00")
                        {
                            cmd.Append($"-ss {job.AudioTrimStart} ");
                        }
                        if (!string.IsNullOrWhiteSpace(job.AudioTrimDuration))
                        {
                            cmd.Append($"-t {job.AudioTrimDuration} ");
                        }
                    }
                    cmd.Append($"-i \"{externalAudioPath}\" ");
                }

                string? brandLogoPath = null;
                bool hasBrandLogo = job.EnableBrandLogo;
                if (hasBrandLogo)
                {
                    brandLogoPath = ResolveBrandLogoAssetPath(onLog);
                    hasBrandLogo = !string.IsNullOrWhiteSpace(brandLogoPath) && File.Exists(brandLogoPath);
                    if (!hasBrandLogo)
                        onLog("[BRAND-LOGO-WARN] Enabled but asset unavailable. Skipping brand logo.");
                }

                bool hasWatermark = !string.IsNullOrEmpty(job.WatermarkPath) && File.Exists(job.WatermarkPath);
                int staticVisualInputBaseIndex = 1 + (hasExternalAudio ? 1 : 0);
                int? brandLogoInputIndex = hasBrandLogo ? staticVisualInputBaseIndex : null;
                if (hasBrandLogo)
                {
                    cmd.Append($"-i \"{brandLogoPath}\" ");
                    onLog($"[BRAND-LOGO] Using asset: {brandLogoPath}");
                }

                int? watermarkInputIndex = hasWatermark
                    ? staticVisualInputBaseIndex + (hasBrandLogo ? 1 : 0)
                    : null;
                if (hasWatermark)
                {
                    cmd.Append($"-i \"{job.WatermarkPath}\" ");
                }
                int postInputInsertPoint = cmd.Length;
                int generatedOverlayInputBaseIndex = staticVisualInputBaseIndex + (hasBrandLogo ? 1 : 0) + (hasWatermark ? 1 : 0);
                bool hasImageOverlayAssets = hasBrandLogo || hasWatermark;

                // ═══════════════════════════════════════════════════════════════
                // SOUNDBAR (audio visualizer) - shared state for every overlay branch.
                // The generated PNG sequence is inserted as the FIRST generated-overlay
                // input, and generatedOverlayInputBaseIndex is advanced past it before the
                // big if/else chain runs. Every branch therefore allocates strictly after
                // the soundbar slot, which makes an index collision structurally impossible.
                // ═══════════════════════════════════════════════════════════════
                int soundBarInputIndex = generatedOverlayInputBaseIndex;
                bool soundBarAnalysisFailed = false;
                bool soundBarLayerActive = false;
                AudioAnalysisResult? soundBarAnalysis = null;
                SoundBarGeneratedOverlay? soundBarOverlay = null;
                List<AudioInterval>? soundBarAudibleIntervals = null;

                bool hasSourceAudio = await HasAudioStreamAsync(videoToEncode);
                if (hasExternalAudio && hasSourceAudio)
                    onLog("[FFMPEG-MAP] Audio sources detected: source video + external audio");
                else if (hasExternalAudio)
                    onLog("[FFMPEG-MAP] Audio source: external input #1");
                else if (hasSourceAudio)
                    onLog("[FFMPEG-MAP] Audio source: source input #0");
                else
                    onLog("[FFMPEG-MAP] No audio stream detected; output will be video-only.");

                // ENCODING SETTINGS
                long targetBitrate = job.SourceBitrate;

                // DETERMINE TARGET RESOLUTION
                int targetResW = -1, targetResH = -1;

                if (job.TargetWidth > 0 && job.TargetHeight > 0)
                {
                    targetResW = job.TargetWidth;
                    targetResH = job.TargetHeight;
                    onLog($"[RESOLUTION] {targetResW}x{targetResH}");
                }
                else if (!job.Resolution.Contains("Original"))
                {
                    if (job.Resolution.Contains("x"))
                    {
                        Match match = Regex.Match(job.Resolution, @"(\d+)x(\d+)");
                        if (match.Success)
                        {
                            if (int.TryParse(match.Groups[1].Value, out int w) && int.TryParse(match.Groups[2].Value, out int h))
                            {
                                targetResW = w;
                                targetResH = h;
                                onLog($"[RESOLUTION] {targetResW}x{targetResH}");
                            }
                        }
                    }
                    else if (job.Resolution.Contains("p"))
                    {
                        Match match = Regex.Match(job.Resolution, @"(\d+)p");
                        if (match.Success)
                        {
                            if (int.TryParse(match.Groups[1].Value, out int height))
                            {
                                targetResH = height;
                                targetResW = -2;
                                onLog($"[RESOLUTION] -2:{targetResH}");
                            }
                        }
                    }
                }

                // SELECT ENCODER - with NVENC capability check
                string encoder = "libx264";
                string preset = "veryslow";

                // [NEW] Check if NVENC is actually supported
                bool nvencAvailable = HasNvencSupport();
                bool hevcNvencAvailable = HasHevcNvencSupport();
                bool isUltraHighResolutionTarget =
                    (targetResW > 0 && targetResW > 4096) ||
                    (targetResH > 0 && targetResH > 4096);

                if (job.HardwareProfile.Contains("NVIDIA") && nvencAvailable)
                {
                    if (job.HardwareProfile.Contains("AV1", StringComparison.OrdinalIgnoreCase))
                    {
                        if (HasAv1NvencSupport())
                        {
                            encoder = "av1_nvenc";
                            preset = "p6"; // NVENC AV1 uses p1-p7, p6 is high quality
                            onLog("[ENCODER] NVIDIA AV1 NVENC AVAILABLE");
                        }
                        else
                        {
                            encoder = "libx264";
                            preset = "slow";
                            onLog("[ENCODER-WARN] AV1 selected but av1_nvenc is unavailable. Fallback to CPU libx264.");
                        }
                    }
                    else if (isUltraHighResolutionTarget)
                    {
                        if (hevcNvencAvailable)
                        {
                            encoder = "hevc_nvenc";
                            preset = "medium";
                            onLog("[ENCODER] NVIDIA HEVC NVENC (8K-safe GPU path)");
                        }
                        else
                        {
                            encoder = "libx264";
                            preset = "slow";
                            onLog("[ENCODER-WARN] 8K target detected but HEVC NVENC is unavailable.");
                            onLog("[ENCODER-FALLBACK] Using libx264 (CPU) for better compatibility.");
                        }
                    }
                    else
                    {
                        encoder = "h264_nvenc";
                        preset = "medium";
                        onLog("[ENCODER] NVIDIA NVENC (medium - GPU) AVAILABLE");
                    }
                }
                else if (job.HardwareProfile.Contains("NVIDIA") && !nvencAvailable)
                {
                    encoder = "libx264";
                    preset = "slow";
                    onLog($"[ENCODER-WARN] NVIDIA NVENC NOT SUPPORTED by your FFmpeg build!");
                    onLog($"[ENCODER-FALLBACK] Using libx264 (CPU) instead");
                    onLog($"[FIX] Download FFmpeg with NVENC support from: https://github.com/BtbN/FFmpeg-Builds");
                }
                else if (job.HardwareProfile.Contains("AMD"))
                {
                    // Giai đoạn 3: h264_amf/av1_amf chỉ tồn tại trên Windows FFmpeg — trên Linux
                    // AMD/Intel đi qua VAAPI (probe test encode thật; fail → libx264 như fallback gốc).
                    if (!TitanPlatform.IsWindows)
                    {
                        if (HasH264VaapiSupport())
                        {
                            encoder = "h264_vaapi";
                            preset = "medium";
                            onLog($"[ENCODER] Linux VAAPI (h264_vaapi via {GetVaapiRenderNode()})");
                        }
                        else
                        {
                            encoder = "libx264";
                            preset = "veryslow";
                            onLog($"[ENCODER-WARN] Linux: no usable VAAPI encoder. Fallback to CPU libx264.");
                        }
                    }
                    else if (job.HardwareProfile.Contains("AV1", StringComparison.OrdinalIgnoreCase))
                    {
                        if (HasAv1AmfSupport())
                        {
                            encoder = "av1_amf";
                            preset = "quality";
                            onLog($"[ENCODER] AMD AMF AV1 (quality - GPU)");
                        }
                        else
                        {
                            encoder = "libx264";
                            preset = "veryslow";
                            onLog($"[ENCODER-WARN] AV1 selected but av1_amf is unavailable. Fallback to CPU libx264.");
                        }
                    }
                    else
                    {
                        encoder = "h264_amf";
                        preset = "quality";
                        onLog($"[ENCODER] AMD AMF (quality - GPU)");
                    }
                }
                else if (job.HardwareProfile.Contains("Intel"))
                {
                    // Giai đoạn 3: trên Linux QSV chỉ chọn khi probe test encode thật pass.
                    if (!TitanPlatform.IsWindows)
                    {
                        if (HasH264QsvSupport())
                        {
                            encoder = "h264_qsv";
                            preset = "veryslow";
                            onLog($"[ENCODER] Intel QSV (veryslow - GPU)");
                        }
                        else if (HasH264VaapiSupport())
                        {
                            encoder = "h264_vaapi";
                            preset = "medium";
                            onLog($"[ENCODER] Linux VAAPI (h264_vaapi via {GetVaapiRenderNode()})");
                        }
                        else
                        {
                            encoder = "libx264";
                            preset = "veryslow";
                            onLog($"[ENCODER-WARN] Linux: no usable QSV/VAAPI encoder. Fallback to CPU libx264.");
                        }
                    }
                    else
                    {
                        encoder = "h264_qsv";
                        preset = "veryslow";
                        onLog($"[ENCODER] Intel QSV (veryslow - GPU)");
                    }
                }
                else
                {
                    encoder = "libx264";
                    preset = "veryslow";
                    onLog($"[ENCODER] libx264 (veryslow - CPU)");
                }

                string videoCodecArgs = BuildVideoCodecArgs(encoder, preset);
                cmd.Append(videoCodecArgs);

                if (encoder == "libx264")
                {
                    // x264 defaults to ~1.5x cores of frame threads; on the CPU fallback the
                    // encoder is the hog, so it gets the same budget as the filters. Pixel
                    // output is thread-count-independent; only the CPU footprint changes.
                    cmd.Append($"-threads {FfmpegThreadCapCount()} ");
                    onLog($"[ENCODER-QUALITY] CRF 16 (High Quality)");
                }
                else if (encoder == "h264_nvenc" || encoder == "hevc_nvenc")
                {
                    onLog($"[ENCODER-NVENC] Using VBR mode with quality level 16");
                }
                // Giai đoạn 3: VAAPI (Linux) — frames sau hwupload là VAAPI surface, không nhận
                // -pix_fmt yuv420p; device args + hwupload gỡ ra sạch khi CPU retry (bẫy 12: Insert(0)
                // đứng đầu chuỗi nên không lệch offset của các Insert số phía sau).
                string? vaapiDeviceArgs = null;
                if (encoder == "h264_vaapi")
                {
                    string? vaapiNode = GetVaapiRenderNode();
                    if (!string.IsNullOrEmpty(vaapiNode))
                    {
                        vaapiDeviceArgs = $"-init_hw_device vaapi=va0:{vaapiNode} -filter_hw_device va0 ";
                        cmd.Insert(0, vaapiDeviceArgs);
                    }
                }
                else
                {
                    cmd.Append("-pix_fmt yuv420p ");
                }

                // BITRATE STRATEGY
                if (job.BitrateStrategy.Contains("Match Source"))
                {
                    int bitrateRefW = targetResW > 0 ? targetResW : job.SourceWidth;
                    int bitrateRefH = targetResH > 0 ? targetResH : job.SourceHeight;
                    long minRecommendedBitrate = GetMinRecommendedBitrate(bitrateRefW, bitrateRefH);
                    if (targetBitrate < minRecommendedBitrate)
                    {
                        onLog($"[BITRATE-AUTO] Match Source too low ({targetBitrate} bps) for {bitrateRefW}x{bitrateRefH}. Raising to {minRecommendedBitrate} bps.");
                        targetBitrate = minRecommendedBitrate;
                    }
                    cmd.Append($"-minrate {(long)(targetBitrate * 0.7)} -maxrate {(long)(targetBitrate * 1.5)} -bufsize {targetBitrate * 4} ");
                }
                else if (job.BitrateStrategy.Contains("Boost"))
                {
                    targetBitrate = (long)(targetBitrate * 1.5);
                    cmd.Append($"-b:v {targetBitrate} -minrate {(long)(targetBitrate * 0.8)} -maxrate {(long)(targetBitrate * 1.3)} -bufsize {targetBitrate * 2} ");
                }
                else if (job.BitrateStrategy.Contains("Custom"))
                {
                    if (long.TryParse(job.CustomBitrate, out long customVal))
                        targetBitrate = customVal * 1000;
                    cmd.Append($"-b:v {targetBitrate} -minrate {(long)(targetBitrate * 0.8)} -maxrate {(long)(targetBitrate * 1.2)} -bufsize {targetBitrate * 2} ");
                }

                double normalizedSpeed = NormalizePlaybackSpeed(job.SlowMotionSpeed);
                if (Math.Abs(normalizedSpeed - job.SlowMotionSpeed) > 0.0001)
                {
                    onLog($"[FILTER-SLOWMOTION-WARN] Requested speed {job.SlowMotionSpeed:F3}x is invalid. Using {normalizedSpeed:F3}x.");
                }
                bool shouldValidateOutput = encoder != "libx264" && Math.Abs(normalizedSpeed - 1.0) > 0.0001;

                double estimatedVideoDuration = GetEstimatedOutputDurationSeconds(job, normalizedSpeed);
                double estimatedAudioDuration = await GetEstimatedAudioOutputDurationSecondsAsync(
                    job,
                    normalizedSpeed,
                    externalAudioPath,
                    hasSourceAudio,
                    hasExternalAudio,
                    onLog);

                // ── DURATION MASTER LOGIC ──────────────────────────────────────
                // If Audio 1 is present AND source is an IMAGE timeline:
                //   → Audio 1 is the absolute duration authority (trim the image timeline to Audio 1 length)
                // If Audio 1 is present AND source is a real VIDEO:
                //   → Take Math.Min(video, audio1): video shorter = use video, video longer = use audio1
                // No Audio 1 at all:
                //   → Old behaviour: Math.Min(video, combinedAudio)
                double estimatedOutputDuration;
                double audio1Duration = 0.0;

                bool hasAudio1 = !string.IsNullOrWhiteSpace(job.AudioPath) && File.Exists(job.AudioPath);
                if (hasAudio1)
                {
                    var audio1Meta = await AnalyzeMediaAsync(job.AudioPath!);
                    audio1Duration = Math.Max(0.0, audio1Meta.Duration);
                    if (audio1Duration > 0.01)
                    {
                        double audio1Speed = Math.Clamp(job.ExternalAudioSpeed, 0.10, 8.0);
                        double audio1EffectiveDuration = audio1Duration / audio1Speed;

                        // Apply trim if user has trimmed Audio 1
                        if (!string.IsNullOrWhiteSpace(job.AudioTrimDuration) &&
                            double.TryParse(job.AudioTrimDuration, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out double trimDur) &&
                            trimDur > 0.01)
                        {
                            double trimStart = 0.0;
                            if (!string.IsNullOrWhiteSpace(job.AudioTrimStart))
                                double.TryParse(job.AudioTrimStart, System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out trimStart);
                            audio1EffectiveDuration = Math.Min(audio1EffectiveDuration - trimStart, trimDur);
                        }
                        audio1EffectiveDuration = Math.Max(0.01, audio1EffectiveDuration);

                        if (containsImageSource)
                        {
                            // IMAGE TIMELINE: Audio 1 dictates total length — trim image timeline to Audio 1
                            estimatedOutputDuration = audio1EffectiveDuration;
                            onLog($"[DURATION-MASTER] Image timeline — Audio 1 is authority: {estimatedOutputDuration:F2}s (image timeline was {estimatedVideoDuration:F2}s)");
                        }
                        else
                        {
                            // REAL VIDEO: never extend a short video — take the shorter of the two
                            estimatedOutputDuration = Math.Min(estimatedVideoDuration, audio1EffectiveDuration);
                            if (estimatedOutputDuration < estimatedVideoDuration - 0.01)
                                onLog($"[DURATION-MASTER] Video source — Audio 1 shorter ({audio1EffectiveDuration:F2}s); output capped to {estimatedOutputDuration:F2}s");
                            else if (estimatedOutputDuration < audio1EffectiveDuration - 0.01)
                                onLog($"[DURATION-MASTER] Video source — video shorter ({estimatedVideoDuration:F2}s) than Audio 1 ({audio1EffectiveDuration:F2}s); keeping video duration");
                            else
                                onLog($"[DURATION-MASTER] Video source — matched Audio 1: {estimatedOutputDuration:F2}s");
                        }
                    }
                    else
                    {
                        // Audio 1 couldn't be probed – fall back to old logic
                        estimatedOutputDuration = estimatedVideoDuration;
                        if (estimatedAudioDuration > 0.01)
                            estimatedOutputDuration = Math.Min(estimatedOutputDuration, estimatedAudioDuration);
                        onLog($"[DURATION-MASTER] Audio 1 probe failed; fallback to min(video,audio): {estimatedOutputDuration:F2}s");
                    }
                }
                else
                {
                    // No Audio 1 – old behavior: take minimum of video and any audio
                    estimatedOutputDuration = estimatedVideoDuration;
                    if (estimatedAudioDuration > 0.01)
                        estimatedOutputDuration = Math.Min(estimatedOutputDuration, estimatedAudioDuration);

                    if (estimatedOutputDuration > 0.01 && Math.Abs(estimatedOutputDuration - estimatedVideoDuration) > 0.01)
                        onLog($"[DURATION-SYNC] Audio shorter than video; output capped to {estimatedOutputDuration:F2}s");
                    else if (estimatedAudioDuration > 0.01 &&
                             estimatedVideoDuration > 0.01 &&
                             estimatedVideoDuration < estimatedAudioDuration - 0.01)
                        onLog($"[DURATION-SYNC] Source/video shorter than audio; keeping source duration: {estimatedVideoDuration:F2}s");
                }
                // ──────────────────────────────────────────────────────────────

                var fadeSettings = NormalizeFadeSettings(job, estimatedOutputDuration, onLog);
                string normalizedFxEffect = ResolveFxEffectName(job.FxEffect);
                string normalizedOverlayEffect = ResolveOverlayEffectName(job.OverlayEffect);
                string normalizedBorderEffect = ResolveOverlayEffectName(job.BorderEffect);
                OverlayEffectRecipe? recipeOverlayEffect =
                    EffectRecipeLibrary.TryGetOverlayEffectRecipe(normalizedOverlayEffect, out OverlayEffectRecipe overlayEffectRecipe)
                        ? overlayEffectRecipe
                        : EffectRecipeLibrary.TryGetOverlayEffectRecipe(normalizedFxEffect, out overlayEffectRecipe)
                            ? overlayEffectRecipe
                            : null;
                ParticleEffectRecipe? recipeParticleEffect =
                    EffectRecipeLibrary.TryGetParticleEffectRecipe(normalizedOverlayEffect, out ParticleEffectRecipe particleEffectRecipe)
                        ? particleEffectRecipe
                        : EffectRecipeLibrary.TryGetParticleEffectRecipe(normalizedFxEffect, out particleEffectRecipe)
                            ? particleEffectRecipe
                            : null;
                PostProcessEffectRecipe? recipePostProcessEffect =
                    EffectRecipeLibrary.TryGetPostProcessEffectRecipe(normalizedFxEffect, out PostProcessEffectRecipe postProcessEffectRecipe)
                        ? postProcessEffectRecipe
                        : EffectRecipeLibrary.TryGetPostProcessEffectRecipe(normalizedOverlayEffect, out postProcessEffectRecipe)
                            ? postProcessEffectRecipe
                            : null;
                TransitionRecipe? recipeTransitionEffect =
                    EffectRecipeLibrary.TryGetTransitionRecipe(normalizedFxEffect, out TransitionRecipe transitionEffectRecipe)
                        ? transitionEffectRecipe
                        : EffectRecipeLibrary.TryGetTransitionRecipe(normalizedOverlayEffect, out transitionEffectRecipe)
                            ? transitionEffectRecipe
                            : null;
                bool useRecipeLightLeakOverlayEffect =
                    recipeOverlayEffect != null &&
                    string.Equals(recipeOverlayEffect.LegacyResolverKey, "light-leak-overlay", StringComparison.OrdinalIgnoreCase);
                bool useRecipeScratchVideoEffect =
                    recipeOverlayEffect != null &&
                    string.Equals(recipeOverlayEffect.LegacyResolverKey, "scratch-video", StringComparison.OrdinalIgnoreCase);
                bool useRecipeParticleOverlayEffect = recipeParticleEffect != null;
                bool useRecipeGenericOverlayAssetEffect =
                    recipeOverlayEffect != null &&
                    !useRecipeLightLeakOverlayEffect &&
                    !useRecipeScratchVideoEffect;
                bool hasLightLeakFx = normalizedFxEffect.Equals("RÃƒÂ² phim (Leak 1 / Light Leak)", StringComparison.OrdinalIgnoreCase);
                bool hasLightLeakBurnFx = normalizedFxEffect.Equals("Light Leak Burn", StringComparison.OrdinalIgnoreCase);
                bool hasScratchVideoFx = normalizedFxEffect.Equals("Dust & Scratches", StringComparison.OrdinalIgnoreCase) ||
                                         normalizedOverlayEffect.Equals("Dust & Scratches", StringComparison.OrdinalIgnoreCase) ||
                                         useRecipeScratchVideoEffect;
                bool hasGlowFx = normalizedFxEffect.Equals("Cinematic Glow", StringComparison.OrdinalIgnoreCase) ||
                                  normalizedFxEffect.Equals("Halo Glow", StringComparison.OrdinalIgnoreCase) ||
                                  normalizedFxEffect.Equals("Prism Light", StringComparison.OrdinalIgnoreCase) ||
                                  normalizedFxEffect.Equals("Editorial Bloom", StringComparison.OrdinalIgnoreCase);
                bool hasStylizedLightFx = hasLightLeakFx || hasLightLeakBurnFx || hasGlowFx;
                bool useLightLeakBurnEffect = hasLightLeakBurnFx && !lightLeakBurnTransitionBaked;
                bool usePolaroidScrapbookEffect =
                    job.EnablePolaroidScrapbook ||
                    normalizedBorderEffect.Equals("Polaroid Scrapbook", StringComparison.OrdinalIgnoreCase) ||
                    normalizedOverlayEffect.Equals("Polaroid Scrapbook", StringComparison.OrdinalIgnoreCase);
                bool useLightLeakOverlayEffect =
                    normalizedOverlayEffect.Equals("Light Leak Overlay", StringComparison.OrdinalIgnoreCase) ||
                    useRecipeLightLeakOverlayEffect ||
                    hasLightLeakFx;
                bool useDreamyDotOverlayEffect =
                    normalizedOverlayEffect.Equals("Dreamy Dot Overlay", StringComparison.OrdinalIgnoreCase) ||
                    normalizedOverlayEffect.Equals("Dreamy Dot Chaos Overlay", StringComparison.OrdinalIgnoreCase);
                bool useDreamyDotOverlay2Effect =
                    normalizedOverlayEffect.Equals("Dreamy Dot Overlay 2", StringComparison.OrdinalIgnoreCase);
                bool useSnowfallOverlayEffect =
                    normalizedOverlayEffect.Equals("Snowfall Overlay", StringComparison.OrdinalIgnoreCase) ||
                    normalizedFxEffect.Equals("Snowfall Overlay", StringComparison.OrdinalIgnoreCase);
                bool useRainOverlayEffect =
                    normalizedOverlayEffect.Equals("Rain Overlay", StringComparison.OrdinalIgnoreCase);
                bool usePolaroidScrapbook2Effect =
                    normalizedBorderEffect.Equals("Polaroid Scrapbook 2", StringComparison.OrdinalIgnoreCase) ||
                    normalizedOverlayEffect.Equals("Polaroid Scrapbook 2", StringComparison.OrdinalIgnoreCase);
                bool useRgbPolaroidLedEffect =
                    job.EnableRgbPolaroidScrapbook ||
                    normalizedBorderEffect.Equals("RGB Polaroid Border", StringComparison.OrdinalIgnoreCase) ||
                    normalizedOverlayEffect.Equals("RGB Polaroid Border", StringComparison.OrdinalIgnoreCase);
                bool useDashedPolaroidEffect =
                    job.EnableDashedPolaroidScrapbook ||
                    normalizedBorderEffect.Equals("Dashed Polaroid Frame", StringComparison.OrdinalIgnoreCase) ||
                    normalizedOverlayEffect.Equals("Dashed Polaroid Frame", StringComparison.OrdinalIgnoreCase);
                bool useScratchVideoEffect = hasScratchVideoFx;
                bool useDashedAndScratchEffect = useDashedPolaroidEffect && useScratchVideoEffect;
                bool hasGeneratedOverlayEffect =
                    useDreamyDotOverlayEffect ||
                    useSnowfallOverlayEffect ||
                    usePolaroidScrapbook2Effect ||
                    useRgbPolaroidLedEffect ||
                    useDashedPolaroidEffect ||
                    useScratchVideoEffect ||
                    useRecipeParticleOverlayEffect;
                bool useLightLeakBurnWithOverlayEffect = useLightLeakBurnEffect && hasGeneratedOverlayEffect;
                bool useGeneratedOverlayStages =
                    useLightLeakBurnEffect ||
                    hasGeneratedOverlayEffect;

                LightLeakOverlayRecipe? lightLeakOverlayRecipe = null;
                LightLeakOverlayRecipe? lightLeakBurnAssetRecipe = null;
                string? recipeOverlayAssetPath = null;
                List<SnowfallOverlayRecipe> snowfallOverlayRecipes = new();
                RainOverlayRecipe? rainOverlayRecipe = null;
                DreamyDotOverlay2Recipe? dreamyDotOverlay2Recipe = null;
                List<SnowfallGeneratedOverlay?> snowfallGeneratedOverlays = new();
                DreamyDotGeneratedOverlay? dreamyDotOverlay = null;
                PolaroidScrapbook2GeneratedOverlay? polaroidScrapbook2Overlay = null;
                RgbPolaroidLedGeneratedOverlay? rgbPolaroidLedOverlay = null;
                DashedPolaroidGeneratedOverlay? dashedPolaroidOverlay = null;
                ScratchVideoGeneratedOverlay? scratchVideoOverlay = null;
                ParticleOverlaySequence? recipeParticleOverlay = null;
                double lightLeakBurnFps = Math.Clamp(job.SourceFps > 0.1 ? job.SourceFps : 30.0, 10.0, 120.0);
                if (useLightLeakBurnEffect)
                {
                    lightLeakBurnAssetRecipe = LightLeakBurnPreset.ResolveSingleClipAssetRecipe(job, estimatedOutputDuration, onLog);
                    if (lightLeakBurnAssetRecipe == null)
                        throw new InvalidOperationException("Light leak asset missing.");

                    await ValidateLightLeakAssetRecipeAsync("Light Leak Burn", lightLeakBurnAssetRecipe, token, onLog);
                    onLog($"[LIGHT-LEAK-BURN] Asset-backed overlay ready: {Path.GetFileName(lightLeakBurnAssetRecipe.Asset.FilePath)}");
                }

                if (useRgbPolaroidLedEffect)
                {
                    var overlayProfile = ResolveOverlayCanvasProfile(job, targetResW, targetResH);
                    int effectWidth = overlayProfile.CanvasWidth;
                    int effectHeight = overlayProfile.CanvasHeight;
                    OverlayContentLayout effectLayout = overlayProfile.Layout;

                    rgbPolaroidLedOverlay = await EngineServices.Images!.GenerateRgbPolaroidLedOverlayAsync(
                        effectWidth,
                        effectHeight,
                        effectLayout,
                        lightLeakBurnFps,
                        estimatedOutputDuration,
                        job,
                        $"{Path.GetFileNameWithoutExtension(rawName)}_{job.Guid}",
                        token,
                        onLog);

                    tempTemplateArtifacts.AddRange(rgbPolaroidLedOverlay.TempArtifacts);
                    onLog($"[RGB-POLAROID] Generated {rgbPolaroidLedOverlay.FrameCount} LED frames @ {effectWidth}x{effectHeight} / {rgbPolaroidLedOverlay.FrameRate:F2}fps (content {effectLayout.ContentWidth:F0}x{effectLayout.ContentHeight:F0} @ {effectLayout.ContentX:F0},{effectLayout.ContentY:F0})");
                }

                if (usePolaroidScrapbook2Effect)
                {
                    var overlayProfile = ResolveOverlayCanvasProfile(job, targetResW, targetResH);
                    int effectWidth = overlayProfile.CanvasWidth;
                    int effectHeight = overlayProfile.CanvasHeight;
                    OverlayContentLayout effectLayout = overlayProfile.Layout;

                    polaroidScrapbook2Overlay = await EngineServices.Images!.GeneratePolaroidScrapbook2OverlayAsync(
                        effectWidth,
                        effectHeight,
                        effectLayout,
                        job,
                        $"{Path.GetFileNameWithoutExtension(rawName)}_{job.Guid}",
                        token,
                        onLog);

                    tempTemplateArtifacts.AddRange(polaroidScrapbook2Overlay.TempArtifacts);
                    onLog($"[POLAROID-2] Generated rounded scrapbook overlay @ {effectWidth}x{effectHeight} (content {effectLayout.ContentWidth:F0}x{effectLayout.ContentHeight:F0} @ {effectLayout.ContentX:F0},{effectLayout.ContentY:F0})");
                }

                if (useDashedPolaroidEffect)
                {
                    var overlayProfile = ResolveOverlayCanvasProfile(job, targetResW, targetResH);
                    int effectWidth = overlayProfile.CanvasWidth;
                    int effectHeight = overlayProfile.CanvasHeight;
                    OverlayContentLayout effectLayout = overlayProfile.Layout;

                    dashedPolaroidOverlay = await EngineServices.Images!.GenerateDashedPolaroidOverlayAsync(
                        effectWidth,
                        effectHeight,
                        effectLayout,
                        lightLeakBurnFps,
                        job,
                        $"{Path.GetFileNameWithoutExtension(rawName)}_{job.Guid}",
                        token,
                        onLog);

                    tempTemplateArtifacts.AddRange(dashedPolaroidOverlay.TempArtifacts);
                    onLog($"[DASHED-POLAROID] Generated dashed rounded frame overlay @ {effectWidth}x{effectHeight} / {dashedPolaroidOverlay.FrameRate:F2}fps (content {effectLayout.ContentWidth:F0}x{effectLayout.ContentHeight:F0} @ {effectLayout.ContentX:F0},{effectLayout.ContentY:F0})");
                }

                if (useScratchVideoEffect)
                {
                    var overlayProfile = ResolveOverlayCanvasProfile(job, targetResW, targetResH);
                    int effectWidth = overlayProfile.CanvasWidth;
                    int effectHeight = overlayProfile.CanvasHeight;
                    OverlayContentLayout effectLayout = overlayProfile.Layout;

                    scratchVideoOverlay = await EngineServices.Images!.GenerateScratchVideoOverlayAsync(
                        effectWidth,
                        effectHeight,
                        effectLayout,
                        lightLeakBurnFps,
                        estimatedOutputDuration,
                        job,
                        $"{Path.GetFileNameWithoutExtension(rawName)}_{job.Guid}",
                        token,
                        onLog);

                    tempTemplateArtifacts.AddRange(scratchVideoOverlay.TempArtifacts);
                    onLog($"[SCRATCH-VIDEO] Generated animated scratch overlay @ {effectWidth}x{effectHeight} / {scratchVideoOverlay.FrameRate:F2}fps (content {effectLayout.ContentWidth:F0}x{effectLayout.ContentHeight:F0} @ {effectLayout.ContentX:F0},{effectLayout.ContentY:F0})");
                }

                if (useRecipeParticleOverlayEffect && recipeParticleEffect != null)
                {
                    var overlayProfile = ResolveOverlayCanvasProfile(job, targetResW, targetResH);
                    int effectWidth = overlayProfile.CanvasWidth;
                    int effectHeight = overlayProfile.CanvasHeight;

                    recipeParticleOverlay = await EngineServices.Images!.GenerateParticleOverlayAsync(
                        effectWidth,
                        effectHeight,
                        lightLeakBurnFps,
                        estimatedOutputDuration,
                        recipeParticleEffect,
                        $"{Path.GetFileNameWithoutExtension(rawName)}_{job.Guid}",
                        token,
                        onLog);

                    onLog($"[RECIPE-PARTICLE] Prepared {recipeParticleEffect.Name} overlay @ {recipeParticleOverlay.Width}x{recipeParticleOverlay.Height} / {recipeParticleOverlay.FrameRate:F2}fps (frames={recipeParticleOverlay.FrameCount}, cache={recipeParticleOverlay.CacheKey}, reused={recipeParticleOverlay.FromCache})");
                }

                if (useDreamyDotOverlayEffect)
                {
                    var overlayProfile = ResolveOverlayCanvasProfile(job, targetResW, targetResH);
                    int effectWidth = overlayProfile.CanvasWidth;
                    int effectHeight = overlayProfile.CanvasHeight;
                    OverlayContentLayout effectLayout = overlayProfile.Layout;
                    DreamyDotGeneratedOverlay cachedDreamyDotOverlay = DreamyDotOverlayPreset.CreateAssetDescriptor(
                        effectWidth,
                        effectHeight,
                        lightLeakBurnFps,
                        estimatedOutputDuration,
                        job);
                    string? cachedDreamyDotAssetPath = await TryGetDreamyDotOverlayAssetAsync(cachedDreamyDotOverlay, token, onLog);
                    if (!string.IsNullOrWhiteSpace(cachedDreamyDotAssetPath))
                    {
                        cachedDreamyDotOverlay.PreRenderedAssetPath = cachedDreamyDotAssetPath;
                        dreamyDotOverlay = cachedDreamyDotOverlay;
                        onLog($"[DREAMY-DOT] Reusing cached overlay asset @ {dreamyDotOverlay.Width}x{dreamyDotOverlay.Height} / {dreamyDotOverlay.FrameRate:F2}fps for {effectWidth}x{effectHeight} output (content {effectLayout.ContentWidth:F0}x{effectLayout.ContentHeight:F0} @ {effectLayout.ContentX:F0},{effectLayout.ContentY:F0})");
                    }
                    else
                    {
                        dreamyDotOverlay = await EngineServices.Images!.GenerateDreamyDotOverlayAsync(
                            effectWidth,
                            effectHeight,
                            effectLayout,
                            lightLeakBurnFps,
                            estimatedOutputDuration,
                            job,
                            $"{Path.GetFileNameWithoutExtension(rawName)}_{job.Guid}",
                            token,
                            onLog);

                        dreamyDotOverlay.PreRenderedAssetPath = await EnsureDreamyDotOverlayAssetAsync(dreamyDotOverlay, token, onLog);
                        tempTemplateArtifacts.AddRange(dreamyDotOverlay.TempArtifacts);
                        onLog($"[DREAMY-DOT] Generated dreamy dot overlay @ {dreamyDotOverlay.Width}x{dreamyDotOverlay.Height} / {dreamyDotOverlay.FrameRate:F2}fps for {effectWidth}x{effectHeight} output (content {effectLayout.ContentWidth:F0}x{effectLayout.ContentHeight:F0} @ {effectLayout.ContentX:F0},{effectLayout.ContentY:F0})");
                    }
                }

                if (useSnowfallOverlayEffect)
                {
                    snowfallOverlayRecipes = SnowfallOverlayPreset.ResolveRecipes(job, estimatedOutputDuration, onLog).ToList();
                    if (snowfallOverlayRecipes.Count == 0)
                        throw new InvalidOperationException("Snowfall overlay recipe was not prepared.");

                    var overlayProfile = ResolveOverlayCanvasProfile(job, targetResW, targetResH);
                    int effectWidth = overlayProfile.CanvasWidth;
                    int effectHeight = overlayProfile.CanvasHeight;
                    OverlayContentLayout effectLayout = overlayProfile.Layout;

                    for (int snowIndex = 0; snowIndex < snowfallOverlayRecipes.Count; snowIndex++)
                    {
                        SnowfallOverlayRecipe snowfallOverlayRecipe = snowfallOverlayRecipes[snowIndex];
                        if (snowfallOverlayRecipe.UseGeneratedOverlay)
                        {
                            SnowfallGeneratedOverlay snowfallOverlay = await EngineServices.Images!.GenerateSnowfallOverlayAsync(
                                effectWidth,
                                effectHeight,
                                effectLayout,
                                lightLeakBurnFps,
                                snowfallOverlayRecipe,
                                $"{Path.GetFileNameWithoutExtension(rawName)}_{job.Guid}_snow{snowIndex + 1}",
                                token,
                                onLog);

                            snowfallGeneratedOverlays.Add(snowfallOverlay);
                            tempTemplateArtifacts.AddRange(snowfallOverlay.TempArtifacts);
                            onLog($"[SNOWFALL] Generated snow overlay #{snowIndex + 1} @ {effectWidth}x{effectHeight} / {snowfallOverlay.FrameRate:F2}fps (content {effectLayout.ContentWidth:F0}x{effectLayout.ContentHeight:F0} @ {effectLayout.ContentX:F0},{effectLayout.ContentY:F0})");
                        }
                        else
                        {
                            snowfallGeneratedOverlays.Add(null);
                            await ValidateSnowfallOverlayAssetRecipeAsync($"Snowfall Overlay #{snowIndex + 1}", snowfallOverlayRecipe, token, onLog);
                        }
                    }
                }

                if (useRainOverlayEffect)
                {
                    rainOverlayRecipe = RainOverlayPreset.ResolveRecipe(job, onLog);
                    if (rainOverlayRecipe == null)
                        throw new InvalidOperationException("Rain overlay asset missing.");

                    await ValidateRainOverlayAssetRecipeAsync("Rain Overlay", rainOverlayRecipe, token, onLog);
                }

                if (useDreamyDotOverlay2Effect)
                {
                    dreamyDotOverlay2Recipe = DreamyDotOverlay2Preset.ResolveRecipe(onLog);
                    if (dreamyDotOverlay2Recipe == null)
                        throw new InvalidOperationException("Dreamy Dot Overlay 2 asset missing.");

                    await ValidateDreamyDotOverlay2AssetRecipeAsync("Dreamy Dot Overlay 2", dreamyDotOverlay2Recipe, token, onLog);
                }

                if (useLightLeakOverlayEffect)
                {
                    lightLeakOverlayRecipe = useRecipeLightLeakOverlayEffect && recipeOverlayEffect != null
                        ? EffectRecipeBridge.ResolveLightLeakOverlayRecipe(job, recipeOverlayEffect, estimatedOutputDuration, onLog)
                        : LightLeakOverlayPreset.ResolveRecipe(job, estimatedOutputDuration, onLog);
                    if (lightLeakOverlayRecipe == null)
                    {
                        throw new InvalidOperationException("Light leak asset missing.");
                    }

                    await ValidateLightLeakAssetRecipeAsync(
                        hasLightLeakFx ? "RÃƒÂ² phim (Leak 1 / Light Leak)" : "Light Leak Overlay",
                        lightLeakOverlayRecipe,
                        token,
                        onLog);
                    if (recipeParticleEffect != null &&
                        recipeParticleEffect.Id.Equals(recipeOverlayEffect?.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        onLog("[LIGHT-LEAK-OVERLAY] Asset found; keeping generated glow accent overlay.");
                    }
                }

                if (useRecipeGenericOverlayAssetEffect && recipeOverlayEffect != null)
                {
                    recipeOverlayAssetPath = EffectRecipeBridge.ResolveOverlayAssetPath(recipeOverlayEffect);
                    if (string.IsNullOrWhiteSpace(recipeOverlayAssetPath))
                    {
                        useRecipeGenericOverlayAssetEffect = false;
                        onLog($"[RECIPE-OVERLAY-WARN] Asset for {recipeOverlayEffect.Name} was not found. Skipping overlay recipe.");
                    }
                    else
                    {
                        onLog($"[RECIPE-OVERLAY] Using asset recipe {recipeOverlayEffect.Name} -> {recipeOverlayAssetPath}");
                    }
                }

                // ═══════════════════════════════════════════════════════════════
                // SOUNDBAR: audio analysis + transparent PNG sequence generation.
                // A soundbar failure must NEVER abort the render, so every exit path
                // here degrades to "no soundbar layer" and lets the video encode.
                // ═══════════════════════════════════════════════════════════════
                if (job.EnableSoundBar)
                {
                    try
                    {
                        SoundBarAudioSource soundBarSourceMode = (job.SoundBarAudioSource ?? "auto").Trim().ToLowerInvariant() switch
                        {
                            "external" => SoundBarAudioSource.External,
                            "video" => SoundBarAudioSource.Video,
                            "audio1" => SoundBarAudioSource.Audio1,
                            "audio2" => SoundBarAudioSource.Audio2,
                            _ => SoundBarAudioSource.Auto
                        };

                        // NOTE ON TEMP ROOTS - the soundbar uses TWO, on purpose.
                        //
                        // GetTitanTempDir() is %TEMP%\TitanEngineTemp, and every path registered in
                        // tempTemplateArtifacts is destroyed by the render's finally block. That makes
                        // it the right home for WORK: the decoded PCM below is work - one file, read
                        // once, thrown away - so it lives there and is registered for deletion.
                        //
                        // The PNG sequence cache and the picker preview cache are NOT work; they are
                        // caches whose whole value is surviving the render that produced them. They
                        // live under %TEMP%\TitanEngine\soundbar_cache and %TEMP%\TitanEngine\
                        // soundbar_preview, which nothing sweeps, and they are bounded by their own
                        // eviction (one entry per style) and pruning (48 PNGs) respectively rather
                        // than by an external sweeper. Putting a cache under GetTitanTempDir() would
                        // mean either not registering it for deletion (so the temp dir silently grows)
                        // or registering it (so the cache dies with the render that made it, which is
                        // the bug that gave it a 0 % hit rate).
                        //
                        // The render path drives the sequence generator at a FIXED 30 fps and
                        // SoundBarVisualizerPreset.ResolveFrameCount hard-caps the loop at 180
                        // frames, so 180/30 = 6.0 s is the real ceiling. The old 1.0-20.0 clamp
                        // advertised 14 s that no input could ever produce. See BAS_LOCALHOST_API_GUIDE.
                        double soundBarLoopSeconds = Math.Clamp(job.SoundBarLoopSeconds, SoundBarMinLoopSeconds, SoundBarMaxLoopSeconds);
                        string soundBarPcmDir = Path.Combine(GetTitanTempDir(), "soundbar_pcm");

                        soundBarAnalysis = await AudioSpectrumAnalyzer.AnalyzeAsync(
                            videoToEncode,
                            externalAudioPath,
                            soundBarSourceMode,
                            0.0,
                            soundBarLoopSeconds,
                            30,
                            soundBarPcmDir,
                            onLog,
                            token,
                            job.AudioPath,
                            job.SecondaryAudioPath);

                        if (soundBarAnalysis == null)
                        {
                            soundBarAnalysisFailed = true;
                            onLog("[SOUNDBAR] Audio analysis unavailable - soundbar layer skipped, render continues.");
                        }
                        else
                        {
                            // AudioSpectrumAnalyzer decodes the source audio to a GUID-named .raw under
                            // soundbarPcmDir and returns it as AudioFilePath. Every FAILURE path inside
                            // that analyzer deletes it; the SUCCESS path does not, and nothing downstream
                            // ever reads it (GenerateOverlayAsync works off analysis.Frames, which are
                            // already in memory). So one ~0.4-0.9 MB file survived every successful
                            // render. Register it with the render's temp-artifact list so the finally
                            // block removes it like everything else - fixed from here so the analyzer
                            // itself stays untouched.
                            if (!string.IsNullOrWhiteSpace(soundBarAnalysis.AudioFilePath))
                            {
                                tempTemplateArtifacts.Add(soundBarAnalysis.AudioFilePath);
                                onLog($"[SOUNDBAR] Decoded PCM registered for cleanup: {soundBarAnalysis.AudioFilePath}");
                            }

                            // ───────────────────────────────────────────────────────────
                            // TIMELINE AUDIBILITY GATE
                            //
                            // The analyser deliberately picks a LOUD window for the loop (otherwise
                            // frame 0 is a fade-in and every repeat drops to a flat line). That is
                            // right for the loop's own shape and wrong for the render: a video whose
                            // music starts at 0:18 showed the loud loop dancing over 18 s of true
                            // digital silence - measured on a real output, frames at 2 s and 7 s were
                            // pixel-identical while the audio was -91 dBFS. The analyser's own
                            // audibility gate cannot see this because it only ever looks at the loud
                            // window, never at the timeline.
                            //
                            // So scan the FULL source once and tell ffmpeg to composite the overlay
                            // only where that source is actually audible. The scan is best-effort:
                            // null means "could not measure" and keeps the old always-on behaviour.
                            // ───────────────────────────────────────────────────────────
                            double videoAudioSpeed = Math.Clamp(job.SourceAudioSpeed, 0.10, 8.0);
                            if (Math.Abs(normalizedSpeed - 1.0) > 0.0001 && job.SlowMotionAudio)
                                videoAudioSpeed = Math.Clamp(videoAudioSpeed * normalizedSpeed, 0.10, 8.0);

                            double externalAudioSpeed = Math.Clamp(job.ExternalAudioSpeed, 0.10, 8.0);
                            bool gateFollowsVideo = ResolveFullPathSafe(soundBarAnalysis.SourcePath).Equals(
                                ResolveFullPathSafe(videoToEncode),
                                StringComparison.OrdinalIgnoreCase);
                            double soundBarSourceSpeed = gateFollowsVideo ? videoAudioSpeed : externalAudioSpeed;
                            double soundBarSourceToOutputScale = 1.0 / Math.Max(0.10, soundBarSourceSpeed);

                            soundBarAudibleIntervals = await AudioSpectrumAnalyzer.AnalyzeAudibleIntervalsAsync(
                                soundBarAnalysis.SourcePath,
                                estimatedOutputDuration,
                                soundBarSourceToOutputScale,
                                onLog,
                                token);

                            // Every clamp below is the DOCUMENTED contract range, and each one is the
                            // range the renderer actually consumes. Anything wider advertised values
                            // that clamp to something identical, which is worse than not offering them:
                            //   Sensitivity -> SoundBarStyleRenderer clamps 0.0-3.0
                            //   Smoothing   -> SoundBarStyleRenderer clamps 0.0-1.0 and derives the
                            //                  one-pole coefficient k = 1 - 0.85 * smoothing, so every
                            //                  value above 1.0 collapsed onto the identical k = 0.15
                            //   RingCount   -> dual-rings clamps 2-6, ripple-rings 1-8, everything else
                            //                  ignores it; 8 is the widest real clamp
                            var sbSettings = new SoundBarSettings
                            {
                                Style = SoundBarVisualizerPreset.FindStyle(job.SoundBarStyle)?.Id ?? SoundBarStyleId.NeonBars,
                                // Empty palette = "not chosen": audio-ring defaults to the yellow of
                                // its NCS reference, everything else keeps cyan. An explicit palette
                                // always wins.
                                PaletteId = string.IsNullOrWhiteSpace(job.SoundBarPalette)
                                    ? (SoundBarVisualizerPreset.FindStyle(job.SoundBarStyle)?.Id == SoundBarStyleId.AudioRing
                                        ? "ncs-house-yellow"
                                        : "ncs-cyan")
                                    : job.SoundBarPalette,
                                XPercent = Math.Clamp(job.SoundBarXPercent, 0.0, 1.0),
                                YPercent = Math.Clamp(job.SoundBarYPercent, 0.0, 1.0),
                                WidthPercent = Math.Clamp(job.SoundBarWidthPercent, 0.02, 1.0),
                                HeightPercent = Math.Clamp(job.SoundBarHeightPercent, 0.02, 1.0),
                                Opacity = Math.Clamp(job.SoundBarOpacity, SoundBarMinOpacity, SoundBarMaxOpacity),
                                Sensitivity = Math.Clamp(job.SoundBarSensitivity, SoundBarMinSensitivity, SoundBarMaxSensitivity),
                                Smoothing = Math.Clamp(job.SoundBarSmoothing, SoundBarMinSmoothing, SoundBarMaxSmoothing),
                                BarCount = Math.Clamp(job.SoundBarBarCount, SoundBarMinBarCount, SoundBarMaxBarCount),
                                RingCount = Math.Clamp(job.SoundBarRingCount, SoundBarMinRingCount, SoundBarMaxRingCount),
                                Roundness = Math.Clamp(job.SoundBarRoundness, 0.0, 1.0),
                                GlowIntensity = Math.Clamp(job.SoundBarGlowIntensity, 0.0, 3.0),
                                LoopSeconds = soundBarLoopSeconds,
                                FrameRate = 30.0
                            };

                            var sbPalette = ColorPresets.GetOrDefault(sbSettings.PaletteId);
                            var sbProfile = ResolveOverlayCanvasProfile(job, targetResW, targetResH);
                            string soundBarTag = $"{Path.GetFileNameWithoutExtension(rawName)}_{job.Guid}";

                            soundBarOverlay = await EngineServices.Images!.GenerateSoundBarOverlayAsync(
                                soundBarAnalysis,
                                sbSettings,
                                sbPalette,
                                sbProfile.CanvasWidth,
                                sbProfile.CanvasHeight,
                                soundBarTag,
                                onLog,
                                token);

                            if (soundBarOverlay == null)
                            {
                                soundBarAnalysisFailed = true;
                                onLog("[SOUNDBAR] Overlay generation failed - soundbar layer skipped.");
                            }
                            else
                            {
                                onLog($"[SOUNDBAR] Generated {soundBarOverlay.FrameCount} frames @ {sbProfile.CanvasWidth}x{sbProfile.CanvasHeight} / {soundBarOverlay.FrameRate:F2}fps (loop {soundBarLoopSeconds:F1}s, style={job.SoundBarStyle}, palette={sbSettings.PaletteId})");
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        soundBarAnalysisFailed = true;
                        soundBarOverlay = null;
                        onLog($"[SOUNDBAR-ERROR] {ex.Message} - soundbar layer skipped, render continues.");
                    }
                }

                // [MODIFIED] BUILD VIDEO FILTER - Skip scaling if Upscayl was used
                StringBuilder filterChain = new StringBuilder();

                bool hasCropFx = HasCropEffect(job);

                if (hasCropFx)
                {
                    filterChain.Append(BuildCropFilter(job, onLog));
                }

                if (!upscalePreprocessedExternally)
                {
                    bool upscaleRequested =
                        !string.IsNullOrWhiteSpace(job.UpscaleMode) &&
                        !job.UpscaleMode.Contains("Off", StringComparison.OrdinalIgnoreCase);

                    // Prefer internal DLSS/FSR-style pipeline when Upscale mode is ON.
                    if (upscaleRequested)
                    {
                        int upscaleTargetW = targetResW;
                        int upscaleTargetH = targetResH;

                        if (upscaleTargetW <= 0 || upscaleTargetH <= 0)
                        {
                            int sourceW = job.SourceWidth;
                            int sourceH = job.SourceHeight;

                            if (sourceW <= 0 || sourceH <= 0)
                            {
                                (sourceW, sourceH) = await GetVideoResolutionAsync(videoToEncode);
                            }

                            double upscaleFactor = job.UpscaleMode.Contains("4x", StringComparison.OrdinalIgnoreCase) ? 4.0 : 2.0;
                            upscaleTargetW = Math.Max(2, (int)Math.Round(sourceW * upscaleFactor));
                            upscaleTargetH = Math.Max(2, (int)Math.Round(sourceH * upscaleFactor));
                            if ((upscaleTargetW & 1) != 0) upscaleTargetW += 1;
                            if ((upscaleTargetH & 1) != 0) upscaleTargetH += 1;

                            onLog($"[UPSCALE-AUTO] Derived target from source: {sourceW}x{sourceH} -> {upscaleTargetW}x{upscaleTargetH}");
                            targetResW = upscaleTargetW;
                            targetResH = upscaleTargetH;
                        }

                        string upscaleFilter = BuildAdvancedScaleFilter(
                            upscaleTargetW,
                            upscaleTargetH,
                            job.UpscaleMode,
                            job.SharpnessIntensity,
                            onLog);

                        if (!string.IsNullOrWhiteSpace(upscaleFilter))
                        {
                            if (filterChain.Length > 0) filterChain.Append(",");
                            filterChain.Append(upscaleFilter);
                        }
                    }
                    else if (hasCropFx &&
                             job.Resolution.Contains("Original", StringComparison.OrdinalIgnoreCase) &&
                             targetResW > 0 &&
                             targetResH > 0)
                    {
                        string scaleFilter = $"scale={targetResW}:{targetResH}:flags=lanczos";
                        if (filterChain.Length > 0) filterChain.Append(",");
                        filterChain.Append(scaleFilter);
                        onLog($"[FILTER-RESOLUTION] Crop active in Original mode -> scaling back to {targetResW}x{targetResH} before overlays");
                    }
                    else if (!job.Resolution.Contains("Original"))
                    {
                        string scaleFilter = "";

                        if (job.Resolution.Contains("x"))
                        {
                            Match match = Regex.Match(job.Resolution, @"(\d+)x(\d+)");
                            if (match.Success)
                            {
                                if (int.TryParse(match.Groups[1].Value, out int w) && int.TryParse(match.Groups[2].Value, out int h))
                                {
                                    scaleFilter = $"scale={w}:{h}:flags=lanczos";
                                    onLog($"[FILTER-RESOLUTION] Basic scaling to {w}x{h}");
                                }
                            }
                        }
                        else if (job.Resolution.Contains("p"))
                        {
                            Match match = Regex.Match(job.Resolution, @"(\d+)p");
                            if (match.Success)
                            {
                                if (int.TryParse(match.Groups[1].Value, out int height))
                                {
                                    scaleFilter = $"scale=-2:{height}:flags=lanczos";
                                    onLog($"[FILTER-RESOLUTION] Basic scaling to -2:{height}");
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(scaleFilter))
                        {
                            if (filterChain.Length > 0) filterChain.Append(",");
                            filterChain.Append(scaleFilter);
                        }
                    }
                }
                else
                {
                    onLog("[FILTER-RESOLUTION] Skipping internal upscale filter - video already upscaled externally");
                }

                // IMAGE FAST-PATH PRESCALE. The looped still is re-decoded by the image2
                // demuxer on EVERY output frame, so a large source photo pays its full decode
                // ~30x per second. When the chain below just appended a plain resolution
                // scale, mirror that exact expression once into a small PNG and swap the
                // input to it - the chain's own scale then runs over identical pixels at
                // identical dimensions. "Original" resolution (no chain scale) and crop-FX
                // jobs keep the raw still: there the prescale would ADD a resample the graph
                // never performs.
                if (singleImageFastPath && !hasCropFx && !job.Resolution.Contains("Original"))
                {
                    string prescaleExpression;
                    int prescaleW = targetResW, prescaleH = targetResH;
                    if (targetResW > 0 && targetResH > 0)
                        prescaleExpression = $"scale={targetResW}:{targetResH}:flags=lanczos";
                    else if (targetResW == -2 && targetResH > 0)
                        prescaleExpression = $"scale=-2:{targetResH}:flags=lanczos";
                    else
                        prescaleExpression = string.Empty;

                    if (!string.IsNullOrEmpty(prescaleExpression))
                    {
                        string? prescaled = await TryPrescaleStillForLoopAsync(
                            videoToEncode, prescaleExpression, prescaleW, prescaleH, outDir, onLog, token);
                        if (!string.IsNullOrWhiteSpace(prescaled))
                        {
                            tempTemplateArtifacts.Add(prescaled);
                            string loopInput = $"-i \"{videoToEncode}\"";
                            string prescaledInput = $"-i \"{prescaled}\"";
                            if (cmd.ToString().Contains(loopInput, StringComparison.Ordinal))
                            {
                                cmd.Replace(loopInput, prescaledInput);
                                // postInputInsertPoint is a NUMERIC offset captured when the
                                // inputs were appended; the generated-overlay branches Insert()
                                // their -i args at it LATER (the soundbar section below runs
                                // after this block). The prescaled path is longer than the raw
                                // still's, so without shifting the offset by the delta every
                                // later Insert lands mid-token inside the audio input's path -
                                // which ffmpeg rejects with exit -22 (measured, not theorised).
                                postInputInsertPoint += prescaledInput.Length - loopInput.Length;
                            }
                        }
                    }
                }

                // Add Motion Editor (60fps & RSMB)
                if (job.Enable60fps)
                {
                    if (filterChain.Length > 0) filterChain.Append(",");
                    filterChain.Append("minterpolate='mi_mode=mci:mc_mode=aobmc:vsbmc=1:fps=60'");
                    onLog("[FILTER-MOTION] AI 60fps Interpolation (Optical Flow) applied");
                }

                if (job.RsmbIntensity > 0.01)
                {
                    if (filterChain.Length > 0) filterChain.Append(",");
                    // tmix creates a cinematic motion blur by blending adjacent frames
                    int frames = (int)Math.Max(2, Math.Round(2 + job.RsmbIntensity * 6)); // 2 to 8 frames
                    filterChain.Append($"tmix=frames={frames}");
                    onLog($"[FILTER-MOTION] RSMB Motion Blur applied (Intensity: {job.RsmbIntensity:F1}, Blend: {frames} frames)");
                }

                // Add FX effects
                if (normalizedFxEffect != "None" && !string.IsNullOrEmpty(normalizedFxEffect))
                {
                    if (normalizedFxEffect.Equals("Flip Horizontal (Anti-Copyright)", StringComparison.OrdinalIgnoreCase))
                    {
                        if (filterChain.Length > 0) filterChain.Append(",");
                        filterChain.Append("hflip");
                        onLog($"[FILTER-FX] hflip applied");
                    }
                    else if (hasLightLeakFx)
                    {
                        onLog("[FILTER-FX] Leak 1 / Light Leak is asset-only and will be applied through the overlay asset graph.");
                    }
                    else if (hasLightLeakBurnFx && !lightLeakBurnTransitionBaked)
                    {
                        onLog("[FILTER-FX] Light Leak Burn will use generated full-res burn mask + overlay layers.");
                    }
                    else if (hasScratchVideoFx)
                    {
                        onLog($"[FILTER-FX] Dust & Scratches will use generated animated overlay (intensity={job.FxIntensity:F0}%).");
                    }
                    else if (normalizedFxEffect.Equals("Cinematic Glow", StringComparison.OrdinalIgnoreCase))
                    {
                        if (filterChain.Length > 0) filterChain.Append(",");
                        string glowFilter = BuildCinematicGlowFxFilter(job, onLog);
                        filterChain.Append(glowFilter);
                    }
                    else if (normalizedFxEffect.Equals("Halo Glow", StringComparison.OrdinalIgnoreCase))
                    {
                        if (filterChain.Length > 0) filterChain.Append(",");
                        string haloFilter = BuildHaloGlowFxFilter(job, onLog);
                        filterChain.Append(haloFilter);
                    }
                    else if (normalizedFxEffect.Equals("Prism Light", StringComparison.OrdinalIgnoreCase))
                    {
                        if (filterChain.Length > 0) filterChain.Append(",");
                        string prismFilter = BuildPrismLightFxFilter(job, onLog);
                        filterChain.Append(prismFilter);
                    }
                    else if (normalizedFxEffect.Equals("Editorial Bloom", StringComparison.OrdinalIgnoreCase))
                    {
                        if (filterChain.Length > 0) filterChain.Append(",");
                        string bloomFilter = BuildEditorialBloomFxFilter(job, onLog);
                        filterChain.Append(bloomFilter);
                    }
                }

                if (useRgbPolaroidLedEffect)
                {
                    if (filterChain.Length > 0) filterChain.Append(",");
                    string polaroidBaseFilter = BuildPolaroidScrapbookFxFilterCore(job, includeRgbBorder: true, includeDecorations: true);
                    filterChain.Append(polaroidBaseFilter);
                    onLog($"[FILTER-FX] RGB Polaroid base scrapbook frame enabled (LED overlay handled separately, intensity={job.FxIntensity:F0}%)");
                }
                else if (useDashedPolaroidEffect)
                {
                    onLog($"[FILTER-FX] Dashed Polaroid overlay only (no solid base frame, no tape/sticky note decorations, intensity={job.FxIntensity:F0}%)");
                }
                else if (usePolaroidScrapbookEffect)
                {
                    if (filterChain.Length > 0) filterChain.Append(",");
                    string polaroidFilter = BuildPolaroidScrapbookFxFilter(job, onLog);
                    filterChain.Append(polaroidFilter);
                }

                // Add slow motion (video)
                if (Math.Abs(normalizedSpeed - 1.0) > 0.0001)
                {
                    if (filterChain.Length > 0) filterChain.Append(",");
                    string slowMotionVideoFilter = BuildPlaybackSpeedVideoFilter(normalizedSpeed, onLog);
                    filterChain.Append(slowMotionVideoFilter);
                }

                if (recipeTransitionEffect != null)
                {
                    int transitionWidth = Math.Max(2, targetResW > 0 ? targetResW : Math.Max(2, job.SourceWidth));
                    int transitionHeight = Math.Max(2, targetResH > 0 ? targetResH : Math.Max(2, job.SourceHeight));
                    string transitionFilter = TransitionRecipeGraphBuilder.BuildFilter(
                        recipeTransitionEffect,
                        transitionWidth,
                        transitionHeight,
                        estimatedOutputDuration,
                        job.FxIntensity);
                    if (!string.IsNullOrWhiteSpace(transitionFilter))
                    {
                        if (filterChain.Length > 0) filterChain.Append(",");
                        filterChain.Append(transitionFilter);
                        onLog($"[FILTER-TRANSITION] {recipeTransitionEffect.Name} applied as engine-side motion pulse.");
                    }
                }

                if (recipePostProcessEffect != null)
                {
                    int postProcessWidth = Math.Max(2, targetResW > 0 ? targetResW : Math.Max(2, job.SourceWidth));
                    int postProcessHeight = Math.Max(2, targetResH > 0 ? targetResH : Math.Max(2, job.SourceHeight));
                    string recipePostProcessFilter = PostProcessEffectGraphBuilder.BuildFilterChain(
                        recipePostProcessEffect,
                        job,
                        postProcessWidth,
                        postProcessHeight,
                        estimatedOutputDuration,
                        onLog);
                    if (!string.IsNullOrWhiteSpace(recipePostProcessFilter))
                    {
                        if (filterChain.Length > 0) filterChain.Append(",");
                        filterChain.Append(recipePostProcessFilter);
                    }
                }

                // Add color grading
                if (!string.IsNullOrWhiteSpace(job.ColorFilter) &&
                    !job.ColorFilter.StartsWith("None", StringComparison.OrdinalIgnoreCase))
                {
                    double intensity = job.ColorIntensity;
                    onLog($"[FILTER-COLOR-DEBUG] Raw intensity: {intensity}, ColorFilter: {job.ColorFilter}");
                    
                    if (intensity > 0.01)
                    {
                        double actualIntensity = Math.Min(intensity * 1.5, 1.0);
                        if (hasStylizedLightFx)
                        {
                            // Prevent over-saturated wash when light/glow FX is enabled together with color grading.
                            actualIntensity = Math.Min(actualIntensity, 0.60);
                            onLog("[FILTER-COLOR] Intensity clamped for light/glow FX compatibility.");
                        }
                        onLog($"[FILTER-COLOR-DEBUG] Actual intensity after boost: {actualIntensity:F3}");
                        
                        string baseColorCmd = EngineCore.GetColorFilterCmd(job.ColorFilter);
                        onLog($"[FILTER-COLOR-DEBUG] Base filter: {baseColorCmd}");
                        
                        if (!string.IsNullOrEmpty(baseColorCmd))
                        {
                            string colorCmd = ScaleFilterIntensity(baseColorCmd, actualIntensity);
                            onLog($"[FILTER-COLOR-DEBUG] After scaling: {colorCmd}");
                            
                            if (!string.IsNullOrEmpty(colorCmd))
                            {
                                if (filterChain.Length > 0) filterChain.Append(",");
                                filterChain.Append(colorCmd);
                                onLog($"[FILTER-COLOR] {job.ColorFilter} applied @ {intensity * 100:F0}% (strength: {actualIntensity * 100:F0}%)");
                            }
                        }
                    }
                    else
                    {
                        onLog($"[FILTER-COLOR] {job.ColorFilter} skipped (0% intensity)");
                    }
                }
                             // Text Overlay (Typewriter & Aesthetic Subtitle Overlay)
                if (!string.IsNullOrWhiteSpace(job.TextOverlay))
                {
                    string textPos = job.TextOverlayPosition?.Trim()?.ToLowerInvariant() ?? "center";
                    string? textAlign = job.TextAlign?.Trim()?.ToLowerInvariant();
                    
                    if (string.IsNullOrEmpty(textAlign) || textAlign.Contains("auto"))
                    {
                        if (textPos.Contains("left")) textAlign = "left";
                        else if (textPos.Contains("right")) textAlign = "right";
                        else textAlign = "center";
                    }
                    
                    string anchorX = "(w/2)";
                    string anchorY = "(h/2)";

                    bool hasExplicitCoordinates = job.TextOverlayXPercent.HasValue ||
                        job.TextOverlayYPercent.HasValue ||
                        textPos.Contains("custom");

                    if (hasExplicitCoordinates)
                    {
                        double rawX = job.TextOverlayXPercent ?? 50.0;
                        double rawY = job.TextOverlayYPercent ?? 85.0;

                        double xPct = NormalizeCoordinatePercent(rawX, 50.0);
                        double yPct = NormalizeCoordinatePercent(rawY, 85.0);

                        var invCulture = System.Globalization.CultureInfo.InvariantCulture;
                        anchorX = $"(w*{xPct.ToString("F3", invCulture)})";
                        anchorY = $"(h*{yPct.ToString("F3", invCulture)})";
                    }
                    else
                    {
                        if (textPos.Contains("left")) anchorX = "80";
                        else if (textPos.Contains("right")) anchorX = "(w-80)";
                        else anchorX = "(w/2)";

                        if (textPos.Contains("bottom")) anchorY = "(h-120)";
                        else if (textPos.Contains("top")) anchorY = "120";
                        else anchorY = "(h/2)";
                    }

                    int fontSize = job.TextOverlayFontSize ?? 28;
                    string color = string.IsNullOrWhiteSpace(job.TextOverlayColor) ? "white" : job.TextOverlayColor;

                    string fontStyle = job.TextOverlayStyle ?? "Aesthetic Lyric (Georgia Italic)";
                    bool addQuotes = job.AddQuotes;

                    // Two rendering paths share the same anchors:
                    //  - legacy drawtext: typewriter + upward scroll + neon aura
                    //  - animated drawtext: the Caption Sync effect set, rebuilt
                    //    on FFmpeg expressions so no ASS track is required
                    bool legacyOverlay = UsesLegacyDrawtextTextOverlay(job.TextOverlayAnimation);
                    string typewriterFilter = legacyOverlay
                        ? BuildTypewriterTextOverlay(
                            job.TextOverlay, anchorX, anchorY, fontSize, color, fontStyle, addQuotes, 35.0,
                            job.DisableTextScroll, job.EnableTextGlow, job.TextGlowColor, textAlign, textPos, onLog)
                        : BuildAnimatedTextOverlay(
                            job.TextOverlay, anchorX, anchorY, fontSize, color, fontStyle, addQuotes,
                            job.TextOverlayAnimation ?? string.Empty, textAlign, textPos,
                            job.TextOverlayEffectDuration, onLog);

                    if (!string.IsNullOrWhiteSpace(typewriterFilter))
                    {
                        if (filterChain.Length > 0) filterChain.Append(",");
                        filterChain.Append(typewriterFilter);
                        onLog($"[FILTER-TEXT] Text overlay: '{job.TextOverlay}' at {textPos} " +
                              $"(Mode={(legacyOverlay ? "LegacyTypewriter" : job.TextOverlayAnimation)}, " +
                              $"Static={job.DisableTextScroll}, Glow={job.EnableTextGlow})");
                    }
                }

                if (!string.IsNullOrEmpty(captionAssPath))
                {
                    // Giai đoạn 3: escape path qua FfmpegPathEscape (Windows = thuật toán gốc nguyên vẹn).
                    string escapedAssPath = FfmpegPathEscape.EscapeForFilterOption(captionAssPath);
                    string fontsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Fonts");
                    string escapedFontsDir = FfmpegPathEscape.EscapeForFilterOption(fontsDir);
                    string subtitleFilter = $"ass='{escapedAssPath}':fontsdir='{escapedFontsDir}'";

                    if (filterChain.Length > 0) filterChain.Append(",");
                    filterChain.Append(subtitleFilter);
                    onLog("[CAPTION-SYNC] ✅ Captions attached to main render pipeline.");
                }

                string brightnessFilter = BuildBrightnessFilter(job.VideoBrightness, onLog);
                if (!string.IsNullOrWhiteSpace(brightnessFilter))
                {
                    if (filterChain.Length > 0) filterChain.Append(",");
                    filterChain.Append(brightnessFilter);
                }

                string fadeVideoFilter = BuildFadeVideoFilter(fadeSettings, onLog);
                double sourceAudioSpeed = Math.Clamp(job.SourceAudioSpeed, 0.10, 8.0);
                if (Math.Abs(sourceAudioSpeed - job.SourceAudioSpeed) > 0.0001)
                {
                    onLog($"[FILTER-SOURCE-AUDIO-WARN] Requested source audio speed {job.SourceAudioSpeed:F3}x is invalid. Using {sourceAudioSpeed:F3}x.");
                }

                bool hasMixedAudio = hasExternalAudio && hasSourceAudio;
                bool hasAnyAudio = hasSourceAudio || hasExternalAudio;

                string sourceAudioInputChain = hasSourceAudio
                    ? BuildSourceAudioInputFilterChain(job, normalizedSpeed, sourceAudioSpeed, job.SlowMotionAudio, onLog)
                    : string.Empty;
                string externalAudioInputChain = hasExternalAudio
                    ? BuildExternalAudioInputFilterChain(job, onLog, externalAudioVolumePreApplied, externalAudioVolumeOverride)
                    : string.Empty;
                string audioPostProcessChain = hasAnyAudio
                    ? BuildAudioPostProcessFilterChain(job, fadeSettings, onLog)
                    : string.Empty;

                double audioVolume = hasAnyAudio ? Math.Clamp(job.AudioVolume, 0.0, 2.0) : 1.0;
                double videoVolume = Math.Clamp(job.VideoVolume, 0.0, 2.0);
                bool needsAudioMixing = hasMixedAudio;

                string AppendOptionalRecipeParticleGraph(string baseInputLabel, string outputLabel, ref int nextOverlayInputIndex)
                {
                    if (!useRecipeParticleOverlayEffect)
                        return string.Empty;

                    if (recipeParticleOverlay == null || recipeParticleEffect == null)
                        throw new InvalidOperationException("Recipe particle overlay was not prepared.");

                    string particleInputArgs =
                        $"-f image2 -pattern_type sequence -start_number 1 -framerate {recipeParticleOverlay.FrameRate.ToString("F3", CultureInfo.InvariantCulture)} -i \"{recipeParticleOverlay.OverlayPattern}\" ";
                    cmd.Insert(postInputInsertPoint, particleInputArgs);
                    postInputInsertPoint += particleInputArgs.Length;

                    string graph = OverlayEffectGraphBuilder.BuildLoopedSequenceClipGraph(
                        baseInputLabel,
                        string.Empty,
                        $"[{nextOverlayInputIndex}:v]",
                        EffectRecipeBridge.CreateGeneratedOverlayRecipe(recipeParticleEffect, job),
                        recipeParticleOverlay.FrameCount,
                        recipeParticleOverlay.FrameRate,
                        estimatedOutputDuration,
                        outputLabel,
                        onLog);

                    nextOverlayInputIndex++;
                    return graph;
                }

                string AppendOptionalLightLeakAssetGraph(string baseInputLabel, string outputLabel, ref int nextOverlayInputIndex)
                {
                    if (!useLightLeakOverlayEffect || lightLeakOverlayRecipe == null)
                        return string.Empty;

                    string lightLeakInputArgs =
                        $"{(lightLeakOverlayRecipe.Asset.CanLoop ? "-stream_loop -1 " : string.Empty)}-i \"{lightLeakOverlayRecipe.Asset.FilePath}\" ";
                    cmd.Insert(postInputInsertPoint, lightLeakInputArgs);
                    postInputInsertPoint += lightLeakInputArgs.Length;

                    var profile = ResolveOverlayCanvasProfile(job, targetResW, targetResH);
                    string graph = LightLeakOverlayPreset.BuildSingleClipGraph(
                        baseInputLabel,
                        string.Empty,
                        $"[{nextOverlayInputIndex}:v]",
                        lightLeakOverlayRecipe,
                        profile.CanvasWidth,
                        profile.CanvasHeight,
                        lightLeakBurnFps,
                        estimatedOutputDuration,
                        outputLabel,
                        onLog);

                    nextOverlayInputIndex++;
                    return graph;
                }

                string AppendOptionalRainGraph(string baseInputLabel, string outputLabel, ref int nextOverlayInputIndex)
                {
                    if (!useRainOverlayEffect)
                        return string.Empty;

                    if (rainOverlayRecipe == null)
                        throw new InvalidOperationException("Rain overlay asset recipe was not prepared.");

                    string rainOverlayInputArgs =
                        $"{(rainOverlayRecipe.Asset.CanLoop ? "-stream_loop -1 " : string.Empty)}-i \"{rainOverlayRecipe.Asset.FilePath}\" ";
                    cmd.Insert(postInputInsertPoint, rainOverlayInputArgs);
                    postInputInsertPoint += rainOverlayInputArgs.Length;

                    var overlayProfile = ResolveOverlayCanvasProfile(job, targetResW, targetResH);
                    string graph = RainOverlayPreset.BuildSingleClipGraph(
                        baseInputLabel,
                        string.Empty,
                        $"[{nextOverlayInputIndex}:v]",
                        rainOverlayRecipe,
                        overlayProfile.CanvasWidth,
                        overlayProfile.CanvasHeight,
                        lightLeakBurnFps,
                        estimatedOutputDuration,
                        outputLabel,
                        onLog);

                    nextOverlayInputIndex++;
                    return graph;
                }

                string BuildSnowfallGraphChain(string baseInputLabel, string baseVideoFilters, string outputLabel, ref int nextOverlayInputIndex)
                {
                    if (!useSnowfallOverlayEffect)
                        return string.Empty;

                    if (snowfallOverlayRecipes.Count == 0)
                        throw new InvalidOperationException("Snowfall overlay recipe was not prepared.");

                    var graphParts = new List<string>();
                    string currentInputLabel = baseInputLabel;
                    string currentBaseFilters = baseVideoFilters;

                    for (int snowIndex = 0; snowIndex < snowfallOverlayRecipes.Count; snowIndex++)
                    {
                        SnowfallOverlayRecipe snowfallRecipe = snowfallOverlayRecipes[snowIndex];
                        bool isLastSnowStage = snowIndex == snowfallOverlayRecipes.Count - 1;
                        string stageOutputLabel = isLastSnowStage
                            ? outputLabel
                            : $"snowstack{snowIndex + 1}";

                        if (snowfallRecipe.UseGeneratedOverlay)
                        {
                            SnowfallGeneratedOverlay? generatedSnowfallOverlay =
                                snowIndex < snowfallGeneratedOverlays.Count ? snowfallGeneratedOverlays[snowIndex] : null;
                            if (generatedSnowfallOverlay == null)
                                throw new InvalidOperationException("Snowfall generated overlay frames were not prepared.");

                            string snowOverlayInputArgs =
                                $"-f image2 -pattern_type sequence -start_number 1 -framerate {generatedSnowfallOverlay.FrameRate.ToString("F3", CultureInfo.InvariantCulture)} -i \"{generatedSnowfallOverlay.OverlayPattern}\" ";
                            cmd.Insert(postInputInsertPoint, snowOverlayInputArgs);
                            postInputInsertPoint += snowOverlayInputArgs.Length;

                            graphParts.Add(SnowfallOverlayPreset.BuildGeneratedClipGraph(
                                currentInputLabel,
                                currentBaseFilters,
                                $"[{nextOverlayInputIndex}:v]",
                                generatedSnowfallOverlay.FrameCount,
                                generatedSnowfallOverlay.FrameRate,
                                estimatedOutputDuration,
                                stageOutputLabel,
                                onLog));
                        }
                        else
                        {
                            string assetSnowOverlayInputArgs =
                                $"{(snowfallRecipe.Asset?.CanLoop == true ? "-stream_loop -1 " : string.Empty)}-i \"{snowfallRecipe.Asset!.FilePath}\" ";
                            cmd.Insert(postInputInsertPoint, assetSnowOverlayInputArgs);
                            postInputInsertPoint += assetSnowOverlayInputArgs.Length;

                            var snowOverlayProfile = ResolveOverlayCanvasProfile(job, targetResW, targetResH);
                            graphParts.Add(SnowfallOverlayPreset.BuildAssetClipGraph(
                                currentInputLabel,
                                currentBaseFilters,
                                $"[{nextOverlayInputIndex}:v]",
                                snowfallRecipe,
                                snowOverlayProfile.CanvasWidth,
                                snowOverlayProfile.CanvasHeight,
                                lightLeakBurnFps,
                                estimatedOutputDuration,
                                stageOutputLabel,
                                onLog));
                        }

                        currentInputLabel = $"[{stageOutputLabel}]";
                        currentBaseFilters = string.Empty;
                        nextOverlayInputIndex++;
                    }

                    return string.Join(";", graphParts.Where(part => !string.IsNullOrWhiteSpace(part)));
                }

                string AppendOptionalSnowfallGraph(string baseInputLabel, string outputLabel, ref int nextOverlayInputIndex)
                {
                    return BuildSnowfallGraphChain(baseInputLabel, string.Empty, outputLabel, ref nextOverlayInputIndex);
                }

                string AppendOptionalScratchGraph(string baseInputLabel, string outputLabel, ref int nextOverlayInputIndex)
                {
                    if (!useScratchVideoEffect)
                        return string.Empty;

                    if (scratchVideoOverlay == null)
                        throw new InvalidOperationException("Dust & Scratches overlay frames were not prepared.");

                    string scratchOverlayInputArgs =
                        $"-f image2 -pattern_type sequence -start_number 1 -framerate {scratchVideoOverlay.FrameRate.ToString("F3", CultureInfo.InvariantCulture)} -i \"{scratchVideoOverlay.OverlayPattern}\" ";
                    cmd.Insert(postInputInsertPoint, scratchOverlayInputArgs);
                    postInputInsertPoint += scratchOverlayInputArgs.Length;

                    string graph = ScratchVideoPreset.BuildSingleClipGraph(
                        baseInputLabel,
                        string.Empty,
                        $"[{nextOverlayInputIndex}:v]",
                        scratchVideoOverlay.FrameCount,
                        scratchVideoOverlay.FrameRate,
                        estimatedOutputDuration,
                        outputLabel,
                        onLog);

                    nextOverlayInputIndex++;
                    return graph;
                }

                string AppendOptionalDreamyDotGraph(string baseInputLabel, string outputLabel, ref int nextOverlayInputIndex)
                {
                    if (!useDreamyDotOverlayEffect)
                        return string.Empty;

                    if (dreamyDotOverlay == null)
                        throw new InvalidOperationException("Dreamy Dot overlay frames were not prepared.");
                    if (string.IsNullOrWhiteSpace(dreamyDotOverlay.PreRenderedAssetPath) || !File.Exists(dreamyDotOverlay.PreRenderedAssetPath))
                        throw new InvalidOperationException("Dreamy Dot overlay asset was not prepared.");

                    string dreamyDotOverlayInputArgs = $"-stream_loop -1 -i \"{dreamyDotOverlay.PreRenderedAssetPath}\" ";
                    cmd.Insert(postInputInsertPoint, dreamyDotOverlayInputArgs);
                    postInputInsertPoint += dreamyDotOverlayInputArgs.Length;

                    string graph = DreamyDotOverlayPreset.BuildAssetClipGraph(
                        baseInputLabel,
                        string.Empty,
                        $"[{nextOverlayInputIndex}:v]",
                        dreamyDotOverlay,
                        targetResW,
                        targetResH,
                        lightLeakBurnFps,
                        estimatedOutputDuration,
                        outputLabel,
                        onLog);

                    nextOverlayInputIndex++;
                    return graph;
                }

                // ═══════════════════════════════════════════════════════════════
                // SOUNDBAR: reserve the soundbar input slot, then expose the graph
                // builder every overlay branch below composes into its own chain.
                //
                // Because the -i arguments are appended to `cmd` in chronological order
                // at `postInputInsertPoint`, inserting here - before the big if/else
                // chain - makes the soundbar the FIRST generated-overlay input. Advancing
                // generatedOverlayInputBaseIndex immediately afterwards guarantees every
                // branch allocates an index strictly greater than soundBarInputIndex.
                // ═══════════════════════════════════════════════════════════════
                if (job.EnableSoundBar && soundBarOverlay != null && !soundBarAnalysisFailed)
                {
                    try
                    {
                        string soundBarInputArgs =
                            $"-f image2 -pattern_type sequence -start_number 1 -framerate {soundBarOverlay.FrameRate.ToString("F3", CultureInfo.InvariantCulture)} -i \"{soundBarOverlay.OverlayPattern}\" ";
                        cmd.Insert(postInputInsertPoint, soundBarInputArgs);
                        postInputInsertPoint += soundBarInputArgs.Length;

                        soundBarInputIndex = generatedOverlayInputBaseIndex;
                        generatedOverlayInputBaseIndex++;
                        soundBarLayerActive = true;

                        // The PNG sequence is a CACHE, not a per-render artifact.
                        //
                        // SoundBarVisualizerPreset lists the freshly generated cache root in
                        // SoundBarGeneratedOverlay.TempArtifacts, and that list used to go straight
                        // into tempTemplateArtifacts. The render's finally block runs TryDeleteFileSafe
                        // over every entry, which is a recursive Directory.Delete - so each render
                        // deleted the very sequence it had just written. TryLoadCache could then only
                        // ever succeed against a directory a previous render had already removed, i.e.
                        // never: RendererVersion, the SHA-256 material hashing and the manifest existed
                        // to serve a cache with a 0 % hit rate.
                        //
                        // So the cache root is deliberately NOT registered for deletion here, exactly
                        // like the cache-hit path already does. That makes the cache work, and makes
                        // eviction - not deletion - responsible for keeping it bounded.
                        string soundBarCacheRoot = ResolveFullPathSafe(soundBarOverlay.TempRoot);
                        foreach (string soundBarArtifact in soundBarOverlay.TempArtifacts)
                        {
                            if (string.IsNullOrWhiteSpace(soundBarArtifact))
                                continue;

                            if (ResolveFullPathSafe(soundBarArtifact).Equals(soundBarCacheRoot, StringComparison.OrdinalIgnoreCase))
                                continue;

                            tempTemplateArtifacts.Add(soundBarArtifact);
                        }

                        int evictedSoundBarCaches = EvictSoundBarCacheSiblings(soundBarOverlay.TempRoot);
                        onLog($"[SOUNDBAR] Reserved input #{soundBarInputIndex} <- {Path.GetFileName(soundBarOverlay.OverlayPattern)} ({soundBarOverlay.FrameCount} frames @ {soundBarOverlay.FrameRate:F2}fps), cache kept at {soundBarCacheRoot}{(evictedSoundBarCaches > 0 ? $", evicted {evictedSoundBarCaches} stale sibling(s)" : string.Empty)}");
                    }
                    catch (Exception ex)
                    {
                        soundBarLayerActive = false;
                        soundBarOverlay = null;
                        onLog($"[SOUNDBAR-INPUT-ERROR] {ex.Message} - soundbar layer skipped, render continues.");
                    }
                }

                /// <summary>
                /// Builds the soundbar composite graph for the branch that is currently
                /// assembling its filter chain. Returns an empty string (and permanently
                /// disables the layer) when there is nothing usable to composite.
                /// </summary>
                string BuildSoundBarGraphStage(string baseInputLabel, string outputLabel, string baseVideoFilters = "")
                {
                    if (!soundBarLayerActive || soundBarOverlay == null)
                        return string.Empty;

                    if (soundBarAudibleIntervals != null && soundBarAudibleIntervals.Count == 0)
                    {
                        soundBarLayerActive = false;
                        onLog("[SOUNDBAR] The render timeline has no audible audio - soundbar layer skipped.");
                        return string.Empty;
                    }

                    try
                    {
                        // The render sequence is capped at MaxRenderEdge (1280 px), and `overlay` does
                        // NOT scale its second input. Without an explicit scale the sequence would be
                        // composited 1:1 at (0,0): 44 % of the frame on a 1920x1080 output, 11 % on 4K,
                        // anchored top-left instead of at the requested X/Y. The canvas passed here is
                        // the OUTPUT size, not the <=1280 render size - passing soundBarOverlay.Width/Height
                        // is the bug. Matches the scale chain the raster-overlay siblings already use
                        // (RainOverlayPreset, DreamyDotOverlay2Preset).
                        var soundBarCanvas = ResolveOverlayCanvasProfile(job, targetResW, targetResH);
                        string soundBarEnableExpression = SoundBarVisualizerPreset.BuildOverlayEnableExpression(
                            soundBarAudibleIntervals,
                            estimatedOutputDuration);
                        if (!string.IsNullOrWhiteSpace(soundBarEnableExpression))
                        {
                            onLog($"[SOUNDBAR] Timeline gate active: overlay composited only inside {soundBarAudibleIntervals!.Count} audible stretch(es); silent stretches stay untouched.");
                        }

                        string graph = SoundBarVisualizerPreset.BuildSingleClipGraph(
                            baseInputLabel,
                            baseVideoFilters,
                            $"[{soundBarInputIndex}:v]",
                            soundBarOverlay.FrameCount,
                            soundBarOverlay.FrameRate,
                            soundBarCanvas.CanvasWidth,
                            soundBarCanvas.CanvasHeight,
                            estimatedOutputDuration,
                            outputLabel,
                            onLog,
                            soundBarEnableExpression);

                        onLog($"[SOUNDBAR] Layer composited from input #{soundBarInputIndex} -> [{outputLabel}] at canvas {soundBarCanvas.CanvasWidth}x{soundBarCanvas.CanvasHeight}");
                        return graph;
                    }
                    catch (Exception ex)
                    {
                        soundBarLayerActive = false;
                        onLog($"[SOUNDBAR-GRAPH-ERROR] {ex.Message} - soundbar layer skipped, render continues.");
                        return string.Empty;
                    }
                }

                if (useLightLeakBurnWithOverlayEffect)
                {
                    int overlayInputBaseIndex = generatedOverlayInputBaseIndex;
                    int lightLeakBurnInputIndex = overlayInputBaseIndex;
                    if (lightLeakBurnAssetRecipe == null)
                        throw new InvalidOperationException("Light leak asset missing.");

                    string lightLeakBurnInputArgs =
                        $"{(lightLeakBurnAssetRecipe.Asset.CanLoop ? "-stream_loop -1 " : string.Empty)}-i \"{lightLeakBurnAssetRecipe.Asset.FilePath}\" ";
                    cmd.Insert(postInputInsertPoint, lightLeakBurnInputArgs);
                    postInputInsertPoint += lightLeakBurnInputArgs.Length;

                    var burnOverlayProfile = ResolveOverlayCanvasProfile(job, targetResW, targetResH);
                    string lightLeakBurnBaseGraph = LightLeakOverlayPreset.BuildSingleClipGraph(
                        "[0:v]",
                        filterChain.ToString(),
                        $"[{lightLeakBurnInputIndex}:v]",
                        lightLeakBurnAssetRecipe,
                        burnOverlayProfile.CanvasWidth,
                        burnOverlayProfile.CanvasHeight,
                        lightLeakBurnFps,
                        estimatedOutputDuration,
                        "burnstage",
                        onLog);

                    string currentOutputLabel = "[burnstage]";
                    var graphParts = new List<string> { lightLeakBurnBaseGraph };
                    int nextOverlayInputIndex = lightLeakBurnInputIndex + 1;

                    if (useScratchVideoEffect && scratchVideoOverlay != null)
                    {
                        string scratchOverlayInputArgs = $"-f image2 -pattern_type sequence -start_number 1 -framerate {scratchVideoOverlay.FrameRate.ToString("F3", CultureInfo.InvariantCulture)} -i \"{scratchVideoOverlay.OverlayPattern}\" ";
                        cmd.Insert(postInputInsertPoint, scratchOverlayInputArgs);
                        postInputInsertPoint += scratchOverlayInputArgs.Length;

                        graphParts.Add(ScratchVideoPreset.BuildSingleClipGraph(
                            currentOutputLabel,
                            string.Empty,
                            $"[{nextOverlayInputIndex}:v]",
                            scratchVideoOverlay.FrameCount,
                            scratchVideoOverlay.FrameRate,
                            estimatedOutputDuration,
                            "overlaystage",
                            onLog));
                        currentOutputLabel = "[overlaystage]";
                        nextOverlayInputIndex++;
                    }
                    else if (useDreamyDotOverlayEffect && dreamyDotOverlay != null)
                    {
                        if (string.IsNullOrWhiteSpace(dreamyDotOverlay.PreRenderedAssetPath) || !File.Exists(dreamyDotOverlay.PreRenderedAssetPath))
                            throw new InvalidOperationException("Dreamy Dot overlay asset was not prepared.");

                        string dreamyDotOverlayInputArgs = $"-stream_loop -1 -i \"{dreamyDotOverlay.PreRenderedAssetPath}\" ";
                        cmd.Insert(postInputInsertPoint, dreamyDotOverlayInputArgs);
                        postInputInsertPoint += dreamyDotOverlayInputArgs.Length;

                        graphParts.Add(DreamyDotOverlayPreset.BuildAssetClipGraph(
                            currentOutputLabel,
                            string.Empty,
                            $"[{nextOverlayInputIndex}:v]",
                            dreamyDotOverlay,
                            targetResW,
                            targetResH,
                            lightLeakBurnFps,
                            estimatedOutputDuration,
                            "overlaystage",
                            onLog));
                        currentOutputLabel = "[overlaystage]";
                        nextOverlayInputIndex++;
                    }
                    else if (useRecipeParticleOverlayEffect && recipeParticleOverlay != null && recipeParticleEffect != null)
                    {
                        string recipeOverlayInputArgs = $"-f image2 -pattern_type sequence -start_number 1 -framerate {recipeParticleOverlay.FrameRate.ToString("F3", CultureInfo.InvariantCulture)} -i \"{recipeParticleOverlay.OverlayPattern}\" ";
                        cmd.Insert(postInputInsertPoint, recipeOverlayInputArgs);
                        postInputInsertPoint += recipeOverlayInputArgs.Length;

                        graphParts.Add(OverlayEffectGraphBuilder.BuildLoopedSequenceClipGraph(
                            currentOutputLabel,
                            string.Empty,
                            $"[{nextOverlayInputIndex}:v]",
                            EffectRecipeBridge.CreateGeneratedOverlayRecipe(recipeParticleEffect, job),
                            recipeParticleOverlay.FrameCount,
                            recipeParticleOverlay.FrameRate,
                            estimatedOutputDuration,
                            "overlaystage",
                            onLog));
                        currentOutputLabel = "[overlaystage]";
                        nextOverlayInputIndex++;
                    }
                    else if (useRgbPolaroidLedEffect && rgbPolaroidLedOverlay != null)
                    {
                        string rgbOverlayInputArgs = $"-f image2 -pattern_type sequence -start_number 1 -framerate {rgbPolaroidLedOverlay.FrameRate.ToString("F3", CultureInfo.InvariantCulture)} -i \"{rgbPolaroidLedOverlay.OverlayPattern}\" ";
                        cmd.Insert(postInputInsertPoint, rgbOverlayInputArgs);
                        postInputInsertPoint += rgbOverlayInputArgs.Length;

                        graphParts.Add(RgbPolaroidLedPreset.BuildSingleClipGraph(
                            currentOutputLabel,
                            string.Empty,
                            $"[{nextOverlayInputIndex}:v]",
                            rgbPolaroidLedOverlay.FrameCount,
                            rgbPolaroidLedOverlay.FrameRate,
                            estimatedOutputDuration,
                            "overlaystage",
                            onLog));
                        currentOutputLabel = "[overlaystage]";
                        nextOverlayInputIndex++;
                    }
                    else if (usePolaroidScrapbook2Effect && polaroidScrapbook2Overlay != null)
                    {
                        string polaroid2OverlayInputArgs = $"-f image2 -pattern_type sequence -start_number 1 -framerate {polaroidScrapbook2Overlay.FrameRate.ToString("F3", CultureInfo.InvariantCulture)} -i \"{polaroidScrapbook2Overlay.OverlayPattern}\" ";
                        cmd.Insert(postInputInsertPoint, polaroid2OverlayInputArgs);
                        postInputInsertPoint += polaroid2OverlayInputArgs.Length;

                        graphParts.Add(PolaroidScrapbook2Preset.BuildSingleClipGraph(
                            currentOutputLabel,
                            string.Empty,
                            $"[{nextOverlayInputIndex}:v]",
                            polaroidScrapbook2Overlay.FrameCount,
                            polaroidScrapbook2Overlay.FrameRate,
                            estimatedOutputDuration,
                            "overlaystage",
                            onLog));
                        currentOutputLabel = "[overlaystage]";
                        nextOverlayInputIndex++;
                    }
                    else if (useDashedPolaroidEffect && dashedPolaroidOverlay != null)
                    {
                        string dashedOverlayInputArgs = $"-f image2 -pattern_type sequence -start_number 1 -framerate {dashedPolaroidOverlay.FrameRate.ToString("F3", CultureInfo.InvariantCulture)} -i \"{dashedPolaroidOverlay.OverlayPattern}\" ";
                        cmd.Insert(postInputInsertPoint, dashedOverlayInputArgs);
                        postInputInsertPoint += dashedOverlayInputArgs.Length;

                        graphParts.Add(DashedPolaroidFramePreset.BuildSingleClipGraph(
                            currentOutputLabel,
                            string.Empty,
                            $"[{nextOverlayInputIndex}:v]",
                            dashedPolaroidOverlay.FrameCount,
                            dashedPolaroidOverlay.FrameRate,
                            estimatedOutputDuration,
                            "overlaystage",
                            onLog));
                        currentOutputLabel = "[overlaystage]";
                        nextOverlayInputIndex++;
                    }

                    string snowfallGraphAfterOverlay = AppendOptionalSnowfallGraph(currentOutputLabel, "snowstage", ref nextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(snowfallGraphAfterOverlay))
                    {
                        graphParts.Add(snowfallGraphAfterOverlay);
                        currentOutputLabel = "[snowstage]";
                    }

                    // SOUNDBAR (1/16): composited after every overlay stage, before the
                    // watermark/logo pass. All three sub-paths below (watermark / audio
                    // mix / plain) read currentOutputLabel, so one insert covers them all.
                    string soundBarStageGraph = BuildSoundBarGraphStage(currentOutputLabel, "soundbarout");
                    if (!string.IsNullOrWhiteSpace(soundBarStageGraph))
                    {
                        graphParts.Add(soundBarStageGraph);
                        currentOutputLabel = "[soundbarout]";
                    }

                    if (hasImageOverlayAssets)
                    {
                        string watermarkGraph = BuildWatermarkFilterComplex(
                            currentOutputLabel,
                            string.Empty,
                            fadeVideoFilter,
                            watermarkInputIndex,
                            brandLogoInputIndex,
                            job,
                            onLog);

                        string finalFilterComplex = $"{string.Join(";", graphParts)};{watermarkGraph}";

                        if (needsAudioMixing)
                        {
                            var combinedFilterComplex = new StringBuilder(finalFilterComplex);
                            combinedFilterComplex.Append(";");
                            combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                            combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                            combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                            if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                                combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                            else
                                combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                            cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");
                            cmd.Append("-map \"[audio_out]\" ");
                            onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Light Leak Burn + Overlay + Audio mixing in single filter_complex");
                            onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                            onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                        }
                        else
                        {
                            cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");

                            if (hasSourceAudio)
                            {
                                string audioMap = "0:a?";
                                cmd.Append($"-map {audioMap} ");
                                string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                    cmd.Append($"-af \"{sourceAudioFilter}\" ");
                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else if (hasExternalAudio)
                            {
                                string audioMap = "1:a?";
                                cmd.Append($"-map {audioMap} ");
                                string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                    cmd.Append($"-af \"{externalAudioFilter}\" ");
                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else
                            {
                                onLog("[AUDIO] disabled (no audio stream)");
                            }

                            onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                        }
                    }
                    else if (needsAudioMixing)
                    {
                        string videoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                            ? $"{currentOutputLabel}{fadeVideoFilter}[vout]"
                            : $"{currentOutputLabel}format=yuv420p[vout]";

                        var combinedFilterComplex = new StringBuilder(string.Join(";", graphParts));
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append(videoTail);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                        combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                        combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                        if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                            combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                        else
                            combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                        cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");
                        cmd.Append("-map \"[audio_out]\" ");
                        onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Light Leak Burn + Overlay + Audio mixing in single filter_complex");
                        onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                        onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                    }
                    else
                    {
                        string videoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                            ? $"{currentOutputLabel}{fadeVideoFilter}[vout]"
                            : $"{currentOutputLabel}format=yuv420p[vout]";

                        string finalFilterComplex = $"{string.Join(";", graphParts)};{videoTail}";
                        cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");

                        if (hasSourceAudio)
                        {
                            string audioMap = "0:a?";
                            cmd.Append($"-map {audioMap} ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");

                            string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                cmd.Append($"-af \"{sourceAudioFilter}\" ");
                        }
                        else if (hasExternalAudio)
                        {
                            string audioMap = "1:a?";
                            cmd.Append($"-map {audioMap} ");
                            string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                cmd.Append($"-af \"{externalAudioFilter}\" ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else
                        {
                            onLog("[AUDIO] disabled (no audio stream)");
                        }

                        onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                    }
                }
                else if (useLightLeakBurnEffect)
                {
                    int lightLeakBurnInputIndex = generatedOverlayInputBaseIndex;
                    if (lightLeakBurnAssetRecipe == null)
                        throw new InvalidOperationException("Light leak asset missing.");

                    string lightLeakBurnInputArgs =
                        $"{(lightLeakBurnAssetRecipe.Asset.CanLoop ? "-stream_loop -1 " : string.Empty)}-i \"{lightLeakBurnAssetRecipe.Asset.FilePath}\" ";
                    cmd.Insert(postInputInsertPoint, lightLeakBurnInputArgs);
                    postInputInsertPoint += lightLeakBurnInputArgs.Length;

                    var burnOverlayProfile = ResolveOverlayCanvasProfile(job, targetResW, targetResH);
                    string lightLeakBurnBaseGraph = LightLeakOverlayPreset.BuildSingleClipGraph(
                        "[0:v]",
                        filterChain.ToString(),
                        $"[{lightLeakBurnInputIndex}:v]",
                        lightLeakBurnAssetRecipe,
                        burnOverlayProfile.CanvasWidth,
                        burnOverlayProfile.CanvasHeight,
                        lightLeakBurnFps,
                        estimatedOutputDuration,
                        "burnout",
                        onLog);
                    // SOUNDBAR (2/16): the burn graph output label is a literal, so promote it to a
                    // local that the soundbar stage can advance.
                    string lightLeakBurnStageLabel = "[burnout]";
                    string lightLeakBurnSoundBarGraph = BuildSoundBarGraphStage(lightLeakBurnStageLabel, "soundbarout");
                    if (!string.IsNullOrWhiteSpace(lightLeakBurnSoundBarGraph))
                    {
                        lightLeakBurnBaseGraph = $"{lightLeakBurnBaseGraph};{lightLeakBurnSoundBarGraph}";
                        lightLeakBurnStageLabel = "[soundbarout]";
                    }

                    string lightLeakBurnVideoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                        ? $"{lightLeakBurnStageLabel}{fadeVideoFilter}[vout]"
                        : $"{lightLeakBurnStageLabel}format=yuv420p[vout]";

                    if (hasImageOverlayAssets)
                    {
                        string watermarkGraph = BuildWatermarkFilterComplex(
                            lightLeakBurnStageLabel,
                            string.Empty,
                            fadeVideoFilter,
                            watermarkInputIndex,
                            brandLogoInputIndex,
                            job,
                            onLog);

                        string finalFilterComplex = $"{lightLeakBurnBaseGraph};{watermarkGraph}";

                        if (needsAudioMixing)
                        {
                            var combinedFilterComplex = new StringBuilder(finalFilterComplex);
                            combinedFilterComplex.Append(";");
                            combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                            combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                            combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                            if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                                combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                            else
                                combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                            cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");
                            cmd.Append("-map \"[audio_out]\" ");
                            onLog($"[FFMPEG-COMBINED-FILTER-COMPLEX] Video + Audio mixing in single filter_complex");
                            onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                            onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                        }
                        else
                        {
                            cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");

                            if (hasSourceAudio)
                            {
                                string audioMap = "0:a?";
                                cmd.Append($"-map {audioMap} ");

                                string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                    cmd.Append($"-af \"{sourceAudioFilter}\" ");

                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else if (hasExternalAudio)
                            {
                                string audioMap = "1:a?";
                                cmd.Append($"-map {audioMap} ");

                                string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                    cmd.Append($"-af \"{externalAudioFilter}\" ");

                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else
                            {
                                onLog("[AUDIO] disabled (no audio stream)");
                            }

                            onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                        }
                    }
                    else if (needsAudioMixing)
                    {
                        var combinedFilterComplex = new StringBuilder(lightLeakBurnBaseGraph);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append(lightLeakBurnVideoTail);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                        combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                        combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                        if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                            combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                        else
                            combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                        cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");
                        cmd.Append("-map \"[audio_out]\" ");
                        onLog($"[FFMPEG-COMBINED-FILTER-COMPLEX] Video + Audio mixing in single filter_complex");
                        onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                        onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                    }
                    else
                    {
                        string finalFilterComplex = $"{lightLeakBurnBaseGraph};{lightLeakBurnVideoTail}";
                        cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");

                        if (hasSourceAudio)
                        {
                            string audioMap = "0:a?";
                            cmd.Append($"-map {audioMap} ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");

                            string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                cmd.Append($"-af \"{sourceAudioFilter}\" ");
                        }
                        else if (hasExternalAudio)
                        {
                            string audioMap = "1:a?";
                            cmd.Append($"-map {audioMap} ");

                            string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                cmd.Append($"-af \"{externalAudioFilter}\" ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else
                        {
                            onLog("[AUDIO] disabled (no audio stream)");
                        }

                        onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                    }
                }
                else if (useRgbPolaroidLedEffect)
                {
                    if (rgbPolaroidLedOverlay == null)
                        throw new InvalidOperationException("RGB Polaroid LED overlay frames were not prepared.");

                    int rgbOverlayInputIndex = generatedOverlayInputBaseIndex;
                    string rgbOverlayInputArgs = $"-f image2 -pattern_type sequence -start_number 1 -framerate {rgbPolaroidLedOverlay.FrameRate.ToString("F3", CultureInfo.InvariantCulture)} -i \"{rgbPolaroidLedOverlay.OverlayPattern}\" ";
                    cmd.Insert(postInputInsertPoint, rgbOverlayInputArgs);
                    postInputInsertPoint += rgbOverlayInputArgs.Length;

                    string rgbBaseGraph = RgbPolaroidLedPreset.BuildSingleClipGraph(
                        "[0:v]",
                        filterChain.ToString(),
                        $"[{rgbOverlayInputIndex}:v]",
                        rgbPolaroidLedOverlay.FrameCount,
                        rgbPolaroidLedOverlay.FrameRate,
                        estimatedOutputDuration,
                        "rgbledout",
                        onLog);
                    var rgbGraphParts = new List<string> { rgbBaseGraph };
                    string rgbCurrentOutputLabel = "[rgbledout]";
                    int rgbNextOverlayInputIndex = rgbOverlayInputIndex + 1;
                    string rgbParticleGraph = AppendOptionalRecipeParticleGraph(rgbCurrentOutputLabel, "recipeparticlestage", ref rgbNextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(rgbParticleGraph))
                    {
                        rgbGraphParts.Add(rgbParticleGraph);
                        rgbCurrentOutputLabel = "[recipeparticlestage]";
                    }
                    string rgbLightLeakGraph = AppendOptionalLightLeakAssetGraph(rgbCurrentOutputLabel, "lightleakstage", ref rgbNextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(rgbLightLeakGraph))
                    {
                        rgbGraphParts.Add(rgbLightLeakGraph);
                        rgbCurrentOutputLabel = "[lightleakstage]";
                    }
                    string rgbRainGraph = AppendOptionalRainGraph(rgbCurrentOutputLabel, "rainstage", ref rgbNextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(rgbRainGraph))
                    {
                        rgbGraphParts.Add(rgbRainGraph);
                        rgbCurrentOutputLabel = "[rainstage]";
                    }
                    string rgbDreamyDotGraph = AppendOptionalDreamyDotGraph(rgbCurrentOutputLabel, "dreamydotstage", ref rgbNextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(rgbDreamyDotGraph))
                    {
                        rgbGraphParts.Add(rgbDreamyDotGraph);
                        rgbCurrentOutputLabel = "[dreamydotstage]";
                    }
                    string rgbScratchGraph = AppendOptionalScratchGraph(rgbCurrentOutputLabel, "scratchstage", ref rgbNextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(rgbScratchGraph))
                    {
                        rgbGraphParts.Add(rgbScratchGraph);
                        rgbCurrentOutputLabel = "[scratchstage]";
                    }
                    string rgbSnowfallGraph = AppendOptionalSnowfallGraph(rgbCurrentOutputLabel, "snowstage", ref rgbNextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(rgbSnowfallGraph))
                    {
                        rgbGraphParts.Add(rgbSnowfallGraph);
                        rgbCurrentOutputLabel = "[snowstage]";
                    }

                    // SOUNDBAR (3/16)
                    string rgbSoundBarGraph = BuildSoundBarGraphStage(rgbCurrentOutputLabel, "soundbarout");
                    if (!string.IsNullOrWhiteSpace(rgbSoundBarGraph))
                    {
                        rgbGraphParts.Add(rgbSoundBarGraph);
                        rgbCurrentOutputLabel = "[soundbarout]";
                    }

                    string rgbVideoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                        ? $"{rgbCurrentOutputLabel}{fadeVideoFilter}[vout]"
                        : $"{rgbCurrentOutputLabel}format=yuv420p[vout]";

                    if (hasImageOverlayAssets)
                    {
                        string watermarkGraph = BuildWatermarkFilterComplex(
                            rgbCurrentOutputLabel,
                            string.Empty,
                            fadeVideoFilter,
                            watermarkInputIndex,
                            brandLogoInputIndex,
                            job,
                            onLog);

                        string finalFilterComplex = $"{string.Join(";", rgbGraphParts)};{watermarkGraph}";

                        if (needsAudioMixing)
                        {
                            var combinedFilterComplex = new StringBuilder(finalFilterComplex);
                            combinedFilterComplex.Append(";");
                            combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                            combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                            combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                            if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                                combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                            else
                                combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                            cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");
                            cmd.Append("-map \"[audio_out]\" ");
                            onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] RGB Polaroid video + Audio mixing in single filter_complex");
                            onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                            onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                        }
                        else
                        {
                            cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");

                            if (hasSourceAudio)
                            {
                                string audioMap = "0:a?";
                                cmd.Append($"-map {audioMap} ");

                                string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                    cmd.Append($"-af \"{sourceAudioFilter}\" ");

                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else if (hasExternalAudio)
                            {
                                string audioMap = "1:a?";
                                cmd.Append($"-map {audioMap} ");

                                string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                    cmd.Append($"-af \"{externalAudioFilter}\" ");

                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else
                            {
                                onLog("[AUDIO] disabled (no audio stream)");
                            }

                            onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                        }
                    }
                    else if (needsAudioMixing)
                    {
                        var combinedFilterComplex = new StringBuilder(string.Join(";", rgbGraphParts));
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append(rgbVideoTail);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                        combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                        combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                        if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                            combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                        else
                            combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                        cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");
                        cmd.Append("-map \"[audio_out]\" ");
                        onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] RGB Polaroid video + Audio mixing in single filter_complex");
                        onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                        onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                    }
                    else
                    {
                        string finalFilterComplex = $"{string.Join(";", rgbGraphParts)};{rgbVideoTail}";
                        cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");

                        if (hasSourceAudio)
                        {
                            string audioMap = "0:a?";
                            cmd.Append($"-map {audioMap} ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");

                            string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                cmd.Append($"-af \"{sourceAudioFilter}\" ");
                        }
                        else if (hasExternalAudio)
                        {
                            string audioMap = "1:a?";
                            cmd.Append($"-map {audioMap} ");
                            string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                cmd.Append($"-af \"{externalAudioFilter}\" ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else
                        {
                            onLog("[AUDIO] disabled (no audio stream)");
                        }

                        onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                    }
                }
                else if (usePolaroidScrapbook2Effect)
                {
                    if (polaroidScrapbook2Overlay == null)
                        throw new InvalidOperationException("Polaroid Scrapbook 2 overlay frames were not prepared.");

                    int polaroid2OverlayInputIndex = generatedOverlayInputBaseIndex;
                    string polaroid2OverlayInputArgs = $"-f image2 -pattern_type sequence -start_number 1 -framerate {polaroidScrapbook2Overlay.FrameRate.ToString("F3", CultureInfo.InvariantCulture)} -i \"{polaroidScrapbook2Overlay.OverlayPattern}\" ";
                    cmd.Insert(postInputInsertPoint, polaroid2OverlayInputArgs);
                    postInputInsertPoint += polaroid2OverlayInputArgs.Length;

                    string polaroid2BaseGraph = PolaroidScrapbook2Preset.BuildSingleClipGraph(
                        "[0:v]",
                        filterChain.ToString(),
                        $"[{polaroid2OverlayInputIndex}:v]",
                        polaroidScrapbook2Overlay.FrameCount,
                        polaroidScrapbook2Overlay.FrameRate,
                        estimatedOutputDuration,
                        "polaroid2out",
                        onLog);
                    var polaroid2GraphParts = new List<string> { polaroid2BaseGraph };
                    string polaroid2CurrentOutputLabel = "[polaroid2out]";
                    int polaroid2NextOverlayInputIndex = polaroid2OverlayInputIndex + 1;
                    string polaroid2ParticleGraph = AppendOptionalRecipeParticleGraph(polaroid2CurrentOutputLabel, "recipeparticlestage", ref polaroid2NextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(polaroid2ParticleGraph))
                    {
                        polaroid2GraphParts.Add(polaroid2ParticleGraph);
                        polaroid2CurrentOutputLabel = "[recipeparticlestage]";
                    }
                    string polaroid2LightLeakGraph = AppendOptionalLightLeakAssetGraph(polaroid2CurrentOutputLabel, "lightleakstage", ref polaroid2NextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(polaroid2LightLeakGraph))
                    {
                        polaroid2GraphParts.Add(polaroid2LightLeakGraph);
                        polaroid2CurrentOutputLabel = "[lightleakstage]";
                    }
                    string polaroid2RainGraph = AppendOptionalRainGraph(polaroid2CurrentOutputLabel, "rainstage", ref polaroid2NextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(polaroid2RainGraph))
                    {
                        polaroid2GraphParts.Add(polaroid2RainGraph);
                        polaroid2CurrentOutputLabel = "[rainstage]";
                    }
                    string polaroid2DreamyDotGraph = AppendOptionalDreamyDotGraph(polaroid2CurrentOutputLabel, "dreamydotstage", ref polaroid2NextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(polaroid2DreamyDotGraph))
                    {
                        polaroid2GraphParts.Add(polaroid2DreamyDotGraph);
                        polaroid2CurrentOutputLabel = "[dreamydotstage]";
                    }
                    string polaroid2ScratchGraph = AppendOptionalScratchGraph(polaroid2CurrentOutputLabel, "scratchstage", ref polaroid2NextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(polaroid2ScratchGraph))
                    {
                        polaroid2GraphParts.Add(polaroid2ScratchGraph);
                        polaroid2CurrentOutputLabel = "[scratchstage]";
                    }
                    string polaroid2SnowfallGraph = AppendOptionalSnowfallGraph(polaroid2CurrentOutputLabel, "snowstage", ref polaroid2NextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(polaroid2SnowfallGraph))
                    {
                        polaroid2GraphParts.Add(polaroid2SnowfallGraph);
                        polaroid2CurrentOutputLabel = "[snowstage]";
                    }

                    // SOUNDBAR (4/16)
                    string polaroid2SoundBarGraph = BuildSoundBarGraphStage(polaroid2CurrentOutputLabel, "soundbarout");
                    if (!string.IsNullOrWhiteSpace(polaroid2SoundBarGraph))
                    {
                        polaroid2GraphParts.Add(polaroid2SoundBarGraph);
                        polaroid2CurrentOutputLabel = "[soundbarout]";
                    }

                    string polaroid2VideoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                        ? $"{polaroid2CurrentOutputLabel}{fadeVideoFilter}[vout]"
                        : $"{polaroid2CurrentOutputLabel}format=yuv420p[vout]";

                    if (hasImageOverlayAssets)
                    {
                        string watermarkGraph = BuildWatermarkFilterComplex(
                            polaroid2CurrentOutputLabel,
                            string.Empty,
                            fadeVideoFilter,
                            watermarkInputIndex,
                            brandLogoInputIndex,
                            job,
                            onLog);

                        string finalFilterComplex = $"{string.Join(";", polaroid2GraphParts)};{watermarkGraph}";

                        if (needsAudioMixing)
                        {
                            var combinedFilterComplex = new StringBuilder(finalFilterComplex);
                            combinedFilterComplex.Append(";");
                            combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                            combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                            combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                            if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                                combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                            else
                                combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                            cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");
                            cmd.Append("-map \"[audio_out]\" ");
                            onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Polaroid Scrapbook 2 video + Audio mixing in single filter_complex");
                            onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                            onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                        }
                        else
                        {
                            cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");

                            if (hasSourceAudio)
                            {
                                string audioMap = "0:a?";
                                cmd.Append($"-map {audioMap} ");
                                string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                    cmd.Append($"-af \"{sourceAudioFilter}\" ");
                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else if (hasExternalAudio)
                            {
                                string audioMap = "1:a?";
                                cmd.Append($"-map {audioMap} ");
                                string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                    cmd.Append($"-af \"{externalAudioFilter}\" ");
                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else
                            {
                                onLog("[AUDIO] disabled (no audio stream)");
                            }

                            onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                        }
                    }
                    else if (needsAudioMixing)
                    {
                        var combinedFilterComplex = new StringBuilder(string.Join(";", polaroid2GraphParts));
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append(polaroid2VideoTail);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                        combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                        combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                        if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                            combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                        else
                            combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                        cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");
                        cmd.Append("-map \"[audio_out]\" ");
                        onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Polaroid Scrapbook 2 video + Audio mixing in single filter_complex");
                        onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                        onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                    }
                    else
                    {
                        string finalFilterComplex = $"{string.Join(";", polaroid2GraphParts)};{polaroid2VideoTail}";
                        cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");

                        if (hasSourceAudio)
                        {
                            string audioMap = "0:a?";
                            cmd.Append($"-map {audioMap} ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");

                            string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                cmd.Append($"-af \"{sourceAudioFilter}\" ");
                        }
                        else if (hasExternalAudio)
                        {
                            string audioMap = "1:a?";
                            cmd.Append($"-map {audioMap} ");
                            string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                cmd.Append($"-af \"{externalAudioFilter}\" ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else
                        {
                            onLog("[AUDIO] disabled (no audio stream)");
                        }

                        onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                    }
                }
                else if (useDashedAndScratchEffect)
                {
                    if (scratchVideoOverlay == null)
                        throw new InvalidOperationException("Dust & Scratches overlay frames were not prepared.");
                    if (dashedPolaroidOverlay == null)
                        throw new InvalidOperationException("Dashed Polaroid overlay frames were not prepared.");

                    int scratchOverlayInputIndex = generatedOverlayInputBaseIndex;
                    string scratchOverlayInputArgs = $"-f image2 -pattern_type sequence -start_number 1 -framerate {scratchVideoOverlay.FrameRate.ToString("F3", CultureInfo.InvariantCulture)} -i \"{scratchVideoOverlay.OverlayPattern}\" ";
                    cmd.Insert(postInputInsertPoint, scratchOverlayInputArgs);
                    postInputInsertPoint += scratchOverlayInputArgs.Length;

                    int dashedOverlayInputIndex = scratchOverlayInputIndex + 1;
                    string dashedOverlayInputArgs = $"-f image2 -pattern_type sequence -start_number 1 -framerate {dashedPolaroidOverlay.FrameRate.ToString("F3", CultureInfo.InvariantCulture)} -i \"{dashedPolaroidOverlay.OverlayPattern}\" ";
                    cmd.Insert(postInputInsertPoint, dashedOverlayInputArgs);
                    postInputInsertPoint += dashedOverlayInputArgs.Length;

                    string scratchBaseGraph = ScratchVideoPreset.BuildSingleClipGraph(
                        "[0:v]",
                        filterChain.ToString(),
                        $"[{scratchOverlayInputIndex}:v]",
                        scratchVideoOverlay.FrameCount,
                        scratchVideoOverlay.FrameRate,
                        estimatedOutputDuration,
                        "scratchstage",
                        onLog);

                    string dashedBaseGraph = DashedPolaroidFramePreset.BuildSingleClipGraph(
                        "[scratchstage]",
                        string.Empty,
                        $"[{dashedOverlayInputIndex}:v]",
                        dashedPolaroidOverlay.FrameCount,
                        dashedPolaroidOverlay.FrameRate,
                        estimatedOutputDuration,
                        "dashpolaroidout",
                        onLog);

                    string dashedCurrentOutputLabel = "[dashpolaroidout]";
                    int dashedNextOverlayInputIndex = dashedOverlayInputIndex + 1;
                    string dashedDreamyDotGraph = AppendOptionalDreamyDotGraph(dashedCurrentOutputLabel, "dreamydotstage", ref dashedNextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(dashedDreamyDotGraph))
                    {
                        dashedBaseGraph = $"{dashedBaseGraph};{dashedDreamyDotGraph}";
                        dashedCurrentOutputLabel = "[dreamydotstage]";
                    }

                    // SOUNDBAR (5/16)
                    string dashedAndScratchSoundBarGraph = BuildSoundBarGraphStage(dashedCurrentOutputLabel, "soundbarout");
                    if (!string.IsNullOrWhiteSpace(dashedAndScratchSoundBarGraph))
                    {
                        dashedBaseGraph = $"{dashedBaseGraph};{dashedAndScratchSoundBarGraph}";
                        dashedCurrentOutputLabel = "[soundbarout]";
                    }

                    string dashedVideoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                        ? $"{dashedCurrentOutputLabel}{fadeVideoFilter}[vout]"
                        : $"{dashedCurrentOutputLabel}format=yuv420p[vout]";

                    if (hasImageOverlayAssets)
                    {
                        string watermarkGraph = BuildWatermarkFilterComplex(
                            dashedCurrentOutputLabel,
                            string.Empty,
                            fadeVideoFilter,
                            watermarkInputIndex,
                            brandLogoInputIndex,
                            job,
                            onLog);

                        string finalFilterComplex = $"{scratchBaseGraph};{dashedBaseGraph};{watermarkGraph}";

                        if (needsAudioMixing)
                        {
                            var combinedFilterComplex = new StringBuilder(finalFilterComplex);
                            combinedFilterComplex.Append(";");
                            combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                            combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                            combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                            if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                                combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                            else
                                combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                            cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");
                            cmd.Append("-map \"[audio_out]\" ");
                            onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Dashed Polaroid + Dust & Scratches + Audio mixing in single filter_complex");
                            onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                            onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                        }
                        else
                        {
                            cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");

                            if (hasSourceAudio)
                            {
                                string audioMap = "0:a?";
                                cmd.Append($"-map {audioMap} ");

                                string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                    cmd.Append($"-af \"{sourceAudioFilter}\" ");

                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else if (hasExternalAudio)
                            {
                                string audioMap = "1:a?";
                                cmd.Append($"-map {audioMap} ");

                                string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                    cmd.Append($"-af \"{externalAudioFilter}\" ");

                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else
                            {
                                onLog("[AUDIO] disabled (no audio stream)");
                            }

                            onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                        }
                    }
                    else if (needsAudioMixing)
                    {
                        var combinedFilterComplex = new StringBuilder(scratchBaseGraph);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append(dashedBaseGraph);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append(dashedVideoTail);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                        combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                        combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                        if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                            combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                        else
                            combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                        cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");
                        cmd.Append("-map \"[audio_out]\" ");
                        onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Dashed Polaroid + Dust & Scratches + Audio mixing in single filter_complex");
                        onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                        onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                    }
                    else
                    {
                        string finalFilterComplex = $"{scratchBaseGraph};{dashedBaseGraph};{dashedVideoTail}";
                        cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");

                        if (hasSourceAudio)
                        {
                            string audioMap = "0:a?";
                            cmd.Append($"-map {audioMap} ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");

                            string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                cmd.Append($"-af \"{sourceAudioFilter}\" ");
                        }
                        else if (hasExternalAudio)
                        {
                            string audioMap = "1:a?";
                            cmd.Append($"-map {audioMap} ");
                            string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                cmd.Append($"-af \"{externalAudioFilter}\" ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else
                        {
                            onLog("[AUDIO] disabled (no audio stream)");
                        }

                        onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                    }
                }
                else if (useDashedPolaroidEffect)
                {
                    if (dashedPolaroidOverlay == null)
                        throw new InvalidOperationException("Dashed Polaroid overlay frames were not prepared.");

                    int dashedOverlayInputIndex = generatedOverlayInputBaseIndex;
                    string dashedOverlayInputArgs = $"-f image2 -pattern_type sequence -start_number 1 -framerate {dashedPolaroidOverlay.FrameRate.ToString("F3", CultureInfo.InvariantCulture)} -i \"{dashedPolaroidOverlay.OverlayPattern}\" ";
                    cmd.Insert(postInputInsertPoint, dashedOverlayInputArgs);
                    postInputInsertPoint += dashedOverlayInputArgs.Length;

                    string dashedBaseGraph = DashedPolaroidFramePreset.BuildSingleClipGraph(
                        "[0:v]",
                        filterChain.ToString(),
                        $"[{dashedOverlayInputIndex}:v]",
                        dashedPolaroidOverlay.FrameCount,
                        dashedPolaroidOverlay.FrameRate,
                        estimatedOutputDuration,
                        "dashpolaroidout",
                        onLog);
                    var dashedGraphParts = new List<string> { dashedBaseGraph };
                    string dashedCurrentOutputLabel = "[dashpolaroidout]";
                    int dashedNextOverlayInputIndex = dashedOverlayInputIndex + 1;
                    string dashedParticleGraph = AppendOptionalRecipeParticleGraph(dashedCurrentOutputLabel, "recipeparticlestage", ref dashedNextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(dashedParticleGraph))
                    {
                        dashedGraphParts.Add(dashedParticleGraph);
                        dashedCurrentOutputLabel = "[recipeparticlestage]";
                    }
                    string dashedLightLeakGraph = AppendOptionalLightLeakAssetGraph(dashedCurrentOutputLabel, "lightleakstage", ref dashedNextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(dashedLightLeakGraph))
                    {
                        dashedGraphParts.Add(dashedLightLeakGraph);
                        dashedCurrentOutputLabel = "[lightleakstage]";
                    }
                    string dashedRainGraph = AppendOptionalRainGraph(dashedCurrentOutputLabel, "rainstage", ref dashedNextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(dashedRainGraph))
                    {
                        dashedGraphParts.Add(dashedRainGraph);
                        dashedCurrentOutputLabel = "[rainstage]";
                    }
                    string dashedDreamyDotGraph = AppendOptionalDreamyDotGraph(dashedCurrentOutputLabel, "dreamydotstage", ref dashedNextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(dashedDreamyDotGraph))
                    {
                        dashedGraphParts.Add(dashedDreamyDotGraph);
                        dashedCurrentOutputLabel = "[dreamydotstage]";
                    }
                    string dashedScratchGraph = AppendOptionalScratchGraph(dashedCurrentOutputLabel, "scratchstage", ref dashedNextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(dashedScratchGraph))
                    {
                        dashedGraphParts.Add(dashedScratchGraph);
                        dashedCurrentOutputLabel = "[scratchstage]";
                    }
                    string dashedSnowfallGraph = AppendOptionalSnowfallGraph(dashedCurrentOutputLabel, "snowstage", ref dashedNextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(dashedSnowfallGraph))
                    {
                        dashedGraphParts.Add(dashedSnowfallGraph);
                        dashedCurrentOutputLabel = "[snowstage]";
                    }

                    // SOUNDBAR (6/16)
                    string dashedSoundBarGraph = BuildSoundBarGraphStage(dashedCurrentOutputLabel, "soundbarout");
                    if (!string.IsNullOrWhiteSpace(dashedSoundBarGraph))
                    {
                        dashedGraphParts.Add(dashedSoundBarGraph);
                        dashedCurrentOutputLabel = "[soundbarout]";
                    }

                    string dashedVideoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                        ? $"{dashedCurrentOutputLabel}{fadeVideoFilter}[vout]"
                        : $"{dashedCurrentOutputLabel}format=yuv420p[vout]";

                    if (hasImageOverlayAssets)
                    {
                        string watermarkGraph = BuildWatermarkFilterComplex(
                            dashedCurrentOutputLabel,
                            string.Empty,
                            fadeVideoFilter,
                            watermarkInputIndex,
                            brandLogoInputIndex,
                            job,
                            onLog);

                        string finalFilterComplex = $"{string.Join(";", dashedGraphParts)};{watermarkGraph}";

                        if (needsAudioMixing)
                        {
                            var combinedFilterComplex = new StringBuilder(finalFilterComplex);
                            combinedFilterComplex.Append(";");
                            combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                            combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                            combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                            if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                                combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                            else
                                combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                            cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");
                            cmd.Append("-map \"[audio_out]\" ");
                            onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Dashed Polaroid video + Audio mixing in single filter_complex");
                            onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                            onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                        }
                        else
                        {
                            cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");

                            if (hasSourceAudio)
                            {
                                string audioMap = "0:a?";
                                cmd.Append($"-map {audioMap} ");

                                string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                    cmd.Append($"-af \"{sourceAudioFilter}\" ");

                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else if (hasExternalAudio)
                            {
                                string audioMap = "1:a?";
                                cmd.Append($"-map {audioMap} ");

                                string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                    cmd.Append($"-af \"{externalAudioFilter}\" ");

                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else
                            {
                                onLog("[AUDIO] disabled (no audio stream)");
                            }

                            onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                        }
                    }
                    else if (needsAudioMixing)
                    {
                        var combinedFilterComplex = new StringBuilder(string.Join(";", dashedGraphParts));
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append(dashedVideoTail);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                        combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                        combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                        if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                            combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                        else
                            combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                        cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");
                        cmd.Append("-map \"[audio_out]\" ");
                        onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Dashed Polaroid video + Audio mixing in single filter_complex");
                        onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                        onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                    }
                    else
                    {
                        string finalFilterComplex = $"{string.Join(";", dashedGraphParts)};{dashedVideoTail}";
                        cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");

                        if (hasSourceAudio)
                        {
                            string audioMap = "0:a?";
                            cmd.Append($"-map {audioMap} ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");

                            string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                cmd.Append($"-af \"{sourceAudioFilter}\" ");
                        }
                        else if (hasExternalAudio)
                        {
                            string audioMap = "1:a?";
                            cmd.Append($"-map {audioMap} ");
                            string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                cmd.Append($"-af \"{externalAudioFilter}\" ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else
                        {
                            onLog("[AUDIO] disabled (no audio stream)");
                        }

                        onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                    }
                }
                else if (useLightLeakOverlayEffect && useScratchVideoEffect)
                {
                    if (lightLeakOverlayRecipe == null)
                        throw new InvalidOperationException("Light Leak overlay asset recipe was not prepared.");
                    if (scratchVideoOverlay == null)
                        throw new InvalidOperationException("Dust & Scratches overlay frames were not prepared.");

                    var overlayProfile = ResolveOverlayCanvasProfile(job, targetResW, targetResH);
                    int lightLeakOverlayInputIndex = generatedOverlayInputBaseIndex;
                    string lightLeakOverlayInputArgs = $"{(lightLeakOverlayRecipe.Asset.CanLoop ? "-stream_loop -1 " : string.Empty)}-i \"{lightLeakOverlayRecipe.Asset.FilePath}\" ";
                    cmd.Insert(postInputInsertPoint, lightLeakOverlayInputArgs);
                    postInputInsertPoint += lightLeakOverlayInputArgs.Length;

                    int scratchOverlayInputIndex = lightLeakOverlayInputIndex + 1;
                    string scratchOverlayInputArgs = $"-f image2 -pattern_type sequence -start_number 1 -framerate {scratchVideoOverlay.FrameRate.ToString("F3", CultureInfo.InvariantCulture)} -i \"{scratchVideoOverlay.OverlayPattern}\" ";
                    cmd.Insert(postInputInsertPoint, scratchOverlayInputArgs);
                    postInputInsertPoint += scratchOverlayInputArgs.Length;

                    string lightLeakBaseGraph = LightLeakOverlayPreset.BuildSingleClipGraph(
                        "[0:v]",
                        filterChain.ToString(),
                        $"[{lightLeakOverlayInputIndex}:v]",
                        lightLeakOverlayRecipe,
                        overlayProfile.CanvasWidth,
                        overlayProfile.CanvasHeight,
                        lightLeakBurnFps,
                        estimatedOutputDuration,
                        "lightleakstage",
                        onLog);

                    string scratchBaseGraph = ScratchVideoPreset.BuildSingleClipGraph(
                        "[lightleakstage]",
                        string.Empty,
                        $"[{scratchOverlayInputIndex}:v]",
                        scratchVideoOverlay.FrameCount,
                        scratchVideoOverlay.FrameRate,
                        estimatedOutputDuration,
                        "scratchout",
                        onLog);
                    // SOUNDBAR (7/16): literal [scratchout] promoted to a local the soundbar can advance.
                    string lightLeakScratchStageLabel = "[scratchout]";
                    string lightLeakScratchSoundBarGraph = BuildSoundBarGraphStage(lightLeakScratchStageLabel, "soundbarout");
                    if (!string.IsNullOrWhiteSpace(lightLeakScratchSoundBarGraph))
                    {
                        scratchBaseGraph = $"{scratchBaseGraph};{lightLeakScratchSoundBarGraph}";
                        lightLeakScratchStageLabel = "[soundbarout]";
                    }

                    string finalVideoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                        ? lightLeakScratchStageLabel + fadeVideoFilter + "[vout]"
                        : lightLeakScratchStageLabel + "format=yuv420p[vout]";

                    if (hasImageOverlayAssets)
                    {
                        string watermarkGraph = BuildWatermarkFilterComplex(
                            lightLeakScratchStageLabel,
                            string.Empty,
                            fadeVideoFilter,
                            watermarkInputIndex,
                            brandLogoInputIndex,
                            job,
                            onLog);

                        string finalFilterComplex = $"{lightLeakBaseGraph};{scratchBaseGraph};{watermarkGraph}";

                        if (needsAudioMixing)
                        {
                            var combinedFilterComplex = new StringBuilder(finalFilterComplex);
                            combinedFilterComplex.Append(";");
                            combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                            combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                            combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                            if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                                combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                            else
                                combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                            cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");
                            cmd.Append("-map \"[audio_out]\" ");
                            onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Light Leak Overlay + Dust & Scratches + Audio mixing in single filter_complex");
                            onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                            onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                        }
                        else
                        {
                            cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");

                            if (hasSourceAudio)
                            {
                                string audioMap = "0:a?";
                                cmd.Append($"-map {audioMap} ");

                                string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                    cmd.Append($"-af \"{sourceAudioFilter}\" ");

                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else if (hasExternalAudio)
                            {
                                string audioMap = "1:a?";
                                cmd.Append($"-map {audioMap} ");

                                string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                    cmd.Append($"-af \"{externalAudioFilter}\" ");

                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else
                            {
                                onLog("[AUDIO] disabled (no audio stream)");
                            }

                            onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                        }
                    }
                    else if (needsAudioMixing)
                    {
                        var combinedFilterComplex = new StringBuilder(lightLeakBaseGraph);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append(scratchBaseGraph);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append(finalVideoTail);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                        combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                        combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                        if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                            combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                        else
                            combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                        cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");
                        cmd.Append("-map \"[audio_out]\" ");
                        onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Light Leak Overlay + Dust & Scratches + Audio mixing in single filter_complex");
                        onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                        onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                    }
                    else
                    {
                        string finalFilterComplex = $"{lightLeakBaseGraph};{scratchBaseGraph};{finalVideoTail}";
                        cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");

                        if (hasSourceAudio)
                        {
                            string audioMap = "0:a?";
                            cmd.Append($"-map {audioMap} ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");

                            string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                cmd.Append($"-af \"{sourceAudioFilter}\" ");
                        }
                        else if (hasExternalAudio)
                        {
                            string audioMap = "1:a?";
                            cmd.Append($"-map {audioMap} ");
                            string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                cmd.Append($"-af \"{externalAudioFilter}\" ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else
                        {
                            onLog("[AUDIO] disabled (no audio stream)");
                        }

                        onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                    }
                }
                else if (useRainOverlayEffect)
                {
                    if (rainOverlayRecipe == null)
                        throw new InvalidOperationException("Rain overlay asset recipe was not prepared.");

                    var overlayProfile = ResolveOverlayCanvasProfile(job, targetResW, targetResH);
                    int rainOverlayInputIndex = generatedOverlayInputBaseIndex;
                    string rainOverlayInputArgs = $"{(rainOverlayRecipe.Asset.CanLoop ? "-stream_loop -1 " : string.Empty)}-i \"{rainOverlayRecipe.Asset.FilePath}\" ";
                    cmd.Insert(postInputInsertPoint, rainOverlayInputArgs);
                    postInputInsertPoint += rainOverlayInputArgs.Length;

                    string rainBaseGraph = RainOverlayPreset.BuildSingleClipGraph(
                        "[0:v]",
                        filterChain.ToString(),
                        $"[{rainOverlayInputIndex}:v]",
                        rainOverlayRecipe,
                        overlayProfile.CanvasWidth,
                        overlayProfile.CanvasHeight,
                        lightLeakBurnFps,
                        estimatedOutputDuration,
                        "rainout",
                        onLog);

                    // SOUNDBAR (8/16)
                    string rainStageLabel = "[rainout]";
                    string rainSoundBarGraph = BuildSoundBarGraphStage(rainStageLabel, "soundbarout");
                    if (!string.IsNullOrWhiteSpace(rainSoundBarGraph))
                    {
                        rainBaseGraph = $"{rainBaseGraph};{rainSoundBarGraph}";
                        rainStageLabel = "[soundbarout]";
                    }

                    string rainVideoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                        ? rainStageLabel + fadeVideoFilter + "[vout]"
                        : rainStageLabel + "format=yuv420p[vout]";

                    if (hasImageOverlayAssets)
                    {
                        string watermarkGraph = BuildWatermarkFilterComplex(
                            rainStageLabel,
                            string.Empty,
                            fadeVideoFilter,
                            watermarkInputIndex,
                            brandLogoInputIndex,
                            job,
                            onLog);

                        string finalFilterComplex = $"{rainBaseGraph};{watermarkGraph}";

                        if (needsAudioMixing)
                        {
                            var combinedFilterComplex = new StringBuilder(finalFilterComplex);
                            combinedFilterComplex.Append(";");
                            combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                            combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                            combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                            if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                                combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                            else
                                combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                            cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");
                            cmd.Append("-map \"[audio_out]\" ");
                            onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Rain Overlay + Audio mixing in single filter_complex");
                            onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                        }
                        else
                        {
                            cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");

                            if (hasSourceAudio)
                            {
                                string audioMap = "0:a?";
                                cmd.Append($"-map {audioMap} ");
                                string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                    cmd.Append($"-af \"{sourceAudioFilter}\" ");
                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else if (hasExternalAudio)
                            {
                                string audioMap = "1:a?";
                                cmd.Append($"-map {audioMap} ");
                                string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                    cmd.Append($"-af \"{externalAudioFilter}\" ");
                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else
                            {
                                onLog("[AUDIO] disabled (no audio stream)");
                            }

                            onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                        }
                    }
                    else if (needsAudioMixing)
                    {
                        var combinedFilterComplex = new StringBuilder(rainBaseGraph);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append(rainVideoTail);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                        combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                        combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                        if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                            combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                        else
                            combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                        cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");
                        cmd.Append("-map \"[audio_out]\" ");
                        onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Rain Overlay + Audio mixing in single filter_complex");
                        onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                    }
                    else
                    {
                        string finalFilterComplex = $"{rainBaseGraph};{rainVideoTail}";
                        cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");

                        if (hasSourceAudio)
                        {
                            string audioMap = "0:a?";
                            cmd.Append($"-map {audioMap} ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");

                            string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                cmd.Append($"-af \"{sourceAudioFilter}\" ");
                        }
                        else if (hasExternalAudio)
                        {
                            string audioMap = "1:a?";
                            cmd.Append($"-map {audioMap} ");
                            string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                cmd.Append($"-af \"{externalAudioFilter}\" ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else
                        {
                            onLog("[AUDIO] disabled (no audio stream)");
                        }

                        onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                    }
                }
                else if (useDreamyDotOverlay2Effect)
                {
                    if (dreamyDotOverlay2Recipe == null)
                        throw new InvalidOperationException("Dreamy Dot Overlay 2 asset recipe was not prepared.");

                    var overlayProfile = ResolveOverlayCanvasProfile(job, targetResW, targetResH);
                    int dreamyDot2InputIndex = generatedOverlayInputBaseIndex;
                    string dreamyDot2InputArgs = $"{(dreamyDotOverlay2Recipe.Asset.CanLoop ? "-stream_loop -1 " : string.Empty)}-i \"{dreamyDotOverlay2Recipe.Asset.FilePath}\" ";
                    cmd.Insert(postInputInsertPoint, dreamyDot2InputArgs);
                    postInputInsertPoint += dreamyDot2InputArgs.Length;

                    string dreamyDot2BaseGraph = DreamyDotOverlay2Preset.BuildSingleClipGraph(
                        "[0:v]",
                        filterChain.ToString(),
                        $"[{dreamyDot2InputIndex}:v]",
                        dreamyDotOverlay2Recipe,
                        overlayProfile.CanvasWidth,
                        overlayProfile.CanvasHeight,
                        lightLeakBurnFps,
                        estimatedOutputDuration,
                        "dreamydot2out",
                        onLog);

                    // SOUNDBAR (9/16)
                    string dreamyDot2StageLabel = "[dreamydot2out]";
                    string dreamyDot2SoundBarGraph = BuildSoundBarGraphStage(dreamyDot2StageLabel, "soundbarout");
                    if (!string.IsNullOrWhiteSpace(dreamyDot2SoundBarGraph))
                    {
                        dreamyDot2BaseGraph = $"{dreamyDot2BaseGraph};{dreamyDot2SoundBarGraph}";
                        dreamyDot2StageLabel = "[soundbarout]";
                    }

                    string dreamyDot2VideoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                        ? dreamyDot2StageLabel + fadeVideoFilter + "[vout]"
                        : dreamyDot2StageLabel + "format=yuv420p[vout]";

                    if (hasImageOverlayAssets)
                    {
                        string watermarkGraph = BuildWatermarkFilterComplex(
                            dreamyDot2StageLabel,
                            string.Empty,
                            fadeVideoFilter,
                            watermarkInputIndex,
                            brandLogoInputIndex,
                            job,
                            onLog);

                        string finalFilterComplex = $"{dreamyDot2BaseGraph};{watermarkGraph}";

                        if (needsAudioMixing)
                        {
                            var combinedFilterComplex = new StringBuilder(finalFilterComplex);
                            combinedFilterComplex.Append(";");
                            combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                            combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                            combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                            if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                                combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                            else
                                combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                            cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");
                            cmd.Append("-map \"[audio_out]\" ");
                            onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Dreamy Dot Overlay 2 + Audio mixing in single filter_complex");
                            onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                        }
                        else
                        {
                            cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");

                            if (hasSourceAudio)
                            {
                                string audioMap = "0:a?";
                                cmd.Append($"-map {audioMap} ");
                                string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                    cmd.Append($"-af \"{sourceAudioFilter}\" ");
                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else if (hasExternalAudio)
                            {
                                string audioMap = "1:a?";
                                cmd.Append($"-map {audioMap} ");
                                string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                    cmd.Append($"-af \"{externalAudioFilter}\" ");
                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else
                            {
                                onLog("[AUDIO] disabled (no audio stream)");
                            }

                            onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                        }
                    }
                    else if (needsAudioMixing)
                    {
                        var combinedFilterComplex = new StringBuilder(dreamyDot2BaseGraph);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append(dreamyDot2VideoTail);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                        combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                        combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                        if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                            combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                        else
                            combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                        cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");
                        cmd.Append("-map \"[audio_out]\" ");
                        onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Dreamy Dot Overlay 2 + Audio mixing in single filter_complex");
                        onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                    }
                    else
                    {
                        string finalFilterComplex = $"{dreamyDot2BaseGraph};{dreamyDot2VideoTail}";
                        cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");

                        if (hasSourceAudio)
                        {
                            string audioMap = "0:a?";
                            cmd.Append($"-map {audioMap} ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");

                            string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                cmd.Append($"-af \"{sourceAudioFilter}\" ");
                        }
                        else if (hasExternalAudio)
                        {
                            string audioMap = "1:a?";
                            cmd.Append($"-map {audioMap} ");
                            string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                cmd.Append($"-af \"{externalAudioFilter}\" ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else
                        {
                            onLog("[AUDIO] disabled (no audio stream)");
                        }

                        onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                    }
                }
                else if (useLightLeakOverlayEffect)
                {
                    if (lightLeakOverlayRecipe == null)
                        throw new InvalidOperationException("Light Leak overlay asset recipe was not prepared.");

                    var overlayProfile = ResolveOverlayCanvasProfile(job, targetResW, targetResH);
                    int lightLeakOverlayInputIndex = generatedOverlayInputBaseIndex;
                    string lightLeakOverlayInputArgs = $"{(lightLeakOverlayRecipe.Asset.CanLoop ? "-stream_loop -1 " : string.Empty)}-i \"{lightLeakOverlayRecipe.Asset.FilePath}\" ";
                    cmd.Insert(postInputInsertPoint, lightLeakOverlayInputArgs);
                    postInputInsertPoint += lightLeakOverlayInputArgs.Length;

                    string lightLeakBaseGraph = LightLeakOverlayPreset.BuildSingleClipGraph(
                        "[0:v]",
                        filterChain.ToString(),
                        $"[{lightLeakOverlayInputIndex}:v]",
                        lightLeakOverlayRecipe,
                        overlayProfile.CanvasWidth,
                        overlayProfile.CanvasHeight,
                        lightLeakBurnFps,
                        estimatedOutputDuration,
                        "lightleakout",
                        onLog);
                    var lightLeakGraphParts = new List<string> { lightLeakBaseGraph };
                    string lightLeakCurrentOutputLabel = "[lightleakout]";
                    int lightLeakNextOverlayInputIndex = lightLeakOverlayInputIndex + 1;
                    string lightLeakParticleGraph = AppendOptionalRecipeParticleGraph(lightLeakCurrentOutputLabel, "recipeparticlestage", ref lightLeakNextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(lightLeakParticleGraph))
                    {
                        lightLeakGraphParts.Add(lightLeakParticleGraph);
                        lightLeakCurrentOutputLabel = "[recipeparticlestage]";
                    }

                    string lightLeakSnowfallGraph = AppendOptionalSnowfallGraph(lightLeakCurrentOutputLabel, "snowstage", ref lightLeakNextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(lightLeakSnowfallGraph))
                    {
                        lightLeakGraphParts.Add(lightLeakSnowfallGraph);
                        lightLeakCurrentOutputLabel = "[snowstage]";
                    }

                    // SOUNDBAR (10/16)
                    string lightLeakSoundBarGraph = BuildSoundBarGraphStage(lightLeakCurrentOutputLabel, "soundbarout");
                    if (!string.IsNullOrWhiteSpace(lightLeakSoundBarGraph))
                    {
                        lightLeakGraphParts.Add(lightLeakSoundBarGraph);
                        lightLeakCurrentOutputLabel = "[soundbarout]";
                    }

                    string lightLeakVideoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                        ? lightLeakCurrentOutputLabel + fadeVideoFilter + "[vout]"
                        : lightLeakCurrentOutputLabel + "format=yuv420p[vout]";

                    if (hasImageOverlayAssets)
                    {
                        string watermarkGraph = BuildWatermarkFilterComplex(
                            lightLeakCurrentOutputLabel,
                            string.Empty,
                            fadeVideoFilter,
                            watermarkInputIndex,
                            brandLogoInputIndex,
                            job,
                            onLog);

                        string finalFilterComplex = $"{string.Join(";", lightLeakGraphParts)};{watermarkGraph}";

                        if (needsAudioMixing)
                        {
                            var combinedFilterComplex = new StringBuilder(finalFilterComplex);
                            combinedFilterComplex.Append(";");
                            combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                            combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                            combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                            if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                                combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                            else
                                combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                            cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");
                            cmd.Append("-map \"[audio_out]\" ");
                            onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Light Leak Overlay + Audio mixing in single filter_complex");
                            onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                            onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                        }
                        else
                        {
                            cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");

                            if (hasSourceAudio)
                            {
                                string audioMap = "0:a?";
                                cmd.Append($"-map {audioMap} ");

                                string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                    cmd.Append($"-af \"{sourceAudioFilter}\" ");

                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else if (hasExternalAudio)
                            {
                                string audioMap = "1:a?";
                                cmd.Append($"-map {audioMap} ");

                                string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                    cmd.Append($"-af \"{externalAudioFilter}\" ");

                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else
                            {
                                onLog("[AUDIO] disabled (no audio stream)");
                            }

                            onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                        }
                    }
                    else if (needsAudioMixing)
                    {
                        var combinedFilterComplex = new StringBuilder(string.Join(";", lightLeakGraphParts));
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append(lightLeakVideoTail);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                        combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                        combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                        if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                            combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                        else
                            combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                        cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");
                        cmd.Append("-map \"[audio_out]\" ");
                        onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Light Leak Overlay + Audio mixing in single filter_complex");
                        onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                        onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                    }
                    else
                    {
                        string finalFilterComplex = $"{string.Join(";", lightLeakGraphParts)};{lightLeakVideoTail}";
                        cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");

                        if (hasSourceAudio)
                        {
                            string audioMap = "0:a?";
                            cmd.Append($"-map {audioMap} ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");

                            string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                cmd.Append($"-af \"{sourceAudioFilter}\" ");
                        }
                        else if (hasExternalAudio)
                        {
                            string audioMap = "1:a?";
                            cmd.Append($"-map {audioMap} ");
                            string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                cmd.Append($"-af \"{externalAudioFilter}\" ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else
                        {
                            onLog("[AUDIO] disabled (no audio stream)");
                        }

                        onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                    }
                }
                else if (useDreamyDotOverlayEffect)
                {
                    if (dreamyDotOverlay == null)
                        throw new InvalidOperationException("Dreamy Dot overlay frames were not prepared.");
                    if (string.IsNullOrWhiteSpace(dreamyDotOverlay.PreRenderedAssetPath) || !File.Exists(dreamyDotOverlay.PreRenderedAssetPath))
                        throw new InvalidOperationException("Dreamy Dot overlay asset was not prepared.");

                    int dreamyDotOverlayInputIndex = generatedOverlayInputBaseIndex;
                    string dreamyDotOverlayInputArgs = $"-stream_loop -1 -i \"{dreamyDotOverlay.PreRenderedAssetPath}\" ";
                    cmd.Insert(postInputInsertPoint, dreamyDotOverlayInputArgs);
                    postInputInsertPoint += dreamyDotOverlayInputArgs.Length;

                    string dreamyDotBaseGraph = DreamyDotOverlayPreset.BuildAssetClipGraph(
                        "[0:v]",
                        filterChain.ToString(),
                        $"[{dreamyDotOverlayInputIndex}:v]",
                        dreamyDotOverlay,
                        targetResW,
                        targetResH,
                        lightLeakBurnFps,
                        estimatedOutputDuration,
                        "dreamydotout",
                        onLog);
                    var dreamyDotGraphParts = new List<string> { dreamyDotBaseGraph };
                    string dreamyDotCurrentOutputLabel = "[dreamydotout]";
                    int dreamyDotNextOverlayInputIndex = dreamyDotOverlayInputIndex + 1;
                    string dreamyDotSnowfallGraph = AppendOptionalSnowfallGraph(dreamyDotCurrentOutputLabel, "snowstage", ref dreamyDotNextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(dreamyDotSnowfallGraph))
                    {
                        dreamyDotGraphParts.Add(dreamyDotSnowfallGraph);
                        dreamyDotCurrentOutputLabel = "[snowstage]";
                    }

                    // SOUNDBAR (11/16)
                    string dreamyDotSoundBarGraph = BuildSoundBarGraphStage(dreamyDotCurrentOutputLabel, "soundbarout");
                    if (!string.IsNullOrWhiteSpace(dreamyDotSoundBarGraph))
                    {
                        dreamyDotGraphParts.Add(dreamyDotSoundBarGraph);
                        dreamyDotCurrentOutputLabel = "[soundbarout]";
                    }

                    string dreamyDotVideoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                        ? $"{dreamyDotCurrentOutputLabel}{fadeVideoFilter}[vout]"
                        : $"{dreamyDotCurrentOutputLabel}format=yuv420p[vout]";

                    if (hasImageOverlayAssets)
                    {
                        string watermarkGraph = BuildWatermarkFilterComplex(
                            dreamyDotCurrentOutputLabel,
                            string.Empty,
                            fadeVideoFilter,
                            watermarkInputIndex,
                            brandLogoInputIndex,
                            job,
                            onLog);

                        string finalFilterComplex = $"{string.Join(";", dreamyDotGraphParts)};{watermarkGraph}";

                        if (needsAudioMixing)
                        {
                            var combinedFilterComplex = new StringBuilder(finalFilterComplex);
                            combinedFilterComplex.Append(";");
                            combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                            combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                            combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                            if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                                combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                            else
                                combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                            cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");
                            cmd.Append("-map \"[audio_out]\" ");
                            onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Dreamy Dot Overlay + Audio mixing in single filter_complex");
                            onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                            onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                        }
                        else
                        {
                            cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");

                            if (hasSourceAudio)
                            {
                                string audioMap = "0:a?";
                                cmd.Append($"-map {audioMap} ");
                                string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                    cmd.Append($"-af \"{sourceAudioFilter}\" ");
                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else if (hasExternalAudio)
                            {
                                string audioMap = "1:a?";
                                cmd.Append($"-map {audioMap} ");
                                string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                    cmd.Append($"-af \"{externalAudioFilter}\" ");
                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else
                            {
                                onLog("[AUDIO] disabled (no audio stream)");
                            }

                            onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                        }
                    }
                    else if (needsAudioMixing)
                    {
                        var combinedFilterComplex = new StringBuilder(string.Join(";", dreamyDotGraphParts));
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append(dreamyDotVideoTail);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                        combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                        combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                        if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                            combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                        else
                            combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                        cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");
                        cmd.Append("-map \"[audio_out]\" ");
                        onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Dreamy Dot Overlay + Audio mixing in single filter_complex");
                        onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                        onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                    }
                    else
                    {
                        string finalFilterComplex = $"{string.Join(";", dreamyDotGraphParts)};{dreamyDotVideoTail}";
                        cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");

                        if (hasSourceAudio)
                        {
                            string audioMap = "0:a?";
                            cmd.Append($"-map {audioMap} ");
                            string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                cmd.Append($"-af \"{sourceAudioFilter}\" ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else if (hasExternalAudio)
                        {
                            string audioMap = "1:a?";
                            cmd.Append($"-map {audioMap} ");
                            string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                cmd.Append($"-af \"{externalAudioFilter}\" ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else
                        {
                            onLog("[AUDIO] disabled (no audio stream)");
                        }

                        onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                    }
                }
                else if (useSnowfallOverlayEffect)
                {
                    int snowOverlayInputIndex = generatedOverlayInputBaseIndex;
                    string snowBaseGraph = BuildSnowfallGraphChain(
                        "[0:v]",
                        filterChain.ToString(),
                        "snowout",
                        ref snowOverlayInputIndex);

                    // SOUNDBAR (12/16)
                    string snowStageLabel = "[snowout]";
                    string snowSoundBarGraph = BuildSoundBarGraphStage(snowStageLabel, "soundbarout");
                    if (!string.IsNullOrWhiteSpace(snowSoundBarGraph))
                    {
                        snowBaseGraph = $"{snowBaseGraph};{snowSoundBarGraph}";
                        snowStageLabel = "[soundbarout]";
                    }

                    string snowVideoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                        ? $"{snowStageLabel}{fadeVideoFilter}[vout]"
                        : $"{snowStageLabel}format=yuv420p[vout]";

                    if (hasImageOverlayAssets)
                    {
                        string watermarkGraph = BuildWatermarkFilterComplex(
                            snowStageLabel,
                            string.Empty,
                            fadeVideoFilter,
                            watermarkInputIndex,
                            brandLogoInputIndex,
                            job,
                            onLog);

                        string finalFilterComplex = $"{snowBaseGraph};{watermarkGraph}";

                        if (needsAudioMixing)
                        {
                            var combinedFilterComplex = new StringBuilder(finalFilterComplex);
                            combinedFilterComplex.Append(";");
                            combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                            combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                            combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                            if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                                combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                            else
                                combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                            cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");
                            cmd.Append("-map \"[audio_out]\" ");
                            onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Snowfall Overlay + Audio mixing in single filter_complex");
                            onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                            onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                        }
                        else
                        {
                            cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");

                            if (hasSourceAudio)
                            {
                                string audioMap = "0:a?";
                                cmd.Append($"-map {audioMap} ");
                                string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                    cmd.Append($"-af \"{sourceAudioFilter}\" ");
                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else if (hasExternalAudio)
                            {
                                string audioMap = "1:a?";
                                cmd.Append($"-map {audioMap} ");
                                string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                    cmd.Append($"-af \"{externalAudioFilter}\" ");
                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else
                            {
                                onLog("[AUDIO] disabled (no audio stream)");
                            }

                            onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                        }
                    }
                    else if (needsAudioMixing)
                    {
                        var combinedFilterComplex = new StringBuilder(snowBaseGraph);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append(snowVideoTail);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                        combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                        combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                        if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                            combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                        else
                            combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                        cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");
                        cmd.Append("-map \"[audio_out]\" ");
                        onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Snowfall Overlay + Audio mixing in single filter_complex");
                        onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                        onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                    }
                    else
                    {
                        string finalFilterComplex = $"{snowBaseGraph};{snowVideoTail}";
                        cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");

                        if (hasSourceAudio)
                        {
                            string audioMap = "0:a?";
                            cmd.Append($"-map {audioMap} ");
                            string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                cmd.Append($"-af \"{sourceAudioFilter}\" ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else if (hasExternalAudio)
                        {
                            string audioMap = "1:a?";
                            cmd.Append($"-map {audioMap} ");
                            string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                cmd.Append($"-af \"{externalAudioFilter}\" ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else
                        {
                            onLog("[AUDIO] disabled (no audio stream)");
                        }

                        onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                    }
                }
                else if (useScratchVideoEffect)
                {
                    if (scratchVideoOverlay == null)
                        throw new InvalidOperationException("Dust & Scratches overlay frames were not prepared.");

                    int scratchOverlayInputIndex = generatedOverlayInputBaseIndex;
                    string scratchOverlayInputArgs = $"-f image2 -pattern_type sequence -start_number 1 -framerate {scratchVideoOverlay.FrameRate.ToString("F3", CultureInfo.InvariantCulture)} -i \"{scratchVideoOverlay.OverlayPattern}\" ";
                    cmd.Insert(postInputInsertPoint, scratchOverlayInputArgs);
                    postInputInsertPoint += scratchOverlayInputArgs.Length;

                    string scratchBaseGraph = ScratchVideoPreset.BuildSingleClipGraph(
                        "[0:v]",
                        filterChain.ToString(),
                        $"[{scratchOverlayInputIndex}:v]",
                        scratchVideoOverlay.FrameCount,
                        scratchVideoOverlay.FrameRate,
                        estimatedOutputDuration,
                        "scratchout",
                        onLog);
                    var scratchGraphParts = new List<string> { scratchBaseGraph };
                    string scratchCurrentOutputLabel = "[scratchout]";
                    int scratchNextOverlayInputIndex = scratchOverlayInputIndex + 1;
                    string scratchSnowfallGraph = AppendOptionalSnowfallGraph(scratchCurrentOutputLabel, "snowstage", ref scratchNextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(scratchSnowfallGraph))
                    {
                        scratchGraphParts.Add(scratchSnowfallGraph);
                        scratchCurrentOutputLabel = "[snowstage]";
                    }

                    // SOUNDBAR (13/16)
                    string scratchSoundBarGraph = BuildSoundBarGraphStage(scratchCurrentOutputLabel, "soundbarout");
                    if (!string.IsNullOrWhiteSpace(scratchSoundBarGraph))
                    {
                        scratchGraphParts.Add(scratchSoundBarGraph);
                        scratchCurrentOutputLabel = "[soundbarout]";
                    }

                    string scratchVideoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                        ? $"{scratchCurrentOutputLabel}{fadeVideoFilter}[vout]"
                        : $"{scratchCurrentOutputLabel}format=yuv420p[vout]";

                    if (hasImageOverlayAssets)
                    {
                        string watermarkGraph = BuildWatermarkFilterComplex(
                            scratchCurrentOutputLabel,
                            string.Empty,
                            fadeVideoFilter,
                            watermarkInputIndex,
                            brandLogoInputIndex,
                            job,
                            onLog);

                        string finalFilterComplex = $"{string.Join(";", scratchGraphParts)};{watermarkGraph}";

                        if (needsAudioMixing)
                        {
                            var combinedFilterComplex = new StringBuilder(finalFilterComplex);
                            combinedFilterComplex.Append(";");
                            combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                            combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                            combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                            if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                                combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                            else
                                combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                            cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");
                            cmd.Append("-map \"[audio_out]\" ");
                            onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Dust & Scratches + Audio mixing in single filter_complex");
                            onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                            onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                        }
                        else
                        {
                            cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");

                            if (hasSourceAudio)
                            {
                                string audioMap = "0:a?";
                                cmd.Append($"-map {audioMap} ");

                                string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                    cmd.Append($"-af \"{sourceAudioFilter}\" ");

                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else if (hasExternalAudio)
                            {
                                string audioMap = "1:a?";
                                cmd.Append($"-map {audioMap} ");

                                string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                    cmd.Append($"-af \"{externalAudioFilter}\" ");

                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else
                            {
                                onLog("[AUDIO] disabled (no audio stream)");
                            }

                            onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                        }
                    }
                    else if (needsAudioMixing)
                    {
                        var combinedFilterComplex = new StringBuilder(string.Join(";", scratchGraphParts));
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append(scratchVideoTail);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                        combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                        combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                        if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                            combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                        else
                            combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                        cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");
                        cmd.Append("-map \"[audio_out]\" ");
                        onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Dust & Scratches + Audio mixing in single filter_complex");
                        onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                        onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                    }
                    else
                    {
                        string finalFilterComplex = $"{string.Join(";", scratchGraphParts)};{scratchVideoTail}";
                        cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");

                        if (hasSourceAudio)
                        {
                            string audioMap = "0:a?";
                            cmd.Append($"-map {audioMap} ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");

                            string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                cmd.Append($"-af \"{sourceAudioFilter}\" ");
                        }
                        else if (hasExternalAudio)
                        {
                            string audioMap = "1:a?";
                            cmd.Append($"-map {audioMap} ");
                            string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                cmd.Append($"-af \"{externalAudioFilter}\" ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else
                        {
                            onLog("[AUDIO] disabled (no audio stream)");
                        }

                        onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                    }
                }
                else if (useRecipeGenericOverlayAssetEffect)
                {
                    if (recipeOverlayEffect == null || string.IsNullOrWhiteSpace(recipeOverlayAssetPath))
                        throw new InvalidOperationException("Generic asset overlay recipe was selected, but no asset path was resolved.");

                    int recipeOverlayInputIndex = generatedOverlayInputBaseIndex;
                    string recipeOverlayInputArgs = $"-stream_loop -1 -i \"{recipeOverlayAssetPath}\" ";
                    cmd.Insert(postInputInsertPoint, recipeOverlayInputArgs);
                    postInputInsertPoint += recipeOverlayInputArgs.Length;

                    var overlayProfile = ResolveOverlayCanvasProfile(job, targetResW, targetResH);
                    string recipeBaseGraph = OverlayEffectGraphBuilder.BuildAssetClipGraph(
                        "[0:v]",
                        filterChain.ToString(),
                        $"[{recipeOverlayInputIndex}:v]",
                        recipeOverlayEffect,
                        overlayProfile.CanvasWidth,
                        overlayProfile.CanvasHeight,
                        lightLeakBurnFps,
                        estimatedOutputDuration,
                        "recipeout",
                        onLog);

                    var recipeGraphParts = new List<string> { recipeBaseGraph };
                    string recipeCurrentOutputLabel = "[recipeout]";
                    int recipeNextOverlayInputIndex = recipeOverlayInputIndex + 1;
                    string recipeSnowfallGraph = AppendOptionalSnowfallGraph(recipeCurrentOutputLabel, "snowstage", ref recipeNextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(recipeSnowfallGraph))
                    {
                        recipeGraphParts.Add(recipeSnowfallGraph);
                        recipeCurrentOutputLabel = "[snowstage]";
                    }

                    // SOUNDBAR (14/16)
                    string recipeSoundBarGraph = BuildSoundBarGraphStage(recipeCurrentOutputLabel, "soundbarout");
                    if (!string.IsNullOrWhiteSpace(recipeSoundBarGraph))
                    {
                        recipeGraphParts.Add(recipeSoundBarGraph);
                        recipeCurrentOutputLabel = "[soundbarout]";
                    }

                    string recipeVideoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                        ? $"{recipeCurrentOutputLabel}{fadeVideoFilter}[vout]"
                        : $"{recipeCurrentOutputLabel}format=yuv420p[vout]";

                    if (hasImageOverlayAssets)
                    {
                        string watermarkGraph = BuildWatermarkFilterComplex(
                            recipeCurrentOutputLabel,
                            string.Empty,
                            fadeVideoFilter,
                            watermarkInputIndex,
                            brandLogoInputIndex,
                            job,
                            onLog);

                        string finalFilterComplex = $"{string.Join(";", recipeGraphParts)};{watermarkGraph}";

                        if (needsAudioMixing)
                        {
                            var combinedFilterComplex = new StringBuilder(finalFilterComplex);
                            combinedFilterComplex.Append(";");
                            combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                            combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                            combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                            if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                                combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                            else
                                combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                            cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");
                            cmd.Append("-map \"[audio_out]\" ");
                            onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Recipe asset overlay + Audio mixing in single filter_complex");
                            onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                            onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                        }
                        else
                        {
                            cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");

                            if (hasSourceAudio)
                            {
                                string audioMap = "0:a?";
                                cmd.Append($"-map {audioMap} ");
                                string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                    cmd.Append($"-af \"{sourceAudioFilter}\" ");
                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else if (hasExternalAudio)
                            {
                                string audioMap = "1:a?";
                                cmd.Append($"-map {audioMap} ");
                                string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                    cmd.Append($"-af \"{externalAudioFilter}\" ");
                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else
                            {
                                onLog("[AUDIO] disabled (no audio stream)");
                            }

                            onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                        }
                    }
                    else if (needsAudioMixing)
                    {
                        var combinedFilterComplex = new StringBuilder(string.Join(";", recipeGraphParts));
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append(recipeVideoTail);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                        combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                        combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                        if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                            combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                        else
                            combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                        cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");
                        cmd.Append("-map \"[audio_out]\" ");
                        onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Recipe asset overlay + Audio mixing in single filter_complex");
                        onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                        onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                    }
                    else
                    {
                        string finalFilterComplex = $"{string.Join(";", recipeGraphParts)};{recipeVideoTail}";
                        cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");

                        if (hasSourceAudio)
                        {
                            string audioMap = "0:a?";
                            cmd.Append($"-map {audioMap} ");
                            string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                cmd.Append($"-af \"{sourceAudioFilter}\" ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else if (hasExternalAudio)
                        {
                            string audioMap = "1:a?";
                            cmd.Append($"-map {audioMap} ");
                            string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                cmd.Append($"-af \"{externalAudioFilter}\" ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else
                        {
                            onLog("[AUDIO] disabled (no audio stream)");
                        }

                        onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                    }
                }
                else if (useRecipeParticleOverlayEffect)
                {
                    if (recipeParticleOverlay == null || recipeParticleEffect == null)
                        throw new InvalidOperationException("Recipe particle overlay was selected, but frames were not prepared.");

                    int recipeOverlayInputIndex = generatedOverlayInputBaseIndex;
                    string recipeOverlayInputArgs = $"-f image2 -pattern_type sequence -start_number 1 -framerate {recipeParticleOverlay.FrameRate.ToString("F3", CultureInfo.InvariantCulture)} -i \"{recipeParticleOverlay.OverlayPattern}\" ";
                    cmd.Insert(postInputInsertPoint, recipeOverlayInputArgs);
                    postInputInsertPoint += recipeOverlayInputArgs.Length;

                    string recipeBaseGraph = OverlayEffectGraphBuilder.BuildLoopedSequenceClipGraph(
                        "[0:v]",
                        filterChain.ToString(),
                        $"[{recipeOverlayInputIndex}:v]",
                        EffectRecipeBridge.CreateGeneratedOverlayRecipe(recipeParticleEffect, job),
                        recipeParticleOverlay.FrameCount,
                        recipeParticleOverlay.FrameRate,
                        estimatedOutputDuration,
                        "recipeparticleout",
                        onLog);

                    var recipeGraphParts = new List<string> { recipeBaseGraph };
                    string recipeCurrentOutputLabel = "[recipeparticleout]";
                    int recipeNextOverlayInputIndex = recipeOverlayInputIndex + 1;
                    string recipeSnowfallGraph = AppendOptionalSnowfallGraph(recipeCurrentOutputLabel, "snowstage", ref recipeNextOverlayInputIndex);
                    if (!string.IsNullOrWhiteSpace(recipeSnowfallGraph))
                    {
                        recipeGraphParts.Add(recipeSnowfallGraph);
                        recipeCurrentOutputLabel = "[snowstage]";
                    }

                    // SOUNDBAR (15/16)
                    string recipeParticleSoundBarGraph = BuildSoundBarGraphStage(recipeCurrentOutputLabel, "soundbarout");
                    if (!string.IsNullOrWhiteSpace(recipeParticleSoundBarGraph))
                    {
                        recipeGraphParts.Add(recipeParticleSoundBarGraph);
                        recipeCurrentOutputLabel = "[soundbarout]";
                    }

                    string recipeVideoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                        ? $"{recipeCurrentOutputLabel}{fadeVideoFilter}[vout]"
                        : $"{recipeCurrentOutputLabel}format=yuv420p[vout]";

                    if (hasImageOverlayAssets)
                    {
                        string watermarkGraph = BuildWatermarkFilterComplex(
                            recipeCurrentOutputLabel,
                            string.Empty,
                            fadeVideoFilter,
                            watermarkInputIndex,
                            brandLogoInputIndex,
                            job,
                            onLog);

                        string finalFilterComplex = $"{string.Join(";", recipeGraphParts)};{watermarkGraph}";

                        if (needsAudioMixing)
                        {
                            var combinedFilterComplex = new StringBuilder(finalFilterComplex);
                            combinedFilterComplex.Append(";");
                            combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                            combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                            combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                            if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                                combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                            else
                                combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                            cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");
                            cmd.Append("-map \"[audio_out]\" ");
                            onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Recipe particle overlay + Audio mixing in single filter_complex");
                            onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                            onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                        }
                        else
                        {
                            cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                            cmd.Append("-map \"[vout]\" ");

                            if (hasSourceAudio)
                            {
                                string audioMap = "0:a?";
                                cmd.Append($"-map {audioMap} ");
                                string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                    cmd.Append($"-af \"{sourceAudioFilter}\" ");
                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else if (hasExternalAudio)
                            {
                                string audioMap = "1:a?";
                                cmd.Append($"-map {audioMap} ");
                                string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                    cmd.Append($"-af \"{externalAudioFilter}\" ");
                                onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                            }
                            else
                            {
                                onLog("[AUDIO] disabled (no audio stream)");
                            }

                            onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                        }
                    }
                    else if (needsAudioMixing)
                    {
                        var combinedFilterComplex = new StringBuilder(string.Join(";", recipeGraphParts));
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append(recipeVideoTail);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                        combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                        combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                        if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                            combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                        else
                            combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                        cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");
                        cmd.Append("-map \"[audio_out]\" ");
                        onLog("[FFMPEG-COMBINED-FILTER-COMPLEX] Recipe particle overlay + Audio mixing in single filter_complex");
                        onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                        onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                    }
                    else
                    {
                        string finalFilterComplex = $"{string.Join(";", recipeGraphParts)};{recipeVideoTail}";
                        cmd.Append($"-filter_complex \"{finalFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");

                        if (hasSourceAudio)
                        {
                            string audioMap = "0:a?";
                            cmd.Append($"-map {audioMap} ");
                            string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                cmd.Append($"-af \"{sourceAudioFilter}\" ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else if (hasExternalAudio)
                        {
                            string audioMap = "1:a?";
                            cmd.Append($"-map {audioMap} ");
                            string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                cmd.Append($"-af \"{externalAudioFilter}\" ");
                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else
                        {
                            onLog("[AUDIO] disabled (no audio stream)");
                        }

                        onLog($"[FFMPEG-FILTER-COMPLEX] {finalFilterComplex}");
                    }
                }
                // SOUNDBAR (16/16): no overlay preset branch matched. This path normally emits a
                // plain -vf filter chain, which cannot take a second input, so the
                // condition is widened to soundBarLayerActive and the soundbar stage
                // is built into a filter_complex that also carries filterChain.
                else if (hasImageOverlayAssets || soundBarLayerActive)
                    {
                    string plainSoundBarGraph = BuildSoundBarGraphStage("[0:v]", "soundbarout", filterChain.ToString());
                    bool plainSoundBarApplied = !string.IsNullOrWhiteSpace(plainSoundBarGraph);
                    string plainStageLabel = plainSoundBarApplied ? "[soundbarout]" : "[0:v]";
                    string plainBaseFilters = plainSoundBarApplied ? string.Empty : filterChain.ToString();

                    string filterComplex = plainSoundBarApplied
                        ? $"{plainSoundBarGraph};{BuildWatermarkFilterComplex(plainStageLabel, plainBaseFilters, fadeVideoFilter, watermarkInputIndex, brandLogoInputIndex, job, onLog)}"
                        : BuildWatermarkFilterComplex(plainStageLabel, plainBaseFilters, fadeVideoFilter, watermarkInputIndex, brandLogoInputIndex, job, onLog);

                    if (needsAudioMixing)
                    {
                        var combinedFilterComplex = new StringBuilder(filterComplex);
                        combinedFilterComplex.Append(";");
                        combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];");
                        combinedFilterComplex.Append($"[1:a]{externalAudioInputChain}[ext_audio];");
                        combinedFilterComplex.Append($"[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");

                        if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                            combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                        else
                            combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");

                        cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");
                        cmd.Append("-map \"[audio_out]\" ");
                        onLog($"[FFMPEG-COMBINED-FILTER-COMPLEX] Video + Audio mixing in single filter_complex");
                        onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                        onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                    }
                    else
                    {
                        cmd.Append($"-filter_complex \"{filterComplex}\" ");
                        cmd.Append("-map \"[vout]\" ");

                        if (hasSourceAudio)
                        {
                            string audioMap = "0:a?";
                            cmd.Append($"-map {audioMap} ");

                            string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                                cmd.Append($"-af \"{sourceAudioFilter}\" ");

                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else if (hasExternalAudio)
                        {
                            string audioMap = "1:a?";
                            cmd.Append($"-map {audioMap} ");

                            string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                                cmd.Append($"-af \"{externalAudioFilter}\" ");

                            onLog($"[FFMPEG-MAP] Video=[vout], Audio={audioMap}");
                        }
                        else
                        {
                            onLog("[AUDIO] disabled (no audio stream)");
                        }

                        onLog($"[FFMPEG-FILTER-COMPLEX] {filterComplex}");
                    }
                }
                else if (needsAudioMixing)
                {
                    // [FIX] When audio mixing needed, build combined filter_complex for both video and audio
                    // Cannot use -vf and -filter_complex together, so merge them all into one filter_complex
                    var combinedFilterComplex = new StringBuilder();
                    
                    // Video processing: [0:v] -> apply video filters -> [vout]
                    if (filterChain.Length > 0)
                    {
                        if (!string.IsNullOrWhiteSpace(fadeVideoFilter))
                        {
                            filterChain.Append(",");
                            filterChain.Append(fadeVideoFilter);
                        }
                        combinedFilterComplex.Append($"[0:v]{filterChain.ToString()}[vout];");
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(fadeVideoFilter))
                            combinedFilterComplex.Append($"[0:v]{fadeVideoFilter}[vout];");
                        else
                            combinedFilterComplex.Append("[0:v]null[vout];");
                    }
                    
                    // Audio mixing: [0:a] + [1:a] -> mix -> [audio_out]
                    combinedFilterComplex.Append($"[0:a]{sourceAudioInputChain}[src_audio];[1:a]{externalAudioInputChain}[ext_audio];");
                    combinedFilterComplex.Append("[src_audio][ext_audio]amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]");
                    if (!string.IsNullOrWhiteSpace(audioPostProcessChain))
                        combinedFilterComplex.Append($";[mixed_audio]{audioPostProcessChain}[audio_out]");
                    else
                        combinedFilterComplex.Append(";[mixed_audio]anull[audio_out]");
                    
                    cmd.Append($"-filter_complex \"{combinedFilterComplex}\" ");
                    cmd.Append("-map \"[vout]\" ");
                    cmd.Append("-map \"[audio_out]\" ");
                    onLog($"[FFMPEG-COMBINED-FILTER-COMPLEX] Video + Audio mixing in single filter_complex");
                    onLog($"[FFMPEG-FILTER-COMPLEX] {combinedFilterComplex}");
                    onLog($"[FFMPEG-AUDIO-MIX] Mixing source audio ({videoVolume:P0}) + external audio ({audioVolume:P0})");
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(fadeVideoFilter))
                    {
                        if (filterChain.Length > 0) filterChain.Append(",");
                        filterChain.Append(fadeVideoFilter);
                    }

                    if (filterChain.Length > 0)
                    {
                        // Giai đoạn 3: VAAPI cần upload frame lên GPU cuối chuỗi (-vf path đơn giản;
                        // đường filter_complex đã được che bởi CPU retry bên dưới).
                        string vaapiOutputChain = encoder == "h264_vaapi" ? filterChain + ",format=nv12,hwupload" : filterChain.ToString();
                        cmd.Append($"-vf \"{vaapiOutputChain}\" ");
                    }

                    cmd.Append("-map 0:v:0 ");

                    if (hasSourceAudio)
                    {
                        string audioMap = "0:a?";
                        cmd.Append($"-map {audioMap} ");
                        onLog($"[FFMPEG-MAP] Video=0:v:0, Audio={audioMap}");

                        string sourceAudioFilter = string.Join(",", new[] { sourceAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                        if (!string.IsNullOrWhiteSpace(sourceAudioFilter))
                            cmd.Append($"-af \"{sourceAudioFilter}\" ");
                    }
                    else if (hasExternalAudio)
                    {
                        string audioMap = "1:a?";
                        cmd.Append($"-map {audioMap} ");

                        string externalAudioFilter = string.Join(",", new[] { externalAudioInputChain, audioPostProcessChain }.Where(s => !string.IsNullOrWhiteSpace(s)));
                        if (!string.IsNullOrWhiteSpace(externalAudioFilter))
                            cmd.Append($"-af \"{externalAudioFilter}\" ");
                        onLog($"[FFMPEG-MAP] Video=0:v:0, Audio={audioMap}");
                    }
                    else
                    {
                        onLog("[AUDIO] disabled (no audio stream)");
                    }
                }

                if (hasAnyAudio)
                {
                    cmd.Append("-c:a aac -b:a 128k ");
                    onLog($"[AUDIO] aac @ 128k bitrate (optimized)");
                }
                else
                {
                    cmd.Append("-an ");
                    onLog("[AUDIO] disabled (no audio stream)");
                }

                if (estimatedOutputDuration > 0.01)
                {
                    string outputDurationStr = FfmpegDouble(estimatedOutputDuration, 3);
                    cmd.Append($"-t {outputDurationStr} ");
                    onLog($"[DURATION-SYNC] Output capped to estimated duration: {outputDurationStr}s");
                }

                cmd.Append($"\"{outPath}\"");

                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = cmd.ToString(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = false
                };

                onLog($"[FFMPEG-CMD] {psi.Arguments}");
                onLog($"[FFMPEG-VFILTER] Filter chain: {filterChain}");

                bool needsCpuRetry = false;
                int failedExitCode = 0;
                string failedStderr = string.Empty;

                using (var process = new Process { StartInfo = psi })
                {
                    var stderrOutput = new StringBuilder();
                    var timeRegex = new Regex(@"time=(\d{2}):(\d{2}):(\d{2}\.\d{2})|time=(\d+\.\d+)");
                    bool firstProgress = true;

                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;
                        stderrOutput.AppendLine(e.Data);

                        if (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase) || 
                            e.Data.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                            e.Data.Contains("unrecognized", StringComparison.OrdinalIgnoreCase))
                        {
                            onLog($"[FFMPEG-ERROR] {e.Data}");

                            // [NEW] Diagnostic for GPU failures
                            if (e.Data.Contains("nvenc", StringComparison.OrdinalIgnoreCase) || 
                                e.Data.Contains("gpu", StringComparison.OrdinalIgnoreCase))
                            {
                                onLog($"[GPU-DIAGNOSTIC] NVIDIA NVENC not available - check FFmpeg build or GPU drivers!");
                                onLog($"[GPU-DIAGNOSTIC] Fallback to CPU encoding (libx264)");
                            }
                        }

                        var match = timeRegex.Match(e.Data);
                        if (match.Success && estimatedOutputDuration > 0)
                        {
                            try
                            {
                                TimeSpan ts = TimeSpan.Zero;
                                if (!string.IsNullOrEmpty(match.Groups[1].Value))
                                {
                                    ts = TimeSpan.Parse(match.Value.Replace("time=", ""));
                                }
                                else if (!string.IsNullOrEmpty(match.Groups[4].Value))
                                {
                                    double seconds = double.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
                                    ts = TimeSpan.FromSeconds(seconds);
                                }

                                double percent = (ts.TotalSeconds / estimatedOutputDuration) * 100;
                                if (percent > 100) percent = 100;
                                // Keep UI/API below 100 until all post-steps are fully done.
                                double inProgressPercent = Math.Min(percent, 99.5);
                                onProgress(inProgressPercent);

                                if (firstProgress)
                                {
                                    onLog($"[PROGRESS] {percent:F1}% ({ts.TotalSeconds:F2}s / {estimatedOutputDuration:F2}s)");
                                    firstProgress = false;
                                }
                            }
                            catch { }
                        }
                    };

                    process.Start();
                    process.BeginErrorReadLine();

                    try
                    {
                        await process.WaitForExitAsync(token);
                    }
                    catch (TaskCanceledException)
                    {
                        try { process.Kill(); } catch { }
                        throw;
                    }

                    if (process.ExitCode != 0)
                    {
                        failedExitCode = process.ExitCode;
                        failedStderr = stderrOutput.ToString();

                        onLog($"[ERROR-CODE] {failedExitCode}");
                        onLog($"[FFMPEG-STDERR-DUMP] {failedStderr.Substring(0, Math.Min(500, failedStderr.Length))}");

                        if (!string.Equals(encoder, "libx264", StringComparison.OrdinalIgnoreCase))
                        {
                            onLog($"[ENCODER-RETRY] Primary encoder '{encoder}' failed. Retrying with libx264...");
                            needsCpuRetry = true;
                        }
                        else
                        {
                            throw new Exception($"FFmpeg failed with exit code: {failedExitCode}");
                        }
                    }
                }

                if (needsCpuRetry)
                {
                    string recoveryCodecArgs = BuildVideoCodecArgs("libx264", "slow");
                    string primaryArgs = cmd.ToString();
                    string recoveryArgs = primaryArgs.Replace(videoCodecArgs, recoveryCodecArgs);

                    // Giai đoạn 3: VAAPI fail → gỡ sạch device args + hwupload và trả lại -pix_fmt
                    // cho lệnh retry libx264 (thiết bị/hardware frame không liên quan CPU encode).
                    if (encoder == "h264_vaapi")
                    {
                        if (vaapiDeviceArgs != null)
                            recoveryArgs = recoveryArgs.Replace(vaapiDeviceArgs, string.Empty);
                        recoveryArgs = recoveryArgs.Replace(",format=nv12,hwupload", string.Empty)
                                                   .Replace(videoCodecArgs, recoveryCodecArgs + "-pix_fmt yuv420p ");
                    }

                    if (string.Equals(primaryArgs, recoveryArgs, StringComparison.Ordinal))
                        throw new Exception($"FFmpeg failed with exit code: {failedExitCode}");

                    onLog("[ENCODER-RETRY] Running fallback encode with libx264...");
                    var (recoveryExitCode, recoveryErr) = await RunFfmpegCaptureAsync(recoveryArgs, token);
                    if (recoveryExitCode != 0)
                    {
                        string recoverySummary = string.Join(" | ",
                            recoveryErr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(8));
                        onLog($"[ENCODER-RETRY-ERROR] {recoverySummary}");
                        throw new Exception($"FFmpeg failed with exit code: {failedExitCode}; CPU retry failed with exit code: {recoveryExitCode}");
                    }

                    encoder = "libx264";
                    shouldValidateOutput = false;
                    onLog("[ENCODER-RETRY] CPU fallback succeeded.");
                }

                onLog("[FINALIZING] Encoding completed, finalizing output...");

                if (shouldValidateOutput)
                {
                    onLog("[OUTPUT-VALIDATION] Checking decode integrity (hardware + slow motion)...");
                    bool outputValid = await ValidateOutputBitstreamAsync(outPath, token, onLog);

                    if (!outputValid)
                    {
                        onLog("[OUTPUT-RECOVERY] Corruption detected. Retrying encode with libx264...");

                        string recoveryCodecArgs = BuildVideoCodecArgs("libx264", "slow");
                        string primaryArgs = cmd.ToString();
                        string recoveryArgs = primaryArgs.Replace(videoCodecArgs, recoveryCodecArgs);

                        if (string.Equals(primaryArgs, recoveryArgs, StringComparison.Ordinal))
                            throw new Exception("Could not prepare recovery command for CPU re-encode.");

                        var (recoveryExitCode, recoveryErr) = await RunFfmpegCaptureAsync(recoveryArgs, token);
                        if (recoveryExitCode != 0)
                        {
                            string recoverySummary = string.Join(" | ",
                                recoveryErr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(8));
                            onLog($"[OUTPUT-RECOVERY-ERROR] {recoverySummary}");
                            throw new Exception($"Recovery re-encode failed with exit code: {recoveryExitCode}");
                        }

                        bool recoveryValid = await ValidateOutputBitstreamAsync(outPath, token, onLog);
                        if (!recoveryValid)
                            throw new Exception("Recovery encode completed but output still failed decode validation.");

                        onLog("[OUTPUT-RECOVERY] CPU re-encode succeeded and passed validation.");
                    }
                }

                // [NEW] Set 50% thumbnail logic to avoid bright intro frames
                try
                {
                    if (File.Exists(outPath))
                    {
                        onLog("[THUMBNAIL] Extracting 50% frame for default cover art...");
                        // BOTH scratch files live in the engine temp dir, NOT next to the output.
                        // They used to be outPath + ".thumb.jpg" / ".mux.mp4": scratch names that
                        // Explorer shows as a mysterious companion JPG beside the finished MP4
                        // whenever the cleanup lost a race with the antivirus/ indexer locking the
                        // just-written file - the delete threw, the catch swallowed it, and the
                        // "thumbnail" stayed in the user's output folder for good.
                        string tempThumbPath = Path.Combine(GetTitanTempDir(), $"cover_{System.Guid.NewGuid():N}.jpg");
                        string tempMuxPath = Path.Combine(GetTitanTempDir(), $"cover_{System.Guid.NewGuid():N}.mux.mp4");

                        double actualDuration = estimatedOutputDuration;
                        if (actualDuration <= 0) actualDuration = 10.0; // fallback if unknown
                        double halfDuration = Math.Max(0.1, actualDuration / 2.0);

                        var extractPsi = new ProcessStartInfo
                        {
                            FileName = _ffmpegPath,
                            Arguments = $"-y -ss {FfmpegDouble(halfDuration, 3)} -i \"{outPath}\" -vframes 1 -q:v 2 \"{tempThumbPath}\"",
                            UseShellExecute = false, CreateNoWindow = true
                        };
                        using (var p = Process.Start(extractPsi)) { p?.WaitForExit(); }

                        if (File.Exists(tempThumbPath))
                        {
                            onLog("[THUMBNAIL] Embedding cover art...");
                            var muxPsi = new ProcessStartInfo
                            {
                                FileName = _ffmpegPath,
                                Arguments = $"-y -i \"{outPath}\" -i \"{tempThumbPath}\" -map 0:v:0 -map 0:a? -map 1 -c copy -c:v:1 mjpeg -disposition:v:1 attached_pic \"{tempMuxPath}\"",
                                UseShellExecute = false, CreateNoWindow = true
                            };
                            using (var p = Process.Start(muxPsi)) { p?.WaitForExit(); }

                            if (File.Exists(tempMuxPath))
                            {
                                File.Delete(outPath);
                                File.Move(tempMuxPath, outPath);
                            }
                            TryDeleteFileSafe(tempThumbPath);
                            TryDeleteFileSafe(tempMuxPath);
                            onLog("[THUMBNAIL] Cover art successfully applied.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    onLog($"[THUMBNAIL-WARN] Failed to set thumbnail: {ex.Message}");
                }

                onProgress(100.0);
                job.ResultPath = outPath;


                // Split rendered video if SplitDurationSeconds is set
                if (job.SplitDurationSeconds > 0.01 && File.Exists(outPath))
                {
                    onLog($"[SPLIT] Splitting output into {job.SplitDurationSeconds:F1}s segments...");
                    var splitPaths = await SplitRenderedVideoAsync(outPath, job.SplitDurationSeconds, encoder, token, onLog);
                    if (splitPaths.Count > 0)
                    {
                        onLog($"[SPLIT] Done Ã¢â‚¬â€ {splitPaths.Count} segment(s) created.");
                    }
                    else
                    {
                        onLog("[SPLIT-WARN] Split produced no output segments.");
                    }
                }
            }
            catch (Exception ex)
            {
                job.StatusDisplay = "FAILED";
                job.StatusColor = "Red";
                onLog($"[ERROR] {ex.Message}");
                throw;
            }
            finally
            {
                TryDeleteFileSafe(tempConcatListPath);
                TryDeleteFileSafe(tempMergedSourcePath);
                TryDeleteFileSafe(tempAudioConcatListPath);
                TryDeleteFileSafe(tempMergedAudioPath);
                TryDeleteFileSafe(tempAudioOverlayMixPath);
                TryDeleteFileSafe(tempEditedAudioPath);
                foreach (string tempPath in tempTemplateArtifacts)
                    TryDeleteFileSafe(tempPath);
            }
        }

        private static async Task<double> GetVideoDurationForSplitAsync(string filePath)
        {
            if (string.IsNullOrEmpty(_ffprobePath) || !File.Exists(_ffprobePath) || !File.Exists(filePath))
                return 0.0;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _ffprobePath,
                    Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    string output = await process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    if (double.TryParse(output.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double dur))
                        return dur;
                }
            }
            catch { }
            return 0.0;
        }

        /// <summary>
        /// Splits a rendered video into exact segments by querying duration and rendering each part individually.
        /// </summary>
        private static async Task<List<string>> SplitRenderedVideoAsync(string inputPath, double segmentDurationSec, string encoder, CancellationToken token, Action<string> onLog)
        {
            var result = new List<string>();
            try
            {
                string? dir = Path.GetDirectoryName(inputPath);
                string baseName = Path.GetFileNameWithoutExtension(inputPath);
                string ext = Path.GetExtension(inputPath);
                if (string.IsNullOrEmpty(dir)) dir = AppDomain.CurrentDomain.BaseDirectory;

                string encoderArgs = BuildVideoCodecArgs(encoder, "medium");

                double totalDurationSec = await GetVideoDurationForSplitAsync(inputPath);
                if (totalDurationSec <= 0.0)
                {
                    onLog("[SPLIT-ERROR] Cannot get total duration of video. Falling back to segment muxer...");
                    // Fallback to segment muxer
                    string pattern = Path.Combine(dir, $"{baseName}_part%03d{ext}");
                    string args = $"-y -i \"{inputPath}\" -force_key_frames \"expr:gte(t,n_forced*{segmentDurationSec:F2})\" {encoderArgs} -c:a aac -b:a 192k -f segment -segment_time {segmentDurationSec:F2} -reset_timestamps 1 \"{pattern}\"";
                    
                    var psi = new System.Diagnostics.ProcessStartInfo { FileName = _ffmpegPath ?? "ffmpeg.exe", Arguments = args, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = false };
                    using var process = new System.Diagnostics.Process { StartInfo = psi };
                    process.Start();
                    await process.WaitForExitAsync(token);
                    
                    for (int i = 0; i < 999; i++)
                    {
                        string segPath = Path.Combine(dir, $"{baseName}_part{i:D3}{ext}");
                        if (File.Exists(segPath)) result.Add(segPath); else break;
                    }
                    return result;
                }

                int parts = (int)Math.Ceiling(totalDurationSec / segmentDurationSec);
                onLog($"[SPLIT] Total duration: {totalDurationSec:F2}s. Splitting into {parts} exact parts of {segmentDurationSec:F2}s...");

                for (int i = 0; i < parts; i++)
                {
                    token.ThrowIfCancellationRequested();
                    
                    double start = i * segmentDurationSec;
                    double durationToEncode = Math.Min(segmentDurationSec, totalDurationSec - start);
                    if (durationToEncode < 0.1) break;

                    string partPath = Path.Combine(dir, $"{baseName}_part{i:D3}{ext}");
                    string args = $"-y -ss {start:F3} -t {durationToEncode:F3} -i \"{inputPath}\" {encoderArgs} -c:a aac -b:a 192k \"{partPath}\"";

                    onLog($"[SPLIT] Processing part {i + 1}/{parts} ({durationToEncode:F2}s)...");

                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = _ffmpegPath ?? "ffmpeg.exe",
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = false
                    };

                    using var process = new System.Diagnostics.Process { StartInfo = psi };
                    var errLines = new List<string>();
                    process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) errLines.Add(e.Data); };
                    process.Start();
                    process.BeginErrorReadLine();
                    await process.WaitForExitAsync(token);

                    if (process.ExitCode == 0 && File.Exists(partPath))
                    {
                        result.Add(partPath);
                        onLog($"[SPLIT] Created: {Path.GetFileName(partPath)}");
                    }
                    else
                    {
                        string lastErr = errLines.Count > 0 ? errLines[^1] : "unknown error";
                        onLog($"[SPLIT-ERROR] Failed on part {i + 1}. Exit code: {process.ExitCode}, Err: {lastErr}");
                    }
                }
            }
            catch (Exception ex)
            {
                onLog($"[SPLIT-ERROR] {ex.Message}");
            }
            return result;
        }

        public static async Task<(int Width, int Height)> GetVideoResolutionAsync(string filePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var imageResolution = TryGetStillImageResolution(filePath);
                    if (imageResolution.Width > 0 && imageResolution.Height > 0)
                        return imageResolution;

                    if (string.IsNullOrEmpty(_ffprobePath) || !File.Exists(_ffprobePath))
                        return (1920, 1080);

                    if (!File.Exists(filePath))
                        return (1920, 1080);

                    var psi = new ProcessStartInfo
                    {
                        FileName = _ffprobePath,
                        Arguments = $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 \"{filePath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(psi))
                    {
                        if (process == null) return (1920, 1080);

                        string output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit();

                        if (process.ExitCode == 0)
                        {
                            var parts = output.Trim().Split(',');
                            if (parts.Length == 2 &&
                                int.TryParse(parts[0], out int width) &&
                                int.TryParse(parts[1], out int height) &&
                                width > 0 && height > 0)
                            {
                                return (width, height);
                            }
                        }
                        return (1920, 1080);
                    }
                }
                catch
                {
                    return (1920, 1080);
                }
            });
        }

        public static async Task<double> GetVideoFrameRateAsync(string filePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(_ffprobePath) || !File.Exists(_ffprobePath))
                        return 0.0;

                    if (!File.Exists(filePath))
                        return 0.0;

                    var psi = new ProcessStartInfo
                    {
                        FileName = _ffprobePath,
                        Arguments = $"-v error -select_streams v:0 -show_entries stream=avg_frame_rate,r_frame_rate -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(psi))
                    {
                        if (process == null) return 0.0;

                        string output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit();

                        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                            return 0.0;

                        foreach (string rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            double parsedRate = ParseFrameRateValue(rawLine.Trim());
                            if (parsedRate > 0.0)
                            {
                                return parsedRate;
                            }
                        }

                        return 0.0;
                    }
                }
                catch
                {
                    return 0.0;
                }
            });
        }

        public static async Task<bool> ExtractFrameToImageAsync(string videoPath, double timeSec, string outputImagePath, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(_ffmpegPath) || !File.Exists(_ffmpegPath))
                return false;

            if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
                return false;

            string? outputDir = Path.GetDirectoryName(outputImagePath);
            if (!string.IsNullOrWhiteSpace(outputDir))
                Directory.CreateDirectory(outputDir);

            double safeTime = Math.Max(0.0, timeSec);
            string timeText = safeTime.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = $"-hide_banner -loglevel error -y -ss {timeText} -i \"{videoPath}\" -an -frames:v 1 -q:v 2 -update 1 \"{outputImagePath}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                CreateNoWindow = true
            };

            try
            {
                using var process = new Process { StartInfo = psi };
                process.Start();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();

                try
                {
                    await process.WaitForExitAsync(token);
                }
                catch (TaskCanceledException)
                {
                    try { process.Kill(); } catch { }
                    return false;
                }

                _ = await stderrTask;
                return process.ExitCode == 0 && File.Exists(outputImagePath);
            }
            catch
            {
                return false;
            }
        }

        public static async Task<VideoAssetProbeInfo> ValidateLightLeakAssetRecipeAsync(
            string effectName,
            LightLeakOverlayRecipe? recipe,
            CancellationToken token,
            Action<string> onLog)
        {
            if (recipe?.Asset == null || string.IsNullOrWhiteSpace(recipe.Asset.FilePath))
            {
                onLog("[LIGHT-LEAK-ASSET] Selected preset: FilmBurnWarm");
                onLog($"[LIGHT-LEAK-ASSET] Selected light leak asset id: {(recipe?.Asset?.Id ?? "<none>")}");
                onLog("[LIGHT-LEAK-ASSET] Selected asset file path: <missing>");
                onLog("[LIGHT-LEAK-ASSET] Asset exists: false");
                onLog($"[LIGHT-LEAK-ASSET] Asset type: {(recipe?.Asset?.Type ?? "unknown")}");
                throw new InvalidOperationException("Light leak asset missing.");
            }

            string assetPath = Path.GetFullPath(recipe.Asset.FilePath.Trim());
            bool exists = File.Exists(assetPath);
            LightLeakOverlayUsage? usage = recipe.Usages.FirstOrDefault();
            double loggedOpacity = usage?.Opacity ?? recipe.Asset.DefaultOpacity;
            double loggedDuration = usage?.DurationSeconds ?? recipe.Asset.RecommendedDuration;
            onLog("[LIGHT-LEAK-ASSET] Selected preset: FilmBurnWarm");
            onLog($"[LIGHT-LEAK-ASSET] Selected light leak asset id: {recipe.Asset.Id}");
            onLog($"[LIGHT-LEAK-ASSET] Selected asset file path: {assetPath}");
            onLog($"[LIGHT-LEAK-ASSET] Asset exists: {exists}");
            onLog($"[LIGHT-LEAK-ASSET] Asset type: {recipe.Asset.Type}");
            onLog($"[LIGHT-LEAK-ASSET] Blend mode: {recipe.Asset.BlendMode}");
            onLog($"[LIGHT-LEAK-ASSET] Opacity: {loggedOpacity.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}");
            onLog($"[LIGHT-LEAK-ASSET] Duration: {loggedDuration.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}s");

            if (!exists)
                throw new InvalidOperationException("Light leak asset missing.");

            VideoAssetProbeInfo probe = await ProbeVideoAssetAsync(assetPath, token);
            onLog($"[LIGHT-LEAK-ASSET] File size: {probe.FileSize}");
            onLog($"[LIGHT-LEAK-ASSET] Modified UTC: {probe.LastModifiedTimeUtc:O}");
            onLog($"[LIGHT-LEAK-ASSET] ffprobe codec={probe.CodecName} duration={probe.Duration:F3}s fps={probe.Fps:F3} resolution={probe.Width}x{probe.Height} pix_fmt={probe.PixelFormat}");

            if (!probe.ProbeSucceeded || !probe.HasVideoStream)
            {
                string reason = string.IsNullOrWhiteSpace(probe.FailureReason)
                    ? "ffprobe could not verify a valid video stream."
                    : probe.FailureReason;
                onLog($"[LIGHT-LEAK-ASSET-ERROR] {reason}");
                throw new InvalidOperationException("Light leak asset missing.");
            }

            OverlayAssetBackgroundAnalysis analysis = await OverlayAssetBackgroundAnalyzer.AnalyzeAsync(assetPath, token);
            recipe.RuntimeAnalysis = analysis;
            LogOverlayBackgroundAnalysis("[LIGHT-LEAK-ASSET]", analysis, recipe.Asset.BlendMode, loggedOpacity, onLog);

            onLog($"[LIGHT-LEAK-ASSET] {effectName} asset verified with ffprobe.");
            return probe;
        }

        public static async Task<VideoAssetProbeInfo> ValidateRainOverlayAssetRecipeAsync(
            string effectName,
            RainOverlayRecipe? recipe,
            CancellationToken token,
            Action<string> onLog)
        {
            if (recipe?.Asset == null || string.IsNullOrWhiteSpace(recipe.Asset.FilePath))
            {
                onLog($"[RAIN-ASSET] Selected rain asset id: {(recipe?.Asset?.Id ?? "<none>")}");
                onLog("[RAIN-ASSET] Selected rain asset path: <missing>");
                onLog("[RAIN-ASSET] Asset exists: false");
                onLog($"[RAIN-ASSET] Asset type: {(recipe?.Asset?.AssetType ?? "unknown")}");
                throw new InvalidOperationException("Rain overlay asset missing.");
            }

            string assetPath = Path.GetFullPath(recipe.Asset.FilePath.Trim());
            bool exists = File.Exists(assetPath);
            onLog($"[RAIN-ASSET] Selected rain asset id: {recipe.Asset.Id}");
            onLog($"[RAIN-ASSET] Selected rain asset path: {assetPath}");
            onLog($"[RAIN-ASSET] Asset exists: {exists}");
            onLog($"[RAIN-ASSET] Asset type: {recipe.Asset.AssetType}");

            if (!exists)
                throw new InvalidOperationException("Rain overlay asset missing.");

            VideoAssetProbeInfo probe = await ProbeVideoAssetAsync(assetPath, token);
            onLog($"[RAIN-ASSET] File size: {probe.FileSize}");
            onLog($"[RAIN-ASSET] Modified UTC: {probe.LastModifiedTimeUtc:O}");
            onLog($"[RAIN-ASSET] ffprobe codec={probe.CodecName} duration={probe.Duration:F3}s fps={probe.Fps:F3} resolution={probe.Width}x{probe.Height} pix_fmt={probe.PixelFormat}");

            if (!probe.ProbeSucceeded || !probe.HasVideoStream)
            {
                string reason = string.IsNullOrWhiteSpace(probe.FailureReason)
                    ? "ffprobe could not verify a valid video stream."
                    : probe.FailureReason;
                onLog($"[RAIN-ASSET-ERROR] {reason}");
                throw new InvalidOperationException("Rain overlay asset missing.");
            }

            OverlayAssetBackgroundAnalysis analysis = await OverlayAssetBackgroundAnalyzer.AnalyzeAsync(assetPath, token);
            recipe.RuntimeAnalysis = analysis;
            LogOverlayBackgroundAnalysis("[RAIN-ASSET]", analysis, recipe.Asset.BlendMode, recipe.Opacity, onLog);

            onLog($"[RAIN-ASSET] {effectName} asset verified with ffprobe.");
            return probe;
        }

        public static async Task<VideoAssetProbeInfo> ValidateDreamyDotOverlay2AssetRecipeAsync(
            string effectName,
            DreamyDotOverlay2Recipe? recipe,
            CancellationToken token,
            Action<string> onLog)
        {
            onLog("[DREAMY-DOT-2-ASSET] Selected preset: DreamyDotOverlay2");
            onLog($"[DREAMY-DOT-2-ASSET] Asset path: {DreamyDotOverlay2Preset.AssetRelativePath.Replace('\\', '/')}");

            if (recipe?.Asset == null || string.IsNullOrWhiteSpace(recipe.Asset.FilePath))
            {
                onLog("[DREAMY-DOT-2-ASSET] Asset exists: false");
                onLog("[DREAMY-DOT-2-ASSET] Asset type: blackBackgroundOrDarkBokeh");
                onLog("[DREAMY-DOT-2-ASSET] Blend mode: screen");
                onLog("[DREAMY-DOT-2-ASSET] Opacity: 0.120");
                onLog("[DREAMY-DOT-2-ASSET] Can loop: true");
                throw new InvalidOperationException("Dreamy Dot Overlay 2 asset missing.");
            }

            string assetPath = Path.GetFullPath(recipe.Asset.FilePath.Trim());
            bool exists = File.Exists(assetPath);
            onLog($"[DREAMY-DOT-2-ASSET] Resolved asset file path: {assetPath}");
            onLog($"[DREAMY-DOT-2-ASSET] Asset exists: {exists}");
            onLog($"[DREAMY-DOT-2-ASSET] Asset type: {recipe.Asset.AssetType}");
            onLog($"[DREAMY-DOT-2-ASSET] Blend mode: {recipe.Asset.BlendMode}");
            onLog($"[DREAMY-DOT-2-ASSET] Opacity: {recipe.Opacity.ToString("0.000", CultureInfo.InvariantCulture)}");
            onLog($"[DREAMY-DOT-2-ASSET] Can loop: {recipe.Asset.CanLoop}");

            if (!exists)
                throw new InvalidOperationException("Dreamy Dot Overlay 2 asset missing.");

            VideoAssetProbeInfo probe = await ProbeVideoAssetAsync(assetPath, token);
            onLog($"[DREAMY-DOT-2-ASSET] File size: {probe.FileSize}");
            onLog($"[DREAMY-DOT-2-ASSET] Modified UTC: {probe.LastModifiedTimeUtc:O}");
            onLog($"[DREAMY-DOT-2-ASSET] ffprobe codec={probe.CodecName} duration={probe.Duration:F3}s fps={probe.Fps:F3} resolution={probe.Width}x{probe.Height} pix_fmt={probe.PixelFormat}");

            if (!probe.ProbeSucceeded || !probe.HasVideoStream)
            {
                string reason = string.IsNullOrWhiteSpace(probe.FailureReason)
                    ? "ffprobe could not verify a valid video stream."
                    : probe.FailureReason;
                onLog($"[DREAMY-DOT-2-ASSET-ERROR] {reason}");
                throw new InvalidOperationException("Dreamy Dot Overlay 2 asset missing.");
            }

            OverlayAssetBackgroundAnalysis analysis = await OverlayAssetBackgroundAnalyzer.AnalyzeAsync(assetPath, token);
            recipe.RuntimeAnalysis = analysis;
            LogOverlayBackgroundAnalysis("[DREAMY-DOT-2-ASSET]", analysis, recipe.Asset.BlendMode, recipe.Opacity, onLog);

            onLog($"[DREAMY-DOT-2-ASSET] {effectName} asset verified with ffprobe.");
            return probe;
        }

        public static async Task<VideoAssetProbeInfo> ValidateSnowfallOverlayAssetRecipeAsync(
            string effectName,
            SnowfallOverlayRecipe? recipe,
            CancellationToken token,
            Action<string> onLog)
        {
            if (recipe?.Asset == null || string.IsNullOrWhiteSpace(recipe.Asset.FilePath))
                throw new InvalidOperationException("Snowfall overlay asset missing.");

            string assetPath = Path.GetFullPath(recipe.Asset.FilePath.Trim());
            bool exists = File.Exists(assetPath);
            onLog($"[SNOWFALL-ASSET] Asset path: {assetPath}");
            onLog($"[SNOWFALL-ASSET] Asset exists: {exists}");
            onLog($"[SNOWFALL-ASSET] Asset type: {recipe.Asset.Type}");
            onLog($"[SNOWFALL-ASSET] Blend mode: {recipe.Asset.BlendMode}");
            if (!exists)
                throw new InvalidOperationException("Snowfall overlay asset missing.");

            VideoAssetProbeInfo probe = await ProbeVideoAssetAsync(assetPath, token);
            onLog($"[SNOWFALL-ASSET] File size: {probe.FileSize}");
            onLog($"[SNOWFALL-ASSET] Modified UTC: {probe.LastModifiedTimeUtc:O}");
            onLog($"[SNOWFALL-ASSET] ffprobe codec={probe.CodecName} duration={probe.Duration:F3}s fps={probe.Fps:F3} resolution={probe.Width}x{probe.Height} pix_fmt={probe.PixelFormat}");

            if (!probe.ProbeSucceeded || !probe.HasVideoStream)
                throw new InvalidOperationException("Snowfall overlay asset missing.");

            OverlayAssetBackgroundAnalysis analysis = await OverlayAssetBackgroundAnalyzer.AnalyzeAsync(assetPath, token);
            recipe.RuntimeAnalysis = analysis;
            LogOverlayBackgroundAnalysis("[SNOWFALL-ASSET]", analysis, recipe.Asset.BlendMode, recipe.Opacity, onLog);
            onLog($"[SNOWFALL-ASSET] {effectName} asset verified with ffprobe.");
            return probe;
        }

        private static void LogOverlayBackgroundAnalysis(
            string prefix,
            OverlayAssetBackgroundAnalysis analysis,
            string blendMode,
            double opacity,
            Action<string> onLog)
        {
            onLog($"{prefix} hasAlpha: {analysis.HasAlpha}");
            onLog($"{prefix} greenCoverageFullFrame: {analysis.GreenCoverageFullFrame.ToString("0.000", CultureInfo.InvariantCulture)}");
            onLog($"{prefix} greenCoverageBorder: {analysis.GreenCoverageBorder.ToString("0.000", CultureInfo.InvariantCulture)}");
            onLog($"{prefix} darkCoverageFullFrame: {analysis.DarkCoverageFullFrame.ToString("0.000", CultureInfo.InvariantCulture)}");
            onLog($"{prefix} darkCoverageBorder: {analysis.DarkCoverageBorder.ToString("0.000", CultureInfo.InvariantCulture)}");
            onLog($"{prefix} Detected background: {analysis.BackgroundType}");
            onLog($"{prefix} Pipeline: {analysis.PipelineName}");
            onLog($"{prefix} Opacity: {(analysis.OpacityOverride > 0 ? analysis.OpacityOverride : opacity).ToString("0.000", CultureInfo.InvariantCulture)}");
            onLog($"{prefix} Blend mode: {blendMode}");
            if (analysis.BackgroundType == OverlayBackgroundType.GreenScreen)
            {
                onLog($"{prefix} KeyColor: {analysis.KeyColor}");
                onLog($"{prefix} Similarity: {analysis.Similarity.ToString("0.000", CultureInfo.InvariantCulture)}");
                onLog($"{prefix} Blend: {analysis.Blend.ToString("0.000", CultureInfo.InvariantCulture)}");
            }
            if (!string.IsNullOrWhiteSpace(analysis.Warning))
                onLog($"{prefix} WARN: {analysis.Warning}");
        }

        private static async Task<string?> TryGetDreamyDotOverlayAssetAsync(
            DreamyDotGeneratedOverlay overlay,
            CancellationToken token,
            Action<string> onLog)
        {
            if (overlay == null)
                throw new ArgumentNullException(nameof(overlay));

            string assetPath = GetDreamyDotOverlayAssetPath(overlay);
            if (!File.Exists(assetPath))
                return null;

            VideoAssetProbeInfo existingProbe = await ProbeVideoAssetAsync(assetPath, token);
            if (existingProbe.ProbeSucceeded && existingProbe.HasVideoStream)
            {
                onLog($"[DREAMY-DOT-ASSET] Reusing pre-rendered asset: {assetPath}");
                onLog($"[DREAMY-DOT-ASSET] ffprobe duration={existingProbe.Duration:F3}s fps={existingProbe.Fps:F3} resolution={existingProbe.Width}x{existingProbe.Height} pix_fmt={existingProbe.PixelFormat}");
                return assetPath;
            }

            try { File.Delete(assetPath); } catch { }
            return null;
        }

        private static string GetDreamyDotOverlayAssetPath(DreamyDotGeneratedOverlay overlay)
        {
            if (overlay == null)
                throw new ArgumentNullException(nameof(overlay));

            string assetRoot = ResolvePreferredDreamyDotAssetRoot();
            Directory.CreateDirectory(assetRoot);

            string variantSlug = Regex.Replace(
                string.IsNullOrWhiteSpace(overlay.Variant) ? "classic" : overlay.Variant.Trim().ToLowerInvariant(),
                @"[^\w\-]+",
                "_");
            string fpsSlug = FfmpegDouble(overlay.FrameRate, 3).Replace(".", "_");
            return Path.Combine(assetRoot, $"dreamy_dot_{variantSlug}_{overlay.Width}x{overlay.Height}_{fpsSlug}.mp4");
        }

        // [NEW] UPSCAYL INTEGRATION - AI-Powered Upscaling via External Tool
        public static async Task<bool> UpscaleWithUpscaylAsync(string inputPath, string outputPath, string mode, Action<string> onLog, CancellationToken token)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    if (string.IsNullOrEmpty(_upscaylPath) || !File.Exists(_upscaylPath))
                    {
                        onLog($"[UPSCAYL] Not found at: {_upscaylPath}");
                        onLog($"[UPSCAYL] Please place Upscayl folder at: {AppDomain.CurrentDomain.BaseDirectory}upscayl\\");
                        return false;
                    }

                    if (!File.Exists(inputPath))
                    {
                        onLog($"[UPSCAYL-ERROR] Input file not found: {inputPath}");
                        return false;
                    }

                    // Determine upscale model and scale
                    string model = "realesrgan";  // Best quality model
                    string scale = mode.Contains("4x") ? "4" : "2";

                    onLog($"[UPSCAYL] Starting upscale: {Path.GetFileName(inputPath)}");
                    onLog($"[UPSCAYL-MODE] Model: {model}, Scale: {scale}x");

                    var psi = new ProcessStartInfo
                    {
                        FileName = _upscaylPath,
                        Arguments = $"-i \"{inputPath}\" -o \"{outputPath}\" -s {scale} -m {model}",
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(_upscaylPath)
                    };

                    onLog($"[UPSCAYL-CMD] {psi.FileName} {psi.Arguments}");

                    using (var process = new Process { StartInfo = psi })
                    {
                        var progressRegex = new Regex(@"(\d+(?:\.\d+)?)\%|progress:\s*(\d+(?:\.\d+)?)");

                        process.OutputDataReceived += (sender, e) =>
                        {
                            if (!string.IsNullOrEmpty(e.Data))
                            {
                                onLog($"[UPSCAYL-LOG] {e.Data}");

                                var match = progressRegex.Match(e.Data);
                                if (match.Success)
                                {
                                    onLog($"[UPSCAYL-PROGRESS] {e.Data}");
                                }
                            }
                        };

                        process.ErrorDataReceived += (sender, e) =>
                        {
                            if (!string.IsNullOrEmpty(e.Data))
                            {
                                onLog($"[UPSCAYL-ERR] {e.Data}");
                            }
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
                            onLog($"[UPSCAYL] Cancelled");
                            return false;
                        }

                        if (process.ExitCode == 0 && File.Exists(outputPath))
                        {
                            onLog($"[UPSCAYL-SUCCESS] Upscale complete: {Path.GetFileName(outputPath)}");
                            return true;
                        }
                        else
                        {
                            onLog($"[UPSCAYL-FAILED] Exit code: {process.ExitCode}");
                            return false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    onLog($"[UPSCAYL-EXCEPTION] {ex.Message}");
                    return false;
                }
            }, token);
        }

        private static double NormalizePlaybackSpeed(double speed)
        {
            if (double.IsNaN(speed) || double.IsInfinity(speed) || speed <= 0.0)
                return 1.0;

            return Math.Clamp(speed, 0.10, 8.0);
        }

        /// <summary>
        /// Decodes and scales a still image ONCE so a <c>-loop 1</c> input stops paying the full
        /// source decode on every output frame: the image2 demuxer re-decodes the file per frame,
        /// so a 12 MP phone photo looped into a 1080p canvas spent ~0.74 s of decode per output
        /// second (measured: 12 s of 1080x1920 from a 4032x3024 PNG = 8.9 s raw vs 1.7 s with
        /// this prescale plus a 0.3 s one-off).
        /// <para>
        /// <paramref name="scaleExpression"/> must MIRROR the scale the consuming graph itself
        /// applies, so the graph's own scale becomes an identity over identical pixels - the
        /// output is unchanged and only the decode work moves out of the loop. (JPEG sources
        /// take one extra RGB roundtrip through the lossless PNG intermediate; PNG sources are
        /// exactly the pixels the graph would have produced.)
        /// </para>
        /// <para>
        /// Returns null - and the caller keeps looping the original - unless the source long
        /// edge is at least 1.3x the target long edge, so an identity or near-identity prescale
        /// never adds a resample of its own. Failures are logged and also return null: the
        /// prescale is an optimisation, never a dependency.
        /// </para>
        /// </summary>
        private static async Task<string?> TryPrescaleStillForLoopAsync(
            string stillPath,
            string scaleExpression,
            int targetWidth,
            int targetHeight,
            string outDir,
            Action<string> onLog,
            CancellationToken token)
        {
            try
            {
                if (token.IsCancellationRequested || !File.Exists(stillPath))
                    return null;

                (int srcW, int srcH) = await GetVideoResolutionAsync(stillPath);
                if (srcW <= 0 || srcH <= 0)
                    return null;

                // Compare long edges: aspect-preserving targets constrain one dimension, but
                // the long edge always maps to the long edge.
                double srcLong = Math.Max(srcW, srcH);
                double dstLong = Math.Max(targetWidth, targetHeight);
                if (dstLong <= 0.0 || srcLong < dstLong * 1.3)
                    return null;

                Directory.CreateDirectory(outDir);
                string prescaled = Path.Combine(
                    outDir,
                    $"prescaled_{Path.GetFileNameWithoutExtension(stillPath)}_{srcW}x{srcH}.png");
                if (!File.Exists(prescaled))
                {
                    string args = $"-y -i \"{stillPath}\" -frames:v 1 -vf \"{scaleExpression}\" \"{prescaled}\"";
                    var (exit, _) = await RunFfmpegCaptureAsync(args, token);
                    if (exit != 0 || !File.Exists(prescaled) || new FileInfo(prescaled).Length <= 0)
                    {
                        TryDeleteFileSafe(prescaled);
                        return null;
                    }
                }

                onLog($"[PRESCALE] Still {srcW}x{srcH} -> ~{targetWidth}x{targetHeight} once ({Path.GetFileName(prescaled)}); the loop now decodes the small copy");
                return prescaled;
            }
            catch (Exception ex)
            {
                onLog($"[PRESCALE-WARN] {ex.Message}; looping the original still.");
                return null;
            }
        }

        public static string NormalizeCropAspectRatioName(string? input)
        {
            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(v) || v is "free" or "none" or "off" or "original")
                return "Free";

            if (v.Contains("720x1280") || v.Contains("9:16 hd"))
                return "9:16";
            if (v.Contains("2:3") || v.Contains("2/3"))
                return "2:3";
            if (v.Contains("1080x720") || v.Contains("3:2") || v.Contains("3/2"))
                return "3:2";
            if (v.Contains("9:16") || v.Contains("9/16") || v.Contains("portrait") || v.Contains("reel") || v.Contains("story"))
                return "9:16";
            if (v.Contains("16:9") || v.Contains("16/9") || v.Contains("landscape") || v.Contains("youtube"))
                return "16:9";
            if (v.Contains("1:1") || v.Contains("1/1") || v.Contains("square"))
                return "1:1";
            if (v.Contains("4:5") || v.Contains("4/5"))
                return "4:5";
            if (v.Contains("5:4") || v.Contains("5/4"))
                return "5:4";
            if (v.Contains("3:4") || v.Contains("3/4"))
                return "3:4";
            if (v.Contains("4:3") || v.Contains("4/3"))
                return "4:3";

            return v switch
            {
                _ => "Free"
            };
        }

        public static bool TryGetCropAspectRatioValue(string? input, out double ratio)
        {
            ratio = 0.0;
            string normalized = NormalizeCropAspectRatioName(input);
            if (normalized.Equals("Free", StringComparison.OrdinalIgnoreCase))
                return false;

            string[] parts = normalized.Split(':');
            if (parts.Length != 2)
                return false;

            if (!double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double w) ||
                !double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double h) ||
                w <= 0.0 || h <= 0.0)
            {
                return false;
            }

            ratio = w / h;
            return ratio > 0.0;
        }

        public static double NormalizeCropZoomPercent(double cropZoomPercent)
        {
            return Math.Clamp(cropZoomPercent, 0.0, 90.0);
        }

        private static double GetEstimatedOutputDurationSeconds(RenderJob job, double playbackSpeed)
        {
            double sourceDuration = job.DurationSec;

            if (TryParseClockValue(job.VideoTrimStart, out TimeSpan trimStart) &&
                trimStart > TimeSpan.Zero &&
                sourceDuration > trimStart.TotalSeconds)
            {
                sourceDuration -= trimStart.TotalSeconds;
            }

            if (TryParseClockValue(job.VideoTrimDuration, out TimeSpan trimDuration) &&
                trimDuration > TimeSpan.Zero)
            {
                if (sourceDuration <= 0.0)
                {
                    sourceDuration = trimDuration.TotalSeconds;
                }
                else
                {
                    sourceDuration = Math.Min(sourceDuration, trimDuration.TotalSeconds);
                }
            }

            if (sourceDuration <= 0.0)
                return 0.0;

            double normalizedSpeed = NormalizePlaybackSpeed(playbackSpeed);
            return sourceDuration / normalizedSpeed;
        }

        private static double ParseFrameRateValue(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
                return 0.0;

            var invCulture = System.Globalization.CultureInfo.InvariantCulture;

            if (rawValue.Contains('/'))
            {
                string[] parts = rawValue.Split('/');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], System.Globalization.NumberStyles.Any, invCulture, out double numerator) &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Any, invCulture, out double denominator) &&
                    denominator > 0.0)
                {
                    return numerator / denominator;
                }
            }

            if (double.TryParse(rawValue, System.Globalization.NumberStyles.Any, invCulture, out double directValue))
            {
                return directValue;
            }

            return 0.0;
        }

        private static bool HasCropEffect(RenderJob job)
        {
            return job.CropZoomPercent > 0.001 ||
                job.CropEnabled ||
                !string.IsNullOrWhiteSpace(job.CropAspectRatio) &&
                !job.CropAspectRatio.Equals("Free", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(job.FxEffect) && job.FxEffect.Contains("Crop", StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<string> GetConcatStreamCopySignatureAsync(string filePath, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(_ffprobePath) || !File.Exists(_ffprobePath))
                return string.Empty;

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return string.Empty;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ffprobePath,
                    Arguments = $"-v error -print_format json -show_entries stream=codec_type,codec_name,profile,level,width,height,pix_fmt,field_order,r_frame_rate,avg_frame_rate,time_base,sample_aspect_ratio,sample_rate,channels,channel_layout \"{filePath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return string.Empty;

                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();

                try
                {
                    await process.WaitForExitAsync(token);
                }
                catch (TaskCanceledException)
                {
                    try { process.Kill(); } catch { }
                    throw;
                }

                string stdout = await stdoutTask;
                _ = await stderrTask;

                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                    return string.Empty;

                using JsonDocument doc = JsonDocument.Parse(stdout);
                if (!doc.RootElement.TryGetProperty("streams", out JsonElement streams) || streams.ValueKind != JsonValueKind.Array)
                    return string.Empty;

                JsonElement? video = null;
                JsonElement? audio = null;
                foreach (JsonElement stream in streams.EnumerateArray())
                {
                    if (!stream.TryGetProperty("codec_type", out JsonElement codecTypeEl))
                        continue;

                    string codecType = codecTypeEl.GetString() ?? string.Empty;
                    if (codecType.Equals("video", StringComparison.OrdinalIgnoreCase) && video == null)
                        video = stream;
                    else if (codecType.Equals("audio", StringComparison.OrdinalIgnoreCase) && audio == null)
                        audio = stream;
                }

                if (video == null)
                    return string.Empty;

                string VideoField(string key)
                {
                    if (video.Value.TryGetProperty(key, out JsonElement el))
                        return el.ToString() ?? string.Empty;
                    return string.Empty;
                }

                string videoSig = string.Join("|", new[]
                {
                    VideoField("codec_name"),
                    VideoField("profile"),
                    VideoField("level"),
                    VideoField("width"),
                    VideoField("height"),
                    VideoField("pix_fmt"),
                    VideoField("field_order"),
                    VideoField("r_frame_rate"),
                    VideoField("avg_frame_rate"),
                    VideoField("time_base"),
                    VideoField("sample_aspect_ratio")
                });

                string audioSig;
                if (audio == null)
                {
                    audioSig = "NO_AUDIO";
                }
                else
                {
                    string AudioField(string key)
                    {
                        if (audio.Value.TryGetProperty(key, out JsonElement el))
                            return el.ToString() ?? string.Empty;
                        return string.Empty;
                    }

                    audioSig = string.Join("|", new[]
                    {
                        AudioField("codec_name"),
                        AudioField("sample_rate"),
                        AudioField("channels"),
                        AudioField("channel_layout"),
                        AudioField("time_base")
                    });
                }

                return $"V:{videoSig}||A:{audioSig}";
            }
            catch
            {
                return string.Empty;
            }
        }

        private static async Task<bool> CanUseConcatStreamCopyAsync(IReadOnlyList<string> inputPaths, CancellationToken token, Action<string> onLog)
        {
            if (inputPaths == null || inputPaths.Count < 2)
                return false;

            string? referenceSignature = null;
            for (int i = 0; i < inputPaths.Count; i++)
            {
                string signature = await GetConcatStreamCopySignatureAsync(inputPaths[i], token);
                if (string.IsNullOrWhiteSpace(signature))
                {
                    onLog("[MERGE-COMPAT] Could not read stream signature. Stream-copy concat disabled.");
                    return false;
                }

                if (referenceSignature == null)
                {
                    referenceSignature = signature;
                    continue;
                }

                if (!string.Equals(referenceSignature, signature, StringComparison.Ordinal))
                {
                    onLog("[MERGE-COMPAT] Stream parameters differ across inputs. Using safe re-encode concat.");
                    return false;
                }
            }

            onLog("[MERGE-COMPAT] Inputs look compatible for concat stream copy.");
            return true;
        }

        internal static bool IsImageSourcePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".bmp" or ".webp" or ".tif" or ".tiff" or ".gif";
        }

        private static string NormalizeTemplateNameForEngine(string? input)
        {
            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(v) || v == "none")
                return "None";
            if (v.Contains("smooth zoom in") || v.Contains("zoom in"))
                return "Smooth Zoom In";
            if (v.Contains("smooth zoom out") || v.Contains("zoom out"))
                return "Smooth Zoom Out";
            if (v.Contains("pan left"))
                return "Pan Left";
            if (v.Contains("pan right"))
                return "Pan Right";
            if (v.Contains("velocity punch") || v == "velocity" || v == "velocitypunch" || v.Contains("velocitypunch"))
                return "Velocity Punch";
            if (v.Contains("cinematic fade zoom"))
                return "Cinematic Fade Zoom";
            if (v.Contains("glitch distort"))
                return "Glitch Distort";
            if (v.Contains("3d spin"))
                return "3D Spin Lite";
            if (v.Contains("trend zoom flash"))
                return "Trend Zoom Flash";
            if (v.Contains("trend blur pulse"))
                return "Trend Blur Pulse";
            if (v.Contains("trend spin glitch"))
                return "Trend Spin Glitch";
            if (v.Contains("digicam") || v.Contains("retro camera") || v.Contains("camera memory"))
                return "Digicam Memory";
            if (v.Contains("polaroid") || v.Contains("scrapbook") || v.Contains("photo album"))
                return "Polaroid Scrapbook";
            if (v.Contains("photo dump") || v.Contains("photodump") || v.Contains("recap dump") || v.Contains("beat dump"))
                return "Beat Photo Dump";
            if (v.Contains("film strip") || v.Contains("retro film") || v == "reel" || v.Contains("reel "))
                return "Film Strip";
            if (v.Contains("magazine cover") || v == "magazine" || v.Contains("editorial"))
                return "Magazine Cover";
            return "None";
        }

        private static bool UsesLegacyDrawtextTextOverlay(string? animation)
        {
            if (string.IsNullOrWhiteSpace(animation)) return true;
            return animation.Contains("Drawtext", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeDrawtextCoordinateExpression(string expr)
        {
            if (string.IsNullOrWhiteSpace(expr))
                return string.Empty;

            string normalized = expr.Trim();
            normalized = Regex.Replace(normalized, @"\biw\b", "w", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\bih\b", "h", RegexOptions.IgnoreCase);
            return normalized;
        }

        private static async Task<string> RenderTemplateSegmentAsync(
            string inputPath,
            bool isImageInput,
            string outputPath,
            double segmentDurationSec,
            int targetWidth,
            int targetHeight,
            double targetFps,
            string templateName,
            string musicSyncMode,
            double fxIntensity,
            string? magazineCoverTitle,
            string? magazineCoverSubtitle,
            CancellationToken token,
            Action<string> onLog)
        {
            string baseFilter =
                $"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=decrease:flags=lanczos," +
                $"pad={targetWidth}:{targetHeight}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1";
            string motionFilter = BuildTemplateMotionFilter(templateName, targetWidth, targetHeight, segmentDurationSec, isImageInput, musicSyncMode, fxIntensity, magazineCoverTitle, magazineCoverSubtitle);
            string fullVideoFilter = string.IsNullOrWhiteSpace(motionFilter)
                ? baseFilter
                : $"{baseFilter},{motionFilter}";

            string dur = FfmpegDouble(Math.Max(0.10, segmentDurationSec), 3);
            string fps = FfmpegDouble(targetFps, 3);
            bool hasAudio = !isImageInput && await HasAudioStreamAsync(inputPath);

            // Image inputs are looped, and the image2 demuxer re-decodes the still on every
            // output frame. Pre-decode + pre-scale ONCE to the segment canvas (mirroring the
            // baseFilter below so the graph's own scale runs as an identity) and loop the
            // small copy instead. No-op for sources already near the target size.
            string effectiveImageInput = inputPath;
            if (isImageInput)
            {
                string prescaleExpression =
                    $"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=decrease:flags=lanczos," +
                    $"pad={targetWidth}:{targetHeight}:(ow-iw)/2:(oh-ih)/2:color=black";
                string? prescaled = await TryPrescaleStillForLoopAsync(
                    inputPath, prescaleExpression, targetWidth, targetHeight, Path.GetDirectoryName(outputPath) ?? GetTitanTempDir(), onLog, token);
                if (!string.IsNullOrWhiteSpace(prescaled))
                    effectiveImageInput = prescaled;
            }

            string args;
            if (isImageInput)
            {
                args =
                    $"-y {FfmpegThreadCapArguments()}-loop 1 -t {dur} -i \"{effectiveImageInput}\" " +
                    $"-f lavfi -t {dur} -i anullsrc=channel_layout=stereo:sample_rate=48000 " +
                    "-map 0:v:0 -map 1:a:0 " +
                    $"-vf \"{fullVideoFilter}\" -r {fps} " +
                    (EngineCore.HasNvencSupport() ? "-c:v h264_nvenc -preset p5 -cq 18 -pix_fmt yuv420p " : "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p ") +
                    "-c:a aac -b:a 192k -ar 48000 -ac 2 " +
                    "-shortest -movflags +faststart " +
                    $"\"{outputPath}\"";
            }
            else if (hasAudio)
            {
                args =
                    $"-y {FfmpegThreadCapArguments()}-i \"{inputPath}\" " +
                    $"-f lavfi -t {dur} -i anullsrc=channel_layout=stereo:sample_rate=48000 " +
                    "-filter_complex \"[0:a]aresample=48000,asetpts=PTS-STARTPTS[a0];[1:a]asetpts=PTS-STARTPTS[a1];[a0][a1]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[aout]\" " +
                    "-map 0:v:0 -map \"[aout]\" " +
                    $"-vf \"{fullVideoFilter}\" -r {fps} " +
                    (EngineCore.HasNvencSupport() ? "-c:v h264_nvenc -preset p5 -cq 18 -pix_fmt yuv420p " : "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p ") +
                    "-c:a aac -b:a 192k -ar 48000 -ac 2 " +
                    "-shortest -movflags +faststart " +
                    $"\"{outputPath}\"";
            }
            else
            {
                args =
                    $"-y {FfmpegThreadCapArguments()}-i \"{inputPath}\" " +
                    $"-f lavfi -t {dur} -i anullsrc=channel_layout=stereo:sample_rate=48000 " +
                    "-map 0:v:0 -map 1:a:0 " +
                    $"-vf \"{fullVideoFilter}\" -r {fps} " +
                    (EngineCore.HasNvencSupport() ? "-c:v h264_nvenc -preset p5 -cq 18 -pix_fmt yuv420p " : "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p ") +
                    "-c:a aac -b:a 192k -ar 48000 -ac 2 " +
                    "-shortest -movflags +faststart " +
                    $"\"{outputPath}\"";
            }

            try
            {
                var (exitCode, stderr) = await RunFfmpegCaptureAsync(args, token);
                if (exitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
                {
                    // ONE CPU retry before giving up. h264_nvenc segfaults on some machine/build
                    // combinations the moment the graph carries a dynamic-expression scale filter
                    // (the template zoom path: scale=...eval=frame). Measured on one box: NVENC +
                    // the zoom graph crashed 8/8 while the IDENTICAL graph on libx264 succeeded
                    // every time, and NVENC + a static scale also succeeded - an upstream
                    // ffmpeg/NVENC defect, not a graph error. Retrying on CPU is the difference
                    // between a slower successful render and a dead job. The stderr of a crash is
                    // only the banner, so the log names the exit code instead.
                    if (args.Contains("h264_nvenc", StringComparison.Ordinal))
                    {
                        onLog($"[TEMPLATE] GPU encode failed (exit {exitCode}); retrying segment on CPU (libx264)...");
                        TryDeleteFileSafe(outputPath);
                        string cpuArgs = args.Replace(
                            "h264_nvenc -preset p5 -cq 18",
                            "libx264 -preset veryfast -crf 18",
                            StringComparison.Ordinal);
                        var (cpuExit, cpuStderr) = await RunFfmpegCaptureAsync(cpuArgs, token);
                        if (cpuExit == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                        {
                            onLog("[TEMPLATE] Segment ready on CPU fallback: " + Path.GetFileName(outputPath));
                            return outputPath;
                        }

                        throw new Exception($"Template segment render failed (GPU exit {exitCode}, CPU retry exit {cpuExit}).");
                    }

                    string summary = string.Join(" | ",
                        (stderr ?? string.Empty)
                            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Take(8));
                    throw new Exception($"Template segment render failed: {summary}");
                }

                onLog($"[TEMPLATE] Segment ready: {Path.GetFileName(outputPath)}");
                return outputPath;
            }
            finally
            {
                // The prescaled still is a per-segment scratch copy: it only exists to be looped
                // by the args above, so it goes as soon as the encode is done - success, retry
                // or failure alike.
                if (!string.Equals(effectiveImageInput, inputPath, StringComparison.OrdinalIgnoreCase))
                    TryDeleteFileSafe(effectiveImageInput);
            }
        }

        private static async Task<(string ConcatListPath, string MergedSourcePath)> MergeVideosToTempSourceAsync(
            IReadOnlyList<string> sourcePaths,
            string outDir,
            string mergeTag,
            CancellationToken token,
            Action<string> onLog,
            TitanTransitionSettings? transitionSettings = null,
            int targetWidthParam = 1080, int targetHeightParam = 1920)
        {
            if (sourcePaths == null || sourcePaths.Count < 2)
                throw new InvalidOperationException("Merge requires at least 2 input videos.");

            var normalizedPaths = new List<string>();
            foreach (string input in sourcePaths)
            {
                if (string.IsNullOrWhiteSpace(input))
                    continue;

                string fullPath = Path.GetFullPath(input.Trim());
                if (!File.Exists(fullPath))
                    throw new FileNotFoundException($"Merge source does not exist: {fullPath}");

                normalizedPaths.Add(fullPath);
            }

            if (normalizedPaths.Count < 2)
                throw new InvalidOperationException("Merge requires at least 2 valid input videos.");

            string safeTag = string.IsNullOrWhiteSpace(mergeTag) ? Guid.NewGuid().ToString("N").Substring(0, 8) : mergeTag;
            string tempWorkDir = GetTitanTempDir();
            string concatListPath = Path.Combine(tempWorkDir, $"merge_{safeTag}_list.txt");
            string mergedSourcePath = Path.Combine(tempWorkDir, $"merge_{safeTag}_source.mp4");

            if (transitionSettings != null && transitionSettings.Enabled && normalizedPaths.Count >= 2)
            {
                try
                {
                    onLog($"[TRANSITION-ENGINE] Processing {normalizedPaths.Count} video clips with '{transitionSettings.TransitionType}' transition ({transitionSettings.DurationSeconds:F2}s)...");

                    var clipMetas = new List<TitanClipMetadata>();
                    foreach (string path in normalizedPaths)
                    {
                        var meta = await AnalyzeMediaDetailsAsync(path);
                        clipMetas.Add(new TitanClipMetadata
                        {
                            FilePath = path,
                            DurationSeconds = meta.Duration,
                            Width = meta.Width,
                            Height = meta.Height,
                            HasAudio = await HasAudioStreamAsync(path)
                        });
                    }

                    int renderW = targetWidthParam > 0 ? targetWidthParam : 1080;
                    int renderH = targetHeightParam > 0 ? targetHeightParam : 1920;

                    var (graph, vOutTag, aOutTag, totalDur) = TitanTransitionGraphBuilder.BuildFilterGraph(
                        clipMetas, transitionSettings, renderW, renderH);

                    var inputSb = new StringBuilder();
                    for (int i = 0; i < normalizedPaths.Count; i++)
                    {
                        inputSb.Append($"-i \"{normalizedPaths[i]}\" ");
                    }

                    string audioMap = string.IsNullOrEmpty(aOutTag) ? "-an" : $"-map \"{aOutTag}\" -c:a aac -b:a 128k";
                    string transArgs = $"-y {inputSb.ToString()}-filter_complex \"{graph}\" -map \"{vOutTag}\" " + (EngineCore.HasNvencSupport() ? "-c:v h264_nvenc -preset p5 -cq 20 " : "-c:v libx264 -preset veryfast -crf 20 ") + $"{audioMap} -movflags +faststart \"{mergedSourcePath}\"";

                    onLog($"[TRANSITION-ENGINE] Rendering graph...");
                    var (transExit, transErr) = await RunFfmpegCaptureAsync(transArgs, token);

                    if (transExit == 0 && File.Exists(mergedSourcePath) && new FileInfo(mergedSourcePath).Length > 0)
                    {
                        onLog($"[TRANSITION-ENGINE] Cross-clip transitions successfully applied! Total merged duration: {totalDur:F2}s.");
                        return (concatListPath, mergedSourcePath);
                    }
                    else
                    {
                        string errLogStr = string.IsNullOrWhiteSpace(transErr)
                            ? "Unknown error"
                            : string.Join(" | ", transErr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).TakeLast(5));
                        onLog($"[TRANSITION-ENGINE-WARN] Custom transition render failed: {errLogStr}. Falling back to standard concat.");
                    }
                }
                catch (Exception ex)
                {
                    onLog($"[TRANSITION-ENGINE-ERROR] Transition processing error: {ex.Message}. Falling back to standard concat.");
                }
            }

            string[] concatLines = normalizedPaths
                .Select(p => $"file '{BuildConcatListPathToken(p)}'")
                .ToArray();
            File.WriteAllLines(concatListPath, concatLines, new UTF8Encoding(false));

            bool mergeCompleted = false;
            bool tryStreamCopy = await CanUseConcatStreamCopyAsync(normalizedPaths, token, onLog);
            double expectedDuration = 0.0;
            foreach (string path in normalizedPaths)
            {
                var meta = await AnalyzeMediaDetailsAsync(path);
                expectedDuration += Math.Max(0.0, meta.Duration);
            }

            if (tryStreamCopy)
            {
                onLog("[MERGE] Attempt 1/2: concat stream copy");
                string copyArgs = $"-y -f concat -safe 0 -i \"{concatListPath}\" -fflags +genpts -avoid_negative_ts make_zero -c copy -movflags +faststart \"{mergedSourcePath}\"";
                var (copyExit, copyErr) = await RunFfmpegCaptureAsync(copyArgs, token);

                bool copyOk = copyExit == 0 && File.Exists(mergedSourcePath) && new FileInfo(mergedSourcePath).Length > 0;
                if (copyOk)
                {
                    var mergedMeta = await AnalyzeMediaDetailsAsync(mergedSourcePath);
                    bool durationLooksValid = true;
                    if (expectedDuration > 1.0 && mergedMeta.Duration > 0.0)
                    {
                        double ratio = Math.Abs(mergedMeta.Duration - expectedDuration) / expectedDuration;
                        durationLooksValid = ratio <= 0.10;
                        if (!durationLooksValid)
                        {
                            onLog($"[MERGE-WARN] Copy concat duration mismatch. Expected {expectedDuration:F2}s, got {mergedMeta.Duration:F2}s.");
                        }
                    }

                    bool bitstreamOk = await ValidateOutputBitstreamAsync(mergedSourcePath, token, onLog);
                    if (durationLooksValid && bitstreamOk)
                    {
                        mergeCompleted = true;
                        onLog("[MERGE] Stream-copy concat passed validation.");
                    }
                    else
                    {
                        onLog("[MERGE-WARN] Stream-copy concat failed validation. Falling back to safe re-encode concat.");
                    }
                }
                else
                {
                    string copySummary = string.Join(" | ",
                        (copyErr ?? string.Empty)
                            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Take(5));
                    if (!string.IsNullOrWhiteSpace(copySummary))
                        onLog($"[MERGE-WARN] Copy concat failed: {copySummary}");
                }
            }

            if (!mergeCompleted)
            {
                TryDeleteFileSafe(mergedSourcePath);

                onLog("[MERGE] Attempt 2/2: safe normalize + concat");
                var firstMeta = await AnalyzeMediaDetailsAsync(normalizedPaths[0]);
                int targetW = firstMeta.Width > 0 ? firstMeta.Width : 1920;
                int targetH = firstMeta.Height > 0 ? firstMeta.Height : 1080;
                if ((targetW & 1) != 0) targetW += 1;
                if ((targetH & 1) != 0) targetH += 1;
                double targetFps = firstMeta.Fps > 0.1 ? firstMeta.Fps : 30.0;
                if (double.IsNaN(targetFps) || double.IsInfinity(targetFps)) targetFps = 30.0;
                targetFps = Math.Clamp(targetFps, 1.0, 120.0);

                string normalizedConcatListPath = Path.Combine(tempWorkDir, $"merge_{safeTag}_norm_list.txt");
                var normalizedSegments = new List<string>();
                try
                {
                    for (int i = 0; i < normalizedPaths.Count; i++)
                    {
                        string normalizedSegmentPath = await NormalizeVideoForSafeConcatAsync(
                            normalizedPaths[i],
                            tempWorkDir,
                            safeTag,
                            i,
                            targetW,
                            targetH,
                            targetFps,
                            token,
                            onLog);
                        normalizedSegments.Add(normalizedSegmentPath);
                    }

                    string[] normalizedConcatLines = normalizedSegments
                        .Select(p => $"file '{BuildConcatListPathToken(p)}'")
                        .ToArray();
                    File.WriteAllLines(normalizedConcatListPath, normalizedConcatLines, new UTF8Encoding(false));

                    TryDeleteFileSafe(concatListPath);
                    concatListPath = normalizedConcatListPath;

                    string safeConcatArgs =
                        $"-y -f concat -safe 0 -i \"{concatListPath}\" " +
                        "-fflags +genpts -avoid_negative_ts make_zero " +
                        "-c copy -movflags +faststart " +
                        $"\"{mergedSourcePath}\"";
                    var (safeConcatExit, safeConcatErr) = await RunFfmpegCaptureAsync(safeConcatArgs, token);

                    if (safeConcatExit != 0 || !File.Exists(mergedSourcePath) || new FileInfo(mergedSourcePath).Length <= 0)
                    {
                        string safeConcatSummary = string.Join(" | ",
                            (safeConcatErr ?? string.Empty)
                                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                .Take(8));
                        throw new Exception($"Merge failed after normalize: {safeConcatSummary}");
                    }
                }
                finally
                {
                    foreach (string normalizedSegment in normalizedSegments)
                        TryDeleteFileSafe(normalizedSegment);
                }

                bool validated = await ValidateOutputBitstreamAsync(mergedSourcePath, token, onLog);
                if (!validated)
                    throw new Exception("Merge failed validation after safe normalize concat.");

                var mergedMeta = await AnalyzeMediaDetailsAsync(mergedSourcePath);
                if (expectedDuration > 1.0 && mergedMeta.Duration > 0.0)
                {
                    double durationDeltaRatio = Math.Abs(mergedMeta.Duration - expectedDuration) / expectedDuration;
                    if (durationDeltaRatio > 0.20)
                    {
                        throw new Exception($"Merge duration mismatch after safe normalize concat (expected {expectedDuration:F2}s, got {mergedMeta.Duration:F2}s).");
                    }
                }
            }

            return (concatListPath, mergedSourcePath);
        }

        private static async Task<string> NormalizeVideoForSafeConcatAsync(
            string inputPath,
            string outDir,
            string safeTag,
            int index,
            int targetWidth,
            int targetHeight,
            double targetFps,
            CancellationToken token,
            Action<string> onLog)
        {
            string tempWorkDir = GetTitanTempDir();
            string normalizedPath = Path.Combine(tempWorkDir, $"merge_{safeTag}_norm_{index:D2}.mp4");
            bool hasAudio = await HasAudioStreamAsync(inputPath);
            string fpsExpr = FfmpegDouble(targetFps, 4);
            string videoFilter =
                $"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=decrease," +
                $"pad={targetWidth}:{targetHeight}:(ow-iw)/2:(oh-ih)/2," +
                $"setsar=1,fps={fpsExpr},format=yuv420p";

            string args;
            if (hasAudio)
            {
                args =
                    $"-y {FfmpegThreadCapArguments()}-i \"{inputPath}\" " +
                    "-map 0:v:0 -map 0:a:0 " +
                    $"-vf \"{videoFilter}\" " +
                    (EngineCore.HasNvencSupport() ? "-c:v h264_nvenc -preset p5 -cq 18 -pix_fmt yuv420p " : "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p ") +
                    "-c:a aac -b:a 192k -ar 48000 -ac 2 " +
                    "-movflags +faststart " +
                    $"\"{normalizedPath}\"";
            }
            else
            {
                args =
                    $"-y {FfmpegThreadCapArguments()}-i \"{inputPath}\" " +
                    "-f lavfi -i anullsrc=channel_layout=stereo:sample_rate=48000 " +
                    "-map 0:v:0 -map 1:a:0 " +
                    $"-vf \"{videoFilter}\" " +
                    (EngineCore.HasNvencSupport() ? "-c:v h264_nvenc -preset p5 -cq 18 -pix_fmt yuv420p " : "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p ") +
                    "-c:a aac -b:a 192k -ar 48000 -ac 2 " +
                    "-shortest -movflags +faststart " +
                    $"\"{normalizedPath}\"";
            }

            onLog($"[MERGE-NORMALIZE] Segment {index + 1}: {(hasAudio ? "with audio" : "silent audio injected")} -> {targetWidth}x{targetHeight} @ {targetFps:F3}fps");
            var (exitCode, stderr) = await RunFfmpegCaptureAsync(args, token);
            if (exitCode != 0 || !File.Exists(normalizedPath) || new FileInfo(normalizedPath).Length <= 0)
            {
                string summary = string.Join(" | ",
                    (stderr ?? string.Empty)
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Take(8));
                throw new Exception($"Normalize segment #{index + 1} failed: {summary}");
            }

            return normalizedPath;
        }

        /// <summary>
        /// Evicts every sibling directory of a live soundbar cache entry, so the cache tree holds at
        /// most ONE sequence per style.
        ///
        /// SoundBarVisualizerPreset lays the cache out as
        ///   %TEMP%\TitanEngine\soundbar_cache\&lt;styleId&gt;\&lt;cacheKey&gt;\
        ///       soundbar_%04d.png, preview\preview_NN.png, manifest.json
        /// so the siblings of a live entry are exactly "some other key for this style", where a key is
        /// 24 hex characters of SHA-256 over every pixel-affecting input. Removing them keeps the disk
        /// cost bounded at one entry per style (12 styles) however many parameter combinations a user
        /// cycles through - which is the whole reason the sequence may stay on disk at all.
        ///
        /// Runs on the cache-hit and the cache-miss path alike. On a hit there is normally nothing to
        /// remove; on a miss every sibling is by definition a stale key for the same style.
        ///
        /// Concurrency: the API render path is serialised by _localApiRenderLock and the UI drives
        /// one queued job at a time, so a sibling cannot be in use here. Even if it were, the soundbar
        /// layer degrades to "skipped" instead of failing the job - the same failure mode the old
        /// unconditional delete had.
        /// </summary>
        /// <returns>How many sibling directories were actually removed.</returns>
        private static int EvictSoundBarCacheSiblings(string? tempRoot)
        {
            string live = ResolveFullPathSafe(tempRoot);
            if (live.Length == 0)
                return 0;

            string? styleParent;
            string keepDirectoryName;
            try
            {
                styleParent = Path.GetDirectoryName(live);
                keepDirectoryName = Path.GetFileName(live);
            }
            catch
            {
                return 0;
            }

            if (string.IsNullOrWhiteSpace(styleParent) ||
                string.IsNullOrWhiteSpace(keepDirectoryName) ||
                !Directory.Exists(styleParent))
            {
                return 0;
            }

            int removed = 0;
            try
            {
                foreach (string sibling in Directory.GetDirectories(styleParent))
                {
                    if (string.Equals(Path.GetFileName(sibling), keepDirectoryName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    TryDeleteFileSafe(sibling);
                    if (!Directory.Exists(sibling))
                        removed++;
                }
            }
            catch
            {
                // Eviction is best-effort. A cache slightly larger than its cap beats a render that
                // fails because a directory could not be listed.
            }

            return removed;
        }

        /// <summary>
        /// Caps the picker preview directory at <see cref="SoundBarPreviewCacheLimit"/> PNGs, newest
        /// first. The filename is a hash of every tunable, so dragging Opacity across 90 slider
        /// positions writes 90 distinct files and none of them is ever revisited - the directory grows
        /// without bound unless something prunes it. Called after every preview request, so it stays
        /// bounded whether the request was a cache hit or a fresh render.
        /// </summary>
        /// <param name="directory">Preview cache directory to prune.</param>
        /// <param name="keepPath">The file about to be loaded; it is never removed.</param>
        /// <returns>How many stale preview PNGs were removed.</returns>
        internal static int PruneSoundBarPreviewCache(string? directory, string? keepPath)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return 0;

            string keep = ResolveFullPathSafe(keepPath);
            int removed = 0;

            try
            {
                var candidates = new List<FileInfo>();
                foreach (string file in Directory.GetFiles(directory, "*.png"))
                {
                    if (keep.Length > 0 && ResolveFullPathSafe(file).Equals(keep, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        candidates.Add(new FileInfo(file));
                    }
                    catch { }
                }

                if (candidates.Count <= SoundBarPreviewCacheLimit)
                    return 0;

                candidates.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

                for (int i = SoundBarPreviewCacheLimit; i < candidates.Count; i++)
                {
                    try
                    {
                        candidates[i].Delete();
                        removed++;
                    }
                    catch { }
                }
            }
            catch
            {
                // Best effort: a preview that cannot prune must still render.
            }

            return removed;
        }
    }
}
