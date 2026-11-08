using Microsoft.Win32;
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
using System.Media;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace TitanEngine
{
    public static partial class EngineCore
    {


        private static async Task<bool> TryAutoBuildNvidiaUpscaleBackendAsync(Action<string> onLog, CancellationToken token)
        {
            try
            {
                RefreshNvidiaUpscaleBackendPaths();
                if (HasNgxDlssSupport())
                    return true;

                string dlssDir = GetDlssDirectoryPath();
                string? sampleRoot = ResolveVfxSamplesSourceRoot(dlssDir);
                if (string.IsNullOrWhiteSpace(sampleRoot))
                {
                    onLog("[DLSS-NGX] Sample source folder not found after download.");
                    return false;
                }

                string? cmakePath = ResolveCmakePath();
                if (string.IsNullOrWhiteSpace(cmakePath))
                {
                    bool cmakeInstalled = await TryAutoInstallCmakeAsync(onLog, token);
                    if (cmakeInstalled)
                        cmakePath = ResolveCmakePath();
                }
                if (string.IsNullOrWhiteSpace(cmakePath))
                {
                    onLog("[DLSS-NGX] CMake not found. Install CMake or Visual Studio CMake tools.");
                    return false;
                }

                string? vfxSdkRoot = ResolveVfxSdkRoot(onLog);
                if (string.IsNullOrWhiteSpace(vfxSdkRoot))
                {
                    onLog("[DLSS-NGX] VFX SDK Core not found on this machine.");
                    onLog("[DLSS-NGX] Install VFX SDK first, then reopen app for auto build.");
                    onLog("[DLSS-NGX] Docs: https://docs.nvidia.com/maxine/vfx/latest/WindowsVFXSDK/InstalltheVFXSDK.html");
                    return false;
                }

                if (!HasUpscaleFeatureInstalled(vfxSdkRoot))
                {
                    onLog("[DLSS-NGX] Required feature 'nvvfxupscale' is not installed.");
                    onLog("[DLSS-NGX] Run install_feature.ps1 in your VFX SDK features directory.");
                    return false;
                }

                string buildDir = Path.Combine(sampleRoot, "build_titan_auto");
                Directory.CreateDirectory(buildDir);

                string configureArgs = $"-S \"{sampleRoot}\" -B \"{buildDir}\" -G \"Visual Studio 17 2022\" -A x64 -DVFXSDK_ROOT=\"{vfxSdkRoot}\"";
                onLog($"[DLSS-BUILD] Configure using VFXSDK_ROOT={vfxSdkRoot}");
                int configureExit = await RunProcessWithLogAsync(cmakePath, configureArgs, sampleRoot, "DLSS-BUILD-CMAKE", onLog, token);
                if (configureExit != 0)
                {
                    onLog($"[DLSS-BUILD] Configure failed with exit code {configureExit}");
                    return false;
                }

                string buildArgs = $"--build \"{buildDir}\" --config Release --target UpscalePipelineApp --parallel";
                onLog("[DLSS-BUILD] Building UpscalePipelineApp...");
                int buildExit = await RunProcessWithLogAsync(cmakePath, buildArgs, sampleRoot, "DLSS-BUILD", onLog, token);
                if (buildExit != 0)
                {
                    onLog($"[DLSS-BUILD] Build failed with exit code {buildExit}");
                    return false;
                }

                RefreshNvidiaUpscaleBackendPaths();
                if (!string.IsNullOrWhiteSpace(_vfxUpscaleExePath) && File.Exists(_vfxUpscaleExePath))
                {
                    StageBuiltUpscalePipelineBackend(_vfxUpscaleExePath, dlssDir, onLog);
                    RefreshNvidiaUpscaleBackendPaths();
                    onLog($"[DLSS-BUILD] Ready: {_vfxUpscaleExePath}");
                    return true;
                }

                onLog("[DLSS-BUILD] Build finished but UpscalePipelineApp.exe was not found.");
                return false;
            }
            catch (Exception ex)
            {
                onLog($"[DLSS-BUILD] Auto build exception: {ex.Message}");
                return false;
            }
        }

        // [NEW] PROFESSIONAL COLOR GRADING FILTERS
        private static string GetColorFilterCmd(string colorFilter)
        {
            return colorFilter switch
            {
                // === CINEMATIC: Teal & Orange Look ===
                // Boost shadows to teal, highlights to orange, DARKEN OVERALL
                "Cinematic" => "colorbalance=rs=0.1:bs=0.2,eq=saturation=1.3:contrast=1.2:brightness=-0.1",

                // === VINTAGE: 1980s Film Look ===
                // Fade blacks, yellow cast, faded colors
                "Vintage" => "eq=saturation=0.7:contrast=0.85:brightness=-0.05",

                // === PASTEL: Soft Pink/Dreamy ===
                // Bright, pink cast, low contrast
                "Pastel" => "colorbalance=rm=0.15:gm=0.1:bm=0.15,eq=brightness=0.08:saturation=1.1:contrast=0.9",

                // === CYBERPUNK: Neon Nights ===
                // Deep blue/purple shadows, vibrant
                "Cyberpunk" => "colorbalance=rs=-0.25:bs=0.3,eq=saturation=1.6:contrast=1.25:brightness=0.05",

                // === B&W PRO: High Contrast Monochrome ===
                // Black & white with strong contrast
                "B&W Pro" => "hue=s=0,eq=contrast=1.4:brightness=0.05",

                // === SPRING: Vibrant Green Cast ===
                "Spring" => "colorbalance=gm=0.25:bm=-0.1,eq=saturation=1.5:brightness=0.1",

                // === AUTUMN: Warm Orange/Brown ===
                "Autumn" => "colorbalance=rm=0.3:bm=-0.15,eq=saturation=1.4:brightness=-0.05:contrast=1.1",

                // === WINTER: Cool Blue Tones ===
                "Winter" => "colorbalance=bs=0.3:rs=-0.15,eq=saturation=0.7:brightness=-0.1:contrast=1.05",

                // === COLD: Arctic Blue ===
                "Cold" => "colorbalance=bs=0.4:rs=-0.3,eq=saturation=1.15:contrast=1.05",

                // === WARM: Golden Hour ===
                "Warm" => "colorbalance=rs=0.4:bs=-0.2:gm=0.1,eq=saturation=1.25:brightness=0.1",

                _ => ""
            };
        }

        private static string BuildPlaybackSpeedVideoFilter(double playbackSpeed, Action<string> onLog)
        {
            var invCulture = System.Globalization.CultureInfo.InvariantCulture;
            double ptsMultiplier = 1.0 / playbackSpeed;
            onLog($"[FILTER-SLOWMOTION] Playback speed {playbackSpeed:F4}x (fast mode: setpts only)");
            return $"setpts=(PTS-STARTPTS)*{ptsMultiplier.ToString("F4", invCulture)}";
        }

        private static string FfmpegDouble(double value, int decimals = 4)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                value = 0.0;

            return value.ToString($"F{decimals}", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Thread budget handed to ffmpeg so filter slices (and, on the libx264 fallback, the
        /// encoder) cannot claim every core the machine has. ffmpeg defaults its filter and
        /// encoder thread pools to the full core count, which on the filter-heavy graphs this
        /// pipeline builds (base scale/crop + overlay blends at output resolution) pegs every
        /// core at 100 % for the whole encode - reported as "CPU siêu ác" while wall time barely
        /// improves, because the graphs do not scale linearly past roughly three quarters of the
        /// cores. Filters are deterministic per slice, so the cap changes TIMING only: the
        /// encoded frames are identical at any thread count (verified: x264 and the slice-threaded
        /// filters - scale, overlay, crop, format - are bit-deterministic w.r.t. threading).
        /// 3/4 of the logical cores, floored at 2, leaves one core's worth of headroom for the
        /// UI, the queue worker and the overlay pre-render STA threads.
        /// </summary>
        private static int FfmpegThreadCapCount()
            => Math.Max(2, (int)Math.Floor(Environment.ProcessorCount * 0.75));

        /// <summary>
        /// The cap as global ffmpeg arguments. Both flags are required: <c>-filter_complex_threads</c>
        /// bounds the filter_complex graphs the overlay branches build, while <c>-filter_threads</c>
        /// bounds simple <c>-vf</c> chains (the template segment pre-render path).
        /// </summary>
        private static string FfmpegThreadCapArguments()
        {
            int cap = FfmpegThreadCapCount();
            return $"-filter_threads {cap} -filter_complex_threads {cap} ";
        }

        private static string BuildFadeVideoFilter((double FadeIn, double FadeOut, double FadeOutStart) fade, Action<string> onLog)
        {
            var filters = new List<string>();

            if (fade.FadeIn > 0.001)
                filters.Add($"fade=t=in:st=0:d={FfmpegDouble(fade.FadeIn)}");

            if (fade.FadeOut > 0.001)
                filters.Add($"fade=t=out:st={FfmpegDouble(fade.FadeOutStart)}:d={FfmpegDouble(fade.FadeOut)}");

            string result = string.Join(",", filters);
            if (!string.IsNullOrWhiteSpace(result))
                onLog($"[FILTER-FADE-VIDEO] {result}");

            return result;
        }

        private static string BuildBrightnessFilter(double videoBrightness, Action<string> onLog)
        {
            if (double.IsNaN(videoBrightness) || double.IsInfinity(videoBrightness))
                videoBrightness = 0.0;

            double normalizedBrightness = Math.Clamp(videoBrightness / 200.0, -0.5, 0.5);
            if (Math.Abs(normalizedBrightness) <= 0.0001)
                return string.Empty;

            string filter = $"eq=brightness={FfmpegDouble(normalizedBrightness, 4)}";
            onLog($"[FILTER-BRIGHTNESS] {filter} (ui={videoBrightness:+0;-0;0})");
            return filter;
        }

        private static string BuildWatermarkFilterComplex(
            string baseInputLabel,
            string baseVideoFilters,
            string postVideoFilters,
            int? watermarkInputIndex,
            int? brandLogoInputIndex,
            RenderJob job,
            Action<string> onLog)
        {
            var graph = new StringBuilder();
            string inputLabel = string.IsNullOrWhiteSpace(baseInputLabel) ? "[0:v]" : baseInputLabel;
            if (!string.IsNullOrWhiteSpace(baseVideoFilters))
                graph.Append($"{inputLabel}{baseVideoFilters}[basev];");
            else
                graph.Append($"{inputLabel}null[basev];");

            string currentLabel = "basev";

            if (brandLogoInputIndex.HasValue)
            {
                var brandProfile = ResolveBrandLogoOverlayProfile(job);
                string logoScaleExpr = FfmpegDouble(brandProfile.RelativeWidth, 6);
                string frameLeftExpr = FfmpegDouble(brandProfile.FrameLeft, 3);
                string frameTopExpr = FfmpegDouble(brandProfile.FrameTop, 3);
                string frameRightExpr = FfmpegDouble(brandProfile.FrameRight, 3);
                string frameBottomExpr = FfmpegDouble(brandProfile.FrameBottom, 3);
                string paddingExpr = FfmpegDouble(brandProfile.Padding, 3);
                double bounceAmplitude = Math.Clamp((brandProfile.FrameBottom - brandProfile.FrameTop) * 0.0055, 2.5, 5.0);
                string bounceExpr = FfmpegDouble(bounceAmplitude, 3);
                string glowScaleExpr = FfmpegDouble(brandProfile.RelativeWidth * 1.11, 6);
                string brandOverlayXBaseExpr = brandProfile.Position switch
                {
                    "Center Top" or "Center Bottom" => $"({frameLeftExpr}+{frameRightExpr}-overlay_w)/2",
                    "Top Left" or "Bottom Left" => $"{frameLeftExpr}+{paddingExpr}",
                    _ => $"{frameRightExpr}-{paddingExpr}-overlay_w"
                };
                string brandOverlayYBaseExpr = brandProfile.Position switch
                {
                    "Center Bottom" => $"{frameBottomExpr}-{paddingExpr}-overlay_h+sin(t*2.75)*{bounceExpr}",
                    "Bottom Left" or "Bottom Right" => $"{frameBottomExpr}-{paddingExpr}-overlay_h+sin(t*2.75)*{bounceExpr}",
                    _ => $"{frameTopExpr}+{paddingExpr}+sin(t*2.75)*{bounceExpr}"
                };
                string brandOverlayXExpr =
                    $"min(max({brandOverlayXBaseExpr},{frameLeftExpr}),max({frameLeftExpr},{frameRightExpr}-overlay_w))";
                string brandOverlayYExpr =
                    $"min(max({brandOverlayYBaseExpr},{frameTopExpr}),max({frameTopExpr},{frameBottomExpr}-overlay_h))";

                graph.Append($"[{brandLogoInputIndex.Value}:v]format=rgba,setpts=PTS-STARTPTS[brandlogo];");
                graph.Append($"[{currentLabel}]setpts=PTS-STARTPTS[brandbase];");
                graph.Append($"[brandlogo]split=2[brandmainsrc][brandglowsrc];");
                graph.Append($"[brandmainsrc][brandbase]scale2ref=w=main_w*{logoScaleExpr}:h=-1[brandfitpre][brandref];");
                graph.Append($"[brandfitpre]format=rgba[brandfit];");
                graph.Append($"[brandglowsrc][brandref]scale2ref=w=main_w*{glowScaleExpr}:h=-1[brandglowfitpre][brandref2];");
                graph.Append($"[brandglowfitpre]format=rgba,colorchannelmixer=aa=0.68,gblur=sigma=9:steps=2[brandglow];");
                graph.Append($"[brandref2][brandglow]overlay=" +
                    $"x='{brandOverlayXExpr}':" +
                    $"y='{brandOverlayYExpr}':" +
                    $"eval=frame[brandglowout];");
                graph.Append($"[brandglowout][brandfit]overlay=" +
                    $"x='{brandOverlayXExpr}':" +
                    $"y='{brandOverlayYExpr}':" +
                    $"eval=frame[brandout];");

                currentLabel = "brandout";
                onLog($"[FILTER-BRAND-LOGO] width~{brandProfile.RelativeWidth:P0} of canvas, pos={brandProfile.Position}, glow=on, bob={bounceAmplitude:F1}px");
            }

            if (watermarkInputIndex.HasValue)
            {
                double scale = Math.Clamp(job.WatermarkScale, 0.01, 6.0);
                double opacity = Math.Clamp(job.WatermarkOpacity, 0.0, 1.0);
                double rotationRadians = job.WatermarkRotation * Math.PI / 180.0;
                double x = Math.Clamp(job.WatermarkXPercent, 0.0, 1.0);
                double y = Math.Clamp(job.WatermarkYPercent, 0.0, 1.0);

                double relativeWidth = 0.20 * scale;
                string scaleExpr = FfmpegDouble(relativeWidth, 6);
                string opacityExpr = FfmpegDouble(opacity, 4);
                string rotationExpr = FfmpegDouble(rotationRadians, 6);
                string xExpr = FfmpegDouble(x, 6);
                string yExpr = FfmpegDouble(y, 6);

                graph.Append($"[{watermarkInputIndex.Value}:v]format=rgba,colorchannelmixer=aa={opacityExpr}[wmalpha];");
                graph.Append($"[wmalpha][{currentLabel}]scale2ref=w=main_w*{scaleExpr}:h=-1[wmfit][vref];");
                graph.Append($"[wmfit]rotate={rotationExpr}:c=none:ow=rotw(iw):oh=roth(ih)[wmrot];");

                string overlay =
                    $"overlay=x='min(max(main_w*{xExpr}-overlay_w/2,0),max(0,main_w-overlay_w))':" +
                    $"y='min(max(main_h*{yExpr}-overlay_h/2,0),max(0,main_h-overlay_h))'";

                graph.Append($"[vref][wmrot]{overlay}[vwm];");
                currentLabel = "vwm";

                onLog($"[FILTER-WATERMARK] scale={scale:F2} (~{relativeWidth:P0} width), opacity={opacity:F2}, rotate={job.WatermarkRotation:F1}, x={x:P1}, y={y:P1}");
            }

            if (!string.IsNullOrWhiteSpace(postVideoFilters))
                graph.Append($"[{currentLabel}]{postVideoFilters}[vout]");
            else
                graph.Append($"[{currentLabel}]null[vout]");

            return graph.ToString();
        }

        private static string BuildFilmLeakFxFilter(RenderJob job, Action<string> onLog)
        {
            _ = job;
            onLog("[FILTER-FX] Procedural Leak 1 / Light Leak filter path is disabled. Asset-only overlay path is required.");
            throw new InvalidOperationException("Light leak asset missing.");
        }

        private static string BuildCinematicGlowFxFilter(RenderJob job, Action<string> onLog)
        {
            var invCulture = System.Globalization.CultureInfo.InvariantCulture;
            double intensity = Math.Clamp(job.FxIntensity / 100.0, 0.0, 1.0);

            string glowOpacity = (0.05 + (0.12 * intensity)).ToString("F3", invCulture);
            string blurStr = (0.95 + (1.15 * intensity)).ToString("F2", invCulture);
            string contrastStr = (1.05 + (0.06 * intensity)).ToString("F3", invCulture);
            string saturationStr = (0.98 + (0.06 * intensity)).ToString("F3", invCulture);
            string brightnessStr = (0.018 + (0.022 * intensity)).ToString("F3", invCulture);
            string noiseAmount = (4.0 + (6.0 * intensity)).ToString("F0", invCulture);

            string filter = string.Join(",", new[]
            {
                $"drawbox=x='iw*0.10 + sin(t*0.9)*iw*0.03':y='ih*0.12':w=iw*0.80:h=ih*0.76:color=white@{glowOpacity}:t=fill:enable='lt(mod(t,2.6),0.12)'",
                $"drawbox=x=0:y=0:w=iw:h=ih*0.08:color=white@{glowOpacity}:t=fill:enable='lt(mod(t,2.2),0.10)'",
                $"drawbox=x=0:y='ih*0.92':w=iw:h=ih*0.08:color=white@{glowOpacity}:t=fill:enable='lt(mod(t,2.2),0.10)'",
                $"noise=alls={noiseAmount}:allf=t+u",
                $"eq=contrast={contrastStr}:saturation={saturationStr}:brightness={brightnessStr}",
                $"boxblur=lr={blurStr}:lp=1",
                "unsharp=lx=3:ly=3:la=0.22"
            });

            onLog($"[FILTER-FX] Cinematic Glow intensity={job.FxIntensity:F0}%");
            onLog($"[FILTER-FX] Glow opacity={glowOpacity}, Blur={blurStr}");
            return filter;
        }

        private static string BuildHaloGlowFxFilter(RenderJob job, Action<string> onLog)
        {
            var invCulture = System.Globalization.CultureInfo.InvariantCulture;
            double intensity = Math.Clamp(job.FxIntensity / 100.0, 0.0, 1.0);

            string haloOpacity = (0.07 + (0.15 * intensity)).ToString("F3", invCulture);
            string edgeOpacity = (0.03 + (0.07 * intensity)).ToString("F3", invCulture);
            string blurStr = (0.90 + (0.85 * intensity)).ToString("F2", invCulture);
            string contrastStr = (1.03 + (0.05 * intensity)).ToString("F3", invCulture);
            string saturationStr = (0.94 + (0.05 * intensity)).ToString("F3", invCulture);
            string brightnessStr = (0.020 + (0.030 * intensity)).ToString("F3", invCulture);
            string noiseAmount = (4.0 + (4.0 * intensity)).ToString("F0", invCulture);

            string filter = string.Join(",", new[]
            {
                $"drawbox=x=0:y=0:w=iw:h=ih*0.12:color=white@{haloOpacity}:t=fill:enable='lt(mod(t,1.8),0.12)'",
                $"drawbox=x=0:y='ih*0.88':w=iw:h=ih*0.12:color=white@{haloOpacity}:t=fill:enable='lt(mod(t,1.8),0.12)'",
                $"drawbox=x=0:y=0:w=iw*0.12:h=ih:color=white@{edgeOpacity}:t=fill:enable='lt(mod(t,2.4),0.08)'",
                $"drawbox=x='iw*0.88':y=0:w=iw*0.12:h=ih:color=white@{edgeOpacity}:t=fill:enable='lt(mod(t,2.4),0.08)'",
                $"noise=alls={noiseAmount}:allf=t+u",
                $"eq=contrast={contrastStr}:saturation={saturationStr}:brightness={brightnessStr}",
                $"boxblur=lr={blurStr}:lp=1",
                "unsharp=lx=3:ly=3:la=0.18"
            });

            onLog($"[FILTER-FX] Halo Glow intensity={job.FxIntensity:F0}%");
            onLog($"[FILTER-FX] Halo opacity={haloOpacity}, Edge opacity={edgeOpacity}, Blur={blurStr}");
            return filter;
        }

        private static string BuildPrismLightFxFilter(RenderJob job, Action<string> onLog)
        {
            var invCulture = System.Globalization.CultureInfo.InvariantCulture;
            double intensity = Math.Clamp(job.FxIntensity / 100.0, 0.0, 1.0);

            string cyanOverlay = NormalizeFfmpegColor("#00E5FF");
            string magentaOverlay = NormalizeFfmpegColor("#FF5CCF");
            string goldOverlay = NormalizeFfmpegColor("#FFE16A");
            string cyanOpacity = (0.04 + (0.08 * intensity)).ToString("F3", invCulture);
            string magentaOpacity = (0.04 + (0.08 * intensity)).ToString("F3", invCulture);
            string goldOpacity = (0.03 + (0.06 * intensity)).ToString("F3", invCulture);
            string contrastStr = (1.06 + (0.06 * intensity)).ToString("F3", invCulture);
            string saturationStr = (1.06 + (0.14 * intensity)).ToString("F3", invCulture);
            string brightnessStr = (0.010 + (0.020 * intensity)).ToString("F3", invCulture);
            string blurStr = (0.95 + (0.55 * intensity)).ToString("F2", invCulture);
            string noiseAmount = (5.0 + (6.0 * intensity)).ToString("F0", invCulture);

            string filter = string.Join(",", new[]
            {
                $"drawbox=x='mod(t*iw*0.18, iw*0.82)':y=0:w=iw*0.14:h=ih:color={cyanOverlay}@{cyanOpacity}:t=fill:enable='lt(mod(t,2.2),0.12)'",
                $"drawbox=x='iw*0.72 - mod(t*iw*0.16, iw*0.60)':y=0:w=iw*0.18:h=ih:color={magentaOverlay}@{magentaOpacity}:t=fill:enable='lt(mod(t,2.0),0.12)'",
                $"drawbox=x='iw*0.28 + sin(t*1.2)*iw*0.08':y='ih*0.08':w=iw*0.12:h=ih*0.84:color={goldOverlay}@{goldOpacity}:t=fill:enable='lt(mod(t,1.6),0.10)'",
                $"noise=alls={noiseAmount}:allf=t+u",
                $"hue=h=10:s={1.04 + (0.10 * intensity):F3}",
                $"eq=contrast={contrastStr}:saturation={saturationStr}:brightness={brightnessStr}",
                $"boxblur=lr={blurStr}:lp=1",
                "unsharp=lx=3:ly=3:la=0.22"
            });

            onLog($"[FILTER-FX] Prism Light intensity={job.FxIntensity:F0}%");
            onLog($"[FILTER-FX] Cyan opacity={cyanOpacity}, Magenta opacity={magentaOpacity}");
            return filter;
        }

        private static string BuildEditorialBloomFxFilter(RenderJob job, Action<string> onLog)
        {
            var invCulture = System.Globalization.CultureInfo.InvariantCulture;
            double intensity = Math.Clamp(job.FxIntensity / 100.0, 0.0, 1.0);

            string bloomOpacity = (0.04 + (0.08 * intensity)).ToString("F3", invCulture);
            string blurStr = (1.00 + (0.75 * intensity)).ToString("F2", invCulture);
            string contrastStr = (1.03 + (0.04 * intensity)).ToString("F3", invCulture);
            string saturationStr = (0.92 + (0.05 * intensity)).ToString("F3", invCulture);
            string brightnessStr = (0.024 + (0.032 * intensity)).ToString("F3", invCulture);
            string noiseAmount = (3.0 + (4.0 * intensity)).ToString("F0", invCulture);

            string filter = string.Join(",", new[]
            {
                $"drawbox=x='iw*0.10':y='ih*0.12':w=iw*0.80:h=ih*0.66:color=white@{bloomOpacity}:t=fill:enable='lt(mod(t,2.8),0.12)'",
                $"drawbox=x=0:y=0:w=iw:h=ih*0.05:color=white@{bloomOpacity}:t=fill:enable='lt(mod(t,3.0),0.10)'",
                $"drawbox=x=0:y='ih*0.95':w=iw:h=ih*0.05:color=white@{bloomOpacity}:t=fill:enable='lt(mod(t,3.0),0.10)'",
                $"noise=alls={noiseAmount}:allf=t+u",
                $"eq=contrast={contrastStr}:saturation={saturationStr}:brightness={brightnessStr}",
                $"boxblur=lr={blurStr}:lp=1",
                "unsharp=lx=3:ly=3:la=0.14"
            });

            onLog($"[FILTER-FX] Editorial Bloom intensity={job.FxIntensity:F0}%");
            onLog($"[FILTER-FX] Bloom opacity={bloomOpacity}, Blur={blurStr}");
            return filter;
        }

        private static string BuildPolaroidScrapbookFxFilter(RenderJob job, Action<string> onLog)
        {
            string filter = BuildPolaroidScrapbookFxFilterCore(job, includeRgbBorder: false, includeDecorations: true);
            onLog($"[FILTER-FX] Polaroid Scrapbook overlay enabled (intensity={job.FxIntensity:F0}%)");
            return filter;
        }

        private static string BuildRgbPolaroidScrapbookFxFilter(RenderJob job, Action<string> onLog)
        {
            string filter = BuildPolaroidScrapbookFxFilterCore(job, includeRgbBorder: true, includeDecorations: true);
            onLog($"[FILTER-FX] RGB Polaroid base scrapbook frame enabled (LED overlay handled separately, intensity={job.FxIntensity:F0}%)");
            return filter;
        }

        private static string BuildPolaroidScrapbookFxFilterCore(RenderJob job, bool includeRgbBorder, bool includeDecorations)
        {
            var invCulture = System.Globalization.CultureInfo.InvariantCulture;
            int requestedWidth = job.TargetWidth > 0 ? job.TargetWidth : job.SourceWidth;
            int requestedHeight = job.TargetHeight > 0 ? job.TargetHeight : job.SourceHeight;
            var overlayProfile = ResolveOverlayCanvasProfile(job, requestedWidth, requestedHeight);
            int safeW = Math.Max(overlayProfile.CanvasWidth, 320);
            int safeH = Math.Max(overlayProfile.CanvasHeight, 180);
            OverlayContentLayout layout = overlayProfile.Layout;
            int contentX = (int)Math.Round(layout.ContentX);
            int contentY = (int)Math.Round(layout.ContentY);
            int contentW = Math.Max(64, (int)Math.Round(layout.ContentWidth));
            int contentH = Math.Max(64, (int)Math.Round(layout.ContentHeight));
            int minDim = Math.Max(180, Math.Min(contentW, contentH));
            int margin = (int)Math.Round(Math.Clamp(minDim * 0.034, 26.0, 42.0));
            int frameThickness = Math.Max(2, (int)Math.Round(OverlayLayoutHelper.ScaleFromHdReference(layout, 6.0, 2.0, 16.0)));
            int shadowThickness = Math.Max(1, (int)Math.Round(OverlayLayoutHelper.ScaleFromHdReference(layout, 4.0, 1.0, 12.0)));
            int tapeWidth = (int)Math.Round(Math.Clamp(minDim * 0.190, 56.0, 120.0));
            int tapeHeight = (int)Math.Round(Math.Clamp(minDim * 0.032, 18.0, 28.0));
            int stickyWidth = (int)Math.Round(Math.Clamp(minDim * 0.120, 46.0, 82.0));
            int stickyHeight = (int)Math.Round(Math.Clamp(minDim * 0.048, 22.0, 34.0));
            int shadowOffsetX = Math.Max(1, (int)Math.Round(OverlayLayoutHelper.ScaleFromHdReference(layout, 3.0, 1.0, 8.0)));
            int shadowOffsetY = shadowOffsetX;
            int frameX = contentX + margin;
            int frameY = contentY + margin;
            int frameW = Math.Max(24, contentW - (margin * 2));
            int frameH = Math.Max(24, contentH - (margin * 2));
            double intensity = Math.Clamp(job.FxIntensity / 100.0, 0.0, 1.0);

            string paperColor = NormalizeFfmpegColor("#FFF9F0");
            string tapeColor = NormalizeFfmpegColor("#E7D3A8");
            string stickyColor = NormalizeFfmpegColor("#D6F0E4");
            string shadowColor = NormalizeFfmpegColor("#000000");
            string paperOpacity = (0.86 + (0.06 * intensity)).ToString("F3", invCulture);
            string shadowOpacity = (0.08 + (0.03 * intensity)).ToString("F3", invCulture);
            string tapeOpacity = (0.58 + (0.10 * intensity)).ToString("F3", invCulture);
            string stickyOpacity = (0.48 + (0.08 * intensity)).ToString("F3", invCulture);

            List<string> filters = new();

            if (!includeRgbBorder)
            {
                filters.Add($"drawbox=x={frameX + shadowOffsetX}:y={frameY + shadowOffsetY}:w={frameW}:h={frameH}:color={shadowColor}@{shadowOpacity}:t={shadowThickness}");
                filters.Add($"drawbox=x={frameX}:y={frameY}:w={frameW}:h={frameH}:color={paperColor}@{paperOpacity}:t={frameThickness}");
            }

            if (includeDecorations)
            {
                filters.Add($"drawbox=x={contentX + (margin * 2)}:y={Math.Max(10, contentY + (margin / 3))}:w={tapeWidth}:h={tapeHeight}:color={tapeColor}@{tapeOpacity}:t=fill");
                filters.Add($"drawbox=x={contentX + contentW - stickyWidth - (margin * 2)}:y={contentY + contentH - stickyHeight - Math.Max(18, margin / 2)}:w={stickyWidth}:h={stickyHeight}:color={stickyColor}@{stickyOpacity}:t=fill");
            }

            return string.Join(",", filters);
        }

        private static string BuildCropFilter(RenderJob job, Action<string> onLog)
        {
            var crop = GetNormalizedCropPercentages(job);
            double x = crop.X;
            double y = crop.Y;
            double w = crop.W;
            double h = crop.H;

            string sx = x.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            string sy = y.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            string sw = w.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            string sh = h.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);

            string filter = $"crop='floor(iw*{sw}/2)*2':'floor(ih*{sh}/2)*2':'floor(iw*{sx}/2)*2':'floor(ih*{sy}/2)*2'";
            if (job.CropZoomPercent > 0.001)
                onLog($"[FILTER-CROP] zoom={job.CropZoomPercent:0.#}%, x={x:P1}, y={y:P1}, w={w:P1}, h={h:P1}, ratio=9:16");
            else
            {
                string ratioInfo = NormalizeCropAspectRatioName(job.CropAspectRatio);
                onLog($"[FILTER-CROP] x={x:P1}, y={y:P1}, w={w:P1}, h={h:P1}, ratio={ratioInfo}");
            }
            return filter;
        }

        private static string BuildVideoCodecArgs(string encoder, string preset)
        {
            if (encoder == "libx264")
                return $"-c:v {encoder} -preset {preset} -crf 16 ";

            if (encoder == "h264_nvenc")
            {
                // [FIXED] NVENC args that work reliably
                // - Use -cq instead of -rc vbr for quality control
                // - Don't use profile for NVENC (compatibility issues)
                return $"-c:v {encoder} -preset {preset} -cq 16 ";
            }

            if (encoder == "hevc_nvenc")
            {
                // Better compatibility for ultra-high resolutions (e.g., 8K output).
                return $"-c:v {encoder} -preset {preset} -cq 18 ";
            }

            if (encoder == "av1_nvenc")
            {
                return $"-c:v {encoder} -preset {preset} -cq 20 ";
            }

            if (encoder == "h264_amf")
                return $"-c:v {encoder} -quality {preset} ";

            if (encoder == "av1_amf")
                return $"-c:v {encoder} -quality {preset} ";

            if (encoder == "h264_qsv")
                return $"-c:v {encoder} -preset {preset} ";

            return $"-c:v {encoder} -preset {preset} ";
        }

        private static long GetMinRecommendedBitrate(int width, int height)
        {
            int safeW = Math.Max(0, width);
            int safeH = Math.Max(0, height);
            long pixels = (long)safeW * safeH;

            if (pixels >= 3840L * 2160L) return 25_000_000; // 4K+
            if (pixels >= 2560L * 1440L) return 12_000_000; // 1440p
            if (pixels >= 1920L * 1080L) return 6_000_000;  // 1080p
            if (pixels >= 1280L * 720L)  return 3_500_000;  // 720p
            return 2_000_000; // SD and below
        }

        private static async Task<(int ExitCode, string Stderr)> RunFfmpegCaptureAsync(string arguments, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(_ffmpegPath) || !File.Exists(_ffmpegPath))
                throw new FileNotFoundException("FFmpeg executable not found.");

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                CreateNoWindow = true
            };

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
                throw;
            }

            string stderr = await stderrTask;
            return (process.ExitCode, stderr);
        }

        private static string EscapeFfmpegDrawtextText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            return text
                .Replace("\\", "\\\\")
                .Replace("\r", "")
                .Replace("\n", "\\n")
                .Replace("\"", "'")
                .Replace(":", "\\:")
                .Replace("'", "\\'")
                .Replace("%", "\\%")
                .Replace(",", "\\,")
                .Replace("[", "\\[")
                .Replace("]", "\\]");
        }

        private static string NormalizeFfmpegColor(string color)
        {
            if (string.IsNullOrWhiteSpace(color))
                return "white";

            string trimmed = color.Trim();
            int alphaIndex = trimmed.IndexOf('@');
            string baseColor = alphaIndex >= 0 ? trimmed[..alphaIndex] : trimmed;
            string alpha = alphaIndex >= 0 ? trimmed[alphaIndex..] : string.Empty;

            if (baseColor.StartsWith("#", StringComparison.Ordinal))
                baseColor = "0x" + baseColor[1..];

            return baseColor + alpha;
        }

        private static string BuildTemplateDrawtext(string text, string xExpr, string yExpr, int fontSize, string fontColor)
        {
            string fontFile = ResolveFfmpegFontFile();
            var parts = new List<string>();
            string normalizedXExpr = NormalizeDrawtextCoordinateExpression(xExpr);
            string normalizedYExpr = NormalizeDrawtextCoordinateExpression(yExpr);

            if (!string.IsNullOrWhiteSpace(fontFile))
                parts.Add($"fontfile='{fontFile}'");
            else
                parts.Add("font='Arial'");

            parts.Add($"text='{EscapeFfmpegDrawtextText(text)}'");
            parts.Add($"x={normalizedXExpr}");
            parts.Add($"y={normalizedYExpr}");
            parts.Add($"fontsize={fontSize}");
            parts.Add($"fontcolor={NormalizeFfmpegColor(fontColor)}");

            return "drawtext=" + string.Join(":", parts);
        }

        private static string BuildTypewriterTextOverlay(
            string fullText, string xExpr, string yExpr,
            int fontSize, string fontColor, string fontStyle, bool addQuotes,
            double charsPerSecond, bool disableScroll, bool enableGlow, string? glowColor,
            string textAlign, string textPos, Action<string> onLog)
        {
            if (string.IsNullOrWhiteSpace(fullText)) return string.Empty;

            var inv = System.Globalization.CultureInfo.InvariantCulture;

            // Apply automatic aesthetic quotation marks if requested and not already present
            string formattedText = fullText.Trim();
            if (addQuotes && !formattedText.StartsWith("\u201C") && !formattedText.StartsWith("\""))
            {
                formattedText = $"\u201C {formattedText} \u201D";
            }

            // Select Font File based on requested style and character set
            (string fontPart, string fontFamilyName) = ResolveFontForText(fontStyle, formattedText, onLog);
            string normalizedColor = NormalizeFfmpegColor(fontColor);
            
            // Resolve Glowing Neon Aura Color if requested
            string normGlowColor = "cyan";
            if (enableGlow)
            {
                string gStr = (glowColor ?? "cyan").ToLowerInvariant();
                if (gStr.Contains("gold") || gStr.Contains("yellow")) normGlowColor = "gold";
                else if (gStr.Contains("pink") || gStr.Contains("magenta")) normGlowColor = "magenta";
                else if (gStr.Contains("white")) normGlowColor = "white";
                else if (gStr.Contains("green") || gStr.Contains("lime") || gStr.Contains("matrix")) normGlowColor = "lime";
                else if (gStr.Contains("red") || gStr.Contains("flame") || gStr.Contains("fire")) normGlowColor = "red";
                else normGlowColor = "cyan";
            }

            // Normalize newlines
            string normalizedText = formattedText.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();
            int totalChars = normalizedText.Length;
            if (totalChars == 0) return string.Empty;

            string[] lines = normalizedText.Split('\n');
            var filters = new List<string>();

            // Calculate base Y and Max Width for the entire block so we can vertically and horizontally position it
            string blockY = NormalizeDrawtextCoordinateExpression(yExpr);
            double maxWpfWidth = 0;
            try
            {
                var typeface = new System.Windows.Media.Typeface(fontFamilyName);
                var ft = new System.Windows.Media.FormattedText(
                    normalizedText,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    System.Windows.Media.Brushes.White,
                    1.0); // 1.0 pixels per dip

                blockY = blockY.Replace("text_h", ft.Height.ToString("F1", inv));

                // Measure each line to find max width
                foreach (string lineText in lines)
                {
                    if (lineText.Length == 0) continue;
                    var lineFt = new System.Windows.Media.FormattedText(
                        lineText,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight,
                        typeface,
                        fontSize,
                        System.Windows.Media.Brushes.White,
                        1.0);
                    if (lineFt.Width > maxWpfWidth) maxWpfWidth = lineFt.Width;
                }
            }
            catch (Exception ex)
            {
                double estH = lines.Length * fontSize * 1.2;
                blockY = blockY.Replace("text_h", estH.ToString("F1", inv));
                maxWpfWidth = lines.Max(l => l.Length) * fontSize * 0.55;
                onLog($"[FILTER-TEXT-WARN] Could not measure text block accurately: {ex.Message}");
            }

            // Determine if custom coords are used
            bool isCustomCoords = textPos.Contains("custom");

            // Option to keep text static (disable upward scrolling animation)
            if (disableScroll)
            {
                blockY = $"({blockY})";
            }
            else
            {
                blockY = $"({blockY}) + 15 - (t*2.5)";
            }

            int charsProcessedSoFar = 0;
            double currentYOffset = 0;
            double revealEnd = (double)totalChars / charsPerSecond + 0.3; // shadow appears 300ms after last char

            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string lineText = lines[lineIndex];
                int lineLen = lineText.Length;

                if (lineLen == 0)
                {
                    charsProcessedSoFar += 1; // count the newline char for timing
                    currentYOffset += fontSize * 1.2;
                    continue;
                }

                string lineX = "";
                if (isCustomCoords)
                {
                    // Standard video editor behavior: X is the anchor point. 
                    // Alignment dictates text placement relative to the anchor point.
                    if (textAlign == "left") lineX = xExpr;
                    else if (textAlign == "right") lineX = $"({xExpr}) - text_w";
                    else lineX = $"({xExpr}) - text_w/2"; // Center
                }
                else
                {
                    // For presets (Bottom Left, etc), we must anchor the block based on the side.
                    bool isLeftAnchored = textPos.Contains("left");
                    bool isRightAnchored = textPos.Contains("right");
                    
                    string blockLeftX = "";
                    if (isLeftAnchored) blockLeftX = xExpr;
                    else if (isRightAnchored) blockLeftX = $"({xExpr}) - {maxWpfWidth.ToString("F1", inv)}";
                    else blockLeftX = $"({xExpr}) - {maxWpfWidth.ToString("F1", inv)}/2";

                    if (textAlign == "left") lineX = blockLeftX;
                    else if (textAlign == "right") lineX = $"{blockLeftX} + {maxWpfWidth.ToString("F1", inv)} - text_w";
                    else lineX = $"{blockLeftX} + {maxWpfWidth.ToString("F1", inv)}/2 - text_w/2";
                }
                
                lineX = NormalizeDrawtextCoordinateExpression(lineX);
                double lineHeight = fontSize * 1.2;

                try
                {
                    var typeface = new System.Windows.Media.Typeface(fontFamilyName);
                    var lineFt = new System.Windows.Media.FormattedText(
                        lineText,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight,
                        typeface,
                        fontSize,
                        System.Windows.Media.Brushes.White,
                        1.0);

                    // We intentionally DO NOT replace "text_w" in lineX here.
                    // Leaving "text_w" in the expression allows FFmpeg to calculate the exact width 
                    // of the string based on its actual font file (like custom Calligraphy fonts)
                    // which guarantees 100% perfect Left/Center/Right alignment!
                    lineHeight = lineFt.Height;
                }
                catch
                {
                    // Fallback line height if WPF measurement fails
                    lineHeight = fontSize * 1.2;
                }

                string lineY = $"({blockY}) + {currentYOffset.ToString("F1", inv)}";
                
                string fullLineEscaped = EscapeFfmpegDrawtextText(lineText);

                // Add Glowing Aura layers if requested (Distinct, smooth breathing glow pulse)
                if (enableGlow)
                {
                    int outerGlowW = Math.Clamp((int)Math.Round(fontSize * 0.28), 7, 12);
                    int midGlowW = Math.Clamp((int)Math.Round(fontSize * 0.15), 4, 7);
                    int innerStrokeW = Math.Clamp((int)Math.Round(fontSize * 0.06), 2, 3);

                    // Layer 1: Outer soft diffuse halo (static 0.60 opacity)
                    var outerGlowParts = new List<string>
                    {
                        fontPart,
                        $"text='{fullLineEscaped}'",
                        $"x={lineX}",
                        $"y={lineY}",
                        $"fontsize={fontSize}",
                        $"fontcolor={normGlowColor}",
                        $"bordercolor={normGlowColor}",
                        $"borderw={outerGlowW}",
                        "alpha=0.6"
                    };
                    filters.Add("drawtext=" + string.Join(":", outerGlowParts));

                    // Layer 2: Mid core glow (static 0.85 opacity)
                    var midGlowParts = new List<string>
                    {
                        fontPart,
                        $"text='{fullLineEscaped}'",
                        $"x={lineX}",
                        $"y={lineY}",
                        $"fontsize={fontSize}",
                        $"fontcolor={normGlowColor}",
                        $"bordercolor={normGlowColor}",
                        $"borderw={midGlowW}",
                        "alpha=0.85"
                    };
                    filters.Add("drawtext=" + string.Join(":", midGlowParts));

                    // Layer 3: Inner tight crisp colored stroke around white text
                    var innerStrokeParts = new List<string>
                    {
                        fontPart,
                        $"text='{fullLineEscaped}'",
                        $"x={lineX}",
                        $"y={lineY}",
                        $"fontsize={fontSize}",
                        "fontcolor=white",
                        $"bordercolor={normGlowColor}",
                        $"borderw={innerStrokeW}"
                    };
                    filters.Add("drawtext=" + string.Join(":", innerStrokeParts));
                }
                
                // Add the main text layer for the full line
                var textParts = new List<string>
                {
                    fontPart,
                    $"text='{fullLineEscaped}'",
                    $"x={lineX}",
                    $"y={lineY}",
                    $"fontsize={fontSize}",
                    $"fontcolor={normalizedColor}",
                    "shadowcolor=black@0.4",
                    "shadowx=2",
                    "shadowy=2"
                };
                filters.Add("drawtext=" + string.Join(":", textParts));

                charsProcessedSoFar += lineLen + 1; // +1 for the newline character
                currentYOffset += lineHeight;
            }

            double totalDuration = revealEnd;
            onLog($"[FILTER-TEXT-TYPEWRITER] Multiline Typewriter: {lines.Length} lines, {totalChars} chars, font={fontFamilyName}, ~{totalDuration:F1}s reveal time");

            return string.Join(",", filters);
        }

        private static string BuildAnimatedTextOverlay(
            string fullText, string xExpr, string yExpr,
            int fontSize, string fontColor, string fontStyle, bool addQuotes,
            string animation, string textAlign, string textPos,
            double effectDurationSeconds, Action<string> onLog)
        {
            if (string.IsNullOrWhiteSpace(fullText)) return string.Empty;

            string anim = animation ?? string.Empty;
            bool isSpotify = anim.Contains("Spotify", StringComparison.OrdinalIgnoreCase);
            bool isPop = anim.Contains("Pop", StringComparison.OrdinalIgnoreCase);
            bool isDrift = anim.Contains("Drift", StringComparison.OrdinalIgnoreCase);
            bool isGlowPulse = anim.Contains("Glow", StringComparison.OrdinalIgnoreCase);
            bool isKaraoke = anim.Contains("Karaoke", StringComparison.OrdinalIgnoreCase);
            bool isTypewriter = anim.Contains("Typewriter", StringComparison.OrdinalIgnoreCase);
            bool isBlurReveal = anim.Contains("Blur Reveal", StringComparison.OrdinalIgnoreCase);
            // Set when a reveal mode is degraded because the text is too long to
            // spend the filter budget on; falls back to revealing whole lines.
            bool isLineReveal = false;
            bool isStatic = !isSpotify && !isPop && !isDrift && !isGlowPulse
                && !isKaraoke && !isTypewriter && !isBlurReveal;

            // ── Text preparation (mirrors the legacy typewriter overlay) ──
            string formattedText = fullText.Trim();
            if (addQuotes && !formattedText.StartsWith("\u201C") && !formattedText.StartsWith("\""))
                formattedText = $"\u201C {formattedText} \u201D";

            var (fontPart, fontFamilyName) = ResolveFontForText(fontStyle, formattedText, onLog);
            string normalizedColor = NormalizeFfmpegColor(fontColor);

            string normalizedText = formattedText.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();
            var activeLines = normalizedText
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();
            if (activeLines.Count == 0) return string.Empty;

            // ── Measure every line once (used for anchors and per-char offsets) ──
            var lineWidths = new List<double>(activeLines.Count);
            var lineHeights = new List<double>(activeLines.Count);
            double maxWpfWidth = 0;
            for (int i = 0; i < activeLines.Count; i++)
            {
                double width;
                double height;
                try
                {
                    var measured = new System.Windows.Media.FormattedText(
                        activeLines[i],
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight,
                        new System.Windows.Media.Typeface(fontFamilyName),
                        fontSize,
                        System.Windows.Media.Brushes.White,
                        1.0);
                    width = measured.Width;
                    height = measured.Height;
                }
                catch (Exception ex)
                {
                    width = activeLines[i].Length * fontSize * 0.55;
                    height = fontSize * 1.2;
                    onLog($"[FILTER-TEXT-ANIM-WARN] Could not measure line {i} accurately: {ex.Message}");
                }
                lineWidths.Add(width);
                lineHeights.Add(height);
                if (width > maxWpfWidth) maxWpfWidth = width;
            }

            // ── Horizontal anchor ──
            bool isCustomCoords = textPos.Contains("custom", StringComparison.OrdinalIgnoreCase);
            bool isLeftAnchored = textPos.Contains("left");
            bool isRightAnchored = textPos.Contains("right");
            string normX = NormalizeDrawtextCoordinateExpression(xExpr);
            string maxW = FfmpegDouble(maxWpfWidth, 1);

            string blockLeftX;
            if (isLeftAnchored) blockLeftX = normX;
            else if (isRightAnchored) blockLeftX = $"({normX}) - {maxW}";
            else blockLeftX = $"({normX}) - {maxW}/2";

            // Horizontal anchor for a whole-line layer. text_w is deliberately
            // left inside the expression so FFmpeg measures the rendered string
            // with the real font file, which keeps Left/Center/Right exact even
            // for custom fonts such as the bundled calligraphy face.
            string LineXTextW(int i)
            {
                if (isCustomCoords)
                {
                    if (textAlign == "left") return normX;
                    if (textAlign == "right") return $"({normX}) - text_w";
                    return $"({normX}) - text_w/2";
                }
                if (textAlign == "left") return blockLeftX;
                if (textAlign == "right") return $"{blockLeftX} + {maxW} - text_w";
                return $"{blockLeftX} + {maxW}/2 - text_w/2";
            }

            string blockY = NormalizeDrawtextCoordinateExpression(yExpr);

            // ── Timing ──
            int lineCount = activeLines.Count;
            double effectDuration = Math.Clamp(
                effectDurationSeconds <= 0 ? 5.0 : effectDurationSeconds,
                TextOverlayMinEffectDuration,
                TextOverlayMaxEffectDuration);
            double slot = effectDuration / lineCount;
            double fadeLength = Math.Min(0.3, slot * 0.4);
            string fade = FfmpegDouble(Math.Max(0.05, fadeLength), 3);

            var filters = new List<string>();
            var yOffsets = new List<double>(lineCount);
            double runningOffset = 0;
            for (int i = 0; i < lineCount; i++)
            {
                yOffsets.Add(runningOffset);
                runningOffset += lineHeights[i];
            }

            // ───────────────────────────────────────────────────────────
            // Helpers for composing drawtext layers
            //
            // drawtext option support in the bundled FFmpeg build is uneven, so
            // this deliberately only animates the options that were verified to
            // re-evaluate per frame:
            //   alpha  -> full expression support (if/lt/between/min/max/sin)
            //   x, y   -> full expression support
            // and keeps every other option a plain literal:
            //   fontsize crashes the process (0xC0000005) when its value varies,
            //   borderw / shadowx / line_spacing reject `t` outright.
            // Scale and outline effects are therefore expressed as stacked
            // layers with different literal font sizes / border widths whose
            // alpha switches on the timeline.
            // ───────────────────────────────────────────────────────────
            void AddTextLayer(string lineX, string lineY, string text, int fontPx,
                string? alphaExpr = null, int? literalBorderW = null,
                string? literalBorderColor = null, bool withShadow = true)
            {
                var parts = new List<string>
                {
                    fontPart,
                    $"text='{EscapeFfmpegDrawtextText(text)}'",
                    $"x='{lineX}'",
                    $"y='{lineY}'",
                    $"fontsize={fontPx}",
                    $"fontcolor={normalizedColor}"
                };
                if (withShadow)
                {
                    parts.Add("shadowcolor=black@0.4");
                    parts.Add("shadowx=2");
                    parts.Add("shadowy=2");
                }
                if (!string.IsNullOrWhiteSpace(literalBorderColor))
                {
                    parts.Add($"bordercolor={literalBorderColor}");
                    parts.Add($"borderw={literalBorderW ?? 2}");
                }
                if (!string.IsNullOrWhiteSpace(alphaExpr))
                    parts.Add($"alpha='{alphaExpr}'");

                filters.Add("drawtext=" + string.Join(":", parts));
            }

            // Caption Sync equivalent: the 300ms \fad(300,0) entrance ramp. The
            // text then holds at full opacity for the rest of the video.
            string SegmentFade(string slotStart) =>
                $"min(1,max(0,(t-{slotStart})/{fade}))";

            // ═════════════════════════════════════════════════════════════
            // BLOCK MODES — every line is visible, each line animates inside
            // its own slot: Pop & Bounce / Smooth Drift / Glow Pulse / Static
            // ═════════════════════════════════════════════════════════════
            if (isStatic || isPop || isDrift || isGlowPulse)
            {
                for (int i = 0; i < lineCount; i++)
                {
                    string lineX = LineXTextW(i);
                    string lineY = $"({blockY}) + {FfmpegDouble(yOffsets[i], 1)}";
                    string slotStart = FfmpegDouble(i * slot, 3);
                    // Elapsed time inside this line's own slot
                    string dt = $"(t-{slotStart})";

                    if (isPop)
                    {
                        // ASS reference: \fscx0 → 120 over 200ms → 100 at 350ms.
                        // fontsize cannot be animated on this build, so the
                        // overshoot is reproduced with three stacked literal
                        // sizes whose alpha hands over on the timeline.
                        string t0 = slotStart;
                        string t1 = FfmpegDouble(i * slot + 0.20, 3);
                        string t2 = FfmpegDouble(i * slot + 0.35, 3);
                        double quickIn = Math.Min(0.08, fadeLength * 0.5);
                        double quickOut = Math.Min(0.15, fadeLength * 0.5);

                        // Three stacked literal sizes cross-fade along the
                        // 0 -> 120% -> 100% envelope of the ASS \fscx/\t original.
                        // 85% grows out of nothing and retires before the end,
                        // 120% is the overshoot, 100% is the resting size.
                        AddTextLayer(lineX, lineY, activeLines[i], (int)Math.Round(fontSize * 0.85),
                            alphaExpr: $"min(1,max(0,(t-{t0})/{fade}))*min(1,max(0,({t2}-t)/{fade}))",
                            withShadow: false);
                        AddTextLayer(lineX, lineY, activeLines[i], (int)Math.Round(fontSize * 1.20),
                            alphaExpr: $"min(1,max(0,(t-{t0})/{quickIn}))*min(1,max(0,({t2}-t)/{quickOut}))",
                            withShadow: false);
                        AddTextLayer(lineX, lineY, activeLines[i], fontSize,
                            alphaExpr: $"min(1,max(0,(t-{t1})/{fade}))",
                            withShadow: false);
                    }
                    else if (isDrift)
                    {
                        // Drifts 20px up to 20px over the slot, then holds (ASS \move)
                        string progress = $"min(1,max(0,{dt}/{FfmpegDouble(slot, 3)}))";
                        lineY = $"({lineY}) + 20 - 40*({progress})";
                        AddTextLayer(lineX, lineY, activeLines[i], fontSize, alphaExpr: SegmentFade(slotStart));
                    }
                    else if (isGlowPulse)
                    {
                        // Breathing aura. borderw cannot be animated either, so
                        // a wide static outline layer pulses through alpha while
                        // the crisp text sits on top.
                        string pulse = $"0.35+0.65*abs(sin(PI*{dt}/1.6))";
                        AddTextLayer(lineX, lineY, activeLines[i], fontSize,
                            alphaExpr: $"({pulse})*{SegmentFade(slotStart)}",
                            literalBorderW: Math.Max(3, (int)Math.Round(fontSize * 0.16)),
                            literalBorderColor: normalizedColor,
                            withShadow: false);
                        AddTextLayer(lineX, lineY, activeLines[i], fontSize,
                            alphaExpr: SegmentFade(slotStart));
                    }
                    else
                    {
                        AddTextLayer(lineX, lineY, activeLines[i], fontSize);
                    }
                }

                onLog($"[FILTER-TEXT-ANIM] {anim} | {lineCount} line(s) over {effectDuration:F1}s, font={fontFamilyName}, {filters.Count} layer(s)");
                return string.Join(",", filters);
            }

            // ═════════════════════════════════════════════════════════════
            // SPOTIFY SCROLL — lines slide up through the anchor point
            // ═════════════════════════════════════════════════════════════
            if (isSpotify)
            {
                string slotStr = FfmpegDouble(slot, 3);
                for (int i = 0; i < lineCount; i++)
                {
                    string lineX = LineXTextW(i);
                    double lineH = lineHeights[i];
                    string lineHStr = FfmpegDouble(Math.Max(8.0, lineH), 1);
                    bool isLast = i == lineCount - 1;

                    // line i sits at anchor + (i - t/slot) * lineHeight
                    string rise = isLast
                        ? $"min(0,{i}-t/{slotStr})"
                        : $"({i}-t/{slotStr})";
                    string lineY = $"({blockY}) + {rise}*{lineHStr}";

                    // The last line stays on screen once the scroll finishes.
                    // `visible` gates the whole layer, `centered` marks the line
                    // currently sitting on the anchor.
                    string centered = $"between(t,{FfmpegDouble((i - 0.5) * slot, 3)},{FfmpegDouble((i + 0.5) * slot, 3)})";
                    string visible = isLast
                        ? $"gte(t,{FfmpegDouble((i - 1) * slot, 3)})"
                        : $"between(t,{FfmpegDouble((i - 1) * slot, 3)},{FfmpegDouble((i + 2) * slot, 3)})";

                    // ASS reference: active line is \fscx115 and blurred lines
                    // are \fscx85 \blur2. Since only alpha animates here, the
                    // active line is drawn larger and the passing lines smaller
                    // and dimmed, with a soft outline standing in for the blur.
                    AddTextLayer(lineX, lineY, activeLines[i], (int)Math.Round(fontSize * 1.12),
                        alphaExpr: $"if({visible},if({centered},1,0),0)",
                        withShadow: false);
                    AddTextLayer(lineX, lineY, activeLines[i], (int)Math.Round(fontSize * 0.85),
                        alphaExpr: $"if({visible},if({centered},0,0.4),0)",
                        literalBorderW: 3, literalBorderColor: normalizedColor,
                        withShadow: false);
                }

                onLog($"[FILTER-TEXT-ANIM] {anim} | {lineCount} line(s) scroll over {effectDuration:F1}s (slot {slot:F2}s), font={fontFamilyName}");
                return string.Join(",", filters);
            }

            // ═════════════════════════════════════════════════════════════
            // PROGRESSIVE REVEAL MODES — Karaoke / Typewriter / Blur Reveal
            // Each revealed unit gets its own drawtext carrying the visible
            // prefix of the line, so the reveal walks the line over its slot.
            //
            // Budget: the whole FFmpeg command line is capped at 32767 chars and
            // the text overlay shares it with the rest of the filter graph, so
            // this stage may only spend a fraction of it. Each reveal layer
            // repeats the full visible prefix, so cost grows with the square of
            // the text length; the two word/char budgets below are sized from
            // measured worst cases (Karaoke ~600 chars/layer, Typewriter
            // ~400 chars/layer, Blur Reveal ~800 chars/layer because it stacks
            // an extra outline layer per unit).
            // ═════════════════════════════════════════════════════════════
            int totalChars = activeLines.Sum(l => l.Length);
            int totalWords = activeLines.Sum(l => l.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length);

            // Degrade progressively so a long text never silently loses its effect.
            if (isBlurReveal && totalChars > TextOverlayBlurRevealCharBudget)
            {
                onLog($"[FILTER-TEXT-ANIM-WARN] Blur Reveal needs {totalChars} chars but the filter budget allows {TextOverlayBlurRevealCharBudget}. Using Typewriter reveal instead.");
                isBlurReveal = false;
                isTypewriter = true;
            }
            if (isTypewriter && totalChars > TextOverlayTypewriterCharBudget)
            {
                onLog($"[FILTER-TEXT-ANIM-WARN] Typewriter needs {totalChars} chars but the filter budget allows {TextOverlayTypewriterCharBudget}. Using word-by-word reveal instead.");
                isTypewriter = false;
                isKaraoke = true;
            }
            if (isKaraoke && totalWords > TextOverlayKaraokeWordBudget)
            {
                onLog($"[FILTER-TEXT-ANIM-WARN] Karaoke needs {totalWords} words but the filter budget allows {TextOverlayKaraokeWordBudget}. Revealing line by line instead.");
                isKaraoke = false;
                isLineReveal = true;
            }
            if (isLineReveal)
            {
                for (int i = 0; i < lineCount; i++)
                {
                    AddTextLayer(LineXTextW(i), $"({blockY}) + {FfmpegDouble(yOffsets[i], 1)}", activeLines[i], fontSize,
                        alphaExpr: SegmentFade(FfmpegDouble(i * slot, 3)));
                }
                onLog($"[FILTER-TEXT-ANIM] Line-by-line reveal | {lineCount} line(s) over {effectDuration:F1}s, font={fontFamilyName}");
                return string.Join(",", filters);
            }

            int revealedUnits = 0;
            for (int i = 0; i < lineCount; i++)
            {
                string line = activeLines[i];
                string lineX = LineXTextW(i);
                string lineY = $"({blockY}) + {FfmpegDouble(yOffsets[i], 1)}";
                string slotStart = FfmpegDouble(i * slot, 3);

                if (isKaraoke)
                {
                    // Word by word: each word fades in at its own timestamp.
                    var words = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    double wordStep = words.Length > 0 ? slot / words.Length : slot;
                    var sb = new System.Text.StringBuilder();
                    for (int w = 0; w < words.Length; w++)
                    {
                        if (w > 0) sb.Append(' ');
                        sb.Append(words[w]);
                        string unitStart = FfmpegDouble(i * slot + (w * wordStep), 3);
                        AddTextLayer(lineX, lineY, sb.ToString(), fontSize,
                            alphaExpr: $"min(1,max(0,(t-{unitStart})/{fade}))",
                            withShadow: false);
                        revealedUnits++;
                    }
                }
                else
                {
                    // Per character: Typewriter (instant) or Blur Reveal (halo decays).
                    var runes = line.EnumerateRunes().ToList();
                    double charStep = runes.Count > 0 ? slot / runes.Count : slot;
                    var prefix = new System.Text.StringBuilder();
                    for (int c = 0; c < runes.Count; c++)
                    {
                        prefix.Append(runes[c]);
                        string unitStart = FfmpegDouble(i * slot + (c * charStep), 3);
                        string unitAlpha = $"min(1,max(0,(t-{unitStart})/{fade}))";

                        if (isTypewriter)
                        {
                            AddTextLayer(lineX, lineY, prefix.ToString(), fontSize,
                                alphaExpr: unitAlpha, withShadow: false);
                        }
                        else
                        {
                            // Blur Reveal: the ASS effect ramps \blur15 → \blur0 on
                            // the newly revealed rune. borderw is static on this
                            // build, so the soft halo is a wide outline layer whose
                            // alpha rises with the rune and then fades, reproducing
                            // the same soft-to-crisp ramp.
                            double haloLength = Math.Max(0.12, slot * 0.5);
                            AddTextLayer(lineX, lineY, prefix.ToString(), fontSize,
                                alphaExpr: unitAlpha, withShadow: false);
                            AddTextLayer(lineX, lineY, prefix.ToString(), fontSize,
                                alphaExpr: $"({unitAlpha})*if(lt(t-{unitStart},{FfmpegDouble(haloLength, 3)}),1,0)",
                                literalBorderW: Math.Max(4, (int)Math.Round(fontSize * 0.22)),
                                literalBorderColor: normalizedColor,
                                withShadow: false);
                        }
                        revealedUnits++;
                    }
                }
            }

            onLog($"[FILTER-TEXT-ANIM] {anim} | {revealedUnits} reveal unit(s) across {lineCount} line(s) over {effectDuration:F1}s, font={fontFamilyName}");
            return string.Join(",", filters);
        }
        private static string BuildTemplateMotionFilter(string templateName, int targetWidth, int targetHeight, double segmentDurationSec, bool isImageInput = false, string? musicSyncMode = null, double fxIntensity = 50.0, string? magazineCoverTitle = null, string? magazineCoverSubtitle = null)
        {
            string normalized = NormalizeTemplateNameForEngine(templateName);
            if (normalized.Equals("None", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string d = FfmpegDouble(Math.Max(0.5, segmentDurationSec), 4);
            string syncMode = NormalizeMusicSyncMode(musicSyncMode);
            bool beatMode = normalized.Equals("Beat Photo Dump", StringComparison.OrdinalIgnoreCase) &&
                (syncMode.Equals("beat", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(musicSyncMode));
            int upW = Math.Max(targetWidth + 2, (int)Math.Ceiling(targetWidth * 1.12));
            int upH = Math.Max(targetHeight + 2, (int)Math.Ceiling(targetHeight * 1.12));
            if ((upW & 1) != 0) upW++;
            if ((upH & 1) != 0) upH++;

            string tw = targetWidth.ToString(inv);
            string th = targetHeight.ToString(inv);
            int safeW = Math.Max(320, targetWidth);
            int safeH = Math.Max(180, targetHeight);
            int fontTiny = Math.Max(14, safeH / 48);
            int fontSmall = Math.Max(18, safeH / 38);
            int fontMed = Math.Max(28, safeH / 24);
            int fontLarge = Math.Max(44, safeH / 14);
            int border = Math.Max(8, safeW / 80);
            int margin = Math.Max(24, safeW / 32);
            int focusT = Math.Max(3, safeW / 420);
            int safeOuterW = Math.Max(32, safeW - (margin * 2));
            int safeOuterH = Math.Max(32, safeH - (margin * 2));
            int safeInnerMargin = margin + border;
            int safeInnerW = Math.Max(24, safeW - (safeInnerMargin * 2));
            int safeInnerH = Math.Max(24, safeH - (safeInnerMargin * 2));
            double fxStrength = Math.Clamp(fxIntensity / 100.0, 0.0, 1.0);
            string magazineTitle = string.IsNullOrWhiteSpace(magazineCoverTitle) ? "TITAN" : magazineCoverTitle.Trim();
            string magazineSubtitle = string.IsNullOrWhiteSpace(magazineCoverSubtitle) ? "COVER STORY" : magazineCoverSubtitle.Trim();
            string warmPaper = NormalizeFfmpegColor("#F4E9D8");
            string scrapbookTape = NormalizeFfmpegColor("#E7D3A8@0.72");
            string scrapbookSticky = NormalizeFfmpegColor("#CFE8DF@0.72");
            string scrapbookInk = NormalizeFfmpegColor("#4C3828@0.92");
            string scrapbookSubInk = NormalizeFfmpegColor("#6D5646@0.82");
            string beatTopGlow = NormalizeFfmpegColor("#00E5FF@0.70");
            string beatBottomGlow = NormalizeFfmpegColor("#FF3AA8@0.70");
            const double imageZoomLoopSeconds = 8.0;
            const double imageZoomScaleRange = 0.10;
            string imageZoomLoop = FfmpegDouble(imageZoomLoopSeconds, 4);
            string imageZoomRange = FfmpegDouble(imageZoomScaleRange, 4);
            string imageZoomProgressExpr = $"(mod(t\\,{imageZoomLoop})/{imageZoomLoop})";

            return normalized switch
            {
                "Smooth Zoom In" =>
                    (isImageInput
                        ? $"scale=w='ceil(({tw})*(1+{imageZoomRange}*{imageZoomProgressExpr})/2)*2':h='ceil(({th})*(1+{imageZoomRange}*{imageZoomProgressExpr})/2)*2':eval=frame:flags=lanczos,"
                        : $"scale=w='ceil(({tw})*(1+0.10*(t/{d}))/2)*2':h='ceil(({th})*(1+0.10*(t/{d}))/2)*2':eval=frame:flags=lanczos,") +
                    $"crop={targetWidth}:{targetHeight}:x='(in_w-out_w)/2':y='(in_h-out_h)/2'",

                "Smooth Zoom Out" =>
                    (isImageInput
                        ? $"scale=w='ceil(({tw})*(1+{imageZoomRange}*(1-{imageZoomProgressExpr}))/2)*2':h='ceil(({th})*(1+{imageZoomRange}*(1-{imageZoomProgressExpr}))/2)*2':eval=frame:flags=lanczos,"
                        : $"scale=w='ceil(({tw})*(1.10-0.10*(t/{d}))/2)*2':h='ceil(({th})*(1.10-0.10*(t/{d}))/2)*2':eval=frame:flags=lanczos,") +
                    $"crop={targetWidth}:{targetHeight}:x='(in_w-out_w)/2':y='(in_h-out_h)/2'",

                "Pan Left" =>
                    $"scale={upW}:{upH}:flags=lanczos," +
                    $"crop={targetWidth}:{targetHeight}:x='(in_w-out_w)*(1-(t/{d}))':y='(in_h-out_h)/2'",

                "Pan Right" =>
                    $"scale={upW}:{upH}:flags=lanczos," +
                    $"crop={targetWidth}:{targetHeight}:x='(in_w-out_w)*(t/{d})':y='(in_h-out_h)/2'",

                "Velocity Punch" =>
                    $"scale=w='ceil(({tw})*max(1.0,1+0.10*exp(-2*t/{d})*(0.5+0.5*sin(14*t))+0.03*(t/{d}))/2)*2':h='ceil(({th})*max(1.0,1+0.10*exp(-2*t/{d})*(0.5+0.5*sin(14*t))+0.03*(t/{d}))/2)*2':eval=frame:flags=lanczos," +
                    $"crop={targetWidth}:{targetHeight}:x='(in_w-out_w)/2':y='(in_h-out_h)/2'",

                "Cinematic Fade Zoom" =>
                    $"scale=w='ceil(({tw})*(1+0.08*(t/{d}))/2)*2':h='ceil(({th})*(1+0.08*(t/{d}))/2)*2':eval=frame:flags=lanczos," +
                    $"crop={targetWidth}:{targetHeight}:x='(in_w-out_w)/2':y='(in_h-out_h)/2',eq=contrast=1.06:saturation=1.05:brightness=-0.03",

                "Glitch Distort" =>
                    $"scale={upW}:{upH}:flags=lanczos," +
                    $"crop={targetWidth}:{targetHeight}:x='(in_w-out_w)/2+((in_w-out_w)*{FfmpegDouble(0.04 + (0.08 * fxStrength), 4)}*sin(22*t))':y='(in_h-out_h)/2+((in_h-out_h)*{FfmpegDouble(0.012 + (0.028 * fxStrength), 4)}*sin(30*t))',eq=contrast={FfmpegDouble(1.08 + (0.04 * fxStrength), 3)}:saturation={FfmpegDouble(1.06 + (0.04 * fxStrength), 3)}",

                "3D Spin Lite" =>
                    $"scale={upW}:{upH}:flags=lanczos,rotate='0.03*sin(2*PI*t/{d})':c=black:ow=iw:oh=ih," +
                    $"crop={targetWidth}:{targetHeight}:x='(in_w-out_w)/2':y='(in_h-out_h)/2'",

                "Trend Zoom Flash" =>
                    $"scale=w='ceil(({tw})*(1+0.14*(t/{d}))/2)*2':h='ceil(({th})*(1+0.14*(t/{d}))/2)*2':eval=frame:flags=lanczos," +
                    $"crop={targetWidth}:{targetHeight}:x='(in_w-out_w)/2':y='(in_h-out_h)/2',unsharp=lx=3:ly=3:la=1.15,eq=contrast=1.22:saturation=1.18:brightness=0.03",

                "Trend Blur Pulse" =>
                    $"scale=w='ceil(({tw})*(1+0.08*(t/{d}))/2)*2':h='ceil(({th})*(1+0.08*(t/{d}))/2)*2':eval=frame:flags=lanczos," +
                    $"crop={targetWidth}:{targetHeight}:x='(in_w-out_w)/2':y='(in_h-out_h)/2',boxblur=luma_radius=2:luma_power=1:chroma_radius=1:chroma_power=1,eq=contrast=1.08:saturation=1.12",

                "Trend Spin Glitch" =>
                    $"scale={upW}:{upH}:flags=lanczos,rotate='{FfmpegDouble(0.015 + (0.010 * fxStrength), 4)}*sin(6*PI*t/{d})':c=black:ow=iw:oh=ih," +
                    $"crop={targetWidth}:{targetHeight}:x='(in_w-out_w)/2+((in_w-out_w)*{FfmpegDouble(0.02 + (0.04 * fxStrength), 4)}*sin(18*t))':y='(in_h-out_h)/2+((in_h-out_h)*{FfmpegDouble(0.015 + (0.035 * fxStrength), 4)}*cos(16*t))',hue=h='{FfmpegDouble(4.0 + (4.0 * fxStrength), 3)}*sin(4*PI*t/{d})':s={FfmpegDouble(1.05 + (0.08 * fxStrength), 3)},eq=contrast={FfmpegDouble(1.08 + (0.06 * fxStrength), 3)}:saturation={FfmpegDouble(1.06 + (0.10 * fxStrength), 3)}",

                "Digicam Memory" =>
                    $"scale=w='ceil(({tw})*(1.04+0.018*sin(2*PI*t/{d}))/2)*2':h='ceil(({th})*(1.04+0.018*sin(2*PI*t/{d}))/2)*2':eval=frame:flags=lanczos," +
                    $"crop={targetWidth}:{targetHeight}:x='(in_w-out_w)/2+((in_w-out_w)*0.30*sin(1.8*t))':y='(in_h-out_h)/2+((in_h-out_h)*0.20*cos(1.4*t))'," +
                    "eq=contrast=1.05:saturation=0.92:brightness=0.010,noise=alls=1:allf=t+u," +
                    $"drawbox=x={margin}:y={margin}:w={safeOuterW}:h={safeOuterH}:color=white@0.28:t={Math.Max(2, focusT - 1)}," +
                    $"drawbox=x={safeW - (margin * 4)}:y={margin}:w={Math.Max(64, safeW / 14)}:h={Math.Max(22, safeH / 42)}:color=white@0.40:t={Math.Max(2, focusT - 1)}," +
                    $"drawbox=x={safeW - (margin * 4) + Math.Max(64, safeW / 14)}:y={margin + Math.Max(5, safeH / 110)}:w={Math.Max(7, safeW / 190)}:h={Math.Max(12, safeH / 90)}:color=white@0.44:t=fill," +
                    $"drawbox=x={margin}:y={safeH - (margin * 2)}:w={Math.Max(18, margin / 2)}:h={Math.Max(18, margin / 2)}:color=red@0.82:t=fill," +
                    $"drawbox=x={(safeW / 2) - Math.Max(34, safeW / 10)}:y={(safeH / 2) - Math.Max(24, safeH / 12)}:w={Math.Max(68, safeW / 5)}:h={Math.Max(48, safeH / 6)}:color=white@0.14:t=2",

                "Polaroid Scrapbook" =>
                    $"scale=w='ceil(({tw})*(1.05+0.035*(t/{d}))/2)*2':h='ceil(({th})*(1.05+0.035*(t/{d}))/2)*2':eval=frame:flags=lanczos,rotate='0.012*sin(2*PI*t/{d})':c={warmPaper}:ow=iw:oh=ih," +
                    $"crop={targetWidth}:{targetHeight}:x='(in_w-out_w)/2':y='(in_h-out_h)/2'," +
                    "eq=contrast=1.03:saturation=0.94:brightness=0.025," +
                    $"drawbox=x={margin}:y={margin}:w=iw-{margin * 2}:h=ih-{margin * 2}:color=white@0.90:t={Math.Max(12, safeW / 70)}," +
                    $"drawbox=x={margin * 2}:y={Math.Max(12, margin / 2)}:w={margin * 5}:h={Math.Max(26, safeH / 24)}:color={scrapbookTape}:t=fill," +
                    $"drawbox=x=iw-{margin * 6}:y=ih-{margin * 3}:w={margin * 5}:h={Math.Max(28, safeH / 24)}:color={scrapbookSticky}:t=fill," +
                    "noise=alls=3:allf=t",

                "Beat Photo Dump" =>
                    (beatMode
                        ? $"scale=w='ceil(({tw})*(1.02+0.18*(0.5+0.5*sin(8*PI*t)))/2)*2':h='ceil(({th})*(1.02+0.18*(0.5+0.5*sin(8*PI*t)))/2)*2':eval=frame:flags=lanczos,"
                        : $"scale=w='ceil(({tw})*(1.04+0.10*exp(-1.5*t/{d})*(0.5+0.5*sin(12*t)))/2)*2':h='ceil(({th})*(1.04+0.10*exp(-1.5*t/{d})*(0.5+0.5*sin(12*t)))/2)*2':eval=frame:flags=lanczos,") +
                    $"crop={targetWidth}:{targetHeight}:x='(in_w-out_w)/2+10*sin(18*t)':y='(in_h-out_h)/2+7*cos(16*t)'," +
                    "eq=contrast=1.18:saturation=1.16:brightness=0.025,unsharp=lx=3:ly=3:la=1.05," +
                    $"drawbox=x=0:y=0:w=iw:h=ih:color=white@0.28:t=fill:enable='lt(mod(t,0.50),0.055)'," +
                    $"drawbox=x=0:y=0:w=iw:h={Math.Max(5, safeH / 80)}:color={beatTopGlow}:t=fill:enable='lt(mod(t,0.75),0.06)'," +
                    $"drawbox=x=0:y=ih-{Math.Max(5, safeH / 80)}:w=iw:h={Math.Max(5, safeH / 80)}:color={beatBottomGlow}:t=fill:enable='lt(mod(t,0.75),0.06)'," +
                    $"{BuildTemplateDrawtext("PHOTO DUMP", margin.ToString(inv), margin.ToString(inv), fontMed, "white@0.92")}," +
                    "noise=alls=5:allf=t+u",

                "Film Strip" =>
                    $"scale=w='ceil(({tw})*1.06/2)*2':h='ceil(({th})*1.06/2)*2':eval=frame:flags=lanczos," +
                    $"crop={targetWidth}:{targetHeight}:x='(in_w-out_w)/2+((in_w-out_w)*0.22*sin(2*PI*t/{d}))':y='(in_h-out_h)/2'," +
                    "eq=contrast=1.09:saturation=0.86:brightness=-0.012,noise=alls=2:allf=t+u," +
                    $"drawbox=x=0:y=0:w=iw:h={margin}:color=black@0.90:t=fill," +
                    $"drawbox=x=0:y=ih-{margin}:w=iw:h={margin}:color=black@0.90:t=fill," +
                    $"drawbox=x={border}:y={Math.Max(8, margin / 3)}:w={Math.Max(18, safeW / 52)}:h={Math.Max(12, safeH / 70)}:color=white@0.65:t=fill," +
                    $"drawbox=x=iw*0.18:y={Math.Max(8, margin / 3)}:w={Math.Max(18, safeW / 52)}:h={Math.Max(12, safeH / 70)}:color=white@0.65:t=fill," +
                    $"drawbox=x=iw*0.36:y={Math.Max(8, margin / 3)}:w={Math.Max(18, safeW / 52)}:h={Math.Max(12, safeH / 70)}:color=white@0.65:t=fill," +
                    $"drawbox=x=iw*0.54:y={Math.Max(8, margin / 3)}:w={Math.Max(18, safeW / 52)}:h={Math.Max(12, safeH / 70)}:color=white@0.65:t=fill," +
                    $"drawbox=x=iw*0.72:y={Math.Max(8, margin / 3)}:w={Math.Max(18, safeW / 52)}:h={Math.Max(12, safeH / 70)}:color=white@0.65:t=fill," +
                    $"drawbox=x=iw*0.90:y={Math.Max(8, margin / 3)}:w={Math.Max(18, safeW / 52)}:h={Math.Max(12, safeH / 70)}:color=white@0.65:t=fill," +
                    $"drawbox=x={border}:y=ih-{margin - Math.Max(8, margin / 3)}:w={Math.Max(18, safeW / 52)}:h={Math.Max(12, safeH / 70)}:color=white@0.65:t=fill," +
                    $"drawbox=x=iw*0.18:y=ih-{margin - Math.Max(8, margin / 3)}:w={Math.Max(18, safeW / 52)}:h={Math.Max(12, safeH / 70)}:color=white@0.65:t=fill," +
                    $"drawbox=x=iw*0.36:y=ih-{margin - Math.Max(8, margin / 3)}:w={Math.Max(18, safeW / 52)}:h={Math.Max(12, safeH / 70)}:color=white@0.65:t=fill," +
                    $"drawbox=x=iw*0.54:y=ih-{margin - Math.Max(8, margin / 3)}:w={Math.Max(18, safeW / 52)}:h={Math.Max(12, safeH / 70)}:color=white@0.65:t=fill," +
                    $"drawbox=x=iw*0.72:y=ih-{margin - Math.Max(8, margin / 3)}:w={Math.Max(18, safeW / 52)}:h={Math.Max(12, safeH / 70)}:color=white@0.65:t=fill," +
                    $"drawbox=x=iw*0.90:y=ih-{margin - Math.Max(8, margin / 3)}:w={Math.Max(18, safeW / 52)}:h={Math.Max(12, safeH / 70)}:color=white@0.65:t=fill," +
                    $"drawbox=x=0:y=0:w=iw:h=ih:color=white@0.03:t=fill:enable='lt(mod(t,1.65),0.03)'",

                "Magazine Cover" =>
                    $"scale=w='ceil(({tw})*(1.03+0.025*(t/{d}))/2)*2':h='ceil(({th})*(1.03+0.025*(t/{d}))/2)*2':eval=frame:flags=lanczos," +
                    $"crop={targetWidth}:{targetHeight}:x='(in_w-out_w)/2':y='(in_h-out_h)/2'," +
                    "eq=contrast=1.10:saturation=0.96:brightness=0.018,unsharp=lx=3:ly=3:la=0.60," +
                    $"drawbox=x={margin}:y={margin}:w=iw-{margin * 2}:h=ih-{margin * 2}:color=white@0.34:t={focusT}," +
                    $"{BuildTemplateDrawtext(magazineTitle, "(w-text_w)/2", margin.ToString(inv), fontLarge, "white@0.94")}," +
                    $"{BuildTemplateDrawtext(magazineSubtitle, margin.ToString(inv), "h*0.22", fontMed, "white@0.90")}," +
                    $"{BuildTemplateDrawtext("MEMORY ISSUE", margin.ToString(inv), "ih*0.29", fontSmall, "white@0.76")}," +
                    $"drawbox=x=iw-{margin * 8}:y=ih*0.23:w={margin * 6}:h={Math.Max(4, safeH / 90)}:color=white@0.82:t=fill," +
                    $"{BuildTemplateDrawtext("AESTHETIC", $"iw-{margin * 8}", "ih*0.25", fontSmall, "white@0.84")}," +
                    $"{BuildTemplateDrawtext("2026 EDITION", $"iw-{margin * 8}", $"ih-{margin * 2}", fontTiny, "white@0.72")}",

                _ => string.Empty
            };
        }

        private static async Task<TemplateTimelineBuildResult> BuildTemplateTimelineSourceAsync(
            IReadOnlyList<string> inputPaths,
            string outDir,
            string templateTag,
            RenderJob job,
            string? externalAudioPath,
            double imageTimelineTotalDurationOverride,
            CancellationToken token,
            Action<string> onLog)
        {
            if (inputPaths == null || inputPaths.Count == 0)
                throw new InvalidOperationException("Template timeline requires at least one input.");

            var normalizedInputs = inputPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Path.GetFullPath(p.Trim()))
                .ToList();

            if (normalizedInputs.Count == 0)
                throw new InvalidOperationException("Template timeline has no valid input.");

            foreach (string input in normalizedInputs)
            {
                if (!File.Exists(input))
                    throw new FileNotFoundException($"Template source does not exist: {input}");
            }

            Directory.CreateDirectory(outDir);
            string safeTag = string.IsNullOrWhiteSpace(templateTag)
                ? Guid.NewGuid().ToString("N").Substring(0, 8)
                : Regex.Replace(templateTag, @"[^\w\-]+", "_");

            string templateName = NormalizeTemplateNameForEngine(job.TemplateName);
            var profile = await ResolveTemplateOutputProfileAsync(normalizedInputs, job);
            onLog($"[TEMPLATE] Profile: {profile.Width}x{profile.Height} @ {profile.Fps:F3}fps ({templateName})");

            var artifacts = new List<string>();
            var segmentPaths = new List<string>();
            double totalDuration = 0.0;
            int imageCount = normalizedInputs.Count(IsImageSourcePath);
            double effectiveImageTimelineTotal = imageTimelineTotalDurationOverride > 0.001
                ? imageTimelineTotalDurationOverride
                : job.ImageTimelineDurationSeconds;
            double imageSegmentDuration = EngineCore.ResolveImageTimelineSegmentDuration(effectiveImageTimelineTotal, imageCount);

            for (int i = 0; i < normalizedInputs.Count; i++)
            {
                string input = normalizedInputs[i];
                bool isImage = IsImageSourcePath(input);

                double segmentDuration = imageSegmentDuration;
                if (!isImage)
                {
                    var meta = await AnalyzeMediaDetailsAsync(input);
                    if (meta.Duration > 0.05)
                        segmentDuration = meta.Duration;
                }

                segmentDuration = Math.Max(0.10, segmentDuration);
                totalDuration += segmentDuration;

                string segPath = Path.Combine(GetTitanTempDir(), $"template_{safeTag}_seg_{i:D2}.mp4");
                await RenderTemplateSegmentAsync(
                    input,
                    isImage,
                    segPath,
                    segmentDuration,
                    profile.Width,
                    profile.Height,
                    profile.Fps,
                    templateName,
                    job.MusicSyncMode,
                    job.FxIntensity,
                    job.MagazineCoverTitle,
                    job.MagazineCoverSubtitle,
                    token,
                    onLog);

                segmentPaths.Add(segPath);
                artifacts.Add(segPath);
            }

            if (segmentPaths.Count == 1)
            {
                return new TemplateTimelineBuildResult
                {
                    OutputPath = segmentPaths[0],
                    DurationSec = totalDuration,
                    TempArtifacts = artifacts
                };
            }

            string concatListPath = Path.Combine(GetTitanTempDir(), $"template_{safeTag}_concat.txt");
            string timelinePath = Path.Combine(GetTitanTempDir(), $"template_{safeTag}_timeline.mp4");
            artifacts.Add(concatListPath);
            artifacts.Add(timelinePath);

            string[] concatLines = segmentPaths
                .Select(p => $"file '{BuildConcatListPathToken(p)}'")
                .ToArray();
            File.WriteAllLines(concatListPath, concatLines, new UTF8Encoding(false));

            string copyArgs =
                $"-y -f concat -safe 0 -i \"{concatListPath}\" " +
                "-fflags +genpts -avoid_negative_ts make_zero " +
                "-c copy -movflags +faststart " +
                $"\"{timelinePath}\"";

            var (copyExit, copyErr) = await RunFfmpegCaptureAsync(copyArgs, token);
            bool copyOk = copyExit == 0 && File.Exists(timelinePath) && new FileInfo(timelinePath).Length > 0;
            if (!copyOk)
            {
                string copySummary = string.Join(" | ",
                    (copyErr ?? string.Empty)
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Take(5));
                if (!string.IsNullOrWhiteSpace(copySummary))
                    onLog($"[TEMPLATE-WARN] Concat copy failed: {copySummary}");

                TryDeleteFileSafe(timelinePath);

                string reencodeArgs =
                    $"-y -f concat -safe 0 -i \"{concatListPath}\" " +
                    "-fflags +genpts -avoid_negative_ts make_zero " +
                    (EngineCore.HasNvencSupport() ? "-c:v h264_nvenc -preset p5 -cq 18 -pix_fmt yuv420p " : "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p ") +
                    "-c:a aac -b:a 192k -ar 48000 -ac 2 -movflags +faststart " +
                    $"\"{timelinePath}\"";

                var (reencodeExit, reencodeErr) = await RunFfmpegCaptureAsync(reencodeArgs, token);
                if (reencodeExit != 0 || !File.Exists(timelinePath) || new FileInfo(timelinePath).Length <= 0)
                {
                    string reencodeSummary = string.Join(" | ",
                        (reencodeErr ?? string.Empty)
                            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Take(8));
                    throw new Exception($"Template concat failed: {reencodeSummary}");
                }
            }

            var finalMeta = await AnalyzeMediaDetailsAsync(timelinePath);
            double finalDuration = finalMeta.Duration > 0.05 ? finalMeta.Duration : totalDuration;

            return new TemplateTimelineBuildResult
            {
                OutputPath = timelinePath,
                DurationSec = finalDuration,
                TempArtifacts = artifacts
            };
        }

        private static string BuildConcatListPathToken(string inputPath)
        {
            string full = Path.GetFullPath(inputPath).Replace('\\', '/');
            return full.Replace("'", "\\'");
        }

        // [HELPER] Scale filter intensity by modifying parameter values
        private static string ScaleFilterIntensity(string filterCmd, double intensity)
        {
            if (intensity >= 0.99) return filterCmd;
            
            var invCulture = System.Globalization.CultureInfo.InvariantCulture;
            
            // Scale saturation: blend toward 1.0 (neutral)
            filterCmd = System.Text.RegularExpressions.Regex.Replace(
                filterCmd,
                @"saturation=([0-9.]+)",
                m =>
                {
                    if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Any, invCulture, out double val))
                    {
                        double scaled = 1.0 + (val - 1.0) * intensity;
                        return $"saturation={scaled.ToString("F3", invCulture)}";
                    }
                    return m.Value;
                }
            );
            
            // Scale contrast: blend toward 1.0 (neutral)
            filterCmd = System.Text.RegularExpressions.Regex.Replace(
                filterCmd,
                @"contrast=([0-9.]+)",
                m =>
                {
                    if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Any, invCulture, out double val))
                    {
                        double scaled = 1.0 + (val - 1.0) * intensity;
                        return $"contrast={scaled.ToString("F3", invCulture)}";
                    }
                    return m.Value;
                }
            );
            
            // Scale brightness: blend toward 0.0 (neutral)
            filterCmd = System.Text.RegularExpressions.Regex.Replace(
                filterCmd,
                @"brightness=([+-]?[0-9.]+)",
                m =>
                {
                    if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Any, invCulture, out double val))
                    {
                        double scaled = val * intensity;
                        return $"brightness={scaled.ToString("F3", invCulture)}";
                    }
                    return m.Value;
                }
            );
            
            // Scale colorbalance parameters: blend toward 0.0 (neutral)
            // Match patterns like: rs=-0.2, bs=0.3, rm=0.1, gm=0.05, etc.
            filterCmd = System.Text.RegularExpressions.Regex.Replace(
                filterCmd,
                @"([rgb])[sm]=([+-]?[0-9.]+)",
                m =>
                {
                    if (double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Any, invCulture, out double val))
                    {
                        double scaled = val * intensity;
                        return $"{m.Groups[1].Value}{m.Value[1]}={scaled.ToString("F3", invCulture)}";
                    }
                    return m.Value;
                }
            );
            
            return filterCmd;
        }

        // [NEW] LIGHTWEIGHT DLSS/FSR - Fast & Stable (Default)
        private static string BuildLightweightUpscaleFilter(int targetW, int targetH, double sharpnessIntensity, Action<string> onLog)
        {
            var invCulture = System.Globalization.CultureInfo.InvariantCulture;
            StringBuilder filterChain = new StringBuilder();
            
            onLog($"[UPSCALE-MODE] Lightweight DLSS/FSR (2x ? 4K)");
            onLog($"[UPSCALE-DEBUG] Mode: Fast & Stable");
            
            // STEP 1: FSRCNNX AI Upscaling via libplacebo
            string shaderPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shaders", "FSRCNNX.glsl").Replace("\\", "/").Replace(":", "\\:");
            filterChain.Append($"libplacebo=w={targetW}:h={targetH}:custom_shader_path='{shaderPath}'");
            onLog($"[STEP1] FSRCNNX AI upscaling to {targetW}x{targetH}");
            
            // STEP 2: Light unsharp for final edge enhancement
            filterChain.Append(",");
            double sharpen = 0.5 + (sharpnessIntensity * 0.5);  // 0.5-1.0
            string step2Filter = $"unsharp=lx=3:ly=3:la={sharpen.ToString("F2", invCulture)}";
            filterChain.Append(step2Filter);
            onLog($"[STEP2] Edge enhancement - la={sharpen:F2}");
            
            // STEP 3: Light contrast/saturation for detail recovery
            filterChain.Append(",");
            double contrast = 1.05 + (sharpnessIntensity * 0.1);  // 1.05-1.15
            double saturation = 1.0 + (sharpnessIntensity * 0.1);
            string step3Filter = $"eq=contrast={contrast.ToString("F2", invCulture)}:saturation={saturation.ToString("F2", invCulture)}";
            filterChain.Append(step3Filter);
            onLog($"[STEP3] Detail recovery eq - contrast={contrast:F2}, sat={saturation:F2}");
            
            string finalChain = filterChain.ToString();
            onLog($"[UPSCALE-FINAL] Lightweight pipeline complete");
            onLog($"[FILTER-CHAIN] {finalChain}");
            
            return finalChain;
        }

        // [NEW] HEAVY UPSCALING - RealESRGAN Style (Best Quality)
        private static string BuildHeavyUpscaleFilter(int targetW, int targetH, double sharpnessIntensity, Action<string> onLog)
        {
            var invCulture = System.Globalization.CultureInfo.InvariantCulture;
            StringBuilder filterChain = new StringBuilder();
            
            onLog($"[UPSCALE-MODE] Heavy RealESRGAN (2x ? 4K with AI enhancement)");
            onLog($"[UPSCALE-DEBUG] Mode: Maximum Quality (Slower)");
            
            // STEP 1: Deblock filter before upscaling
            filterChain.Append("deblock=filter=weak");
            onLog($"[STEP1] Deblock filter (Pre-upscale)");
            
            // STEP 2: FSRCNNX AI Upscaling via libplacebo
            filterChain.Append(",");
            string shaderPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shaders", "FSRCNNX.glsl").Replace("\\", "/").Replace(":", "\\:");
            filterChain.Append($"libplacebo=w={targetW}:h={targetH}:custom_shader_path='{shaderPath}'");
            onLog($"[STEP2] FSRCNNX AI upscaling to {targetW}x{targetH}");
            
            // STEP 3: Strong unsharp for edge reconstruction
            filterChain.Append(",");
            double sharpen = 0.8 + (sharpnessIntensity * 1.0);  // 0.8-1.8
            string step3Filter = $"unsharp=lx=5:ly=5:la={sharpen.ToString("F2", invCulture)}";
            filterChain.Append(step3Filter);
            onLog($"[STEP3] Edge reconstruction - la={sharpen:F2}");
            
            // STEP 4: Strong contrast/saturation for detail enhancement
            filterChain.Append(",");
            double contrast = 1.15 + (sharpnessIntensity * 0.25);  // 1.15-1.40
            double saturation = 1.10 + (sharpnessIntensity * 0.20);
            string step4Filter = $"eq=contrast={contrast.ToString("F2", invCulture)}:saturation={saturation.ToString("F2", invCulture)}:brightness=0.02";
            filterChain.Append(step4Filter);
            onLog($"[STEP4] Detail enhancement eq");
            
            // STEP 5: Smart anti-aliasing
            filterChain.Append(",");
            filterChain.Append("smartblur=luma_radius=1.0:luma_strength=0.2");
            onLog($"[STEP5] Smart anti-aliasing");
            
            string finalChain = filterChain.ToString();
            onLog($"[UPSCALE-FINAL] Heavy pipeline complete");
            onLog($"[FILTER-CHAIN] {finalChain}");
            
            return finalChain;
        }

        // [NEW] ADVANCED UPSCALING - Selector (choose between Lightweight and Heavy)
        private static string BuildAdvancedScaleFilter(int targetW, int targetH, string upscaleMode, double sharpnessIntensity, Action<string> onLog)
        {
            if (upscaleMode == "Off" || upscaleMode == "Off (Original Size)")
            {
                onLog($"[UPSCALE-DEBUG] Mode = Off, no upscaling");
                return $"scale={targetW}:{targetH}:flags=lanczos";
            }

            // Use Lightweight for 2x (fast & stable)
            if (upscaleMode.Contains("2x"))
            {
                onLog($"[UPSCALE-SELECTOR] 2x selected ? Using Lightweight DLSS/FSR");
                return BuildLightweightUpscaleFilter(targetW, targetH, sharpnessIntensity, onLog);
            }
            
            // Use Heavy for 4x (best quality, slower)
            if (upscaleMode.Contains("4x"))
            {
                onLog($"[UPSCALE-SELECTOR] 4x selected ? Using Heavy RealESRGAN");
                return BuildHeavyUpscaleFilter(targetW, targetH, sharpnessIntensity, onLog);
            }

            // Default to lightweight
            return BuildLightweightUpscaleFilter(targetW, targetH, sharpnessIntensity, onLog);
        }
    }
}
