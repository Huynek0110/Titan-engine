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


        public static void Initialize()
        {
            if (_isInitialized) return;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            // Giai đoạn 3: tên binary theo OS (Windows = ffmpeg.exe như bản gốc; Linux = ffmpeg).
            _ffmpegPath = System.IO.Path.Combine(baseDir, TitanPlatform.BinaryName("ffmpeg"));
            _ffprobePath = System.IO.Path.Combine(baseDir, TitanPlatform.BinaryName("ffprobe"));

            // Giai đoạn 3 (đề bài): cạnh app → PATH → cấu hình người dùng. Windows giữ hành vi
            // gốc (chỉ cạnh app + hỏi IUserPrompt); Linux tìm thêm trên PATH/env trước khi hỏi.
            if (!TitanPlatform.IsWindows)
            {
                _ffmpegPath = LocateNonWindowsTool(baseDir, "ffmpeg", "TITAN_FFMPEG_PATH") ?? _ffmpegPath;
                _ffprobePath = LocateNonWindowsTool(baseDir, "ffprobe", "TITAN_FFPROBE_PATH") ?? _ffprobePath;
            }

            // Giai đoạn 3: Upscayl/DLSS theo OS (Windows giữ tên cũ; Linux = binary thường + PATH).
            _upscaylPath = LocateUpscayl(baseDir);
            _ngxDlvsrPath = System.IO.Path.Combine(baseDir, "dlss", TitanPlatform.BinaryName("DLVSR"));
            RefreshNvidiaUpscaleBackendPaths();

            if (!File.Exists(_ffmpegPath))
            {
                var msg = $"FFmpeg not found at:\n{baseDir}\n\nManually select ffmpeg.exe?";
                if (EngineServices.Prompt?.Confirm("Missing Core Engine", msg) == true)
                {
                    string? selectedFfmpeg = EngineServices.Prompt.PromptForFfmpegPath();
                    if (selectedFfmpeg != null)
                    {
                        _ffmpegPath = selectedFfmpeg;
                        string? dirName = System.IO.Path.GetDirectoryName(_ffmpegPath);
                        if (dirName != null)
                            _ffprobePath = System.IO.Path.Combine(dirName, TitanPlatform.BinaryName("ffprobe"));
                    }
                    else throw new FileNotFoundException("FFmpeg Core Engine not found.");
                }
                else throw new FileNotFoundException($"Please copy ffmpeg.exe to:\n{baseDir}");
            }
            _isInitialized = true;
        }

        public static string GetNgxDlssExpectedPath()
        {
            if (!string.IsNullOrWhiteSpace(_ngxDlvsrPath))
                return _ngxDlvsrPath;

            return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dlss", TitanPlatform.BinaryName("DLVSR"));
        }

        /// <summary>
        /// Giai đoạn 3 (đề bài): trên Linux tool tìm theo cạnh app → PATH → cấu hình người dùng
        /// (env var). Trả null nếu không tìm thấy đâu (caller giữ đường dẫn cạnh app như cũ để
        /// log lỗi rõ). Windows không dùng hàm này.
        /// </summary>
        private static string? LocateNonWindowsTool(string baseDir, string baseName, string envVarName)
        {
            string besideApp = System.IO.Path.Combine(baseDir, TitanPlatform.BinaryName(baseName));
            if (File.Exists(besideApp) && TitanPlatform.IsExecutable(besideApp))
                return besideApp;

            string envValue = Environment.GetEnvironmentVariable(envVarName) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(envValue) && File.Exists(envValue))
                return envValue;

            string? onPath = TitanPlatform.FindOnPath(baseName);
            if (onPath != null)
                return onPath;

            return null;
        }

        /// <summary>
        /// Giai đoạn 3: đường dẫn Upscayl theo OS. Windows giữ nguyên "upscayl\Upscayl.exe".
        /// Linux: thử binary cạnh app (upscayl-bin/upscayl) rồi PATH; không có → trả đường dẫn
        /// cạnh app để RenderPipeline log đúng chỗ cần đặt như bản gốc.
        /// </summary>
        private static string LocateUpscayl(string baseDir)
        {
            if (TitanPlatform.IsWindows)
                return System.IO.Path.Combine(baseDir, "upscayl", "Upscayl.exe");

            string[] besideAppNames = { "upscayl-bin", "upscayl" };
            foreach (string name in besideAppNames)
            {
                string candidate = System.IO.Path.Combine(baseDir, "upscayl", name);
                if (File.Exists(candidate) && TitanPlatform.IsExecutable(candidate))
                    return candidate;
            }

            string? onPath = TitanPlatform.FindOnPath("upscayl-bin") ?? TitanPlatform.FindOnPath("upscayl");
            if (onPath != null)
                return onPath;

            return System.IO.Path.Combine(baseDir, "upscayl", "upscayl-bin");
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
            // Giai đoạn 3 (đề bài): DLSS NGX = Windows-only. Trên Linux log rõ rồi rơi xuống
            // Upscayl / internal đúng docs/10 §7 — không tải bundle, không chạy winget/powershell.
            if (!TitanPlatform.IsWindows)
            {
                onLog("[DLSS-NGX] DLSS NGX (DLVSR/UpscalePipelineApp + winget/VFX SDK) is Windows-only.");
                onLog("[DLSS-NGX] Skipping auto setup on Linux — falling back to Upscayl/internal upscale.");
                return false;
            }

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
                "-an " +
                (EngineCore.HasNvencSupport() ? "-c:v h264_nvenc -preset medium -cq 18 " : "-c:v libx264 -preset medium -crf 18 ") +
                "-pix_fmt yuv420p " +
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

        // Phase 2: decode/re-encode PNG (TrySanitizeBrandLogoAsset) moved sang WPF qua IImageServices;
        // headless (Images == null) trả false như nhánh catch gốc.
        private static bool TrySanitizeBrandLogoAsset(string sourcePath, string destinationPath, Action<string> onLog)
            => EngineServices.Images?.SanitizeBrandLogoAsset(sourcePath, destinationPath, onLog) ?? false;

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

        private static int EnsureEvenDimension(int value)
        {
            int dimension = Math.Max(2, value);
            if ((dimension & 1) != 0)
                dimension += 1;
            return dimension;
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

        /// <summary>Giai đoạn 3: doctor đọc font drawtext đã resolve (không chạy lại logic).</summary>
        public static string ResolveFontFileForDoctor() => ResolveFfmpegFontFile();

        private static string ResolveFfmpegFontFile()
        {
            // Giai đoạn 3: Linux không có C:\Windows\Fonts — lấy font hệ thống hỗ trợ tiếng Việt.
            if (!TitanPlatform.IsWindows)
            {
                string[] linuxCandidates =
                {
                    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                    "/usr/share/fonts/dejavu/DejaVuSans.ttf",
                    "/usr/share/fonts/TTF/DejaVuSans.ttf",
                    "/usr/share/fonts/noto/NotoSans-Regular.ttf",
                    "/usr/share/fonts/noto-sans/NotoSans-Regular.ttf",
                    "/usr/share/fonts/TTF/NotoSans-Regular.ttf",
                    "/usr/share/fonts/liberation/LiberationSans-Regular.ttf",
                    "/usr/share/fonts/liberation-sans/LiberationSans-Regular.ttf",
                    "/usr/share/fonts/TTF/LiberationSans-Regular.ttf",
                };
                foreach (string candidate in linuxCandidates)
                {
                    if (File.Exists(candidate))
                        return candidate.Replace("\\", "/").Replace(":", "\\:");
                }
                return string.Empty;
            }

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

        /// <summary>
        /// Absolute-path form of <paramref name="path"/>, or the empty string when the input is
        /// blank. Used to compare two temp-artifact paths without throwing on the blank or
        /// malformed strings that occasionally reach the cleanup list.
        /// </summary>
        private static string ResolveFullPathSafe(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }
    }
}
