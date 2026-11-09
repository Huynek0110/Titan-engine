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


        private static string BuildPlaybackSpeedAudioFilter(double playbackSpeed)
        {
            var invCulture = System.Globalization.CultureInfo.InvariantCulture;
            double remainingTempo = NormalizePlaybackSpeed(playbackSpeed);

            if (Math.Abs(remainingTempo - 1.0) <= 0.0001)
                return string.Empty;

            var segments = new List<string>();

            while (remainingTempo < 0.5)
            {
                segments.Add("atempo=0.5000");
                remainingTempo /= 0.5;
            }

            while (remainingTempo > 2.0)
            {
                segments.Add("atempo=2.0000");
                remainingTempo /= 2.0;
            }

            segments.Add($"atempo={remainingTempo.ToString("F4", invCulture)}");
            return string.Join(",", segments);
        }

        private static string BuildSourceAudioInputFilterChain(
            RenderJob job,
            double playbackSpeed,
            double sourceAudioSpeed,
            bool syncSlowMotionAudio,
            Action<string> onLog)
        {
            var filters = new List<string>();

            double sourceVolume = Math.Clamp(job.VideoVolume, 0.0, 2.0);
            if (Math.Abs(sourceVolume - 1.0) > 0.0001)
            {
                string volumeStr = FfmpegDouble(sourceVolume, 4);
                filters.Add($"volume={volumeStr}");
                onLog($"[FILTER-AUDIO-VOLUME] Source audio volume={volumeStr}");
            }

            double effectiveSpeed = Math.Clamp(sourceAudioSpeed, 0.10, 8.0);
            if (Math.Abs(playbackSpeed - 1.0) > 0.0001 && syncSlowMotionAudio)
                effectiveSpeed = Math.Clamp(effectiveSpeed * playbackSpeed, 0.10, 8.0);

            if (Math.Abs(effectiveSpeed - 1.0) > 0.0001)
            {
                string audioPlaybackFilter = BuildPlaybackSpeedAudioFilter(effectiveSpeed);
                if (!string.IsNullOrWhiteSpace(audioPlaybackFilter))
                {
                    filters.Add(audioPlaybackFilter);
                    onLog($"[FILTER-SOURCE-AUDIO-SPEED] Source audio speed: {effectiveSpeed:F4}x ({audioPlaybackFilter})");
                }
            }

            if (filters.Count == 0)
                filters.Add("anull");

            return string.Join(",", filters);
        }

        private static string BuildExternalAudioInputFilterChain(
            RenderJob job,
            Action<string> onLog,
            bool skipVolume = false,
            double? volumeOverride = null)
        {
            var filters = new List<string>();

            double audioVolume = Math.Clamp(volumeOverride ?? job.AudioVolume, 0.0, 2.0);
            if (skipVolume)
            {
                onLog("[FILTER-AUDIO-VOLUME] External audio volume already applied during audio overlay mix.");
            }
            else if (Math.Abs(audioVolume - 1.0) > 0.0001)
            {
                string volumeStr = FfmpegDouble(audioVolume, 4);
                filters.Add($"volume={volumeStr}");
                onLog($"[FILTER-AUDIO-VOLUME] External audio volume={volumeStr}");
            }

            double externalAudioSpeed = Math.Clamp(job.ExternalAudioSpeed, 0.10, 8.0);
            if (Math.Abs(externalAudioSpeed - 1.0) > 0.0001)
            {
                string audioPlaybackFilter = BuildPlaybackSpeedAudioFilter(externalAudioSpeed);
                if (!string.IsNullOrWhiteSpace(audioPlaybackFilter))
                {
                    filters.Add(audioPlaybackFilter);
                    onLog($"[FILTER-EXTERNAL-AUDIO-SPEED] External audio speed: {externalAudioSpeed:F4}x ({audioPlaybackFilter})");
                }
            }

            // Audio 1 voice FX (echo/reverb/robot/...) applied to the external Audio 1 track
            string audio1Fx = BuildAudio1EffectChain(job.Audio1Effect, job.Audio1EffectIntensity);
            if (!string.IsNullOrWhiteSpace(audio1Fx))
            {
                filters.Add(audio1Fx);
                onLog($"[AUDIO1-FX] {job.Audio1Effect} @ {job.Audio1EffectIntensity}%: {audio1Fx}");
            }

            if (filters.Count == 0)
                filters.Add("anull");

            return string.Join(",", filters);
        }

        private static string BuildAudioPostProcessFilterChain(RenderJob job, (double FadeIn, double FadeOut, double FadeOutStart) fade, Action<string> onLog)
        {
            var filters = new List<string>();

            if (fade.FadeIn > 0.001)
                filters.Add($"afade=t=in:st=0:d={FfmpegDouble(fade.FadeIn)}");

            if (fade.FadeOut > 0.001)
                filters.Add($"afade=t=out:st={FfmpegDouble(fade.FadeOutStart)}:d={FfmpegDouble(fade.FadeOut)}");

            if (job.EnableLoudnorm)
            {
                filters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
                onLog("[FILTER-AUDIO-POST] Audio loudness normalization enabled (loudnorm)");
            }

            string result = string.Join(",", filters);
            if (!string.IsNullOrWhiteSpace(result))
                onLog($"[FILTER-AUDIO-POST] {result}");

            return result;
        }

        private static async Task<bool> HasAudioStreamAsync(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(_ffprobePath) || !File.Exists(_ffprobePath))
                return false;

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ffprobePath,
                    Arguments = $"-v error -select_streams a:0 -show_entries stream=codec_type -of csv=p=0 \"{filePath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return false;

                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                return process.ExitCode == 0 && output.Contains("audio", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static async Task<double> GetEstimatedAudioOutputDurationSecondsAsync(
            RenderJob job,
            double playbackSpeed,
            string? externalAudioPath,
            bool hasSourceAudio,
            bool hasExternalAudio,
            Action<string> onLog)
        {
            double audioDuration = double.PositiveInfinity;

            if (hasSourceAudio)
            {
                double sourceBaseDuration = GetEstimatedOutputDurationSeconds(job, 1.0);
                if (sourceBaseDuration > 0.01)
                {
                    double sourceAudioSpeed = Math.Clamp(job.SourceAudioSpeed, 0.10, 8.0);
                    if (Math.Abs(playbackSpeed - 1.0) > 0.0001 && job.SlowMotionAudio)
                        sourceAudioSpeed = Math.Clamp(sourceAudioSpeed * playbackSpeed, 0.10, 8.0);

                    double sourceEffectiveDuration = sourceBaseDuration / sourceAudioSpeed;
                    if (sourceEffectiveDuration > 0.01)
                        audioDuration = Math.Min(audioDuration, sourceEffectiveDuration);
                }
            }

            if (hasExternalAudio && !string.IsNullOrWhiteSpace(externalAudioPath) && File.Exists(externalAudioPath))
            {
                var externalMeta = await AnalyzeMediaAsync(externalAudioPath);
                double externalBaseDuration = Math.Max(0.0, externalMeta.Duration);
                if (externalBaseDuration > 0.01)
                {
                    double externalSpeed = Math.Clamp(job.ExternalAudioSpeed, 0.10, 8.0);
                    double externalEffectiveDuration = externalBaseDuration / externalSpeed;
                    if (externalEffectiveDuration > 0.01)
                        audioDuration = Math.Min(audioDuration, externalEffectiveDuration);
                }
            }

            if (!double.IsPositiveInfinity(audioDuration) && audioDuration > 0.01)
            {
                onLog($"[DURATION-SYNC] Audio duration estimate: {audioDuration:F2}s");
                return audioDuration;
            }

            return 0.0;
        }

        private static async Task<double> GetEstimatedExternalAudioOutputDurationSecondsAsync(
            RenderJob job,
            string? externalAudioPath,
            Action<string>? onLog = null)
        {
            if (string.IsNullOrWhiteSpace(externalAudioPath) || !File.Exists(externalAudioPath))
                return 0.0;

            var externalMeta = await AnalyzeMediaAsync(externalAudioPath);
            double externalBaseDuration = Math.Max(0.0, externalMeta.Duration);
            if (externalBaseDuration <= 0.01)
                return 0.0;

            double externalSpeed = Math.Clamp(job.ExternalAudioSpeed, 0.10, 8.0);
            double externalEffectiveDuration = externalBaseDuration / externalSpeed;
            if (externalEffectiveDuration > 0.01 && onLog != null)
                onLog($"[DURATION-SYNC] External audio effective duration: {externalEffectiveDuration:F2}s");

            return externalEffectiveDuration;
        }

        // ═══════════════════════════════════════════════════════════════════
        // CAPTION SYNC: Whisper AI Transcription + SRT Burn
        // ═══════════════════════════════════════════════════════════════════
        private static async Task<string> GenerateCaptionSyncAssAsync(
            RenderJob job, string audioPath,
            CancellationToken token, Action<double> onProgress, Action<string> onLog)
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            // Giai đoạn 3: whisper-cli theo OS (Windows .exe như bản gốc; Linux binary thường).
            string whisperExe = Path.Combine(appDir, "Assets", "Whisper", TitanPlatform.BinaryName("whisper-cli"));
            
            string modelSize = job.CaptionSyncModelSize ?? "Small";
            string modelFileName = "ggml-small.bin";
            string downloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin";

            if (modelSize.Contains("Medium", StringComparison.OrdinalIgnoreCase))
            {
                modelFileName = "ggml-medium.bin";
                downloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin";
            }
            else if (modelSize.Contains("Large", StringComparison.OrdinalIgnoreCase))
            {
                modelFileName = "ggml-large-v3-turbo-q5_0.bin";
                downloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin";
            }

            string whisperModel = Path.Combine(appDir, "Assets", "Whisper", modelFileName);

            // Validate Whisper installation
            if (!File.Exists(whisperExe))
            {
                onLog($"[CAPTION-SYNC-ERROR] whisper-cli.exe not found at: {whisperExe}");
                return "";
            }

            if (!File.Exists(whisperModel))
            {
                onLog($"[CAPTION-SYNC] Model {modelFileName} not found. Downloading (this may take a while)...");
                try
                {
                    using (var client = new System.Net.Http.HttpClient())
                    {
                        client.Timeout = TimeSpan.FromHours(1);
                        using (var response = await client.GetAsync(downloadUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, token))
                        {
                            response.EnsureSuccessStatusCode();
                            var totalBytes = response.Content.Headers.ContentLength;
                            using (var contentStream = await response.Content.ReadAsStreamAsync(token))
                            using (var fileStream = new FileStream(whisperModel + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                            {
                                var buffer = new byte[8192];
                                long totalRead = 0;
                                int bytesRead;
                                int lastReportedPercent = -1;
                                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                                {
                                    await fileStream.WriteAsync(buffer, 0, bytesRead, token);
                                    totalRead += bytesRead;
                                    if (totalBytes.HasValue)
                                    {
                                        int percent = (int)(totalRead * 100 / totalBytes.Value);
                                        if (percent != lastReportedPercent && percent % 5 == 0)
                                        {
                                            onLog($"[CAPTION-SYNC] Downloading {modelFileName}: {percent}% ({(totalRead / 1024 / 1024)}MB / {(totalBytes.Value / 1024 / 1024)}MB)");
                                            lastReportedPercent = percent;
                                        }
                                    }
                                }
                            }
                            File.Move(whisperModel + ".tmp", whisperModel, true);
                            onLog($"[CAPTION-SYNC] Model downloaded successfully!");
                        }
                    }
                }
                catch (Exception ex)
                {
                    onLog($"[CAPTION-SYNC-ERROR] Failed to download model: {ex.Message}");
                    TryDeleteFileSafe(whisperModel + ".tmp");
                    return "";
                }
            }

            string tempDir = Path.GetDirectoryName(audioPath) ?? Path.GetTempPath();
            string baseName = Path.GetFileNameWithoutExtension(audioPath);
            string tempWavPath = Path.Combine(tempDir, $"{baseName}_whisper_temp.wav");
            string tempSrtBase = Path.Combine(tempDir, $"{baseName}_whisper_caption");
            string tempSrtPath = tempSrtBase + ".srt";

            try
            {
                // ───────────────────────────────────────────────
                // STEP 1: Extract audio from video → WAV 16kHz mono
                // ───────────────────────────────────────────────
                onLog("[CAPTION-SYNC] Step 1/3: Extracting audio from video...");
                string ffmpegPath = Path.Combine(appDir, "ffmpeg.exe");
                if (!File.Exists(ffmpegPath))
                    ffmpegPath = "ffmpeg"; // fallback to PATH

                // EQ filter isolates the vocal frequencies so Whisper can hear singing over loud music
                var extractArgs = $"-i \"{audioPath}\" -af \"highpass=f=200, lowpass=f=3000, equalizer=f=1500:t=q:w=1:g=10\" -ar 16000 -ac 1 -c:a pcm_s16le -y \"{tempWavPath}\"";
                onLog($"[CAPTION-SYNC] ffmpeg {extractArgs}");

                var extractPsi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = extractArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                using (var extractProcess = new Process { StartInfo = extractPsi })
                {
                    extractProcess.Start();
                    string extractStderr = await extractProcess.StandardError.ReadToEndAsync();
                    await extractProcess.WaitForExitAsync(token);

                    if (extractProcess.ExitCode != 0 || !File.Exists(tempWavPath))
                    {
                        onLog($"[CAPTION-SYNC-ERROR] Failed to extract audio: {extractStderr}");
                        return "";
                    }
                }

                var wavInfo = new FileInfo(tempWavPath);
                onLog($"[CAPTION-SYNC] Audio extracted: {wavInfo.Length / 1024} KB");

                // ───────────────────────────────────────────────
                // STEP 2: Run Whisper CLI → Generate SRT
                // ───────────────────────────────────────────────
                onLog("[CAPTION-SYNC] Step 2/3: Running Whisper AI transcription...");
                
                // Determine language code
                string langCode = "auto";
                string langSetting = job.CaptionSyncLanguage?.Trim() ?? "auto";
                if (langSetting.Contains("Việt", StringComparison.OrdinalIgnoreCase) || langSetting.Equals("vi", StringComparison.OrdinalIgnoreCase))
                    langCode = "vi";
                else if (langSetting.Contains("English", StringComparison.OrdinalIgnoreCase) || langSetting.Equals("en", StringComparison.OrdinalIgnoreCase))
                    langCode = "en";
                else if (langSetting.Contains("Japanese", StringComparison.OrdinalIgnoreCase) || langSetting.Equals("ja", StringComparison.OrdinalIgnoreCase))
                    langCode = "ja";
                else if (langSetting.Contains("Korean", StringComparison.OrdinalIgnoreCase) || langSetting.Equals("ko", StringComparison.OrdinalIgnoreCase))
                    langCode = "ko";
                else if (langSetting.Contains("Chinese", StringComparison.OrdinalIgnoreCase) || langSetting.Equals("zh", StringComparison.OrdinalIgnoreCase))
                    langCode = "zh";

                int threadCount = Math.Max(4, Environment.ProcessorCount - 2);
                string langArg = langCode == "auto" ? "-l auto" : $"-l {langCode}";
                var whisperArgs = $"-m \"{whisperModel}\" -f \"{tempWavPath}\" -osrt -of \"{tempSrtBase}\" {langArg} -bs 5 -mc 0 -ml 60 -sow -t {threadCount} -np";
                onLog($"[CAPTION-SYNC] whisper-cli {whisperArgs}");

                var whisperPsi = new ProcessStartInfo
                {
                    FileName = whisperExe,
                    Arguments = whisperArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var whisperProcess = new Process { StartInfo = whisperPsi })
                {
                    whisperProcess.Start();
                    string whisperStdout = await whisperProcess.StandardOutput.ReadToEndAsync();
                    string whisperStderr = await whisperProcess.StandardError.ReadToEndAsync();
                    await whisperProcess.WaitForExitAsync(token);

                    if (!string.IsNullOrWhiteSpace(whisperStderr))
                        onLog($"[CAPTION-SYNC] Whisper log: {whisperStderr.Trim()}");

                    if (whisperProcess.ExitCode != 0)
                    {
                        onLog($"[CAPTION-SYNC-ERROR] Whisper failed (exit {whisperProcess.ExitCode}): {whisperStderr}");
                        return "";
                    }
                }

                if (!File.Exists(tempSrtPath))
                {
                    onLog($"[CAPTION-SYNC-ERROR] SRT file not generated at: {tempSrtPath}");
                    return "";
                }

                string srtContent = await File.ReadAllTextAsync(tempSrtPath, token);
                int captionCount = srtContent.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries).Length;
                onLog($"[CAPTION-SYNC] Transcription complete: {captionCount} caption segments detected.");

                // ───────────────────────────────────────────────
                // STEP 3: Convert SRT to ASS & Burn into video via FFmpeg
                // ───────────────────────────────────────────────
                onLog("[CAPTION-SYNC] Step 3/3: Burning captions into video...");

                int fontSize = job.CaptionSyncFontSize ?? 28;
                string fontColor = job.CaptionSyncColor ?? "white";
                string captionPos = job.CaptionSyncPosition?.Trim() ?? "Bottom Center";

                int alignment = 2; // Bottom Center
                int marginV = 80;

                if (captionPos.Contains("Top Center", StringComparison.OrdinalIgnoreCase))
                {
                    alignment = 8;
                    marginV = 80;
                }
                else if (captionPos.Equals("Center", StringComparison.OrdinalIgnoreCase))
                {
                    alignment = 5;
                    marginV = 0;
                }
                else if (captionPos.Contains("Custom", StringComparison.OrdinalIgnoreCase))
                {
                    alignment = 5; // Use center alignment, position will be overridden by \pos tag
                    marginV = 0;
                }

                // Convert fontColor name to ASS hex format (&HBBGGRR)
                string assColor = "&HFFFFFF"; // default white
                if (fontColor.StartsWith("#") && fontColor.Length >= 7)
                {
                    string r = fontColor.Substring(1, 2);
                    string g = fontColor.Substring(3, 2);
                    string b = fontColor.Substring(5, 2);
                    assColor = $"&H{b}{g}{r}";
                }
                else
                {
                    var colorMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["white"] = "&HFFFFFF", ["yellow"] = "&H00FFFF", ["cyan"] = "&HFFFF00",
                        ["green"] = "&H00FF00", ["red"] = "&H0000FF", ["blue"] = "&HFF0000",
                        ["orange"] = "&H0080FF", ["pink"] = "&HB469FF", ["gold"] = "&H00D7FF",
                        ["black"] = "&H000000"
                    };
                    if (colorMap.TryGetValue(fontColor, out string? mapped)) assColor = mapped;
                }

                // Generate ASS file
                string tempAssPath = tempSrtBase + ".ass";
                int resX = job.TargetWidth > 0 ? (int)job.TargetWidth : (job.SourceWidth > 0 ? job.SourceWidth : 1080);
                int resY = job.TargetHeight > 0 ? (int)job.TargetHeight : (job.SourceHeight > 0 ? job.SourceHeight : 1920);

                var assLines = new List<string>();
                assLines.Add("[Script Info]");
                assLines.Add($"PlayResX: {resX}");
                assLines.Add($"PlayResY: {resY}");
                assLines.Add("WrapStyle: 0");
                assLines.Add("");
                assLines.Add("[V4+ Styles]");
                string fontFamilySelection = job.CaptionSyncFontFamily ?? "Clean Sans (Arial Italic)";
                string actualFont = "Arial";
                if (fontFamilySelection.Contains("Aesthetic")) actualFont = "Georgia";
                else if (fontFamilySelection.Contains("Modern Sans")) actualFont = "Segoe UI";
                else if (fontFamilySelection.Contains("Classic Serif")) actualFont = "Times New Roman";
                else if (fontFamilySelection.Contains("Clean Sans")) actualFont = "Arial";
                else if (fontFamilySelection.Contains("Thư Pháp") || fontFamilySelection.Contains("Calligraphy")) actualFont = "Dancing Script";
                else if (fontFamilySelection.Contains("Anime")) actualFont = "Trebuchet MS";
                else if (fontFamilySelection.Contains("Typewriter")) actualFont = "Courier New";

                assLines.Add("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
                assLines.Add($"Style: Default,{actualFont},{fontSize},{assColor},&H000000FF,&H00000000,&H80000000,-1,0,0,0,100,100,0,0,1,2,1,{alignment},20,20,{marginV},1");
                assLines.Add("");
                assLines.Add("[Events]");
                assLines.Add("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

                var blocks = srtContent.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
                
                var parsedEvents = new List<(string start, string end, string text)>();
                var hallucinationBlacklist = new string[] {
                    "hãy subscribe cho kênh",
                    "hãy đăng kí cho kênh",
                    "để ủng hộ kênh",
                    "không bỏ lỡ những video",
                    "la la school",
                    "ghiền mì gõ"
                };

                foreach (var block in blocks)
                {
                    var lines = block.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    if (lines.Length >= 3)
                    {
                        var parts = lines[1].Split(new[] { " --> " }, StringSplitOptions.None);
                        if (parts.Length == 2)
                        {
                            string start = parts[0].Trim();
                            string end = parts[1].Trim();
                            if (start.Length >= 12) start = $"{start.Substring(1, 1)}:{start.Substring(3, 2)}:{start.Substring(6, 2)}.{start.Substring(9, 2)}";
                            if (end.Length >= 12) end = $"{end.Substring(1, 1)}:{end.Substring(3, 2)}:{end.Substring(6, 2)}.{end.Substring(9, 2)}";
                            string text = string.Join("\\N", lines.Skip(2)).Trim();
                            
                            bool isHallucination = false;
                            string lowerText = text.ToLowerInvariant();
                            foreach (var badPhrase in hallucinationBlacklist)
                            {
                                if (lowerText.Contains(badPhrase))
                                {
                                    isHallucination = true;
                                    break;
                                }
                            }
                            
                            if (!isHallucination)
                            {
                                parsedEvents.Add((start, end, text));
                            }
                        }
                    }
                }

                string animStyle = job.CaptionSyncAnimation ?? "Mặc định (Hiện tĩnh)";
                bool isSpotify = animStyle.Contains("Spotify", StringComparison.OrdinalIgnoreCase);
                bool isPop = animStyle.Contains("Pop", StringComparison.OrdinalIgnoreCase);
                bool isDrift = animStyle.Contains("Drift", StringComparison.OrdinalIgnoreCase);
                bool isGlow = animStyle.Contains("Glow", StringComparison.OrdinalIgnoreCase);
                bool isKaraoke = animStyle.Contains("Karaoke", StringComparison.OrdinalIgnoreCase);
                bool isTypewriter = animStyle.Contains("Typewriter", StringComparison.OrdinalIgnoreCase);
                bool isBlurReveal = animStyle.Contains("Blur Reveal", StringComparison.OrdinalIgnoreCase);

                // Calculate absolute coordinates for styles that require them (\pos, \move)
                double xPctDef = job.CaptionSyncXPercent ?? 50.0;
                double yPctDef = job.CaptionSyncYPercent ?? 85.0;
                int absX = (int)(resX * (xPctDef / 100.0));
                int absY = (int)(resY * (yPctDef / 100.0));
                if (!captionPos.Contains("Custom", StringComparison.OrdinalIgnoreCase))
                {
                    absX = resX / 2;
                    if (alignment == 8) absY = (int)(resY * 0.15); // Top Center
                    else if (alignment == 5) absY = resY / 2; // Center
                    else absY = (int)(resY * 0.85); // Bottom Center
                }

                if (isSpotify)
                {
                    // Spotify Scroll Logic
                    int dy = (int)(resY * 0.05); // 5% vertical distance between lines
                    int yActive = absY;
                    int yPrev = absY - dy; // move up
                    int yNext = absY + dy; // move down

                    for (int i = 0; i < parsedEvents.Count; i++)
                    {
                        var ev = parsedEvents[i];
                        string prevStart = i > 0 ? parsedEvents[i - 1].start : "0:00:00.00";
                        string safeEnd = ev.end.Length == 10 ? "0" + ev.end : ev.end;
                        string nextEnd = i < parsedEvents.Count - 1 ? parsedEvents[i + 1].end : TimeSpan.Parse(safeEnd).Add(TimeSpan.FromSeconds(1)).ToString(@"h\:mm\:ss\.ff");

                        // Preview phase
                        if (i > 0 && prevStart != ev.start)
                        {
                            assLines.Add($"Dialogue: 0,{prevStart},{ev.start},Default,,0,0,0,,{{\\an5\\pos({absX},{yNext})\\fscx85\\fscy85\\blur2\\alpha&HA0&\\fad(300,0)}}{ev.text}");
                        }

                        // Active phase
                        assLines.Add($"Dialogue: 0,{ev.start},{ev.end},Default,,0,0,0,,{{\\an5\\move({absX},{yNext},{absX},{yActive},0,300)\\fscx85\\fscy85\\blur2\\alpha&HA0&\\t(0,300,\\fscx115\\fscy115\\blur0\\alpha&H00&)}}{ev.text}");

                        // Post phase
                        if (i == parsedEvents.Count - 1)
                        {
                            assLines.Add($"Dialogue: 0,{ev.end},{nextEnd},Default,,0,0,0,,{{\\an5\\move({absX},{yActive},{absX},{yPrev},0,300)\\fscx115\\fscy115\\blur0\\alpha&H00&\\t(0,300,\\fscx85\\fscy85\\blur2\\alpha&HFF&)}}{ev.text}");
                        }
                        else if (ev.end != nextEnd)
                        {
                            assLines.Add($"Dialogue: 0,{ev.end},{nextEnd},Default,,0,0,0,,{{\\an5\\move({absX},{yActive},{absX},{yPrev},0,300)\\fscx115\\fscy115\\blur0\\alpha&H00&\\t(0,300,\\fscx85\\fscy85\\blur2\\alpha&HA0&)}}{ev.text}");
                        }
                    }
                }
                else
                {
                    foreach (var ev in parsedEvents)
                    {
                        string text = ev.text;
                        if (isPop)
                        {
                            text = $"{{\\an5\\pos({absX},{absY})\\fad(150,200)\\fscx0\\fscy0\\t(0,200,\\fscx120\\fscy120)\\t(200,350,\\fscx100\\fscy100)}}{text}";
                        }
                        else if (isDrift)
                        {
                            int yStart = absY + 20;
                            int yEnd = absY - 20;
                            text = $"{{\\an5\\move({absX},{yStart},{absX},{yEnd})\\fad(300,300)}}{text}";
                        }
                        else if (isGlow)
                        {
                            text = $"{{\\an5\\pos({absX},{absY})\\fad(300,300)\\fscx100\\fscy100\\blur0\\t(\\fscx110\\fscy110\\blur4)}}{text}";
                        }
                        else if (isKaraoke || isTypewriter || isBlurReveal)
                        {
                            if (string.IsNullOrEmpty(text)) 
                            {
                                assLines.Add($"Dialogue: 0,{ev.start},{ev.end},Default,,0,0,0,,");
                                continue;
                            }
                            string safeStart = ev.start.Replace(',', '.');
                            string safeEnd = ev.end.Replace(',', '.');
                            if (TimeSpan.TryParse(safeStart, out TimeSpan startTS) && TimeSpan.TryParse(safeEnd, out TimeSpan endTS))
                            {
                                double totalDurationMs = Math.Max(10, (endTS - startTS).TotalMilliseconds);
                                if (isKaraoke)
                                {
                                    double totalDurationCs = totalDurationMs / 10.0;
                                    var sb = new System.Text.StringBuilder();
                                    sb.Append($"{{\\an5\\pos({absX},{absY})}}{{\\2a&HFF&}}");
                                    // Remove multiple spaces to avoid empty words breaking the logic
                                    string[] words = text.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                    if (words.Length > 0) 
                                    {
                                        double csPerChar = totalDurationCs / text.Length;
                                        int accumulatedCs = 0;
                                        for (int w = 0; w < words.Length; w++)
                                        {
                                            int charsInWord = words[w].Length + (w < words.Length - 1 ? 1 : 0);
                                            int wordDurationCs = (int)Math.Round(charsInWord * csPerChar);
                                            
                                            // Fix cumulative rounding drift on the last word
                                            if (w == words.Length - 1)
                                            {
                                                wordDurationCs = Math.Max(1, (int)Math.Round(totalDurationCs) - accumulatedCs);
                                            }
                                            accumulatedCs += wordDurationCs;
                                            
                                            sb.Append($"{{\\kf{wordDurationCs}}}{words[w]}");
                                            if (w < words.Length - 1) sb.Append(" ");
                                        }
                                        text = sb.ToString();
                                    }
                                }
                                else if (isTypewriter || isBlurReveal)
                                {
                                    if (text.Length > 300) text = text.Substring(0, 300); // Prevent buffer overflow
                                    var runes = text.EnumerateRunes().ToList();
                                    double delayPerChar = runes.Count > 0 ? totalDurationMs / runes.Count : 0;
                                    var sb = new System.Text.StringBuilder();
                                    sb.Append($"{{\\an5\\pos({absX},{absY})}}");
                                    for (int c = 0; c < runes.Count; c++)
                                    {
                                        int startOffset = (int)(c * delayPerChar);
                                        int endOffset = startOffset + (isBlurReveal ? 200 : 10);
                                        if (isBlurReveal)
                                        {
                                            sb.Append($"{{\\alpha&HFF&\\blur15\\t({startOffset},{endOffset},\\alpha&H00&\\blur0)}}{runes[c]}");
                                        }
                                        else
                                        {
                                            sb.Append($"{{\\alpha&HFF&\\t({startOffset},{endOffset},\\alpha&H00&)}}{runes[c]}");
                                        }
                                    }
                                    text = sb.ToString();
                                }

                            }
                            else
                            {
                                text = $"{{\\an5\\pos({absX},{absY})}}{text}";
                            }
                        }
                        else
                        {
                            // Default static
                            if (captionPos.Contains("Custom", StringComparison.OrdinalIgnoreCase))
                            {
                                text = $"{{\\pos({absX},{absY})}}" + text;
                            }
                        }
                        
                        // Only add Dialogue line if it wasn't fully formatted by Karaoke/Typewriter (which omit the text var sometimes? No, they re-assign text)
                        assLines.Add($"Dialogue: 0,{ev.start},{ev.end},Default,,0,0,0,,{text}");
                    }
                }
                
                await File.WriteAllLinesAsync(tempAssPath, assLines, token);

                onLog("[CAPTION-SYNC] ✅ Pre-pass ASS file generated successfully.");
                return tempAssPath;
            }
            catch (Exception ex)
            {
                onLog($"[CAPTION-SYNC-ERROR] {ex.Message}");
                return "";
            }
            finally
            {
                // Cleanup temp files
                TryDeleteFileSafe(tempWavPath);
                TryDeleteFileSafe(tempSrtPath);
                // Note: We DO NOT delete tempAssPath here because it will be injected into the main ffmpeg render graph!
            }
        }


        public static string BuildAudio1EffectChain(string? effectName, double intensityPercent = 100.0)
        {
            if (string.IsNullOrWhiteSpace(effectName)) return string.Empty;
            string e = effectName.Trim();

            if (e.Contains("Không") || e.Contains("Giữ nguyên")) return string.Empty;

            // Intensity 0..100 -> 0..1, clamped. Scales every effect down smoothly.
            double k = Math.Clamp(intensityPercent, 0.0, 100.0) / 100.0;
            if (k <= 0.001) return string.Empty;
            if (e.Contains("Echo") || e.Contains("Church") || e.Contains("Nhà thờ"))
                return string.Empty; // Echo & Church are rendered via IR convolution (PrepareAudioFxTrackAsync), not an inline chain.
            if (e.Contains("Robot") || e.Contains("Glitch") || e.Contains("Kim loại"))
                return $"aecho={FfmpegDouble(0.8,3)}:{FfmpegDouble(0.88,3)}:{(int)(6*k)+1}:{FfmpegDouble(0.4*k,3)},highpass=f=200,lowpass=f=3400,volume={FfmpegDouble(1+0.4*k,3)}";
            if (e.Contains("Điện thoại") || e.Contains("Telephone") || e.Contains("Bandpass"))
                return $"highpass=f={(int)(500-300*k)},lowpass=f={(int)(2500+10000*(1-k))},volume={FfmpegDouble(1+0.6*k,3)}";
            if (e.Contains("Chipmunk") || e.Contains("Tăng tone"))
                return $"asetrate=44100*{FfmpegDouble(1+0.35*k,4)},aresample=44100,atempo={FfmpegDouble(1/(1+0.35*k),4)}";
            if (e.Contains("Deep") || e.Contains("Giảm tone"))
                return $"asetrate=44100*{FfmpegDouble(1-0.25*k,4)},aresample=44100,atempo={FfmpegDouble(1/(1-0.25*k),4)}";
            if (e.Contains("Chorus"))
                return $"chorus=0.5:{FfmpegDouble(0.9*k,3)}:50|60|70:{FfmpegDouble(0.3*k,3)}|{FfmpegDouble(0.22*k,3)}|{FfmpegDouble(0.3*k,3)}:0.25|0.4|0.3:2|2.3|1.3";
            if (e.Contains("Phaser") || e.Contains("Phát xung"))
                return $"aphaser=type=t:speed={FfmpegDouble(2*k,3)}:decay={FfmpegDouble(0.6*k,3)}";
            if (e.Contains("Bitcrush") || e.Contains("rè kỹ thuật số") || e.Contains("Rè kỹ thuật số"))
                return $"acrusher=level_in={FfmpegDouble(8,3)}:level_out={FfmpegDouble(18,3)}:bits={(int)Math.Round(16-8*k)}:mode=log:aa=1";
            if (e.Contains("Whisper") || e.Contains("Thì thầm"))
                return "afftfilt=real='hypot(re,im)*cos((random(0)*2-1)*2*3.14)':imag='hypot(re,im)*sin((random(1)*2-1)*2*3.14)':win_size=128:overlap=0.8";
            return string.Empty;
        }

        public static async Task<(string ConcatListPath, string MergedAudioPath)> MergeAudiosToTempTrackAsync(
            IReadOnlyList<string> audioPaths,
            string outDir,
            string mergeTag,
            CancellationToken token,
            Action<string> onLog)
        {
            if (audioPaths == null || audioPaths.Count < 2)
                throw new InvalidOperationException("Audio merge requires at least 2 input tracks.");

            var normalizedPaths = new List<string>();
            foreach (string input in audioPaths)
            {
                if (string.IsNullOrWhiteSpace(input))
                    continue;

                string fullPath = Path.GetFullPath(input.Trim());
                if (!File.Exists(fullPath))
                    throw new FileNotFoundException($"Audio source does not exist: {fullPath}");

                normalizedPaths.Add(fullPath);
            }

            if (normalizedPaths.Count < 2)
                throw new InvalidOperationException("Audio merge requires at least 2 valid tracks.");

            string safeTag = string.IsNullOrWhiteSpace(mergeTag) ? Guid.NewGuid().ToString("N").Substring(0, 8) : mergeTag;
            string tempWorkDir = GetTitanTempDir();
            string concatListPath = Path.Combine(tempWorkDir, $"merge_{safeTag}_audio_list.txt");
            string mergedAudioPath = Path.Combine(tempWorkDir, $"merge_{safeTag}_audio.m4a");

            string[] concatLines = normalizedPaths
                .Select(p => $"file '{BuildConcatListPathToken(p)}'")
                .ToArray();
            File.WriteAllLines(concatListPath, concatLines, new UTF8Encoding(false));

            onLog("[AUDIO-MERGE] Attempt 1/2: concat stream copy");
            string copyArgs = $"-y -f concat -safe 0 -i \"{concatListPath}\" -c copy \"{mergedAudioPath}\"";
            var (copyExit, copyErr) = await RunFfmpegCaptureAsync(copyArgs, token);

            if (copyExit != 0 || !File.Exists(mergedAudioPath) || new FileInfo(mergedAudioPath).Length <= 0)
            {
                string copySummary = string.Join(" | ",
                    (copyErr ?? string.Empty)
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Take(5));
                if (!string.IsNullOrWhiteSpace(copySummary))
                    onLog($"[AUDIO-MERGE-WARN] Copy concat failed: {copySummary}");

                TryDeleteFileSafe(mergedAudioPath);

                onLog("[AUDIO-MERGE] Attempt 2/2: concat with AAC re-encode");
                string reencodeArgs = $"-y -f concat -safe 0 -i \"{concatListPath}\" -vn -c:a aac -b:a 192k \"{mergedAudioPath}\"";
                var (reencodeExit, reencodeErr) = await RunFfmpegCaptureAsync(reencodeArgs, token);

                if (reencodeExit != 0 || !File.Exists(mergedAudioPath) || new FileInfo(mergedAudioPath).Length <= 0)
                {
                    string reencodeSummary = string.Join(" | ",
                        (reencodeErr ?? string.Empty)
                            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Take(8));
                    throw new Exception($"Audio merge failed: {reencodeSummary}");
                }
            }

            return (concatListPath, mergedAudioPath);
        }

        public static async Task<string> MixAudiosToTempTrackAsync(
            IReadOnlyList<string> audioPaths,
            IReadOnlyList<double> audioVolumes,
            string outDir,
            string mixTag,
            CancellationToken token,
            Action<string> onLog)
        {
            if (audioPaths == null || audioPaths.Count < 2)
                throw new InvalidOperationException("Audio overlay mix requires at least 2 input tracks.");

            var normalizedPaths = new List<string>();
            foreach (string input in audioPaths)
            {
                if (string.IsNullOrWhiteSpace(input))
                    continue;

                string fullPath = Path.GetFullPath(input.Trim());
                if (!File.Exists(fullPath))
                    throw new FileNotFoundException($"Audio source does not exist: {fullPath}");

                normalizedPaths.Add(fullPath);
            }

            if (normalizedPaths.Count < 2)
                throw new InvalidOperationException("Audio overlay mix requires at least 2 valid tracks.");

            string tempWorkDir = GetTitanTempDir();
            string safeTag = string.IsNullOrWhiteSpace(mixTag)
                ? Guid.NewGuid().ToString("N").Substring(0, 8)
                : Regex.Replace(mixTag.Trim(), @"[^\w\-]+", "_");
            string mixedAudioPath = Path.Combine(tempWorkDir, $"mix_{safeTag}_audio.m4a");

            var args = new StringBuilder("-y ");
            foreach (string path in normalizedPaths)
                args.Append($"-i \"{path}\" ");

            var filterParts = new List<string>();
            for (int i = 0; i < normalizedPaths.Count; i++)
            {
                double volume = i < audioVolumes.Count ? audioVolumes[i] : 1.0;
                volume = Math.Clamp(volume, 0.0, 2.0);
                filterParts.Add($"[{i}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume={FfmpegDouble(volume, 4)}[a{i}]");
            }

            string inputLabels = string.Concat(Enumerable.Range(0, normalizedPaths.Count).Select(i => $"[a{i}]"));
            filterParts.Add($"{inputLabels}amix=inputs={normalizedPaths.Count}:duration=longest:dropout_transition=2:normalize=0,alimiter=limit=0.95[aout]");

            args.Append($"-filter_complex \"{string.Join(";", filterParts)}\" ");
            args.Append("-map \"[aout]\" -vn -c:a aac -b:a 192k -ar 48000 -ac 2 ");
            args.Append($"\"{mixedAudioPath}\"");

            onLog($"[AUDIO-OVERLAY] Mixing {normalizedPaths.Count} audio track(s) with independent volume.");
            var (exitCode, stderr) = await RunFfmpegCaptureAsync(args.ToString(), token);
            if (exitCode != 0 || !File.Exists(mixedAudioPath) || new FileInfo(mixedAudioPath).Length <= 0)
            {
                string summary = string.Join(" | ",
                    (stderr ?? string.Empty)
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Take(8));
                throw new Exception($"Audio overlay mix failed: {summary}");
            }

            return mixedAudioPath;
        }

        private static List<AudioEditSegment> ResolveAudioEditSegments(RenderJob job, double? maxDurationSeconds = null)
        {
            var segments = new List<AudioEditSegment>();

            if (job.AudioSegments != null && job.AudioSegments.Count > 0)
            {
                segments.AddRange(job.AudioSegments.Select(seg => new AudioEditSegment
                {
                    StartSeconds = seg.StartSeconds,
                    EndSeconds = seg.EndSeconds
                }));
            }
            else
            {
                bool hasStart = AudioEditHelpers.TryParseFlexibleTime(job.AudioTrimStart, out double trimStart);
                bool hasEnd = AudioEditHelpers.TryParseFlexibleTime(job.AudioTrimEnd, out double trimEnd);
                bool hasDuration = AudioEditHelpers.TryParseFlexibleTime(job.AudioTrimDuration, out double trimDuration);

                if (!hasStart && (hasEnd || hasDuration))
                    trimStart = 0.0;

                if (hasStart || hasEnd || hasDuration)
                {
                    double trimEndSeconds = hasEnd ? trimEnd : (hasDuration ? trimStart + trimDuration : trimStart);
                    segments.Add(new AudioEditSegment
                    {
                        StartSeconds = trimStart,
                        EndSeconds = trimEndSeconds
                    });
                }
            }

            return AudioEditHelpers.NormalizeSegments(segments, maxDurationSeconds);
        }

        private static string BuildAudioSegmentFilterComplex(IReadOnlyList<AudioEditSegment> segments, Action<string> onLog)
        {
            if (segments == null || segments.Count == 0)
                return string.Empty;

            if (segments.Count == 1)
            {
                var segment = segments[0];
                string singleTrim = $"[0:a]atrim=start={FfmpegDouble(segment.StartSeconds)}:end={FfmpegDouble(segment.EndSeconds)},asetpts=PTS-STARTPTS[aout]";
                onLog($"[AUDIO-EDIT-FILTER] Single trim: {segment.DisplayLabel}");
                return singleTrim;
            }

            var sb = new StringBuilder();
            sb.Append($"[0:a]asplit={segments.Count}");
            for (int i = 0; i < segments.Count; i++)
                sb.Append($"[a{i}]");
            sb.Append(";");

            for (int i = 0; i < segments.Count; i++)
            {
                AudioEditSegment seg = segments[i];
                sb.Append($"[a{i}]atrim=start={FfmpegDouble(seg.StartSeconds)}:end={FfmpegDouble(seg.EndSeconds)},asetpts=PTS-STARTPTS[s{i}];");
            }

            for (int i = 0; i < segments.Count; i++)
                sb.Append($"[s{i}]");

            sb.Append($"concat=n={segments.Count}:v=0:a=1[aout]");
            onLog($"[AUDIO-EDIT-FILTER] Multi-segment concat: {segments.Count} segment(s)");
            return sb.ToString();
        }

        private static async Task<string?> PrepareEditedAudioTrackAsync(
            string inputAudioPath,
            RenderJob job,
            string outDir,
            string renderTag,
            CancellationToken token,
            Action<string> onLog)
        {
            if (string.IsNullOrWhiteSpace(inputAudioPath) || !File.Exists(inputAudioPath))
                return null;

            var audioMeta = await EngineCore.AnalyzeMediaAsync(inputAudioPath);
            double maxDuration = audioMeta.Duration > 0.0 ? audioMeta.Duration : 0.0;
            List<AudioEditSegment> segments = ResolveAudioEditSegments(job, maxDuration > 0.0 ? maxDuration : null);

            if (segments.Count == 0)
                return null;

            if (AudioEditHelpers.IsFullTrackSelection(segments, maxDuration))
                return null;

            string safeTag = string.IsNullOrWhiteSpace(renderTag)
                ? Guid.NewGuid().ToString("N").Substring(0, 8)
                : renderTag;

            string editedAudioPath = Path.Combine(GetTitanTempDir(), $"audioedit_{safeTag}.m4a");
            string filterComplex = BuildAudioSegmentFilterComplex(segments, onLog);
            if (string.IsNullOrWhiteSpace(filterComplex))
                return null;

            string args =
                $"-y -i \"{inputAudioPath}\" " +
                $"-filter_complex \"{filterComplex}\" " +
                "-map \"[aout]\" -vn -c:a aac -b:a 192k -movflags +faststart " +
                $"\"{editedAudioPath}\"";

            onLog($"[AUDIO-EDIT] Preprocessing audio cuts into temp track...");
            var (exitCode, stderr) = await RunFfmpegCaptureAsync(args, token);
            if (exitCode != 0 || !File.Exists(editedAudioPath) || new FileInfo(editedAudioPath).Length <= 0)
            {
                string summary = string.Join(" | ",
                    (stderr ?? string.Empty)
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Take(8));
                throw new Exception($"Audio edit failed: {summary}");
            }

            onLog($"[AUDIO-EDIT] Ready: {Path.GetFileName(editedAudioPath)}");
            return editedAudioPath;
        }

        private static async Task<string?> PrepareAudioFxTrackAsync(
            string inputAudioPath,
            RenderJob job,
            string outDir,
            string renderTag,
            CancellationToken token,
            Action<string> onLog)
        {
            if (string.IsNullOrWhiteSpace(inputAudioPath) || !File.Exists(inputAudioPath))
                return null;

            string effect = (job.Audio1Effect ?? string.Empty).Trim();

            bool isEcho = effect.Contains("Echo", StringComparison.OrdinalIgnoreCase);
            bool isChurch = effect.Contains("Church", StringComparison.OrdinalIgnoreCase) || effect.Contains("Nhà thờ", StringComparison.OrdinalIgnoreCase);
            if (!isEcho && !isChurch)
                return null; // robot/phone/bitcrush/etc are handled inline in the chain
            double intensity = Math.Clamp(job.Audio1EffectIntensity, 0.0, 100.0) / 100.0;
            if (intensity <= 0.001) return null;

            string safeTag = string.IsNullOrWhiteSpace(renderTag)
                ? Guid.NewGuid().ToString("N").Substring(0, 8)
                : renderTag;

            string? irPath = null;
            bool isChurchPtr = isChurch;
            string fxDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Effects");
            string bundled = isChurchPtr
                ? Path.Combine(fxDir, "church_ir.wav")
                : Path.Combine(fxDir, "echo_ir.wav");
            if (File.Exists(bundled)) irPath = bundled;
            if (irPath == null)
            {
                // No bundled IR file found; skip IR pre-pass instead of producing silence.
                onLog($"[AUDIO1-FX-WARN] IR file missing at {bundled}. Skipping IR pre-pass for {effect}.");
                return null;
            }

            string fxAudioPath = Path.Combine(GetTitanTempDir(), $"fx_{safeTag}.m4a");
            int maxWet = isChurch ? 6 : 3;
            double k = intensity;
            double wet = Math.Clamp(maxWet * k, 0, 10);
            // Dry stays at full level and only the reverb tail (wet) is scaled.
            string wetStr = wet.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            // asplit sends the original voice to a dry branch and a wet branch;
            // the wet branch is convolved with the IR, then the two are mixed.
            // (Running afir as a direct single-stream filter with dry/wet drops
            //  the input level to near silence on this FFmpeg build.)
            string args =
                $"-y -i \"{inputAudioPath}\" -i \"{irPath}\" " +
                $"-filter_complex \"[0:a]asplit=2[ary][wy];[wy][1:a]afir=length=1:dry=0:wet={wetStr},alimiter=limit=0.95[wetout];[ary][wetout]amix=inputs=2:duration=first:normalize=0,alimiter=limit=0.95[a]\" " +
                "-map \"[a]\" -vn -c:a aac -b:a 192k -movflags +faststart " +
                $"\"{fxAudioPath}\"";

            onLog($"[AUDIO1-FX] Preprocessing {effect} @ {intensity * 100:F0}% via IR convolution...");
            var (exitCode, stderr) = await RunFfmpegCaptureAsync(args, token);
            if (exitCode != 0 || !File.Exists(fxAudioPath) || new FileInfo(fxAudioPath).Length <= 0)
            {
                string summary = string.Join(" | ",
                    (stderr ?? string.Empty)
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("  configuration") && !l.StartsWith("ffmpeg version") && !l.Contains("libav"))
                        .TakeLast(8));
                throw new Exception($"Audio 1 FX IR render failed: {summary}");
            }

            onLog($"[AUDIO1-FX] Ready: {Path.GetFileName(fxAudioPath)}");
            return fxAudioPath;
        }
    }
}
