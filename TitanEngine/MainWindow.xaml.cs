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

    #region --- [MAIN WINDOW] ---

    public partial class MainWindow : Window
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern void DwmSetWindowAttribute(System.IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// Windows 11 chrome for the modern dark UI: a DARK title bar (immersive dark mode)
        /// and DWM-rounded window corners, applied on source init. Purely visual, additive,
        /// and silently skipped on systems without DWM support.
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                // Brand the title bar and taskbar with the bundled logo when it ships
                // next to the exe. Purely cosmetic: a missing file just leaves the
                // default application icon.
                string logoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Content", "Branding", "video-logo.png");
                if (System.IO.File.Exists(logoPath))
                    this.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(logoPath));
            }
            catch
            {
                // Cosmetic only - never block startup over a missing icon.
            }

            try
            {
                System.IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int on = 1;
                DwmSetWindowAttribute(hwnd, 20, ref on, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
                int round = 2;
                DwmSetWindowAttribute(hwnd, 33, ref round, 4); // DWMWA_WINDOW_CORNER_PREFERENCE = ROUND
            }
            catch
            {
                // Older DWM: keep the standard chrome, nothing else depends on it.
            }
        }

        // The soundbar clamp ranges live on EngineCore, which owns the render pipeline and the API
        // job builder. This class needs the same numbers for the UI controls and the preview path, so
        // it mirrors them by reference rather than repeating the literals - a single edit upstream
        // cannot drift away from the UI.
        private const double SoundBarMinOpacity = EngineCore.SoundBarMinOpacity;
        private const double SoundBarMaxOpacity = EngineCore.SoundBarMaxOpacity;
        private const double SoundBarMinSensitivity = EngineCore.SoundBarMinSensitivity;
        private const double SoundBarMaxSensitivity = EngineCore.SoundBarMaxSensitivity;
        private const double SoundBarMinSmoothing = EngineCore.SoundBarMinSmoothing;
        private const double SoundBarMaxSmoothing = EngineCore.SoundBarMaxSmoothing;
        private const int SoundBarMinBarCount = EngineCore.SoundBarMinBarCount;
        private const int SoundBarMaxBarCount = EngineCore.SoundBarMaxBarCount;
        private const double SoundBarMinLoopSeconds = EngineCore.SoundBarMinLoopSeconds;
        private const double SoundBarMaxLoopSeconds = EngineCore.SoundBarMaxLoopSeconds;
        private const int SoundBarMinRingCount = EngineCore.SoundBarMinRingCount;
        private const int SoundBarMaxRingCount = EngineCore.SoundBarMaxRingCount;

        // The preview cache lives on EngineCore next to the generator that writes it; this class owns
        // the picker, which is the only caller that can decide what is worth keeping.
        private static int PruneSoundBarPreviewCache(string? directory, string? keepPath)
            => EngineCore.PruneSoundBarPreviewCache(directory, keepPath);

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

        private readonly string _appVersion = "V193 UI+Soundbar Update";

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
        private const string LocalApiSoundBarPresetsPath = "/api/soundbar/presets";
        private static readonly byte[] BlockingApiHeartbeatBytes = Encoding.UTF8.GetBytes(" \n");

        private readonly JsonSerializerOptions _localApiJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        public MainWindow()
        {
            WpfEngineServices.Install();
            InitializeComponent();
            JobQueue = new ObservableCollection<RenderJob>();
            lstQueue.ItemsSource = JobQueue;
            this.Title = $"Titan Engine {_appVersion}";
            this.TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo();
            this.Loaded += OnSystemLoaded;
            this.Drop += OnWindowDrop;
            this.Closed += OnMainWindowClosed;
            this.AllowDrop = true;

            // Debounce for "the user stopped typing in the soundbar width/height boxes". Created here
            // rather than lazily so it can never be null in the TextChanged path.
            _soundBarSizeEditCommitTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
            _soundBarSizeEditCommitTimer.Tick += SoundBarSizeEditCommitTimer_Tick;

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

            PopulateSoundBarComboBoxes();
            UpdateSoundBarDescription();
            UpdateSoundBarUiState();

            UpdateHostModeButtonState();
        }


        private static bool TryExtractPlaybackSpeed(string? text, out double speed)
        {
            speed = 1.0;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            var match = Regex.Match(text, @"([0-9]+(?:[.,][0-9]+)?)\s*x", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return LocalhostRequestMapper.TryParseFlexibleDouble(match.Groups[1].Value, out speed) && speed > 0.0;
            }

            return LocalhostRequestMapper.TryParseFlexibleDouble(text, out speed) && speed > 0.0;
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

        /// <summary>
        /// Every audio container FFmpeg can demux, accepted as an external audio source.
        /// <para>
        /// This list used to exist as three separate literals that drifted apart: the single-select
        /// browse dialog listed mp3/wav/aac, drag-and-drop accepted only those three, and the
        /// secondary-audio dialog listed a different set again including flac. A FLAC file was
        /// therefore reachable through one dialog and rejected by another. FFmpeg decodes all of
        /// these natively, so the only thing gating them was this filter.
        /// </para>
        /// </summary>
        private static readonly string[] AudioSourceExtensions =
        {
            ".mp3", ".wav", ".aac", ".m4a", ".m4b", ".flac", ".ogg", ".oga", ".opus",
            ".wma", ".aiff", ".aif", ".aifc", ".alac", ".ape", ".wv", ".mka", ".m4r",
            ".ac3", ".eac3", ".dts", ".amr", ".aaw", ".tta", ".tak", ".mpc", ".ra",
            ".rm", ".gsm", ".voc", ".spx", ".caf", ".au", ".snd", ".mid", ".midi"
        };

        /// <summary>
        /// Pattern-list form of <see cref="AudioSourceExtensions"/> for an OpenFileDialog filter.
        /// Built from the array so the two can never disagree again.
        /// </summary>
        private static string AudioSourceFilterPattern => "*" + string.Join(";*", AudioSourceExtensions);

        private static bool IsAudioSourcePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return AudioSourceExtensions.Contains(ext, StringComparer.Ordinal);
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
                            LocalhostRequestMapper.TryParseFlexibleDouble(txtSlowMotionCustom.Text, out double customSpeed) &&
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

            if (txtFadeInSeconds != null && LocalhostRequestMapper.TryParseFlexibleDouble(txtFadeInSeconds.Text, out double parsedIn))
                fadeIn = parsedIn;

            if (txtFadeOutSeconds != null && LocalhostRequestMapper.TryParseFlexibleDouble(txtFadeOutSeconds.Text, out double parsedOut))
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
                ProbeSoundBarSourceResolutionAsync();
            }
        }

        private void BtnBrowseAudio_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = $"Audio|{AudioSourceFilterPattern}|All|*.*", Multiselect = true };
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
                Filter = $"Audio|{AudioSourceFilterPattern}|All|*.*",
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
                if (IsVideoSourcePath(f) || IsImageSourcePath(f)) vids.Add(f);
                else if (IsAudioSourcePath(f)) auds.Add(f);
            }

            if (vids.Count > 0)
            {
                _listVideoPaths = vids;
                txtVideoPath.Text = vids.Count > 1 ? $"<{vids.Count} files>" : System.IO.Path.GetFileName(vids[0]);
                int imageCount = vids.Count(IsImageSourcePath);
                int videoCount = vids.Count - imageCount;
                LogSystem($"[INPUT] {vids.Count} source file(s) ({videoCount} video, {imageCount} image)");
                ProbeSoundBarSourceResolutionAsync();
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

            // The output canvas just changed, so any position preset the user already picked is now
            // computed against the wrong aspect. Re-derive it - see RefreshSoundBarCanvasAspect.
            if (IsLoaded)
                RefreshSoundBarCanvasAspect();
        }

        /// <summary>
        /// The custom width / height boxes are the resolution control too, so editing them has to
        /// refresh the canvas aspect on exactly the same footing as picking from the combo.
        /// </summary>
        private void TxtOutputResolution_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Half-typed values are normal here; TryGetSoundBarOutputSize simply declines them, which
            // leaves the previous aspect in place rather than snapping to a garbage one mid-edit.
            if (IsLoaded && gridCustomRes?.Visibility == Visibility.Visible)
                RefreshSoundBarCanvasAspect();
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
            string fxEffect = LocalhostRequestMapper.ResolveFxEffectName(GetSelectedComboText(cmbFxType));
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
                    if (txtSlowMotionCustom == null || !LocalhostRequestMapper.TryParseFlexibleDouble(txtSlowMotionCustom.Text, out _slowMotionSpeed) || _slowMotionSpeed <= 0.0)
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
            if (txtSlowMotionCustom != null && LocalhostRequestMapper.TryParseFlexibleDouble(txtSlowMotionCustom.Text, out double speed))
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

        // ══════════════════════════════════════════════════════════════════════
        // SOUNDBAR (Audio Visualizer) - UI layer
        // ══════════════════════════════════════════════════════════════════════

        private CancellationTokenSource? _soundBarPreviewCts;

        /// <summary>
        /// Short picker labels for the style combo, keyed by canonical style id.
        /// <para>
        /// The id list itself is NOT duplicated here - it comes from
        /// <see cref="SoundBarVisualizerPreset.AllStyles"/>, so a style added to or removed
        /// from the canonical table can never go missing from the picker. That mattered: a
        /// hard-coded id list that drifted would make <c>FindStyle</c> return null and silently
        /// render neon-bars instead of what the user picked, with nothing in the log.
        /// </para>
        /// <para>
        /// These are menu labels only. The long copy lives in the description strip underneath,
        /// which is fed straight from the same canonical table. Any style without an override
        /// falls back to its canonical <c>Name</c>, so a brand new style still shows something
        /// sensible without a code change here.
        /// </para>
        /// </summary>
        private static readonly Dictionary<string, string> SoundBarStyleDisplayLabels =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["neon-bars"] = "Neon Bars (đáy)",
                ["mirror-spectrum"] = "Center Mirror Spectrum",
                ["pill-bars"] = "Pill Bar Row",
                ["dense-columns"] = "Dense Thin Columns",
                ["dot-matrix"] = "Dot Matrix Grid",
                ["radial-ring"] = "Radial Spectrum Ring",
                ["dual-rings"] = "Dual Counter Rings",
                ["ripple-rings"] = "Ripple Pulse Rings",
                ["spoke-lines"] = "Radial Spoke Lines",
                ["wave-ribbon"] = "Waveform Ribbon",
                ["center-column"] = "Center Sound Column",
                ["peak-stars"] = "Peak Star Field",
                ["audio-ring"] = "Audio Ring (NCS)"
            };

        private void PopulateSoundBarComboBoxes()
        {
            if (cmbSoundBarStyle != null && cmbSoundBarStyle.Items.Count == 0)
            {
                try
                {
                    int added = 0;
                    foreach (SoundBarStyleInfo style in SoundBarVisualizerPreset.AllStyles)
                    {
                        if (string.IsNullOrWhiteSpace(style.StyleId))
                            continue;

                        string label = SoundBarStyleDisplayLabels.TryGetValue(style.StyleId, out string? customLabel)
                            ? customLabel
                            : style.Name;

                        cmbSoundBarStyle.Items.Add(new ComboBoxItem { Content = label, Tag = style.StyleId });
                        added++;
                    }

                    if (added == 0)
                        throw new InvalidOperationException("SoundBarVisualizerPreset.AllStyles returned no usable styles.");

                    LogSystem($"[SOUNDBAR] Loaded {added} style(s) from SoundBarVisualizerPreset");
                }
                catch (Exception ex)
                {
                    // Never let a style-table load failure break startup.
                    LogSystem($"[SOUNDBAR-WARN] Style list unavailable ({ex.Message}); falling back to neon-bars.");
                    cmbSoundBarStyle.Items.Clear();
                    cmbSoundBarStyle.Items.Add(new ComboBoxItem { Content = "Neon Bars (đáy)", Tag = "neon-bars" });
                }

                cmbSoundBarStyle.SelectedIndex = 0;
            }

            if (cmbSoundBarPalette != null && cmbSoundBarPalette.Items.Count == 0)
            {
                try
                {
                    int added = 0;
                    foreach (ColorPreset preset in ColorPresets.All)
                    {
                        if (string.IsNullOrWhiteSpace(preset.Id))
                            continue;

                        cmbSoundBarPalette.Items.Add(new ComboBoxItem { Content = preset.Name, Tag = preset.Id });
                        added++;
                    }

                    if (added == 0)
                        throw new InvalidOperationException("ColorPresets.All returned no usable palettes.");

                    LogSystem($"[SOUNDBAR] Loaded {added} palette(s) from ColorPresets");
                }
                catch (Exception ex)
                {
                    // Never let a palette-table load failure break startup.
                    LogSystem($"[SOUNDBAR-WARN] Palette list unavailable ({ex.Message}); falling back to ncs-cyan.");
                    cmbSoundBarPalette.Items.Clear();
                    cmbSoundBarPalette.Items.Add(new ComboBoxItem { Content = "NCS Cyan", Tag = "ncs-cyan" });
                }

                cmbSoundBarPalette.SelectedIndex = 0;
            }

            if (cmbSoundBarAudioSource != null && cmbSoundBarAudioSource.Items.Count == 0)
            {
                cmbSoundBarAudioSource.Items.Add(new ComboBoxItem { Content = "Tự động (ưu tiên audio ngoài)", Tag = "auto" });
                cmbSoundBarAudioSource.Items.Add(new ComboBoxItem { Content = "Chỉ audio ngoài", Tag = "external" });
                cmbSoundBarAudioSource.Items.Add(new ComboBoxItem { Content = "Audio 1 - nghe riêng track 1", Tag = "audio1" });
                cmbSoundBarAudioSource.Items.Add(new ComboBoxItem { Content = "Audio 2 - nghe riêng track 2 (vocal)", Tag = "audio2" });
                cmbSoundBarAudioSource.Items.Add(new ComboBoxItem { Content = "Chỉ audio trong video", Tag = "video" });
                cmbSoundBarAudioSource.SelectedIndex = 0;
            }

            // SoundBarSizeMax is derived from the authored footprint and the preset feasibility
            // limit, so it cannot be written as a XAML literal without becoming a second source of
            // truth. The XAML value is the same number for readers; this pins the exact value.
            if (sldSoundBarSize != null && sldSoundBarSize.Maximum != SoundBarSizeMax)
                sldSoundBarSize.Maximum = SoundBarSizeMax;

            if (cmbSoundBarSizePreset != null && cmbSoundBarSizePreset.Items.Count == 0)
            {
                foreach ((string label, string tag) in SoundBarSizePresetEntries)
                    cmbSoundBarSizePreset.Items.Add(new ComboBoxItem { Content = label, Tag = tag });

                // Resting entry for any scale that is not one of the presets. SyncSoundBarSizePresetCombo
                // falls back to it, so the combo tracks the slider instead of going stale forever.
                cmbSoundBarSizePreset.Items.Add(new ComboBoxItem { Content = "Tuỳ chỉnh", Tag = SoundBarSizeCustomTag });

                // "Vừa (100%)" by NAME, not by index - the index was a second thing to keep in sync with
                // the list above, and the XAML slider's start value (100) is the footprint itself.
                cmbSoundBarSizePreset.SelectedIndex = FindSoundBarSizePresetIndex(100.0);
            }

            if (cmbSoundBarPosition != null && cmbSoundBarPosition.Items.Count == 0)
            {
                cmbSoundBarPosition.Items.Add(new ComboBoxItem { Content = "Dưới - Giữa", Tag = "bottom-center" });
                cmbSoundBarPosition.Items.Add(new ComboBoxItem { Content = "Dưới - Trái", Tag = "bottom-left" });
                cmbSoundBarPosition.Items.Add(new ComboBoxItem { Content = "Dưới - Phải", Tag = "bottom-right" });
                cmbSoundBarPosition.Items.Add(new ComboBoxItem { Content = "Giữa khung", Tag = "center" });
                cmbSoundBarPosition.Items.Add(new ComboBoxItem { Content = "Trên - Giữa", Tag = "top-center" });
                cmbSoundBarPosition.Items.Add(new ComboBoxItem { Content = "Trên - Trái", Tag = "top-left" });
                cmbSoundBarPosition.Items.Add(new ComboBoxItem { Content = "Trên - Phải", Tag = "top-right" });
                cmbSoundBarPosition.Items.Add(new ComboBoxItem { Content = "Tuỳ chỉnh (X/Y %)", Tag = SoundBarCustomPositionTag });
                cmbSoundBarPosition.SelectedIndex = 0;
                // The X / Y boxes are authored in the XAML (X = 50, Y = 82) and those exact values are
                // RenderJob's defaults and the documented API defaults. CmbSoundBarPosition_Changed is
                // IsLoaded-guarded, so it cannot run from the constructor - which is the point: leaving
                // index 0 ("Dưới - Giữa") selected here would leave the combo claiming Y = 95.5 while
                // the box says 82. Point it at "Tuỳ chỉnh" so the label and the boxes agree until the
                // user picks a preset, which then writes both boxes.
                SelectSoundBarCustomPosition();
            }

            // Seed the canvas aspect from the Resolution combo before the user can pick a position
            // preset against a stale one. cmbRes has its XAML default (1080x1920 Portrait) selected by
            // now, so this is normally a real answer, not the 16:9 fallback.
            RefreshSoundBarCanvasAspect();
            UpdateSoundBarCanvasTooltip();
        }

        /// <summary>
        /// Fraction of the canvas left as a margin when snapping to a preset position. Matches the
        /// feel of the existing text / brand-logo anchors, which inset rather than hugging the edge.
        /// </summary>
        private const double SoundBarAnchorMarginFraction = 0.045;

        /// <summary>Tag of the "Tuỳ chỉnh (X/Y %)" entry in the position combo.</summary>
        private const string SoundBarCustomPositionTag = "custom";

        /// <summary>Tag of the "Tuỳ chỉnh" resting entry in the size-preset combo.</summary>
        private const string SoundBarSizeCustomTag = "custom";

        // Width : height ratio the soundbar is authored at. The size slider scales both dimensions by the
        // same factor so the figure keeps its proportions instead of being squashed into a square
        // every time the user touches a size control.
        private const double SoundBarBaseWidthPercent = 62.0;
        private const double SoundBarBaseHeightPercent = 26.0;
        private const double SoundBarBaseAspect = SoundBarBaseWidthPercent / SoundBarBaseHeightPercent;

        /// <summary>
        /// UI POLICY ceiling on the width box, applied BEFORE the height box so the authored
        /// 62 : 26 aspect survives for as long as possible.
        /// <para>
        /// This is NOT a bound anything downstream enforces, and the old comment claiming it was
        /// "just inside the renderer's own 0.02 - 1.0 clamp" was simply false:
        /// <c>SoundBarStyleRenderer.ResolvePlotRect</c> clamps to 0.02 - 1.0, and so does the
        /// pipeline at <c>MainWindow.xaml.cs:2444</c>. Nothing anywhere knows the number 99. It is a
        /// UI choice - a figure needing more than 91 % of the frame width has no room for
        /// <see cref="SoundBarAnchorMarginFraction"/> on either side - and the drift when it bites
        /// is SILENT and ASYMMETRIC: the width is clipped while the height keeps the value
        /// the user typed, so the figure comes out the wrong shape under a label promising 62 : 26.
        /// </para>
        /// <para>
        /// It used to be a literal 99, which is what let the defect back in through the TYPED box.
        /// The slider could not reach 91 (its ceiling is <see cref="SoundBarMaxScaleFraction"/>, the
        /// same limit in scale units), but a hand-typed width bypassed the slider entirely, and
        /// <c>halfWidth</c> pinned at <see cref="SoundBarHalfWidthCeiling"/> collapsed leftX and
        /// rightX to the same 50 %: measured, 92 % gave a 4.00 % inset, 95 % gave 2.50 % and 99 %
        /// gave 0.50 %. Both ceilings are now derived from the same expression, so the number the
        /// slider stops at and the number the box is clamped to cannot drift apart again.
        /// </para>
        /// </summary>
        private static readonly double SoundBarWidthPercentCap =
            2.0 * SoundBarHalfWidthCeiling * 100.0;   // 91.0

        /// <summary>
        /// UI POLICY ceiling on the height box - again not a renderer bound, see
        /// <see cref="SoundBarWidthPercentCap"/>, and enforced on BOTH paths: the slider path
        /// inside <see cref="ComputeSoundBarSizeFootprint"/>, the typed path inside
        /// <see cref="SyncSoundBarSizeFromBoxes"/> and
        /// <see cref="CommitSoundBarSizeBoxEdit"/>.
        /// <para>
        /// 95.5, not 96, and the last 0.5 is not arbitrary. A top / bottom anchor lands the plot
        /// edge at <c>heightPercent + SoundBarAnchorMarginFraction</c>, so a ceiling of 96 put that
        /// sum at 1.005; the clamp to 1.0 then pulled the edge back and rendered a 4.00 % inset
        /// under a preset that promises 4.50 % - measured 210 times across the anchor matrix. The
        /// cap has to satisfy <c>cap + margin &lt;= 1</c>, which is 95.5 exactly.
        /// </para>
        /// </summary>
        private const double SoundBarHeightPercentCap = 95.5;

        /// <summary>Floor of the width / height boxes, in percent. Matches the renderer's own 0.02.</summary>
        private const double SoundBarSizeBoxMinPercent = 2.0;

        private const double SoundBarSizeMin = 20.0;

        /// <summary>
        /// Largest scale FRACTION at which the figure is still too NARROW for the anchor margin to
        /// exist on both sides - the constraint that actually limits the presets. In this type's own
        /// units 1.0 is the authored 62 x 26 footprint, so this is 1.46774.
        /// <para>
        /// The binding constraint is the PRESET FEASIBILITY LIMIT, not <see cref="SoundBarWidthPercentCap"/>
        /// and not <see cref="SoundBarHeightPercentCap"/>. A left anchor at
        /// <c>halfWidth + margin</c> only produces a plot whose left edge is exactly
        /// <c>margin</c> from the frame edge while <c>halfWidth &lt;= 0.5 - margin</c>. Past that,
        /// <c>leftX</c> and <c>rightX</c> both collapse to 50 %, the renderer's own clamp then runs
        /// the plot out to 0.5 % - 99.5 %, and the "preset slams the figure against the frame edge"
        /// defect returns with a preset label still claiming a corner. That is the same failure the
        /// width cap was introduced to remove, so the slider stops BELOW it.
        /// </para>
        /// <para>
        /// Why the two caps are NOT the ceiling, stated once so nobody "corrects" this back: at
        /// 146.77 % the figure is 91.00 % of the frame width, exactly ON the width cap rather than
        /// inside it, and 38.16 % of the height, far inside the height cap. The width cap is now
        /// <c>2 * SoundBarHalfWidthCeiling * 100</c> - the very limit this is derived from, in the
        /// slider's other unit - so "the slider stops at the width cap" is true by construction
        /// rather than by coincidence. The old derivation read <c>min(99 / 62, 96 / 26) = 159.67 %</c>,
        /// which put two of the five shipped presets above the feasibility limit and silently
        /// reintroduced the very defect the commit fixed.
        /// </para>
        /// </summary>
        private static readonly double SoundBarMaxScaleFraction =
            2.0 * SoundBarHalfWidthCeiling / SoundBarBaseWidthPercent * 100.0;   // 1.46774

        /// <summary>
        /// <see cref="SoundBarMaxScaleFraction"/> in the slider's percent units, floored to 2 dp so
        /// the derived width lands a hair UNDER the feasibility limit rather than exactly on it:
        /// 146.77 %.
        /// <para>
        /// The unit hop is a trap worth naming, because getting it wrong is silent. This class's
        /// scale units are 1.0 = the authored footprint, so <see cref="SoundBarMaxScaleFraction"/> is
        /// already 1.46774; reaching percent needs <c>* 100</c>, and flooring that at 2 dp needs a
        /// further <c>* 100</c>. One factor of 100 too few hands back 1.46 - which compiles, ships, and
        /// only detonates when something compares the slider ceiling against the preset list.
        /// </para>
        /// </summary>
        private static readonly double SoundBarSizeMax = Math.Floor(
            SoundBarMaxScaleFraction * 10000.0) / 100.0;   // 146.77

        /// <summary>
        /// The shipped size presets, in combo order, as (label, tag) pairs.
        /// <para>
        /// A data source rather than inline literals, so the list can be CHECKED rather than trusted:
        /// every tag must parse, and every one must be at or below <see cref="SoundBarSizeMax"/> -
        /// the previous list carried a 150 % entry that sat above the feasibility limit and rendered
        /// the figure 3.50 % from the frame edge under a corner-preset label.
        /// </para>
        /// <para>
        /// The final entry is <see cref="SoundBarSizeMax"/>, and its label and tag are formatted from
        /// the same string, so the combo can never read "160 %" while applying 146.77 %.
        /// </para>
        /// </summary>
        internal static readonly (string Label, string Tag)[] SoundBarSizePresetEntries = BuildSoundBarSizePresetEntries();

        private static (string Label, string Tag)[] BuildSoundBarSizePresetEntries()
        {
            (string Label, string Tag)[] fixedEntries =
            {
                ("Rất nhỏ (50%)", "50"),
                ("Nhỏ (75%)", "75"),
                ("Vừa (100%)", "100"),
                ("Lớn (125%)", "125"),
            };

            string maxTag = SoundBarSizeMax.ToString("0.##", CultureInfo.InvariantCulture);
            var entries = new (string Label, string Tag)[fixedEntries.Length + 1];
            Array.Copy(fixedEntries, entries, fixedEntries.Length);
            entries[^1] = ($"Tối đa ({maxTag}%)", maxTag);

            foreach ((string label, string tag) in entries)
            {
                if (!double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) ||
                    scale > SoundBarSizeMax + 0.005 || scale < SoundBarSizeMin - 0.005)
                {
                    throw new InvalidOperationException(
                        $"Soundbar size preset '{label}' (tag '{tag}') is outside the {SoundBarSizeMin:0}-{SoundBarSizeMax:0.##}% " +
                        "range the size control covers. Every entry must yield an exact 4.5 % anchor inset.");
                }
            }

            return entries;
        }

        /// <summary>
        /// Half-width, as a fraction of the frame width, at which a left anchor and a right anchor
        /// would cross over. Above it the plot is too wide for the 4.5 % inset to exist on both
        /// sides, so both presets collapse to centre rather than one of them claiming the far side.
        /// <para>
        /// This ONE expression is the source of truth for three numbers that used to disagree:
        /// the slider ceiling (<see cref="SoundBarMaxScaleFraction"/>), the typed width box ceiling
        /// (<see cref="SoundBarWidthPercentCap"/>) and the <c>halfWidth</c> clamp inside
        /// <see cref="ComputeSoundBarPresetAnchor"/>. They are the same limit in three different
        /// units; deriving all three from here is what stops them drifting apart again.
        /// </para>
        /// </summary>
        private const double SoundBarHalfWidthCeiling = 0.5 - SoundBarAnchorMarginFraction;

        /// <summary>Two preset tags within this many percent counts as the same size.</summary>
        private const double SoundBarSizePresetMatchTolerance = 0.5;

        /// <summary>
        /// Canvas aspect (width / height) the position presets are computed against, captured from
        /// the RESOLUTION controls (cmbRes / txtResW / txtResH) rather than from the last queued job.
        /// <para>
        /// Refreshed by <see cref="RefreshSoundBarCanvasAspect"/> whenever the output resolution
        /// changes, because presets are picked in the UI long before anything is queued - capturing
        /// only at queue time meant a portrait job's presets were computed against 16:9, and for the
        /// square styles on portrait that is the difference between a 4.50 % inset and a 0.00 % one.
        /// 16:9 is used as the value UNTIL the resolution is genuinely known (e.g. "Original" with no
        /// probeable source), and <see cref="_soundBarCanvasAspectIsKnown"/> records which of the two
        /// you are looking at so the UI can say so rather than presenting a guess as an answer. On
        /// the unknown path the field is RESET to that stand-in rather than retained, because the
        /// tooltip and the log both claim 16:9 - a retained value would make both of them false.
        /// </para>
        /// </summary>
        private double _soundBarCanvasAspect = SoundBarCanvasAspectFallback;

        /// <summary>
        /// The documented stand-in used until the output size is resolvable. Named rather than
        /// inlined at the three sites that reset to it, because a tooltip that says "tạm dùng 16:9"
        /// is only true while this is the number actually in the field.
        /// </summary>
        private const double SoundBarCanvasAspectFallback = 16.0 / 9.0;

        /// <summary>Clamp range for <see cref="_soundBarCanvasAspect"/>; a 1-px probe must never reach it.</summary>
        private const double SoundBarCanvasAspectMin = 0.2;
        private const double SoundBarCanvasAspectMax = 5.0;

        /// <summary>
        /// False while <see cref="_soundBarCanvasAspect"/> is the 16:9 stand-in rather than the
        /// resolved output size. Drives the warning in the position-combo tooltip and the log line.
        /// </summary>
        private bool _soundBarCanvasAspectIsKnown;

        /// <summary>
        /// Source dimensions already probed for the currently selected media, so "Original" can be
        /// resolved in the UI without a second ffprobe launch. Zero means "not probed yet".
        /// </summary>
        private int _soundBarSourceWidth;
        private int _soundBarSourceHeight;

        /// <summary>Guards <see cref="ProbeSoundBarSourceResolutionAsync"/> against overlapping probes.</summary>
        private bool _soundBarSourceProbeInFlight;

        /// <summary>
        /// Bumped by every entry to <see cref="ProbeSoundBarSourceResolutionAsync"/>. A probe only
        /// writes its answer if its generation is still this one, so a selection that changes
        /// mid-probe is never overwritten by the result for the file it replaced.
        /// </summary>
        private int _soundBarSourceProbeGeneration;

        /// <summary>
        /// Set when a selection arrives during a probe, so the latch defers it instead of dropping
        /// it. Always paired with a <see cref="_soundBarSourceProbeGeneration"/> bump, which is what
        /// makes the two checks in <see cref="ProbeSoundBarSourceResolutionAsync"/> equivalent.
        /// </summary>
        private bool _soundBarSourceProbeRerunRequested;

        private bool _soundBarApplyingSize;

        /// <summary>
        /// Master size control. 100 % reproduces the authored 62 % x 26 % footprint;
        /// <see cref="SoundBarSizeMax"/> (146.77 %) is the largest scale that both fits inside it
        /// without the width cap distorting the aspect and still leaves the 4.5 % anchor inset on
        /// both sides of a left / right preset.
        /// </summary>
        private void SldSoundBarSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _soundBarApplyingSize)
                return;

            ApplySoundBarSizePercent(e.NewValue);
        }

        private void CmbSoundBarSizePreset_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _soundBarApplyingSize)
                return;

            if ((cmbSoundBarSizePreset?.SelectedItem as ComboBoxItem)?.Tag?.ToString() is string tag &&
                double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
            {
                ApplySoundBarSizePercent(percent);
                return;
            }

            // The "Tuỳ chỉnh" entry has no numeric tag, so selecting it used to do NOTHING: the combo
            // stayed on "Tuỳ chỉnh" even when the slider sat exactly on 100 %, i.e. it asserted
            // "the user chose a custom size" for a size the user never chose, with no correction
            // until some unrelated edit happened to re-sync it. Re-sync from the current slider
            // value instead, which lands back on a named preset when one matches and stays on
            // "Tuỳ chỉnh" when none does.
            if (sldSoundBarSize != null)
                SyncSoundBarSizePresetCombo(sldSoundBarSize.Value);
        }

        /// <summary>
        /// The scale the size control will actually apply. Pure, so the ceiling has one definition.
        /// </summary>
        internal static double ClampSoundBarSizePercent(double percent)
            => Math.Clamp(percent, SoundBarSizeMin, SoundBarSizeMax);

        /// <summary>
        /// Clamps a hand-typed WIDTH box value to the range the position presets can honour.
        /// <para>
        /// Pure and static for the same reason as <see cref="ClampSoundBarSizePercent"/>: the typed
        /// box is the one input the slider cannot constrain, so its ceiling has to be verifiable by
        /// calling it. Every reader of the width box goes through here - the anchor arithmetic, the
        /// job mapping and the preview - because clamping in only one of the three would leave the
        /// other two rendering a width the presets were never computed for.
        /// </para>
        /// </summary>
        /// <param name="widthPercent">Width as a percent figure, i.e. the raw box text.</param>
        internal static double ClampSoundBarTypedWidthPercent(double widthPercent)
            => Math.Clamp(widthPercent, SoundBarSizeBoxMinPercent, SoundBarWidthPercentCap);

        /// <summary>
        /// Clamps a hand-typed HEIGHT box value. The height is the master of the size group, and it
        /// has the same mid-edit hole the width had: the consumer used the raw box value for up to
        /// 700 ms while the settle tick was pending, so a typed 96 rendered a 4.00 % inset under a
        /// preset promising 4.50 %. Clamping at the consumer, not only in the box-sync, is what
        /// makes "the anchors were computed from what is rendered" true at every instant rather than
        /// only after the edit settles.
        /// </summary>
        /// <param name="heightPercent">Height as a percent figure, i.e. the raw box text.</param>
        internal static double ClampSoundBarTypedHeightPercent(double heightPercent)
            => Math.Clamp(heightPercent, SoundBarSizeBoxMinPercent, SoundBarHeightPercentCap);

        /// <summary>
        /// The width / height pair a scale resolves to, in percent of the frame. Height is the
        /// reference (it is what limits circular styles) and the width follows the authored aspect, so
        /// a size slider position always yields the promised ratio.
        /// <para>
        /// Pure and static for the same reason as <see cref="ComputeSoundBarPresetAnchor"/>: this is
        /// half of the geometry the anchor table is verified against, and it must be CALLABLE rather
        /// than transcribed for that verification to mean anything.
        /// </para>
        /// </summary>
        internal static void ComputeSoundBarSizeFootprint(double clampedPercent, out double heightPercent, out double widthPercent)
        {
            double scale = clampedPercent / 100.0;
            heightPercent = Math.Clamp(SoundBarBaseHeightPercent * scale, 2.0, SoundBarHeightPercentCap);
            widthPercent = Math.Clamp(heightPercent * SoundBarBaseAspect, 2.0, SoundBarWidthPercentCap);
        }

        private void ApplySoundBarSizePercent(double percent)
        {
            if (txtSoundBarWidthPercent == null || txtSoundBarHeightPercent == null)
                return;

            double clamped = ClampSoundBarSizePercent(percent);
            if (Math.Abs(clamped - percent) > 0.005)
            {
                LogSystem($"[SOUNDBAR] Size {percent:0.##}% clamped to {clamped:0.##}% - past that scale the "
                          + $"{SoundBarBaseWidthPercent:0}x{SoundBarBaseHeightPercent:0} footprint is wider than the frame "
                          + $"can inset by {SoundBarAnchorMarginFraction * 100:0.##}% on both sides, so the "
                          + $"corner presets would collapse to the middle.");
            }

            ComputeSoundBarSizeFootprint(clamped, out double height, out double width);

            // The boxes are being rewritten from a scale, so any pending settle-commit for a previous
            // hand edit is now moot. Cancelling here keeps that tick from firing against boxes it no
            // longer knows about - harmless (CommitSoundBarSizeBoxEdit recomputes and declines) but
            // one less thing to reason about.
            CancelSoundBarSizeEditCommit();

            _soundBarApplyingSize = true;
            try
            {
                // Caret-preserving: this runs from the settle tick, which can land while the user is
                // still typing in one of these very boxes.
                SetSoundBarBoxText(txtSoundBarHeightPercent, height.ToString("0.##", CultureInfo.InvariantCulture));
                SetSoundBarBoxText(txtSoundBarWidthPercent, width.ToString("0.##", CultureInfo.InvariantCulture));
                if (sldSoundBarSize != null)
                    sldSoundBarSize.Value = clamped;
                if (txtSoundBarSizeValue != null)
                    txtSoundBarSizeValue.Text = $"{Math.Round(clamped, 0):0}%";
            }
            finally
            {
                _soundBarApplyingSize = false;
            }

            SyncSoundBarSizePresetCombo(clamped);

            // Widening or narrowing invalidates the anchors the position presets computed from: the
            // half-width a left / right anchor compensates for is a function of the plot width. So the
            // STILL-SELECTED preset is re-applied against the new footprint - otherwise a figure parked
            // in "Trên - Phải" keeps its old X/Y, drifts off the corner the label promises, and the
            // anchors then freeze for good, because on the NEXT size change ApplySoundBarPositionPreset
            // would read SelectedItem = "custom" and early-return.
            //
            // The preset STAYS selected afterwards, deliberately. The anchors this writes genuinely
            // ARE that preset evaluated at the new size, so flipping the label to "Tuỳ chỉnh" would
            // claim the user authored X/Y they never touched - and would make the re-apply a one-shot
            // that silently stops holding the corner on the following change. SelectSoundBarCustomPosition
            // is still reached, from TxtSoundBarPercent_TextChanged, for the case it exists for: the
            // user hand-editing X or Y.
            ApplySoundBarPositionPreset();
            ScheduleSoundBarPreview();
        }

        /// <summary>
        /// Recomputes the size slider from a hand-edited width/height so the two controls never
        /// disagree. The height is the reference because it is what limits circular styles.
        /// <para>
        /// The boxes are deliberately NOT rewritten here - this runs on every keystroke, so clamping
        /// the text out from under the caret would fight the edit. That leaves one damage case: a
        /// height the slider cannot express (a half-typed "5" is 19.23 %, below the 20 % floor) keeps
        /// the user's number in the box while the slider snaps to the nearest expressible scale, and
        /// the RENDER honours the boxes - a 62 % x 5 % figure under a slider reading 20 %, silently.
        /// <see cref="CommitSoundBarSizeBoxEdit"/> closes it once the edit settles; see that method
        /// for why the fix is deferred rather than immediate.
        /// </para>
        /// <para>
        /// This re-applies the still-selected POSITION preset exactly as
        /// <see cref="ApplySoundBarSizePercent"/> does. It did not, so dragging the slider held the
        /// corner and typing in the box did not - reachable in two keystrokes, and it left the preset
        /// label asserting an anchor the boxes no longer produce.
        /// </para>
        /// </summary>
        private void SyncSoundBarSizeFromBoxes()
        {
            if (_soundBarApplyingSize || sldSoundBarSize == null)
                return;

            double height = ClampSoundBarTypedHeightPercent(ParsePercent(txtSoundBarHeightPercent?.Text, SoundBarBaseHeightPercent));
            double rawPercent = (height / SoundBarBaseHeightPercent) * 100.0;
            double percent = ClampSoundBarSizePercent(rawPercent);

            _soundBarApplyingSize = true;
            try
            {
                sldSoundBarSize.Value = percent;
                if (txtSoundBarSizeValue != null)
                    txtSoundBarSizeValue.Text = $"{Math.Round(percent, 0):0}%";
            }
            finally
            {
                _soundBarApplyingSize = false;
            }

            SyncSoundBarSizePresetCombo(percent);

            // Same writer, same behaviour: the anchors are re-derived from the new footprint and the
            // preset label stays put, because they still ARE that preset at this size.
            ApplySoundBarPositionPreset();

            // Deferred repair for anything the size control cannot express. The WIDTH needs its own
            // trigger: the slider is derived from the height, so an over-wide box never moved it and
            // nothing armed the tick on the user's behalf - which is how a typed 99 % survived and
            // collapsed the left / right presets to 0.5 % from the frame edge.
            double typedWidth = ParsePercent(txtSoundBarWidthPercent?.Text, SoundBarBaseWidthPercent);
            bool widthUnrepresentable = Math.Abs(typedWidth - ClampSoundBarTypedWidthPercent(typedWidth)) > 0.005;
            if (widthUnrepresentable || Math.Abs(rawPercent - percent) > 0.005)
                ArmSoundBarSizeEditCommit();
        }

        /// <summary>Quiet period after the last width/height keystroke before the boxes are repaired.</summary>
        private static readonly TimeSpan SoundBarSizeEditSettleDelay = TimeSpan.FromMilliseconds(700);

        private readonly DispatcherTimer _soundBarSizeEditCommitTimer;

        /// <summary>
        /// Writes a soundbar box's text without moving the caret.
        /// <para>
        /// Assigning <see cref="TextBox.Text"/> parks the caret at index 0 and drops any selection.
        /// Measured: type "5", pause past the settle delay, and the tick rewrites "5.2" with the
        /// caret at 0 - so the "0" of a fast "50" lands at the START and produces "05.2"; selecting
        /// "38" and ticking produces "26". Skipping the write while the box holds the keyboard
        /// focus is not an alternative either: the caret survives, but the box keeps advertising a
        /// size nothing is rendering, which is the defect the settle tick exists to remove.
        /// </para>
        /// </summary>
        private static void SetSoundBarBoxText(TextBox? box, string text)
        {
            if (box == null || string.Equals(box.Text, text, StringComparison.Ordinal))
                return;

            int caret = box.CaretIndex;
            int selectionStart = box.SelectionStart;
            int selectionLength = box.SelectionLength;

            box.Text = text;

            // The replacement can be shorter than the caret was, so both ends are clamped rather
            // than trusted - Select throws ArgumentOutOfRangeException on an out-of-window range.
            int max = box.Text.Length;
            if (selectionLength > 0)
            {
                int start = Math.Clamp(selectionStart, 0, max);
                box.Select(start, Math.Min(selectionLength, max - start));
            }
            else
            {
                box.CaretIndex = Math.Clamp(caret, 0, max);
            }
        }

        /// <summary>
        /// Cancels a pending settle-commit. Safe from any context, including before the timer is
        /// constructed during <c>InitializeComponent</c>.
        /// </summary>
        private void CancelSoundBarSizeEditCommit()
        {
            try { _soundBarSizeEditCommitTimer?.Stop(); } catch { }
        }

        private void ArmSoundBarSizeEditCommit()
        {
            // The null check is load-bearing, not defensive: TxtSoundBarPercent_TextChanged is not
            // IsLoaded-guarded, so it fires from InitializeComponent while the XAML is still being
            // parsed - and the timer is constructed after InitializeComponent returns.
            if (_soundBarSizeEditCommitTimer == null)
                return;

            try
            {
                _soundBarSizeEditCommitTimer.Stop();
                _soundBarSizeEditCommitTimer.Interval = SoundBarSizeEditSettleDelay;
                _soundBarSizeEditCommitTimer.Start();
            }
            catch (Exception ex)
            {
                LogSystem($"[SOUNDBAR-WARN] Size edit commit could not be scheduled: {ex.Message}");
            }
        }

        private void SoundBarSizeEditCommitTimer_Tick(object? sender, EventArgs e)
        {
            // Stop FIRST: CommitSoundBarSizeBoxEdit rewrites the boxes, which raises TextChanged,
            // which re-arms this timer. Stopping up front makes that re-arm a no-op instead of a
            // second pending tick.
            _soundBarSizeEditCommitTimer?.Stop();
            CommitSoundBarSizeBoxEdit();
        }

        /// <summary>
        /// Rewrites the width/height boxes from the slider once a hand edit that the slider cannot
        /// express has SETTLED, and says so in the log.
        /// <para>
        /// Deferred, not per-keystroke, on purpose. Rewriting under an active caret is what the old
        /// comment here called "fighting the edit", and it is worse than that: "5" -&gt; "5.2"
        /// before the "0" of a fast "50" turns the typed value into "05.2". Two things stop that,
        /// and both are load-bearing: the write waits for a quiet period, AND
        /// <see cref="SetSoundBarBoxText"/> keeps the caret where the user left it. The first alone
        /// was not enough - measured, a pause of only 700 ms between the two digits was enough to
        /// park the caret at 0 and reverse the edit.
        /// </para>
        /// <para>
        /// The mirror is idempotent: after it runs, the boxes imply a scale inside
        /// [SoundBarSizeMin, SoundBarSizeMax], a width within
        /// [SoundBarSizeBoxMinPercent, SoundBarWidthPercentCap] and a height within
        /// [SoundBarSizeBoxMinPercent, SoundBarHeightPercentCap], so the next keystroke arms nothing
        /// and this never runs twice for one edit.
        /// </para>
        /// <para>
        /// A box the user is STILL TYPING IN is skipped outright, and re-armed by
        /// <c>LostKeyboardFocus</c> instead. That is stronger than preserving the caret, and it has
        /// to be: a caret-preserving rewrite of "5" still lands "5.2", so typing "5", pausing past
        /// the settle delay and then typing "0" yields "50.2" rather than the "50" the user was
        /// after. Only not touching the box until focus leaves can deliver that, and focus leaving
        /// is exactly the point at which the number is finished.
        /// </para>
        /// </summary>
        private void CommitSoundBarSizeBoxEdit()
        {
            try
            {
                if (_soundBarApplyingSize || sldSoundBarSize == null ||
                    txtSoundBarWidthPercent == null || txtSoundBarHeightPercent == null)
                    return;

                // The height is the master of the size group, so it is repaired first; the
                // rewrite it triggers re-derives BOTH boxes from the scale, which settles the width
                // too, because the footprint it writes is at most SoundBarWidthPercentCap wide by
                // construction.
                if (!txtSoundBarHeightPercent.IsKeyboardFocusWithin)
                {
                    double typedHeight = ParsePercent(txtSoundBarHeightPercent?.Text, SoundBarBaseHeightPercent);
                    double height = ClampSoundBarTypedHeightPercent(typedHeight);
                    double rawPercent = (height / SoundBarBaseHeightPercent) * 100.0;
                    double percent = ClampSoundBarSizePercent(rawPercent);

                    if (Math.Abs(rawPercent - percent) > 0.005)
                    {
                        // typedHeight, not height: the box still says what the user typed, and reporting
                        // the already-clamped figure back to them would be a number they never entered.
                        LogSystem($"[SOUNDBAR] Height box {typedHeight:0.##}% needs {rawPercent:0.##}%, which is outside the "
                                  + $"{SoundBarSizeMin:0}%-{SoundBarSizeMax:0.##}% the size control covers at the "
                                  + $"{SoundBarBaseWidthPercent:0}x{SoundBarBaseHeightPercent:0} footprint. The boxes now read "
                                  + $"{SoundBarBaseHeightPercent * (percent / 100.0):0.##}% x {SoundBarBaseHeightPercent * (percent / 100.0) * SoundBarBaseAspect:0.##}% - "
                                  + $"keeping {typedHeight:0.##}% would have rendered a figure that breaks that ratio. "
                                  + $"The previous commit suppressed this line because it also fires mid-word; it fires here only once the edit has settled.");

                        ApplySoundBarSizePercent(percent);
                        return;
                    }
                }

                // The height was expressible (or is mid-edit), so the scale never moved and only the
                // width can still be out of range. Its ceiling is a different limit from the scale
                // ceiling - that is the whole reason this branch exists.
                if (txtSoundBarWidthPercent.IsKeyboardFocusWithin)
                    return;

                double typedWidth = ParsePercent(txtSoundBarWidthPercent?.Text, SoundBarBaseWidthPercent);
                double clampedWidth = ClampSoundBarTypedWidthPercent(typedWidth);
                if (Math.Abs(typedWidth - clampedWidth) <= 0.005)
                    return;

                LogSystem($"[SOUNDBAR] Width box {typedWidth:0.##}% is over the {SoundBarWidthPercentCap:0.##}% ceiling "
                          + $"the corner presets need in order to leave {SoundBarAnchorMarginFraction * 100:0.##}% of frame on both sides; "
                          + $"past it a left and a right anchor collapse to the same centre. Clamped to {clampedWidth:0.##}%.");

                SetSoundBarBoxText(txtSoundBarWidthPercent, clampedWidth.ToString("0.##", CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                LogSystem($"[SOUNDBAR-WARN] Size edit commit failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-arms the settle-commit when a size box loses the keyboard focus.
        /// <para>
        /// This is the other half of the focus rule in <see cref="CommitSoundBarSizeBoxEdit"/>: the
        /// tick declines to rewrite a box the user is still in, so the repair has to be re-queued at
        /// the moment the edit is over. Without it, a half-typed "5" that the user abandons by
        /// clicking away would keep its 20 % slider forever.
        /// </para>
        /// </summary>
        private void TxtSoundBarSizeBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is not TextBox box)
                return;

            if (ReferenceEquals(box, txtSoundBarWidthPercent) || ReferenceEquals(box, txtSoundBarHeightPercent))
                ArmSoundBarSizeEditCommit();
        }

        /// <summary>
        /// Points the size-preset combo at the entry matching <paramref name="percent"/>, or at the
        /// "Tuỳ chỉnh" entry when no preset matches - which is every value the slider can sit on
        /// between two presets.
        /// <para>
        /// Without this the combo is written exactly once, at startup, and then goes stale forever.
        /// WPF raises SelectionChanged only when SelectedIndex actually changes, so re-clicking the
        /// stale entry does nothing at all and the boxes never update again.
        /// </para>
        /// </summary>
        private void SyncSoundBarSizePresetCombo(double percent)
        {
            if (_soundBarApplyingSize || cmbSoundBarSizePreset == null || cmbSoundBarSizePreset.Items.Count == 0)
                return;

            int target = FindSoundBarSizePresetIndex(percent);
            if (target < 0 || target == cmbSoundBarSizePreset.SelectedIndex)
                return;

            _soundBarApplyingSize = true;
            try
            {
                cmbSoundBarSizePreset.SelectedIndex = target;
            }
            finally
            {
                _soundBarApplyingSize = false;
            }
        }

        /// <summary>
        /// Index of the preset whose Tag equals <paramref name="percent"/> to within
        /// <see cref="SoundBarSizePresetMatchTolerance"/>, or of the "Tuỳ chỉnh" entry when none
        /// matches. -1 only if the combo lost both, which PopulateSoundBarComboBoxes never allows.
        /// </summary>
        private int FindSoundBarSizePresetIndex(double percent)
        {
            if (cmbSoundBarSizePreset == null)
                return -1;

            int customIndex = -1;

            for (int i = 0; i < cmbSoundBarSizePreset.Items.Count; i++)
            {
                if (cmbSoundBarSizePreset.Items[i] is not ComboBoxItem item)
                    continue;

                string tag = item.Tag?.ToString() ?? string.Empty;
                if (string.Equals(tag, SoundBarSizeCustomTag, StringComparison.OrdinalIgnoreCase))
                {
                    customIndex = i;
                    continue;
                }

                if (double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double tagPercent) &&
                    Math.Abs(tagPercent - percent) < SoundBarSizePresetMatchTolerance)
                {
                    return i;
                }
            }

            return customIndex;
        }

        /// <summary>
        /// Combo entry point only. The anchor arithmetic lives in
        /// <see cref="ApplySoundBarPositionPreset"/> because a SIZE change has to re-run it, and a
        /// SelectionChanged handler cannot be re-invoked once its own guard flag is set.
        /// <para>
        /// The IsLoaded guard matters: without it PopulateSoundBarComboBoxes - which runs from the
        /// constructor, before the window is loaded - writes the anchors of whatever entry it happens
        /// to select, overwriting the authored X / Y defaults with values the user never chose.
        /// </para>
        /// </summary>
        private void CmbSoundBarPosition_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded)
                return;

            ApplySoundBarPositionPreset();
        }

        /// <summary>
        /// The X / Y anchor pair a position preset resolves to, for a given plot footprint and canvas.
        /// <para>
        /// Pure and static on purpose: this is the single source of truth for the preset arithmetic,
        /// and keeping it free of UI state is what lets the anchor table be verified by CALLING it
        /// (see <c>SoundBarAnchorProbe</c>) rather than by transcribing a copy of it into a throwaway
        /// script - which is how the previous pass shipped a false claim about the inset.
        /// </para>
        /// <para>
        /// Mirrors <c>SoundBarStyleRenderer.ResolvePlotRect</c>, which is the authority this whole
        /// feature is built on. There, <c>XPercent</c> is the plot's HORIZONTAL CENTRE and
        /// <c>YPercent</c> is its BOTTOM edge; the plot is <c>widthPercent * W</c> by
        /// <c>heightPercent * H</c>; the left edge is <c>anchorX - plotW / 2</c> and the top edge is
        /// <c>anchorY - plotH</c>. So a left / right anchor compensates for HALF the plot width and a
        /// top anchor for the WHOLE plot height.
        /// </para>
        /// <para>
        /// The six styles in <c>SoundBarStyleRenderer.UsesSquarePlot</c> are squared IN PIXELS:
        /// <c>side = min(widthPercent * W, heightPercent * H)</c>. Expressed back as fractions of
        /// their own axis that is <c>min(widthPercent, heightPercent / aspect)</c> across and
        /// <c>min(heightPercent, widthPercent * aspect)</c> down. Both are needed: the squaring was
        /// previously applied to X only, so on portrait - where WIDTH is the smaller edge - every top
        /// and centre anchor was computed from the unsquared height while the renderer was drawing a
        /// square. Measured on 1080x1920 at the shipped 62 % / 26 % default, the top inset came out
        /// at 65.13 % instead of 4.50 %; a hand-entered 10 % / 26 % put it at 24.88 %, and 26 % / 90 %
        /// at 79.88 %.
        /// </para>
        /// </summary>
        /// <param name="tag">Preset tag, e.g. <c>bottom-left</c>. Unknown / custom tags fall back to bottom-centre.</param>
        /// <param name="widthPercent">Plot width as a fraction of the canvas width, already clamped to 0.02 - 1.0.</param>
        /// <param name="heightPercent">Plot height as a fraction of the canvas height, already clamped to 0.02 - 1.0.</param>
        /// <param name="canvasAspect">Canvas width / height.</param>
        /// <param name="usesSquarePlot">Whether the renderer squares the plot in pixels for this style.</param>
        internal static (double X, double Y) ComputeSoundBarPresetAnchor(
            string tag,
            double widthPercent,
            double heightPercent,
            double canvasAspect,
            bool usesSquarePlot)
        {
            double aspect = canvasAspect > 0 && !double.IsNaN(canvasAspect) && !double.IsInfinity(canvasAspect)
                ? canvasAspect
                : 16.0 / 9.0;

            double plotWidthFraction = usesSquarePlot
                ? Math.Min(widthPercent, heightPercent / aspect)
                : widthPercent;
            double plotHeightFraction = usesSquarePlot
                ? Math.Min(heightPercent, widthPercent * aspect)
                : heightPercent;

            double margin = SoundBarAnchorMarginFraction;
            double halfWidth = Math.Min(plotWidthFraction * 0.5, SoundBarHalfWidthCeiling);
            double leftX = Math.Clamp(halfWidth + margin, 0.0, 1.0);
            double rightX = Math.Clamp(1.0 - halfWidth - margin, 0.0, 1.0);
            double bottomY = Math.Clamp(1.0 - margin, 0.0, 1.0);
            double topY = Math.Clamp(plotHeightFraction + margin, 0.0, 1.0);
            double centerY = Math.Clamp(0.5 + (plotHeightFraction * 0.5), 0.0, 1.0);

            double x = 0.5;
            double y = bottomY;

            switch (tag)
            {
                case "bottom-left": x = leftX; y = bottomY; break;
                case "bottom-right": x = rightX; y = bottomY; break;
                case "center": x = 0.5; y = centerY; break;
                case "top-center": x = 0.5; y = topY; break;
                case "top-left": x = leftX; y = topY; break;
                case "top-right": x = rightX; y = topY; break;
            }

            return (x, y);
        }

        /// <summary>
        /// Writes a preset position into the X / Y boxes.
        /// <para>
        /// This is a UI convenience ONLY - there is deliberately no new API field. The renderer's
        /// anchor semantics are fixed in <c>ResolvePlotRect</c>: X is the HORIZONTAL CENTRE of the plot
        /// rect and Y is its BOTTOM edge (the bars grow upward from it). A preset is therefore not a
        /// free-standing value, it is a computed X/Y pair, and inventing a parallel
        /// <c>soundBarPosition</c> field would create a second source of truth that can disagree with
        /// X/Y - exactly the "documented but never read" bug class this feature already had twice.
        /// </para>
        /// <para>
        /// The arithmetic itself lives in <see cref="ComputeSoundBarPresetAnchor"/>, which carries the
        /// derivation and the measured failure cases; this method only reads the boxes, asks for the
        /// current style, and writes the result back.
        /// </para>
        /// </summary>
        private void ApplySoundBarPositionPreset()
        {
            if (_soundBarApplyingPosition || cmbSoundBarPosition?.SelectedItem is not ComboBoxItem item)
                return;

            string tag = item.Tag?.ToString() ?? SoundBarCustomPositionTag;
            if (string.Equals(tag, SoundBarCustomPositionTag, StringComparison.OrdinalIgnoreCase))
                return;

            double widthPercent = ClampSoundBarTypedWidthPercent(ParsePercent(txtSoundBarWidthPercent?.Text, SoundBarBaseWidthPercent)) / 100.0;
            double heightPercent = ClampSoundBarTypedHeightPercent(ParsePercent(txtSoundBarHeightPercent?.Text, SoundBarBaseHeightPercent)) / 100.0;

            SoundBarStyleId styleId = SoundBarVisualizerPreset.FindStyle(GetSelectedSoundBarStyleId())?.Id
                ?? SoundBarStyleId.NeonBars;

            (double x, double y) = ComputeSoundBarPresetAnchor(
                tag,
                widthPercent,
                heightPercent,
                _soundBarCanvasAspect,
                SoundBarStyleRenderer.UsesSquarePlot(styleId));

            _soundBarApplyingPosition = true;
            try
            {
                if (txtSoundBarXPercent != null)
                    SetSoundBarBoxText(txtSoundBarXPercent, (x * 100.0).ToString("0.##", CultureInfo.InvariantCulture));
                if (txtSoundBarYPercent != null)
                    SetSoundBarBoxText(txtSoundBarYPercent, (y * 100.0).ToString("0.##", CultureInfo.InvariantCulture));
            }
            finally
            {
                _soundBarApplyingPosition = false;
            }

            ScheduleSoundBarPreview();
        }

        /// <summary>
        /// Selecting the custom entry without touching X/Y, so the user is not immediately snapped
        /// back to a preset by their own edit.
        /// <para>
        /// The index is resolved by scanning for <see cref="SoundBarCustomPositionTag"/> rather than
        /// hard-coded: a fixed index silently selects a PRESET if the list is ever reordered, and the
        /// <c>_soundBarApplyingPosition</c> flag set around the assignment then suppresses the handler
        /// that would have written the anchors - leaving a label promising a position the frame is not
        /// in, with nothing logged.
        /// </para>
        /// </summary>
        private void SelectSoundBarCustomPosition()
        {
            if (_soundBarApplyingPosition || cmbSoundBarPosition == null)
                return;

            int customIndex = -1;
            for (int i = 0; i < cmbSoundBarPosition.Items.Count; i++)
            {
                if (cmbSoundBarPosition.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Tag?.ToString(), SoundBarCustomPositionTag, StringComparison.OrdinalIgnoreCase))
                {
                    customIndex = i;
                    break;
                }
            }

            if (customIndex < 0)
            {
                LogSystem("[SOUNDBAR-WARN] Position combo has no 'custom' entry; X/Y edits cannot be labelled.");
                return;
            }

            if (cmbSoundBarPosition.SelectedIndex == customIndex)
                return;

            _soundBarApplyingPosition = true;
            try
            {
                cmbSoundBarPosition.SelectedIndex = customIndex;
            }
            finally
            {
                _soundBarApplyingPosition = false;
            }
        }

        private bool _soundBarApplyingPosition;

        private string GetSelectedSoundBarStyleId()
        {
            return (cmbSoundBarStyle?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "neon-bars";
        }

        private void ChkEnableSoundBar_Changed(object sender, RoutedEventArgs e)
        {
            // Turning the panel off ends the size edit: a tick landing afterwards would rewrite the
            // boxes of a control the user can no longer see or reach, and would re-arm itself once.
            CancelSoundBarSizeEditCommit();

            UpdateSoundBarUiState();

            if (chkEnableSoundBar?.IsChecked == true)
                ScheduleSoundBarPreview();
            else
                CancelSoundBarPreview();
        }

        private void CmbSoundBar_Changed(object sender, SelectionChangedEventArgs e)
        {
            UpdateSoundBarDescription();
            UpdateSoundBarUiState();
            ScheduleSoundBarPreview();
        }

        /// <summary>
        /// Opacity / sensitivity / smoothing / bar-count sliders all feed the same debounced
        /// preview pass. ScheduleSoundBarPreview coalesces a whole drag into a single render,
        /// so no throttle of its own is needed here. It no-ops while the soundbar is off, which
        /// also covers the ValueChanged that WPF raises while InitializeComponent parses XAML.
        /// </summary>
        private void SldSoundBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            ScheduleSoundBarPreview();
        }

        /// <summary>
        /// X / Y / width / height boxes. No IsKeyboardFocusWithin guard: this file does not use
        /// one anywhere, and both the 250 ms preview debounce and the 700 ms settle-commit leave
        /// room for the intermediate states of "5" -> "50". Unparseable text falls back to the field
        /// default inside ParsePercent, so a half-typed value can never throw or blank the preview.
        /// </summary>
        private void TxtSoundBarPercent_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Editing X or Y by hand means the preset no longer describes what is on screen - flip the
            // combo to "Custom" so it never silently disagrees with the boxes. BOTH boxes count: the
            // combo tooltip promises "sửa tay ô X/Y", and Y is half of the anchor pair. The
            // _soundBarApplyingPosition guard is load-bearing - ApplySoundBarPositionPreset writes
            // these very boxes under that flag, and re-entering here would overwrite the preset with
            // its own output.
            if (!_soundBarApplyingPosition &&
                (ReferenceEquals(sender, txtSoundBarXPercent) || ReferenceEquals(sender, txtSoundBarYPercent)))
            {
                SelectSoundBarCustomPosition();
            }

            // Width / height drive the master size readout.
            if (!_soundBarApplyingSize &&
                (ReferenceEquals(sender, txtSoundBarWidthPercent) || ReferenceEquals(sender, txtSoundBarHeightPercent)))
                SyncSoundBarSizeFromBoxes();

            ScheduleSoundBarPreview();
        }

        private void UpdateSoundBarDescription()
        {
            if (txtSoundBarDescription == null)
                return;

            try
            {
                var style = SoundBarVisualizerPreset.FindStyle(GetSelectedSoundBarStyleId());
                txtSoundBarDescription.Text = style?.Description ?? string.Empty;
            }
            catch (Exception ex)
            {
                LogSystem($"[SOUNDBAR-WARN] Style description unavailable: {ex.Message}");
                txtSoundBarDescription.Text = string.Empty;
            }
        }

        private void UpdateSoundBarUiState()
        {
            bool enabled = chkEnableSoundBar?.IsChecked == true;

            if (pnlSoundBarSettings != null)
                pnlSoundBarSettings.IsEnabled = enabled;

            if (brdSoundBarPreview != null)
                brdSoundBarPreview.Opacity = enabled ? 1.0 : 0.45;
        }

        /// <summary>
        /// Debounces preview regeneration so dragging through the combo boxes does not
        /// spawn one render pass per keystroke-equivalent interaction.
        /// </summary>
        private void ScheduleSoundBarPreview()
        {
            if (chkEnableSoundBar?.IsChecked != true)
                return;

            _ = RegenerateSoundBarPreviewAsync();
        }

        private void CancelSoundBarPreview()
        {
            try { _soundBarPreviewCts?.Cancel(); } catch { }
            try { _soundBarPreviewCts?.Dispose(); } catch { }
            _soundBarPreviewCts = null;
        }

        /// <summary>
        /// Pixel budget for the preview render - the exact pixel count of the hard-coded 480 x 270 the
        /// preview used to be locked to, so following the output aspect costs the SAME to render.
        /// </summary>
        private const int SoundBarPreviewPixelBudget = 480 * 270;

        /// <summary>Preview border height, matching the XAML default, used for landscape previews.</summary>
        private const double SoundBarPreviewBorderHeight = 144.0;

        /// <summary>Upper bound on the preview border, so a 9:16 preview cannot take over the panel.</summary>
        private const double SoundBarPreviewBorderHeightMax = 260.0;

        /// <summary>
        /// Preview canvas size for the CURRENT output aspect, at a fixed pixel budget.
        /// <para>
        /// The preview used to hard-code 480 x 270, so corner placement was always judged on a 16:9
        /// thumbnail no matter what the output was - which is precisely why the portrait aspect defect
        /// stayed invisible: a figure slammed against both frame edges in the real render looks
        /// comfortably inset on a 16:9 thumbnail. Sizing the canvas to the resolved aspect costs
        /// nothing measurable, because a fixed AREA is held rather than a fixed pair of sides:
        /// 16:9 still yields exactly 480 x 270, 9:16 yields 270 x 480.
        /// </para>
        /// </summary>
        private static void ResolveSoundBarPreviewCanvas(double canvasAspect, out int width, out int height)
        {
            double aspect = canvasAspect > 0 && !double.IsNaN(canvasAspect) && !double.IsInfinity(canvasAspect)
                ? canvasAspect
                : 16.0 / 9.0;

            width = (int)Math.Round(Math.Sqrt(SoundBarPreviewPixelBudget * aspect), MidpointRounding.AwayFromZero);
            height = (int)Math.Round(Math.Sqrt(SoundBarPreviewPixelBudget / aspect), MidpointRounding.AwayFromZero);

            // Even sides only (some encoders dislike odd extents).
            //
            // There is deliberately no further clamp. The one that used to be here, [64, 960], was
            // unreachable and its comment was wrong on both counts: sweeping the allowed aspect
            // range [SoundBarCanvasAspectMin, SoundBarCanvasAspectMax] gives sides spanning
            // 160..804, so neither bound could ever engage, and it described the floor as stopping
            // "a 5:1 ultrawide degenerating into a 16 px sliver" - the 5:1 side is 160 px, 4x the
            // floor it was protecting. A side can only drop under 64 px at aspect < 0.032 or
            // > 31.6, both far outside the clamp above. If either constant is ever widened, the
            // bound has to come back with it.
            width = width / 2 * 2;
            height = height / 2 * 2;
        }

        private async Task RegenerateSoundBarPreviewAsync()
        {
            CancelSoundBarPreview();
            _soundBarPreviewCts = new CancellationTokenSource();
            CancellationToken token = _soundBarPreviewCts.Token;

            try
            {
                // 250 ms debounce.
                await Task.Delay(250, token);

                var settings = new SoundBarSettings
                {
                    Style = SoundBarVisualizerPreset.FindStyle(GetSelectedSoundBarStyleId())?.Id ?? SoundBarStyleId.NeonBars,
                    PaletteId = (cmbSoundBarPalette?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ncs-cyan",
                    XPercent = ParsePercent(txtSoundBarXPercent?.Text, 50.0) / 100.0,
                    YPercent = ParsePercent(txtSoundBarYPercent?.Text, 82.0) / 100.0,
                    WidthPercent = ClampSoundBarTypedWidthPercent(ParsePercent(txtSoundBarWidthPercent?.Text, 62.0)) / 100.0,
                    HeightPercent = ClampSoundBarTypedHeightPercent(ParsePercent(txtSoundBarHeightPercent?.Text, 26.0)) / 100.0,
                    Opacity = Math.Clamp(sldSoundBarOpacity?.Value ?? 1.0, SoundBarMinOpacity, SoundBarMaxOpacity),
                    Sensitivity = Math.Clamp(sldSoundBarSensitivity?.Value ?? 1.0, SoundBarMinSensitivity, SoundBarMaxSensitivity),
                    Smoothing = Math.Clamp(sldSoundBarSmoothing?.Value ?? 1.0, SoundBarMinSmoothing, SoundBarMaxSmoothing),
                    BarCount = (int)Math.Round(Math.Clamp(sldSoundBarBarCount?.Value ?? 64.0, SoundBarMinBarCount, SoundBarMaxBarCount)),
                    RingCount = 2,
                    Roundness = 1.0,
                    GlowIntensity = 1.0,
                    LoopSeconds = 5.0,
                    FrameRate = 30.0
                };

                ColorPreset palette = ColorPresets.GetOrDefault(settings.PaletteId);
                string previewDir = Path.Combine(Path.GetTempPath(), "TitanEngine", "soundbar_preview");
                Directory.CreateDirectory(previewDir);

                // Follow the resolved output aspect so the preview shows the same geometry the render
                // will produce. Same pixel count as the old fixed 480 x 270, so same cost. When the
                // aspect is still the un-probed 16:9 stand-in this is exactly 480 x 270 - i.e. the
                // preview honestly shows the assumption rather than hiding it.
                ResolveSoundBarPreviewCanvas(_soundBarCanvasAspect, out int previewWidth, out int previewHeight);

                // GeneratePreviewPngAsync renders one deterministic frame (no audio file needed)
                // and caches it under previewDir, so the returned file is intentionally NOT deleted.
                // The cache key already covers renderW / renderH, so a new aspect cannot serve a PNG
                // rendered at the old one.
                string? previewPath = await SoundBarVisualizerArt.GeneratePreviewPngAsync(
                    settings,
                    palette,
                    previewWidth,
                    previewHeight,
                    previewDir,
                    LogSystem,
                    token);

                if (token.IsCancellationRequested || string.IsNullOrWhiteSpace(previewPath) || !File.Exists(previewPath))
                    return;

                // Cap the preview directory BEFORE loading the bytes, so previewPath is never a
                // pruning candidate. The filename hashes every tunable, which is exactly why the
                // directory needs a cap: 90 Opacity positions means 90 PNGs, and none is ever
                // revisited. Runs on cache hits too, so the cap holds however the picker is used.
                int prunedSoundBarPreviews = PruneSoundBarPreviewCache(previewDir, previewPath);
                if (prunedSoundBarPreviews > 0)
                    LogSystem($"[SOUNDBAR] Pruned {prunedSoundBarPreviews} stale preview PNG(s) from {previewDir}");

                byte[] raw = await File.ReadAllBytesAsync(previewPath, token);
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
                    if (imgSoundBarPreview != null)
                        imgSoundBarPreview.Source = bmp;

                    // The Border is Height="144" in XAML, which is right for a 16:9 preview but would
                    // squash a portrait one into an ~81 px strip under Stretch="Uniform" - too small to
                    // judge a corner. Follow the preview's own aspect, never shrinking below the
                    // authored height and never growing without bound. Layout-only; no soundbar
                    // handler reads it, so it cannot feed back into the size or position controls.
                    if (brdSoundBarPreview != null && previewHeight > 0)
                    {
                        double wanted = SoundBarPreviewBorderHeight * previewHeight / (double)previewWidth;
                        brdSoundBarPreview.Height = Math.Clamp(wanted, SoundBarPreviewBorderHeight, SoundBarPreviewBorderHeightMax);
                    }

                    if (txtSoundBarPreviewHint != null)
                        txtSoundBarPreviewHint.Visibility = Visibility.Collapsed;
                });

                LogSystem("[SOUNDBAR] Preview updated");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogSystem($"[SOUNDBAR-PREVIEW-WARN] {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void ApplySoundBarSettingsToJob(RenderJob job)
        {
            if (job == null)
                return;

            // A settle-commit still pending would repair the boxes AFTER this job was built from
            // them, so the render would use the un-repaired figure and only the screen would show
            // the corrected one. Cancelling is right rather than forcing the repair here: the boxes
            // as they stand are the post-keystroke state the user last expressed, and queue time is
            // not a settle point.
            CancelSoundBarSizeEditCommit();

            CaptureSoundBarCanvasAspect(job);
            job.EnableSoundBar = chkEnableSoundBar?.IsChecked ?? false;
            job.SoundBarStyle = (cmbSoundBarStyle?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "neon-bars";
            job.SoundBarPalette = (cmbSoundBarPalette?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ncs-cyan";
            job.SoundBarAudioSource = (cmbSoundBarAudioSource?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
            job.SoundBarXPercent = Math.Clamp(ParsePercent(txtSoundBarXPercent?.Text, 50.0) / 100.0, 0.0, 1.0);
            job.SoundBarYPercent = Math.Clamp(ParsePercent(txtSoundBarYPercent?.Text, 82.0) / 100.0, 0.0, 1.0);
            job.SoundBarWidthPercent = ClampSoundBarTypedWidthPercent(ParsePercent(txtSoundBarWidthPercent?.Text, 62.0)) / 100.0;
            job.SoundBarHeightPercent = ClampSoundBarTypedHeightPercent(ParsePercent(txtSoundBarHeightPercent?.Text, 26.0)) / 100.0;
            job.SoundBarOpacity = Math.Clamp(sldSoundBarOpacity?.Value ?? 1.0, SoundBarMinOpacity, SoundBarMaxOpacity);
            job.SoundBarSensitivity = Math.Clamp(sldSoundBarSensitivity?.Value ?? 1.0, SoundBarMinSensitivity, SoundBarMaxSensitivity);
            job.SoundBarSmoothing = Math.Clamp(sldSoundBarSmoothing?.Value ?? 1.0, SoundBarMinSmoothing, SoundBarMaxSmoothing);
            job.SoundBarBarCount = (int)Math.Round(Math.Clamp(sldSoundBarBarCount?.Value ?? 64.0, SoundBarMinBarCount, SoundBarMaxBarCount));
            job.SoundBarRoundness = 1.0;
            job.SoundBarGlowIntensity = 1.0;
            job.SoundBarRingCount = 2;
            job.SoundBarLoopSeconds = 5.0;
        }

        /// <summary>
        /// Remembers the output canvas aspect (width / height) so the position presets can compensate
        /// for HALF the plot width - a quantity that is only expressible as a fraction of the frame
        /// width once the aspect is known.
        /// <para>
        /// Now only the LAST-WORD source, not the only one. Queue time is authoritative because it is
        /// the last point where the final TargetWidth / TargetHeight are both in hand (both job
        /// builders fill the object initializer before calling it). But capturing ONLY here was the
        /// defect: presets are picked in the UI long before anything is queued, so on the default
        /// portrait job every preset the user chose had been computed against 16:9.
        /// Measured, square styles, default 62 / 26, real canvas 1080x1920: <c>plotWidth</c> was
        /// computed as 0.1463 when the truth is 0.4622, giving a 0.00 % inset on BOTH sides - the
        /// figure slammed flush against both frame edges - on the orientation this product is mostly
        /// used in, and on the default settings. <see cref="RefreshSoundBarCanvasAspect"/> closes
        /// that by recomputing from the resolution controls themselves.
        /// </para>
        /// <para>
        /// The localhost-API job builder deliberately does NOT call this: its anchors come from the
        /// request, not from these presets, so letting it overwrite the aspect would move every
        /// preset the user picks next.
        /// </para>
        /// </summary>
        private void CaptureSoundBarCanvasAspect(RenderJob job)
        {
            if (job == null || job.TargetWidth <= 0 || job.TargetHeight <= 0)
                return;

            double previous = _soundBarCanvasAspect;
            bool wasKnown = _soundBarCanvasAspectIsKnown;

            // Stores the field. Queue time is the last point where the final TargetWidth /
            // TargetHeight are both in hand, so if this disagrees with what the UI believed, the
            // anchors have to be re-derived BEFORE ApplySoundBarSettingsToJob reads X / Y back off
            // the boxes - otherwise the job renders the stale pair and only the screen recovers.
            if (!TrySetSoundBarCanvasAspect(job.TargetWidth, job.TargetHeight, out _, out _))
                return;

            _soundBarCanvasAspectIsKnown = true;

            if (Math.Abs(_soundBarCanvasAspect - previous) < 0.0005 && wasKnown)
                return;

            if (IsLoaded)
                ApplySoundBarPositionPreset();
        }

        /// <summary>
        /// Recomputes <see cref="_soundBarCanvasAspect"/> from the RESOLUTION controls, and re-applies
        /// the selected position preset if the answer moved. Called whenever cmbRes, txtResW or
        /// txtResH changes, and once when media is selected so "Original" has something to resolve to.
        /// <para>
        /// Re-applying the preset is not optional: the anchors are a function of the aspect for the
        /// six square styles, so a portrait/landscape switch silently invalidates X/Y that were
        /// correct a moment ago.
        /// </para>
        /// </summary>
        private void RefreshSoundBarCanvasAspect()
        {
            if (cmbRes == null)
                return;

            double previous = _soundBarCanvasAspect;
            bool wasKnown = _soundBarCanvasAspectIsKnown;

            if (!TryGetSoundBarOutputSize(out int outW, out int outH, out string origin))
            {
                // Nothing resolved yet (e.g. "Original" with no media loaded and no probe). Fall back
                // to the documented 16:9 stand-in - and SAY SO rather than presenting it as a
                // measured answer. The field is RESET, not retained: the tooltip below and the log
                // line both promise 16:9, so leaving the last measured aspect in place would make
                // both of them assert a number the presets are not actually using.
                _soundBarCanvasAspectIsKnown = false;

                bool moved = Math.Abs(SoundBarCanvasAspectFallback - previous) >= 0.0005;
                if (moved)
                {
                    _soundBarCanvasAspect = SoundBarCanvasAspectFallback;
                    LogSystem($"[SOUNDBAR] Output resolution no longer resolvable; position presets are back on the 16:9 default.");
                }
                else if (wasKnown)
                {
                    LogSystem($"[SOUNDBAR] Output resolution no longer resolvable; position presets already used the 16:9 default.");
                }

                UpdateSoundBarCanvasTooltip();

                // The anchors were derived from the old aspect, so they are now stale - re-apply.
                if (moved && IsLoaded)
                    ApplySoundBarPositionPreset();
                return;
            }

            if (!TrySetSoundBarCanvasAspect(outW, outH, out double aspect, out string description))
                return;

            _soundBarCanvasAspectIsKnown = true;
            UpdateSoundBarCanvasTooltip();

            if (Math.Abs(aspect - previous) < 0.0005 && wasKnown)
            {
                // Same answer - do not rewrite X/Y underneath a user who may be mid-edit on them.
                return;
            }

            LogSystem($"[SOUNDBAR] Output canvas {description} ({aspect:0.####}) from {origin}; position presets re-applied.");

            if (IsLoaded)
                ApplySoundBarPositionPreset();
        }

        /// <summary>
        /// The output size the UI is currently configured to render at, resolved the same way the
        /// queue-time resolver does. "Original" reads the ALREADY-PROBED source dimensions
        /// (<see cref="_soundBarSourceWidth"/>/<see cref="_soundBarSourceHeight"/>) instead of
        /// launching another ffprobe on every resolution change.
        /// </summary>
        private bool TryGetSoundBarOutputSize(out int width, out int height, out string origin)
        {
            width = 0;
            height = 0;
            origin = "the Resolution box";

            string? content = (cmbRes?.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (string.IsNullOrWhiteSpace(content))
                return false;

            if (content.Contains("Original", StringComparison.OrdinalIgnoreCase))
            {
                origin = "'Original' (probed source)";
                if (_soundBarSourceWidth <= 0 || _soundBarSourceHeight <= 0)
                {
                    origin = "'Original' (source not probed yet)";
                    return false;
                }

                width = _soundBarSourceWidth;
                height = _soundBarSourceHeight;
                return true;
            }

            if (content.Contains("Custom", StringComparison.OrdinalIgnoreCase))
            {
                origin = "'Custom' (width x height boxes)";
                width = (int)ParsePercent(txtResW?.Text, 0);
                height = (int)ParsePercent(txtResH?.Text, 0);
                return width > 0 && height > 0;
            }

            Match match = Regex.Match(content, @"(\d+)x(\d+)");
            if (!match.Success ||
                !int.TryParse(match.Groups[1].Value, out width) ||
                !int.TryParse(match.Groups[2].Value, out height))
            {
                width = 0;
                height = 0;
                return false;
            }

            return width > 0 && height > 0;
        }

        /// <summary>
        /// Applies a candidate canvas to <see cref="_soundBarCanvasAspect"/>.
        /// <para>
        /// It STORES the field, and that is why it is an instance method rather than a static one
        /// with an <c>out</c> parameter. It used to be exactly that: <c>STFIELD
        /// _soundBarCanvasAspect</c> appeared precisely once in the shipped IL, in the constructor
        /// initialiser, so every caller read the 16:9 the field was born with and the whole
        /// "refresh the stale canvas aspect" fix was inert. Measured live, the field stayed 1.7778
        /// while the output was 1080x1920 (0.5625), and the six squared styles put a left or right
        /// preset 0.00 % from the frame edge instead of 4.50 %.
        /// </para>
        /// </summary>
        private bool TrySetSoundBarCanvasAspect(int width, int height, out double aspect, out string description)
        {
            aspect = 0;
            description = string.Empty;

            if (width <= 0 || height <= 0)
                return false;

            double candidate = width / (double)height;
            if (double.IsNaN(candidate) || double.IsInfinity(candidate))
                return false;

            double clamped = Math.Clamp(candidate, SoundBarCanvasAspectMin, SoundBarCanvasAspectMax);
            _soundBarCanvasAspect = clamped;
            aspect = clamped;
            description = $"{width}x{height}";
            return true;
        }

        private void UpdateSoundBarCanvasTooltip()
        {
            if (cmbSoundBarPosition == null)
                return;

            if (_soundBarCanvasAspectIsKnown)
            {
                cmbSoundBarPosition.ToolTip =
                    "Chọn vị trí dựng sẵn. Vị trí được ghi vào X/Y (%) bên dưới - sửa tay ô X/Y sẽ tự chuyển sang 'Tuỳ chỉnh'. "
                    + $"Các vị trí dựng sẵn đang tính cho khung xuất tỉ lệ {_soundBarCanvasAspect:0.###} (ngang/dọc).";
                return;
            }

            cmbSoundBarPosition.ToolTip =
                "Chọn vị trí dựng sẵn. Vị trí được ghi vào X/Y (%) bên dưới - sửa tay ô X/Y sẽ tự chuyển sang 'Tuỳ chỉnh'. "
                + "CHƯA xác định được khung xuất (Original mà chưa có file để đo), nên đang tạm dùng 16:9 - "
                + "các vị trí dựng sẵn có thể lệch, và sẽ tự tính lại khi bạn chọn file hoặc đổi độ phân giải.";
        }

        /// <summary>
        /// Probes the first selected media's dimensions ONCE and caches them, so "Original" resolves in
        /// the UI without an ffprobe launch on every resolution change.
        /// <para>
        /// Failure is made observable on purpose. <see cref="EngineCore.GetVideoResolutionAsync"/>
        /// returns the literal <c>(1920, 1080)</c> on a missing ffprobe, a missing file or a probe
        /// failure, which passes the 0.2 - 5.0 clamp and is indistinguishable from a real answer -
        /// so it cannot be used to decide "is the source known?". This checks the two things it can
        /// actually observe (the file exists, ffprobe exists) instead, and only then trusts the probe.
        /// A genuine 1920x1080 source is still indistinguishable from the sentinel - that limitation
        /// is real and is inherited from the shared helper, which has eight other callers whose
        /// behaviour must not change - but the case the guard is here for, "we never probed anything
        /// at all", is now caught and surfaced instead of silently reading as 16:9.
        /// </para>
        /// </summary>
        /// <summary>
        /// Entry point for the probe. Owns the latch, the generation counter and the refresh.
        /// <para>
        /// The generation counter exists because the latch used to be a latch and nothing else. A
        /// second media selection arriving while a probe was in flight was discarded outright, and
        /// the in-flight probe then wrote the PREVIOUS file's dimensions - which nothing ever
        /// re-probed, so "Original" stayed wrong for the rest of the session. Now every entry bumps
        /// <see cref="_soundBarSourceProbeGeneration"/> and asks for a re-run instead of dropping
        /// the request, and a probe only applies its answer if it is still the newest one. That
        /// invariant is what makes the discard safe: generation can only change via another entry,
        /// which always sets the re-run flag, so "my generation is stale" and "a re-run is
        /// queued" are the same condition.
        /// </para>
        /// <para>
        /// The refresh goes through <see cref="RefreshSoundBarCanvasAspectSafely"/> because this is
        /// an <c>async void</c>: an exception thrown by the tail call lands on the dispatcher, where
        /// nothing catches it, and takes the process down with it.
        /// </para>
        /// </summary>
        private async void ProbeSoundBarSourceResolutionAsync()
        {
            // Bumped before the in-flight check, so a request that is about to be deferred still
            // invalidates the probe it is superseding.
            int generation = ++_soundBarSourceProbeGeneration;

            if (_soundBarSourceProbeInFlight)
            {
                _soundBarSourceProbeRerunRequested = true;
                return;
            }

            _soundBarSourceProbeInFlight = true;
            try
            {
                await ProbeSoundBarSourceResolutionCoreAsync(generation);
            }
            catch (Exception ex)
            {
                // Unreachable from inside the Core (it has no throw of its own), but GetVideoResolutionAsync
                // is a shared helper whose contract is not ours to widen, and this is an async void:
                // anything that escapes lands on the dispatcher and ends the process.
                ClearSoundBarSourceSize(generation);
                LogSystem($"[SOUNDBAR-WARN] Source probe failed ({ex.Message}); 'Original' resolution unresolved.");
            }
            finally
            {
                _soundBarSourceProbeInFlight = false;
            }

            if (_soundBarSourceProbeRerunRequested)
            {
                _soundBarSourceProbeRerunRequested = false;
                // Re-enter rather than recurse: the stack here is one frame deep and bounded, but
                // this keeps the probe work off whatever continuation ran the previous one.
                ProbeSoundBarSourceResolutionAsync();
                return;
            }

            RefreshSoundBarCanvasAspectSafely();
        }

        /// <summary>
        /// The probe itself. Every write to <see cref="_soundBarSourceWidth"/> is gated on
        /// <paramref name="generation"/> still being current, so a superseded probe contributes
        /// nothing rather than leaving a stale pair for the re-run to overwrite.
        /// </summary>
        private async Task ProbeSoundBarSourceResolutionCoreAsync(int generation)
        {
            string? source = _listVideoPaths?.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
            if (source == null)
            {
                ClearSoundBarSourceSize(generation);
                return;
            }

            if (!File.Exists(source))
            {
                // Clear rather than keep whatever the previous media probed to: a stale pair would be
                // presented as a live answer for a file that is no longer there.
                ClearSoundBarSourceSize(generation);
                if (generation == _soundBarSourceProbeGeneration)
                    LogSystem($"[SOUNDBAR-WARN] Cannot probe '{Path.GetFileName(source)}' - file not found; 'Original' resolution stays unresolved.");
                return;
            }

            (int Width, int Height) size = await EngineCore.GetVideoResolutionAsync(source);
            if (generation != _soundBarSourceProbeGeneration)
                return;

            // GetVideoResolutionAsync reports (1920, 1080) both for a real 16:9 file and for every
            // failure it hides. ffprobe availability is the one discriminator available here.
            bool ffprobeAvailable = EngineCore.IsFfprobeAvailable;
            if (!ffprobeAvailable && !IsImageSourcePath(source))
            {
                ClearSoundBarSourceSize(generation);
                LogSystem("[SOUNDBAR-WARN] ffprobe not found; cannot resolve 'Original' output resolution. "
                          + "Soundbar position presets are using the 16:9 default until a resolution is chosen explicitly.");
                return;
            }

            if (size.Width <= 0 || size.Height <= 0)
            {
                ClearSoundBarSourceSize(generation);
                return;
            }

            // The failure sentinel is indistinguishable from a genuine 1920x1080 source, so the
            // honest answer is "unknown" rather than whichever of the two it happened to be. This
            // costs nothing geometrically: the stand-in the unknown path falls back to is itself
            // 16:9, so every anchor resolves exactly as it would have for a real 1920x1080 file.
            // The previous code claimed the measurement regardless, so a probe that failed read as
            // a confirmed 1.778 in the tooltip.
            if (!IsImageSourcePath(source) && size.Width == 1920 && size.Height == 1080)
            {
                ClearSoundBarSourceSize(generation);
                LogSystem($"[SOUNDBAR] Probe of '{Path.GetFileName(source)}' returned 1920x1080, which is also what a "
                          + "failed probe returns, so it cannot be told apart from a real 16:9 source. Treating 'Original' "
                          + "as unresolved; the 16:9 stand-in gives identical position presets either way.");
                return;
            }

            _soundBarSourceWidth = size.Width;
            _soundBarSourceHeight = size.Height;
            LogSystem($"[SOUNDBAR] Source probed at {size.Width}x{size.Height} for 'Original' resolution.");
        }

        private void ClearSoundBarSourceSize(int generation)
        {
            if (generation != _soundBarSourceProbeGeneration)
                return;

            _soundBarSourceWidth = 0;
            _soundBarSourceHeight = 0;
        }

        /// <summary>
        /// <see cref="RefreshSoundBarCanvasAspect"/> reached from an <c>async void</c> continuation.
        /// It reads and writes live controls and appends to the log, and an exception escaping one
        /// of those onto the dispatcher is unrecoverable, so every tail call goes through here.
        /// </summary>
        private void RefreshSoundBarCanvasAspectSafely()
        {
            try
            {
                RefreshSoundBarCanvasAspect();
            }
            catch (Exception ex)
            {
                LogSystem($"[SOUNDBAR-WARN] Canvas aspect refresh failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Parses a UI percentage field. Accepts both '.' and ',' as the decimal separator
        /// so the box works under Vietnamese and invariant locales.
        /// </summary>
        private static double ParsePercent(string? text, double fallback)
        {
            if (string.IsNullOrWhiteSpace(text))
                return fallback;

            string normalized = text.Trim().Replace(',', '.');
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v
                : fallback;
        }

        private async void BtnAddJob_Click(object sender, RoutedEventArgs e)
        {
            if (_listVideoPaths == null || _listVideoPaths.Count == 0)
            {
                MessageBox.Show("Select source media!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string templateName = LocalhostRequestMapper.NormalizeTemplateName((cmbTemplateStyle?.SelectedItem as ComboBoxItem)?.Content?.ToString());
            string selectedBorderEffect = LocalhostRequestMapper.ResolveOverlayEffectName(GetSelectedComboText(cmbBorderType));
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
            string selectedFxEffect = LocalhostRequestMapper.ResolveFxEffectName(rawFxSelection);
            string snowfallPreset = LocalhostRequestMapper.ResolveSnowfallPresetName(rawFxSelection);
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
            bool allImageSources = LocalhostRequestMapper.ShouldUsePortraitHdDefaultForImages(_listVideoPaths);

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
                    LocalhostRequestMapper.TryParseFlexibleDouble(txtImageTimelineDuration.Text, out double parsedImageTimelineDuration))
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
                    StatusColor = "Cyan",
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

                ApplySoundBarSettingsToJob(job);

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
            bool allImageSources = LocalhostRequestMapper.ShouldUsePortraitHdDefaultForImages(videos);
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
                string templateName = LocalhostRequestMapper.NormalizeTemplateName((cmbTemplateStyle?.SelectedItem as ComboBoxItem)?.Content?.ToString());
                string selectedBorderEffect = LocalhostRequestMapper.ResolveOverlayEffectName(GetSelectedComboText(cmbBorderType));
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
                string selectedFxEffect = LocalhostRequestMapper.ResolveFxEffectName(rawFxSelection);
                string snowfallPreset = LocalhostRequestMapper.ResolveSnowfallPresetName(rawFxSelection);
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
                    LocalhostRequestMapper.TryParseFlexibleDouble(txtImageTimelineDuration.Text, out double parsedImageTimelineDuration))
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
                    StatusColor = "Cyan",
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

                ApplySoundBarSettingsToJob(job);

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

// This used to PEEK at the render lock and then carry on holding nothing, so a UI batch
            // and an API job could render at the same time - two ffmpeg processes at default
            // thread counts on six cores, which is the 97-100 % CPU. ExecuteRenderAsync has no
            // lock of its own, so this check was the only thing standing between them. Take
            // the lock for the whole batch instead of asking whether it happens to be free.
            if (!await _localApiRenderLock.WaitAsync(0))
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
                    job.StatusColor = "Yellow";
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
                    job.StatusColor = "LimeGreen";
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
                // The batch held the shared render lock for its whole run; give it back here so an
                // API request that arrived while it was working is not rejected forever.
                if (_localApiRenderLock.CurrentCount == 0)
                    _localApiRenderLock.Release();

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
            // The soundbar picker render runs on a background STA thread reading a PNG from the
            // temp tree; leaving its CTS undrained means it can still be mid-write as the process
            // tears down. Cancel + dispose it exactly like the two frame loaders above.
            CancelSoundBarPreview();
            try { _soundBarSizeEditCommitTimer?.Stop(); } catch { }
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

                if (path.Equals(LocalApiSoundBarPresetsPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (!context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteApiResponseAsync(context.Response, 405, new LocalhostRenderResponse
                        {
                            Success = false,
                            Message = "Method not allowed. Use GET /api/soundbar/presets"
                        });
                        return;
                    }

                    await WriteApiResponseAsync(context.Response, 200, BuildSoundBarPresetsResponse());
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
                    LocalhostRequestMapper.GetFeatureBool(request, "waitForCompletion", "wait", "sync", "blocking");

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

                LocalhostRequestMapping mapped;
                try
                {
                    mapped = await LocalhostRequestMapper.MapAsync(request, LogSystem);
                }
                catch (LocalhostRequestException ex)
                {
                    await WriteApiResponseAsync(context.Response, ex.StatusCode, new LocalhostRenderResponse
                    {
                        Success = false,
                        Message = ex.Message
                    });
                    return;
                }

                job = mapped.Job;
                string inputPath = mapped.InputPath;
                string outputPath = mapped.OutputPath;
                string outputDir = mapped.OutputDir;
                string colorFilter = mapped.ColorFilter;
                string fxEffect = mapped.FxEffect;
                string overlayEffect = mapped.OverlayEffect;
                string templateName = mapped.TemplateName;
                bool cropEnabled = mapped.CropEnabled;
                string upscaleMode = mapped.UpscaleMode;

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
                        job.StatusColor = "Red";
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
                        job.StatusColor = "Red";
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
                        queueJob.StatusColor = "Red";
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
                        queueJob.StatusColor = "Red";
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
                job.StatusColor = "Yellow";
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
                        job.StatusColor = "Yellow";
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
                        // The percentage was already in `Progress` and updating 139 times over a
                        // job, but `message` never carried it - and the API guide documents
                        // `message`, so a client rendering it watched "Processing" forever.
                        Message = $"Processing {p:F1}%",
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
                job.StatusColor = "LimeGreen";
            });

            LogSystem($"[API] COMPLETED: {Path.GetFileName(finalOutput)} (took {encodingTime:F2}s)");
            try { SystemSounds.Asterisk.Play(); } catch { }

            return (engineOutput, finalOutput);
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
















        /// <summary>
        /// Maps an incoming audio-source string onto "auto" / "external" / "video".
        /// </summary>

        /// <summary>
        /// Normalizes a soundbar placement fraction. Callers may send either 0.0-1.0 or a
        /// 0-100 percentage; anything above 1.0 is read as a percentage so BAS scripts do
        /// not silently collapse the layer into the corner.
        /// </summary>

        /// <summary>
        /// Builds the GET /api/soundbar/presets payload: two SEPARATE arrays, <c>soundBarStyles</c>
        /// (one entry per visualizer style, in menu order) and <c>soundBarPalettes</c> (one entry
        /// per colour palette). They are deliberately not concatenated into one flat array - the two
        /// shapes have different member names ("styleId" vs "id") and no boundary marker was ever
        /// emitted, so a flat client had to probe every element just to split them.
        /// </summary>
        private LocalhostRenderResponse BuildSoundBarPresetsResponse()
        {
            var styles = new List<object>();

            foreach (SoundBarStyleInfo style in SoundBarVisualizerPreset.AllStyles)
            {
                styles.Add(new
                {
                    styleId = style.StyleId,
                    name = style.Name,
                    description = style.Description,
                    usesFft = style.UsesFft,
                    usesWaveform = style.UsesWaveform,
                    usesOnset = style.UsesOnset,
                    usesRms = style.UsesRms,
                    defaultBarCount = style.DefaultBarCount
                });
            }

            var palettes = new List<object>();

            foreach (ColorPreset palette in ColorPresets.All)
            {
                IReadOnlyList<GradientStopSpec> paletteStops = palette.Stops ?? Array.Empty<GradientStopSpec>();
                var stops = new List<object>(paletteStops.Count);
                foreach (GradientStopSpec stop in paletteStops)
                {
                    stops.Add(new
                    {
                        offset = stop.Offset,
                        hex = stop.Hex
                    });
                }

                palettes.Add(new
                {
                    id = palette.Id,
                    name = palette.Name,
                    vibe = palette.Vibe,
                    genres = palette.Genres,
                    stops,
                    core = palette.Core,
                    glow = palette.Glow,
                    bgTint = palette.BgTint,
                    accent = palette.Accent,
                    glowStrength = palette.GlowStrength
                });
            }

            LogSystem($"[SOUNDBAR-PRESETS] Serving {styles.Count} style(s) and {palettes.Count} palette(s)");

            return new LocalhostRenderResponse
            {
                Success = true,
                Message = "Soundbar visualizer styles and palettes",
                SoundBarStyles = styles,
                SoundBarPalettes = palettes
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
















