/*******************************************************************************************************
* TITAN ENGINE V106 - AI UPSCALE REWRITE (CAS ALGORITHM + CUSTOM RESOLUTION FIX)
* ---------------------------------------------------------------------------------------------------
* Major Changes:
* 1. Upscale: unsharp ? CAS (Contrast Adaptive Sharpening) + Hardcode Bitrate 25Mbps
* 2. Resolution: Fix Custom + Vertical (Video D?c) parsing
* 3. Filter Chain: Strict order [Scale] ? [CAS] ? [FX] ? [Color] ? [Watermark]
*******************************************************************************************************/

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

    #region --- [ENGINE CORE] ---

    public static class EngineCore
    {
        private const string BundledBrandLogoRelativePath = @"Content\Branding\video-logo.png";
        private const string SeedBrandLogoPath = @"C:\Users\lemin\Desktop\ba546ee2-dea8-4016-b57b-12833c12710c.png";

        private static string? _ffmpegPath;
        private static string? _ffprobePath;
        private static string? _upscaylPath;
        private static string? _ngxDlvsrPath;
        private static string? _vfxUpscaleExePath;
        private static string? _vfxModelDir;
        private static string? _vfxRuntimePathPrefix;
        private static bool _isInitialized = false;

        // [NEW] Check if FFmpeg supports GPU encoders
        private static bool _hasNvencSupport = false;
        private static bool _hasHevcNvencSupport = false;
        private static bool _hasAv1NvencSupport = false;
        private static bool _hasAv1AmfSupport = false;
        private static bool _hasNvencChecked = false;

        public static bool HasNvencSupport()
        {
            if (_hasNvencChecked) return _hasNvencSupport;
            _hasNvencChecked = true;

            try
            {
                if (string.IsNullOrEmpty(_ffmpegPath) || !File.Exists(_ffmpegPath))
                    return false;

                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = "-encoders",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null) return false;
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    _hasNvencSupport = output.Contains("h264_nvenc", StringComparison.OrdinalIgnoreCase);
                    _hasHevcNvencSupport = output.Contains("hevc_nvenc", StringComparison.OrdinalIgnoreCase);
                    _hasAv1NvencSupport = output.Contains("av1_nvenc", StringComparison.OrdinalIgnoreCase);
                    _hasAv1AmfSupport = output.Contains("av1_amf", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }

            return _hasNvencSupport;
        }

        public static bool HasHevcNvencSupport()
        {
            if (!_hasNvencChecked)
                _ = HasNvencSupport();

            return _hasHevcNvencSupport;
        }

        public static bool HasAv1NvencSupport()
        {
            if (!_hasNvencChecked) _ = HasNvencSupport();
            return _hasAv1NvencSupport;
        }

        public static bool HasAv1AmfSupport()
        {
            if (!_hasNvencChecked) _ = HasNvencSupport();
            return _hasAv1AmfSupport;
        }

        public static void Initialize()
        {
            if (_isInitialized) return;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _ffmpegPath = System.IO.Path.Combine(baseDir, "ffmpeg.exe");
            _ffprobePath = System.IO.Path.Combine(baseDir, "ffprobe.exe");
            _upscaylPath = System.IO.Path.Combine(baseDir, "upscayl", "Upscayl.exe");
            _ngxDlvsrPath = System.IO.Path.Combine(baseDir, "dlss", "DLVSR.exe");
            RefreshNvidiaUpscaleBackendPaths();

            if (!File.Exists(_ffmpegPath))
            {
                var msg = $"FFmpeg not found at:\n{baseDir}\n\nManually select ffmpeg.exe?";
                if (MessageBox.Show(msg, "Missing Core Engine", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    var dlg = new OpenFileDialog { Filter = "FFmpeg|ffmpeg.exe" };
                    if (dlg.ShowDialog() == true)
                    {
                        _ffmpegPath = dlg.FileName;
                        string? dirName = System.IO.Path.GetDirectoryName(_ffmpegPath);
                        if (dirName != null)
                            _ffprobePath = System.IO.Path.Combine(dirName, "ffprobe.exe");
                    }
                    else throw new FileNotFoundException("FFmpeg Core Engine not found.");
                }
                else throw new FileNotFoundException($"Please copy ffmpeg.exe to:\n{baseDir}");
            }
            _isInitialized = true;
        }

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

        public static bool HasNgxDlssSupport()
        {
            bool hasDlvsrExe = !string.IsNullOrWhiteSpace(_ngxDlvsrPath) && File.Exists(_ngxDlvsrPath);
            if (hasDlvsrExe)
                return true;

            RefreshNvidiaUpscaleBackendPaths();
            return !string.IsNullOrWhiteSpace(_vfxUpscaleExePath) && File.Exists(_vfxUpscaleExePath);
        }

        public static string GetNgxDlssExpectedPath()
        {
            if (!string.IsNullOrWhiteSpace(_ngxDlvsrPath))
                return _ngxDlvsrPath;

            return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dlss", "DLVSR.exe");
        }

        public static string GetDlssDirectoryPath()
        {
            string expected = GetNgxDlssExpectedPath();
            string? dir = Path.GetDirectoryName(expected);
            if (!string.IsNullOrWhiteSpace(dir))
                return dir;

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dlss");
        }

        public static string GetNgxBackendInfo()
        {
            RefreshNvidiaUpscaleBackendPaths();

            if (!string.IsNullOrWhiteSpace(_ngxDlvsrPath) && File.Exists(_ngxDlvsrPath))
                return $"DLVSR.exe ({_ngxDlvsrPath})";

            if (!string.IsNullOrWhiteSpace(_vfxUpscaleExePath) && File.Exists(_vfxUpscaleExePath))
                return $"UpscalePipelineApp ({_vfxUpscaleExePath})";

            return "NOT FOUND";
        }

        public static async Task<bool> DownloadDlvsrSetupBundleAsync(Action<string> onLog, CancellationToken token)
        {
            string dlssDir = GetDlssDirectoryPath();
            string expectedDlvsrPath = GetNgxDlssExpectedPath();
            Directory.CreateDirectory(dlssDir);

            if (HasNgxDlssSupport())
            {
                onLog("[DLSS-NGX] Upscale backend already ready. Skip download.");
                return true;
            }

            string setupZipPath = Path.Combine(dlssDir, "NVIDIA_VFX_SDK_Samples.zip");

            string zipUrl = await ResolveVfxSamplesZipUrlAsync(onLog, token);
            onLog($"[DLSS-NGX] Downloading setup bundle...");
            onLog($"[DLSS-NGX] Source: {zipUrl}");

            bool downloaded = true;
            if (File.Exists(setupZipPath) && new FileInfo(setupZipPath).Length > 0)
            {
                onLog($"[DLSS-NGX] Reusing existing bundle: {setupZipPath}");
            }
            else
            {
                downloaded = await DownloadFileWithProgressAsync(zipUrl, setupZipPath, onLog, token);
            }
            if (!downloaded)
            {
                onLog("[DLSS-NGX] Download failed.");
                return false;
            }

            string? sampleRoot = ExtractVfxSamplesZip(setupZipPath, dlssDir, onLog);
            if (!string.IsNullOrWhiteSpace(sampleRoot))
                onLog($"[DLSS-NGX] Source extracted: {sampleRoot}");

            WriteDlssSetupArtifacts(dlssDir, sampleRoot, onLog);

            bool autoBuildReady = await TryAutoBuildNvidiaUpscaleBackendAsync(onLog, token);
            if (autoBuildReady)
                onLog("[DLSS-NGX] NVIDIA upscale backend is ready after auto setup.");

            onLog($"[DLSS-NGX] Bundle downloaded: {setupZipPath}");
            onLog($"[DLSS-NGX] Guide created: {Path.Combine(dlssDir, "DLVSR_SETUP_README.txt")}");
            onLog($"[DLSS-NGX] Helper script created: {Path.Combine(dlssDir, "NVIDIA_VFX_AUTO_SETUP.ps1")}");
            onLog($"[DLSS-NGX] Put DLVSR.exe here after setup: {expectedDlvsrPath}");
            return true;
        }

        private static void WriteDlssSetupArtifacts(string dlssDir, string? sampleRoot, Action<string> onLog)
        {
            try
            {
                Directory.CreateDirectory(dlssDir);

                string expectedDlvsrPath = GetNgxDlssExpectedPath();
                string stagedUpscalePath = Path.Combine(dlssDir, "UpscalePipelineApp", "UpscalePipelineApp.exe");
                string setupGuidePath = Path.Combine(dlssDir, "DLVSR_SETUP_README.txt");
                string helperScriptPath = Path.Combine(dlssDir, "NVIDIA_VFX_AUTO_SETUP.ps1");
                string statusPath = Path.Combine(dlssDir, "NVIDIA_VFX_STATUS.txt");

                string? cmakePath = ResolveCmakePath();
                string? vfxSdkRoot = ResolveVfxSdkRoot(_ => { });
                bool hasFeature = !string.IsNullOrWhiteSpace(vfxSdkRoot) && HasUpscaleFeatureInstalled(vfxSdkRoot);
                sampleRoot ??= ResolveVfxSamplesSourceRoot(dlssDir);

                string guide = string.Join(Environment.NewLine, new[]
                {
                    "TITAN ENGINE - NVIDIA VFX / DLSS AUTO SETUP",
                    "",
                    "What this folder contains:",
                    "- NVIDIA public sample source bundle (VFX-SDK-Samples)",
                    "- Helper PowerShell script to build the UpscalePipelineApp backend",
                    "",
                    "Important:",
                    "- NVIDIA does NOT ship a public prebuilt DLVSR.exe in the sample bundle.",
                    "- Titan Engine can use either DLVSR.exe or the NVIDIA sample backend UpscalePipelineApp.exe.",
                    "- A real build still requires NVIDIA VFX SDK Core + feature nvvfxupscale from NGC.",
                    "",
                    "Detected paths on this machine:",
                    $"Sample source root: {sampleRoot ?? "(not extracted yet)"}",
                    $"CMake: {cmakePath ?? "(not found)"}",
                    $"VFXSDK_ROOT: {vfxSdkRoot ?? "(not found)"}",
                    $"Feature nvvfxupscale: {(hasFeature ? "READY" : "MISSING")}",
                    "",
                    "Expected / supported backend paths:",
                    $"DLVSR.exe path: {expectedDlvsrPath}",
                    $"UpscalePipelineApp staged path: {stagedUpscalePath}",
                    "",
                    "One-click helper:",
                    helperScriptPath,
                    "",
                    "If VFX SDK is not installed yet:",
                    "1. Open the official install guide below.",
                    "2. Install the NVIDIA Maxine VFX SDK Core package.",
                    "3. In the SDK features folder, install nvvfxupscale.",
                    "4. Run NVIDIA_VFX_AUTO_SETUP.ps1 from this dlss folder.",
                    "",
                    "Feature install command example:",
                    "cd <VFXSDK_ROOT>\\features",
                    "$Env:NGC_CLI_API_KEY = \"<your-ngc-api-key>\"",
                    ".\\install_feature.ps1 -features nvvfxupscale",
                    "",
                    "Official docs:",
                    "https://docs.nvidia.com/maxine/vfx/latest/WindowsVFXSDK/InstalltheVFXSDK.html",
                    "",
                    "NGC collection page:",
                    "https://catalog.ngc.nvidia.com/orgs/nvidia/teams/maxine/collections/maxine_windows_vfx_sdk_collection_ga",
                    "",
                    "Open-source sample source:",
                    "https://github.com/NVIDIA-Maxine/VFX-SDK-Samples"
                });

                string helperScript = BuildDlssAutoSetupScript();
                string status = string.Join(Environment.NewLine, new[]
                {
                    $"timestamp={DateTime.Now:O}",
                    $"sampleRoot={sampleRoot ?? string.Empty}",
                    $"cmakePath={cmakePath ?? string.Empty}",
                    $"vfxSdkRoot={vfxSdkRoot ?? string.Empty}",
                    $"hasUpscaleFeature={hasFeature}",
                    $"expectedDlvsrPath={expectedDlvsrPath}",
                    $"stagedUpscalePath={stagedUpscalePath}"
                });

                File.WriteAllText(setupGuidePath, guide, new UTF8Encoding(true));
                File.WriteAllText(helperScriptPath, helperScript, new UTF8Encoding(true));
                File.WriteAllText(statusPath, status, new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                onLog($"[DLSS-NGX] Could not write setup helper files: {ex.Message}");
            }
        }

        private static string BuildDlssAutoSetupScript()
        {
            return """
param(
    [string]$VfxSdkRoot = "",
    [switch]$SkipFeatureInstall
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Resolve-VfxSdkRoot {
    param([string]$Preferred)

    $candidates = @()
    if ($Preferred) { $candidates += $Preferred }
    if ($env:VFXSDK_ROOT) { $candidates += $env:VFXSDK_ROOT }
    $candidates += @(
        (Join-Path $env:ProgramFiles "NVIDIA Corporation\VFXSDK"),
        (Join-Path $env:ProgramFiles "NVIDIA Corporation\NVIDIA Video Effects SDK"),
        (Join-Path $env:ProgramFiles "NVIDIA Corporation\NVIDIA Maxine Video Effects SDK"),
        (Join-Path $env:ProgramFiles "NVIDIA Corporation\Video Effects SDK")
    )

    foreach ($candidate in $candidates | Where-Object { $_ } | Select-Object -Unique) {
        $dll = Join-Path $candidate "bin\NVVideoEffects.dll"
        $include = Join-Path $candidate "include"
        $features = Join-Path $candidate "features"
        if ((Test-Path $dll) -and (Test-Path $include) -and (Test-Path $features)) {
            return (Resolve-Path $candidate).Path
        }
    }

    return $null
}

function Ensure-UpscaleFeature {
    param([string]$Root)

    $featureDir = Join-Path $Root "features\nvvfxupscale"
    if (Test-Path $featureDir) {
        Write-Host "[DLSS-AUTO] nvvfxupscale already installed."
        return
    }

    if ($SkipFeatureInstall) {
        throw "nvvfxupscale is missing and -SkipFeatureInstall was supplied."
    }

    $installer = Join-Path $Root "features\install_feature.ps1"
    if (-not (Test-Path $installer)) {
        throw "install_feature.ps1 was not found under $Root\features."
    }

    if (-not $env:NGC_CLI_API_KEY) {
        throw "NGC_CLI_API_KEY is not set. Set it first, then rerun this script."
    }

    Write-Host "[DLSS-AUTO] Installing NVIDIA feature nvvfxupscale..."
    & powershell -ExecutionPolicy Bypass -File $installer -features nvvfxupscale
    if ($LASTEXITCODE -ne 0) {
        throw "install_feature.ps1 failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path $featureDir)) {
        throw "Feature install finished but nvvfxupscale is still missing."
    }
}

$sampleRoot = Get-ChildItem $scriptDir -Directory | Where-Object { $_.Name -like "NVIDIA-Maxine-VFX-SDK-Samples-*" } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $sampleRoot) {
    throw "Sample source folder was not found next to this script."
}

$cmake = (Get-Command cmake -ErrorAction SilentlyContinue)
if (-not $cmake) {
    throw "cmake was not found on PATH. Install CMake first."
}

$resolvedVfxSdkRoot = Resolve-VfxSdkRoot -Preferred $VfxSdkRoot
if (-not $resolvedVfxSdkRoot) {
    throw "VFX SDK Core was not found. Install it from NVIDIA first."
}

Ensure-UpscaleFeature -Root $resolvedVfxSdkRoot

$buildDir = Join-Path $sampleRoot.FullName "build_titan_auto"
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null

Write-Host "[DLSS-AUTO] Configure using VFXSDK_ROOT=$resolvedVfxSdkRoot"
& $cmake.Source -S $sampleRoot.FullName -B $buildDir -G "Visual Studio 17 2022" -A x64 -DVFXSDK_ROOT="$resolvedVfxSdkRoot"
if ($LASTEXITCODE -ne 0) {
    throw "CMake configure failed with exit code $LASTEXITCODE."
}

Write-Host "[DLSS-AUTO] Building UpscalePipelineApp..."
& $cmake.Source --build $buildDir --config Release --target UpscalePipelineApp --parallel
if ($LASTEXITCODE -ne 0) {
    throw "CMake build failed with exit code $LASTEXITCODE."
}

$builtDir = Join-Path $buildDir "apps\UpscalePipelineApp\Release"
$builtExe = Join-Path $builtDir "UpscalePipelineApp.exe"
if (-not (Test-Path $builtExe)) {
    throw "Build completed but UpscalePipelineApp.exe was not found."
}

$stagedDir = Join-Path $scriptDir "UpscalePipelineApp"
New-Item -ItemType Directory -Force -Path $stagedDir | Out-Null
Copy-Item (Join-Path $builtDir "*") $stagedDir -Recurse -Force

Write-Host "[DLSS-AUTO] Ready:"
Write-Host "  $builtExe"
Write-Host "[DLSS-AUTO] Staged copy:"
Write-Host "  $(Join-Path $stagedDir 'UpscalePipelineApp.exe')"
""";
        }

        private static async Task<string> ResolveVfxSamplesZipUrlAsync(Action<string> onLog, CancellationToken token)
        {
            const string fallbackUrl = "https://codeload.github.com/NVIDIA-Maxine/VFX-SDK-Samples/zip/refs/heads/main";

            try
            {
                using var http = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("TitanEngine/106.0");

                using var resp = await http.GetAsync("https://api.github.com/repos/NVIDIA-Maxine/VFX-SDK-Samples/releases/latest", token);
                if (!resp.IsSuccessStatusCode)
                {
                    onLog($"[DLSS-NGX] Could not query latest release ({(int)resp.StatusCode}). Using fallback package URL.");
                    return fallbackUrl;
                }

                string json = await resp.Content.ReadAsStringAsync(token);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("zipball_url", out JsonElement zipEl))
                {
                    string? zipUrl = zipEl.GetString();
                    if (!string.IsNullOrWhiteSpace(zipUrl))
                        return zipUrl;
                }
            }
            catch (Exception ex)
            {
                onLog($"[DLSS-NGX] Release query failed: {ex.Message}. Using fallback package URL.");
            }

            return fallbackUrl;
        }

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

        private static string? ResolveVfxSamplesSourceRoot(string dlssDir)
        {
            if (string.IsNullOrWhiteSpace(dlssDir) || !Directory.Exists(dlssDir))
                return null;

            return Directory.GetDirectories(dlssDir, "NVIDIA-Maxine-VFX-SDK-Samples-*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                .FirstOrDefault();
        }

        private static string? ResolveCmakePath()
        {
            try
            {
                // 1) PATH first
                string? fromPath = FindExecutableOnPath("cmake.exe");
                if (!string.IsNullOrWhiteSpace(fromPath))
                    return fromPath;

                var candidates = new List<string>();
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

                candidates.Add(Path.Combine(programFiles, "CMake", "bin", "cmake.exe"));
                candidates.Add(Path.Combine(programFilesX86, "CMake", "bin", "cmake.exe"));

                string[] vsRoots = { programFiles, programFilesX86 };
                string[] vsYears = { "2022", "2019", "2017", "18", "17", "16", "15" };
                string[] vsEditions = { "Community", "Professional", "Enterprise", "BuildTools", "Preview" };

                foreach (string root in vsRoots)
                {
                    foreach (string year in vsYears)
                    {
                        foreach (string edition in vsEditions)
                        {
                            candidates.Add(Path.Combine(root, "Microsoft Visual Studio", year, edition, "Common7", "IDE", "CommonExtensions", "Microsoft", "CMake", "CMake", "bin", "cmake.exe"));
                        }
                    }
                }

                foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (File.Exists(candidate))
                        return candidate;
                }

                // 2) Try vswhere if Visual Studio installer exists
                string? fromVsWhere = ResolveCmakePathFromVsWhere();
                if (!string.IsNullOrWhiteSpace(fromVsWhere))
                    return fromVsWhere;
            }
            catch { }

            return null;
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

        private static string? ResolveCmakePathFromVsWhere()
        {
            try
            {
                string vswherePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Microsoft Visual Studio",
                    "Installer",
                    "vswhere.exe");
                if (!File.Exists(vswherePath))
                    return null;

                var psi = new ProcessStartInfo
                {
                    FileName = vswherePath,
                    Arguments = "-products * -property installationPath",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var p = Process.Start(psi);
                if (p == null)
                    return null;

                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);

                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string installPath = line.Trim();
                    if (string.IsNullOrWhiteSpace(installPath))
                        continue;

                    string candidate = Path.Combine(installPath, "Common7", "IDE", "CommonExtensions", "Microsoft", "CMake", "CMake", "bin", "cmake.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch { }

            return null;
        }

        private static async Task<bool> TryAutoInstallCmakeAsync(Action<string> onLog, CancellationToken token)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(ResolveCmakePath()))
                    return true;

                string? wingetPath = FindExecutableOnPath("winget.exe");
                if (string.IsNullOrWhiteSpace(wingetPath))
                    wingetPath = "winget.exe";

                int wingetCheck = await RunProcessWithLogAsync(wingetPath, "--version", null, "DLSS-CMAKE-CHECK", onLog, token);
                if (wingetCheck != 0)
                {
                    onLog("[DLSS-NGX] winget not available. Cannot auto-install CMake.");
                    return false;
                }

                onLog("[DLSS-NGX] CMake missing. Trying auto-install via winget (Kitware.CMake)...");
                string installArgs = "install --id Kitware.CMake --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";
                int installExit = await RunProcessWithLogAsync(wingetPath, installArgs, null, "DLSS-CMAKE-INSTALL", onLog, token);
                if (installExit != 0)
                {
                    onLog($"[DLSS-NGX] CMake auto-install failed (exit {installExit}).");
                    return false;
                }

                string? cmakePath = ResolveCmakePath();
                if (!string.IsNullOrWhiteSpace(cmakePath))
                {
                    onLog($"[DLSS-NGX] CMake ready: {cmakePath}");
                    return true;
                }

                onLog("[DLSS-NGX] CMake install completed but cmake.exe was not found yet. Please reopen app.");
                return false;
            }
            catch (Exception ex)
            {
                onLog($"[DLSS-NGX] CMake auto-install error: {ex.Message}");
                return false;
            }
        }

        private static string? ResolveVfxSdkRoot(Action<string> onLog)
        {
            try
            {
                var candidates = new List<string>();
                string? envRoot = Environment.GetEnvironmentVariable("VFXSDK_ROOT");
                if (!string.IsNullOrWhiteSpace(envRoot))
                    candidates.Add(envRoot);

                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                candidates.Add(Path.Combine(programFiles, "NVIDIA Corporation", "VFXSDK"));
                candidates.Add(Path.Combine(programFiles, "NVIDIA Corporation", "NVIDIA Video Effects SDK"));
                candidates.Add(Path.Combine(programFiles, "NVIDIA Corporation", "NVIDIA Maxine Video Effects SDK"));
                candidates.Add(Path.Combine(programFiles, "NVIDIA Corporation", "Video Effects SDK"));

                foreach (string candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)))
                {
                    if (LooksLikeVfxSdkRoot(candidate))
                        return Path.GetFullPath(candidate);
                }
            }
            catch (Exception ex)
            {
                onLog($"[DLSS-NGX] VFX SDK detect error: {ex.Message}");
            }

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

        private static (int Width, int Height) TryGetStillImageResolution(string filePath)
        {
            try
            {
                if (!IsImageSourcePath(filePath) || !File.Exists(filePath))
                    return (0, 0);

                using FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapFrame? frame = decoder.Frames.FirstOrDefault();
                if (frame != null && frame.PixelWidth > 0 && frame.PixelHeight > 0)
                    return (frame.PixelWidth, frame.PixelHeight);
            }
            catch
            {
            }

            return (0, 0);
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

                cmd.Append($"-y -i \"{videoToEncode}\" ");

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
                    if (job.HardwareProfile.Contains("AV1", StringComparison.OrdinalIgnoreCase))
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
                    encoder = "h264_qsv";
                    preset = "veryslow";
                    onLog($"[ENCODER] Intel QSV (veryslow - GPU)");
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
                    onLog($"[ENCODER-QUALITY] CRF 16 (High Quality)");
                }
                else if (encoder == "h264_nvenc" || encoder == "hevc_nvenc")
                {
                    onLog($"[ENCODER-NVENC] Using VBR mode with quality level 16");
                }
                cmd.Append("-pix_fmt yuv420p ");

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

                    rgbPolaroidLedOverlay = await RgbPolaroidLedPreset.GenerateOverlayAsync(
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

                    polaroidScrapbook2Overlay = await PolaroidScrapbook2Preset.GenerateOverlayAsync(
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

                    dashedPolaroidOverlay = await DashedPolaroidFramePreset.GenerateOverlayAsync(
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

                    scratchVideoOverlay = await ScratchVideoPreset.GenerateOverlayAsync(
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

                    recipeParticleOverlay = await ParticleOverlayGenerator.GenerateOverlayAsync(
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
                        dreamyDotOverlay = await DreamyDotOverlayPreset.GenerateOverlayAsync(
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
                            SnowfallGeneratedOverlay snowfallOverlay = await SnowfallOverlayPreset.GenerateOverlayAsync(
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
                    string escapedAssPath = captionAssPath.Replace("\\", "/").Replace(":", "\\:");
                    string fontsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Fonts");
                    string escapedFontsDir = fontsDir.Replace("\\", "/").Replace(":", "\\:");
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
                    string lightLeakBurnVideoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                        ? $"[burnout]{fadeVideoFilter}[vout]"
                        : "[burnout]format=yuv420p[vout]";

                    if (hasImageOverlayAssets)
                    {
                        string watermarkGraph = BuildWatermarkFilterComplex(
                            "[burnout]",
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
                    string finalVideoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                        ? "[scratchout]" + fadeVideoFilter + "[vout]"
                        : "[scratchout]format=yuv420p[vout]";

                    if (hasImageOverlayAssets)
                    {
                        string watermarkGraph = BuildWatermarkFilterComplex(
                            "[scratchout]",
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

                    string rainVideoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                        ? "[rainout]" + fadeVideoFilter + "[vout]"
                        : "[rainout]format=yuv420p[vout]";

                    if (hasImageOverlayAssets)
                    {
                        string watermarkGraph = BuildWatermarkFilterComplex(
                            "[rainout]",
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

                    string dreamyDot2VideoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                        ? "[dreamydot2out]" + fadeVideoFilter + "[vout]"
                        : "[dreamydot2out]format=yuv420p[vout]";

                    if (hasImageOverlayAssets)
                    {
                        string watermarkGraph = BuildWatermarkFilterComplex(
                            "[dreamydot2out]",
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

                    string snowVideoTail = !string.IsNullOrWhiteSpace(fadeVideoFilter)
                        ? $"[snowout]{fadeVideoFilter}[vout]"
                        : "[snowout]format=yuv420p[vout]";

                    if (hasImageOverlayAssets)
                    {
                        string watermarkGraph = BuildWatermarkFilterComplex(
                            "[snowout]",
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
                else if (hasImageOverlayAssets)
                {
                    string filterComplex = BuildWatermarkFilterComplex(
                        "[0:v]",
                        filterChain.ToString(),
                        fadeVideoFilter,
                        watermarkInputIndex,
                        brandLogoInputIndex,
                        job,
                        onLog);

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
                        cmd.Append($"-vf \"{filterChain}\" ");
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
                        string tempThumbPath = outPath + ".thumb.jpg";
                        string tempMuxPath = outPath + ".mux.mp4";
                        
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
                            File.Delete(tempThumbPath);
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
                job.StatusColor = Brushes.Red;
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

        private static async Task<string> EnsureDreamyDotOverlayAssetAsync(
            DreamyDotGeneratedOverlay overlay,
            CancellationToken token,
            Action<string> onLog)
        {
            if (overlay == null)
                throw new ArgumentNullException(nameof(overlay));
            string? reusableAssetPath = await TryGetDreamyDotOverlayAssetAsync(overlay, token, onLog);
            if (!string.IsNullOrWhiteSpace(reusableAssetPath))
                return reusableAssetPath;

            if (string.IsNullOrWhiteSpace(overlay.OverlayPattern))
                throw new InvalidOperationException("Dreamy Dot overlay frames were not prepared.");

            string assetPath = GetDreamyDotOverlayAssetPath(overlay);

            double assetDuration = Math.Max(0.10, overlay.FrameCount / Math.Max(1.0, overlay.FrameRate));
            string filterGraph = DreamyDotOverlayPreset.BuildSingleClipGraph(
                "[0:v]",
                string.Empty,
                "[1:v]",
                overlay.FrameCount,
                overlay.FrameRate,
                assetDuration,
                "dreamydotasset",
                _ => { });

            string args =
                $"-y -f lavfi -i \"color=c=black:s={overlay.Width}x{overlay.Height}:r={FfmpegDouble(overlay.FrameRate, 3)}:d={FfmpegDouble(assetDuration, 3)}\" " +
                $"-framerate {FfmpegDouble(overlay.FrameRate, 3)} " +
                $"-start_number 1 -i \"{overlay.OverlayPattern}\" " +
                $"-filter_complex \"{filterGraph};[dreamydotasset]gblur=sigma=1.1:steps=2,format=yuv420p[vout]\" " +
                "-map \"[vout]\" " +
                "-an -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p " +
                $"\"{assetPath}\"";

            onLog($"[DREAMY-DOT-ASSET] Rendering pre-render asset -> {assetPath}");
            var (exitCode, stderr) = await RunFfmpegCaptureAsync(args, token);
            if (exitCode != 0 || !File.Exists(assetPath))
            {
                string reason = string.IsNullOrWhiteSpace(stderr)
                    ? $"ffmpeg exited with code {exitCode}."
                    : stderr.Trim();
                throw new InvalidOperationException($"Dreamy Dot overlay asset render failed: {reason}");
            }

            VideoAssetProbeInfo probe = await ProbeVideoAssetAsync(assetPath, token);
            if (!probe.ProbeSucceeded || !probe.HasVideoStream)
            {
                string reason = string.IsNullOrWhiteSpace(probe.FailureReason)
                    ? "ffprobe could not verify a valid video stream."
                    : probe.FailureReason;
                throw new InvalidOperationException($"Dreamy Dot overlay asset invalid: {reason}");
            }

            onLog($"[DREAMY-DOT-ASSET] Ready: {assetPath}");
            onLog($"[DREAMY-DOT-ASSET] ffprobe duration={probe.Duration:F3}s fps={probe.Fps:F3} resolution={probe.Width}x{probe.Height} pix_fmt={probe.PixelFormat}");
            return assetPath;
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

        private static string ResolvePreferredDreamyDotAssetRoot()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string? parent = Directory.GetParent(baseDir)?.FullName;
            for (int i = 0; i < 6 && !string.IsNullOrWhiteSpace(parent); i++)
            {
                if (File.Exists(Path.Combine(parent, "TitanEngine.csproj")))
                    return Path.Combine(parent, "Assets", "Overlays", "DreamyDots");

                parent = Directory.GetParent(parent)?.FullName;
            }

            string currentDir = Environment.CurrentDirectory;
            if (File.Exists(Path.Combine(currentDir, "TitanEngine.csproj")))
                return Path.Combine(currentDir, "Assets", "Overlays", "DreamyDots");

            return Path.Combine(baseDir, "Assets", "Overlays", "DreamyDots");
        }

        public static async Task<bool> UpscaleWithNgxDlssAsync(string inputPath, string outputPath, string mode, Action<string> onLog, CancellationToken token)
        {
            RefreshNvidiaUpscaleBackendPaths();
            bool hasDlvsrExe = !string.IsNullOrWhiteSpace(_ngxDlvsrPath) && File.Exists(_ngxDlvsrPath);
            bool hasVfxSampleExe = !string.IsNullOrWhiteSpace(_vfxUpscaleExePath) && File.Exists(_vfxUpscaleExePath);
            if (!hasDlvsrExe && !hasVfxSampleExe)
            {
                onLog($"[DLSS-NGX] DLVSR.exe not found at: {GetNgxDlssExpectedPath()}");
                onLog("[DLSS-NGX] UpscalePipelineApp backend is also missing.");
                return false;
            }

            if (!File.Exists(inputPath))
            {
                onLog($"[DLSS-NGX] Input file not found: {inputPath}");
                return false;
            }

            string? outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDir))
                Directory.CreateDirectory(outputDir);

            async Task<bool> RunVfxUpscaleSampleAsync(string inPath, string outPath, int factor, string passLabel)
            {
                string? exePath = _vfxUpscaleExePath;
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                    return false;

                string? modelDir = _vfxModelDir;
                if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir))
                {
                    string? detectedRoot = ResolveVfxSdkRoot(onLog);
                    if (!string.IsNullOrWhiteSpace(detectedRoot))
                    {
                        string candidateModelDir = Path.Combine(detectedRoot, "bin", "models");
                        if (Directory.Exists(candidateModelDir))
                            modelDir = candidateModelDir;
                    }
                }

                if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir))
                {
                    onLog("[DLSS-NGX] UpscalePipelineApp found but model_dir is missing.");
                    onLog("[DLSS-NGX] Install VFX SDK feature 'nvvfxupscale' and retry.");
                    return false;
                }

                (int srcW, int srcH) = await GetVideoResolutionAsync(inPath);
                int targetH = Math.Max(2, srcH * factor);
                if ((targetH & 1) != 0) targetH += 1;

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"--model_dir=\"{modelDir}\" --in_file=\"{inPath}\" --out_file=\"{outPath}\" --resolution={targetH} --upscale_strength=0.4 --progress --codec=avc1",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath)
                };

                if (!string.IsNullOrWhiteSpace(_vfxRuntimePathPrefix))
                {
                    string existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                    psi.Environment["PATH"] = $"{_vfxRuntimePathPrefix};{existingPath}";
                }

                onLog($"[DLSS-NGX-{passLabel}] {psi.FileName} {psi.Arguments}");

                using var process = new Process { StartInfo = psi };
                var stdOut = new StringBuilder();
                var stdErr = new StringBuilder();

                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        stdOut.AppendLine(e.Data);
                        onLog($"[DLSS-NGX-{passLabel}] {e.Data}");
                    }
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        stdErr.AppendLine(e.Data);
                        onLog($"[DLSS-NGX-{passLabel}-ERR] {e.Data}");
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
                    return false;
                }

                if (process.ExitCode != 0)
                {
                    string summary = string.Join(" | ",
                        (stdErr.Length > 0 ? stdErr.ToString() : stdOut.ToString())
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Take(8));
                    onLog($"[DLSS-NGX-{passLabel}-FAILED] ExitCode={process.ExitCode} {summary}");
                    return false;
                }

                if (!File.Exists(outPath) || new FileInfo(outPath).Length <= 0)
                {
                    onLog($"[DLSS-NGX-{passLabel}-FAILED] Output not created: {outPath}");
                    return false;
                }

                return true;
            }

            async Task<bool> RunDlssPassAsync(string inPath, string outPath, int factor, string passLabel)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ngxDlvsrPath,
                    Arguments = $"--input \"{inPath}\" --factor {factor} --output \"{outPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(_ngxDlvsrPath)
                };

                onLog($"[DLSS-NGX-{passLabel}] {psi.FileName} {psi.Arguments}");

                using var process = new Process { StartInfo = psi };
                var stdOut = new StringBuilder();
                var stdErr = new StringBuilder();

                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        stdOut.AppendLine(e.Data);
                        onLog($"[DLSS-NGX-{passLabel}] {e.Data}");
                    }
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        stdErr.AppendLine(e.Data);
                        onLog($"[DLSS-NGX-{passLabel}-ERR] {e.Data}");
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
                    return false;
                }

                if (process.ExitCode != 0)
                {
                    string summary = string.Join(" | ",
                        (stdErr.Length > 0 ? stdErr.ToString() : stdOut.ToString())
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Take(8));
                    onLog($"[DLSS-NGX-{passLabel}-FAILED] ExitCode={process.ExitCode} {summary}");
                    return false;
                }

                if (!File.Exists(outPath) || new FileInfo(outPath).Length <= 0)
                {
                    onLog($"[DLSS-NGX-{passLabel}-FAILED] Output not created: {outPath}");
                    return false;
                }

                return true;
            }

            try
            {
                bool useTwoPass4x = mode.Contains("4x", StringComparison.OrdinalIgnoreCase);
                if (!useTwoPass4x)
                {
                    onLog(hasDlvsrExe ? "[DLSS-NGX] Starting native DLVSR 2x" : "[DLSS-NGX] Starting VFX UpscalePipeline 2x");
                    bool ok2x = hasDlvsrExe
                        ? await RunDlssPassAsync(inputPath, outputPath, 2, "2X")
                        : await RunVfxUpscaleSampleAsync(inputPath, outputPath, 2, "2X");
                    if (ok2x)
                        onLog($"[DLSS-NGX] Success: {Path.GetFileName(outputPath)}");
                    return ok2x;
                }

                string temp2xPath = Path.Combine(
                    outputDir ?? AppDomain.CurrentDomain.BaseDirectory,
                    $"{Path.GetFileNameWithoutExtension(outputPath)}_dlss2x_tmp.mp4");

                TryDeleteFileSafe(temp2xPath);
                TryDeleteFileSafe(outputPath);

                if (hasDlvsrExe)
                {
                    onLog("[DLSS-NGX] 4x requested -> executing two DLSS 2x passes");
                    bool pass1 = await RunDlssPassAsync(inputPath, temp2xPath, 2, "PASS1");
                    if (!pass1)
                        return false;

                    bool pass2 = await RunDlssPassAsync(temp2xPath, outputPath, 2, "PASS2");
                    TryDeleteFileSafe(temp2xPath);

                    if (pass2)
                        onLog($"[DLSS-NGX] Success 4x (2-pass): {Path.GetFileName(outputPath)}");
                    return pass2;
                }

                onLog("[DLSS-NGX] 4x requested -> executing VFX UpscalePipeline 4x");
                bool ok4x = await RunVfxUpscaleSampleAsync(inputPath, outputPath, 4, "4X");
                TryDeleteFileSafe(temp2xPath);
                if (ok4x)
                    onLog($"[DLSS-NGX] Success 4x: {Path.GetFileName(outputPath)}");
                return ok4x;
            }
            catch (Exception ex)
            {
                onLog($"[DLSS-NGX-EXCEPTION] {ex.Message}");
                return false;
            }
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

        private static double NormalizePlaybackSpeed(double speed)
        {
            if (double.IsNaN(speed) || double.IsInfinity(speed) || speed <= 0.0)
                return 1.0;

            return Math.Clamp(speed, 0.10, 8.0);
        }

        private static string BuildPlaybackSpeedVideoFilter(double playbackSpeed, Action<string> onLog)
        {
            var invCulture = System.Globalization.CultureInfo.InvariantCulture;
            double ptsMultiplier = 1.0 / playbackSpeed;
            onLog($"[FILTER-SLOWMOTION] Playback speed {playbackSpeed:F4}x (fast mode: setpts only)");
            return $"setpts=(PTS-STARTPTS)*{ptsMultiplier.ToString("F4", invCulture)}";
        }

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

        private static string FfmpegDouble(double value, int decimals = 4)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                value = 0.0;

            return value.ToString($"F{decimals}", System.Globalization.CultureInfo.InvariantCulture);
        }

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

        private static string? ResolveBrandLogoAssetPath(Action<string> onLog)
        {
            string bundledPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BundledBrandLogoRelativePath);
            if (File.Exists(bundledPath))
            {
                TrySanitizeBrandLogoAsset(bundledPath, bundledPath, onLog);
                return bundledPath;
            }

            try
            {
                if (File.Exists(SeedBrandLogoPath))
                {
                    string? targetDir = Path.GetDirectoryName(bundledPath);
                    if (!string.IsNullOrWhiteSpace(targetDir))
                        Directory.CreateDirectory(targetDir);

                    if (TrySanitizeBrandLogoAsset(SeedBrandLogoPath, bundledPath, onLog))
                        return bundledPath;

                    File.Copy(SeedBrandLogoPath, bundledPath, overwrite: true);
                    onLog($"[BRAND-LOGO] Installed bundled logo asset without sanitizing: {bundledPath}");
                    return bundledPath;
                }
            }
            catch (Exception ex)
            {
                onLog($"[BRAND-LOGO-WARN] Failed to copy bundled asset: {ex.Message}");
            }

            onLog($"[BRAND-LOGO-WARN] Asset not found. Expected: {bundledPath}");
            return null;
        }

        private static bool TrySanitizeBrandLogoAsset(string sourcePath, string destinationPath, Action<string> onLog)
        {
            try
            {
                using FileStream stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapFrame? frame = decoder.Frames.FirstOrDefault();
                if (frame == null || frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
                    return false;

                BitmapSource rgbaSource = frame.Format == PixelFormats.Pbgra32
                    ? frame
                    : new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0.0);

                int width = rgbaSource.PixelWidth;
                int height = rgbaSource.PixelHeight;
                int stride = width * 4;
                byte[] pixels = new byte[stride * height];
                rgbaSource.CopyPixels(pixels, stride, 0);

                const byte alphaThreshold = 5;
                bool hiddenRgbDetected = false;

                for (int y = 0; y < height; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = row + (x * 4);
                        byte b = pixels[idx];
                        byte g = pixels[idx + 1];
                        byte r = pixels[idx + 2];
                        byte a = pixels[idx + 3];

                        if (a < alphaThreshold)
                        {
                            if (r != 0 || g != 0 || b != 0)
                                hiddenRgbDetected = true;

                            pixels[idx] = 0;
                            pixels[idx + 1] = 0;
                            pixels[idx + 2] = 0;
                            pixels[idx + 3] = 0;
                        }
                    }
                }

                string fullSource = Path.GetFullPath(sourcePath);
                string fullDestination = Path.GetFullPath(destinationPath);
                bool sameFile = string.Equals(fullSource, fullDestination, StringComparison.OrdinalIgnoreCase);
                bool needsRewrite = !sameFile || hiddenRgbDetected;
                if (!needsRewrite)
                    return true;

                double dpiX = frame.DpiX > 0 ? frame.DpiX : 96.0;
                double dpiY = frame.DpiY > 0 ? frame.DpiY : 96.0;
                BitmapSource sanitized = BitmapSource.Create(
                    width,
                    height,
                    dpiX,
                    dpiY,
                    PixelFormats.Pbgra32,
                    null,
                    pixels,
                    stride);
                if (sanitized.CanFreeze)
                    sanitized.Freeze();

                string? targetDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(targetDir))
                    Directory.CreateDirectory(targetDir);

                string tempPath = sameFile ? destinationPath + ".sanitized.tmp" : destinationPath;
                using (FileStream output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(sanitized));
                    encoder.Save(output);
                }

                if (sameFile)
                {
                    File.Copy(tempPath, destinationPath, overwrite: true);
                    File.Delete(tempPath);
                }

                onLog($"[BRAND-LOGO] Sanitized asset alpha-only cleanup: {destinationPath}");
                return true;
            }
            catch (Exception ex)
            {
                onLog($"[BRAND-LOGO-WARN] Failed to sanitize asset '{sourcePath}': {ex.Message}");
                return false;
            }
        }

        private static (int CanvasWidth, int CanvasHeight, Rect FrameRect) ResolveBrandLogoFrameReference(RenderJob job)
        {
            int requestedWidth = job.TargetWidth > 0 ? job.TargetWidth : job.SourceWidth;
            int requestedHeight = job.TargetHeight > 0 ? job.TargetHeight : job.SourceHeight;
            var overlayProfile = ResolveOverlayCanvasProfile(job, requestedWidth, requestedHeight);
            OverlayContentLayout layout = overlayProfile.Layout;

            double minDim = Math.Max(180.0, Math.Min(layout.ContentWidth, layout.ContentHeight));
            double inset = Math.Clamp(minDim * 0.034, 26.0, 42.0);
            Rect frameRect = new Rect(
                layout.ContentX + inset,
                layout.ContentY + inset,
                Math.Max(24.0, layout.ContentWidth - (inset * 2.0)),
                Math.Max(24.0, layout.ContentHeight - (inset * 2.0)));

            return (overlayProfile.CanvasWidth, overlayProfile.CanvasHeight, frameRect);
        }

        public static string NormalizeBrandLogoPosition(string? input)
        {
            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(v))
                return "Top Right";

            if (v is "top left" or "topleft" or "left top" or "tl" or "upper left")
                return "Top Left";
            if (v is "top right" or "topright" or "right top" or "tr" or "upper right")
                return "Top Right";
            if (v is "center top" or "top center" or "topcenter" or "ct" or "tc" or "upper center")
                return "Center Top";
            if (v is "bottom left" or "bottomleft" or "left bottom" or "bl" or "lower left")
                return "Bottom Left";
            if (v is "bottom right" or "bottomright" or "right bottom" or "br" or "lower right")
                return "Bottom Right";
            if (v is "center bottom" or "bottom center" or "bottomcenter" or "cb" or "bc" or "lower center")
                return "Center Bottom";

            return "Top Right";
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
            Rect frameRect = reference.FrameRect;
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

        private static double ResolveBrandLogoAspectRatio()
        {
            string bundledPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BundledBrandLogoRelativePath);
            var bundled = TryGetStillImageResolution(bundledPath);
            if (bundled.Width > 0 && bundled.Height > 0)
                return Math.Clamp((double)bundled.Width / bundled.Height, 0.25, 4.0);

            var seed = TryGetStillImageResolution(SeedBrandLogoPath);
            if (seed.Width > 0 && seed.Height > 0)
                return Math.Clamp((double)seed.Width / seed.Height, 0.25, 4.0);

            return 1024.0 / 1536.0;
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

        private static async Task<double> ResolveEffectiveImageTimelineTotalDurationAsync(
            IReadOnlyList<string> timelineInputs,
            RenderJob job,
            string? externalAudioPath,
            Action<string> onLog)
        {
            int imageCount = timelineInputs?.Count(IsImageSourcePath) ?? 0;
            if (imageCount <= 0)
                return 0.0;

            double defaultPerImage = ResolveImageTimelineSegmentDuration(0.0, 1);
            double requestedTotalDuration = job.ImageTimelineDurationSeconds > 0.001
                ? job.ImageTimelineDurationSeconds
                : defaultPerImage * imageCount;

            double effectiveDuration = requestedTotalDuration;
            double externalAudioDuration = await GetEstimatedExternalAudioOutputDurationSecondsAsync(job, externalAudioPath, null);
            if (externalAudioDuration > 0.01)
            {
                if (externalAudioDuration < effectiveDuration - 0.01)
                {
                    effectiveDuration = externalAudioDuration;
                    onLog($"[DURATION-SYNC] Image timeline capped to external audio: {effectiveDuration:F2}s");
                }
                else if (effectiveDuration < externalAudioDuration - 0.01)
                {
                    onLog($"[DURATION-SYNC] Image timeline shorter than external audio; keeping source duration: {effectiveDuration:F2}s");
                }
            }

            return Math.Max(0.10, effectiveDuration);
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

        private static bool HasCropEffect(RenderJob job)
        {
            return job.CropZoomPercent > 0.001 ||
                job.CropEnabled ||
                !string.IsNullOrWhiteSpace(job.CropAspectRatio) &&
                !job.CropAspectRatio.Equals("Free", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(job.FxEffect) && job.FxEffect.Contains("Crop", StringComparison.OrdinalIgnoreCase));
        }

        private static int FloorEvenDimension(double value)
        {
            int dimension = Math.Max(2, (int)Math.Floor(value));
            if ((dimension & 1) != 0)
                dimension -= 1;
            return Math.Max(2, dimension);
        }

        private static int EnsureEvenDimension(int value)
        {
            int dimension = Math.Max(2, value);
            if ((dimension & 1) != 0)
                dimension += 1;
            return dimension;
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

        private static bool ShouldUseFullPortraitCanvasForImageOverlays(RenderJob job, int canvasWidth, int canvasHeight)
        {
            if (canvasWidth <= 0 || canvasHeight <= 0 || canvasHeight <= canvasWidth)
                return false;

            IEnumerable<string> inputPaths = (job.MergeVideos && job.MergeInputPaths.Count > 0)
                ? job.MergeInputPaths
                : (string.IsNullOrWhiteSpace(job.SourcePath)
                    ? Array.Empty<string>()
                    : new[] { job.SourcePath });

            if (!inputPaths.Any() || !inputPaths.All(IsImageSourcePath))
                return false;

            if (job.TargetWidth == 1080 && job.TargetHeight == 1920)
                return true;

            return !string.IsNullOrWhiteSpace(job.Resolution) &&
                job.Resolution.Contains("Portrait Image Default", StringComparison.OrdinalIgnoreCase);
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

        private sealed class TemplateTimelineBuildResult
        {
            public string OutputPath { get; set; } = string.Empty;
            public double DurationSec { get; set; }
            public List<string> TempArtifacts { get; set; } = new List<string>();
        }

        private static bool IsImageSourcePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".bmp" or ".webp" or ".tif" or ".tiff" or ".gif";
        }

        private static bool ShouldUsePortraitHdDefaultForImages(IReadOnlyCollection<string>? inputPaths)
        {
            return inputPaths != null &&
                   inputPaths.Count > 0 &&
                   inputPaths.All(IsImageSourcePath);
        }

        public static double ResolveImageTimelineSegmentDuration(double totalImageTimelineSeconds, int imageCount)
        {
            if (imageCount <= 0)
                return 3.0;

            double total = Math.Max(0.0, totalImageTimelineSeconds);
            if (total > 0.001)
                return total / imageCount;

            return 3.0;
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

        private static string ResolveFxEffectName(string? input)
        {
            if (EffectRecipeLibrary.IsRetiredRecipeName(input))
                return "None";

            if (EffectRecipeLibrary.TryResolveCanonicalEffectName(input, out string canonicalRecipeName))
                return canonicalRecipeName;

            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(v) || v == "none")
                return "None";

            if (v.Contains("crop"))
                return "Crop Video (Custom Frame)";
            if (v.Contains("flip"))
                return "Flip Horizontal (Anti-Copyright)";
            if (v.Contains("light leak burn") || v.Contains("lightleakburn") || v.Contains("film burn") || v.Contains("burn transition") || v.Contains("light burn"))
                return "Light Leak Burn";
            if (v.Contains("snowfall") || v == "snow" || v.Contains("snow overlay") || v.Contains("snow soft") ||
                v.Contains("snow bokeh") || v.Contains("snow heavy") || v.Contains("snow windy") || v.Contains("snow cinematic"))
                return "Snowfall Overlay";
            if (v.Contains("film leak") || v.Contains("light leak") || v.Contains("leak 1") || v.Contains("rÃƒÂ² phim") || v.Contains("ro phim") || v.Contains("leak"))
                return "RÃƒÂ² phim (Leak 1 / Light Leak)";
            if (v.Contains("cinematic glow") || v.Contains("glow cinematic") || v == "glow")
                return "Cinematic Glow";
            if (v.Contains("halo glow") || v == "halo" || v.Contains("halo"))
                return "Halo Glow";
            if (v.Contains("prism light") || v == "prism" || v.Contains("prism"))
                return "Prism Light";
            if (v.Contains("editorial bloom") || v.Contains("bloom"))
                return "Editorial Bloom";
            if (v.Contains("dust & scratches") || v.Contains("dust and scratches") || v.Contains("dust scratches") ||
                v.Contains("old film scratches") || v.Contains("scratch video") || v == "scratch" ||
                v.Contains("film scratch") || v.Contains("video scratch"))
                return "Dust & Scratches";

            return "None";
        }

        private static string ResolveOverlayEffectName(string? input)
        {
            if (EffectRecipeLibrary.IsRetiredRecipeName(input))
                return "None";

            if (EffectRecipeLibrary.TryResolveCanonicalEffectName(input, out string canonicalRecipeName))
                return canonicalRecipeName;

            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(v) || v == "none")
                return "None";

            if (v.Contains("light leak overlay") || v.Contains("lightleakoverlay") || v.Contains("light leak pack") ||
                v.Contains("film burn overlay") || v.Contains("camera leak overlay") || v.Contains("overlay leak") ||
                v == "light leak" || v == "film leak")
                return "Light Leak Overlay";
            if (v.Contains("dreamydotoverlay2") || v.Contains("dreamy dot overlay 2") || v.Contains("dreamy dot 2") ||
                v.Contains("bokeh light overlay 2") || v.Contains("dreamy bokeh 2"))
                return "Dreamy Dot Overlay 2";
            if (v.Contains("dreamy dot chaos") || v.Contains("chaos dot") || v.Contains("random dot") ||
                v.Contains("dreamy chaos") || v.Contains("wandering dots"))
                return "Dreamy Dot Chaos Overlay";
            if (v.Contains("dreamy dot") || v.Contains("soft dot") || v.Contains("dot overlay") ||
                v.Contains("bokeh dot") || v.Contains("bokeh drift") || v.Contains("particle drift") ||
                v.Contains("floating dots") || v.Contains("blur dot"))
                return "Dreamy Dot Chaos Overlay";
            if (v.Contains("snowfall") || v == "snow" || v.Contains("snow overlay") || v.Contains("snow soft") ||
                v.Contains("snow bokeh") || v.Contains("snow heavy") || v.Contains("snow windy") || v.Contains("snow cinematic"))
                return "Snowfall Overlay";
            if (v.Contains("rain overlay") || v == "rain" || v.Contains("heavy rain") ||
                v.Contains("cinematic rain") || v.Contains("window drops") || v.Contains("heavyrainblackbg") ||
                v.Contains("cinematicrainblackbg") || v.Contains("windowdrops"))
                return "Rain Overlay";
            if (v.Contains("polaroid scrapbook 2") || v.Contains("polaroidscrapbook2") || v.Contains("rounded polaroid scrapbook") || v.Contains("rounded polaroid"))
                return "Polaroid Scrapbook 2";
            if (v.Contains("rgb polaroid") || v.Contains("polaroid rgb") || v.Contains("rgb border") || v.Contains("rainbow polaroid"))
                return "RGB Polaroid Border";
            if (v.Contains("dashed polaroid") || v.Contains("polaroid dashed") || v.Contains("dashed frame"))
                return "Dashed Polaroid Frame";
            if (v.Contains("dust & scratches") || v.Contains("dust and scratches") || v.Contains("dust scratches") ||
                v.Contains("old film scratches") || v.Contains("scratch video") || v == "scratch" ||
                v.Contains("film scratch") || v.Contains("video scratch"))
                return "Dust & Scratches";
            if (v.Contains("polaroid") || v.Contains("scrapbook") || v.Contains("photo album"))
                return "Polaroid Scrapbook";

            return "None";
        }

        private static string ResolveSnowfallPresetName(string? input)
        {
            string v = (input ?? string.Empty).Trim();
            return SnowfallOverlayPreset.NormalizePreset(v);
        }

        private static string NormalizeMusicSyncMode(string? input)
        {
            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (v == "beat" || v == "beatsync" || v == "beat-sync" || v == "sync")
                return "beat";
            return "off";
        }

        private static string ResolveFfmpegFontFile()
        {
            string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrWhiteSpace(windowsDir) || !Directory.Exists(windowsDir))
                return string.Empty;

            string[] candidates =
            {
                "arial.ttf",
                "segoeui.ttf",
                "tahoma.ttf",
                "calibri.ttf"
            };

            foreach (string candidate in candidates)
            {
                string candidatePath = Path.Combine(windowsDir, "Fonts", candidate);
                if (File.Exists(candidatePath))
                    return candidatePath.Replace("\\", "/").Replace(":", "\\:");
            }

            return string.Empty;
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

        // ═══════════════════════════════════════════════════════════════════
        // CAPTION SYNC: Whisper AI Transcription + SRT Burn
        // ═══════════════════════════════════════════════════════════════════
        private static async Task<string> GenerateCaptionSyncAssAsync(
            RenderJob job, string audioPath,
            CancellationToken token, Action<double> onProgress, Action<string> onLog)
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string whisperExe = Path.Combine(appDir, "Assets", "Whisper", "whisper-cli.exe");
            
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

        private static double NormalizeCoordinatePercent(double inputVal, double fallbackDefault)
        {
            if (double.IsNaN(inputVal) || double.IsInfinity(inputVal)) return fallbackDefault / 100.0;
            if (inputVal < 0) return 0.0;
            if (inputVal > 0.0 && inputVal <= 1.0) return inputVal;
            if (inputVal <= 100.0) return inputVal / 100.0;
            return Math.Clamp(inputVal / 100.0, 0.0, 1.0);
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

        // ═══════════════════════════════════════════════════════════════════
        // TEXT OVERLAY: Animation effects ported from CAPTION SYNC
        // ═══════════════════════════════════════════════════════════════════
        // Caption Sync burns timed text through an ASS subtitle track, so its
        // effects (Spotify Scroll, Pop & Bounce, Smooth Drift, Glow Pulse,
        // Karaoke Fade, Typewriter, Blur Reveal) are driven by the event
        // timeline. The Text Overlay has no timeline, so the very same effects
        // are rebuilt here purely on top of drawtext expressions that read the
        // presentation timestamp `t` (x / y / alpha / fontsize / borderw /
        // enable all accept FFmpeg expressions).
        //
        // Timing model: line `i` of the text block owns the slot
        // [i * slot, (i + 1) * slot] where slot = effectDuration / lineCount,
        // so the whole effect always completes inside `effectDuration` seconds
        // and the text then holds on screen until the end of the video.
        //
        // Per-character / per-word effects need one drawtext per revealed unit
        // (the only way to reveal text progressively in drawtext). That grows
        // the filter string linearly, so each mode has a hard filter budget to
        // keep the Windows command line under its 32K limit.
        // ═══════════════════════════════════════════════════════════════════
        // The whole FFmpeg command line is limited to 32767 characters and the
        // text overlay only owns a slice of it, so progressive-reveal modes are
        // capped. Blur Reveal is the most expensive (two layers per character),
        // then Typewriter, then word-by-word Karaoke, then plain line reveal.
        // Sized so the generated filter string stays comfortably inside budget.
        private const int TextOverlayBlurRevealCharBudget = 40;
        private const int TextOverlayTypewriterCharBudget = 60;
        private const int TextOverlayKaraokeWordBudget = 45;
        private const double TextOverlayMinEffectDuration = 1.0;
        private const double TextOverlayMaxEffectDuration = 60.0;

        private static bool UsesLegacyDrawtextTextOverlay(string? animation)
        {
            if (string.IsNullOrWhiteSpace(animation)) return true;
            return animation.Contains("Drawtext", StringComparison.OrdinalIgnoreCase);
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

        private static string NormalizeDrawtextCoordinateExpression(string expr)
        {
            if (string.IsNullOrWhiteSpace(expr))
                return string.Empty;

            string normalized = expr.Trim();
            normalized = Regex.Replace(normalized, @"\biw\b", "w", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\bih\b", "h", RegexOptions.IgnoreCase);
            return normalized;
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

        private static async Task<(int Width, int Height, double Fps)> ResolveTemplateOutputProfileAsync(
            IReadOnlyList<string> inputPaths,
            RenderJob job)
        {
            int targetWidth = job.TargetWidth > 0 ? job.TargetWidth : job.SourceWidth;
            int targetHeight = job.TargetHeight > 0 ? job.TargetHeight : job.SourceHeight;

            if (targetWidth <= 0 || targetHeight <= 0)
            {
                string? firstVideo = inputPaths.FirstOrDefault(p => !IsImageSourcePath(p));
                if (!string.IsNullOrWhiteSpace(firstVideo))
                {
                    var res = await GetVideoResolutionAsync(firstVideo);
                    targetWidth = res.Width;
                    targetHeight = res.Height;
                }
            }

            if (targetWidth <= 0 || targetHeight <= 0)
            {
                targetWidth = 1920;
                targetHeight = 1080;
            }

            if ((targetWidth & 1) != 0) targetWidth += 1;
            if ((targetHeight & 1) != 0) targetHeight += 1;

            double targetFps = job.SourceFps;
            if (targetFps <= 0.1 || double.IsNaN(targetFps) || double.IsInfinity(targetFps))
            {
                string? firstVideo = inputPaths.FirstOrDefault(p => !IsImageSourcePath(p));
                if (!string.IsNullOrWhiteSpace(firstVideo))
                    targetFps = await GetVideoFrameRateAsync(firstVideo);
            }

            if (targetFps <= 0.1 || double.IsNaN(targetFps) || double.IsInfinity(targetFps))
                targetFps = 30.0;

            targetFps = Math.Clamp(targetFps, 10.0, 120.0);
            return (targetWidth, targetHeight, targetFps);
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

            string args;
            if (isImageInput)
            {
                args =
                    $"-y -loop 1 -t {dur} -i \"{inputPath}\" " +
                    $"-f lavfi -t {dur} -i anullsrc=channel_layout=stereo:sample_rate=48000 " +
                    "-map 0:v:0 -map 1:a:0 " +
                    $"-vf \"{fullVideoFilter}\" -r {fps} " +
                    "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p " +
                    "-c:a aac -b:a 192k -ar 48000 -ac 2 " +
                    "-shortest -movflags +faststart " +
                    $"\"{outputPath}\"";
            }
            else if (hasAudio)
            {
                args =
                    $"-y -i \"{inputPath}\" " +
                    $"-f lavfi -t {dur} -i anullsrc=channel_layout=stereo:sample_rate=48000 " +
                    "-filter_complex \"[0:a]aresample=48000,asetpts=PTS-STARTPTS[a0];[1:a]asetpts=PTS-STARTPTS[a1];[a0][a1]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[aout]\" " +
                    "-map 0:v:0 -map \"[aout]\" " +
                    $"-vf \"{fullVideoFilter}\" -r {fps} " +
                    "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p " +
                    "-c:a aac -b:a 192k -ar 48000 -ac 2 " +
                    "-shortest -movflags +faststart " +
                    $"\"{outputPath}\"";
            }
            else
            {
                args =
                    $"-y -i \"{inputPath}\" " +
                    $"-f lavfi -t {dur} -i anullsrc=channel_layout=stereo:sample_rate=48000 " +
                    "-map 0:v:0 -map 1:a:0 " +
                    $"-vf \"{fullVideoFilter}\" -r {fps} " +
                    "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p " +
                    "-c:a aac -b:a 192k -ar 48000 -ac 2 " +
                    "-shortest -movflags +faststart " +
                    $"\"{outputPath}\"";
            }

            var (exitCode, stderr) = await RunFfmpegCaptureAsync(args, token);
            if (exitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
            {
                string summary = string.Join(" | ",
                    (stderr ?? string.Empty)
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Take(8));
                throw new Exception($"Template segment render failed: {summary}");
            }

            onLog($"[TEMPLATE] Segment ready: {Path.GetFileName(outputPath)}");
            return outputPath;
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
                    "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p " +
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
                    string transArgs = $"-y {inputSb.ToString()}-filter_complex \"{graph}\" -map \"{vOutTag}\" -c:v libx264 -preset veryfast -crf 20 {audioMap} -movflags +faststart \"{mergedSourcePath}\"";

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
                    $"-y -i \"{inputPath}\" " +
                    "-map 0:v:0 -map 0:a:0 " +
                    $"-vf \"{videoFilter}\" " +
                    "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p " +
                    "-c:a aac -b:a 192k -ar 48000 -ac 2 " +
                    "-movflags +faststart " +
                    $"\"{normalizedPath}\"";
            }
            else
            {
                args =
                    $"-y -i \"{inputPath}\" " +
                    "-f lavfi -i anullsrc=channel_layout=stereo:sample_rate=48000 " +
                    "-map 0:v:0 -map 1:a:0 " +
                    $"-vf \"{videoFilter}\" " +
                    "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p " +
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

        private static string BuildConcatListPathToken(string inputPath)
        {
            string full = Path.GetFullPath(inputPath).Replace('\\', '/');
            return full.Replace("'", "\\'");
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

        // Generates a synthetic hall impulse response WAV (exponential-noise decay)
        private static void WriteSyntheticIr(string path, double durationSec, int sampleRate, double decay)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? GetTitanTempDir());
            int n = (int)(sampleRate * durationSec);
            var rnd = new Random(0x51);
            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);
            void W(string s) => bw.Write(Encoding.ASCII.GetBytes(s));
            W("RIFF"); bw.Write(36 + n * 2); W("WAVE"); W("fmt ");
            bw.Write(16); bw.Write((short)1); bw.Write((short)1);
            bw.Write(sampleRate); bw.Write(sampleRate * 2); bw.Write((short)2); bw.Write((short)16);
            W("data"); bw.Write(n * 2);
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)sampleRate;
                double env = Math.Exp(-decay * t);
                double attack = Math.Min(1.0, t / 0.012);
                double noise = rnd.NextDouble() * 2.0 - 1.0;
                bw.Write((short)(noise * env * attack * 11000));
            }
        }

        // Applies the IR-convolution reverb effects (Echo / Church) to the external
        // Audio 1 track in a pre-pass, replacing it with a temp file.
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

            string irDuration = isChurch ? "3.0" : "1.6";
            string irPath = Path.Combine(GetTitanTempDir(), $"fx_ir_{Path.GetFileNameWithoutExtension(inputAudioPath)}.wav");
            WriteSyntheticIr(irPath, isChurch ? 3.0 : 1.6, 44100, isChurch ? 2.2 : 3.2);

            string fxAudioPath = Path.Combine(GetTitanTempDir(), $"fx_{safeTag}.m4a");
            int maxWet = isChurch ? 2 : 1;
            double k = intensity;
            double wet = Math.Clamp(maxWet * k, 0, 10);
            // Dry stays at full level and only the reverb tail (wet) is scaled,
            // so intensity actually changes how much reverb is heard.
            string wetStr = wet.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            string fm = $"-i \"{inputAudioPath}\" -i \"{irPath}\" -filter_complex \"[0:a][1:a]afir=length=1:dry=1:wet={wetStr},alimiter=limit=0.95[a]\" -map \"[a]\" -vn -c:a aac -b:a 192k -movflags +faststart \"{fxAudioPath}\"";
            string args = $"-y " + fm;

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

        // Preserve the source orientation. "Original" should stay original.
        public static (int Width, int Height) AutoOrientResolution(int width, int height)
        {
            return (width, height);
        }
    }

    #endregion

    #region --- [MAIN WINDOW] ---

    public partial class MainWindow : Window
    {
        private const string SupportedMediaDialogFilter =
            "Media (Video/Image)|*.mp4;*.mov;*.avi;*.mkv;*.webm;*.m4v;*.wmv;*.mpg;*.mpeg;*.ts;*.m2ts;*.mts;*.3gp;*.flv;*.f4v;*.vob;*.ogv;*.gif;*.png;*.jpg;*.jpeg;*.jpe;*.jfif;*.bmp;*.webp;*.tif;*.tiff|" +
            "Video|*.mp4;*.mov;*.avi;*.mkv;*.webm;*.m4v;*.wmv;*.mpg;*.mpeg;*.ts;*.m2ts;*.mts;*.3gp;*.flv;*.f4v;*.vob;*.ogv|" +
            "Image|*.png;*.jpg;*.jpeg;*.jpe;*.jfif;*.bmp;*.webp;*.tif;*.tiff;*.gif|" +
            "All Files|*.*";

        public ObservableCollection<RenderJob> JobQueue { get; set; }
        private CancellationTokenSource? _cancellationTokenSource;
        private bool _isBatchRunning = false;
        private readonly object _logLock = new object();

        private object? _originalStartBtnContent;
        private Brush? _originalStartBtnBackground;
        private Brush? _originalStartBtnBorder;

        private readonly string _appVersion = "v106.0-CAS-UPSCALE";

        private List<string> _listVideoPaths = new List<string>();
        private List<string> _listAudioPaths = new List<string>();
        private string? _secondaryAudioPath = null;
        private List<AudioEditSegment> _audioEditSegments = new List<AudioEditSegment>();
        private string? _watermarkPath = null;
        private string _customOutputFolder = "";

        private double _watermarkScale = 1.0;
        private double _watermarkRotation = 0.0;
        private double _watermarkOpacity = 1.0;
        private double _watermarkXPercent = 0.05;
        private double _watermarkYPercent = 0.05;
        private double _watermarkAspectRatio = 1.0;

        private string _upscaleMode = "Off";

        private double _slowMotionSpeed = 1.0;
        private bool _slowMotionAudio = true;

        private double _fxStartPercent = 0.0;
        private double _fxEndPercent = 100.0;
        private bool _enableFade = false;
        private double _fadeInSeconds = 0.8;
        private double _fadeOutSeconds = 0.8;

        private bool _isWatermarkDragging = false;
        private Point _watermarkDragStart = new Point();
        private CancellationTokenSource? _watermarkPreviewFrameCts;
        private bool _isCropDragging = false;
        private Point _cropDragStart = new Point();
        private double _cropXPercent = 0.0;
        private double _cropYPercent = 0.0;
        private double _cropWidthPercent = 1.0;
        private double _cropHeightPercent = 1.0;
        private string? _cropPreviewVideoPath;
        private double _cropPreviewDurationSec = 0.0;
        private CancellationTokenSource? _cropPreviewFrameCts;
        private bool _isUpdatingCropTimelineUi = false;
        private bool _cropPreviewMediaReady = false;

        private bool _isHostModeEnabled = false;
        private HttpListener? _localApiListener;
        private CancellationTokenSource? _localApiCts;
        private Task? _localApiLoopTask;
        private readonly SemaphoreSlim _localApiRenderLock = new SemaphoreSlim(1, 1);
        private readonly object _apiJobStateLock = new object();
        private readonly Dictionary<string, ApiRenderJobState> _apiJobStates = new Dictionary<string, ApiRenderJobState>(StringComparer.OrdinalIgnoreCase);

        private string LocalApiPrefix = "http://127.0.0.1:5555/";
        private const string LocalApiRenderPath = "/api/render";
        private const string LocalApiRenderAsyncPath = "/api/render/async";
        private const string LocalApiRenderWaitPath = "/api/render/wait";
        private const string LocalApiRenderStatusPath = "/api/render/status";
        private const string LocalApiQueueAddPath = "/api/queue/add";
        private const string LocalApiQueueStartPath = "/api/queue/start";
        private static readonly byte[] BlockingApiHeartbeatBytes = Encoding.UTF8.GetBytes(" \n");

        private readonly JsonSerializerOptions _localApiJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        public MainWindow()
        {
            InitializeComponent();
            JobQueue = new ObservableCollection<RenderJob>();
            lstQueue.ItemsSource = JobQueue;
            this.Title = $"TITAN ENGINE {_appVersion}";
            this.TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo();
            this.Loaded += OnSystemLoaded;
            this.Drop += OnWindowDrop;
            this.Closed += OnMainWindowClosed;
            this.AllowDrop = true;

            if (cmbFxType != null) cmbFxType.SelectedIndex = 0;
            if (cmbTemplateStyle != null) cmbTemplateStyle.SelectedIndex = 0;
            if (cmbBorderType != null) cmbBorderType.SelectedIndex = 0;
            if (cmbColorFilter != null) cmbColorFilter.SelectedIndex = 0;
            if (cmbUpscale != null) cmbUpscale.SelectedIndex = 0;
            if (cmbSlowMotion != null) cmbSlowMotion.SelectedIndex = 2;
            if (chkSlowMotionAudio != null) chkSlowMotionAudio.IsChecked = true;
            if (chkEnableFade != null) chkEnableFade.IsChecked = false;
            if (txtFadeInSeconds != null) txtFadeInSeconds.Text = "0.8";
            if (txtFadeOutSeconds != null) txtFadeOutSeconds.Text = "0.8";
            if (sldVideoBrightness != null) sldVideoBrightness.Value = 0.0;
            if (sldSourceAudioSpeed != null) sldSourceAudioSpeed.Value = 1.0;
            if (sldExternalAudioSpeed != null) sldExternalAudioSpeed.Value = 1.0;
            if (chkSnowOverlay1 != null) chkSnowOverlay1.IsChecked = false;
            if (chkSnowOverlay2 != null) chkSnowOverlay2.IsChecked = false;
            if (chkSnowOverlay3 != null) chkSnowOverlay3.IsChecked = false;
            if (chkOverlayIntro != null) chkOverlayIntro.IsChecked = false;
            UpdateAudioEditSummary();
            UpdateSnowfallOpacityUi();

            UpdateHostModeButtonState();
        }

        private static bool TryParseFlexibleDouble(string? input, out double value)
        {
            value = 0.0;

            if (string.IsNullOrWhiteSpace(input))
                return false;

            string trimmed = input.Trim();

            return double.TryParse(trimmed, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out value) ||
                   double.TryParse(trimmed, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out value) ||
                   double.TryParse(trimmed.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private static bool TryExtractPlaybackSpeed(string? text, out double speed)
        {
            speed = 1.0;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            var match = Regex.Match(text, @"([0-9]+(?:[.,][0-9]+)?)\s*x", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return TryParseFlexibleDouble(match.Groups[1].Value, out speed) && speed > 0.0;
            }

            return TryParseFlexibleDouble(text, out speed) && speed > 0.0;
        }

        private static bool IsVideoSourcePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm" or ".m4v" or ".wmv" or ".mpg" or ".mpeg" or ".ts" or ".m2ts" or ".mts" or ".3gp" or ".flv" or ".f4v" or ".vob" or ".ogv";
        }

        private static bool IsImageSourcePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".bmp" or ".webp" or ".tif" or ".tiff" or ".gif";
        }

        private static bool ShouldUsePortraitHdDefaultForImages(IReadOnlyCollection<string>? inputPaths)
        {
            return inputPaths != null &&
                   inputPaths.Count > 0 &&
                   inputPaths.All(IsImageSourcePath);
        }

        private double ResolveSelectedPlaybackSpeed()
        {
            try
            {
                if (cmbSlowMotion?.SelectedItem is ComboBoxItem item)
                {
                    string content = item.Content?.ToString() ?? "1.0x";

                    if (content.Contains("Custom", StringComparison.OrdinalIgnoreCase))
                    {
                        if (txtSlowMotionCustom != null &&
                            TryParseFlexibleDouble(txtSlowMotionCustom.Text, out double customSpeed) &&
                            customSpeed > 0.0)
                        {
                            return Math.Clamp(customSpeed, 0.10, 8.0);
                        }

                        return 1.0;
                    }

                    if (TryExtractPlaybackSpeed(content, out double presetSpeed) && presetSpeed > 0.0)
                    {
                        return Math.Clamp(presetSpeed, 0.10, 8.0);
                    }
                }
            }
            catch { }

            return 1.0;
        }

        private (bool Enabled, double FadeIn, double FadeOut) ResolveFadeSettingsFromUi()
        {
            bool enabled = chkEnableFade?.IsChecked == true;

            double fadeIn = 0.8;
            double fadeOut = 0.8;

            if (txtFadeInSeconds != null && TryParseFlexibleDouble(txtFadeInSeconds.Text, out double parsedIn))
                fadeIn = parsedIn;

            if (txtFadeOutSeconds != null && TryParseFlexibleDouble(txtFadeOutSeconds.Text, out double parsedOut))
                fadeOut = parsedOut;

            fadeIn = Math.Clamp(fadeIn, 0.0, 60.0);
            fadeOut = Math.Clamp(fadeOut, 0.0, 60.0);

            _enableFade = enabled;
            _fadeInSeconds = fadeIn;
            _fadeOutSeconds = fadeOut;

            return (enabled, fadeIn, fadeOut);
        }

        private void OnSystemLoaded(object sender, RoutedEventArgs e)
        {
            LogSystem($"[BOOT] Titan Engine {_appVersion}");
            try
            {
                EngineCore.Initialize();
                LogSystem("[OK] FFmpeg Core Engine Loaded");
                StartLocalApiServer();

                // [NEW] Check GPU encoder support
                bool hasNvenc = EngineCore.HasNvencSupport();
                bool hasHevcNvenc = EngineCore.HasHevcNvencSupport();
                LogSystem($"[NVENC-CHECK] NVIDIA NVENC Support: {(hasNvenc ? "ÃƒÂ¢Ã…â€œÃ¢â‚¬Å“ YES" : "ÃƒÂ¢Ã…â€œÃ¢â‚¬â€ NO")}");
                LogSystem($"[NVENC-CHECK] HEVC NVENC Support: {(hasHevcNvenc ? "YES" : "NO")}");

                if (!hasNvenc)
                {
                    LogSystem("[WARNING] Your FFmpeg does NOT have NVIDIA NVENC support!");
                    LogSystem("[FIX] Get FFmpeg with NVENC: https://github.com/BtbN/FFmpeg-Builds/releases");
                    LogSystem("[FALLBACK] Will use CPU encoding (libx264) instead");
                }

                bool hasNgxDlss = EngineCore.HasNgxDlssSupport();
                LogSystem($"[DLSS-NGX] DLVSR bridge: {(hasNgxDlss ? "READY" : "NOT FOUND")}");
                LogSystem($"[DLSS-NGX] Backend: {EngineCore.GetNgxBackendInfo()}");
                if (!hasNgxDlss)
                {
                    LogSystem($"[DLSS-NGX] Put DLVSR.exe here: {EngineCore.GetNgxDlssExpectedPath()}");
                }

                LogSystem("[FEATURES] FSRCNNX AI Upscale + Custom Resolution + FX Timeline");
                LogSystem("[READY] Drop video or browse to start");
                LogSystem("[API] Host mode OFF (click HOST button to enable)");

                _ = this.Dispatcher.BeginInvoke(() =>
                {
                    UpdateFxTimelineVisualization();
                }, System.Windows.Threading.DispatcherPriority.Loaded);

                LogSystem("[DLSS-NGX] Auto setup prompt is shown only when an upscale mode is selected.");
            }
            catch (Exception ex)
            {
                LogSystem($"[FATAL] {ex.Message}");
            }
        }

        private async Task PromptDlvsrAutoDownloadIfMissingAsync(bool alreadyDetected)
        {
            try
            {
                if (alreadyDetected || EngineCore.HasNgxDlssSupport())
                    return;

                string expectedPath = EngineCore.GetNgxDlssExpectedPath();
                MessageBoxResult ask = MessageBox.Show(
                    "The NVIDIA upscale backend is missing.\n\nDo you want Titan Engine to auto-download and auto-build the NVIDIA VFX sample backend now?\n\n" +
                    "Note: NVIDIA provides public sample source, but final build still depends on local VFX SDK Core + nvvfxupscale installation.\n\n" +
                    $"Expected DLVSR path:\n{expectedPath}",
                    "DLSS NGX Setup",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (ask != MessageBoxResult.Yes)
                {
                    LogSystem("[DLSS-NGX] Auto-download skipped by user.");
                    return;
                }

                LogSystem("[DLSS-NGX] Auto-download accepted. Starting...");
                bool ok = await EngineCore.DownloadDlvsrSetupBundleAsync(LogSystem, CancellationToken.None);

                if (EngineCore.HasNgxDlssSupport())
                {
                    LogSystem("[DLSS-NGX] DLVSR bridge is now READY.");
                    MessageBox.Show("DLSS backend is ready.", "DLSS NGX", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (ok)
                {
                    string dlssDir = EngineCore.GetDlssDirectoryPath();
                    MessageBox.Show(
                        "Setup bundle downloaded.\n\n" +
                        "If backend is still not ready, open DLVSR_SETUP_README.txt or NVIDIA_VFX_AUTO_SETUP.ps1 in the dlss folder and complete VFX SDK feature install.",
                        "DLSS NGX",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = dlssDir,
                            UseShellExecute = true
                        });
                    }
                    catch { }
                }
                else
                {
                    MessageBox.Show(
                        "DLVSR setup download/build failed. Check logs for details.",
                        "DLSS NGX",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                LogSystem($"[DLSS-NGX] Auto-download flow failed: {ex.Message}");
            }
        }

        private void BtnBrowseVideo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = SupportedMediaDialogFilter,
                Multiselect = true
            };
            if (dlg.ShowDialog() == true)
            {
                _listVideoPaths = new List<string>(dlg.FileNames);
                txtVideoPath.Text = _listVideoPaths.Count > 1 ? $"<{_listVideoPaths.Count} files>" : System.IO.Path.GetFileName(_listVideoPaths[0]);
                int imageCount = _listVideoPaths.Count(IsImageSourcePath);
                int videoCount = _listVideoPaths.Count - imageCount;
                LogSystem($"[INPUT] {_listVideoPaths.Count} source file(s) ({videoCount} video, {imageCount} image)");
                UpdateRandomButtonVisibility();
            }
        }

        private void BtnBrowseAudio_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Audio|*.mp3;*.wav;*.aac|All|*.*", Multiselect = true };
            if (dlg.ShowDialog() == true)
            {
                _listAudioPaths = new List<string>(dlg.FileNames);
                _audioEditSegments.Clear();
                txtAudioPath.Text = _listAudioPaths.Count > 1 ? $"<{_listAudioPaths.Count} files>" : System.IO.Path.GetFileName(_listAudioPaths[0]);
                LogSystem($"[INPUT] {_listAudioPaths.Count} audio(s)");
                if (_listAudioPaths.Count > 1 && chkMergeAudios != null && chkMergeAudios.IsChecked != true)
                {
                    chkMergeAudios.IsChecked = true;
                    LogSystem("[AUDIO-MERGE] Auto-selected (multiple audio files).");
                }
                UpdateAudioEditSummary();
                UpdateRandomButtonVisibility();
            }
        }

        private void BtnBrowseAudio2_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Audio|*.mp3;*.wav;*.aac;*.m4a;*.flac;*.ogg|All|*.*",
                Multiselect = false
            };

            if (dlg.ShowDialog() == true)
            {
                _secondaryAudioPath = dlg.FileName;
                txtAudioPath2.Text = Path.GetFileName(_secondaryAudioPath);
                LogSystem($"[INPUT] Audio 2 overlay: {Path.GetFileName(_secondaryAudioPath)}");
            }
        }

        private async void BtnOpenAudioEditor_Click(object sender, RoutedEventArgs e)
        {
            if (_listAudioPaths.Count == 0)
            {
                MessageBox.Show("Select audio first!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string previewAudioPath = _listAudioPaths[0];
            string? tempConcatListPath = null;
            string? tempMergedAudioPath = null;
            bool cleanupMergedPreview = false;

            try
            {
                if (_listAudioPaths.Count > 1 && (chkMergeAudios?.IsChecked ?? false))
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "TitanEngine_AudioPreview");
                    Directory.CreateDirectory(tempDir);
                    string previewTag = $"preview_{Guid.NewGuid():N}";
                    (tempConcatListPath, tempMergedAudioPath) = await EngineCore.MergeAudiosToTempTrackAsync(_listAudioPaths, tempDir, previewTag, CancellationToken.None, LogSystem);
                    if (!string.IsNullOrWhiteSpace(tempMergedAudioPath) && File.Exists(tempMergedAudioPath))
                    {
                        previewAudioPath = tempMergedAudioPath;
                        cleanupMergedPreview = true;
                        LogSystem($"[AUDIO-EDITOR] Preview merged track ready: {Path.GetFileName(previewAudioPath)}");
                    }
                }
                else if (_listAudioPaths.Count > 1)
                {
                    LogSystem("[AUDIO-EDITOR] Multiple audio files selected. Opening editor for the first track preview.");
                }

                var meta = await EngineCore.AnalyzeMediaAsync(previewAudioPath);
                double durationSec = meta.Duration > 0.0 ? meta.Duration : 0.0;

                var editor = new AudioEditorWindow(previewAudioPath, durationSec, new List<AudioEditSegment>(_audioEditSegments))
                {
                    Owner = this
                };

                bool? dialogResult = editor.ShowDialog();
                if (dialogResult == true)
                {
                    _audioEditSegments = AudioEditHelpers.NormalizeSegments(editor.ResultSegments, durationSec);
                    UpdateAudioEditSummary();
                    LogSystem(_audioEditSegments.Count > 0
                        ? $"[AUDIO-EDITOR] Saved {_audioEditSegments.Count} cut segment(s)"
                        : "[AUDIO-EDITOR] Saved without cuts");
                }
            }
            catch (Exception ex)
            {
                LogSystem($"[AUDIO-EDITOR-ERROR] {ex.Message}");
                MessageBox.Show($"Audio editor failed: {ex.Message}", "Audio Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (cleanupMergedPreview)
                {
                    EngineCore.TryDeleteFileSafe(tempMergedAudioPath);
                    EngineCore.TryDeleteFileSafe(tempConcatListPath);
                }
            }
        }

        private void UpdateAudioEditSummary()
        {
            if (txtAudioEditSummary == null)
                return;

            if (_audioEditSegments.Count == 0)
            {
                txtAudioEditSummary.Text = "No audio cuts applied";
                return;
            }

            string preview = string.Join(" | ", _audioEditSegments
                .OrderBy(s => s.StartSeconds)
                .Take(2)
                .Select(s => s.DisplayLabel));

            if (_audioEditSegments.Count > 2)
                preview += $" | +{_audioEditSegments.Count - 2} more";

            txtAudioEditSummary.Text = $"Audio cuts: {_audioEditSegments.Count} segment(s) - {preview}";
        }

        private void BtnBrowseWatermark_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Image|*.png;*.jpg;*.jpeg|All|*.*" };
            if (dlg.ShowDialog() == true)
            {
                _watermarkPath = dlg.FileName;
                txtWatermarkPath.Text = System.IO.Path.GetFileName(_watermarkPath);
                LogSystem($"[WATERMARK] {System.IO.Path.GetFileName(_watermarkPath)}");

                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(_watermarkPath);
                    bmp.EndInit();

                    if (bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
                    {
                        _watermarkAspectRatio = (double)bmp.PixelWidth / bmp.PixelHeight;
                        LogSystem($"[WATERMARK] Aspect: {_watermarkAspectRatio:F3}:1");
                    }
                }
                catch (Exception ex)
                {
                    LogSystem($"[WATERMARK-ERROR] {ex.Message}");
                    _watermarkAspectRatio = 1.0;
                }
            }
        }

        private void BtnBrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            string initialDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            OpenFileDialog dlg = new OpenFileDialog
            {
                Title = "Select Output Folder",
                ValidateNames = false,
                CheckFileExists = false,
                CheckPathExists = true,
                FileName = "folder_select",
                InitialDirectory = initialDir
            };

            if (dlg.ShowDialog() == true)
            {
                string? selectedPath = System.IO.Path.GetDirectoryName(dlg.FileName);
                if (!string.IsNullOrEmpty(selectedPath) && Directory.Exists(selectedPath))
                {
                    _customOutputFolder = selectedPath;
                    string folderName = new DirectoryInfo(selectedPath).Name;
                    txtOutputDir.Text = folderName;
                    LogSystem($"[OUTPUT] {selectedPath}");
                }
            }
        }

        private async void BtnOpenWatermarkEditor_Click(object sender, RoutedEventArgs e)
        {
            if (_listVideoPaths.Count == 0)
            {
                MessageBox.Show("Select video first!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(_watermarkPath) || !File.Exists(_watermarkPath))
            {
                MessageBox.Show("Select watermark first!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            CancelWatermarkPreviewFrameLoading();
            gridWatermarkOverlay.Visibility = Visibility.Visible;
            LogSystem("[WATERMARK-EDITOR] Opened");

            await Task.Delay(80);

            try
            {
                string previewVideo = _listVideoPaths[0];
                var meta = await EngineCore.AnalyzeMediaDetailsAsync(previewVideo);
                int w = meta.Width > 0 ? meta.Width : 1920;
                int h = meta.Height > 0 ? meta.Height : 1080;
                if (w > 0 && h > 0)
                {
                    gridPreviewContainer.Width = w;
                    gridPreviewContainer.Height = h;
                    cvsWatermarkPreview.Width = w;
                    cvsWatermarkPreview.Height = h;
                    imgWatermarkVideoFrame.Width = w;
                    imgWatermarkVideoFrame.Height = h;
                    imgWatermarkVideoFrame.Source = null;

                    var logoBmp = new BitmapImage();
                    logoBmp.BeginInit();
                    logoBmp.CacheOption = BitmapCacheOption.OnLoad;
                    logoBmp.UriSource = new Uri(_watermarkPath);
                    logoBmp.EndInit();
                    logoBmp.Freeze();

                    imgWatermarkPreview.Source = logoBmp;
                    double logoAspect = logoBmp.PixelWidth > 0 && logoBmp.PixelHeight > 0
                        ? (double)logoBmp.PixelWidth / logoBmp.PixelHeight
                        : Math.Max(0.01, _watermarkAspectRatio);
                    _watermarkAspectRatio = logoAspect;

                    double baseLogoWidth = Math.Max(24.0, w * 0.20);
                    double baseLogoHeight = Math.Max(24.0, baseLogoWidth / Math.Max(0.01, logoAspect));
                    if (baseLogoHeight > h * 0.8)
                    {
                        baseLogoHeight = h * 0.8;
                        baseLogoWidth = baseLogoHeight * logoAspect;
                    }

                    imgWatermarkPreview.Width = baseLogoWidth;
                    imgWatermarkPreview.Height = baseLogoHeight;
                    ttWatermark.X = 0;
                    ttWatermark.Y = 0;

                    sldWmkScale.Value = Math.Clamp(_watermarkScale, sldWmkScale.Minimum, sldWmkScale.Maximum);
                    sldWmkRotate.Value = Math.Clamp(_watermarkRotation, sldWmkRotate.Minimum, sldWmkRotate.Maximum);
                    sldWmkOpacity.Value = Math.Clamp(_watermarkOpacity, sldWmkOpacity.Minimum, sldWmkOpacity.Maximum);
                    SldWmk_ValueChanged(sldWmkScale, new RoutedPropertyChangedEventArgs<double>(sldWmkScale.Value, sldWmkScale.Value));

                    double left = (Math.Clamp(_watermarkXPercent, 0.0, 1.0) * w) - (baseLogoWidth / 2.0);
                    double top = (Math.Clamp(_watermarkYPercent, 0.0, 1.0) * h) - (baseLogoHeight / 2.0);
                    left = Math.Clamp(left, -baseLogoWidth / 2.0, w - (baseLogoWidth / 2.0));
                    top = Math.Clamp(top, -baseLogoHeight / 2.0, h - (baseLogoHeight / 2.0));

                    Canvas.SetLeft(imgWatermarkPreview, left);
                    Canvas.SetTop(imgWatermarkPreview, top);

                    await RefreshWatermarkPreviewFrameAsync(previewVideo);

                    LogSystem($"[WATERMARK-EDITOR] Logo loaded on preview frame ({w}x{h})");
                }
            }
            catch (Exception ex)
            {
                LogSystem($"[WATERMARK-EDITOR] Error: {ex.Message}");
            }
        }

        private async Task RefreshWatermarkPreviewFrameAsync(string videoPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath) || imgWatermarkVideoFrame == null)
                return;

            CancelWatermarkPreviewFrameLoading();
            _watermarkPreviewFrameCts = new CancellationTokenSource();
            CancellationToken token = _watermarkPreviewFrameCts.Token;

            string? framePath = null;
            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "TitanEngine", "WatermarkPreview");
                Directory.CreateDirectory(tempDir);
                framePath = Path.Combine(tempDir, $"watermark_{System.Guid.NewGuid():N}.jpg");

                LogSystem("[WATERMARK-EDITOR] Loading video frame preview...");
                bool extracted = await EngineCore.ExtractFrameToImageAsync(videoPath, 0.0, framePath, token);

                if (!extracted || token.IsCancellationRequested || !File.Exists(framePath))
                {
                    if (!token.IsCancellationRequested)
                        LogSystem("[WATERMARK-EDITOR] Preview frame unavailable; using black background.");
                    return;
                }

                byte[] raw = await File.ReadAllBytesAsync(framePath, token);
                using var ms = new MemoryStream(raw);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();

                if (token.IsCancellationRequested)
                    return;

                await Dispatcher.InvokeAsync(() =>
                {
                    imgWatermarkVideoFrame.Source = bmp;
                });

                LogSystem("[WATERMARK-EDITOR] Preview frame loaded");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogSystem($"[WATERMARK-EDITOR] Preview error: {ex.Message}");
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(framePath))
                {
                    try { File.Delete(framePath); } catch { }
                }
            }
        }

        private void CancelWatermarkPreviewFrameLoading()
        {
            try { _watermarkPreviewFrameCts?.Cancel(); } catch { }
            try { _watermarkPreviewFrameCts?.Dispose(); } catch { }
            _watermarkPreviewFrameCts = null;
        }

        private void OnWindowDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            List<string> vids = new List<string>();
            List<string> auds = new List<string>();

            foreach (string f in files)
            {
                string ext = System.IO.Path.GetExtension(f).ToLower();
                if (IsVideoSourcePath(f) || IsImageSourcePath(f)) vids.Add(f);
                else if (new[] { ".mp3", ".wav", ".aac" }.Contains(ext)) auds.Add(f);
            }

            if (vids.Count > 0)
            {
                _listVideoPaths = vids;
                txtVideoPath.Text = vids.Count > 1 ? $"<{vids.Count} files>" : System.IO.Path.GetFileName(vids[0]);
                int imageCount = vids.Count(IsImageSourcePath);
                int videoCount = vids.Count - imageCount;
                LogSystem($"[INPUT] {vids.Count} source file(s) ({videoCount} video, {imageCount} image)");
            }
            if (auds.Count > 0)
            {
                _listAudioPaths = auds;
                txtAudioPath.Text = auds.Count > 1 ? $"<{auds.Count} files>" : System.IO.Path.GetFileName(auds[0]);
                _audioEditSegments.Clear();
                if (auds.Count > 1 && chkMergeAudios != null && chkMergeAudios.IsChecked != true)
                {
                    chkMergeAudios.IsChecked = true;
                    LogSystem("[AUDIO-MERGE] Auto-selected (multiple audio files).");
                }
                UpdateAudioEditSummary();
                UpdateRandomButtonVisibility();
            }
        }

        private void ImgWatermark_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isWatermarkDragging = true;
            _watermarkDragStart = e.GetPosition(cvsWatermarkPreview);
            (sender as UIElement)?.CaptureMouse();
        }

        private void ImgWatermark_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isWatermarkDragging || imgWatermarkPreview == null || cvsWatermarkPreview == null) return;

            Point currentPos = e.GetPosition(cvsWatermarkPreview);
            double deltaX = currentPos.X - _watermarkDragStart.X;
            double deltaY = currentPos.Y - _watermarkDragStart.Y;

            double newLeft = Canvas.GetLeft(imgWatermarkPreview) + deltaX;
            double newTop = Canvas.GetTop(imgWatermarkPreview) + deltaY;

            double visualWidth = imgWatermarkPreview.ActualWidth > 0 ? imgWatermarkPreview.ActualWidth : imgWatermarkPreview.Width;
            double visualHeight = imgWatermarkPreview.ActualHeight > 0 ? imgWatermarkPreview.ActualHeight : imgWatermarkPreview.Height;
            double imgHalfWidth = visualWidth / 2;
            double imgHalfHeight = visualHeight / 2;

            newLeft = Math.Max(-imgHalfWidth, Math.Min(newLeft, cvsWatermarkPreview.ActualWidth - imgHalfWidth));
            newTop = Math.Max(-imgHalfHeight, Math.Min(newTop, cvsWatermarkPreview.ActualHeight - imgHalfHeight));

            Canvas.SetLeft(imgWatermarkPreview, newLeft);
            Canvas.SetTop(imgWatermarkPreview, newTop);

            _watermarkDragStart = currentPos;
        }

        private void ImgWatermark_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isWatermarkDragging = false;
            (sender as UIElement)?.ReleaseMouseCapture();
        }

        private void SldWmk_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sldWmkScale != null && stWatermark != null)
            {
                stWatermark.ScaleX = sldWmkScale.Value;
                stWatermark.ScaleY = sldWmkScale.Value;
            }
            if (sldWmkRotate != null && rtWatermark != null)
            {
                rtWatermark.Angle = sldWmkRotate.Value;
            }
            if (sldWmkOpacity != null && imgWatermarkPreview != null)
            {
                imgWatermarkPreview.Opacity = sldWmkOpacity.Value;
            }
        }

        private void BtnCancelWatermark_Click(object sender, RoutedEventArgs e)
        {
            CancelWatermarkPreviewFrameLoading();
            gridWatermarkOverlay.Visibility = Visibility.Collapsed;
        }

        private void BtnSaveWatermark_Click(object sender, RoutedEventArgs e)
        {
            _watermarkScale = sldWmkScale?.Value ?? 1.0;
            _watermarkRotation = sldWmkRotate?.Value ?? 0.0;
            _watermarkOpacity = sldWmkOpacity?.Value ?? 1.0;

            if (imgWatermarkPreview != null && cvsWatermarkPreview != null)
            {
                double canvasWidth = cvsWatermarkPreview.ActualWidth;
                double canvasHeight = cvsWatermarkPreview.ActualHeight;
                double imgWidth = imgWatermarkPreview.ActualWidth > 0 ? imgWatermarkPreview.ActualWidth : imgWatermarkPreview.Width;
                double imgHeight = imgWatermarkPreview.ActualHeight > 0 ? imgWatermarkPreview.ActualHeight : imgWatermarkPreview.Height;

                if (canvasWidth > 0 && canvasHeight > 0 && imgWidth > 0 && imgHeight > 0)
                {
                    double imgLeft = Canvas.GetLeft(imgWatermarkPreview);
                    double imgTop = Canvas.GetTop(imgWatermarkPreview);

                    if (double.IsNaN(imgLeft)) imgLeft = 0;
                    if (double.IsNaN(imgTop)) imgTop = 0;

                    double imageCenterX = imgLeft + (imgWidth / 2);
                    double imageCenterY = imgTop + (imgHeight / 2);

                    _watermarkXPercent = Math.Clamp(imageCenterX / canvasWidth, 0, 1);
                    _watermarkYPercent = Math.Clamp(imageCenterY / canvasHeight, 0, 1);
                }
            }

            gridWatermarkOverlay.Visibility = Visibility.Collapsed;
            CancelWatermarkPreviewFrameLoading();
            LogSystem($"[WATERMARK] Saved (Scale: {_watermarkScale:F2}x)");
        }

        private string GetSelectedCropAspectRatioName()
        {
            return EngineCore.NormalizeCropAspectRatioName((cmbCropAspectRatio?.SelectedItem as ComboBoxItem)?.Content?.ToString());
        }

        private bool TryGetSelectedCropAspectRatioValue(out double ratio)
        {
            return EngineCore.TryGetCropAspectRatioValue(GetSelectedCropAspectRatioName(), out ratio);
        }

        private static Rect BuildAspectRatioRectWithinCanvas(
            double left,
            double top,
            double width,
            double height,
            double canvasWidth,
            double canvasHeight,
            double aspectRatio,
            double minSize = 40.0)
        {
            width = Math.Clamp(width, minSize, Math.Max(minSize, canvasWidth));
            height = Math.Clamp(height, minSize, Math.Max(minSize, canvasHeight));

            double centerX = left + (width / 2.0);
            double centerY = top + (height / 2.0);
            double currentRatio = width / Math.Max(0.0001, height);

            if (currentRatio > aspectRatio)
                width = height * aspectRatio;
            else
                height = width / aspectRatio;

            width = Math.Min(width, canvasWidth);
            height = Math.Min(height, canvasHeight);

            if ((width / Math.Max(0.0001, height)) > aspectRatio)
                width = height * aspectRatio;
            else
                height = width / aspectRatio;

            left = centerX - (width / 2.0);
            top = centerY - (height / 2.0);
            left = Math.Clamp(left, 0.0, Math.Max(0.0, canvasWidth - width));
            top = Math.Clamp(top, 0.0, Math.Max(0.0, canvasHeight - height));

            return new Rect(left, top, Math.Max(minSize, width), Math.Max(minSize, height));
        }

        private static Rect BuildAspectRatioResizeRect(
            string tag,
            double left,
            double top,
            double width,
            double height,
            double horizontalChange,
            double verticalChange,
            double canvasWidth,
            double canvasHeight,
            double aspectRatio,
            double minSize = 40.0)
        {
            bool dragLeft = tag is "TL" or "BL";
            bool dragTop = tag is "TL" or "TR";

            double anchorX = dragLeft ? left + width : left;
            double anchorY = dragTop ? top + height : top;
            double movingX = dragLeft ? left + horizontalChange : left + width + horizontalChange;
            double movingY = dragTop ? top + verticalChange : top + height + verticalChange;

            double rawWidth = Math.Max(minSize, Math.Abs(movingX - anchorX));
            double rawHeight = Math.Max(minSize, Math.Abs(movingY - anchorY));

            if ((rawWidth / Math.Max(0.0001, rawHeight)) > aspectRatio)
                rawHeight = rawWidth / aspectRatio;
            else
                rawWidth = rawHeight * aspectRatio;

            double maxWidth = dragLeft ? anchorX : (canvasWidth - anchorX);
            double maxHeight = dragTop ? anchorY : (canvasHeight - anchorY);
            maxWidth = Math.Max(minSize, maxWidth);
            maxHeight = Math.Max(minSize, maxHeight);

            rawWidth = Math.Min(rawWidth, maxWidth);
            rawHeight = Math.Min(rawHeight, maxHeight);

            if ((rawWidth / Math.Max(0.0001, rawHeight)) > aspectRatio)
                rawWidth = rawHeight * aspectRatio;
            else
                rawHeight = rawWidth / aspectRatio;

            if (rawWidth > maxWidth)
            {
                rawWidth = maxWidth;
                rawHeight = rawWidth / aspectRatio;
            }
            if (rawHeight > maxHeight)
            {
                rawHeight = maxHeight;
                rawWidth = rawHeight * aspectRatio;
            }

            double newLeft = dragLeft ? anchorX - rawWidth : anchorX;
            double newTop = dragTop ? anchorY - rawHeight : anchorY;
            newLeft = Math.Clamp(newLeft, 0.0, Math.Max(0.0, canvasWidth - rawWidth));
            newTop = Math.Clamp(newTop, 0.0, Math.Max(0.0, canvasHeight - rawHeight));

            return new Rect(newLeft, newTop, Math.Max(minSize, rawWidth), Math.Max(minSize, rawHeight));
        }

        private void SetCropFrameRect(Rect rect)
        {
            if (bdrCropFrame == null)
                return;

            bdrCropFrame.Width = rect.Width;
            bdrCropFrame.Height = rect.Height;
            Canvas.SetLeft(bdrCropFrame, rect.Left);
            Canvas.SetTop(bdrCropFrame, rect.Top);
        }

        private void ApplySelectedCropAspectRatioToEditor()
        {
            if (bdrCropFrame == null || cvsCropPreview == null)
                return;

            if (!TryGetSelectedCropAspectRatioValue(out double aspectRatio))
            {
                ClampCropFrameWithinCanvas();
                UpdateCropInfoText();
                return;
            }

            double left = Canvas.GetLeft(bdrCropFrame);
            double top = Canvas.GetTop(bdrCropFrame);
            if (double.IsNaN(left)) left = 0.0;
            if (double.IsNaN(top)) top = 0.0;

            Rect rect = BuildAspectRatioRectWithinCanvas(
                left,
                top,
                bdrCropFrame.Width,
                bdrCropFrame.Height,
                cvsCropPreview.Width,
                cvsCropPreview.Height,
                aspectRatio);

            SetCropFrameRect(rect);
            UpdateCropInfoText();
        }

        private void cmbCropAspectRatio_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (gridCropOverlay?.Visibility == Visibility.Visible)
                ApplySelectedCropAspectRatioToEditor();
        }

        private async void BtnOpenCropEditor_Click(object sender, RoutedEventArgs e)
        {
            if (_listVideoPaths.Count == 0)
            {
                MessageBox.Show("Select media first!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _cropPreviewVideoPath = _listVideoPaths[0];
            CancelCropPreviewFrameLoading();
            _cropPreviewMediaReady = false;
            gridCropOverlay.Visibility = Visibility.Visible;
            LogSystem("[CROP-EDITOR] Opened");

            await Task.Delay(80);

            try
            {
                var meta = await EngineCore.AnalyzeMediaDetailsAsync(_cropPreviewVideoPath);
                int w = meta.Width > 0 ? meta.Width : 1920;
                int h = meta.Height > 0 ? meta.Height : 1080;
                cvsCropPreview.Width = w;
                cvsCropPreview.Height = h;
                _cropPreviewDurationSec = meta.Duration > 0 ? meta.Duration : 1.0;

                if (medCropPreview != null)
                {
                    medCropPreview.Width = w;
                    medCropPreview.Height = h;
                    medCropPreview.Visibility = Visibility.Visible;
                }

                if (imgCropPreviewFrame != null)
                {
                    imgCropPreviewFrame.Width = w;
                    imgCropPreviewFrame.Height = h;
                }

                double canvasWidth = cvsCropPreview.Width;
                double canvasHeight = cvsCropPreview.Height;
                double left = Math.Clamp(_cropXPercent, 0.0, 0.98) * canvasWidth;
                double top = Math.Clamp(_cropYPercent, 0.0, 0.98) * canvasHeight;
                double width = Math.Clamp(_cropWidthPercent, 0.02, 1.0) * canvasWidth;
                double height = Math.Clamp(_cropHeightPercent, 0.02, 1.0) * canvasHeight;

                if (left + width > canvasWidth)
                    width = Math.Max(40.0, canvasWidth - left);
                if (top + height > canvasHeight)
                    height = Math.Max(40.0, canvasHeight - top);

                bdrCropFrame.Width = width;
                bdrCropFrame.Height = height;
                Canvas.SetLeft(bdrCropFrame, left);
                Canvas.SetTop(bdrCropFrame, top);

                ClampCropFrameWithinCanvas();
                ApplySelectedCropAspectRatioToEditor();
                UpdateCropInfoText();

                if (sldCropTimeline != null)
                {
                    _isUpdatingCropTimelineUi = true;
                    sldCropTimeline.Minimum = 0;
                    sldCropTimeline.Maximum = Math.Max(0.0, _cropPreviewDurationSec);
                    sldCropTimeline.Value = 0;
                    _isUpdatingCropTimelineUi = false;
                }

                UpdateCropTimelineLabel(0);
                if (imgCropPreviewFrame != null)
                {
                    imgCropPreviewFrame.Source = null;
                    imgCropPreviewFrame.Visibility = Visibility.Collapsed;
                }

                bool isImageSource = IsImageSourcePath(_cropPreviewVideoPath);
                if (medCropPreview != null)
                    medCropPreview.Visibility = isImageSource ? Visibility.Collapsed : Visibility.Visible;

                if (!isImageSource && medCropPreview != null && !string.IsNullOrWhiteSpace(_cropPreviewVideoPath))
                {
                    try
                    {
                        medCropPreview.Stop();
                        medCropPreview.Source = new Uri(_cropPreviewVideoPath);
                        medCropPreview.IsMuted = true;
                        medCropPreview.Volume = 0;
                        medCropPreview.Position = TimeSpan.Zero;
                        medCropPreview.Play();
                        await RefreshCropPreviewFrameAsync(0, immediate: true);
                    }
                    catch (Exception mediaEx)
                    {
                        LogSystem($"[CROP-EDITOR] Media preview fallback: {mediaEx.Message}");
                        await RefreshCropPreviewFrameAsync(0, immediate: true);
                    }
                }
                else
                {
                    await RefreshCropPreviewFrameAsync(0, immediate: true);
                }
            }
            catch (Exception ex)
            {
                LogSystem($"[CROP-EDITOR] Error: {ex.Message}");
            }
        }

        private async void SldCropTimeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingCropTimelineUi || gridCropOverlay.Visibility != Visibility.Visible)
                return;

            double previewSec = Math.Max(0.0, sldCropTimeline?.Value ?? 0.0);
            UpdateCropTimelineLabel(previewSec);
            if (_cropPreviewMediaReady && medCropPreview != null)
            {
                try
                {
                    medCropPreview.Position = TimeSpan.FromSeconds(previewSec);
                    medCropPreview.Pause();
                }
                catch (Exception ex)
                {
                    _cropPreviewMediaReady = false;
                    LogSystem($"[CROP-EDITOR] Media seek fallback: {ex.Message}");
                }
            }

            await RefreshCropPreviewFrameAsync(previewSec, immediate: false);
        }

        private void MedCropPreview_MediaOpened(object sender, RoutedEventArgs e)
        {
            _cropPreviewMediaReady = true;

            try
            {
                if (medCropPreview?.NaturalDuration.HasTimeSpan == true)
                {
                    _cropPreviewDurationSec = Math.Max(0.0, medCropPreview.NaturalDuration.TimeSpan.TotalSeconds);
                    if (sldCropTimeline != null)
                    {
                        _isUpdatingCropTimelineUi = true;
                        sldCropTimeline.Maximum = Math.Max(0.0, _cropPreviewDurationSec);
                        _isUpdatingCropTimelineUi = false;
                    }
                }

                double previewSec = Math.Max(0.0, sldCropTimeline?.Value ?? 0.0);
                if (medCropPreview != null)
                {
                    medCropPreview.Position = TimeSpan.FromSeconds(previewSec);
                    medCropPreview.Pause();
                }
                UpdateCropTimelineLabel(previewSec);

                LogSystem("[CROP-EDITOR] Media preview ready");
            }
            catch (Exception ex)
            {
                _cropPreviewMediaReady = false;
                LogSystem($"[CROP-EDITOR] Media preview error: {ex.Message}");
            }
        }

        private async void MedCropPreview_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            _cropPreviewMediaReady = false;
            LogSystem($"[CROP-EDITOR] Media preview failed: {e.ErrorException?.Message}");
            if (medCropPreview != null)
                medCropPreview.Visibility = Visibility.Collapsed;

            double previewSec = Math.Max(0.0, sldCropTimeline?.Value ?? 0.0);
            await RefreshCropPreviewFrameAsync(previewSec, immediate: true);
        }

        private void UpdateCropTimelineLabel(double previewSec)
        {
            if (txtCropTimelineTime == null)
                return;

            double safeCurrent = Math.Max(0.0, previewSec);
            double safeTotal = Math.Max(0.0, _cropPreviewDurationSec);
            txtCropTimelineTime.Text = $"{FormatSecondsAsClock(safeCurrent)} / {FormatSecondsAsClock(safeTotal)}";
        }

        private static string FormatSecondsAsClock(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0)
                seconds = 0.0;

            return TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");
        }

        private async Task RefreshCropPreviewFrameAsync(double previewSec, bool immediate)
        {
            if (string.IsNullOrWhiteSpace(_cropPreviewVideoPath) || imgCropPreviewFrame == null)
                return;

            if (immediate)
                LogSystem($"[CROP-EDITOR] Loading preview frame @ {previewSec:F2}s ...");

            CancelCropPreviewFrameOnly();
            _cropPreviewFrameCts = new CancellationTokenSource();
            CancellationToken token = _cropPreviewFrameCts.Token;

            try
            {
                if (!immediate)
                    await Task.Delay(120, token);

                string tempDir = Path.Combine(Path.GetTempPath(), "TitanEngine", "CropPreview");
                Directory.CreateDirectory(tempDir);
                string framePath = Path.Combine(tempDir, $"crop_{System.Guid.NewGuid():N}.jpg");

                bool extracted = await EngineCore.ExtractFrameToImageAsync(_cropPreviewVideoPath, previewSec, framePath, token);
                if (!extracted && !token.IsCancellationRequested && previewSec > 0.001)
                {
                    extracted = await EngineCore.ExtractFrameToImageAsync(_cropPreviewVideoPath, 0.0, framePath, token);
                    if (extracted)
                        LogSystem("[CROP-EDITOR] Fallback to 0s frame");
                }

                if (!extracted || token.IsCancellationRequested || !File.Exists(framePath))
                {
                    if (!token.IsCancellationRequested)
                        LogSystem($"[CROP-EDITOR] Preview frame not available at {previewSec:F2}s");
                    return;
                }

                byte[] raw = await File.ReadAllBytesAsync(framePath, token);
                using var ms = new MemoryStream(raw);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();

                if (token.IsCancellationRequested)
                    return;

                await Dispatcher.InvokeAsync(() =>
                {
                    imgCropPreviewFrame.Source = bmp;
                    imgCropPreviewFrame.Visibility = Visibility.Visible;
                });

                if (immediate)
                    LogSystem($"[CROP-EDITOR] Preview frame loaded @ {previewSec:F2}s");

                try { File.Delete(framePath); } catch { }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogSystem($"[CROP-EDITOR] Preview error: {ex.Message}");
            }
        }

        private void CancelCropPreviewFrameLoading()
        {
            CancelCropPreviewFrameOnly();
            _cropPreviewMediaReady = false;

            try
            {
                if (medCropPreview != null)
                {
                    medCropPreview.Stop();
                    medCropPreview.Source = null;
                }
            }
            catch { }
        }

        private void CancelCropPreviewFrameOnly()
        {
            try { _cropPreviewFrameCts?.Cancel(); } catch { }
            try { _cropPreviewFrameCts?.Dispose(); } catch { }
            _cropPreviewFrameCts = null;
        }

        private void BdrCropFrame_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isCropDragging = true;
            _cropDragStart = e.GetPosition(cvsCropPreview);
            (sender as UIElement)?.CaptureMouse();
        }

        private void BdrCropFrame_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isCropDragging || bdrCropFrame == null || cvsCropPreview == null)
                return;

            Point current = e.GetPosition(cvsCropPreview);
            double dx = current.X - _cropDragStart.X;
            double dy = current.Y - _cropDragStart.Y;

            double left = Canvas.GetLeft(bdrCropFrame);
            double top = Canvas.GetTop(bdrCropFrame);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;

            left += dx;
            top += dy;

            double maxLeft = Math.Max(0.0, cvsCropPreview.Width - bdrCropFrame.Width);
            double maxTop = Math.Max(0.0, cvsCropPreview.Height - bdrCropFrame.Height);
            left = Math.Clamp(left, 0.0, maxLeft);
            top = Math.Clamp(top, 0.0, maxTop);

            Canvas.SetLeft(bdrCropFrame, left);
            Canvas.SetTop(bdrCropFrame, top);
            _cropDragStart = current;
            UpdateCropInfoText();
        }

        private void BdrCropFrame_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isCropDragging = false;
            (sender as UIElement)?.ReleaseMouseCapture();
        }

        private void CropHandle_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (sender is not Thumb handle || bdrCropFrame == null || cvsCropPreview == null)
                return;

            string tag = handle.Tag?.ToString() ?? string.Empty;
            double left = Canvas.GetLeft(bdrCropFrame);
            double top = Canvas.GetTop(bdrCropFrame);
            double width = bdrCropFrame.Width;
            double height = bdrCropFrame.Height;
            double minSize = 40.0;

            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;

            if (TryGetSelectedCropAspectRatioValue(out double aspectRatio))
            {
                Rect fixedRect = BuildAspectRatioResizeRect(
                    tag,
                    left,
                    top,
                    width,
                    height,
                    e.HorizontalChange,
                    e.VerticalChange,
                    cvsCropPreview.Width,
                    cvsCropPreview.Height,
                    aspectRatio,
                    minSize);

                SetCropFrameRect(fixedRect);
                UpdateCropInfoText();
                return;
            }

            switch (tag)
            {
                case "TL":
                    left += e.HorizontalChange;
                    top += e.VerticalChange;
                    width -= e.HorizontalChange;
                    height -= e.VerticalChange;
                    break;
                case "TR":
                    top += e.VerticalChange;
                    width += e.HorizontalChange;
                    height -= e.VerticalChange;
                    break;
                case "BL":
                    left += e.HorizontalChange;
                    width -= e.HorizontalChange;
                    height += e.VerticalChange;
                    break;
                case "BR":
                    width += e.HorizontalChange;
                    height += e.VerticalChange;
                    break;
            }

            if (width < minSize)
            {
                if (tag is "TL" or "BL")
                    left -= (minSize - width);
                width = minSize;
            }

            if (height < minSize)
            {
                if (tag is "TL" or "TR")
                    top -= (minSize - height);
                height = minSize;
            }

            if (left < 0)
            {
                width += left;
                left = 0;
            }

            if (top < 0)
            {
                height += top;
                top = 0;
            }

            if (left + width > cvsCropPreview.Width)
            {
                width = cvsCropPreview.Width - left;
            }

            if (top + height > cvsCropPreview.Height)
            {
                height = cvsCropPreview.Height - top;
            }

            width = Math.Max(minSize, width);
            height = Math.Max(minSize, height);
            left = Math.Clamp(left, 0.0, Math.Max(0.0, cvsCropPreview.Width - width));
            top = Math.Clamp(top, 0.0, Math.Max(0.0, cvsCropPreview.Height - height));

            bdrCropFrame.Width = width;
            bdrCropFrame.Height = height;
            Canvas.SetLeft(bdrCropFrame, left);
            Canvas.SetTop(bdrCropFrame, top);
            UpdateCropInfoText();
        }

        private void ClampCropFrameWithinCanvas()
        {
            if (bdrCropFrame == null || cvsCropPreview == null)
                return;

            double minSize = 40.0;
            double left = Canvas.GetLeft(bdrCropFrame);
            double top = Canvas.GetTop(bdrCropFrame);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;

            double width = Math.Max(minSize, bdrCropFrame.Width);
            double height = Math.Max(minSize, bdrCropFrame.Height);

            width = Math.Min(width, cvsCropPreview.Width);
            height = Math.Min(height, cvsCropPreview.Height);
            left = Math.Clamp(left, 0.0, Math.Max(0.0, cvsCropPreview.Width - width));
            top = Math.Clamp(top, 0.0, Math.Max(0.0, cvsCropPreview.Height - height));

            if (TryGetSelectedCropAspectRatioValue(out double aspectRatio))
            {
                Rect rect = BuildAspectRatioRectWithinCanvas(left, top, width, height, cvsCropPreview.Width, cvsCropPreview.Height, aspectRatio, minSize);
                SetCropFrameRect(rect);
                return;
            }

            bdrCropFrame.Width = width;
            bdrCropFrame.Height = height;
            Canvas.SetLeft(bdrCropFrame, left);
            Canvas.SetTop(bdrCropFrame, top);
        }

        private void UpdateCropInfoText()
        {
            if (txtCropInfo == null || bdrCropFrame == null || cvsCropPreview == null)
                return;

            double left = Canvas.GetLeft(bdrCropFrame);
            double top = Canvas.GetTop(bdrCropFrame);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;

            double canvasWidth = Math.Max(1.0, cvsCropPreview.Width);
            double canvasHeight = Math.Max(1.0, cvsCropPreview.Height);

            double xPct = Math.Clamp(left / canvasWidth, 0.0, 1.0);
            double yPct = Math.Clamp(top / canvasHeight, 0.0, 1.0);
            double wPct = Math.Clamp(bdrCropFrame.Width / canvasWidth, 0.0, 1.0);
            double hPct = Math.Clamp(bdrCropFrame.Height / canvasHeight, 0.0, 1.0);

            string ratioInfo = GetSelectedCropAspectRatioName();
            txtCropInfo.Text = $"X={xPct * 100:F1}% | Y={yPct * 100:F1}% | W={wPct * 100:F1}% | H={hPct * 100:F1}% | Ratio={ratioInfo}";
        }

        private void BtnCancelCrop_Click(object sender, RoutedEventArgs e)
        {
            CancelCropPreviewFrameLoading();
            gridCropOverlay.Visibility = Visibility.Collapsed;
            _isCropDragging = false;
        }

        private void BtnSaveCrop_Click(object sender, RoutedEventArgs e)
        {
            if (bdrCropFrame == null || cvsCropPreview == null)
                return;

            ClampCropFrameWithinCanvas();

            double left = Canvas.GetLeft(bdrCropFrame);
            double top = Canvas.GetTop(bdrCropFrame);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;

            double canvasWidth = Math.Max(1.0, cvsCropPreview.Width);
            double canvasHeight = Math.Max(1.0, cvsCropPreview.Height);

            _cropXPercent = Math.Clamp(left / canvasWidth, 0.0, 0.98);
            _cropYPercent = Math.Clamp(top / canvasHeight, 0.0, 0.98);
            _cropWidthPercent = Math.Clamp(bdrCropFrame.Width / canvasWidth, 0.02, 1.0);
            _cropHeightPercent = Math.Clamp(bdrCropFrame.Height / canvasHeight, 0.02, 1.0);

            if (_cropXPercent + _cropWidthPercent > 1.0)
                _cropWidthPercent = Math.Max(0.02, 1.0 - _cropXPercent);
            if (_cropYPercent + _cropHeightPercent > 1.0)
                _cropHeightPercent = Math.Max(0.02, 1.0 - _cropYPercent);

            if (cmbFxType != null)
            {
                foreach (var item in cmbFxType.Items)
                {
                    if (item is ComboBoxItem cbItem &&
                        cbItem.Content?.ToString()?.Contains("Crop", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        cmbFxType.SelectedItem = cbItem;
                        break;
                    }
                }
            }

            CancelCropPreviewFrameLoading();
            gridCropOverlay.Visibility = Visibility.Collapsed;
            LogSystem($"[CROP] Saved frame: x={_cropXPercent:P1}, y={_cropYPercent:P1}, w={_cropWidthPercent:P1}, h={_cropHeightPercent:P1}, ratio={GetSelectedCropAspectRatioName()}");
        }

        private void SldFx_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sldFxStart != null && sldFxEnd != null)
            {
                _fxStartPercent = sldFxStart.Value;
                _fxEndPercent = sldFxEnd.Value;
                UpdateFxTimelineVisualization();
            }
        }

        private void UpdateFxTimelineVisualization()
        {
            if (gridTimelinePreview == null || bdrFxRange == null) return;

            try
            {
                double gridWidth = gridTimelinePreview.ActualWidth;
                if (gridWidth <= 0) gridWidth = 200;

                double leftPos = (_fxStartPercent / 100.0) * gridWidth;
                double barWidth = ((_fxEndPercent - _fxStartPercent) / 100.0) * gridWidth;

                leftPos = Math.Max(0, Math.Min(leftPos, gridWidth));
                barWidth = Math.Max(0, Math.Min(barWidth, gridWidth - leftPos));

                bdrFxRange.Width = barWidth;
                Canvas.SetLeft(bdrFxRange, leftPos);
            }
            catch { }
        }

        private void CmbRes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (gridCustomRes == null || cmbRes == null) return;

            if (cmbRes.SelectedItem is ComboBoxItem item)
            {
                string content = item.Content?.ToString() ?? "";
                gridCustomRes.Visibility = content.Contains("Custom") ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void CmbBitrate_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (txtCustomBitrate == null || cmbBitrate == null) return;

            if (cmbBitrate.SelectedItem is ComboBoxItem item)
            {
                string content = item.Content?.ToString() ?? "";
                txtCustomBitrate.Visibility = content.Contains("Custom") ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void FxOrOverlaySelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSnowfallOpacityUi();
        }

        private void FxOrOverlayToggleChanged(object sender, RoutedEventArgs e)
        {
            UpdateSnowfallOpacityUi();
        }

        private void UpdateSnowfallOpacityUi()
        {
            if (pnlSnowfallOpacity == null)
                return;

            string overlayEffect = GetSelectedOverlayEffectFromUi();
            string fxEffect = ResolveFxEffectName(GetSelectedComboText(cmbFxType));
            bool showSnowUi = IsSnowOverlaySelection(overlayEffect, fxEffect);
            bool showOverlay1Opacity = showSnowUi && IsSnowOverlay1Enabled();
            bool showOverlay2Opacity = showSnowUi && IsSnowOverlay2Enabled();
            bool showOverlay3Opacity = showSnowUi && IsSnowOverlay3Enabled();
            bool showOverlayIntroOpacity = showSnowUi && IsOverlayIntroEnabled();
            bool showSharedOpacity = showSnowUi && !showOverlay1Opacity && !showOverlay2Opacity && !showOverlay3Opacity && !showOverlayIntroOpacity;

            pnlSnowfallOpacity.Visibility = showSnowUi ? Visibility.Visible : Visibility.Collapsed;
            if (pnlSnowOverlay1Opacity != null)
                pnlSnowOverlay1Opacity.Visibility = showOverlay1Opacity ? Visibility.Visible : Visibility.Collapsed;
            if (pnlSnowOverlay2Opacity != null)
                pnlSnowOverlay2Opacity.Visibility = showOverlay2Opacity ? Visibility.Visible : Visibility.Collapsed;
            if (pnlSnowOverlay3Opacity != null)
                pnlSnowOverlay3Opacity.Visibility = showOverlay3Opacity ? Visibility.Visible : Visibility.Collapsed;
            if (pnlOverlayIntroOpacity != null)
                pnlOverlayIntroOpacity.Visibility = showOverlayIntroOpacity ? Visibility.Visible : Visibility.Collapsed;
            if (pnlSnowSharedOpacity != null)
                pnlSnowSharedOpacity.Visibility = showSharedOpacity ? Visibility.Visible : Visibility.Collapsed;
        }

        private string GetSelectedOverlayEffectFromUi()
        {
            return (chkSnowOverlay1?.IsChecked == true || chkSnowOverlay2?.IsChecked == true || chkSnowOverlay3?.IsChecked == true || chkOverlayIntro?.IsChecked == true)
                ? "Snowfall Overlay"
                : "None";
        }

        private bool IsSnowOverlay1Enabled()
        {
            return chkSnowOverlay1?.IsChecked == true;
        }

        private bool IsSnowOverlay2Enabled()
        {
            return chkSnowOverlay2?.IsChecked == true;
        }

        private bool IsSnowOverlay3Enabled()
        {
            return chkSnowOverlay3?.IsChecked == true;
        }

        private bool IsOverlayIntroEnabled()
        {
            return chkOverlayIntro?.IsChecked == true;
        }

        private static bool IsSnowOverlaySelection(string? overlayEffect, string? fxEffect)
        {
            if (string.Equals(overlayEffect, "Snowfall Overlay", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fxEffect, "Snowfall Overlay", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return EffectRecipeLibrary.TryGetParticleEffectRecipe(overlayEffect, out ParticleEffectRecipe recipe) &&
                   recipe.ParticleType == ParticleType.Snow;
        }

        private double ResolveSelectedSnowfallOpacity(string selectedOverlayEffect, string selectedFxEffect)
        {
            if (!IsSnowOverlaySelection(selectedOverlayEffect, selectedFxEffect))
                return -1.0;
            if (IsSnowOverlay1Enabled() || IsSnowOverlay2Enabled() || IsSnowOverlay3Enabled() || IsOverlayIntroEnabled())
                return -1.0;

            return Math.Clamp((sldSnowfallOpacity?.Value ?? 30.0) / 100.0, 0.0, 1.0);
        }

        private double ResolveSelectedSnowOverlay1Opacity()
        {
            if (!IsSnowOverlay1Enabled())
                return -1.0;

            return Math.Clamp((sldSnowOverlay1Opacity?.Value ?? sldSnowfallOpacity?.Value ?? 55.0) / 100.0, 0.0, 1.0);
        }

        private double ResolveSelectedSnowOverlay2Opacity()
        {
            if (!IsSnowOverlay2Enabled())
                return -1.0;
            return Math.Clamp((sldSnowOverlay2Opacity?.Value ?? sldSnowfallOpacity?.Value ?? 55.0) / 100.0, 0.0, 1.0);
        }

        private double ResolveSelectedSnowOverlay3Opacity()
        {
            if (!IsSnowOverlay3Enabled())
                return -1.0;
            return Math.Clamp((sldSnowOverlay3Opacity?.Value ?? sldSnowfallOpacity?.Value ?? 55.0) / 100.0, 0.0, 1.0);
        }

        private double ResolveSelectedOverlayIntroOpacity()
        {
            if (!IsOverlayIntroEnabled())
                return -1.0;
            return Math.Clamp((sldOverlayIntroOpacity?.Value ?? 100.0) / 100.0, 0.0, 1.0);
        }

        private string ResolveSelectedRainOverlayPreset(string selectedOverlayEffect)
        {
            if (!selectedOverlayEffect.Equals("Rain Overlay", StringComparison.OrdinalIgnoreCase))
                return "Heavy Rain";

            return RainOverlayPreset.NormalizePreset(GetSelectedComboText(cmbRainOverlayPreset));
        }

        private string ResolveSelectedRainOverlayIntensity(string selectedOverlayEffect)
        {
            if (!selectedOverlayEffect.Equals("Rain Overlay", StringComparison.OrdinalIgnoreCase))
                return "medium";

            return RainOverlayPreset.NormalizeIntensity(GetSelectedComboText(cmbRainOverlayIntensity));
        }

        private static string GetSelectedComboText(ComboBox? comboBox)
            => (comboBox?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "None";

        private void cmbUpscale_SelectionChanged_1(object sender, SelectionChangedEventArgs e)
        {
            if (cmbUpscale?.SelectedItem is ComboBoxItem item)
            {
                _upscaleMode = item.Content?.ToString() ?? "Off";
                LogSystem($"[UPSCALE] {_upscaleMode}");
            }
        }

        private void cmbSlowMotion_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbSlowMotion?.SelectedItem is ComboBoxItem item)
            {
                string content = item.Content?.ToString() ?? "1.0x";
                if (content.Contains("Custom"))
                {
                    if (txtSlowMotionCustom != null)
                        txtSlowMotionCustom.Visibility = Visibility.Visible;
                    if (txtSlowMotionCustom == null || !TryParseFlexibleDouble(txtSlowMotionCustom.Text, out _slowMotionSpeed) || _slowMotionSpeed <= 0.0)
                        _slowMotionSpeed = 1.0;
                }
                else
                {
                    if (txtSlowMotionCustom != null)
                        txtSlowMotionCustom.Visibility = Visibility.Collapsed;
                    if (TryExtractPlaybackSpeed(content, out double speed))
                        _slowMotionSpeed = speed;
                }
                LogSystem($"[SLOW-MOTION] Speed: {_slowMotionSpeed}x, Audio Sync: {(_slowMotionAudio ? "ON" : "OFF")}");
            }
        }

        private void txtSlowMotionCustom_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (txtSlowMotionCustom != null && TryParseFlexibleDouble(txtSlowMotionCustom.Text, out double speed))
            {
                if (speed > 0) _slowMotionSpeed = speed;
            }
        }

        private void chkSlowMotionAudio_Checked(object sender, RoutedEventArgs e)
        {
            _slowMotionAudio = true;
            LogSystem("[SLOW-MOTION-AUDIO] Audio will slow motion with video");
        }

        private void chkSlowMotionAudio_Unchecked(object sender, RoutedEventArgs e)
        {
            _slowMotionAudio = false;
            LogSystem("[SLOW-MOTION-AUDIO] Audio will keep original speed");
        }

        private async void BtnAddJob_Click(object sender, RoutedEventArgs e)
        {
            if (_listVideoPaths == null || _listVideoPaths.Count == 0)
            {
                MessageBox.Show("Select source media!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string templateName = NormalizeTemplateName((cmbTemplateStyle?.SelectedItem as ComboBoxItem)?.Content?.ToString());
            string selectedBorderEffect = ResolveOverlayEffectName(GetSelectedComboText(cmbBorderType));
            string selectedOverlayEffect = GetSelectedOverlayEffectFromUi();
            bool enableSnowOverlay1 = IsSnowOverlay1Enabled();
            bool enableSnowOverlay2 = IsSnowOverlay2Enabled();
            bool enableSnowOverlay3 = IsSnowOverlay3Enabled();
            bool enableCrossTransitions = chkEnableCrossTransitions?.IsChecked == true;
            string crossTransitionType = (cmbCrossTransitionType?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Dissolve";
            double crossTransitionDuration = double.TryParse(txtCrossTransitionDuration?.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double durVal) ? durVal : 0.6;
            bool enablePolaroidScrapbook = selectedBorderEffect.Equals("Polaroid Scrapbook", StringComparison.OrdinalIgnoreCase);
            bool enableRgbPolaroidScrapbook = selectedBorderEffect.Equals("RGB Polaroid Border", StringComparison.OrdinalIgnoreCase);
            bool enableDashedPolaroidScrapbook = selectedBorderEffect.Equals("Dashed Polaroid Frame", StringComparison.OrdinalIgnoreCase);
            bool enableBrandLogo = chkEnableBrandLogo?.IsChecked ?? false;
            string brandLogoPosition = EngineCore.NormalizeBrandLogoPosition((cmbBrandLogoPosition?.SelectedItem as ComboBoxItem)?.Content?.ToString());
            double cropZoomPercent = EngineCore.NormalizeCropZoomPercent(sldCropZoom?.Value ?? 0.0);
            if (templateName.Equals("Polaroid Scrapbook", StringComparison.OrdinalIgnoreCase))
            {
                enablePolaroidScrapbook = true;
                templateName = "None";
            }
            bool templateEnabled = !templateName.Equals("None", StringComparison.OrdinalIgnoreCase);
            string rawFxSelection = (cmbFxType?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "None";
            string selectedFxEffect = ResolveFxEffectName(rawFxSelection);
            string snowfallPreset = ResolveSnowfallPresetName(rawFxSelection);
            bool lightLeakBurnEffect = selectedFxEffect.Equals("Light Leak Burn", StringComparison.OrdinalIgnoreCase);
            if (selectedOverlayEffect.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                selectedFxEffect.Equals("Snowfall Overlay", StringComparison.OrdinalIgnoreCase))
            {
                selectedOverlayEffect = "Snowfall Overlay";
                selectedFxEffect = "None";
            }
            double selectedSnowfallOpacity = ResolveSelectedSnowfallOpacity(selectedOverlayEffect, selectedFxEffect);
            double selectedSnowOverlay1Opacity = ResolveSelectedSnowOverlay1Opacity();
            double selectedSnowOverlay2Opacity = ResolveSelectedSnowOverlay2Opacity();
            double selectedSnowOverlay3Opacity = ResolveSelectedSnowOverlay3Opacity();
            double selectedOverlayIntroOpacity = ResolveSelectedOverlayIntroOpacity();
            string selectedRainOverlayPreset = ResolveSelectedRainOverlayPreset(selectedOverlayEffect);
            string selectedRainOverlayIntensity = ResolveSelectedRainOverlayIntensity(selectedOverlayEffect);

            bool mergeVideos = (chkMergeVideos?.IsChecked ?? false) && _listVideoPaths.Count > 1;
            if ((templateEnabled || lightLeakBurnEffect) && _listVideoPaths.Count > 1)
            {
                mergeVideos = true;
                LogSystem(templateEnabled
                    ? $"[TEMPLATE] Auto-merge enabled for template mode: {templateName}"
                    : "[TRANSITION] Auto-merge enabled for Light Leak Burn transition");
            }

            bool mergeAudios = (chkMergeAudios?.IsChecked ?? false) && _listAudioPaths.Count > 1;
            List<string> queueSourceVideos = mergeVideos
                ? new List<string> { _listVideoPaths[0] }
                : new List<string>(_listVideoPaths);
            bool allImageSources = ShouldUsePortraitHdDefaultForImages(_listVideoPaths);

            if (mergeVideos)
                LogSystem($"[MERGE-QUEUE] Enabled: {_listVideoPaths.Count} videos will be rendered as ONE output.");
            if (mergeVideos && _listAudioPaths.Count > 1 && !mergeAudios)
            {
                mergeAudios = true;
                LogSystem("[AUDIO-MERGE] Auto-enabled because merge video mode has multiple audio tracks.");
            }
            if (mergeAudios)
                LogSystem($"[AUDIO-MERGE] Enabled: {_listAudioPaths.Count} audio(s) will be merged into one track.");

            for (int i = 0; i < queueSourceVideos.Count; i++)
            {
                string vp = queueSourceVideos[i];
                string? ap = mergeAudios
                    ? (_listAudioPaths.Count > 0 ? _listAudioPaths[0] : null)
                    : (_listAudioPaths.Count == 1 ? _listAudioPaths[0] :
                       (_listAudioPaths.Count > i ? _listAudioPaths[i] : null));

                string outName = string.IsNullOrWhiteSpace(txtOutputName.Text)
                    ? System.IO.Path.GetFileNameWithoutExtension(vp) + "_Titan"
                    : txtOutputName.Text;

                if (!mergeVideos && _listVideoPaths.Count > 1) outName += $"_{(i + 1):D2}";

                string resolutionText = (cmbRes.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Original";
                int targetW = -1, targetH = -1;
                bool isOriginalResolution = resolutionText.Contains("Original");

                // [NEW] APPLY UPSCALE MULTIPLIER TO ALL RESOLUTIONS
                double upscaleMultiplier = 1.0;
                if (_upscaleMode.Contains("2x") && !_upscaleMode.Contains("4x"))
                {
                    upscaleMultiplier = 2.0;
                    LogSystem($"[UPSCALE-MULTIPLIER] 2x mode selected - will multiply resolution by 2");
                }
                else if (_upscaleMode.Contains("4x"))
                {
                    upscaleMultiplier = 4.0;
                    LogSystem($"[UPSCALE-MULTIPLIER] 4x mode selected - will multiply resolution by 4");
                }

                // Parse resolution first
                if (resolutionText.Contains("Custom"))
                {
                    if (int.TryParse(txtResW.Text?.Trim(), out int w) && w > 0) targetW = w;
                    if (int.TryParse(txtResH.Text?.Trim(), out int h) && h > 0) targetH = h;
                    LogSystem($"[RESOLUTION-PARSE] Custom: {targetW}x{targetH}");
                }
                else if (!resolutionText.Contains("Original"))
                {
                    Match match = Regex.Match(resolutionText, @"(\d+)x(\d+)");
                    if (match.Success)
                    {
                        if (int.TryParse(match.Groups[1].Value, out int w)) targetW = w;
                        if (int.TryParse(match.Groups[2].Value, out int h)) targetH = h;
                        LogSystem($"[RESOLUTION-PARSE] Preset: {targetW}x{targetH}");
                    }
                }
                else if (resolutionText.Contains("Original"))
                {
                    if (allImageSources && IsImageSourcePath(vp))
                    {
                        targetW = 1080;
                        targetH = 1920;
                        resolutionText = "1080x1920 (Portrait Image Default)";
                        LogSystem("[RESOLUTION-PARSE] Image source detected -> defaulting Original to 1080x1920 portrait");
                    }
                    else
                    {
                        // Get source resolution for "Original" mode
                        var sourceRes = await EngineCore.GetVideoResolutionAsync(vp);
                        targetW = sourceRes.Width;
                        targetH = sourceRes.Height;
                        LogSystem($"[RESOLUTION-PARSE] Original (source): {targetW}x{targetH}");
                    }
                }

                // [NEW] APPLY UPSCALE MULTIPLIER TO TARGET RESOLUTION
                if (upscaleMultiplier > 1.0 && targetW > 0 && targetH > 0)
                {
                    int newW = (int)(targetW * upscaleMultiplier);
                    int newH = (int)(targetH * upscaleMultiplier);
                    LogSystem($"[UPSCALE-APPLY] Multiplying {targetW}x{targetH} by {upscaleMultiplier}x ? {newW}x{newH}");
                    targetW = newW;
                    targetH = newH;
                    resolutionText = $"{targetW}x{targetH} ({upscaleMultiplier}x Upscaled)";
                }

                // Preserve the source orientation for Original mode.
                if (isOriginalResolution && upscaleMultiplier == 1.0 && targetW > 0 && targetH > 0 && targetH > targetW)
                {
                    (targetW, targetH) = EngineCore.AutoOrientResolution(targetW, targetH);
                    LogSystem($"[AUTO-ORIENT] Original mode keeps portrait source orientation: {targetW}x{targetH}");
                }
                else if (!isOriginalResolution && targetW > 0 && targetH > 0 && targetH > targetW)
                {
                    LogSystem($"[AUTO-ORIENT] Portrait preset selected ({targetW}x{targetH}) - keeping vertical orientation");
                }
                else if (isOriginalResolution && upscaleMultiplier > 1.0)
                {
                    LogSystem($"[AUTO-ORIENT] Upscale mode active - preserving source aspect ratio (portrait)");
                }

                if (cropZoomPercent > 0.001 && targetW > 0 && targetH > 0)
                {
                    int originalTargetW = targetW;
                    int originalTargetH = targetH;
                    (targetW, targetH) = EngineCore.CoerceResolutionToPortraitNineBySixteen(targetW, targetH);
                    resolutionText = $"{targetW}x{targetH} (Crop 9:16)";
                    LogSystem($"[CROP-ZOOM] {cropZoomPercent:0.#}% center crop enabled -> forcing 9:16 output {originalTargetW}x{originalTargetH} -> {targetW}x{targetH}");
                }

                double selectedPlaybackSpeed = ResolveSelectedPlaybackSpeed();
                _slowMotionSpeed = selectedPlaybackSpeed;
                var fadeSettings = ResolveFadeSettingsFromUi();
                double imageTimelineDurationSeconds = 0.0;
                if (txtImageTimelineDuration != null &&
                    TryParseFlexibleDouble(txtImageTimelineDuration.Text, out double parsedImageTimelineDuration))
                {
                    imageTimelineDurationSeconds = Math.Max(0.0, parsedImageTimelineDuration);
                }

                var job = new RenderJob
                {
                    Id = JobQueue.Count + 1,
                    SourcePath = vp,
                    MergeVideos = mergeVideos,
                    MergeInputPaths = mergeVideos ? new List<string>(_listVideoPaths) : new List<string> { vp },
                    MergeAudio = mergeAudios && _listAudioPaths.Count > 1,
                    MergeAudioPaths = (mergeAudios && _listAudioPaths.Count > 1)
                        ? new List<string>(_listAudioPaths)
                        : (string.IsNullOrWhiteSpace(ap) ? new List<string>() : new List<string> { ap }),
                    AudioPath = ap,
                    SecondaryAudioPath = _secondaryAudioPath,
                    WatermarkPath = _watermarkPath,
                    OutputName = outName,
                    HardwareProfile = (cmbHardware.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "NVIDIA NVENC",
                    BitrateStrategy = (cmbBitrate.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Match Source",
                    Resolution = resolutionText,
                    TargetWidth = targetW,
                    TargetHeight = targetH,
                    Framerate = (cmbFps.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Original",
                    CustomBitrate = txtCustomBitrate.Text?.Trim() ?? "5000",
                    VideoTrimStart = txtTrimStart.Text?.Trim() ?? "00:00:00",
                    VideoTrimDuration = txtTrimDuration.Text?.Trim() ?? "",
                    SplitDurationSeconds = double.TryParse(txtSplitDuration.Text?.Trim(), out double splitSec) ? splitSec : 0.0,
                    ImageTimelineDurationSeconds = imageTimelineDurationSeconds,
                    WatermarkScale = _watermarkScale > 0.001 ? _watermarkScale : 0.20,
                    WatermarkRotation = _watermarkRotation,
                    WatermarkOpacity = _watermarkOpacity,
                    WatermarkXPercent = _watermarkXPercent,
                    WatermarkYPercent = _watermarkYPercent,
                    WatermarkAspectRatio = _watermarkAspectRatio,
                    FxEffect = selectedFxEffect,
                    OverlayEffect = selectedOverlayEffect,
                    BorderEffect = selectedBorderEffect,
                    EnableSnowOverlay1 = enableSnowOverlay1,
                    EnableSnowOverlay2 = enableSnowOverlay2,
                    EnableSnowOverlay3 = enableSnowOverlay3,
                    EnableCrossTransitions = enableCrossTransitions,
                    CrossTransitionType = crossTransitionType,
                    CrossTransitionDuration = crossTransitionDuration,
                    TemplateName = templateName,
                    EnablePolaroidScrapbook = enablePolaroidScrapbook,
                    EnableRgbPolaroidScrapbook = enableRgbPolaroidScrapbook,
                    EnableDashedPolaroidScrapbook = enableDashedPolaroidScrapbook,
                    EnableBrandLogo = enableBrandLogo,
                    BrandLogoPosition = brandLogoPosition,
                    SnowfallPreset = snowfallPreset,
                    SnowfallMode = "auto",
                    SnowfallAssetPath = null,
                    SnowfallOpacity = selectedSnowfallOpacity,
                    SnowOverlay1Opacity = selectedSnowOverlay1Opacity,
                    SnowOverlay2Opacity = selectedSnowOverlay2Opacity,
                    SnowOverlay3Opacity = selectedSnowOverlay3Opacity,
                    OverlayIntroOpacity = selectedOverlayIntroOpacity,
                    RainOverlayPreset = selectedRainOverlayPreset,
                    RainOverlayIntensity = selectedRainOverlayIntensity,
                    RainOverlayAssetPath = null,
                    MusicSyncMode = templateName.Equals("Beat Photo Dump", StringComparison.OrdinalIgnoreCase) ? "beat" : "off",
                    MagazineCoverTitle = txtMagazineCoverTitle.Text?.Trim() ?? "TITAN",
                    MagazineCoverSubtitle = txtMagazineCoverSubtitle.Text?.Trim() ?? "COVER STORY",
                    LightLeakBurnIntensity = sldFxIntensity.Value,
                    LightLeakBurnSpread = 55.0,
                    LightLeakBurnWarmth = 72.0,
                    LightLeakBurnBurn = 60.0,
                    LightLeakBurnEdgeSoftness = 75.0,
                    LightLeakBurnGrain = 35.0,
                    LightLeakBurnDirection = "from-left",
                    LightLeakBurnDuration = 0.95,
                    LightLeakBurnBlendMode = "screen",
                    LightLeakBurnOpacity = 0.82,
                    LightLeakBurnAssetPath = null,
                    LightLeakBurnUseAssetOverlay = false,
                    LightLeakOverlayMode = "auto",
                    LightLeakOverlayId = null,
                    LightLeakOverlayAssetPath = null,
                    LightLeakOverlayOpacity = -1.0,
                    FxIntensity = sldFxIntensity.Value,
                    FxStartPercent = _fxStartPercent,
                    FxEndPercent = _fxEndPercent,
                    VideoBrightness = sldVideoBrightness.Value,
                    EnableFade = fadeSettings.Enabled,
                    FadeInSeconds = fadeSettings.FadeIn,
                    FadeOutSeconds = fadeSettings.FadeOut,
                    CropEnabled = selectedFxEffect.Contains("Crop", StringComparison.OrdinalIgnoreCase) ||
                        cropZoomPercent > 0.001,
                    CropZoomPercent = cropZoomPercent,
                    CropAspectRatio = cropZoomPercent > 0.001 ? "9:16" : "Free",
                    CropXPercent = _cropXPercent,
                    CropYPercent = _cropYPercent,
                    CropWidthPercent = _cropWidthPercent,
                    CropHeightPercent = _cropHeightPercent,
                    ColorFilter = (cmbColorFilter.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "None (Original)",
                    ColorIntensity = sldColorIntensity.Value / 100.0,
                    StatusDisplay = "WAITING",
                    StatusColor = Brushes.Cyan,
                    VideoVolume = sldVideoVol.Value / 100.0,
                    AudioVolume = sldAudioVol.Value / 100.0,
                    Audio1Effect = (cmbAudio1Fx.SelectedItem as ComboBoxItem)?.Content.ToString(),
                    Audio1EffectIntensity = (int)sldAudio1FxIntensity.Value,
                    SecondaryAudioVolume = sldAudio2Vol.Value / 100.0,
                    SourceAudioSpeed = sldSourceAudioSpeed.Value,
                    ExternalAudioSpeed = sldExternalAudioSpeed.Value,
                    UpscaleMode = _upscaleMode,
                    SharpnessIntensity = sldSharpness.Value,
                    SlowMotionSpeed = selectedPlaybackSpeed,
                    SlowMotionAudio = _slowMotionAudio,
                    Enable60fps = chk60fps.IsChecked ?? false,
                    RsmbIntensity = sldRsmb.Value,
                    TextOverlay = (chkTextOverlay.IsChecked ?? false) ? txtOverlayText.Text?.Trim() : null,
                    TextOverlayPosition = (cmbOverlayPosition.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Bottom Left",
                    TextAlign = (cmbTextAlign.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Auto",
                    TextOverlayFontSize = (int)sldOverlayFontSize.Value,
                    TextOverlayColor = "white",
                    TextOverlayStyle = (cmbTextStyle.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Aesthetic Lyric (Georgia Italic)",
                    AddQuotes = chkAddQuotes.IsChecked ?? true,
                    DisableTextScroll = chkDisableTextScroll.IsChecked ?? false,
                    TextOverlayXPercent = double.TryParse(txtTextXPercent.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double tx) ? tx : 50.0,
                    TextOverlayYPercent = double.TryParse(txtTextYPercent.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double ty) ? ty : 85.0,
                    EnableTextGlow = chkEnableTextGlow.IsChecked ?? false,
                    TextGlowColor = (cmbTextGlowColor.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Cyan Neon",
                    TextOverlayAnimation = (cmbTextAnimation.SelectedItem as ComboBoxItem)?.Content.ToString(),
                    TextOverlayEffectDuration = double.TryParse(txtTextEffectDuration.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double te) ? te : 5.0,
                    EnableAudioSpectrum = chkAudioSpectrum.IsChecked ?? false,
                    EnableCaptionSync = chkCaptionSync.IsChecked ?? false,
                    CaptionSyncLanguage = (cmbCaptionLang.SelectedItem as ComboBoxItem)?.Content.ToString(),
                    CaptionSyncModelSize = (cmbCaptionModel.SelectedItem as ComboBoxItem)?.Content.ToString(),
                    CaptionSyncAnimation = (cmbCaptionAnimation.SelectedItem as ComboBoxItem)?.Content.ToString(),
                    CaptionSyncFontFamily = (cmbCaptionFont.SelectedItem as ComboBoxItem)?.Content.ToString(),
                    CaptionSyncPosition = (cmbCaptionPosition.SelectedItem as ComboBoxItem)?.Content.ToString(),
                    CaptionSyncXPercent = double.TryParse(txtCaptionXPercent.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double cx) ? cx : 50.0,
                    CaptionSyncYPercent = double.TryParse(txtCaptionYPercent.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double cy) ? cy : 85.0,
                    CaptionSyncFontSize = (int)sldCaptionFontSize.Value,
                    CaptionSyncColor = "white" // Using white default, could add color picker later
                };

                if (job.MergeVideos && job.MergeInputPaths.Count > 1)
                {
                    string referenceInput = job.MergeInputPaths.FirstOrDefault(p => !IsImageSourcePath(p)) ?? job.MergeInputPaths[0];
                    var firstMeta = await EngineCore.AnalyzeMediaDetailsAsync(referenceInput);
                    double totalDuration = 0.0;
                    long maxBitrate = Math.Max(0L, firstMeta.Bitrate);
                    int imageCount = job.MergeInputPaths.Count(IsImageSourcePath);
                    double imageSegmentDuration = EngineCore.ResolveImageTimelineSegmentDuration(job.ImageTimelineDurationSeconds, imageCount);
                    foreach (string mergeInput in job.MergeInputPaths)
                    {
                        if (IsImageSourcePath(mergeInput))
                        {
                            totalDuration += imageSegmentDuration;
                            continue;
                        }

                        var mergeMeta = await EngineCore.AnalyzeMediaDetailsAsync(mergeInput);
                        totalDuration += Math.Max(0.0, mergeMeta.Duration);
                        if (mergeMeta.Bitrate > maxBitrate)
                            maxBitrate = mergeMeta.Bitrate;
                    }

                    job.DurationSec = totalDuration > 0.0 ? totalDuration : firstMeta.Duration;
                    job.SourceBitrate = Math.Max(Math.Max(firstMeta.Bitrate, maxBitrate), 5000000);
                    job.SourceWidth = firstMeta.Width > 0 ? firstMeta.Width : 1920;
                    job.SourceHeight = firstMeta.Height > 0 ? firstMeta.Height : 1080;
                    job.SourceFps = firstMeta.Fps > 0.1 ? firstMeta.Fps : 30.0;
                    LogSystem($"[MERGE-META] Total duration: {job.DurationSec:F2}s ({job.MergeInputPaths.Count} inputs), bitrate={job.SourceBitrate} bps");
                }
                else
                {
                    if (IsImageSourcePath(job.SourcePath))
                    {
                        var meta = await EngineCore.AnalyzeMediaDetailsAsync(job.SourcePath);
                        job.DurationSec = EngineCore.ResolveImageTimelineSegmentDuration(job.ImageTimelineDurationSeconds, 1);
                        job.SourceBitrate = Math.Max(meta.Bitrate, 5000000);
                        job.SourceWidth = meta.Width > 0
                            ? meta.Width
                            : (job.TargetWidth > 0 ? job.TargetWidth : 1920);
                        job.SourceHeight = meta.Height > 0
                            ? meta.Height
                            : (job.TargetHeight > 0 ? job.TargetHeight : 1080);
                        job.SourceFps = meta.Fps > 0.1 ? meta.Fps : 30.0;
                        LogSystem($"[IMAGE-META] {job.SourceWidth}x{job.SourceHeight} @ {job.SourceFps:F2}fps (image source)");
                    }
                    else
                    {
                        var meta = await EngineCore.AnalyzeMediaDetailsAsync(job.SourcePath);
                        job.DurationSec = meta.Duration;
                        job.SourceBitrate = meta.Bitrate;
                        job.SourceWidth = meta.Width;
                        job.SourceHeight = meta.Height;
                        job.SourceFps = meta.Fps;
                    }
                }

                job.AudioSegments = AudioEditHelpers.NormalizeSegments(_audioEditSegments, job.DurationSec > 0.0 ? job.DurationSec : null);

                JobQueue.Add(job);
                if (mergeVideos)
                    LogSystem($"[QUEUED-MERGE] Job #{job.Id}: {job.MergeInputPaths.Count} video(s) -> {job.OutputName}");
                else
                    LogSystem($"[QUEUED] Job #{job.Id}: {job.Name}");
                if (job.MergeAudio && job.MergeAudioPaths.Count > 1)
                    LogSystem($"[QUEUED-AUDIO-MERGE] Job #{job.Id}: {job.MergeAudioPaths.Count} audio(s)");
                if (!string.IsNullOrWhiteSpace(job.SecondaryAudioPath))
                    LogSystem($"[QUEUED-AUDIO-OVERLAY] Job #{job.Id}: Audio 2 will be mixed over Audio 1");
                LogSystem($"[SLOW-MOTION] Final output speed: {job.SlowMotionSpeed:F2}x");
            }

            _listVideoPaths.Clear();
            _listAudioPaths.Clear();
            _secondaryAudioPath = null;
            _audioEditSegments.Clear();
            txtVideoPath.Text = "";
            txtAudioPath.Text = "Select Audio 1...";
            txtAudioPath2.Text = "Select Audio 2...";
            if (txtImageTimelineDuration != null)
                txtImageTimelineDuration.Text = "";
            txtOutputName.Text = "";
            UpdateAudioEditSummary();
            UpdateRandomButtonVisibility();

            if (lstQueue.Items.Count > 0)
                lstQueue.ScrollIntoView(lstQueue.Items[lstQueue.Items.Count - 1]);
        }

        private void BtnDeleteJob_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is RenderJob job && JobQueue.Contains(job))
            {
                JobQueue.Remove(job);
                for (int i = 0; i < JobQueue.Count; i++) JobQueue[i].Id = i + 1;
            }
        }

        private void MergeOptionChanged(object sender, RoutedEventArgs e)
        {
            UpdateRandomButtonVisibility();
        }

        private void UpdateRandomButtonVisibility()
        {
            try
            {
                if (btnRandomCombos == null) return;
                int videoCount = _listVideoPaths?.Count ?? 0;
                int audioCount = _listAudioPaths?.Count ?? 0;
                bool mergeVideosChecked = chkMergeVideos?.IsChecked == true;
                bool mergeAudiosChecked = chkMergeAudios?.IsChecked == true;
                bool show = videoCount > 1 && audioCount > 1 && !mergeVideosChecked && !mergeAudiosChecked;
                btnRandomCombos.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (show)
                {
                    long total = (long)videoCount * (long)audioCount;
                    btnRandomCombos.Content = $"🎲 RANDOM {videoCount} VIDEO × {audioCount} AUDIO = {total} QUEUE";
                }
            }
            catch { }
        }

        private static string SanitizeRandomNamePart(string? name, string fallback)
        {
            if (string.IsNullOrWhiteSpace(name)) return fallback;
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            string trimmed = name.Trim();
            if (trimmed.Length > 40) trimmed = trimmed.Substring(0, 40);
            return string.IsNullOrWhiteSpace(trimmed) ? fallback : trimmed;
        }

        private async void BtnRandomCombos_Click(object sender, RoutedEventArgs e)
        {
            if (_listVideoPaths == null || _listVideoPaths.Count == 0)
            {
                MessageBox.Show("Select source media first!", "Random", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_listAudioPaths == null || _listAudioPaths.Count == 0)
            {
                MessageBox.Show("Select at least 2 audio files to randomize!", "Random", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_listVideoPaths.Count < 2 || _listAudioPaths.Count < 2)
            {
                MessageBox.Show("Random needs multiple videos/images AND multiple audios (both > 1).", "Random", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if ((chkMergeVideos?.IsChecked ?? false) || (chkMergeAudios?.IsChecked ?? false))
            {
                MessageBox.Show("Uncheck both Merge options to use Random combos.", "Random", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var videos = _listVideoPaths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).ToList();
            var audios = _listAudioPaths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).ToList();
            if (videos.Count < 2 || audios.Count < 2)
            {
                MessageBox.Show("Some selected files no longer exist. Need at least 2 videos and 2 audios.", "Random", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            long totalCombos = (long)videos.Count * (long)audios.Count;
            if (totalCombos > 100)
            {
                var confirm = MessageBox.Show($"This will create {totalCombos} queue jobs ({videos.Count} x {audios.Count}). Continue?", "Random", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;
            }

            var pairs = new List<(string Video, string Audio)>();
            foreach (var v in videos)
                foreach (var a in audios)
                    pairs.Add((v, a));
            var rng = new Random();
            for (int i = pairs.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                var tmp = pairs[i];
                pairs[i] = pairs[j];
                pairs[j] = tmp;
            }

            string outputBase = txtOutputName.Text?.Trim() ?? string.Empty;
            bool allImageSources = ShouldUsePortraitHdDefaultForImages(videos);
            int created = 0;
            for (int i = 0; i < pairs.Count; i++)
            {
                string outName;
                if (!string.IsNullOrWhiteSpace(outputBase) && pairs.Count == 1) outName = outputBase;
                else if (!string.IsNullOrWhiteSpace(outputBase)) outName = $"{outputBase}_R{(i + 1):D2}";
                else
                {
                    string vBase = SanitizeRandomNamePart(Path.GetFileNameWithoutExtension(pairs[i].Video), "Titan");
                    string aBase = SanitizeRandomNamePart(Path.GetFileNameWithoutExtension(pairs[i].Audio), "Audio");
                    outName = $"{vBase}_x_{aBase}_R{(i + 1):D2}";
                }
                if (await EnqueueSingleRandomJobAsync(pairs[i].Video, pairs[i].Audio, outName, allImageSources))
                    created++;
            }

            LogSystem($"[RANDOM] Created {created}/{pairs.Count} queue jobs ({videos.Count} video x {audios.Count} audio, shuffled, settings preserved).");
            ClearSourceInputs();
        }

        private async Task<bool> EnqueueSingleRandomJobAsync(string vp, string ap, string outName, bool allImageSources)
        {
            try
            {
                string templateName = NormalizeTemplateName((cmbTemplateStyle?.SelectedItem as ComboBoxItem)?.Content?.ToString());
                string selectedBorderEffect = ResolveOverlayEffectName(GetSelectedComboText(cmbBorderType));
                string selectedOverlayEffect = GetSelectedOverlayEffectFromUi();
                bool enableSnowOverlay1 = IsSnowOverlay1Enabled();
                bool enableSnowOverlay2 = IsSnowOverlay2Enabled();
                bool enableSnowOverlay3 = IsSnowOverlay3Enabled();
                bool enableCrossTransitions = chkEnableCrossTransitions?.IsChecked == true;
                string crossTransitionType = (cmbCrossTransitionType?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Dissolve";
                double crossTransitionDuration = double.TryParse(txtCrossTransitionDuration?.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double durVal) ? durVal : 0.6;
                bool enablePolaroidScrapbook = selectedBorderEffect.Equals("Polaroid Scrapbook", StringComparison.OrdinalIgnoreCase);
                bool enableRgbPolaroidScrapbook = selectedBorderEffect.Equals("RGB Polaroid Border", StringComparison.OrdinalIgnoreCase);
                bool enableDashedPolaroidScrapbook = selectedBorderEffect.Equals("Dashed Polaroid Frame", StringComparison.OrdinalIgnoreCase);
                bool enableBrandLogo = chkEnableBrandLogo?.IsChecked ?? false;
                string brandLogoPosition = EngineCore.NormalizeBrandLogoPosition((cmbBrandLogoPosition?.SelectedItem as ComboBoxItem)?.Content?.ToString());
                double cropZoomPercent = EngineCore.NormalizeCropZoomPercent(sldCropZoom?.Value ?? 0.0);
                if (templateName.Equals("Polaroid Scrapbook", StringComparison.OrdinalIgnoreCase))
                {
                    enablePolaroidScrapbook = true;
                    templateName = "None";
                }
                string rawFxSelection = (cmbFxType?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "None";
                string selectedFxEffect = ResolveFxEffectName(rawFxSelection);
                string snowfallPreset = ResolveSnowfallPresetName(rawFxSelection);
                if (selectedOverlayEffect.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                    selectedFxEffect.Equals("Snowfall Overlay", StringComparison.OrdinalIgnoreCase))
                {
                    selectedOverlayEffect = "Snowfall Overlay";
                    selectedFxEffect = "None";
                }
                double selectedSnowfallOpacity = ResolveSelectedSnowfallOpacity(selectedOverlayEffect, selectedFxEffect);
                double selectedSnowOverlay1Opacity = ResolveSelectedSnowOverlay1Opacity();
                double selectedSnowOverlay2Opacity = ResolveSelectedSnowOverlay2Opacity();
                double selectedSnowOverlay3Opacity = ResolveSelectedSnowOverlay3Opacity();
                double selectedOverlayIntroOpacity = ResolveSelectedOverlayIntroOpacity();
                string selectedRainOverlayPreset = ResolveSelectedRainOverlayPreset(selectedOverlayEffect);
                string selectedRainOverlayIntensity = ResolveSelectedRainOverlayIntensity(selectedOverlayEffect);

                string resolutionText = (cmbRes.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Original";
                int targetW = -1, targetH = -1;
                bool isOriginalResolution = resolutionText.Contains("Original");

                double upscaleMultiplier = 1.0;
                if (_upscaleMode.Contains("2x") && !_upscaleMode.Contains("4x")) upscaleMultiplier = 2.0;
                else if (_upscaleMode.Contains("4x")) upscaleMultiplier = 4.0;

                if (resolutionText.Contains("Custom"))
                {
                    if (int.TryParse(txtResW.Text?.Trim(), out int w) && w > 0) targetW = w;
                    if (int.TryParse(txtResH.Text?.Trim(), out int h) && h > 0) targetH = h;
                }
                else if (!resolutionText.Contains("Original"))
                {
                    Match match = Regex.Match(resolutionText, @"(\d+)x(\d+)");
                    if (match.Success)
                    {
                        if (int.TryParse(match.Groups[1].Value, out int w)) targetW = w;
                        if (int.TryParse(match.Groups[2].Value, out int h)) targetH = h;
                    }
                }
                else if (resolutionText.Contains("Original"))
                {
                    if (allImageSources && IsImageSourcePath(vp))
                    {
                        targetW = 1080;
                        targetH = 1920;
                        resolutionText = "1080x1920 (Portrait Image Default)";
                    }
                    else
                    {
                        var sourceRes = await EngineCore.GetVideoResolutionAsync(vp);
                        targetW = sourceRes.Width;
                        targetH = sourceRes.Height;
                    }
                }

                if (upscaleMultiplier > 1.0 && targetW > 0 && targetH > 0)
                {
                    targetW = (int)(targetW * upscaleMultiplier);
                    targetH = (int)(targetH * upscaleMultiplier);
                    resolutionText = $"{targetW}x{targetH} ({upscaleMultiplier}x Upscaled)";
                }

                if (isOriginalResolution && upscaleMultiplier == 1.0 && targetW > 0 && targetH > 0 && targetH > targetW)
                    (targetW, targetH) = EngineCore.AutoOrientResolution(targetW, targetH);

                if (cropZoomPercent > 0.001 && targetW > 0 && targetH > 0)
                {
                    (targetW, targetH) = EngineCore.CoerceResolutionToPortraitNineBySixteen(targetW, targetH);
                    resolutionText = $"{targetW}x{targetH} (Crop 9:16)";
                }

                double selectedPlaybackSpeed = ResolveSelectedPlaybackSpeed();
                _slowMotionSpeed = selectedPlaybackSpeed;
                var fadeSettings = ResolveFadeSettingsFromUi();
                double imageTimelineDurationSeconds = 0.0;
                if (txtImageTimelineDuration != null &&
                    TryParseFlexibleDouble(txtImageTimelineDuration.Text, out double parsedImageTimelineDuration))
                    imageTimelineDurationSeconds = Math.Max(0.0, parsedImageTimelineDuration);

                var job = new RenderJob
                {
                    Id = JobQueue.Count + 1,
                    SourcePath = vp,
                    MergeVideos = false,
                    MergeInputPaths = new List<string> { vp },
                    MergeAudio = false,
                    MergeAudioPaths = string.IsNullOrWhiteSpace(ap) ? new List<string>() : new List<string> { ap },
                    AudioPath = ap,
                    SecondaryAudioPath = _secondaryAudioPath,
                    WatermarkPath = _watermarkPath,
                    OutputName = outName,
                    HardwareProfile = (cmbHardware.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "NVIDIA NVENC",
                    BitrateStrategy = (cmbBitrate.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Match Source",
                    Resolution = resolutionText,
                    TargetWidth = targetW,
                    TargetHeight = targetH,
                    Framerate = (cmbFps.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Original",
                    CustomBitrate = txtCustomBitrate.Text?.Trim() ?? "5000",
                    VideoTrimStart = txtTrimStart.Text?.Trim() ?? "00:00:00",
                    VideoTrimDuration = txtTrimDuration.Text?.Trim() ?? "",
                    SplitDurationSeconds = double.TryParse(txtSplitDuration.Text?.Trim(), out double splitSec) ? splitSec : 0.0,
                    ImageTimelineDurationSeconds = imageTimelineDurationSeconds,
                    WatermarkScale = _watermarkScale > 0.001 ? _watermarkScale : 0.20,
                    WatermarkRotation = _watermarkRotation,
                    WatermarkOpacity = _watermarkOpacity,
                    WatermarkXPercent = _watermarkXPercent,
                    WatermarkYPercent = _watermarkYPercent,
                    WatermarkAspectRatio = _watermarkAspectRatio,
                    FxEffect = selectedFxEffect,
                    OverlayEffect = selectedOverlayEffect,
                    BorderEffect = selectedBorderEffect,
                    EnableSnowOverlay1 = enableSnowOverlay1,
                    EnableSnowOverlay2 = enableSnowOverlay2,
                    EnableSnowOverlay3 = enableSnowOverlay3,
                    EnableCrossTransitions = enableCrossTransitions,
                    CrossTransitionType = crossTransitionType,
                    CrossTransitionDuration = crossTransitionDuration,
                    TemplateName = templateName,
                    EnablePolaroidScrapbook = enablePolaroidScrapbook,
                    EnableRgbPolaroidScrapbook = enableRgbPolaroidScrapbook,
                    EnableDashedPolaroidScrapbook = enableDashedPolaroidScrapbook,
                    EnableBrandLogo = enableBrandLogo,
                    BrandLogoPosition = brandLogoPosition,
                    SnowfallPreset = snowfallPreset,
                    SnowfallMode = "auto",
                    SnowfallAssetPath = null,
                    SnowfallOpacity = selectedSnowfallOpacity,
                    SnowOverlay1Opacity = selectedSnowOverlay1Opacity,
                    SnowOverlay2Opacity = selectedSnowOverlay2Opacity,
                    SnowOverlay3Opacity = selectedSnowOverlay3Opacity,
                    OverlayIntroOpacity = selectedOverlayIntroOpacity,
                    RainOverlayPreset = selectedRainOverlayPreset,
                    RainOverlayIntensity = selectedRainOverlayIntensity,
                    RainOverlayAssetPath = null,
                    MusicSyncMode = templateName.Equals("Beat Photo Dump", StringComparison.OrdinalIgnoreCase) ? "beat" : "off",
                    MagazineCoverTitle = txtMagazineCoverTitle.Text?.Trim() ?? "TITAN",
                    MagazineCoverSubtitle = txtMagazineCoverSubtitle.Text?.Trim() ?? "COVER STORY",
                    LightLeakBurnIntensity = sldFxIntensity.Value,
                    LightLeakBurnSpread = 55.0,
                    LightLeakBurnWarmth = 72.0,
                    LightLeakBurnBurn = 60.0,
                    LightLeakBurnEdgeSoftness = 75.0,
                    LightLeakBurnGrain = 35.0,
                    LightLeakBurnDirection = "from-left",
                    LightLeakBurnDuration = 0.95,
                    LightLeakBurnBlendMode = "screen",
                    LightLeakBurnOpacity = 0.82,
                    LightLeakBurnAssetPath = null,
                    LightLeakBurnUseAssetOverlay = false,
                    LightLeakOverlayMode = "auto",
                    LightLeakOverlayId = null,
                    LightLeakOverlayAssetPath = null,
                    LightLeakOverlayOpacity = -1.0,
                    FxIntensity = sldFxIntensity.Value,
                    FxStartPercent = _fxStartPercent,
                    FxEndPercent = _fxEndPercent,
                    VideoBrightness = sldVideoBrightness.Value,
                    EnableFade = fadeSettings.Enabled,
                    FadeInSeconds = fadeSettings.FadeIn,
                    FadeOutSeconds = fadeSettings.FadeOut,
                    CropEnabled = selectedFxEffect.Contains("Crop", StringComparison.OrdinalIgnoreCase) || cropZoomPercent > 0.001,
                    CropZoomPercent = cropZoomPercent,
                    CropAspectRatio = cropZoomPercent > 0.001 ? "9:16" : "Free",
                    CropXPercent = _cropXPercent,
                    CropYPercent = _cropYPercent,
                    CropWidthPercent = _cropWidthPercent,
                    CropHeightPercent = _cropHeightPercent,
                    ColorFilter = (cmbColorFilter.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "None (Original)",
                    ColorIntensity = sldColorIntensity.Value / 100.0,
                    StatusDisplay = "WAITING",
                    StatusColor = Brushes.Cyan,
                    VideoVolume = sldVideoVol.Value / 100.0,
                    AudioVolume = sldAudioVol.Value / 100.0,
                    Audio1Effect = (cmbAudio1Fx.SelectedItem as ComboBoxItem)?.Content.ToString(),
                    Audio1EffectIntensity = (int)sldAudio1FxIntensity.Value,
                    SecondaryAudioVolume = sldAudio2Vol.Value / 100.0,
                    SourceAudioSpeed = sldSourceAudioSpeed.Value,
                    ExternalAudioSpeed = sldExternalAudioSpeed.Value,
                    UpscaleMode = _upscaleMode,
                    SharpnessIntensity = sldSharpness.Value,
                    SlowMotionSpeed = selectedPlaybackSpeed,
                    SlowMotionAudio = _slowMotionAudio,
                    Enable60fps = chk60fps.IsChecked ?? false,
                    RsmbIntensity = sldRsmb.Value,
                    TextOverlay = (chkTextOverlay.IsChecked ?? false) ? txtOverlayText.Text?.Trim() : null,
                    TextOverlayPosition = (cmbOverlayPosition.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Bottom Left",
                    TextAlign = (cmbTextAlign.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Auto",
                    TextOverlayFontSize = (int)sldOverlayFontSize.Value,
                    TextOverlayColor = "white",
                    TextOverlayStyle = (cmbTextStyle.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Aesthetic Lyric (Georgia Italic)",
                    AddQuotes = chkAddQuotes.IsChecked ?? true,
                    DisableTextScroll = chkDisableTextScroll.IsChecked ?? false,
                    TextOverlayXPercent = double.TryParse(txtTextXPercent.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double tx) ? tx : 50.0,
                    TextOverlayYPercent = double.TryParse(txtTextYPercent.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double ty) ? ty : 85.0,
                    EnableTextGlow = chkEnableTextGlow.IsChecked ?? false,
                    TextGlowColor = (cmbTextGlowColor.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Cyan Neon",
                    TextOverlayAnimation = (cmbTextAnimation.SelectedItem as ComboBoxItem)?.Content.ToString(),
                    TextOverlayEffectDuration = double.TryParse(txtTextEffectDuration.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double te) ? te : 5.0,
                    EnableAudioSpectrum = chkAudioSpectrum.IsChecked ?? false,
                    EnableCaptionSync = chkCaptionSync.IsChecked ?? false,
                    CaptionSyncLanguage = (cmbCaptionLang.SelectedItem as ComboBoxItem)?.Content.ToString(),
                    CaptionSyncModelSize = (cmbCaptionModel.SelectedItem as ComboBoxItem)?.Content.ToString(),
                    CaptionSyncAnimation = (cmbCaptionAnimation.SelectedItem as ComboBoxItem)?.Content.ToString(),
                    CaptionSyncFontFamily = (cmbCaptionFont.SelectedItem as ComboBoxItem)?.Content.ToString(),
                    CaptionSyncPosition = (cmbCaptionPosition.SelectedItem as ComboBoxItem)?.Content.ToString(),
                    CaptionSyncXPercent = double.TryParse(txtCaptionXPercent.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double cx) ? cx : 50.0,
                    CaptionSyncYPercent = double.TryParse(txtCaptionYPercent.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double cy) ? cy : 85.0,
                    CaptionSyncFontSize = (int)sldCaptionFontSize.Value,
                    CaptionSyncColor = "white"
                };

                if (IsImageSourcePath(job.SourcePath))
                {
                    var meta = await EngineCore.AnalyzeMediaDetailsAsync(job.SourcePath);
                    job.DurationSec = EngineCore.ResolveImageTimelineSegmentDuration(job.ImageTimelineDurationSeconds, 1);
                    job.SourceBitrate = Math.Max(meta.Bitrate, 5000000);
                    job.SourceWidth = meta.Width > 0 ? meta.Width : (job.TargetWidth > 0 ? job.TargetWidth : 1920);
                    job.SourceHeight = meta.Height > 0 ? meta.Height : (job.TargetHeight > 0 ? job.TargetHeight : 1080);
                    job.SourceFps = meta.Fps > 0.1 ? meta.Fps : 30.0;
                }
                else
                {
                    var meta = await EngineCore.AnalyzeMediaDetailsAsync(job.SourcePath);
                    job.DurationSec = meta.Duration;
                    job.SourceBitrate = meta.Bitrate;
                    job.SourceWidth = meta.Width;
                    job.SourceHeight = meta.Height;
                    job.SourceFps = meta.Fps;
                }

                job.AudioSegments = AudioEditHelpers.NormalizeSegments(_audioEditSegments, job.DurationSec > 0.0 ? job.DurationSec : null);
                JobQueue.Add(job);
                LogSystem($"[QUEUED-RANDOM] Job #{job.Id}: {Path.GetFileName(vp)} x {Path.GetFileName(ap)} -> {job.OutputName}");
                return true;
            }
            catch (Exception ex)
            {
                LogSystem($"[RANDOM-ERROR] {Path.GetFileName(vp)} x {Path.GetFileName(ap)}: {ex.Message}");
                return false;
            }
        }

        private void ClearSourceInputs()
        {
            _listVideoPaths.Clear();
            _listAudioPaths.Clear();
            _secondaryAudioPath = null;
            _audioEditSegments.Clear();
            txtVideoPath.Text = "";
            txtAudioPath.Text = "Select Audio 1...";
            txtAudioPath2.Text = "Select Audio 2...";
            if (txtImageTimelineDuration != null)
                txtImageTimelineDuration.Text = "";
            txtOutputName.Text = "";
            UpdateAudioEditSummary();
            UpdateRandomButtonVisibility();
            if (lstQueue.Items.Count > 0)
                lstQueue.ScrollIntoView(lstQueue.Items[lstQueue.Items.Count - 1]);
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            JobQueue.Clear();
        }



        private async void BtnStartBatch_Click(object sender, RoutedEventArgs e)
        {
            if (_isBatchRunning)
            {
                _cancellationTokenSource?.Cancel();
                LogSystem("[STOP] Cancelling...");
                return;
            }

            if (_localApiRenderLock.CurrentCount == 0)
            {
                MessageBox.Show("API host is rendering a job. Please wait.", "Host Busy", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var pending = JobQueue.Where(j => j.StatusDisplay == "WAITING" || j.StatusDisplay == "FAILED").ToList();
            if (!pending.Any())
            {
                MessageBox.Show("Queue empty!", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _isBatchRunning = true;
            _cancellationTokenSource = new CancellationTokenSource();

            var btn = sender as Button;
            if (btn != null)
            {
                _originalStartBtnContent = btn.Content;
                _originalStartBtnBackground = btn.Background;
                _originalStartBtnBorder = btn.BorderBrush;

                btn.Background = new SolidColorBrush(Color.FromRgb(200, 30, 30));
                btn.BorderBrush = Brushes.Red;
                btn.Content = "STOP RENDER";
            }

            LogSystem(">>> BATCH STARTED <<<");

            try
            {
                int total = pending.Count;
                int done = 0;

                foreach (var job in pending)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested) break;

                    job.Progress = 0;
                    job.StatusDisplay = "PROCESSING...";
                    job.StatusColor = Brushes.Yellow;
                    lstQueue.Items.Refresh();
                    lstQueue.ScrollIntoView(job);

                    LogSystem($"[START] Job #{job.Id}: {job.Name}");

                    Action<double> progHandler = (p) =>
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            job.Progress = p;
                            job.StatusDisplay = $"{p:F1}%";
                            pbGlobalProgress.Value = p;
                        });
                    };

                    Action<string> logHandler = (m) => LogSystem(m);

                    await EngineCore.ExecuteRenderAsync(job, _customOutputFolder, _cancellationTokenSource.Token, progHandler, logHandler);

                    job.StatusDisplay = "DONE";
                    job.StatusColor = Brushes.LimeGreen;
                    job.Progress = 100;
                    LogSystem($"[SUCCESS] Job #{job.Id} completed");

                    done++;
                    await Task.Delay(500);
                }

                MessageBox.Show($"Completed {done}/{total} jobs!", "Done", MessageBoxButton.OK, MessageBoxImage.Information);
                
                // [NEW] OPEN OUTPUT FOLDER AUTOMATICALLY
                if (done > 0 && pending.Count > 0 && pending[0].ResultPath != null)
                {
                    try
                    {
                        string? outPath = System.IO.Path.GetDirectoryName(pending[0].ResultPath);
                        if (outPath != null && Directory.Exists(outPath))
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "explorer.exe",
                                Arguments = outPath,
                                UseShellExecute = true
                            });
                            LogSystem($"[OUTPUT-FOLDER] Opened: {outPath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSystem($"[OUTPUT-FOLDER] Could not open: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                LogSystem("[STOP] Batch cancelled");
            }
            catch (Exception ex)
            {
                LogSystem($"[FATAL] {ex.Message}");
            }
            finally
            {
                _isBatchRunning = false;
                if (btn != null && _originalStartBtnContent != null)
                {
                    btn.Content = _originalStartBtnContent;
                    btn.Background = _originalStartBtnBackground;
                    btn.BorderBrush = _originalStartBtnBorder;
                }
                _cancellationTokenSource?.Dispose();
            }
        }

        private void BtnHostMode_Click(object sender, RoutedEventArgs e)
        {
            if (_isBatchRunning)
            {
                MessageBox.Show("Cannot toggle host mode while batch is running.", "Busy", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_isHostModeEnabled)
            {
                StopLocalApiServer();
            }
            else
            {
                var portWindow = new PortInputWindow();
                portWindow.Owner = this;
                if (portWindow.ShowDialog() == true)
                {
                    LocalApiPrefix = $"http://{portWindow.LocalIp}:{portWindow.SelectedPort}/";
                    StartLocalApiServer();
                }
            }
        }

        private void OnMainWindowClosed(object? sender, EventArgs e)
        {
            CancelCropPreviewFrameLoading();
            CancelWatermarkPreviewFrameLoading();
            StopLocalApiServer();
            _localApiRenderLock.Dispose();
        }

        private void UpdateHostModeButtonState()
        {
            if (btnHostMode == null) return;

            btnHostMode.Content = _isHostModeEnabled ? "HOST: ON" : "HOST: OFF";

            if (_isHostModeEnabled)
            {
                btnHostMode.Background = new SolidColorBrush(Color.FromRgb(25, 122, 71));
                btnHostMode.BorderBrush = new SolidColorBrush(Color.FromRgb(55, 180, 110));
            }
            else
            {
                btnHostMode.Background = new SolidColorBrush(Color.FromRgb(85, 51, 51));
                btnHostMode.BorderBrush = new SolidColorBrush(Color.FromRgb(136, 68, 68));
            }
        }

        private bool StartLocalApiServer()
        {
            try
            {
                if (_localApiListener != null && _localApiListener.IsListening)
                {
                    _isHostModeEnabled = true;
                    UpdateHostModeButtonState();
                    return true;
                }

                _localApiListener = new HttpListener();
                _localApiListener.Prefixes.Add(LocalApiPrefix);
                _localApiListener.Start();

                _localApiCts = new CancellationTokenSource();
                _localApiLoopTask = Task.Run(() => RunLocalApiLoopAsync(_localApiCts.Token));

                _isHostModeEnabled = true;
                UpdateHostModeButtonState();
                LogSystem($"[API] Host mode ON - {LocalApiPrefix.TrimEnd('/')}{LocalApiRenderPath}");
                LogSystem($"[API] Queue add endpoint - {LocalApiPrefix.TrimEnd('/')}{LocalApiQueueAddPath}");
                LogSystem($"[API] Queue start endpoint - {LocalApiPrefix.TrimEnd('/')}{LocalApiQueueStartPath}");
                LogSystem($"[API] Async endpoint - {LocalApiPrefix.TrimEnd('/')}{LocalApiRenderAsyncPath}");
                LogSystem($"[API] Blocking endpoint - {LocalApiPrefix.TrimEnd('/')}{LocalApiRenderWaitPath}");
                return true;
            }
            catch (HttpListenerException ex)
            {
                _isHostModeEnabled = false;
                UpdateHostModeButtonState();
                LogSystem($"[API-ERROR] {ex.Message}");
                LogSystem($"[API-HINT] Run as admin once: netsh http add urlacl url={LocalApiPrefix} user=%USERNAME%");
                MessageBox.Show($"Cannot start localhost API: {ex.Message}", "Host Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            catch (Exception ex)
            {
                _isHostModeEnabled = false;
                UpdateHostModeButtonState();
                LogSystem($"[API-ERROR] {ex.Message}");
                MessageBox.Show($"Cannot start localhost API: {ex.Message}", "Host Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void StopLocalApiServer()
        {
            try
            {
                _localApiCts?.Cancel();
            }
            catch { }

            try
            {
                if (_localApiListener != null && _localApiListener.IsListening)
                {
                    _localApiListener.Stop();
                }
            }
            catch { }

            try
            {
                _localApiListener?.Close();
            }
            catch { }

            _localApiListener = null;
            _localApiCts?.Dispose();
            _localApiCts = null;
            _localApiLoopTask = null;

            if (_isHostModeEnabled)
            {
                LogSystem("[API] Host mode OFF");
            }

            _isHostModeEnabled = false;
            UpdateHostModeButtonState();
        }

        private async Task RunLocalApiLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (_localApiListener == null || !_localApiListener.IsListening)
                    break;

                HttpListenerContext? context = null;
                try
                {
                    context = await _localApiListener.GetContextAsync().WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }

                if (context != null)
                {
                    _ = Task.Run(() => HandleLocalApiRequestAsync(context, token), token);
                }
            }
        }

        private async Task HandleLocalApiRequestAsync(HttpListenerContext context, CancellationToken token)
        {
            bool lockTaken = false;
            bool lockTransferred = false;
            bool streamingResponseStarted = false;
            RenderJob? job = null;

            try
            {
                string path = context.Request.Url?.AbsolutePath ?? "/";
                if (path.Length > 1)
                {
                    path = path.TrimEnd('/');
                }

                if (context.Request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteApiResponseAsync(context.Response, 200, new LocalhostRenderResponse
                    {
                        Success = true,
                        Message = "OK"
                    });
                    return;
                }

                if (!_isHostModeEnabled)
                {
                    await WriteApiResponseAsync(context.Response, 503, new LocalhostRenderResponse
                    {
                        Success = false,
                        Message = "Host mode is OFF"
                    });
                    return;
                }

                if (path.Equals(LocalApiRenderStatusPath, StringComparison.OrdinalIgnoreCase))
                {
                    string statusJobId = (context.Request.QueryString["jobId"] ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(statusJobId))
                    {
                        await WriteApiResponseAsync(context.Response, 400, new LocalhostRenderResponse
                        {
                            Success = false,
                            Message = "jobId is required"
                        });
                        return;
                    }

                    ApiRenderJobState? state = GetApiJobState(statusJobId);
                    if (state == null)
                    {
                        await WriteApiResponseAsync(context.Response, 404, new LocalhostRenderResponse
                        {
                            Success = false,
                            QueueId = int.TryParse(statusJobId, out int parsedMissingQueueId) ? parsedMissingQueueId : null,
                            Message = "jobId not found",
                            JobId = statusJobId
                        });
                        return;
                    }

                    RenderJob? queuedJob = JobQueue.FirstOrDefault(j => j.Guid == state.JobId);

                    long? outputFileSize = null;
                    if (!string.IsNullOrWhiteSpace(state.FinalOutput) && File.Exists(state.FinalOutput))
                    {
                        outputFileSize = new FileInfo(state.FinalOutput).Length;
                    }

                    double encodingTime = queuedJob != null ? (queuedJob.EndTime - queuedJob.StartTime).TotalSeconds : 0;

                    await WriteApiResponseAsync(context.Response, 200, new LocalhostRenderResponse
                    {
                        Success = true,
                        QueueId = state.QueueId,
                        Message = state.Message,
                        JobId = state.JobId,
                        Status = state.Status,
                        Done = state.Done,
                        JobSuccess = state.Success,
                        Progress = state.Progress,
                        RequestedOutput = state.RequestedOutput,
                        EngineOutput = state.EngineOutput,
                        FinalOutput = state.FinalOutput,
                        InputFile = state.InputFile,
                        InputFiles = state.InputFiles ??
                            (queuedJob != null
                                ? (queuedJob.MergeVideos
                                    ? new List<string>(queuedJob.MergeInputPaths)
                                    : new List<string> { queuedJob.SourcePath })
                                : null),
                        InputCount = state.InputFiles?.Count ??
                            (queuedJob != null
                                ? (queuedJob.MergeVideos ? queuedJob.MergeInputPaths.Count : 1)
                                : (string.IsNullOrWhiteSpace(state.InputFile) ? null : 1)),
                        MergeVideos = state.MergeVideos || (queuedJob?.MergeVideos ?? false),
                        AudioFile = state.AudioFile ?? queuedJob?.AudioPath,
                        AudioFile2 = state.AudioFile2 ?? queuedJob?.SecondaryAudioPath,
                        AudioFiles = state.AudioFiles ?? (queuedJob != null ? BuildAudioFilesForJob(queuedJob) : null),
                        AudioCount = state.AudioFiles?.Count ?? (queuedJob != null ? BuildAudioFilesForJob(queuedJob)?.Count : (string.IsNullOrWhiteSpace(state.AudioFile) ? null : 1)),
                        MergeAudio = state.MergeAudio || (queuedJob?.MergeAudio ?? false),
                        AudioTrimStart = state.AudioTrimStart ?? queuedJob?.AudioTrimStart,
                        AudioTrimEnd = state.AudioTrimEnd ?? queuedJob?.AudioTrimEnd,
                        AudioTrimDuration = state.AudioTrimDuration ?? queuedJob?.AudioTrimDuration,
                        AudioSegments = state.AudioSegments ?? (queuedJob?.AudioSegments == null ? null : queuedJob.AudioSegments
                            .Select(seg => new AudioEditSegment
                            {
                                StartSeconds = seg.StartSeconds,
                                EndSeconds = seg.EndSeconds
                            })
                            .ToList()),
                        OutputVideo = state.OutputVideo,
                        TemplateName = state.TemplateName ?? queuedJob?.TemplateName,
                        OverlayEffect = state.OverlayEffect ?? queuedJob?.OverlayEffect,
                        EnableSnowOverlay1 = state.EnableSnowOverlay1 || (queuedJob?.EnableSnowOverlay1 ?? false),
                        EnableSnowOverlay2 = state.EnableSnowOverlay2 || (queuedJob?.EnableSnowOverlay2 ?? false),
                        EnableSnowOverlay3 = state.EnableSnowOverlay3 || (queuedJob?.EnableSnowOverlay3 ?? false),
                        EnableCrossTransitions = state.EnableCrossTransitions || (queuedJob?.EnableCrossTransitions ?? false),
                        CrossTransitionType = !string.IsNullOrWhiteSpace(state.CrossTransitionType) ? state.CrossTransitionType : queuedJob?.CrossTransitionType,
                        CrossTransitionDuration = state.CrossTransitionDuration > 0.01 ? state.CrossTransitionDuration : (queuedJob?.CrossTransitionDuration ?? 0.6),
                        EnablePolaroidScrapbook = state.EnablePolaroidScrapbook || (queuedJob?.EnablePolaroidScrapbook ?? false),
                        EnableRgbPolaroidScrapbook = state.EnableRgbPolaroidScrapbook || (queuedJob?.EnableRgbPolaroidScrapbook ?? false),
                        EnableDashedPolaroidScrapbook = state.EnableDashedPolaroidScrapbook || (queuedJob?.EnableDashedPolaroidScrapbook ?? false),
                        EnableBrandLogo = state.EnableBrandLogo || (queuedJob?.EnableBrandLogo ?? false),
                        BrandLogoPosition = state.BrandLogoPosition ?? queuedJob?.BrandLogoPosition,
                        SnowfallPreset = state.SnowfallPreset ?? queuedJob?.SnowfallPreset,
                        SnowfallMode = state.SnowfallMode ?? queuedJob?.SnowfallMode,
                        SnowfallOpacity = state.SnowfallOpacity > 0.0 ? state.SnowfallOpacity : queuedJob?.SnowfallOpacity,
                        SnowOverlay1Opacity = state.SnowOverlay1Opacity > 0.0 ? state.SnowOverlay1Opacity : queuedJob?.SnowOverlay1Opacity,
                        SnowOverlay2Opacity = state.SnowOverlay2Opacity > 0.0 ? state.SnowOverlay2Opacity : queuedJob?.SnowOverlay2Opacity,
                        SnowOverlay3Opacity = state.SnowOverlay3Opacity > 0.0 ? state.SnowOverlay3Opacity : queuedJob?.SnowOverlay3Opacity,
                        OverlayIntroOpacity = state.OverlayIntroOpacity > 0.0 ? state.OverlayIntroOpacity : queuedJob?.OverlayIntroOpacity,
                        RainOverlayPreset = state.RainOverlayPreset ?? queuedJob?.RainOverlayPreset,
                        RainOverlayIntensity = state.RainOverlayIntensity ?? queuedJob?.RainOverlayIntensity,
                        MusicSyncMode = state.MusicSyncMode ?? queuedJob?.MusicSyncMode,
                        ImageTimelineDurationSeconds = state.ImageTimelineDurationSeconds > 0.0
                            ? state.ImageTimelineDurationSeconds
                            : queuedJob?.ImageTimelineDurationSeconds,
                        CropZoomPercent = state.CropZoomPercent > 0.0
                            ? state.CropZoomPercent
                            : queuedJob?.CropZoomPercent,
                        CropAspectRatio = state.CropAspectRatio ?? queuedJob?.CropAspectRatio,
                        VideoId = state.JobId,
                        SourceDuration = queuedJob?.DurationSec,
                        SourceWidth = queuedJob?.SourceWidth,
                        SourceHeight = queuedJob?.SourceHeight,
                        SourceBitrate = queuedJob?.SourceBitrate,
                        SourceFramerate = queuedJob?.SourceFps,
                        OutputWidth = GetEffectiveOutputWidth(queuedJob),
                        OutputHeight = GetEffectiveOutputHeight(queuedJob),
                        OutputFramerate = queuedJob?.Framerate,
                        SourceFileName = queuedJob != null ? Path.GetFileName(queuedJob.SourcePath) : null,
                        OutputFileName = Path.GetFileName(state.FinalOutput ?? state.RequestedOutput ?? state.OutputVideo ?? string.Empty),
                        OutputFileSize = outputFileSize,
                        EncodingTime = state.Done ? encodingTime : null
                    });
                    return;
                }

                bool isQueueAddPath = path.Equals(LocalApiQueueAddPath, StringComparison.OrdinalIgnoreCase);
                bool isQueueStartPath = path.Equals(LocalApiQueueStartPath, StringComparison.OrdinalIgnoreCase);

                if (isQueueStartPath)
                {
                    if (!context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteApiResponseAsync(context.Response, 405, new LocalhostRenderResponse
                        {
                            Success = false,
                            Message = "Method not allowed. Use POST /api/queue/start"
                        });
                        return;
                    }

                    if (_isBatchRunning)
                    {
                        await WriteApiResponseAsync(context.Response, 409, new LocalhostRenderResponse
                        {
                            Success = false,
                            Message = "Queue is already running"
                        });
                        return;
                    }

                    List<RenderJob> pending = await Dispatcher.InvokeAsync(() =>
                        JobQueue.Where(j => j.StatusDisplay == "WAITING" || j.StatusDisplay == "FAILED" || j.StatusDisplay == "FAILED (API)").ToList());
                    int totalJobsInQueue = await Dispatcher.InvokeAsync(() => JobQueue.Count);

                    if (!pending.Any())
                    {
                        await WriteApiResponseAsync(context.Response, 200, new LocalhostRenderResponse
                        {
                            Success = true,
                            Message = "Queue has no pending jobs",
                            Status = "idle",
                            Done = true,
                            TotalJobs = totalJobsInQueue,
                            StartedJobs = 0
                        });
                        return;
                    }

                    lockTaken = await _localApiRenderLock.WaitAsync(0, token);
                    if (!lockTaken)
                    {
                        await WriteApiResponseAsync(context.Response, 429, new LocalhostRenderResponse
                        {
                            Success = false,
                            Message = "Host is busy rendering another request"
                        });
                        return;
                    }

                    _isBatchRunning = true;
                    _cancellationTokenSource = new CancellationTokenSource();

                    foreach (RenderJob pendingJob in pending)
                    {
                        string requestedOutput = ResolveRequestedOutputPathForJob(pendingJob, GetApiJobState(pendingJob.Guid));
                        SetApiJobState(new ApiRenderJobState
                        {
                            QueueId = pendingJob.Id,
                            JobId = pendingJob.Guid,
                            Status = "queued",
                            Done = false,
                            Success = false,
                            Progress = pendingJob.Progress,
                            Message = "Queued. Waiting for queue renderer",
                            RequestedOutput = requestedOutput,
                            InputFile = pendingJob.SourcePath,
                            InputFiles = BuildInputFilesForJob(pendingJob),
                            MergeVideos = pendingJob.MergeVideos,
                            AudioFile = pendingJob.AudioPath,
                            AudioFile2 = pendingJob.SecondaryAudioPath,
                            AudioFiles = BuildAudioFilesForJob(pendingJob),
                            MergeAudio = pendingJob.MergeAudio,
                            OutputVideo = requestedOutput,
                            EngineOutput = pendingJob.ResultPath,
                            TemplateName = pendingJob.TemplateName,
                            EnablePolaroidScrapbook = pendingJob.EnablePolaroidScrapbook,
                            EnableRgbPolaroidScrapbook = pendingJob.EnableRgbPolaroidScrapbook,
                            EnableDashedPolaroidScrapbook = pendingJob.EnableDashedPolaroidScrapbook,
                            EnableBrandLogo = pendingJob.EnableBrandLogo,
                            BrandLogoPosition = pendingJob.BrandLogoPosition,
                            MusicSyncMode = pendingJob.MusicSyncMode,
                            CropZoomPercent = pendingJob.CropZoomPercent,
                            CropAspectRatio = pendingJob.CropAspectRatio
                        });
                    }

                    List<LocalhostQueueJobInfo> startedJobs = pending
                        .Select(j => BuildQueueJobInfo(j, GetApiJobState(j.Guid)))
                        .ToList();

                    CancellationToken queueToken = _cancellationTokenSource.Token;
                    lockTransferred = true;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await RunApiQueueAsync(pending, queueToken);
                        }
                        finally
                        {
                            _isBatchRunning = false;
                            try { _cancellationTokenSource?.Dispose(); } catch { }
                            _cancellationTokenSource = null;
                            if (lockTaken)
                                _localApiRenderLock.Release();
                        }
                    }, queueToken);

                    await WriteApiResponseAsync(context.Response, 200, new LocalhostRenderResponse
                    {
                        Success = true,
                        Message = $"Queue started with {pending.Count} job(s). Poll each statusUrl for completion",
                        Status = "processing",
                        Done = false,
                        TotalJobs = totalJobsInQueue,
                        StartedJobs = pending.Count,
                        Jobs = startedJobs
                    });
                    return;
                }

                bool isRenderPath =
                    path.Equals(LocalApiRenderPath, StringComparison.OrdinalIgnoreCase) ||
                    path.Equals(LocalApiRenderAsyncPath, StringComparison.OrdinalIgnoreCase) ||
                    path.Equals(LocalApiRenderWaitPath, StringComparison.OrdinalIgnoreCase) ||
                    isQueueAddPath;
                bool forceWaitByPath = path.Equals(LocalApiRenderWaitPath, StringComparison.OrdinalIgnoreCase);
                bool forceAsyncByPath = path.Equals(LocalApiRenderAsyncPath, StringComparison.OrdinalIgnoreCase);

                if (!isRenderPath)
                {
                    await WriteApiResponseAsync(context.Response, 404, new LocalhostRenderResponse
                    {
                        Success = false,
                        Message = "Endpoint not found. Use POST /api/queue/add, POST /api/queue/start, POST /api/render, or GET /api/render/status"
                    });
                    return;
                }

                if (!context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteApiResponseAsync(context.Response, 405, new LocalhostRenderResponse
                    {
                        Success = false,
                        Message = "Method not allowed. Use POST /api/queue/add, POST /api/queue/start, POST /api/render, POST /api/render/async, or POST /api/render/wait"
                    });
                    return;
                }

                if (_isBatchRunning && !isQueueAddPath)
                {
                    await WriteApiResponseAsync(context.Response, 409, new LocalhostRenderResponse
                    {
                        Success = false,
                        Message = "UI batch is running"
                    });
                    return;
                }

                string bodyJson;
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
                {
                    bodyJson = await reader.ReadToEndAsync();
                }

                LocalhostRenderRequest? request = JsonSerializer.Deserialize<LocalhostRenderRequest>(bodyJson, _localApiJsonOptions);
                if (request == null)
                {
                    await WriteApiResponseAsync(context.Response, 400, new LocalhostRenderResponse
                    {
                        Success = false,
                        Message = "Invalid JSON body"
                    });
                    return;
                }

                bool? waitForCompletionFromQuery = ParseApiBoolQuery(
                    context.Request.QueryString["waitForCompletion"] ??
                    context.Request.QueryString["wait"] ??
                    context.Request.QueryString["sync"] ??
                    context.Request.QueryString["blocking"]
                );

                bool? waitForCompletionFromBody =
                    request.WaitForCompletion ??
                    GetFeatureBool(request, "waitForCompletion", "wait", "sync", "blocking");

                // Default BAS flow: POST /api/render returns job/video info immediately.
                // POST /api/queue/add only adds to the app queue; POST /api/queue/start starts rendering later.
                bool waitForCompletion = forceWaitByPath && !isQueueAddPath;
                if (forceAsyncByPath)
                {
                    waitForCompletion = false;
                }
                else if (!isQueueAddPath && waitForCompletionFromBody.HasValue)
                {
                    waitForCompletion = waitForCompletionFromBody.Value;
                }
                else if (!isQueueAddPath && waitForCompletionFromQuery.HasValue)
                {
                    waitForCompletion = waitForCompletionFromQuery.Value;
                }

                LogSystem(isQueueAddPath
                    ? "[API] queueAdd=true"
                    : $"[API] waitForCompletion={(waitForCompletion ? "true" : "false")}");

                if (!isQueueAddPath)
                {
                    lockTaken = await _localApiRenderLock.WaitAsync(0, token);
                    if (!lockTaken)
                    {
                        await WriteApiResponseAsync(context.Response, 429, new LocalhostRenderResponse
                        {
                            Success = false,
                            Message = "Host is busy rendering another request"
                        });
                        return;
                    }
                }

                var requestedInputFiles = new List<string>();
                if (request.InputFiles != null && request.InputFiles.Count > 0)
                    requestedInputFiles.AddRange(request.InputFiles);

                List<string>? featureInputFiles = GetFeatureStringList(request,
                    "inputFiles", "inputfiles", "videos", "videoFiles");
                if (featureInputFiles != null && featureInputFiles.Count > 0)
                    requestedInputFiles.AddRange(featureInputFiles);

                string? singleInput = FirstNonEmpty(
                    request.InputFile,
                    GetFeatureString(request, "inputFile", "inputfile", "sourcePath", "source", "videoPath", "video")
                );
                if (!string.IsNullOrWhiteSpace(singleInput))
                    requestedInputFiles.Add(singleInput);

                List<string> inputPaths = requestedInputFiles
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => Path.GetFullPath(x.Trim()))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                bool allImageInputs = ShouldUsePortraitHdDefaultForImages(inputPaths);

                if (inputPaths.Count == 0)
                {
                    await WriteApiResponseAsync(context.Response, 400, new LocalhostRenderResponse
                    {
                        Success = false,
                        Message = "inputFile/inputFiles is missing"
                    });
                    return;
                }

                foreach (string inputCandidate in inputPaths)
                {
                    if (!File.Exists(inputCandidate))
                    {
                        await WriteApiResponseAsync(context.Response, 400, new LocalhostRenderResponse
                        {
                            Success = false,
                            Message = $"Input file does not exist: {inputCandidate}"
                        });
                        return;
                    }
                }

                bool mergeVideosRequested = request.MergeVideos ??
                    GetFeatureBool(request, "mergeVideos", "merge", "concat", "joinVideos") ??
                    false;
                if (inputPaths.Count > 1 && !mergeVideosRequested)
                {
                    // If caller sends multiple files, default to merge workflow.
                    mergeVideosRequested = true;
                }

                string inputPath = inputPaths[0];

                string outputPath = (FirstNonEmpty(
                    request.OutputVideo,
                    GetFeatureString(request, "outputVideo", "outputvideo", "outputPath", "output", "saveTo")
                ) ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    await WriteApiResponseAsync(context.Response, 400, new LocalhostRenderResponse
                    {
                        Success = false,
                        Message = "outputVideo is required"
                    });
                    return;
                }

                outputPath = Path.GetFullPath(outputPath);
                string outputDir = Path.GetDirectoryName(outputPath) ?? string.Empty;
                string outputName = Path.GetFileNameWithoutExtension(outputPath);

                if (string.IsNullOrWhiteSpace(outputDir) || string.IsNullOrWhiteSpace(outputName))
                {
                    await WriteApiResponseAsync(context.Response, 400, new LocalhostRenderResponse
                    {
                        Success = false,
                        Message = "outputVideo path is invalid"
                    });
                    return;
                }

                Directory.CreateDirectory(outputDir);

                var requestedAudioFiles = new List<string>();
                if (request.AudioFiles != null && request.AudioFiles.Count > 0)
                    requestedAudioFiles.AddRange(request.AudioFiles);

                List<string>? featureAudioFiles = GetFeatureStringList(request,
                    "audioFiles", "audiofiles", "musicFiles", "musicfiles");
                if (featureAudioFiles != null && featureAudioFiles.Count > 0)
                    requestedAudioFiles.AddRange(featureAudioFiles);

                string? singleAudioInput = FirstNonEmpty(
                    request.AudioFile,
                    GetFeatureString(request, "audioFile", "audiopath", "audio", "musicFile")
                );
                if (!string.IsNullOrWhiteSpace(singleAudioInput))
                    requestedAudioFiles.Add(singleAudioInput);

                string? secondaryAudioPath = FirstNonEmpty(
                    request.AudioFile2,
                    GetFeatureString(request, "audioFile2", "audio2", "musicFile2", "secondaryAudioFile", "overlayAudioFile")
                );
                if (!string.IsNullOrWhiteSpace(secondaryAudioPath))
                {
                    secondaryAudioPath = Path.GetFullPath(secondaryAudioPath.Trim());
                    if (!File.Exists(secondaryAudioPath))
                    {
                        await WriteApiResponseAsync(context.Response, 400, new LocalhostRenderResponse
                        {
                            Success = false,
                            Message = $"audioFile2 does not exist: {secondaryAudioPath}"
                        });
                        return;
                    }
                }

                List<string> audioPaths = requestedAudioFiles
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => Path.GetFullPath(x.Trim()))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (string audioCandidate in audioPaths)
                {
                    if (!File.Exists(audioCandidate))
                    {
                        await WriteApiResponseAsync(context.Response, 400, new LocalhostRenderResponse
                        {
                            Success = false,
                            Message = $"audioFile does not exist: {audioCandidate}"
                        });
                        return;
                    }
                }

                bool mergeAudioRequested = request.MergeAudio ??
                    GetFeatureBool(request, "mergeAudio", "mergeAudios", "audioMerge", "concatAudio", "joinAudio") ??
                    false;
                if (audioPaths.Count > 1 && !mergeAudioRequested)
                {
                    // If caller sends multiple audio files, default to merge audio workflow.
                    mergeAudioRequested = true;
                }

                string? audioPath = audioPaths.Count > 0 ? audioPaths[0] : null;

                string? watermarkPath = FirstNonEmpty(
                    request.WatermarkFile,
                    GetFeatureString(request, "watermarkFile", "watermarkPath", "watermark", "logo")
                );

                if (!string.IsNullOrWhiteSpace(watermarkPath))
                {
                    watermarkPath = Path.GetFullPath(watermarkPath.Trim());
                    if (!File.Exists(watermarkPath))
                    {
                        await WriteApiResponseAsync(context.Response, 400, new LocalhostRenderResponse
                        {
                            Success = false,
                            Message = "watermarkFile does not exist"
                        });
                        return;
                    }
                }

                string colorFilter = ResolveColorFilterFromRequest(request);
                double colorIntensity = Math.Clamp(
                    request.ColorIntensity ??
                    GetFeatureDouble(request, "colorIntensity", "color_intensity", "colorPower", "color_power") ??
                    1.0,
                    0.0, 1.0
                );

                string? rawFxEffectInput = FirstNonEmpty(
                    request.FxEffect,
                    GetFeatureString(request, "fxEffect", "effect", "fx")
                );
                string fxEffect = ResolveFxEffectName(rawFxEffectInput);

                string overlayEffect = ResolveOverlayEffectName(FirstNonEmpty(
                    request.Overlay,
                    request.OverlayEffect,
                    GetFeatureString(request, "overlay", "overlayEffect", "overlayPreset", "overlayType", "overlayName")
                ));
                bool enableSnowOverlay1 = request.EnableSnowOverlay1 ??
                    GetFeatureBool(request, "enableSnowOverlay1", "snowOverlay1", "overlay1", "overlayOne") ??
                    false;
                bool enableSnowOverlay2 = request.EnableSnowOverlay2 ??
                    GetFeatureBool(request, "enableSnowOverlay2", "snowOverlay2", "overlay2", "overlayTwo") ??
                    false;
                bool enableSnowOverlay3 = request.EnableSnowOverlay3 ??
                    GetFeatureBool(request, "enableSnowOverlay3", "snowOverlay3", "overlay3", "overlayThree") ??
                    false;
                bool enableOverlayIntro = request.EnableOverlayIntro ??
                    GetFeatureBool(request, "enableOverlayIntro", "overlayIntro", "introOverlay") ??
                    false;
                bool enableCrossTransitions = request.EnableCrossTransitions ?? GetFeatureBool(request, "enableCrossTransitions", "crossTransitions", "enableTransitions", "transitions") ?? false;
                string crossTransitionType = FirstNonEmpty(request.CrossTransitionType, GetFeatureString(request, "crossTransitionType", "transitionType", "transitionPreset", "transitionName"), "Dissolve") ?? "Dissolve";
                double crossTransitionDuration = request.CrossTransitionDuration ?? GetFeatureDouble(request, "crossTransitionDuration", "transitionDuration", "transitionSec") ?? 0.6;
                string borderEffect = ResolveOverlayEffectName(FirstNonEmpty(
                    request.Border,
                    request.BorderEffect,
                    GetFeatureString(request, "border", "borderEffect", "borderPreset", "borderType", "borderName")
                ));
                string snowfallPreset = ResolveSnowfallPresetName(FirstNonEmpty(
                    request.SnowfallPreset,
                    rawFxEffectInput,
                    GetFeatureString(request, "snowfallPreset", "snowPreset", "snowProfile", "snowModePreset")
                ));
                string snowfallMode = SnowfallOverlayPreset.NormalizeMode(FirstNonEmpty(
                    request.SnowfallMode,
                    GetFeatureString(request, "snowfallMode", "snowOverlayMode", "snowRenderMode")
                ));
                string? snowfallAssetPath = FirstNonEmpty(
                    request.SnowfallAssetPath,
                    GetFeatureString(request, "snowfallAssetPath", "snowOverlayAssetPath", "snowAssetPath")
                );
                double snowfallOpacity = NormalizeOptionalOpacity(
                    request.SnowfallOpacity ??
                    GetFeatureDouble(
                        request,
                        "snowfallOpacity",
                        "snowOverlayOpacity",
                        "snowOpacity",
                        "snowfallOpacityPercent",
                        "snowOverlayOpacityPercent",
                        "snowOpacityPercent") ??
                    -1.0);
                double snowOverlay1Opacity = NormalizeOptionalOpacity(
                    request.SnowOverlay1Opacity ??
                    GetFeatureDouble(
                        request,
                        "snowOverlay1Opacity",
                        "snow1Opacity",
                        "overlay1Opacity",
                        "snowOverlay1OpacityPercent",
                        "snow1OpacityPercent",
                        "overlay1OpacityPercent") ??
                    snowfallOpacity);
                double snowOverlay2Opacity = NormalizeOptionalOpacity(
                    request.SnowOverlay2Opacity ??
                    GetFeatureDouble(
                        request,
                        "snowOverlay2Opacity",
                        "snow2Opacity",
                        "overlay2Opacity",
                        "snowOverlay2OpacityPercent",
                        "snow2OpacityPercent",
                        "overlay2OpacityPercent") ??
                    snowfallOpacity);
                double snowOverlay3Opacity = NormalizeOptionalOpacity(
                    request.SnowOverlay3Opacity ??
                    GetFeatureDouble(
                        request,
                        "snowOverlay3Opacity",
                        "snow3Opacity",
                        "overlay3Opacity",
                        "snowOverlay3OpacityPercent",
                        "snow3OpacityPercent",
                        "overlay3OpacityPercent") ??
                    snowfallOpacity);
                double overlayIntroOpacity = NormalizeOptionalOpacity(
                    request.OverlayIntroOpacity ??
                    GetFeatureDouble(
                        request,
                        "overlayIntroOpacity",
                        "introOpacity",
                        "overlayIntroOpacityPercent",
                        "introOpacityPercent") ??
                    snowfallOpacity);
                string rainOverlayPreset = ResolveRainOverlayPresetName(FirstNonEmpty(
                    request.RainOverlayPreset,
                    GetFeatureString(request, "rainOverlayPreset", "rainPreset", "overlayPreset", "rainProfile")
                ));
                string rainOverlayIntensity = ResolveRainOverlayIntensityName(FirstNonEmpty(
                    request.RainOverlayIntensity,
                    GetFeatureString(request, "rainOverlayIntensity", "rainIntensity", "overlayIntensity")
                ));
                string? rainOverlayAssetPath = FirstNonEmpty(
                    request.RainOverlayAssetPath,
                    GetFeatureString(request, "rainOverlayAssetPath", "rainAssetPath", "overlayAssetPath")
                );

                string templateName = NormalizeTemplateName(FirstNonEmpty(
                    request.TemplateName,
                    GetFeatureString(request, "templateName", "template", "presetTemplate")
                ));
                bool enablePolaroidScrapbook = request.EnablePolaroidScrapbook ??
                    GetFeatureBool(request, "enablePolaroidScrapbook", "polaroidScrapbook", "usePolaroidScrapbook", "polaroidFrame") ??
                    false;
                bool enableRgbPolaroidScrapbook = request.EnableRgbPolaroidScrapbook ??
                    GetFeatureBool(request, "enableRgbPolaroidScrapbook", "rgbPolaroidScrapbook", "enableRgbPolaroidBorder", "polaroidRgbBorder", "rainbowPolaroid") ??
                    false;
                bool enableDashedPolaroidScrapbook = request.EnableDashedPolaroidScrapbook ??
                    GetFeatureBool(request, "enableDashedPolaroidScrapbook", "dashedPolaroidScrapbook", "enableDashedPolaroidFrame", "polaroidDashedFrame", "roundedDashedPolaroid") ??
                    false;

                bool overlayContainsLegacyBorder =
                    overlayEffect.Equals("Polaroid Scrapbook", StringComparison.OrdinalIgnoreCase) ||
                    overlayEffect.Equals("Polaroid Scrapbook 2", StringComparison.OrdinalIgnoreCase) ||
                    overlayEffect.Equals("RGB Polaroid Border", StringComparison.OrdinalIgnoreCase) ||
                    overlayEffect.Equals("Dashed Polaroid Frame", StringComparison.OrdinalIgnoreCase);
                if (borderEffect.Equals("None", StringComparison.OrdinalIgnoreCase) && overlayContainsLegacyBorder)
                {
                    borderEffect = overlayEffect;
                    overlayEffect = "None";
                }

                enablePolaroidScrapbook |= borderEffect.Equals("Polaroid Scrapbook", StringComparison.OrdinalIgnoreCase);
                enableRgbPolaroidScrapbook |= borderEffect.Equals("RGB Polaroid Border", StringComparison.OrdinalIgnoreCase);
                enableDashedPolaroidScrapbook |= borderEffect.Equals("Dashed Polaroid Frame", StringComparison.OrdinalIgnoreCase);
                bool enableBrandLogo = request.EnableBrandLogo ??
                    GetFeatureBool(request, "enableBrandLogo", "brandLogo", "videoLogo", "useBrandLogo", "useVideoLogo") ??
                    false;
                string brandLogoPosition = EngineCore.NormalizeBrandLogoPosition(FirstNonEmpty(
                    request.BrandLogoPosition,
                    GetFeatureString(request, "brandLogoPosition", "logoPosition", "videoLogoPosition", "titanLogoPosition")
                ));

                if (overlayEffect.Equals("Polaroid Scrapbook", StringComparison.OrdinalIgnoreCase))
                    enablePolaroidScrapbook = true;
                else if (overlayEffect.Equals("RGB Polaroid Border", StringComparison.OrdinalIgnoreCase))
                    enableRgbPolaroidScrapbook = true;
                else if (overlayEffect.Equals("Dashed Polaroid Frame", StringComparison.OrdinalIgnoreCase))
                    enableDashedPolaroidScrapbook = true;
                if (overlayEffect.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                    fxEffect.Equals("Snowfall Overlay", StringComparison.OrdinalIgnoreCase))
                {
                    overlayEffect = "Snowfall Overlay";
                    fxEffect = "None";
                }
                if (enableSnowOverlay1 || enableSnowOverlay2 || enableSnowOverlay3 || enableOverlayIntro)
                {
                    overlayEffect = "Snowfall Overlay";
                }
                if (overlayEffect.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                    fxEffect.Equals("Dust & Scratches", StringComparison.OrdinalIgnoreCase))
                {
                    overlayEffect = "Dust & Scratches";
                    fxEffect = "None";
                }

                if (templateName.Equals("Polaroid Scrapbook", StringComparison.OrdinalIgnoreCase))
                {
                    enablePolaroidScrapbook = true;
                    templateName = "None";
                }
                string? rawMusicSyncMode = FirstNonEmpty(
                    request.MusicSyncMode,
                    GetFeatureString(request, "musicSyncMode", "musicSync", "syncMode")
                );
                string musicSyncMode = string.IsNullOrWhiteSpace(rawMusicSyncMode) &&
                    templateName.Equals("Beat Photo Dump", StringComparison.OrdinalIgnoreCase)
                    ? "beat"
                    : NormalizeMusicSyncModeName(rawMusicSyncMode);
                string? rawMagazineCoverTitle = FirstNonEmpty(
                    request.MagazineCoverTitle,
                    GetFeatureString(request, "magazineCoverTitle", "magazineTitle", "coverTitle")
                );
                string? rawMagazineCoverSubtitle = FirstNonEmpty(
                    request.MagazineCoverSubtitle,
                    GetFeatureString(request, "magazineCoverSubtitle", "magazineSubtitle", "coverSubtitle")
                );
                string? rawLightLeakBurnDirection = FirstNonEmpty(
                    request.LightLeakBurnDirection,
                    GetFeatureString(request, "lightLeakBurnDirection", "leakBurnDirection", "transitionDirection", "direction")
                );
                string? rawLightLeakBurnBlendMode = FirstNonEmpty(
                    request.LightLeakBurnBlendMode,
                    GetFeatureString(request, "lightLeakBurnBlendMode", "leakBurnBlendMode", "blendMode")
                );
                string? rawLightLeakBurnAssetPath = FirstNonEmpty(
                    request.LightLeakBurnAssetPath,
                    GetFeatureString(request, "lightLeakBurnAssetPath", "leakBurnAssetPath", "transitionAsset", "overlayAsset")
                );
                string lightLeakOverlayMode = LightLeakOverlayPreset.NormalizeMode(FirstNonEmpty(
                    request.LightLeakOverlayMode,
                    GetFeatureString(request, "lightLeakOverlayMode", "overlayLightLeakMode", "lightLeakMode", "overlayMode")
                ));
                string? lightLeakOverlayId = FirstNonEmpty(
                    request.LightLeakOverlayId,
                    GetFeatureString(request, "lightLeakOverlayId", "overlayLightLeakId", "lightLeakAssetId", "overlayAssetId")
                );
                string? lightLeakOverlayAssetPath = FirstNonEmpty(
                    request.LightLeakOverlayAssetPath,
                    GetFeatureString(request, "lightLeakOverlayAssetPath", "overlayLightLeakAssetPath", "lightLeakAssetPath")
                );
                bool forceTemplateMerge = !templateName.Equals("None", StringComparison.OrdinalIgnoreCase) && inputPaths.Count > 1;
                bool forceTransitionMerge = fxEffect.Equals("Light Leak Burn", StringComparison.OrdinalIgnoreCase) && inputPaths.Count > 1;
                bool forceMerge = forceTemplateMerge || forceTransitionMerge;

                double fxIntensity = Math.Clamp(
                    request.FxIntensity ??
                    GetFeatureDouble(request, "fxIntensity", "fx_intensity", "effectIntensity") ??
                    50.0,
                    0.0, 100.0
                );

                double fxStart = Math.Clamp(
                    request.FxStartPercent ??
                    GetFeatureDouble(request, "fxStartPercent", "fxStart", "effectStart") ??
                    0.0,
                    0.0, 100.0
                );

                double lightLeakBurnIntensity = Math.Clamp(
                    request.LightLeakBurnIntensity ??
                    GetFeatureDouble(request, "lightLeakBurnIntensity", "leakBurnIntensity", "transitionIntensity") ??
                    fxIntensity,
                    0.0, 100.0
                );

                double lightLeakBurnSpread = Math.Clamp(
                    request.LightLeakBurnSpread ??
                    GetFeatureDouble(request, "lightLeakBurnSpread", "leakBurnSpread", "spread") ??
                    55.0,
                    0.0, 100.0
                );

                double lightLeakBurnWarmth = Math.Clamp(
                    request.LightLeakBurnWarmth ??
                    GetFeatureDouble(request, "lightLeakBurnWarmth", "leakBurnWarmth", "warmth") ??
                    72.0,
                    0.0, 100.0
                );

                double lightLeakBurnBurn = Math.Clamp(
                    request.LightLeakBurnBurn ??
                    GetFeatureDouble(request, "lightLeakBurnBurn", "leakBurnBurn", "burn") ??
                    60.0,
                    0.0, 100.0
                );

                double lightLeakBurnEdgeSoftness = Math.Clamp(
                    request.LightLeakBurnEdgeSoftness ??
                    GetFeatureDouble(request, "lightLeakBurnEdgeSoftness", "leakBurnEdgeSoftness", "edgeSoftness") ??
                    75.0,
                    0.0, 100.0
                );

                double lightLeakBurnGrain = Math.Clamp(
                    request.LightLeakBurnGrain ??
                    GetFeatureDouble(request, "lightLeakBurnGrain", "leakBurnGrain", "grain") ??
                    35.0,
                    0.0, 100.0
                );

                double lightLeakBurnDuration = Math.Max(0.10,
                    request.LightLeakBurnDuration ??
                    GetFeatureDouble(request, "lightLeakBurnDuration", "leakBurnDuration", "transitionDuration") ??
                    0.95);

                double lightLeakBurnOpacity = Math.Clamp(
                    request.LightLeakBurnOpacity ??
                    GetFeatureDouble(request, "lightLeakBurnOpacity", "leakBurnOpacity", "transitionOpacity") ??
                    0.82,
                    0.0, 1.0
                );

                string lightLeakBurnDirection = LightLeakBurnPreset.NormalizeDirection(rawLightLeakBurnDirection);
                string lightLeakBurnBlendMode = LightLeakBurnPreset.NormalizeBlendMode(rawLightLeakBurnBlendMode);
                string? lightLeakBurnAssetPath = rawLightLeakBurnAssetPath;
                bool lightLeakBurnUseAssetOverlay = (request.LightLeakBurnUseAssetOverlay ??
                    GetFeatureBool(request, "lightLeakBurnUseAssetOverlay", "leakBurnUseAssetOverlay", "useAssetOverlay") ??
                    false) && !string.IsNullOrWhiteSpace(lightLeakBurnAssetPath);
                double lightLeakOverlayOpacity = request.LightLeakOverlayOpacity ??
                    GetFeatureDouble(request, "lightLeakOverlayOpacity", "overlayLightLeakOpacity", "lightLeakOpacity") ??
                    -1.0;
                if (lightLeakOverlayOpacity >= 0.0)
                    lightLeakOverlayOpacity = Math.Clamp(lightLeakOverlayOpacity, 0.0, 1.0);

                double fxEnd = Math.Clamp(
                    request.FxEndPercent ??
                    GetFeatureDouble(request, "fxEndPercent", "fxEnd", "effectEnd") ??
                    100.0,
                    0.0, 100.0
                );

                if (fxEnd < fxStart)
                {
                    (fxStart, fxEnd) = (fxEnd, fxStart);
                }

                double cropX = Math.Clamp(
                    request.CropXPercent ??
                    GetFeatureDouble(request, "cropXPercent", "cropX", "crop_left_percent") ??
                    0.0,
                    0.0, 0.98
                );

                double cropY = Math.Clamp(
                    request.CropYPercent ??
                    GetFeatureDouble(request, "cropYPercent", "cropY", "crop_top_percent") ??
                    0.0,
                    0.0, 0.98
                );

                double cropWidth = Math.Clamp(
                    request.CropWidthPercent ??
                    GetFeatureDouble(request, "cropWidthPercent", "cropW", "crop_width_percent") ??
                    1.0,
                    0.02, 1.0
                );

                double cropHeight = Math.Clamp(
                    request.CropHeightPercent ??
                    GetFeatureDouble(request, "cropHeightPercent", "cropH", "crop_height_percent") ??
                    1.0,
                    0.02, 1.0
                );

                double cropZoomPercent = EngineCore.NormalizeCropZoomPercent(
                    request.CropZoomPercent ??
                    GetFeatureDouble(request, "cropZoomPercent", "cropPercent", "cropZoom", "zoomCropPercent") ??
                    0.0
                );

                string cropAspectRatio = EngineCore.NormalizeCropAspectRatioName(FirstNonEmpty(
                    request.CropAspectRatio,
                    GetFeatureString(request, "cropAspectRatio", "cropRatio", "aspectRatio", "cropPreset")
                ));

                bool hasCropValues =
                    request.CropXPercent.HasValue ||
                    request.CropYPercent.HasValue ||
                    request.CropWidthPercent.HasValue ||
                    request.CropHeightPercent.HasValue ||
                    GetFeatureDouble(request, "cropXPercent", "cropX", "crop_left_percent").HasValue ||
                    GetFeatureDouble(request, "cropYPercent", "cropY", "crop_top_percent").HasValue ||
                    GetFeatureDouble(request, "cropWidthPercent", "cropW", "crop_width_percent").HasValue ||
                    GetFeatureDouble(request, "cropHeightPercent", "cropH", "crop_height_percent").HasValue;

                bool cropEnabled = (request.FxEffect?.Contains("crop", StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (GetFeatureBool(request, "cropEnabled", "enableCrop") ?? false) ||
                    cropZoomPercent > 0.001 ||
                    hasCropValues ||
                    !cropAspectRatio.Equals("Free", StringComparison.OrdinalIgnoreCase);

                int targetWidth = request.TargetWidth ??
                    GetFeatureInt(request, "targetWidth", "targetW", "width") ??
                    -1;

                int targetHeight = request.TargetHeight ??
                    GetFeatureInt(request, "targetHeight", "targetH", "height") ??
                    -1;

                string rawResolution = FirstNonEmpty(
                    request.Resolution,
                    GetFeatureString(request, "resolution", "res")
                ) ?? "Original";

                string resolution = NormalizeResolutionName(rawResolution);
                if ((targetWidth <= 0 || targetHeight <= 0) && TryExtractResolution(rawResolution, out int rw, out int rh))
                {
                    targetWidth = rw;
                    targetHeight = rh;
                    resolution = $"{rw}x{rh} (API Custom)";
                }
                else if ((targetWidth <= 0 || targetHeight <= 0) &&
                    allImageInputs &&
                    resolution.Equals("Original", StringComparison.OrdinalIgnoreCase))
                {
                    targetWidth = 1080;
                    targetHeight = 1920;
                    resolution = "1080x1920 (Portrait Image Default)";
                    LogSystem("[API] Image source detected -> defaulting Original resolution to 1080x1920 portrait");
                }
                else if (targetWidth > 0 && targetHeight > 0)
                {
                    resolution = $"{targetWidth}x{targetHeight} (API Custom)";
                }

                if (cropZoomPercent > 0.001 && targetWidth > 0 && targetHeight > 0)
                {
                    int originalTargetWidth = targetWidth;
                    int originalTargetHeight = targetHeight;
                    (targetWidth, targetHeight) = EngineCore.CoerceResolutionToPortraitNineBySixteen(targetWidth, targetHeight);
                    resolution = $"{targetWidth}x{targetHeight} (Crop 9:16)";
                    LogSystem($"[API-CROP-ZOOM] {cropZoomPercent:0.#}% center crop enabled -> forcing 9:16 output {originalTargetWidth}x{originalTargetHeight} -> {targetWidth}x{targetHeight}");
                }

                string hardwareProfile = NormalizeHardwareProfileName(FirstNonEmpty(
                    request.HardwareProfile,
                    GetFeatureString(request, "hardwareProfile", "hardware", "encoder")
                ) ?? "NVIDIA NVENC");

                string bitrateStrategy = NormalizeBitrateStrategy(FirstNonEmpty(
                    request.BitrateStrategy,
                    GetFeatureString(request, "bitrateStrategy", "bitrateMode", "bitrate")
                ) ?? "Match Source");

                string framerate = ResolveFramerateName(FirstNonEmpty(
                    request.Framerate,
                    GetFeatureString(request, "framerate", "fps")
                ) ?? "Original");

                string customBitrate = (FirstNonEmpty(
                    request.CustomBitrate,
                    GetFeatureString(request, "customBitrate", "targetBitrate", "kbps")
                ) ?? "5000").Trim();

                string upscaleMode = NormalizeUpscaleModeName(FirstNonEmpty(
                    request.UpscaleMode,
                    GetFeatureString(request, "upscaleMode", "upscale")
                ) ?? "Off (Original Size)");

                double sharpness = Math.Clamp(
                    request.SharpnessIntensity ??
                    GetFeatureDouble(request, "sharpnessIntensity", "sharpness") ??
                    1.0,
                    0.0, 2.0
                );

                double videoBrightness = Math.Clamp(
                    request.VideoBrightness ??
                    GetFeatureDouble(request, "videoBrightness", "brightness", "videoBright") ??
                    0.0,
                    -100.0, 100.0
                );

                double slowMotionSpeed = Math.Clamp(
                    request.SlowMotionSpeed ??
                    GetFeatureDouble(request, "slowMotionSpeed", "playbackSpeed", "speed") ??
                    1.0,
                    0.10, 8.0
                );

                bool slowMotionAudio = request.SlowMotionAudio ??
                    GetFeatureBool(request, "slowMotionAudio", "syncAudio") ??
                    true;

                double imageTimelineDurationSeconds = Math.Max(0.0,
                    request.ImageTimelineDurationSeconds ??
                    GetFeatureDouble(request, "imageTimelineDurationSeconds", "imageTimelineDuration", "imageDurationSeconds", "slideshowDuration", "slideDuration") ??
                    0.0);

                double videoVolume = Math.Clamp(
                    request.VideoVolume ??
                    GetFeatureDouble(request, "videoVolume", "videoVol") ??
                    1.0,
                    0.0, 2.0
                );

                double audioVolume = Math.Clamp(
                    request.AudioVolume ??
                    GetFeatureDouble(request, "audioVolume", "audioVol", "musicVolume") ??
                    1.0,
                    0.0, 2.0
                );

                double audio2Volume = Math.Clamp(
                    request.Audio2Volume ??
                    request.SecondaryAudioVolume ??
                    GetFeatureDouble(request, "audio2Volume", "audio2Vol", "secondaryAudioVolume", "overlayAudioVolume", "music2Volume") ??
                    1.0,
                    0.0, 2.0
                );

                double sourceAudioSpeed = Math.Clamp(
                    request.SourceAudioSpeed ??
                    GetFeatureDouble(request, "sourceAudioSpeed", "audioSpeed", "sourceAudioRate") ??
                    1.0,
                    0.10, 8.0
                );

                double externalAudioSpeed = Math.Clamp(
                    request.ExternalAudioSpeed ??
                    GetFeatureDouble(request, "externalAudioSpeed", "audioTrackSpeed", "musicSpeed", "addedAudioSpeed", "externalMusicSpeed") ??
                    1.0,
                    0.10, 8.0
                );

                string videoTrimStart = (FirstNonEmpty(
                    request.VideoTrimStart,
                    GetFeatureString(request, "videoTrimStart", "trimStart", "videoStart")
                ) ?? "00:00:00").Trim();

                string videoTrimDuration = (FirstNonEmpty(
                    request.VideoTrimDuration,
                    GetFeatureString(request, "videoTrimDuration", "trimDuration", "videoDuration")
                ) ?? string.Empty).Trim();

                string audioTrimStart = (FirstNonEmpty(
                    request.AudioTrimStart,
                    GetFeatureString(request, "audioTrimStart", "audioStart")
                ) ?? "00:00:00").Trim();

                string audioTrimDuration = (FirstNonEmpty(
                    request.AudioTrimDuration,
                    GetFeatureString(request, "audioTrimDuration", "audioDuration")
                ) ?? string.Empty).Trim();

                string audioTrimEnd = (FirstNonEmpty(
                    request.AudioTrimEnd,
                    GetFeatureString(request, "audioTrimEnd", "audioEnd")
                ) ?? string.Empty).Trim();

                List<AudioEditSegment> requestAudioSegments = new List<AudioEditSegment>();
                if (request.AudioSegments != null && request.AudioSegments.Count > 0)
                {
                    requestAudioSegments = AudioEditHelpers.NormalizeSegments(request.AudioSegments, null);
                }
                else
                {
                    bool hasAudioTrimStart = AudioEditHelpers.TryParseFlexibleTime(audioTrimStart, out double parsedAudioTrimStart);
                    bool hasAudioTrimEnd = AudioEditHelpers.TryParseFlexibleTime(audioTrimEnd, out double parsedAudioTrimEnd);
                    bool hasAudioTrimDuration = AudioEditHelpers.TryParseFlexibleTime(audioTrimDuration, out double parsedAudioTrimDuration);

                    if (hasAudioTrimStart || hasAudioTrimEnd || hasAudioTrimDuration)
                    {
                        double audioTrimStartValue = hasAudioTrimStart ? parsedAudioTrimStart : 0.0;
                        double audioTrimEndValue = hasAudioTrimEnd
                            ? parsedAudioTrimEnd
                            : (hasAudioTrimDuration ? audioTrimStartValue + parsedAudioTrimDuration : audioTrimStartValue);

                        requestAudioSegments = AudioEditHelpers.NormalizeSegments(new[]
                        {
                            new AudioEditSegment
                            {
                                StartSeconds = audioTrimStartValue,
                                EndSeconds = audioTrimEndValue
                            }
                        });
                    }
                }

                double watermarkScale = Math.Clamp(
                    request.WatermarkScale ??
                    GetFeatureDouble(request, "watermarkScale", "logoScale") ??
                    1.0,
                    0.01, 6.0
                );

                double watermarkRotation = request.WatermarkRotation ??
                    GetFeatureDouble(request, "watermarkRotation", "logoRotation") ??
                    0.0;

                double watermarkOpacity = Math.Clamp(
                    request.WatermarkOpacity ??
                    GetFeatureDouble(request, "watermarkOpacity", "logoOpacity") ??
                    1.0,
                    0.0, 1.0
                );

                double watermarkX = Math.Clamp(
                    request.WatermarkXPercent ??
                    GetFeatureDouble(request, "watermarkXPercent", "watermarkX", "logoX") ??
                    0.05,
                    0.0, 1.0
                );

                double watermarkY = Math.Clamp(
                    request.WatermarkYPercent ??
                    GetFeatureDouble(request, "watermarkYPercent", "watermarkY", "logoY") ??
                    0.05,
                    0.0, 1.0
                );

                double watermarkAspect = Math.Clamp(
                    request.WatermarkAspectRatio ??
                    GetFeatureDouble(request, "watermarkAspectRatio", "logoAspect") ??
                    1.0,
                    0.01, 100.0
                );

                bool enableFade = request.EnableFade ??
                    GetFeatureBool(request, "enableFade", "fadeEnabled", "fade", "fadeInOut") ??
                    false;

                double fadeInSeconds = Math.Clamp(
                    request.FadeInSeconds ??
                    GetFeatureDouble(request, "fadeInSeconds", "fadeIn", "fadeInSec") ??
                    0.8,
                    0.0, 60.0
                );

                double fadeOutSeconds = Math.Clamp(
                    request.FadeOutSeconds ??
                    GetFeatureDouble(request, "fadeOutSeconds", "fadeOut", "fadeOutSec") ??
                    0.8,
                    0.0, 60.0
                );

                job = new RenderJob
                {
                    SourcePath = inputPath,
                    MergeVideos = (mergeVideosRequested || forceMerge) && inputPaths.Count > 1,
                    MergeInputPaths = (mergeVideosRequested || forceMerge) && inputPaths.Count > 1
                        ? new List<string>(inputPaths)
                        : new List<string> { inputPath },
                    MergeAudio = mergeAudioRequested && audioPaths.Count > 1,
                    MergeAudioPaths = mergeAudioRequested && audioPaths.Count > 1
                        ? new List<string>(audioPaths)
                        : (string.IsNullOrWhiteSpace(audioPath) ? new List<string>() : new List<string> { audioPath }),
                    AudioPath = audioPath,
                    SecondaryAudioPath = secondaryAudioPath,
                    WatermarkPath = watermarkPath,
                    OutputName = outputName,
                    HardwareProfile = hardwareProfile,
                    BitrateStrategy = bitrateStrategy,
                    Resolution = resolution,
                    TargetWidth = targetWidth,
                    TargetHeight = targetHeight,
                    Framerate = framerate,
                    CustomBitrate = customBitrate,
                    VideoTrimStart = videoTrimStart,
                    VideoTrimDuration = videoTrimDuration,
                    AudioTrimStart = audioTrimStart,
                    AudioTrimEnd = audioTrimEnd,
                    AudioTrimDuration = audioTrimDuration,
                    ImageTimelineDurationSeconds = imageTimelineDurationSeconds,
                    WatermarkScale = watermarkScale,
                    WatermarkRotation = watermarkRotation,
                    WatermarkOpacity = watermarkOpacity,
                    WatermarkXPercent = watermarkX,
                    WatermarkYPercent = watermarkY,
                    WatermarkAspectRatio = watermarkAspect,
                    FxEffect = fxEffect,
                    OverlayEffect = overlayEffect,
                    BorderEffect = borderEffect,
                    EnableSnowOverlay1 = enableSnowOverlay1,
                    EnableSnowOverlay2 = enableSnowOverlay2,
                    EnableSnowOverlay3 = enableSnowOverlay3,
                    EnableOverlayIntro = enableOverlayIntro,
                    EnableCrossTransitions = enableCrossTransitions,
                    CrossTransitionType = crossTransitionType,
                    CrossTransitionDuration = crossTransitionDuration,
                    TemplateName = templateName,
                    EnablePolaroidScrapbook = enablePolaroidScrapbook,
                    EnableRgbPolaroidScrapbook = enableRgbPolaroidScrapbook,
                    EnableDashedPolaroidScrapbook = enableDashedPolaroidScrapbook,
                    EnableBrandLogo = enableBrandLogo,
                    BrandLogoPosition = brandLogoPosition,
                    SnowfallPreset = snowfallPreset,
                    SnowfallMode = snowfallMode,
                    SnowfallAssetPath = snowfallAssetPath,
                    SnowfallOpacity = snowfallOpacity,
                    SnowOverlay1Opacity = snowOverlay1Opacity,
                    SnowOverlay2Opacity = snowOverlay2Opacity,
                    SnowOverlay3Opacity = snowOverlay3Opacity,
                    OverlayIntroOpacity = overlayIntroOpacity,
                    RainOverlayPreset = rainOverlayPreset,
                    RainOverlayIntensity = rainOverlayIntensity,
                    RainOverlayAssetPath = rainOverlayAssetPath,
                    MusicSyncMode = musicSyncMode,
                    MagazineCoverTitle = string.IsNullOrWhiteSpace(rawMagazineCoverTitle) ? "TITAN" : rawMagazineCoverTitle!,
                    MagazineCoverSubtitle = string.IsNullOrWhiteSpace(rawMagazineCoverSubtitle) ? "COVER STORY" : rawMagazineCoverSubtitle!,
                    LightLeakBurnIntensity = lightLeakBurnIntensity,
                    LightLeakBurnSpread = lightLeakBurnSpread,
                    LightLeakBurnWarmth = lightLeakBurnWarmth,
                    LightLeakBurnBurn = lightLeakBurnBurn,
                    LightLeakBurnEdgeSoftness = lightLeakBurnEdgeSoftness,
                    LightLeakBurnGrain = lightLeakBurnGrain,
                    LightLeakBurnDirection = lightLeakBurnDirection,
                    LightLeakBurnDuration = lightLeakBurnDuration,
                    LightLeakBurnBlendMode = lightLeakBurnBlendMode,
                    LightLeakBurnOpacity = lightLeakBurnOpacity,
                    LightLeakBurnAssetPath = lightLeakBurnAssetPath,
                    LightLeakBurnUseAssetOverlay = lightLeakBurnUseAssetOverlay,
                    LightLeakOverlayMode = lightLeakOverlayMode,
                    LightLeakOverlayId = lightLeakOverlayId,
                    LightLeakOverlayAssetPath = lightLeakOverlayAssetPath,
                    LightLeakOverlayOpacity = lightLeakOverlayOpacity,
                    FxIntensity = fxIntensity,
                    FxStartPercent = fxStart,
                    FxEndPercent = fxEnd,
                    VideoBrightness = videoBrightness,
                    EnableFade = enableFade,
                    FadeInSeconds = fadeInSeconds,
                    FadeOutSeconds = fadeOutSeconds,
                    CropEnabled = cropEnabled,
                    CropZoomPercent = cropZoomPercent,
                    CropAspectRatio = cropZoomPercent > 0.001 ? "9:16" : cropAspectRatio,
                    CropXPercent = cropX,
                    CropYPercent = cropY,
                    CropWidthPercent = cropWidth,
                    CropHeightPercent = cropHeight,
                    ColorFilter = colorFilter,
                    ColorIntensity = colorIntensity,
                    VideoVolume = videoVolume,
                    AudioVolume = audioVolume,
                    SecondaryAudioVolume = audio2Volume,
                    Audio1Effect = request.Audio1Effect ?? GetFeatureString(request, "audio1Effect", "voiceFx", "audioEffect"),
                    Audio1EffectIntensity = request.Audio1EffectIntensity.HasValue
                        ? (int)Math.Clamp(request.Audio1EffectIntensity.Value, 0, 100)
                        : (int?)GetFeatureDouble(request, "audio1EffectIntensity", "audioFxIntensity", "fxIntensity1") ?? 100,
                    SourceAudioSpeed = sourceAudioSpeed,
                    ExternalAudioSpeed = externalAudioSpeed,
                    AudioSegments = requestAudioSegments,
                    UpscaleMode = upscaleMode,
                    SharpnessIntensity = sharpness,
                    SlowMotionSpeed = slowMotionSpeed,
                    SlowMotionAudio = slowMotionAudio,
                    SplitDurationSeconds = request.SplitDurationSeconds ?? GetFeatureDouble(request, "splitDurationSeconds", "splitDuration", "splitSec") ?? 0.0,
                    StatusDisplay = "WAITING",
                    StatusColor = Brushes.Yellow
                };
                job.IsApiJob = true;
                job.WebhookUrl = request.WebhookUrl;
                job.EnableLoudnorm = request.EnableLoudnorm ?? GetFeatureBool(request, "enableLoudnorm", "loudnorm") ?? false;
                job.TextOverlay = request.TextOverlay ?? GetFeatureString(request, "textOverlay", "text");
                job.TextOverlayPosition = request.TextOverlayPosition ?? GetFeatureString(request, "textOverlayPosition", "textPosition");
                job.TextAlign = request.TextAlign ?? GetFeatureString(request, "textAlign", "align");
                job.TextOverlayFontSize = request.TextOverlayFontSize;
                job.TextOverlayColor = request.TextOverlayColor ?? GetFeatureString(request, "textOverlayColor", "textColor");
                job.TextOverlayStyle = request.TextOverlayStyle ?? GetFeatureString(request, "textOverlayStyle", "textStyle", "fontStyle");
                job.AddQuotes = request.AddQuotes ?? GetFeatureBool(request, "addQuotes", "quotes") ?? true;
                job.DisableTextScroll = request.DisableTextScroll ?? GetFeatureBool(request, "disableTextScroll", "disableTextAnimation", "staticText", "noScroll") ?? false;
                job.TextOverlayXPercent = request.TextOverlayXPercent ?? GetFeatureDouble(request, "textOverlayXPercent", "textXPercent", "textX") ?? job.TextOverlayXPercent;
                job.TextOverlayYPercent = request.TextOverlayYPercent ?? GetFeatureDouble(request, "textOverlayYPercent", "textYPercent", "textY") ?? job.TextOverlayYPercent;
                job.EnableTextGlow = request.EnableTextGlow ?? GetFeatureBool(request, "enableTextGlow", "textGlow", "glowingText", "glow") ?? false;
                job.TextGlowColor = request.TextGlowColor ?? GetFeatureString(request, "textGlowColor", "glowColor") ?? "Cyan Neon";
                job.TextOverlayAnimation = request.TextOverlayAnimation ?? GetFeatureString(request, "textOverlayAnimation", "textAnimation", "overlayAnimation");
                job.TextOverlayEffectDuration = request.TextOverlayEffectDuration ?? GetFeatureDouble(request, "textOverlayEffectDuration", "textEffectDuration", "animationDuration") ?? 5.0;
                
                // Caption Sync mapping
                job.EnableCaptionSync = request.EnableCaptionSync ?? GetFeatureBool(request, "enableCaptionSync", "captionSync", "whisper") ?? false;
                job.CaptionSyncLanguage = request.CaptionSyncLanguage ?? GetFeatureString(request, "captionSyncLanguage", "captionLanguage", "whisperLanguage") ?? "auto";
                job.CaptionSyncModelSize = request.CaptionSyncModelSize ?? GetFeatureString(request, "captionSyncModelSize", "captionModelSize", "whisperModel") ?? "Small";
                job.CaptionSyncAnimation = request.CaptionSyncAnimation ?? GetFeatureString(request, "captionSyncAnimation", "captionAnimation", "whisperAnimation") ?? "None";
                job.CaptionSyncFontFamily = request.CaptionSyncFontFamily ?? GetFeatureString(request, "captionSyncFontFamily", "captionFont", "whisperFont") ?? "Clean Sans (Arial Italic)";
                job.CaptionSyncPosition = request.CaptionSyncPosition ?? GetFeatureString(request, "captionSyncPosition", "captionPosition", "captionStyle") ?? "Bottom Center";
                job.CaptionSyncFontSize = request.CaptionSyncFontSize ?? (int?)(GetFeatureDouble(request, "captionSyncFontSize", "captionFontSize") ?? null) ?? 28;
                job.CaptionSyncColor = request.CaptionSyncColor ?? GetFeatureString(request, "captionSyncColor", "captionColor") ?? "white";
                job.CaptionSyncXPercent = request.CaptionSyncXPercent ?? GetFeatureDouble(request, "captionSyncXPercent", "captionXPercent", "captionX") ?? 50.0;
                job.CaptionSyncYPercent = request.CaptionSyncYPercent ?? GetFeatureDouble(request, "captionSyncYPercent", "captionYPercent", "captionY") ?? 85.0;

                if (job.MergeVideos && job.MergeInputPaths.Count > 1)
                {
                    string referenceInput = job.MergeInputPaths.FirstOrDefault(p => !IsImageSourcePath(p)) ?? job.MergeInputPaths[0];
                    var firstMeta = await EngineCore.AnalyzeMediaDetailsAsync(referenceInput);
                    double totalDuration = 0.0;
                    long maxBitrate = Math.Max(0L, firstMeta.Bitrate);
                    int imageCount = job.MergeInputPaths.Count(IsImageSourcePath);
                    double imageSegmentDuration = EngineCore.ResolveImageTimelineSegmentDuration(job.ImageTimelineDurationSeconds, imageCount);
                    foreach (string mergeInput in job.MergeInputPaths)
                    {
                        if (IsImageSourcePath(mergeInput))
                        {
                            totalDuration += imageSegmentDuration;
                            continue;
                        }

                        var mergeMeta = await EngineCore.AnalyzeMediaDetailsAsync(mergeInput);
                        totalDuration += Math.Max(0.0, mergeMeta.Duration);
                        if (mergeMeta.Bitrate > maxBitrate)
                            maxBitrate = mergeMeta.Bitrate;
                    }

                    job.DurationSec = totalDuration > 0.0 ? totalDuration : firstMeta.Duration;
                    job.SourceBitrate = Math.Max(Math.Max(firstMeta.Bitrate, maxBitrate), 5000000);
                    job.SourceWidth = firstMeta.Width > 0 ? firstMeta.Width : 1920;
                    job.SourceHeight = firstMeta.Height > 0 ? firstMeta.Height : 1080;
                    job.SourceFps = firstMeta.Fps > 0.1 ? firstMeta.Fps : 30.0;
                }
                else
                {
                    if (IsImageSourcePath(job.SourcePath))
                    {
                        var meta = await EngineCore.AnalyzeMediaDetailsAsync(job.SourcePath);
                        job.DurationSec = EngineCore.ResolveImageTimelineSegmentDuration(job.ImageTimelineDurationSeconds, 1);
                        job.SourceBitrate = Math.Max(meta.Bitrate, 5000000);
                        job.SourceWidth = meta.Width > 0
                            ? meta.Width
                            : (job.TargetWidth > 0 ? job.TargetWidth : 1920);
                        job.SourceHeight = meta.Height > 0
                            ? meta.Height
                            : (job.TargetHeight > 0 ? job.TargetHeight : 1080);
                        job.SourceFps = meta.Fps > 0.1 ? meta.Fps : 30.0;
                        LogSystem($"[IMAGE-META] {job.SourceWidth}x{job.SourceHeight} @ {job.SourceFps:F2}fps (image source)");
                    }
                    else
                    {
                        var meta = await EngineCore.AnalyzeMediaDetailsAsync(job.SourcePath);
                        job.DurationSec = meta.Duration;
                        job.SourceBitrate = meta.Bitrate;
                        job.SourceWidth = meta.Width;
                        job.SourceHeight = meta.Height;
                        job.SourceFps = meta.Fps;
                    }
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    job.Id = JobQueue.Count + 1;
                    JobQueue.Add(job);
                    lstQueue.ScrollIntoView(job);
                    lstQueue.Items.Refresh();
                });

                string apiInputDesc = job.MergeVideos
                    ? $"{job.MergeInputPaths.Count} video(s) [MERGE]"
                    : Path.GetFileName(inputPath);
                LogSystem($"[API] Incoming render: {apiInputDesc} -> {outputPath}");
                LogSystem($"[API] Feature: colorgrading={colorFilter}, fx={fxEffect}, overlay={overlayEffect}, template={templateName}, crop={(cropEnabled ? "on" : "off")}, upscale={upscaleMode}");
                if (job.MergeAudio && job.MergeAudioPaths.Count > 1)
                    LogSystem($"[API] Audio merge: {job.MergeAudioPaths.Count} track(s)");
                string jobId = job.Guid;
                string statusUrl = $"{LocalApiPrefix.TrimEnd('/')}{LocalApiRenderStatusPath}?jobId={jobId}";

                SetApiJobState(new ApiRenderJobState
                {
                    QueueId = job.Id,
                    JobId = jobId,
                    Status = "queued",
                    Done = false,
                    Success = false,
                    Progress = 0,
                    Message = "Accepted",
                    RequestedOutput = outputPath,
                    InputFile = inputPath,
                    InputFiles = BuildInputFilesForJob(job),
                    MergeVideos = job.MergeVideos,
                    AudioFile = job.AudioPath,
                    AudioFile2 = job.SecondaryAudioPath,
                    AudioFiles = BuildAudioFilesForJob(job),
                    MergeAudio = job.MergeAudio,
                    AudioTrimStart = job.AudioTrimStart,
                    AudioTrimEnd = job.AudioTrimEnd,
                    AudioTrimDuration = job.AudioTrimDuration,
                    ImageTimelineDurationSeconds = job.ImageTimelineDurationSeconds,
                    AudioSegments = job.AudioSegments == null ? null : job.AudioSegments
                        .Select(seg => new AudioEditSegment
                        {
                            StartSeconds = seg.StartSeconds,
                            EndSeconds = seg.EndSeconds
                        })
                        .ToList(),
                    OutputVideo = outputPath,
                    TemplateName = job.TemplateName,
                    OverlayEffect = job.OverlayEffect,
                    EnableSnowOverlay1 = job.EnableSnowOverlay1,
                    EnableSnowOverlay2 = job.EnableSnowOverlay2,
                    EnableSnowOverlay3 = job.EnableSnowOverlay3,
                            EnableCrossTransitions = job.EnableCrossTransitions,
                            CrossTransitionType = job.CrossTransitionType,
                            CrossTransitionDuration = job.CrossTransitionDuration,
                    EnablePolaroidScrapbook = job.EnablePolaroidScrapbook,
                    EnableRgbPolaroidScrapbook = job.EnableRgbPolaroidScrapbook,
                    EnableDashedPolaroidScrapbook = job.EnableDashedPolaroidScrapbook,
                    EnableBrandLogo = job.EnableBrandLogo,
                    BrandLogoPosition = job.BrandLogoPosition,
                    SnowfallPreset = job.SnowfallPreset,
                    SnowfallMode = job.SnowfallMode,
                    SnowfallOpacity = job.SnowfallOpacity,
                    SnowOverlay1Opacity = job.SnowOverlay1Opacity,
                    SnowOverlay2Opacity = job.SnowOverlay2Opacity,
                    SnowOverlay3Opacity = job.SnowOverlay3Opacity,

                    OverlayIntroOpacity = job.OverlayIntroOpacity,
                    RainOverlayPreset = job.RainOverlayPreset,
                    RainOverlayIntensity = job.RainOverlayIntensity,
                    MusicSyncMode = job.MusicSyncMode
                });

                if (isQueueAddPath)
                {
                    await WriteApiResponseAsync(context.Response, 200, new LocalhostRenderResponse
                    {
                        Success = true,
                        QueueId = job.Id,
                        Message = job.MergeVideos
                            ? "Added merged job to queue. Call POST /api/queue/start to render"
                            : "Added to queue. Call POST /api/queue/start to render",
                        JobId = jobId,
                        Status = "queued",
                        Done = false,
                        JobSuccess = false,
                        Progress = 0,
                        RequestedOutput = outputPath,
                        InputFile = inputPath,
                        InputFiles = BuildInputFilesForJob(job),
                        InputCount = BuildInputFilesForJob(job).Count,
                        MergeVideos = job.MergeVideos,
                        AudioFile = job.AudioPath,
                        AudioFile2 = job.SecondaryAudioPath,
                        AudioFiles = BuildAudioFilesForJob(job),
                        AudioCount = BuildAudioFilesForJob(job)?.Count,
                        MergeAudio = job.MergeAudio,
                        AudioTrimStart = job.AudioTrimStart,
                        AudioTrimEnd = job.AudioTrimEnd,
                        AudioTrimDuration = job.AudioTrimDuration,
                        ImageTimelineDurationSeconds = job.ImageTimelineDurationSeconds,
                        AudioSegments = job.AudioSegments == null ? null : job.AudioSegments
                            .Select(seg => new AudioEditSegment
                            {
                                StartSeconds = seg.StartSeconds,
                                EndSeconds = seg.EndSeconds
                            })
                            .ToList(),
                        OutputVideo = outputPath,
                        TemplateName = job.TemplateName,
                        OverlayEffect = job.OverlayEffect,
                        EnableSnowOverlay1 = job.EnableSnowOverlay1,
                        EnableSnowOverlay2 = job.EnableSnowOverlay2,
                        EnableSnowOverlay3 = job.EnableSnowOverlay3,
                            EnableCrossTransitions = job.EnableCrossTransitions,
                            CrossTransitionType = job.CrossTransitionType,
                            CrossTransitionDuration = job.CrossTransitionDuration,
                        EnablePolaroidScrapbook = job.EnablePolaroidScrapbook,
                        EnableRgbPolaroidScrapbook = job.EnableRgbPolaroidScrapbook,
                        EnableDashedPolaroidScrapbook = job.EnableDashedPolaroidScrapbook,
                        EnableBrandLogo = job.EnableBrandLogo,
                        BrandLogoPosition = job.BrandLogoPosition,
                        SnowfallPreset = job.SnowfallPreset,
                        SnowfallMode = job.SnowfallMode,
                        SnowfallOpacity = job.SnowfallOpacity,
                        SnowOverlay1Opacity = job.SnowOverlay1Opacity,
                        SnowOverlay2Opacity = job.SnowOverlay2Opacity,
                        SnowOverlay3Opacity = job.SnowOverlay3Opacity,

                        OverlayIntroOpacity = job.OverlayIntroOpacity,
                        RainOverlayPreset = job.RainOverlayPreset,
                        RainOverlayIntensity = job.RainOverlayIntensity,
                        MusicSyncMode = job.MusicSyncMode,
                        StatusUrl = statusUrl,
                        VideoId = jobId,
                        SourceDuration = job.DurationSec,
                        SourceWidth = job.SourceWidth,
                        SourceHeight = job.SourceHeight,
                        SourceBitrate = job.SourceBitrate,
                        SourceFramerate = job.SourceFps,
                        OutputWidth = GetEffectiveOutputWidth(job),
                        OutputHeight = GetEffectiveOutputHeight(job),
                        OutputFramerate = job.Framerate,
                        EnableFade = job.EnableFade,
                        FadeInSeconds = job.FadeInSeconds,
                        FadeOutSeconds = job.FadeOutSeconds,
                        SourceFileName = Path.GetFileName(job.SourcePath),
                        OutputFileName = Path.GetFileName(outputPath),
                        Jobs = new List<LocalhostQueueJobInfo> { BuildQueueJobInfo(job, GetApiJobState(jobId)) }
                    });
                    return;
                }

                if (!waitForCompletion)
                {
                    lockTransferred = true;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await RunApiRenderCoreAsync(job, outputDir, outputPath, token);

                            SetApiJobState(new ApiRenderJobState
                            {
                                QueueId = job.Id,
                                JobId = jobId,
                                Status = "completed",
                                Done = true,
                                Success = true,
                                Progress = 100,
                                Message = "Render completed",
                                RequestedOutput = outputPath,
                                InputFile = inputPath,
                                OutputVideo = outputPath,
                                EngineOutput = result.EngineOutput,
                                FinalOutput = result.FinalOutput,
                                TemplateName = job.TemplateName,
                                EnablePolaroidScrapbook = job.EnablePolaroidScrapbook,
                                EnableRgbPolaroidScrapbook = job.EnableRgbPolaroidScrapbook,
                                EnableDashedPolaroidScrapbook = job.EnableDashedPolaroidScrapbook,
                                EnableBrandLogo = job.EnableBrandLogo,
                                BrandLogoPosition = job.BrandLogoPosition,
                                MusicSyncMode = job.MusicSyncMode,
                                ImageTimelineDurationSeconds = job.ImageTimelineDurationSeconds
                            });
                        }
                        catch (Exception ex)
                        {
                            SetApiJobState(new ApiRenderJobState
                            {
                                QueueId = job.Id,
                                JobId = jobId,
                                Status = "failed",
                                Done = true,
                                Success = false,
                                Progress = job.Progress,
                                Message = ex.Message,
                                RequestedOutput = outputPath,
                                InputFile = inputPath,
                                OutputVideo = outputPath,
                                EngineOutput = job.ResultPath,
                                TemplateName = job.TemplateName,
                                EnablePolaroidScrapbook = job.EnablePolaroidScrapbook,
                                EnableRgbPolaroidScrapbook = job.EnableRgbPolaroidScrapbook,
                                EnableDashedPolaroidScrapbook = job.EnableDashedPolaroidScrapbook,
                                EnableBrandLogo = job.EnableBrandLogo,
                                BrandLogoPosition = job.BrandLogoPosition,
                                MusicSyncMode = job.MusicSyncMode,
                                ImageTimelineDurationSeconds = job.ImageTimelineDurationSeconds
                            });
                        }
                        finally
                        {
                            if (lockTaken)
                                _localApiRenderLock.Release();
                        }
                    });

                    await WriteApiResponseAsync(context.Response, 200, new LocalhostRenderResponse
                    {
                        Success = true,
                        QueueId = job.Id,
                        Message = job.MergeVideos
                            ? "Accepted merged job. Poll statusUrl for completion"
                            : "Accepted. Poll statusUrl for completion",
                        JobId = jobId,
                        Status = "queued",
                        Done = false,
                        JobSuccess = false,
                        Progress = 0,
                        RequestedOutput = outputPath,
                        InputFile = inputPath,
                        InputFiles = BuildInputFilesForJob(job),
                        InputCount = BuildInputFilesForJob(job).Count,
                        MergeVideos = job.MergeVideos,
                        AudioFile = job.AudioPath,
                        AudioFile2 = job.SecondaryAudioPath,
                        AudioFiles = BuildAudioFilesForJob(job),
                        AudioCount = BuildAudioFilesForJob(job)?.Count,
                        MergeAudio = job.MergeAudio,
                        OutputVideo = outputPath,
                        TemplateName = job.TemplateName,
                        OverlayEffect = job.OverlayEffect,
                        EnableSnowOverlay1 = job.EnableSnowOverlay1,
                        EnableSnowOverlay2 = job.EnableSnowOverlay2,
                        EnableSnowOverlay3 = job.EnableSnowOverlay3,
                            EnableCrossTransitions = job.EnableCrossTransitions,
                            CrossTransitionType = job.CrossTransitionType,
                            CrossTransitionDuration = job.CrossTransitionDuration,
                        EnablePolaroidScrapbook = job.EnablePolaroidScrapbook,
                        EnableRgbPolaroidScrapbook = job.EnableRgbPolaroidScrapbook,
                        EnableDashedPolaroidScrapbook = job.EnableDashedPolaroidScrapbook,
                        EnableBrandLogo = job.EnableBrandLogo,
                        BrandLogoPosition = job.BrandLogoPosition,
                        SnowfallPreset = job.SnowfallPreset,
                        SnowfallMode = job.SnowfallMode,
                        SnowfallOpacity = job.SnowfallOpacity,
                        SnowOverlay1Opacity = job.SnowOverlay1Opacity,
                        SnowOverlay2Opacity = job.SnowOverlay2Opacity,
                        SnowOverlay3Opacity = job.SnowOverlay3Opacity,

                        OverlayIntroOpacity = job.OverlayIntroOpacity,
                        RainOverlayPreset = job.RainOverlayPreset,
                        RainOverlayIntensity = job.RainOverlayIntensity,
                        MusicSyncMode = job.MusicSyncMode,
                        StatusUrl = statusUrl,
                        VideoId = jobId,
                        SourceDuration = job.DurationSec,
                        SourceWidth = job.SourceWidth,
                        SourceHeight = job.SourceHeight,
                        SourceBitrate = job.SourceBitrate,
                        SourceFramerate = job.SourceFps,
                        OutputWidth = GetEffectiveOutputWidth(job),
                        OutputHeight = GetEffectiveOutputHeight(job),
                        OutputFramerate = job.Framerate,
                        EnableFade = job.EnableFade,
                        FadeInSeconds = job.FadeInSeconds,
                        FadeOutSeconds = job.FadeOutSeconds,
                        ImageTimelineDurationSeconds = job.ImageTimelineDurationSeconds,
                        SourceFileName = Path.GetFileName(job.SourcePath),
                        OutputFileName = Path.GetFileName(outputPath)
                    });
                    return;
                }

                streamingResponseStarted = true;
                await WriteBlockingApiResponseAsync(context.Response, async () =>
                {
                    try
                    {
                        var syncResult = await RunApiRenderCoreAsync(job, outputDir, outputPath, token);
                        SetApiJobState(new ApiRenderJobState
                        {
                            QueueId = job.Id,
                            JobId = jobId,
                            Status = "completed",
                            Done = true,
                            Success = true,
                            Progress = 100,
                        Message = "Render completed",
                        RequestedOutput = outputPath,
                        InputFile = inputPath,
                        OutputVideo = outputPath,
                        EngineOutput = syncResult.EngineOutput,
                        FinalOutput = syncResult.FinalOutput,
                        TemplateName = job.TemplateName,
                        OverlayEffect = job.OverlayEffect,
                        EnableSnowOverlay1 = job.EnableSnowOverlay1,
                        EnableSnowOverlay2 = job.EnableSnowOverlay2,
                        EnableSnowOverlay3 = job.EnableSnowOverlay3,
                            EnableCrossTransitions = job.EnableCrossTransitions,
                            CrossTransitionType = job.CrossTransitionType,
                            CrossTransitionDuration = job.CrossTransitionDuration,
                        EnablePolaroidScrapbook = job.EnablePolaroidScrapbook,
                        EnableRgbPolaroidScrapbook = job.EnableRgbPolaroidScrapbook,
                        EnableDashedPolaroidScrapbook = job.EnableDashedPolaroidScrapbook,
                        EnableBrandLogo = job.EnableBrandLogo,
                        BrandLogoPosition = job.BrandLogoPosition,
                        SnowfallPreset = job.SnowfallPreset,
                        SnowfallMode = job.SnowfallMode,
                        SnowfallOpacity = job.SnowfallOpacity,
                        SnowOverlay1Opacity = job.SnowOverlay1Opacity,
                        SnowOverlay2Opacity = job.SnowOverlay2Opacity,
                        SnowOverlay3Opacity = job.SnowOverlay3Opacity,

                        OverlayIntroOpacity = job.OverlayIntroOpacity,
                        RainOverlayPreset = job.RainOverlayPreset,
                        RainOverlayIntensity = job.RainOverlayIntensity,
                        MusicSyncMode = job.MusicSyncMode,
                        ImageTimelineDurationSeconds = job.ImageTimelineDurationSeconds
                    });

                        // [NEW] Get output file info
                        long outputFileSize = 0;
                        if (File.Exists(syncResult.FinalOutput))
                        {
                            outputFileSize = new FileInfo(syncResult.FinalOutput).Length;
                        }

                        double encodingTime = (job.EndTime - job.StartTime).TotalSeconds;

                        return new LocalhostRenderResponse
                        {
                            Success = true,
                            QueueId = job.Id,
                            Message = "Render completed",
                            JobId = jobId,
                            Status = "completed",
                            Done = true,
                            JobSuccess = true,
                            Progress = 100,
                            RequestedOutput = outputPath,
                            InputFile = inputPath,
                            InputFiles = BuildInputFilesForJob(job),
                            InputCount = BuildInputFilesForJob(job).Count,
                            MergeVideos = job.MergeVideos,
                            AudioFile = job.AudioPath,
                            AudioFile2 = job.SecondaryAudioPath,
                            AudioFiles = BuildAudioFilesForJob(job),
                            AudioCount = BuildAudioFilesForJob(job)?.Count,
                            MergeAudio = job.MergeAudio,
                            AudioTrimStart = job.AudioTrimStart,
                            AudioTrimEnd = job.AudioTrimEnd,
                            AudioTrimDuration = job.AudioTrimDuration,
                            AudioSegments = job.AudioSegments == null ? null : job.AudioSegments
                                .Select(seg => new AudioEditSegment
                                {
                                    StartSeconds = seg.StartSeconds,
                                    EndSeconds = seg.EndSeconds
                                })
                                .ToList(),
                            OutputVideo = outputPath,
                            EngineOutput = syncResult.EngineOutput,
                            FinalOutput = syncResult.FinalOutput,
                            TemplateName = job.TemplateName,
                            OverlayEffect = job.OverlayEffect,
                            EnableSnowOverlay1 = job.EnableSnowOverlay1,
                            EnableSnowOverlay2 = job.EnableSnowOverlay2,
                            EnableSnowOverlay3 = job.EnableSnowOverlay3,
                            EnableCrossTransitions = job.EnableCrossTransitions,
                            CrossTransitionType = job.CrossTransitionType,
                            CrossTransitionDuration = job.CrossTransitionDuration,
                            EnablePolaroidScrapbook = job.EnablePolaroidScrapbook,
                            EnableRgbPolaroidScrapbook = job.EnableRgbPolaroidScrapbook,
                            EnableDashedPolaroidScrapbook = job.EnableDashedPolaroidScrapbook,
                            EnableBrandLogo = job.EnableBrandLogo,
                            BrandLogoPosition = job.BrandLogoPosition,
                            SnowfallPreset = job.SnowfallPreset,
                            SnowfallMode = job.SnowfallMode,
                            SnowfallOpacity = job.SnowfallOpacity,
                            SnowOverlay1Opacity = job.SnowOverlay1Opacity,
                            SnowOverlay2Opacity = job.SnowOverlay2Opacity,
                            SnowOverlay3Opacity = job.SnowOverlay3Opacity,

                            OverlayIntroOpacity = job.OverlayIntroOpacity,
                            RainOverlayPreset = job.RainOverlayPreset,
                            RainOverlayIntensity = job.RainOverlayIntensity,
                            MusicSyncMode = job.MusicSyncMode,
                            StatusUrl = statusUrl,
                            // [NEW] Video Information
                            VideoId = jobId,
                            SourceDuration = job.DurationSec,
                            SourceWidth = job.SourceWidth,
                            SourceHeight = job.SourceHeight,
                            SourceBitrate = job.SourceBitrate,
                            SourceFramerate = job.SourceFps,
                            OutputWidth = GetEffectiveOutputWidth(job),
                            OutputHeight = GetEffectiveOutputHeight(job),
                            OutputFramerate = job.Framerate,
                            EnableFade = job.EnableFade,
                            FadeInSeconds = job.FadeInSeconds,
                            FadeOutSeconds = job.FadeOutSeconds,
                            ImageTimelineDurationSeconds = job.ImageTimelineDurationSeconds,
                            SourceFileName = Path.GetFileName(job.SourcePath),
                            OutputFileName = Path.GetFileName(syncResult.FinalOutput),
                            OutputFileSize = outputFileSize,
                            EncodingTime = encodingTime
                        };
                    }
                    catch (OperationCanceledException)
                    {
                        SetApiJobState(new ApiRenderJobState
                        {
                            QueueId = job.Id,
                            JobId = jobId,
                            Status = "failed",
                            Done = true,
                            Success = false,
                            Progress = job.Progress,
                            Message = "Request cancelled",
                            RequestedOutput = outputPath,
                            InputFile = inputPath,
                            OutputVideo = outputPath,
                            EngineOutput = job.ResultPath,
                            TemplateName = job.TemplateName,
                            EnablePolaroidScrapbook = job.EnablePolaroidScrapbook,
                            EnableRgbPolaroidScrapbook = job.EnableRgbPolaroidScrapbook,
                            EnableDashedPolaroidScrapbook = job.EnableDashedPolaroidScrapbook,
                            EnableBrandLogo = job.EnableBrandLogo,
                            BrandLogoPosition = job.BrandLogoPosition,
                            MusicSyncMode = job.MusicSyncMode,
                            ImageTimelineDurationSeconds = job.ImageTimelineDurationSeconds
                        });

                        return new LocalhostRenderResponse
                        {
                            Success = false,
                            QueueId = job.Id,
                            Message = "Request cancelled",
                            JobId = jobId,
                            Status = "failed",
                            Done = true,
                            JobSuccess = false,
                            Progress = job.Progress,
                            RequestedOutput = outputPath,
                            InputFile = inputPath,
                            InputFiles = BuildInputFilesForJob(job),
                            InputCount = BuildInputFilesForJob(job).Count,
                            MergeVideos = job.MergeVideos,
                            AudioFile = job.AudioPath,
                            AudioFile2 = job.SecondaryAudioPath,
                            AudioFiles = BuildAudioFilesForJob(job),
                            AudioCount = BuildAudioFilesForJob(job)?.Count,
                            MergeAudio = job.MergeAudio,
                            AudioTrimStart = job.AudioTrimStart,
                            AudioTrimEnd = job.AudioTrimEnd,
                            AudioTrimDuration = job.AudioTrimDuration,
                            AudioSegments = job.AudioSegments == null ? null : job.AudioSegments
                                .Select(seg => new AudioEditSegment
                                {
                                    StartSeconds = seg.StartSeconds,
                                    EndSeconds = seg.EndSeconds
                                })
                                .ToList(),
                            OutputVideo = outputPath,
                            EngineOutput = job.ResultPath,
                            TemplateName = job.TemplateName,
                            OverlayEffect = job.OverlayEffect,
                            EnableSnowOverlay1 = job.EnableSnowOverlay1,
                            EnableSnowOverlay2 = job.EnableSnowOverlay2,
                            EnableSnowOverlay3 = job.EnableSnowOverlay3,
                            EnableCrossTransitions = job.EnableCrossTransitions,
                            CrossTransitionType = job.CrossTransitionType,
                            CrossTransitionDuration = job.CrossTransitionDuration,
                            EnablePolaroidScrapbook = job.EnablePolaroidScrapbook,
                            EnableRgbPolaroidScrapbook = job.EnableRgbPolaroidScrapbook,
                            EnableDashedPolaroidScrapbook = job.EnableDashedPolaroidScrapbook,
                            EnableBrandLogo = job.EnableBrandLogo,
                            BrandLogoPosition = job.BrandLogoPosition,
                            SnowfallPreset = job.SnowfallPreset,
                            SnowfallMode = job.SnowfallMode,
                            SnowfallOpacity = job.SnowfallOpacity,
                            SnowOverlay1Opacity = job.SnowOverlay1Opacity,
                            SnowOverlay2Opacity = job.SnowOverlay2Opacity,
                            SnowOverlay3Opacity = job.SnowOverlay3Opacity,

                            OverlayIntroOpacity = job.OverlayIntroOpacity,
                            RainOverlayPreset = job.RainOverlayPreset,
                            RainOverlayIntensity = job.RainOverlayIntensity,
                            MusicSyncMode = job.MusicSyncMode,
                            StatusUrl = statusUrl,
                            VideoId = jobId,
                            SourceFileName = Path.GetFileName(job.SourcePath),
                            EnableFade = job.EnableFade,
                            FadeInSeconds = job.FadeInSeconds,
                            FadeOutSeconds = job.FadeOutSeconds,
                            ImageTimelineDurationSeconds = job.ImageTimelineDurationSeconds
                        };
                    }
                    catch (Exception ex)
                    {
                        LogSystem($"[API-ERROR] {ex.Message}");
                        SetApiJobState(new ApiRenderJobState
                        {
                            QueueId = job.Id,
                            JobId = jobId,
                            Status = "failed",
                            Done = true,
                            Success = false,
                            Progress = job.Progress,
                            Message = ex.Message,
                            RequestedOutput = outputPath,
                            InputFile = inputPath,
                            OutputVideo = outputPath,
                            EngineOutput = job.ResultPath,
                            TemplateName = job.TemplateName,
                            OverlayEffect = job.OverlayEffect,
                            EnableSnowOverlay1 = job.EnableSnowOverlay1,
                            EnableSnowOverlay2 = job.EnableSnowOverlay2,
                            EnableSnowOverlay3 = job.EnableSnowOverlay3,
                            EnableCrossTransitions = job.EnableCrossTransitions,
                            CrossTransitionType = job.CrossTransitionType,
                            CrossTransitionDuration = job.CrossTransitionDuration,
                            EnablePolaroidScrapbook = job.EnablePolaroidScrapbook,
                            EnableRgbPolaroidScrapbook = job.EnableRgbPolaroidScrapbook,
                            EnableDashedPolaroidScrapbook = job.EnableDashedPolaroidScrapbook,
                            EnableBrandLogo = job.EnableBrandLogo,
                            BrandLogoPosition = job.BrandLogoPosition,
                            SnowfallPreset = job.SnowfallPreset,
                            SnowfallMode = job.SnowfallMode,
                            SnowfallOpacity = job.SnowfallOpacity,
                            SnowOverlay1Opacity = job.SnowOverlay1Opacity,
                            SnowOverlay2Opacity = job.SnowOverlay2Opacity,
                            SnowOverlay3Opacity = job.SnowOverlay3Opacity,

                            OverlayIntroOpacity = job.OverlayIntroOpacity,
                            RainOverlayPreset = job.RainOverlayPreset,
                            RainOverlayIntensity = job.RainOverlayIntensity,
                            MusicSyncMode = job.MusicSyncMode
                        });

                        return new LocalhostRenderResponse
                        {
                            Success = false,
                            QueueId = job.Id,
                            Message = ex.Message,
                            JobId = jobId,
                            Status = "failed",
                            Done = true,
                            JobSuccess = false,
                            Progress = job.Progress,
                            RequestedOutput = outputPath,
                            InputFile = inputPath,
                            InputFiles = BuildInputFilesForJob(job),
                            InputCount = BuildInputFilesForJob(job).Count,
                            MergeVideos = job.MergeVideos,
                            AudioFile = job.AudioPath,
                            AudioFile2 = job.SecondaryAudioPath,
                            AudioFiles = BuildAudioFilesForJob(job),
                            AudioCount = BuildAudioFilesForJob(job)?.Count,
                            MergeAudio = job.MergeAudio,
                            OutputVideo = outputPath,
                            EngineOutput = job.ResultPath,
                            TemplateName = job.TemplateName,
                            OverlayEffect = job.OverlayEffect,
                            EnableSnowOverlay1 = job.EnableSnowOverlay1,
                            EnableSnowOverlay2 = job.EnableSnowOverlay2,
                            EnableSnowOverlay3 = job.EnableSnowOverlay3,
                            EnableCrossTransitions = job.EnableCrossTransitions,
                            CrossTransitionType = job.CrossTransitionType,
                            CrossTransitionDuration = job.CrossTransitionDuration,
                            EnablePolaroidScrapbook = job.EnablePolaroidScrapbook,
                            EnableRgbPolaroidScrapbook = job.EnableRgbPolaroidScrapbook,
                            EnableDashedPolaroidScrapbook = job.EnableDashedPolaroidScrapbook,
                            EnableBrandLogo = job.EnableBrandLogo,
                            BrandLogoPosition = job.BrandLogoPosition,
                            SnowfallPreset = job.SnowfallPreset,
                            SnowfallMode = job.SnowfallMode,
                            SnowfallOpacity = job.SnowfallOpacity,
                            SnowOverlay1Opacity = job.SnowOverlay1Opacity,
                            SnowOverlay2Opacity = job.SnowOverlay2Opacity,
                            SnowOverlay3Opacity = job.SnowOverlay3Opacity,

                            OverlayIntroOpacity = job.OverlayIntroOpacity,
                            RainOverlayPreset = job.RainOverlayPreset,
                            RainOverlayIntensity = job.RainOverlayIntensity,
                            MusicSyncMode = job.MusicSyncMode,
                            StatusUrl = statusUrl,
                            VideoId = jobId,
                            SourceFileName = Path.GetFileName(job.SourcePath),
                            EnableFade = job.EnableFade,
                            FadeInSeconds = job.FadeInSeconds,
                            FadeOutSeconds = job.FadeOutSeconds,
                            ImageTimelineDurationSeconds = job.ImageTimelineDurationSeconds
                        };
                    }
                }, token);
                return;
            }
            catch (OperationCanceledException)
            {
                if (job != null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        job.StatusDisplay = "FAILED (API)";
                        job.StatusColor = Brushes.Red;
                    });

                    SetApiJobState(new ApiRenderJobState
                    {
                        QueueId = job.Id,
                        JobId = job.Guid,
                        Status = "failed",
                        Done = true,
                        Success = false,
                        Progress = job.Progress,
                        Message = "Request cancelled",
                        EngineOutput = job.ResultPath,
                        TemplateName = job.TemplateName,
                        EnablePolaroidScrapbook = job.EnablePolaroidScrapbook,
                        EnableRgbPolaroidScrapbook = job.EnableRgbPolaroidScrapbook,
                        EnableDashedPolaroidScrapbook = job.EnableDashedPolaroidScrapbook,
                        EnableBrandLogo = job.EnableBrandLogo,
                        BrandLogoPosition = job.BrandLogoPosition,
                        MusicSyncMode = job.MusicSyncMode
                    });
                }

                try
                {
                    if (!streamingResponseStarted)
                    {
                        await WriteApiResponseAsync(context.Response, 499, new LocalhostRenderResponse
                        {
                            Success = false,
                            Message = "Request cancelled"
                        });
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                if (job != null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        job.StatusDisplay = "FAILED (API)";
                        job.StatusColor = Brushes.Red;
                    });

                    SetApiJobState(new ApiRenderJobState
                    {
                        QueueId = job.Id,
                        JobId = job.Guid,
                        Status = "failed",
                        Done = true,
                        Success = false,
                        Progress = job.Progress,
                        Message = ex.Message,
                        EngineOutput = job.ResultPath,
                        TemplateName = job.TemplateName,
                        EnablePolaroidScrapbook = job.EnablePolaroidScrapbook,
                        EnableRgbPolaroidScrapbook = job.EnableRgbPolaroidScrapbook,
                        EnableDashedPolaroidScrapbook = job.EnableDashedPolaroidScrapbook,
                        EnableBrandLogo = job.EnableBrandLogo,
                        BrandLogoPosition = job.BrandLogoPosition,
                        MusicSyncMode = job.MusicSyncMode
                    });
                }

                LogSystem($"[API-ERROR] {ex.Message}");
                try
                {
                    if (!streamingResponseStarted)
                    {
                        await WriteApiResponseAsync(context.Response, 500, new LocalhostRenderResponse
                        {
                            Success = false,
                            Message = ex.Message
                        });
                    }
                }
                catch { }
            }
            finally
            {
                if (lockTaken && !lockTransferred)
                {
                    _localApiRenderLock.Release();
                }
            }
        }

        private void SetApiJobState(ApiRenderJobState state)
        {
            lock (_apiJobStateLock)
            {
                if (_apiJobStates.TryGetValue(state.JobId, out ApiRenderJobState? previous))
                {
                    if (state.InputFiles == null || state.InputFiles.Count == 0)
                    {
                        if (previous.InputFiles != null && previous.InputFiles.Count > 0)
                            state.InputFiles = new List<string>(previous.InputFiles);
                    }
                    if (state.AudioFiles == null || state.AudioFiles.Count == 0)
                    {
                        if (previous.AudioFiles != null && previous.AudioFiles.Count > 0)
                            state.AudioFiles = new List<string>(previous.AudioFiles);
                    }

                    if (string.IsNullOrWhiteSpace(state.InputFile))
                        state.InputFile = previous.InputFile;
                    if (string.IsNullOrWhiteSpace(state.AudioFile))
                        state.AudioFile = previous.AudioFile;
                    if (string.IsNullOrWhiteSpace(state.TemplateName))
                        state.TemplateName = previous.TemplateName;
                    if (string.IsNullOrWhiteSpace(state.OverlayEffect))
                        state.OverlayEffect = previous.OverlayEffect;
                    if (!state.EnableSnowOverlay1 && previous.EnableSnowOverlay1)
                        state.EnableSnowOverlay1 = true;
                    if (!state.EnableSnowOverlay2 && previous.EnableSnowOverlay2)
                    if (!state.EnableSnowOverlay3 && previous.EnableSnowOverlay3)
                        state.EnableSnowOverlay2 = true;
                        state.EnableSnowOverlay3 = true;
                    if (!state.EnablePolaroidScrapbook && previous.EnablePolaroidScrapbook)
                        state.EnablePolaroidScrapbook = true;
                    if (!state.EnableRgbPolaroidScrapbook && previous.EnableRgbPolaroidScrapbook)
                        state.EnableRgbPolaroidScrapbook = true;
                    if (!state.EnableDashedPolaroidScrapbook && previous.EnableDashedPolaroidScrapbook)
                        state.EnableDashedPolaroidScrapbook = true;
                    if (!state.EnableBrandLogo && previous.EnableBrandLogo)
                        state.EnableBrandLogo = true;
                    if (string.IsNullOrWhiteSpace(state.BrandLogoPosition))
                        state.BrandLogoPosition = previous.BrandLogoPosition;
                    if (string.IsNullOrWhiteSpace(state.SnowfallPreset))
                        state.SnowfallPreset = previous.SnowfallPreset;
                    if (string.IsNullOrWhiteSpace(state.SnowfallMode))
                        state.SnowfallMode = previous.SnowfallMode;
                    if (state.SnowfallOpacity <= 0.0 && previous.SnowfallOpacity > 0.0)
                        state.SnowfallOpacity = previous.SnowfallOpacity;
                    if (state.SnowOverlay1Opacity <= 0.0 && previous.SnowOverlay1Opacity > 0.0)
                        state.SnowOverlay1Opacity = previous.SnowOverlay1Opacity;
                    if (state.SnowOverlay2Opacity <= 0.0 && previous.SnowOverlay2Opacity > 0.0)
                    if (state.SnowOverlay3Opacity <= 0.0 && previous.SnowOverlay3Opacity > 0.0)
                        state.SnowOverlay2Opacity = previous.SnowOverlay2Opacity;
                        state.SnowOverlay3Opacity = previous.SnowOverlay3Opacity;
                    if (string.IsNullOrWhiteSpace(state.RainOverlayPreset))
                        state.RainOverlayPreset = previous.RainOverlayPreset;
                    if (string.IsNullOrWhiteSpace(state.RainOverlayIntensity))
                        state.RainOverlayIntensity = previous.RainOverlayIntensity;
                    if (string.IsNullOrWhiteSpace(state.MusicSyncMode))
                        state.MusicSyncMode = previous.MusicSyncMode;
                    if (string.IsNullOrWhiteSpace(state.AudioTrimStart))
                        state.AudioTrimStart = previous.AudioTrimStart;
                    if (string.IsNullOrWhiteSpace(state.AudioTrimEnd))
                        state.AudioTrimEnd = previous.AudioTrimEnd;
                    if (string.IsNullOrWhiteSpace(state.AudioTrimDuration))
                        state.AudioTrimDuration = previous.AudioTrimDuration;
                    if (state.ImageTimelineDurationSeconds <= 0.0 && previous.ImageTimelineDurationSeconds > 0.0)
                        state.ImageTimelineDurationSeconds = previous.ImageTimelineDurationSeconds;
                    if (state.CropZoomPercent <= 0.0 && previous.CropZoomPercent > 0.0)
                        state.CropZoomPercent = previous.CropZoomPercent;
                    if (string.IsNullOrWhiteSpace(state.CropAspectRatio))
                        state.CropAspectRatio = previous.CropAspectRatio;
                    if (state.AudioSegments == null || state.AudioSegments.Count == 0)
                    {
                        if (previous.AudioSegments != null && previous.AudioSegments.Count > 0)
                            state.AudioSegments = previous.AudioSegments
                                .Select(seg => new AudioEditSegment
                                {
                                    StartSeconds = seg.StartSeconds,
                                    EndSeconds = seg.EndSeconds
                                })
                                .ToList();
                    }

                    if (!state.MergeVideos && previous.MergeVideos)
                        state.MergeVideos = true;
                    if (!state.MergeAudio && previous.MergeAudio)
                        state.MergeAudio = true;
                }

                _apiJobStates[state.JobId] = state;
            }

            if (state.Done && !string.IsNullOrWhiteSpace(state.WebhookUrl))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var client = new HttpClient();
                        client.Timeout = TimeSpan.FromSeconds(10);
                        var responsePayload = new LocalhostRenderResponse
                        {
                            Success = state.Success,
                            JobId = state.JobId,
                            Status = state.Status,
                            Done = state.Done,
                            JobSuccess = state.Success,
                            Progress = state.Progress,
                            Message = state.Message,
                            RequestedOutput = state.RequestedOutput,
                            EngineOutput = state.EngineOutput,
                            FinalOutput = state.FinalOutput
                        };
                        var json = JsonSerializer.Serialize(responsePayload, _localApiJsonOptions);
                        var content = new StringContent(json, Encoding.UTF8, "application/json");
                        await client.PostAsync(state.WebhookUrl, content);
                        LogSystem($"[API-WEBHOOK] Sent status for job {state.JobId} to {state.WebhookUrl}");
                    }
                    catch (Exception ex)
                    {
                        LogSystem($"[API-WEBHOOK-ERROR] Failed to send webhook to {state.WebhookUrl}: {ex.Message}");
                    }
                });
            }
        }

        private ApiRenderJobState? GetApiJobState(string jobId)
        {
            lock (_apiJobStateLock)
            {
                if (_apiJobStates.TryGetValue(jobId, out ApiRenderJobState? state))
                {
                    return new ApiRenderJobState
                    {
                        QueueId = state.QueueId,
                        JobId = state.JobId,
                        Status = state.Status,
                        Done = state.Done,
                        Success = state.Success,
                        Progress = state.Progress,
                        Message = state.Message,
                        RequestedOutput = state.RequestedOutput,
                        EngineOutput = state.EngineOutput,
                        FinalOutput = state.FinalOutput,
                        InputFile = state.InputFile,
                        InputFiles = state.InputFiles == null ? null : new List<string>(state.InputFiles),
                        MergeVideos = state.MergeVideos,
                        AudioFile = state.AudioFile,
                        AudioFile2 = state.AudioFile2,
                        AudioFiles = state.AudioFiles == null ? null : new List<string>(state.AudioFiles),
                        MergeAudio = state.MergeAudio,
                        AudioTrimStart = state.AudioTrimStart,
                        AudioTrimEnd = state.AudioTrimEnd,
                        AudioTrimDuration = state.AudioTrimDuration,
                        ImageTimelineDurationSeconds = state.ImageTimelineDurationSeconds,
                        CropZoomPercent = state.CropZoomPercent,
                        CropAspectRatio = state.CropAspectRatio,
                        AudioSegments = state.AudioSegments == null ? null : state.AudioSegments
                            .Select(seg => new AudioEditSegment
                            {
                                StartSeconds = seg.StartSeconds,
                                EndSeconds = seg.EndSeconds
                            })
                            .ToList(),
                        OutputVideo = state.OutputVideo,
                        TemplateName = state.TemplateName,
                        OverlayEffect = state.OverlayEffect,
                        EnableSnowOverlay1 = state.EnableSnowOverlay1,
                        EnableSnowOverlay2 = state.EnableSnowOverlay2,
                        EnableSnowOverlay3 = state.EnableSnowOverlay3,
                        EnableCrossTransitions = state.EnableCrossTransitions,
                        CrossTransitionType = state.CrossTransitionType,
                        CrossTransitionDuration = state.CrossTransitionDuration,
                        EnablePolaroidScrapbook = state.EnablePolaroidScrapbook,
                        EnableRgbPolaroidScrapbook = state.EnableRgbPolaroidScrapbook,
                        EnableDashedPolaroidScrapbook = state.EnableDashedPolaroidScrapbook,
                        EnableBrandLogo = state.EnableBrandLogo,
                        BrandLogoPosition = state.BrandLogoPosition,
                        SnowfallPreset = state.SnowfallPreset,
                        SnowfallMode = state.SnowfallMode,
                        SnowfallOpacity = state.SnowfallOpacity,
                        SnowOverlay1Opacity = state.SnowOverlay1Opacity,
                        SnowOverlay2Opacity = state.SnowOverlay2Opacity,
                        SnowOverlay3Opacity = state.SnowOverlay3Opacity,

                        OverlayIntroOpacity = state.OverlayIntroOpacity,
                        RainOverlayPreset = state.RainOverlayPreset,
                        RainOverlayIntensity = state.RainOverlayIntensity,
                        MusicSyncMode = state.MusicSyncMode
                    };
                }

                if (int.TryParse(jobId, out int queueId))
                {
                    ApiRenderJobState? queueState = _apiJobStates.Values
                        .FirstOrDefault(x => x.QueueId == queueId);

                    if (queueState != null)
                    {
                        return new ApiRenderJobState
                        {
                            QueueId = queueState.QueueId,
                            JobId = queueState.JobId,
                            Status = queueState.Status,
                            Done = queueState.Done,
                            Success = queueState.Success,
                            Progress = queueState.Progress,
                            Message = queueState.Message,
                            RequestedOutput = queueState.RequestedOutput,
                            EngineOutput = queueState.EngineOutput,
                            FinalOutput = queueState.FinalOutput,
                            InputFile = queueState.InputFile,
                            InputFiles = queueState.InputFiles == null ? null : new List<string>(queueState.InputFiles),
                            MergeVideos = queueState.MergeVideos,
                            AudioFile = queueState.AudioFile,
                            AudioFile2 = queueState.AudioFile2,
                            AudioFiles = queueState.AudioFiles == null ? null : new List<string>(queueState.AudioFiles),
                            MergeAudio = queueState.MergeAudio,
                            AudioTrimStart = queueState.AudioTrimStart,
                            AudioTrimEnd = queueState.AudioTrimEnd,
                            AudioTrimDuration = queueState.AudioTrimDuration,
                            ImageTimelineDurationSeconds = queueState.ImageTimelineDurationSeconds,
                            CropZoomPercent = queueState.CropZoomPercent,
                            CropAspectRatio = queueState.CropAspectRatio,
                            AudioSegments = queueState.AudioSegments == null ? null : queueState.AudioSegments
                                .Select(seg => new AudioEditSegment
                                {
                                    StartSeconds = seg.StartSeconds,
                                    EndSeconds = seg.EndSeconds
                                })
                                .ToList(),
                            OutputVideo = queueState.OutputVideo,
                            TemplateName = queueState.TemplateName,
                            OverlayEffect = queueState.OverlayEffect,
                            EnableSnowOverlay1 = queueState.EnableSnowOverlay1,
                            EnableSnowOverlay2 = queueState.EnableSnowOverlay2,
                            EnableSnowOverlay3 = queueState.EnableSnowOverlay3,
                            EnableCrossTransitions = queueState.EnableCrossTransitions,
                            CrossTransitionType = queueState.CrossTransitionType,
                            CrossTransitionDuration = queueState.CrossTransitionDuration,
                            EnablePolaroidScrapbook = queueState.EnablePolaroidScrapbook,
                            EnableRgbPolaroidScrapbook = queueState.EnableRgbPolaroidScrapbook,
                            EnableDashedPolaroidScrapbook = queueState.EnableDashedPolaroidScrapbook,
                            EnableBrandLogo = queueState.EnableBrandLogo,
                            BrandLogoPosition = queueState.BrandLogoPosition,
                            SnowfallPreset = queueState.SnowfallPreset,
                            SnowfallMode = queueState.SnowfallMode,
                            SnowfallOpacity = queueState.SnowfallOpacity,
                            SnowOverlay1Opacity = queueState.SnowOverlay1Opacity,
                            SnowOverlay2Opacity = queueState.SnowOverlay2Opacity,
                            SnowOverlay3Opacity = queueState.SnowOverlay3Opacity,

                            OverlayIntroOpacity = queueState.OverlayIntroOpacity,
                            RainOverlayPreset = queueState.RainOverlayPreset,
                            RainOverlayIntensity = queueState.RainOverlayIntensity,
                            MusicSyncMode = queueState.MusicSyncMode
                        };
                    }
                }
            }

            return null;
        }

        private LocalhostQueueJobInfo BuildQueueJobInfo(RenderJob job, ApiRenderJobState? state = null)
        {
            state ??= GetApiJobState(job.Guid);
            string outputVideo = state?.OutputVideo ?? state?.RequestedOutput ?? ResolveRequestedOutputPathForJob(job, state);
            bool done = state?.Done ?? job.StatusDisplay.Contains("DONE", StringComparison.OrdinalIgnoreCase);
            bool success = state?.Success ?? done;

            return new LocalhostQueueJobInfo
            {
                QueueId = job.Id,
                JobId = job.Guid,
                VideoId = job.Guid,
                Status = state?.Status ?? ResolveApiStatusFromJob(job),
                Done = done,
                JobSuccess = success,
                Progress = state?.Progress ?? job.Progress,
                StatusUrl = $"{LocalApiPrefix.TrimEnd('/')}{LocalApiRenderStatusPath}?jobId={job.Guid}",
                InputFile = state?.InputFile ?? job.SourcePath,
                InputFiles = state?.InputFiles ?? (job.MergeVideos ? new List<string>(job.MergeInputPaths) : new List<string> { job.SourcePath }),
                InputCount = (state?.InputFiles?.Count) ?? (job.MergeVideos ? job.MergeInputPaths.Count : 1),
                MergeVideos = state?.MergeVideos ?? job.MergeVideos,
                AudioFile = state?.AudioFile ?? job.AudioPath,
                AudioFile2 = state?.AudioFile2 ?? job.SecondaryAudioPath,
                AudioFiles = state?.AudioFiles ?? BuildAudioFilesForJob(job),
                AudioCount = state?.AudioFiles?.Count ?? BuildAudioFilesForJob(job)?.Count,
                MergeAudio = state?.MergeAudio ?? job.MergeAudio,
                AudioTrimStart = state?.AudioTrimStart ?? job.AudioTrimStart,
                AudioTrimEnd = state?.AudioTrimEnd ?? job.AudioTrimEnd,
                AudioTrimDuration = state?.AudioTrimDuration ?? job.AudioTrimDuration,
                ImageTimelineDurationSeconds = state?.ImageTimelineDurationSeconds ?? job.ImageTimelineDurationSeconds,
                CropZoomPercent = state?.CropZoomPercent ?? job.CropZoomPercent,
                CropAspectRatio = state?.CropAspectRatio ?? job.CropAspectRatio,
                AudioSegments = state?.AudioSegments ?? (job.AudioSegments == null ? null : job.AudioSegments
                    .Select(seg => new AudioEditSegment
                    {
                        StartSeconds = seg.StartSeconds,
                        EndSeconds = seg.EndSeconds
                    })
                    .ToList()),
                OutputVideo = outputVideo,
                TemplateName = state?.TemplateName ?? job.TemplateName,
                OverlayEffect = state?.OverlayEffect ?? job.OverlayEffect,
                EnableSnowOverlay1 = (state?.EnableSnowOverlay1 ?? false) || job.EnableSnowOverlay1,
                EnableSnowOverlay2 = (state?.EnableSnowOverlay2 ?? false) || job.EnableSnowOverlay2,
                EnableSnowOverlay3 = (state?.EnableSnowOverlay3 ?? false) || job.EnableSnowOverlay3,
                EnableCrossTransitions = (state?.EnableCrossTransitions ?? false) || job.EnableCrossTransitions,
                CrossTransitionType = (!string.IsNullOrWhiteSpace(state?.CrossTransitionType) ? state?.CrossTransitionType : null) ?? job.CrossTransitionType,
                CrossTransitionDuration = (state?.CrossTransitionDuration > 0.01 ? state?.CrossTransitionDuration : null) ?? job.CrossTransitionDuration,
                EnablePolaroidScrapbook = state?.EnablePolaroidScrapbook ?? job.EnablePolaroidScrapbook,
                EnableRgbPolaroidScrapbook = state?.EnableRgbPolaroidScrapbook ?? job.EnableRgbPolaroidScrapbook,
                EnableDashedPolaroidScrapbook = state?.EnableDashedPolaroidScrapbook ?? job.EnableDashedPolaroidScrapbook,
                EnableBrandLogo = state?.EnableBrandLogo ?? job.EnableBrandLogo,
                BrandLogoPosition = state?.BrandLogoPosition ?? job.BrandLogoPosition,
                SnowfallPreset = state?.SnowfallPreset ?? job.SnowfallPreset,
                SnowfallMode = state?.SnowfallMode ?? job.SnowfallMode,
                SnowfallOpacity = (state?.SnowfallOpacity > 0.0 ? state?.SnowfallOpacity : null) ?? job.SnowfallOpacity,
                SnowOverlay1Opacity = (state?.SnowOverlay1Opacity > 0.0 ? state?.SnowOverlay1Opacity : null) ?? job.SnowOverlay1Opacity,
                SnowOverlay2Opacity = (state?.SnowOverlay2Opacity > 0.0 ? state?.SnowOverlay2Opacity : null) ?? job.SnowOverlay2Opacity,
                SnowOverlay3Opacity = (state?.SnowOverlay3Opacity > 0.0 ? state?.SnowOverlay3Opacity : null) ?? job.SnowOverlay3Opacity,
                RainOverlayPreset = state?.RainOverlayPreset ?? job.RainOverlayPreset,
                RainOverlayIntensity = state?.RainOverlayIntensity ?? job.RainOverlayIntensity,
                MusicSyncMode = state?.MusicSyncMode ?? job.MusicSyncMode,
                SourceFileName = Path.GetFileName(job.SourcePath),
                OutputFileName = Path.GetFileName(outputVideo),
                SourceDuration = job.DurationSec,
                SourceWidth = job.SourceWidth > 0 ? job.SourceWidth : null,
                SourceHeight = job.SourceHeight > 0 ? job.SourceHeight : null,
                SourceBitrate = job.SourceBitrate > 0 ? job.SourceBitrate : null,
                SourceFramerate = job.SourceFps > 0 ? job.SourceFps : null,
                OutputWidth = GetEffectiveOutputWidth(job),
                OutputHeight = GetEffectiveOutputHeight(job),
                OutputFramerate = job.Framerate,
                EnableFade = job.EnableFade,
                FadeInSeconds = job.FadeInSeconds,
                FadeOutSeconds = job.FadeOutSeconds
            };
        }

        private static string ResolveApiStatusFromJob(RenderJob job)
        {
            string status = job.StatusDisplay ?? string.Empty;
            if (status.Contains("DONE", StringComparison.OrdinalIgnoreCase))
                return "completed";
            if (status.Contains("FAILED", StringComparison.OrdinalIgnoreCase))
                return "failed";
            if (status.Contains("PROCESS", StringComparison.OrdinalIgnoreCase) || status.EndsWith("%", StringComparison.OrdinalIgnoreCase))
                return "processing";
            return "queued";
        }

        private string ResolveDefaultRenderBaseDir()
        {
            if (!string.IsNullOrWhiteSpace(_customOutputFolder) && Directory.Exists(_customOutputFolder))
                return _customOutputFolder;

            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private string ResolveRequestedOutputPathForJob(RenderJob job, ApiRenderJobState? state = null)
        {
            string? apiOutput = state?.OutputVideo ?? state?.RequestedOutput;
            if (!string.IsNullOrWhiteSpace(apiOutput))
                return Path.GetFullPath(apiOutput);

            string outputName = string.IsNullOrWhiteSpace(job.OutputName)
                ? $"Titan_{Path.GetFileNameWithoutExtension(job.SourcePath)}"
                : job.OutputName;

            if (!outputName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                outputName += ".mp4";

            return Path.Combine(ResolveDefaultRenderBaseDir(), "Titan_Output", outputName);
        }

        private string ResolveRenderBaseDirForJob(RenderJob job, ApiRenderJobState? state = null)
        {
            string? apiOutput = state?.OutputVideo ?? state?.RequestedOutput;
            if (job.IsApiJob && !string.IsNullOrWhiteSpace(apiOutput))
                return Path.GetDirectoryName(Path.GetFullPath(apiOutput)) ?? ResolveDefaultRenderBaseDir();

            return ResolveDefaultRenderBaseDir();
        }

        private async Task RunApiQueueAsync(List<RenderJob> jobs, CancellationToken token)
        {
            int total = jobs.Count;
            int completed = 0;
            LogSystem($">>> API QUEUE STARTED ({total} job(s)) <<<");

            foreach (RenderJob queueJob in jobs)
            {
                if (token.IsCancellationRequested)
                    break;

                ApiRenderJobState? state = GetApiJobState(queueJob.Guid);
                string requestedOutput = ResolveRequestedOutputPathForJob(queueJob, state);
                string renderBaseDir = ResolveRenderBaseDirForJob(queueJob, state);

                try
                {
                    LogSystem($"[API-QUEUE] START #{queueJob.Id}: {queueJob.Name}");
                    var result = await RunApiRenderCoreAsync(queueJob, renderBaseDir, requestedOutput, token);

                    SetApiJobState(new ApiRenderJobState
                    {
                        QueueId = queueJob.Id,
                        JobId = queueJob.Guid,
                        Status = "completed",
                        Done = true,
                        Success = true,
                        Progress = 100,
                        Message = "Render completed",
                        RequestedOutput = requestedOutput,
                        InputFile = queueJob.SourcePath,
                        OutputVideo = requestedOutput,
                        EngineOutput = result.EngineOutput,
                        FinalOutput = result.FinalOutput,
                        TemplateName = queueJob.TemplateName,
                        EnablePolaroidScrapbook = queueJob.EnablePolaroidScrapbook,
                        EnableRgbPolaroidScrapbook = queueJob.EnableRgbPolaroidScrapbook,
                        EnableDashedPolaroidScrapbook = queueJob.EnableDashedPolaroidScrapbook,
                        EnableBrandLogo = queueJob.EnableBrandLogo,
                        MusicSyncMode = queueJob.MusicSyncMode,
                        ImageTimelineDurationSeconds = queueJob.ImageTimelineDurationSeconds
                    });

                    completed++;
                    LogSystem($"[API-QUEUE] DONE #{queueJob.Id}: {queueJob.Name}");
                }
                catch (OperationCanceledException)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        queueJob.StatusDisplay = "FAILED (API)";
                        queueJob.StatusColor = Brushes.Red;
                    });

                    SetApiJobState(new ApiRenderJobState
                    {
                        QueueId = queueJob.Id,
                        JobId = queueJob.Guid,
                        Status = "failed",
                        Done = true,
                        Success = false,
                        Progress = queueJob.Progress,
                        Message = "Queue cancelled",
                        RequestedOutput = requestedOutput,
                        InputFile = queueJob.SourcePath,
                        OutputVideo = requestedOutput,
                        EngineOutput = queueJob.ResultPath,
                        TemplateName = queueJob.TemplateName,
                        EnablePolaroidScrapbook = queueJob.EnablePolaroidScrapbook,
                        EnableRgbPolaroidScrapbook = queueJob.EnableRgbPolaroidScrapbook,
                        EnableDashedPolaroidScrapbook = queueJob.EnableDashedPolaroidScrapbook,
                        EnableBrandLogo = queueJob.EnableBrandLogo,
                        MusicSyncMode = queueJob.MusicSyncMode
                    });
                    break;
                }
                catch (Exception ex)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        queueJob.StatusDisplay = "FAILED (API)";
                        queueJob.StatusColor = Brushes.Red;
                    });

                    SetApiJobState(new ApiRenderJobState
                    {
                        QueueId = queueJob.Id,
                        JobId = queueJob.Guid,
                        Status = "failed",
                        Done = true,
                        Success = false,
                        Progress = queueJob.Progress,
                        Message = ex.Message,
                        RequestedOutput = requestedOutput,
                        InputFile = queueJob.SourcePath,
                        OutputVideo = requestedOutput,
                        EngineOutput = queueJob.ResultPath,
                        TemplateName = queueJob.TemplateName,
                        EnablePolaroidScrapbook = queueJob.EnablePolaroidScrapbook,
                        EnableRgbPolaroidScrapbook = queueJob.EnableRgbPolaroidScrapbook,
                        EnableDashedPolaroidScrapbook = queueJob.EnableDashedPolaroidScrapbook,
                        EnableBrandLogo = queueJob.EnableBrandLogo,
                        MusicSyncMode = queueJob.MusicSyncMode
                    });

                    LogSystem($"[API-QUEUE-ERROR] #{queueJob.Id}: {ex.Message}");
                }
            }

            LogSystem($">>> API QUEUE FINISHED {completed}/{total} <<<");
            try { SystemSounds.Asterisk.Play(); } catch { }
        }

        private async Task<(string EngineOutput, string FinalOutput)> RunApiRenderCoreAsync(RenderJob job, string outputDir, string outputPath, CancellationToken token)
        {
            SetApiJobState(new ApiRenderJobState
            {
                QueueId = job.Id,
                JobId = job.Guid,
                Status = "processing",
                Done = false,
                Success = false,
                Progress = 0,
                Message = "Processing",
                RequestedOutput = outputPath,
                InputFile = job.SourcePath,
                InputFiles = BuildInputFilesForJob(job),
                MergeVideos = job.MergeVideos,
                AudioFile = job.AudioPath,
                AudioFile2 = job.SecondaryAudioPath,
                AudioFiles = BuildAudioFilesForJob(job),
                MergeAudio = job.MergeAudio,
                AudioTrimStart = job.AudioTrimStart,
                AudioTrimEnd = job.AudioTrimEnd,
                AudioTrimDuration = job.AudioTrimDuration,
                ImageTimelineDurationSeconds = job.ImageTimelineDurationSeconds,
                AudioSegments = job.AudioSegments == null ? null : job.AudioSegments
                    .Select(seg => new AudioEditSegment
                    {
                        StartSeconds = seg.StartSeconds,
                        EndSeconds = seg.EndSeconds
                    })
                    .ToList(),
                OutputVideo = outputPath,
                TemplateName = job.TemplateName,
                EnablePolaroidScrapbook = job.EnablePolaroidScrapbook,
                EnableRgbPolaroidScrapbook = job.EnableRgbPolaroidScrapbook,
                EnableDashedPolaroidScrapbook = job.EnableDashedPolaroidScrapbook,
                EnableBrandLogo = job.EnableBrandLogo,
                MusicSyncMode = job.MusicSyncMode
            });

            await Dispatcher.InvokeAsync(() =>
            {
                job.StatusDisplay = "PROCESSING...";
                job.StatusColor = Brushes.Yellow;
            });

            // [NEW] Record start time
            DateTime startTime = DateTime.UtcNow;

            await EngineCore.ExecuteRenderAsync(
                job,
                outputDir,
                token,
                p =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        job.Progress = p;
                        job.StatusDisplay = $"{p:F1}%";
                        job.StatusColor = Brushes.Yellow;
                        pbGlobalProgress.Value = p;
                    });

                    SetApiJobState(new ApiRenderJobState
                    {
                        QueueId = job.Id,
                        JobId = job.Guid,
                        Status = "processing",
                        Done = false,
                        Success = false,
                        Progress = p,
                        Message = "Processing",
                        RequestedOutput = outputPath,
                        InputFile = job.SourcePath,
                        InputFiles = BuildInputFilesForJob(job),
                        MergeVideos = job.MergeVideos,
                        AudioFile = job.AudioPath,
                        AudioFile2 = job.SecondaryAudioPath,
                        AudioFiles = BuildAudioFilesForJob(job),
                        MergeAudio = job.MergeAudio,
                        AudioTrimStart = job.AudioTrimStart,
                        AudioTrimEnd = job.AudioTrimEnd,
                        AudioTrimDuration = job.AudioTrimDuration,
                        AudioSegments = job.AudioSegments == null ? null : job.AudioSegments
                            .Select(seg => new AudioEditSegment
                            {
                                StartSeconds = seg.StartSeconds,
                                EndSeconds = seg.EndSeconds
                            })
                            .ToList(),
                        OutputVideo = outputPath,
                        EngineOutput = job.ResultPath,
                        TemplateName = job.TemplateName,
                        EnablePolaroidScrapbook = job.EnablePolaroidScrapbook,
                        EnableRgbPolaroidScrapbook = job.EnableRgbPolaroidScrapbook,
                        EnableDashedPolaroidScrapbook = job.EnableDashedPolaroidScrapbook,
                        EnableBrandLogo = job.EnableBrandLogo,
                        MusicSyncMode = job.MusicSyncMode,
                        WebhookUrl = job.WebhookUrl
                    });
                },
                msg => LogSystem($"[API-RENDER] {msg}")
            );

            string engineOutput = job.ResultPath ?? string.Empty;
            string finalOutput = engineOutput;

            if (!string.IsNullOrWhiteSpace(engineOutput) &&
                !Path.GetFullPath(engineOutput).Equals(outputPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string? targetDir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrWhiteSpace(targetDir))
                        Directory.CreateDirectory(targetDir);

                    if (File.Exists(outputPath))
                        File.Delete(outputPath);

                    File.Move(engineOutput, outputPath);
                    LogSystem($"[API] Moved output to requested path: {outputPath}");
                }
                catch (Exception moveEx)
                {
                    LogSystem($"[API-WARN] Move output failed, fallback copy+delete: {moveEx.Message}");
                    File.Copy(engineOutput, outputPath, true);
                    try
                    {
                        File.Delete(engineOutput);
                    }
                    catch (Exception deleteEx)
                    {
                        LogSystem($"[API-WARN] Could not delete temp engine output: {deleteEx.Message}");
                    }
                }

                engineOutput = outputPath;
                finalOutput = outputPath;
            }

            // [NEW] Calculate encoding time
            DateTime endTime = DateTime.UtcNow;
            double encodingTime = (endTime - startTime).TotalSeconds;

            // [NEW] Store info for response
            job.StartTime = startTime;
            job.EndTime = endTime;

            await Dispatcher.InvokeAsync(() =>
            {
                job.Progress = 100.0;
                job.StatusDisplay = "DONE (API)";
                job.StatusColor = Brushes.LimeGreen;
            });

            LogSystem($"[API] COMPLETED: {Path.GetFileName(finalOutput)} (took {encodingTime:F2}s)");
            try { SystemSounds.Asterisk.Play(); } catch { }

            return (engineOutput, finalOutput);
        }

        private string ResolveColorFilterFromRequest(LocalhostRenderRequest request)
        {
            string? color = FirstNonEmpty(
                request.ColorGrading,
                GetFeatureString(request, "colorgrading", "color", "colorFilter")
            );

            if (!string.IsNullOrWhiteSpace(color))
                return NormalizeColorFilterName(color);

            return "None (Original)";
        }

        private static string? FirstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        private static List<string> BuildInputFilesForJob(RenderJob job)
        {
            if (job.MergeVideos && job.MergeInputPaths.Count > 0)
                return new List<string>(job.MergeInputPaths);

            return new List<string> { job.SourcePath };
        }

        private static List<string>? BuildAudioFilesForJob(RenderJob job)
        {
            var files = new List<string>();

            if (job.MergeAudio && job.MergeAudioPaths.Count > 0)
                files.AddRange(job.MergeAudioPaths);
            else if (!string.IsNullOrWhiteSpace(job.AudioPath))
                files.Add(job.AudioPath);

            if (!string.IsNullOrWhiteSpace(job.SecondaryAudioPath))
                files.Add(job.SecondaryAudioPath);

            return files.Count > 0 ? files : null;
        }

        private static int? GetEffectiveOutputWidth(RenderJob? job)
        {
            if (job == null) return null;
            if (job.TargetWidth > 0) return job.TargetWidth;
            if (job.SourceWidth > 0) return job.SourceWidth;
            return null;
        }

        private static int? GetEffectiveOutputHeight(RenderJob? job)
        {
            if (job == null) return null;
            if (job.TargetHeight > 0) return job.TargetHeight;
            if (job.SourceHeight > 0) return job.SourceHeight;
            return null;
        }

        private static bool TryGetCaseInsensitive(Dictionary<string, JsonElement>? dict, string key, out JsonElement value)
        {
            if (dict != null)
            {
                foreach (var kv in dict)
                {
                    if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        value = kv.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private string? GetFeatureString(LocalhostRenderRequest request, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (TryGetCaseInsensitive(request.Features, key, out JsonElement fv))
                {
                    string? parsed = ParseJsonString(fv);
                    if (!string.IsNullOrWhiteSpace(parsed))
                        return parsed;
                }

                if (TryGetCaseInsensitive(request.ExtraFields, key, out JsonElement ev))
                {
                    string? parsed = ParseJsonString(ev);
                    if (!string.IsNullOrWhiteSpace(parsed))
                        return parsed;
                }
            }

            return null;
        }

        private List<string>? GetFeatureStringList(LocalhostRenderRequest request, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (TryGetCaseInsensitive(request.Features, key, out JsonElement fv))
                {
                    List<string>? parsed = ParseJsonStringList(fv);
                    if (parsed != null && parsed.Count > 0)
                        return parsed;
                }

                if (TryGetCaseInsensitive(request.ExtraFields, key, out JsonElement ev))
                {
                    List<string>? parsed = ParseJsonStringList(ev);
                    if (parsed != null && parsed.Count > 0)
                        return parsed;
                }
            }

            return null;
        }

        private double? GetFeatureDouble(LocalhostRenderRequest request, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (TryGetCaseInsensitive(request.Features, key, out JsonElement fv))
                {
                    double? parsed = ParseJsonDouble(fv);
                    if (parsed.HasValue) return parsed.Value;
                }

                if (TryGetCaseInsensitive(request.ExtraFields, key, out JsonElement ev))
                {
                    double? parsed = ParseJsonDouble(ev);
                    if (parsed.HasValue) return parsed.Value;
                }
            }

            return null;
        }

        private int? GetFeatureInt(LocalhostRenderRequest request, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (TryGetCaseInsensitive(request.Features, key, out JsonElement fv))
                {
                    int? parsed = ParseJsonInt(fv);
                    if (parsed.HasValue) return parsed.Value;
                }

                if (TryGetCaseInsensitive(request.ExtraFields, key, out JsonElement ev))
                {
                    int? parsed = ParseJsonInt(ev);
                    if (parsed.HasValue) return parsed.Value;
                }
            }

            return null;
        }

        private bool? GetFeatureBool(LocalhostRenderRequest request, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (TryGetCaseInsensitive(request.Features, key, out JsonElement fv))
                {
                    bool? parsed = ParseJsonBool(fv);
                    if (parsed.HasValue) return parsed.Value;
                }

                if (TryGetCaseInsensitive(request.ExtraFields, key, out JsonElement ev))
                {
                    bool? parsed = ParseJsonBool(ev);
                    if (parsed.HasValue) return parsed.Value;
                }
            }

            return null;
        }

        private string? ParseJsonString(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString()?.Trim(),
                JsonValueKind.Number => element.GetRawText().Trim(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        private List<string>? ParseJsonStringList(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    string? value = ParseJsonString(item);
                    if (!string.IsNullOrWhiteSpace(value))
                        list.Add(value);
                }
                return list;
            }

            string? single = ParseJsonString(element);
            if (!string.IsNullOrWhiteSpace(single))
                return new List<string> { single };

            return null;
        }

        private double? ParseJsonDouble(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double n))
                return n;

            if (element.ValueKind == JsonValueKind.String &&
                TryParseFlexibleDouble(element.GetString(), out double s))
                return s;

            return null;
        }

        private static double NormalizeOptionalOpacity(double value)
        {
            if (value < 0.0)
                return -1.0;

            double normalized = value > 1.0 ? value / 100.0 : value;
            return Math.Clamp(normalized, 0.0, 1.0);
        }

        private int? ParseJsonInt(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number)
            {
                if (element.TryGetInt32(out int i32)) return i32;
                if (element.TryGetDouble(out double nd)) return (int)Math.Round(nd);
            }

            if (element.ValueKind == JsonValueKind.String &&
                int.TryParse(element.GetString()?.Trim(), out int si))
                return si;

            return null;
        }

        private bool? ParseJsonBool(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.True) return true;
            if (element.ValueKind == JsonValueKind.False) return false;
            if (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString()?.Trim(), out bool sb)) return sb;
            return null;
        }

        private static bool? ParseApiBoolQuery(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            string v = input.Trim();
            if (bool.TryParse(v, out bool b))
                return b;

            if (v.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("on", StringComparison.OrdinalIgnoreCase))
                return true;

            if (v.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("n", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("off", StringComparison.OrdinalIgnoreCase))
                return false;

            return null;
        }

        private static bool TryExtractResolution(string input, out int width, out int height)
        {
            width = -1;
            height = -1;

            if (string.IsNullOrWhiteSpace(input))
                return false;

            Match match = Regex.Match(input, @"(\d+)\s*x\s*(\d+)", RegexOptions.IgnoreCase);
            if (!match.Success)
                return false;

            if (!int.TryParse(match.Groups[1].Value, out width) || !int.TryParse(match.Groups[2].Value, out height))
                return false;

            return width > 0 && height > 0;
        }

        private static string NormalizeHardwareProfileName(string input)
        {
            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (v.Contains("nvidia")) return "NVIDIA NVENC";
            if (v.Contains("amd")) return "AMD AMF";
            if (v.Contains("cpu")) return "Cpu";
            return "NVIDIA NVENC";
        }

        private static string NormalizeBitrateStrategy(string input)
        {
            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (v.Contains("boost")) return "Boost Quality";
            if (v.Contains("custom")) return "Custom (Kbps)";
            return "Match Source";
        }

        private static string NormalizeResolutionName(string input)
        {
            string raw = (input ?? string.Empty).Trim();
            string v = raw.ToLowerInvariant();

            if (v.Contains("1920x1080")) return "1920x1080 (HD)";
            if (v.Contains("1080x1920")) return "1080x1920 (Portrait)";
            if (v.Contains("720x1280")) return "720x1280 (Portrait HD)";
            if (v.Contains("1280x720")) return "1280x720";
            if (v.Contains("custom")) return "Custom (Nhap so)";
            if (v.Contains("original") || string.IsNullOrWhiteSpace(v)) return "Original";

            return raw;
        }

        private static string ResolveFramerateName(string input)
        {
            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (v == "60" || v.Contains("60")) return "60";
            if (v == "30" || v.Contains("30")) return "30";
            if (v == "24" || v.Contains("24")) return "24";
            return "Original";
        }

        private static string NormalizeUpscaleModeName(string input)
        {
            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (v.Contains("2x")) return "2x Upscale (FSR + DLSS)";
            if (v.Contains("4x")) return "4x Upscale (Ultra Quality)";
            return "Off (Original Size)";
        }

        private static string NormalizeFxEffectName(string? input)
        {
            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(v) || v == "none")
                return "None";
            if (v.Contains("crop")) return "Crop Video (Custom Frame)";
            if (v.Contains("flip")) return "Flip Horizontal (Anti-Copyright)";
            if (v.Contains("light leak burn") || v.Contains("lightleakburn") || v.Contains("film burn") || v.Contains("burn transition") || v.Contains("light burn"))
                return "Light Leak Burn";
            if (v.Contains("film leak") || v.Contains("light leak") || v.Contains("leak 1") || v.Contains("rÃƒÂ² phim") || v.Contains("ro phim") || v.Contains("leak")) return "RÃƒÂ² phim (Leak 1 / Light Leak)";
            if (v.Contains("cinematic glow") || v.Contains("glow cinematic") || v == "glow") return "Cinematic Glow";
            if (v.Contains("halo glow") || v == "halo") return "Halo Glow";
            if (v.Contains("prism light") || v == "prism") return "Prism Light";
            if (v.Contains("editorial bloom") || v.Contains("bloom")) return "Editorial Bloom";
            if (v.Contains("dust & scratches") || v.Contains("dust and scratches") || v.Contains("dust scratches") ||
                v.Contains("old film scratches") || v.Contains("scratch video") || v == "scratch" ||
                v.Contains("film scratch") || v.Contains("video scratch")) return "Dust & Scratches";
            return "None";
        }

        private static string ResolveFxEffectName(string? input)
        {
            if (EffectRecipeLibrary.IsRetiredRecipeName(input))
                return "None";

            if (EffectRecipeLibrary.TryResolveCanonicalEffectName(input, out string canonicalRecipeName))
                return canonicalRecipeName;

            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(v) || v == "none")
                return "None";

            if (v.Contains("crop"))
                return "Crop Video (Custom Frame)";
            if (v.Contains("flip"))
                return "Flip Horizontal (Anti-Copyright)";
            if (v.Contains("snowfall") || v == "snow" || v.Contains("snow overlay") || v.Contains("snow soft") ||
                v.Contains("snow bokeh") || v.Contains("snow heavy") || v.Contains("snow windy") || v.Contains("snow cinematic"))
                return "Snowfall Overlay";
            if (v.Contains("film leak") || v.Contains("light leak") || v.Contains("leak 1") || v.Contains("rÃƒÂ² phim") || v.Contains("ro phim") || v.Contains("leak"))
                return "RÃƒÂ² phim (Leak 1 / Light Leak)";
            if (v.Contains("cinematic glow") || v.Contains("glow cinematic") || v == "glow")
                return "Cinematic Glow";
            if (v.Contains("halo glow") || v == "halo" || v.Contains("halo"))
                return "Halo Glow";
            if (v.Contains("prism light") || v == "prism" || v.Contains("prism"))
                return "Prism Light";
            if (v.Contains("editorial bloom") || v.Contains("bloom"))
                return "Editorial Bloom";
            if (v.Contains("dust & scratches") || v.Contains("dust and scratches") || v.Contains("dust scratches") ||
                v.Contains("old film scratches") || v.Contains("scratch video") || v == "scratch" ||
                v.Contains("film scratch") || v.Contains("video scratch"))
                return "Dust & Scratches";

            return "None";
        }

        private static string ResolveOverlayEffectName(string? input)
        {
            if (EffectRecipeLibrary.IsRetiredRecipeName(input))
                return "None";

            if (EffectRecipeLibrary.TryResolveCanonicalEffectName(input, out string canonicalRecipeName))
                return canonicalRecipeName;

            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(v) || v == "none")
                return "None";

            if (v.Contains("light leak overlay") || v.Contains("lightleakoverlay") || v.Contains("light leak pack") ||
                v.Contains("film burn overlay") || v.Contains("camera leak overlay") || v.Contains("overlay leak") ||
                v == "light leak" || v == "film leak")
                return "Light Leak Overlay";
            if (v.Contains("dreamydotoverlay2") || v.Contains("dreamy dot overlay 2") || v.Contains("dreamy dot 2") ||
                v.Contains("bokeh light overlay 2") || v.Contains("dreamy bokeh 2"))
                return "Dreamy Dot Overlay 2";
            if (v.Contains("dreamy dot chaos") || v.Contains("chaos dot") || v.Contains("random dot") ||
                v.Contains("dreamy chaos") || v.Contains("wandering dots"))
                return "Dreamy Dot Chaos Overlay";
            if (v.Contains("dreamy dot") || v.Contains("soft dot") || v.Contains("dot overlay") ||
                v.Contains("bokeh dot") || v.Contains("bokeh drift") || v.Contains("particle drift") ||
                v.Contains("floating dots") || v.Contains("blur dot"))
                return "Dreamy Dot Chaos Overlay";
            if (v.Contains("snowfall") || v == "snow" || v.Contains("snow overlay") || v.Contains("snow soft") ||
                v.Contains("snow bokeh") || v.Contains("snow heavy") || v.Contains("snow windy") || v.Contains("snow cinematic"))
                return "Snowfall Overlay";
            if (v.Contains("rain overlay") || v == "rain" || v.Contains("heavy rain") ||
                v.Contains("cinematic rain") || v.Contains("window drops") || v.Contains("heavyrainblackbg") ||
                v.Contains("cinematicrainblackbg") || v.Contains("windowdrops"))
                return "Rain Overlay";
            if (v.Contains("polaroid scrapbook 2") || v.Contains("polaroidscrapbook2") || v.Contains("rounded polaroid scrapbook") || v.Contains("rounded polaroid"))
                return "Polaroid Scrapbook 2";
            if (v.Contains("rgb polaroid") || v.Contains("polaroid rgb") || v.Contains("rgb border") || v.Contains("rainbow polaroid"))
                return "RGB Polaroid Border";
            if (v.Contains("dashed polaroid") || v.Contains("polaroid dashed") || v.Contains("dashed frame"))
                return "Dashed Polaroid Frame";
            if (v.Contains("dust & scratches") || v.Contains("dust and scratches") || v.Contains("dust scratches") ||
                v.Contains("old film scratches") || v.Contains("scratch video") || v == "scratch" ||
                v.Contains("film scratch") || v.Contains("video scratch"))
                return "Dust & Scratches";
            if (v.Contains("polaroid") || v.Contains("scrapbook") || v.Contains("photo album"))
                return "Polaroid Scrapbook";

            return "None";
        }

        private static string ResolveSnowfallPresetName(string? input)
        {
            return SnowfallOverlayPreset.NormalizePreset((input ?? string.Empty).Trim());
        }

        private static string ResolveRainOverlayPresetName(string? input)
        {
            return RainOverlayPreset.NormalizePreset((input ?? string.Empty).Trim());
        }

        private static string ResolveRainOverlayIntensityName(string? input)
        {
            return RainOverlayPreset.NormalizeIntensity((input ?? string.Empty).Trim());
        }

        private static string NormalizeTemplateName(string? input)
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

        private static string NormalizeMusicSyncModeName(string? input)
        {
            string v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (v == "beat" || v == "beatsync" || v == "beat-sync" || v == "sync")
                return "beat";
            return "off";
        }

        private static string NormalizeColorFilterName(string? input)
        {
            string value = (input ?? string.Empty).Trim().ToLowerInvariant();

            return value switch
            {
                "spring" => "Spring",
                "autumn" => "Autumn",
                "winter" => "Winter",
                "cold" => "Cold",
                "warm" => "Warm",
                "cinematic" => "Cinematic",
                "vintage" => "Vintage",
                "pastel" => "Pastel",
                "cyberpunk" => "Cyberpunk",
                "b&w pro" => "B&W Pro",
                "bw pro" => "B&W Pro",
                "none" => "None (Original)",
                "original" => "None (Original)",
                "none (original)" => "None (Original)",
                _ => "None (Original)"
            };
        }

        private async Task WriteApiResponseAsync(HttpListenerResponse response, int statusCode, LocalhostRenderResponse payload)
        {
            try
            {
                string jsonResponse = JsonSerializer.Serialize(payload, _localApiJsonOptions);
                byte[] bytes = Encoding.UTF8.GetBytes(jsonResponse);

                LogSystem($"[API-RESPONSE-DEBUG] Status: {statusCode}, Size: {bytes.Length} bytes");

                response.StatusCode = statusCode;
                response.ContentType = "application/json; charset=utf-8";
                response.ContentEncoding = Encoding.UTF8;
                response.KeepAlive = false;
                response.SendChunked = false;
                response.Headers["Access-Control-Allow-Origin"] = "*";
                response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                response.Headers["Access-Control-Allow-Methods"] = "POST, GET, OPTIONS";
                response.Headers["Connection"] = "close";
                response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
                response.Headers["Pragma"] = "no-cache";
                response.ContentLength64 = bytes.LongLength;

                LogSystem($"[API-RESPONSE-DEBUG] Writing {bytes.Length} bytes to stream...");

                await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                await response.OutputStream.FlushAsync();

                LogSystem($"[API-RESPONSE-DEBUG] Stream flushed, closing response...");
                response.Close();

                LogSystem($"[API-RESPONSE-SUCCESS] Response sent and closed");
            }
            catch (Exception ex)
            {
                LogSystem($"[API-RESPONSE-ERROR] {ex.GetType().Name}: {ex.Message}");
                try 
                { 
                    response?.Close(); 
                } 
                catch { }
            }
        }

        private async Task WriteBlockingApiResponseAsync(
            HttpListenerResponse response,
            Func<Task<LocalhostRenderResponse>> payloadFactory,
            CancellationToken token)
        {
            Task<LocalhostRenderResponse>? payloadTask = null;
            bool clientDisconnected = false;

            try
            {
                response.StatusCode = 200;
                response.ContentType = "application/json; charset=utf-8";
                response.ContentEncoding = Encoding.UTF8;
                response.KeepAlive = false;
                response.SendChunked = true;
                response.Headers["Access-Control-Allow-Origin"] = "*";
                response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                response.Headers["Access-Control-Allow-Methods"] = "POST, GET, OPTIONS";
                response.Headers["Connection"] = "close";
                response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
                response.Headers["Pragma"] = "no-cache";

                payloadTask = payloadFactory();
                DateTime nextHeartbeatAt = DateTime.UtcNow.AddSeconds(1);

                while (!payloadTask.IsCompleted)
                {
                    Task delayTask = Task.Delay(250, token);
                    Task completedTask = await Task.WhenAny(payloadTask, delayTask);
                    if (completedTask == payloadTask)
                        break;

                    if (DateTime.UtcNow >= nextHeartbeatAt)
                    {
                        try
                        {
                            await response.OutputStream.WriteAsync(BlockingApiHeartbeatBytes, 0, BlockingApiHeartbeatBytes.Length);
                            await response.OutputStream.FlushAsync();
                        }
                        catch (Exception ex) when (IsClientDisconnectException(ex))
                        {
                            clientDisconnected = true;
                            LogSystem($"[API-RESPONSE-INFO] Blocking client disconnected during heartbeat: {ex.Message}");
                            break;
                        }

                        nextHeartbeatAt = DateTime.UtcNow.AddSeconds(1);
                        LogSystem("[API-RESPONSE-DEBUG] Heartbeat sent for blocking request");
                    }
                }

                LocalhostRenderResponse payload = await payloadTask;
                if (clientDisconnected)
                    return;

                string jsonResponse = JsonSerializer.Serialize(payload, _localApiJsonOptions);
                byte[] bytes = Encoding.UTF8.GetBytes(jsonResponse);

                LogSystem($"[API-RESPONSE-DEBUG] Blocking final payload size: {bytes.Length} bytes");

                try
                {
                    await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    await response.OutputStream.FlushAsync();
                    response.Close();
                }
                catch (Exception ex) when (IsClientDisconnectException(ex))
                {
                    LogSystem($"[API-RESPONSE-INFO] Blocking client disconnected before final payload: {ex.Message}");
                    return;
                }

                LogSystem("[API-RESPONSE-SUCCESS] Blocking response sent and closed");
            }
            catch (Exception ex)
            {
                if (IsClientDisconnectException(ex))
                {
                    LogSystem($"[API-RESPONSE-INFO] Blocking client disconnected: {ex.Message}");
                    if (payloadTask != null)
                        await payloadTask;
                    return;
                }

                LogSystem($"[API-RESPONSE-ERROR] Blocking {ex.GetType().Name}: {ex.Message}");
                try
                {
                    response?.Close();
                }
                catch { }

                throw;
            }
        }

        private static bool IsClientDisconnectException(Exception ex)
        {
            return ex is HttpListenerException ||
                   ex is ObjectDisposedException ||
                   ex is IOException;
        }

        private void LogSystem(string message)
        {
            Dispatcher.BeginInvoke(() =>
            {
                lock (_logLock)
                {
                    string ts = DateTime.Now.ToString("HH:mm:ss");
                    txtLogs.AppendText($"[{ts}] {message}\n");
                    txtLogs.ScrollToEnd();
                }
            });
        }
    }

    #endregion
}
















